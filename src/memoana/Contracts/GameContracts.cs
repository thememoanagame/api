using System.Text.Json.Serialization;

namespace memoana.Contracts;

public enum GameMode
{
    Time,
    PVP,
    AI
}

public enum GameDifficulty
{
    Easy,
    Medium,
    Hard
}

public enum GameStatus
{
    Waiting,
    Preparing,
    Playing,
    Finished
}

public sealed record CreateRoomRequest(GameMode Mode, GameDifficulty Difficulty);

public sealed record CreateRoomResponse(
    string RoomId,
    GameMode Mode,
    GameDifficulty Difficulty,
    GameStatus Status);

public sealed record JoinRoomResponse(
    string RoomId,
    string PlayerId,
    GameMode Mode,
    GameDifficulty Difficulty,
    GameStatus Status,
    IReadOnlyList<CardView> Board,
    string? CurrentTurn);

public sealed record AssetManifest(string RoomId, IReadOnlyList<AssetManifestEntry> Assets);
public sealed record AssetManifestEntry(string AssetToken, string ContentType, long Size);

public sealed record CardView(
    int Position,
    bool IsRevealed,
    bool IsMatched,
    string? AssetReference);

public sealed record GameState(
    string RoomId,
    GameMode Mode,
    GameDifficulty Difficulty,
    GameStatus Status,
    IReadOnlyList<string> Players,
    IReadOnlyList<CardView> Board,
    string? CurrentTurn,
    IReadOnlyDictionary<string, int> Scores,
    IReadOnlyDictionary<string, int> ConsecutiveHits,
    DateTimeOffset? StartedAt,
    TimeSpan? Duration);

public sealed record CardRevealed(string RoomId, int Position, string AssetReference);
public sealed record PairMatched(string RoomId, int FirstPosition, int SecondPosition, string PlayerId, int EarnedScore, int TotalScore, int Streak);
public sealed record PairMissed(string RoomId, int FirstPosition, int SecondPosition);
public sealed record TurnChanged(string RoomId, string? PlayerId);
public sealed record ScoreUpdated(string RoomId, string PlayerId, int EarnedScore, int TotalScore, int Streak);
public sealed record GameFinished(string RoomId, IReadOnlyDictionary<string, int> Scores, string Reason);
public sealed record PlayerJoined(string RoomId, string PlayerId);
public sealed record PlayerLeft(string RoomId, string PlayerId);
public sealed record GameStarted(string RoomId, DateTimeOffset StartedAt, TimeSpan? Duration);
public sealed record AssetsAvailable(string RoomId, AssetManifest Manifest);
public sealed record AssetsReady(string RoomId, string PlayerId);
public sealed record GameError(string Code, string Message);

public sealed record GameEvent(string Name, object Payload);
public sealed record RoomEvents(string RoomId, IReadOnlyList<GameEvent> Events);

public sealed class GameOperationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public object? Value { get; init; }
    public IReadOnlyList<GameEvent> Events { get; init; } = [];

    public static GameOperationResult Success(object? value = null, IReadOnlyList<GameEvent>? events = null) =>
        new() { Succeeded = true, Value = value, Events = events ?? [] };

    public static GameOperationResult Failure(string code, string message) =>
        new() { ErrorCode = code, ErrorMessage = message };
}
