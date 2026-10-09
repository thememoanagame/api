using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

var baseUrl = GetBaseUrl(args);
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() }
};

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
var createResponse = await http.PostAsJsonAsync("api/game/rooms", new CreateRoomRequest("PVP", "Easy"), jsonOptions);
createResponse.EnsureSuccessStatusCode();
var room = (await createResponse.Content.ReadFromJsonAsync<CreateRoomResponse>(jsonOptions))
    ?? throw new InvalidOperationException("The API returned an empty room response.");

Console.WriteLine($"Room created: {room.RoomId} ({room.Mode}/{room.Difficulty})");

await using var playerOne = new GameClient($"{baseUrl}/gameHub", "Player 1");
await using var playerTwo = new GameClient($"{baseUrl}/gameHub", "Player 2");
playerOne.RegisterEvents();
playerTwo.RegisterEvents();
await playerOne.Connection.StartAsync();
await playerTwo.Connection.StartAsync();

var firstJoin = await playerOne.JoinAsync(room.RoomId);
var secondJoin = await playerTwo.JoinAsync(room.RoomId);
Console.WriteLine($"Joined with connections {playerOne.Connection.ConnectionId} and {playerTwo.Connection.ConnectionId}");

playerOne.PlayerId = firstJoin.PlayerId;
playerTwo.PlayerId = secondJoin.PlayerId;
playerOne.CurrentPlayer = secondJoin.CurrentTurn;
playerTwo.CurrentPlayer = secondJoin.CurrentTurn;

var turns = 0;
while (!playerOne.Finished && !playerTwo.Finished && turns++ < 100)
{
    var currentPlayer = playerOne.CurrentPlayer ?? playerTwo.CurrentPlayer;
    var client = currentPlayer == playerTwo.PlayerId ? playerTwo : playerOne;
    if (string.IsNullOrWhiteSpace(currentPlayer))
    {
        await Task.Delay(25);
        continue;
    }

    var pair = FindKnownPair(playerOne.MatchedPositions, playerOne, playerTwo);
    var firstPosition = pair?.First ?? FindUnmatchedPosition(playerOne.MatchedPositions, playerOne, playerTwo);
    var secondPosition = pair?.Second ?? FindUnmatchedPosition(playerOne.MatchedPositions, playerOne, playerTwo, firstPosition);

    ApplyState(await client.FlipAsync(room.RoomId, firstPosition), playerOne, playerTwo);
    ApplyState(await client.FlipAsync(room.RoomId, secondPosition), playerOne, playerTwo);
    await Task.Delay(25);
}

if (!playerOne.Finished && !playerTwo.Finished)
    throw new InvalidOperationException("The validation exceeded 100 turns without GameFinished.");

Console.WriteLine($"Validation finished after {turns - 1} turns.");
Console.WriteLine($"Player 1 score: {playerOne.LastScores.GetValueOrDefault(playerOne.PlayerId ?? string.Empty)}");
Console.WriteLine($"Player 2 score: {playerTwo.LastScores.GetValueOrDefault(playerTwo.PlayerId ?? string.Empty)}");

static string GetBaseUrl(string[] args)
{
    var option = args.FirstOrDefault(x => x.StartsWith("--url=", StringComparison.OrdinalIgnoreCase));
    return (option is null ? "http://127.0.0.1:5090" : option[6..]).TrimEnd('/');
}

static (int First, int Second)? FindKnownPair(HashSet<int> matched, params GameClient[] clients)
{
    var known = clients.SelectMany(x => x.KnownAssets)
        .GroupBy(x => x.Key)
        .Select(x => x.First())
        .Where(x => !matched.Contains(x.Key))
        .GroupBy(x => x.Value, StringComparer.Ordinal)
        .FirstOrDefault(x => x.Count() >= 2);
    if (known is null) return null;
    var positions = known.Take(2).Select(x => x.Key).ToArray();
    return (positions[0], positions[1]);
}

static int FindUnmatchedPosition(HashSet<int> matched, GameClient first, GameClient second, int excluded = -1)
{
    var knownPositions = first.KnownAssets.Keys.Concat(second.KnownAssets.Keys).ToHashSet();
    return Enumerable.Range(0, 8).FirstOrDefault(x => x != excluded && !matched.Contains(x) && !knownPositions.Contains(x));
}

static void ApplyState(GameState state, GameClient first, GameClient second)
{
    if (state.Board is null)
        throw new InvalidOperationException("The Hub returned a game state without a board.");
    first.CurrentPlayer = second.CurrentPlayer = state.CurrentTurn;
    first.MatchedPositions.Clear();
    foreach (var card in state.Board.Where(x => x.IsMatched))
    {
        first.MatchedPositions.Add(card.Position);
        second.MatchedPositions.Add(card.Position);
    }
}

sealed class GameClient(string hubUrl, string name) : IAsyncDisposable
{
    public HubConnection Connection { get; } = new HubConnectionBuilder()
        .WithUrl(hubUrl)
        .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true)
        .Build();
    public string Name { get; } = name;
    public string? PlayerId { get; set; }
    public string? CurrentPlayer { get; set; }
    public bool Finished { get; private set; }
    public HashSet<int> MatchedPositions { get; } = [];
    public Dictionary<int, string> KnownAssets { get; } = [];
    public Dictionary<string, int> LastScores { get; } = [];

    public void RegisterEvents()
    {
        Connection.On<CardRevealed>("CardRevealed", message =>
        {
            KnownAssets[message.Position] = message.AssetReference;
            Console.WriteLine($"{Name}: CardRevealed position={message.Position}");
        });
        Connection.On<PairMatched>("PairMatched", message =>
        {
            MatchedPositions.Add(message.FirstPosition);
            MatchedPositions.Add(message.SecondPosition);
            Console.WriteLine($"PairMatched by {message.PlayerId}: +{message.EarnedScore}, total={message.TotalScore}");
        });
        Connection.On<PairMissed>("PairMissed", message =>
            Console.WriteLine($"PairMissed positions={message.FirstPosition},{message.SecondPosition}"));
        Connection.On<TurnChanged>("TurnChanged", message =>
        {
            CurrentPlayer = message.PlayerId;
            Console.WriteLine($"TurnChanged: {message.PlayerId}");
        });
        Connection.On<ScoreUpdated>("ScoreUpdated", message => LastScores[message.PlayerId] = message.TotalScore);
        Connection.On<GameFinished>("GameFinished", message =>
        {
            Finished = true;
            foreach (var score in message.Scores) LastScores[score.Key] = score.Value;
            Console.WriteLine($"GameFinished: {message.Reason}");
        });
        Connection.On<GameError>("Error", message => Console.WriteLine($"Hub Error: {message.Code} - {message.Message}"));
    }

    public async Task<JoinRoomResponse> JoinAsync(string roomId)
    {
        var result = await Connection.InvokeAsync<GameOperationResult>("JoinRoom", roomId);
        EnsureSuccess(result);
        return result.Value.Deserialize<JoinRoomResponse>(CliJson.Options)
            ?? throw new InvalidOperationException("The Hub returned no join response.");
    }

    public async Task<GameState> FlipAsync(string roomId, int position)
    {
        var result = await Connection.InvokeAsync<GameOperationResult>("FlipCard", roomId, position);
        EnsureSuccess(result);
        return result.Value.Deserialize<GameState>(CliJson.Options)
            ?? throw new InvalidOperationException("The Hub returned no game state after FlipCard.");
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private static void EnsureSuccess(GameOperationResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Hub operation failed: {result.ErrorCode} - {result.ErrorMessage}");
    }
}

record CreateRoomRequest(string Mode, string Difficulty);
record CreateRoomResponse(string RoomId, string Mode, string Difficulty, string Status);
record JoinRoomResponse(string RoomId, string PlayerId, int Mode, int Difficulty, int Status, CardView[] Board, string? CurrentTurn);
record CardView(int Position, bool IsRevealed, bool IsMatched, string? AssetReference);
record GameState(string RoomId, int Mode, int Difficulty, int Status, string[] Players, CardView[]? Board, string? CurrentTurn, Dictionary<string, int> Scores, Dictionary<string, int> ConsecutiveHits, JsonElement StartedAt, JsonElement Duration);
record GameOperationResult(bool Succeeded, string? ErrorCode, string? ErrorMessage, JsonElement Value);
record CardRevealed(string RoomId, int Position, string AssetReference);
record PairMatched(string RoomId, int FirstPosition, int SecondPosition, string PlayerId, int EarnedScore, int TotalScore, int Streak);
record PairMissed(string RoomId, int FirstPosition, int SecondPosition);
record TurnChanged(string RoomId, string? PlayerId);
record ScoreUpdated(string RoomId, string PlayerId, int EarnedScore, int TotalScore, int Streak);
record GameFinished(string RoomId, Dictionary<string, int> Scores, string Reason);
record GameError(string Code, string Message);

static class CliJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true };
}
