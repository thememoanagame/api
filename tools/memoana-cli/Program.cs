using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;

var options = CliOptions.Parse(args);
using var http = new HttpClient { BaseAddress = new Uri(options.Url) };
var runner = new ValidationRunner(http, options);
await runner.RunAsync();

sealed class ValidationRunner(HttpClient http, CliOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, PropertyNameCaseInsensitive = true };
    public async Task RunAsync()
    {
        var modes = options.Scenario.Equals("all", StringComparison.OrdinalIgnoreCase) ? new[] { "PVP", "Time", "AI" } : [options.Mode];
        var difficulties = options.Scenario.Equals("all", StringComparison.OrdinalIgnoreCase) ? Enum.GetNames<GameDifficulty>() : [options.Difficulty];
        if (options.Scenario is "smoke" or "pvp" or "all") foreach (var difficulty in difficulties) await RunGameAsync("PVP", difficulty, true);
        if (options.Scenario is "time" or "all") foreach (var difficulty in difficulties) await RunTimeAsync(difficulty);
        if (options.Scenario is "ai" or "all") foreach (var difficulty in difficulties) await RunGameAsync("AI", difficulty, false);
        if (options.Scenario is "invalid" or "all") await RunInvalidAsync();
        if (options.Scenario is "disconnect" or "all") await RunDisconnectAsync();
    }

    private async Task RunGameAsync(string mode, string difficulty, bool twoPlayers)
    {
        var room = await CreateRoomAsync(mode, difficulty);
        await using var first = new GameClient(new Uri(options.Url + "/gameHub"), "P1");
        await using var second = twoPlayers ? new GameClient(new Uri(options.Url + "/gameHub"), "P2") : null;
        var ledger = new EventLedger();
        first.Register(ledger); second?.Register(ledger);
        await first.StartAsync(); if (second is not null) await second.StartAsync();
        var joined = await first.JoinAsync(room.RoomId); var other = second is null ? null : await second.JoinAsync(room.RoomId);
        first.PlayerId = joined.PlayerId; if (second is not null) second.PlayerId = other!.PlayerId;
        var state = await GetStateAsync(room.RoomId); ValidateBoard(state, difficulty);
        Console.WriteLine($"{mode} {difficulty}: board={state.Board.Length}, players={state.Players.Length}");
        var scores = state.Scores.ToDictionary(x => x.Key, x => x.Value); var streaks = state.ConsecutiveHits.ToDictionary(x => x.Key, x => x.Value); var matched = new HashSet<int>(); ledger.CurrentTurn = state.CurrentTurn;
        while (state.Status != GameStatus.Finished)
        {
            if (state.CurrentTurn is null) throw new InvalidOperationException("Playing room has no current turn.");
            var client = state.CurrentTurn == first.PlayerId ? first : second;
            if (client is null) throw new InvalidOperationException("The server selected an unknown player turn.");
            var known = ledger.KnownAssets.Where(x => !matched.Contains(x.Key)).GroupBy(x => x.Value).FirstOrDefault(x => x.Count() >= 2);
            var firstPosition = known?.First().Key ?? Enumerable.Range(0, state.Board.Length).First(x => !matched.Contains(x) && !ledger.KnownAssets.ContainsKey(x));
            var secondPosition = known?.Skip(1).First().Key ?? Enumerable.Range(0, state.Board.Length).First(x => x != firstPosition && !matched.Contains(x) && !ledger.KnownAssets.ContainsKey(x));
            state = await client.FlipAndValidateAsync(room.RoomId, firstPosition, state, ledger, scores, streaks, matched);
            if (state.Status == GameStatus.Finished) break;
            state = await client.FlipAndValidateAsync(room.RoomId, secondPosition, state, ledger, scores, streaks, matched);
        }
        if (!ledger.Finished || ledger.FinishReason is not "all_pairs_matched") throw new InvalidOperationException($"{mode}/{difficulty} did not finish by matching all pairs.");
        Console.WriteLine($"{mode} {difficulty}: PASS, scores={string.Join(",", scores.Select(x => $"{x.Key}={x.Value}"))}");
    }

    private async Task RunTimeAsync(string difficulty)
    {
        var room = await CreateRoomAsync("Time", difficulty);
        await using var client = new GameClient(new Uri(options.Url + "/gameHub"), "Time"); var ledger = new EventLedger(); client.Register(ledger); await client.StartAsync();
        await client.JoinAsync(room.RoomId); var state = await GetStateAsync(room.RoomId); ValidateBoard(state, difficulty);
        if (state.StartedAt is null || state.Duration is null) throw new InvalidOperationException("Time game did not expose timing metadata.");
        Console.WriteLine($"Time {difficulty}: board={state.Board.Length}, duration={state.Duration}");
        await ledger.FinishedTask.Task.WaitAsync(state.Duration.Value + TimeSpan.FromSeconds(10));
        if (ledger.FinishReason != "time_expired") throw new InvalidOperationException("Time game finished for an unexpected reason.");
        Console.WriteLine($"Time {difficulty}: PASS");
    }

    private async Task RunInvalidAsync()
    {
        var room = await CreateRoomAsync("PVP", "Easy");
        await using var one = new GameClient(new Uri(options.Url + "/gameHub"), "invalid-1"); await using var two = new GameClient(new Uri(options.Url + "/gameHub"), "invalid-2"); await using var three = new GameClient(new Uri(options.Url + "/gameHub"), "invalid-3");
        one.Register(new EventLedger()); two.Register(new EventLedger()); three.Register(new EventLedger()); await one.StartAsync(); await two.StartAsync(); await three.StartAsync();
        ExpectFailure(await one.TryJoinAsync("missing"), "missing room"); var first = await one.JoinAsync(room.RoomId); ExpectFailure(await one.TryFlipAsync(room.RoomId, -1), "flip while waiting");
        await two.JoinAsync(room.RoomId); ExpectFailure(await two.TryFlipAsync(room.RoomId, 0), "wrong turn"); ExpectFailure(await one.TryFlipAsync(room.RoomId, 999), "position above board"); ExpectFailure(await three.TryJoinAsync(room.RoomId), "room full");
        await one.FlipAsync(room.RoomId, 0); ExpectFailure(await one.TryFlipAsync(room.RoomId, 0), "already revealed"); Console.WriteLine("Invalid ops: PASS");
    }

    private async Task RunDisconnectAsync()
    {
        var room = await CreateRoomAsync("PVP", "Easy");
        var client = new GameClient(new Uri(options.Url + "/gameHub"), "disconnect");
        client.Register(new EventLedger()); await client.StartAsync(); await client.JoinAsync(room.RoomId); await client.DisposeAsync();
        for (var i = 0; i < 50; i++)
        {
            var state = await GetStateAsync(room.RoomId);
            if (state.Players.Length == 0) { if (state.Status != GameStatus.Finished) throw new InvalidOperationException("An empty disconnected room was not finished."); Console.WriteLine("Disconnect: PASS"); return; }
            await Task.Delay(100);
        }
        throw new TimeoutException("The disconnected player remained associated with the room.");
    }

    private async Task<CreateRoomResponse> CreateRoomAsync(string mode, string difficulty)
    { using var response = await http.PostAsJsonAsync("api/game/rooms", new { Mode = mode, Difficulty = difficulty }, Json); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<CreateRoomResponse>(Json))!; }
    private async Task<GameState> GetStateAsync(string roomId) => (await http.GetFromJsonAsync<GameState>($"api/game/rooms/{roomId}", Json))!;
    private static void ValidateBoard(GameState state, string difficulty) { var expected = difficulty.ToLowerInvariant() switch { "easy" => 8, "medium" => 16, "hard" => 24, _ => throw new ArgumentException(difficulty) }; if (state.Board.Length != expected) throw new InvalidOperationException($"Expected {expected} cards, got {state.Board.Length}."); }
    private static void ExpectFailure(GameOperationResult result, string operation) { if (result.Succeeded || string.IsNullOrWhiteSpace(result.ErrorCode) || string.IsNullOrWhiteSpace(result.ErrorMessage)) throw new InvalidOperationException($"Invalid operation '{operation}' was accepted or lacked an error contract."); }
}

sealed class GameClient(Uri hubUrl, string name) : IAsyncDisposable
{
    public HubConnection Connection { get; } = new HubConnectionBuilder().WithUrl(hubUrl).Build(); public string Name { get; } = name; public string? PlayerId { get; set; }
    public void Register(EventLedger ledger) { Connection.On<CardRevealed>("CardRevealed", ledger.CardRevealed); Connection.On<PairMatched>("PairMatched", ledger.PairMatched); Connection.On<PairMissed>("PairMissed", ledger.PairMissed); Connection.On<ScoreUpdated>("ScoreUpdated", ledger.ScoreUpdated); Connection.On<TurnChanged>("TurnChanged", ledger.TurnChanged); Connection.On<GameFinished>("GameFinished", ledger.GameFinished); }
    public Task StartAsync() => Connection.StartAsync();
    public async Task<JoinRoomResponse> JoinAsync(string roomId) => Ensure(await Connection.InvokeAsync<GameOperationResult>("JoinRoom", roomId)).Value.Deserialize<JoinRoomResponse>(CliJson.Options)!;
    public Task<GameOperationResult> TryJoinAsync(string roomId) => Connection.InvokeAsync<GameOperationResult>("JoinRoom", roomId); public Task<GameOperationResult> TryFlipAsync(string roomId, int position) => Connection.InvokeAsync<GameOperationResult>("FlipCard", roomId, position);
    public async Task FlipAsync(string roomId, int position) => Ensure(await Connection.InvokeAsync<GameOperationResult>("FlipCard", roomId, position));
    public async Task<GameState> FlipAndValidateAsync(string roomId, int position, GameState before, EventLedger ledger, Dictionary<string, int> scores, Dictionary<string, int> streaks, HashSet<int> matched)
    {
        var outcome = ledger.NextOutcome(); var eventStart = ledger.Events.Count; var result = Ensure(await Connection.InvokeAsync<GameOperationResult>("FlipCard", roomId, position)); var state = result.Value.Deserialize<GameState>(CliJson.Options)!; await ledger.WaitForRevealAsync(position);
        if (state.Status == GameStatus.Finished || before.Board.Count(x => x.IsRevealed) == 1) await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var gameEvent in ledger.Events.Skip(eventStart))
        {
            if (gameEvent.Match is { } pair)
            { var expectedStreak = streaks[pair.PlayerId] + 1; var expectedEarned = expectedStreak == 1 ? 100 : (int)Math.Floor(100 * Math.Pow(1.5, expectedStreak - 1)); if (pair.EarnedScore != expectedEarned || pair.TotalScore != scores[pair.PlayerId] + expectedEarned || pair.Streak != expectedStreak) throw new InvalidOperationException($"Score contract is inconsistent for {pair.PlayerId}: earned={pair.EarnedScore}, total={pair.TotalScore}, expected={expectedEarned}/{scores[pair.PlayerId] + expectedEarned}, streak={pair.Streak}, expectedStreak={expectedStreak}."); scores[pair.PlayerId] = pair.TotalScore; streaks[pair.PlayerId] = pair.Streak; matched.Add(pair.FirstPosition); matched.Add(pair.SecondPosition); }
            else if (gameEvent.MissPlayer is not null && streaks.ContainsKey(gameEvent.MissPlayer)) streaks[gameEvent.MissPlayer] = 0;
        }
        ledger.CurrentTurn = state.CurrentTurn;
        return state;
    }
    public async ValueTask DisposeAsync() => await Connection.DisposeAsync(); private static GameOperationResult Ensure(GameOperationResult result) => result.Succeeded ? result : throw new InvalidOperationException($"{result.ErrorCode}: {result.ErrorMessage}");
}

sealed class EventLedger
{
    private readonly object gate = new(); private TaskCompletionSource<bool> outcome = NewSignal(); public Dictionary<int, string> KnownAssets { get; } = []; public List<LedgerEvent> Events { get; } = []; public string? CurrentTurn { get; set; } public bool Finished { get; private set; } public string? FinishReason { get; private set; } public TaskCompletionSource<bool> FinishedTask { get; } = NewSignal();
    public void CardRevealed(CardRevealed x) { lock (gate) KnownAssets[x.Position] = x.AssetReference; } public void PairMatched(PairMatched x) { Events.Add(new(x, null)); Signal(); } public void PairMissed(PairMissed x) { Events.Add(new(null, CurrentTurn)); Signal(); } public void ScoreUpdated(ScoreUpdated _) { } public void TurnChanged(TurnChanged x) { CurrentTurn = x.PlayerId; } public void GameFinished(GameFinished x) { Finished = true; FinishReason = x.Reason; FinishedTask.TrySetResult(true); Signal(); }
    public TaskCompletionSource<bool> NextOutcome() { lock (gate) { outcome = NewSignal(); return outcome; } } public async Task WaitForRevealAsync(int position) { for (var i = 0; i < 50; i++) { lock (gate) if (KnownAssets.ContainsKey(position)) return; await Task.Delay(20); } throw new TimeoutException($"CardRevealed was not received for position {position}."); } private void Signal() { lock (gate) outcome.TrySetResult(true); } private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

sealed record CliOptions(string Url, string Mode, string Difficulty, string Scenario)
{ public static CliOptions Parse(string[] args) => new(Get(args, "url", "http://127.0.0.1:5090").TrimEnd('/'), Get(args, "mode", "PVP"), Get(args, "difficulty", "Easy"), Get(args, "scenario", "smoke")); private static string Get(string[] args, string name, string fallback) => args.FirstOrDefault(x => x.StartsWith($"--{name}=", StringComparison.OrdinalIgnoreCase))?[($"--{name}=".Length)..] ?? fallback; }
enum GameMode { Time, PVP, AI } enum GameDifficulty { Easy, Medium, Hard } enum GameStatus { Waiting, Preparing, Playing, Finished }
record CreateRoomResponse(string RoomId, string Mode, string Difficulty, GameStatus Status); record JoinRoomResponse(string RoomId, string PlayerId, GameMode Mode, GameDifficulty Difficulty, GameStatus Status, CardView[] Board, string? CurrentTurn, string[]? Players = null, Dictionary<string, int>? Scores = null, Dictionary<string, int>? ConsecutiveHits = null, DateTimeOffset? StartedAt = null, TimeSpan? Duration = null); record CardView(int Position, bool IsRevealed, bool IsMatched, string? AssetReference); record GameState(string RoomId, GameMode Mode, GameDifficulty Difficulty, GameStatus Status, string[] Players, CardView[] Board, string? CurrentTurn, Dictionary<string, int> Scores, Dictionary<string, int> ConsecutiveHits, DateTimeOffset? StartedAt, TimeSpan? Duration); record GameOperationResult(bool Succeeded, string? ErrorCode, string? ErrorMessage, JsonElement Value);
record CardRevealed(string RoomId, int Position, string AssetReference); record PairMatched(string RoomId, int FirstPosition, int SecondPosition, string PlayerId, int EarnedScore, int TotalScore, int Streak); record PairMissed(string RoomId, int FirstPosition, int SecondPosition); record ScoreUpdated(string RoomId, string PlayerId, int EarnedScore, int TotalScore, int Streak); record TurnChanged(string RoomId, string? PlayerId); record GameFinished(string RoomId, Dictionary<string, int> Scores, string Reason);
static class CliJson { public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }; }
record LedgerEvent(PairMatched? Match, string? MissPlayer);
