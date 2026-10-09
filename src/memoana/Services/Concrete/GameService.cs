using memoana.Contracts;
using memoana.Services.Abstract;

namespace memoana.Services.Concrete;

/// <summary>Authoritative, in-memory game engine shared by HTTP and SignalR.</summary>
public sealed class GameService : IGameService
{
    private readonly IThemeProvider _themeProvider;
    private const int BaseScore = 100;
    private const double ScoreMultiplier = 1.5;
    private static readonly IReadOnlyDictionary<GameDifficulty, int> PairCounts = new Dictionary<GameDifficulty, int>
    {
        [GameDifficulty.Easy] = 6,
        [GameDifficulty.Medium] = 10,
        [GameDifficulty.Hard] = 15
    };
    private static readonly IReadOnlyDictionary<GameDifficulty, double> AiAccuracy = new Dictionary<GameDifficulty, double>
    {
        [GameDifficulty.Easy] = 0.25,
        [GameDifficulty.Medium] = 0.55,
        [GameDifficulty.Hard] = 0.85
    };
    private static readonly IReadOnlyDictionary<GameDifficulty, TimeSpan> TimeLimits = new Dictionary<GameDifficulty, TimeSpan>
    {
        [GameDifficulty.Easy] = TimeSpan.FromMinutes(5),
        [GameDifficulty.Medium] = TimeSpan.FromMinutes(3),
        [GameDifficulty.Hard] = TimeSpan.FromMinutes(2)
    };

    private readonly object _roomsGate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public GameService(IThemeProvider themeProvider) => _themeProvider = themeProvider;

    public GameOperationResult CreateRoom(CreateRoomRequest request)
    {
        if (!PairCounts.ContainsKey(request.Difficulty))
            return GameOperationResult.Failure("invalid_difficulty", "Difficulty is not supported.");

        var room = new Room(CreateRoomId(), request.Mode, request.Difficulty);
        lock (_roomsGate)
        {
            _rooms.Add(room.RoomId, room);
        }

        return GameOperationResult.Success(new CreateRoomResponse(room.RoomId, room.Mode, room.Difficulty, room.Status));
    }

    public GameOperationResult JoinRoom(string roomId, string playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
            return GameOperationResult.Failure("invalid_player", "A connection identity is required.");

        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");

        lock (room.Gate)
        {
            if (room.Status == GameStatus.Finished)
                return GameOperationResult.Failure("game_finished", "The game has already finished.");
            if (room.Players.Contains(playerId))
                return GameOperationResult.Failure("player_already_joined", "This connection already belongs to the room.");
            if (room.Mode == GameMode.AI && room.Players.Count >= 1)
                return GameOperationResult.Failure("room_full", "An AI room accepts one human player.");
            if (room.Mode == GameMode.PVP && room.Players.Count >= 2)
                return GameOperationResult.Failure("room_full", "A PVP room accepts at most two players.");

            room.Players.Add(playerId);
            room.Scores[playerId] = 0;
            room.Streaks[playerId] = 0;
            if (room.Mode == GameMode.AI)
            {
                room.AiPlayerId = $"ai:{room.RoomId}";
                room.Scores[room.AiPlayerId] = 0;
                room.Streaks[room.AiPlayerId] = 0;
            }

            var events = new List<GameEvent> { new("PlayerJoined", new PlayerJoined(room.RoomId, playerId)) };
            if (room.Mode is GameMode.AI or GameMode.Time || room.Players.Count == 2)
                PrepareRoomLocked(room, events);

            return GameOperationResult.Success(ToJoinResponse(room, playerId), events);
        }
    }

    public GameOperationResult AssetsReady(string roomId, string playerId)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            if (!room.Players.Contains(playerId)) return GameOperationResult.Failure("player_not_in_room", "The player is not in this room.");
            if (room.Status is GameStatus.Playing) return GameOperationResult.Success(ToState(room));
            if (room.Status is GameStatus.Finished) return GameOperationResult.Failure("game_finished", "The game has already finished.");
            if (room.Status != GameStatus.Preparing || room.Manifest is null)
                return GameOperationResult.Failure("assets_not_available", "The asset manifest is not ready.");
            if (!room.ReadyPlayers.Add(playerId))
                return GameOperationResult.Failure("assets_already_ready", "This player already confirmed the asset cache.");
            var events = new List<GameEvent>();
            events.Add(new("AssetsReady", new AssetsReady(room.RoomId, playerId)));
            var requiredPlayers = room.Mode == GameMode.PVP ? 2 : 1;
            if (room.ReadyPlayers.Count >= requiredPlayers)
                StartRoomLocked(room, events);
            return GameOperationResult.Success(ToState(room), events);
        }
    }

    public AssetManifest? GetAssetManifest(string roomId, string playerId)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate) return room.Players.Contains(playerId) ? room.Manifest : null;
    }

    public (byte[] Content, string ContentType)? GetAsset(string roomId, string playerId, string token)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate)
        {
            if (!room.Players.Contains(playerId) || room.Status == GameStatus.Finished || !room.Assets.TryGetValue(token, out var asset)) return null;
            return (asset.Content, asset.ContentType);
        }
    }

    public GameOperationResult LeaveRoom(string roomId, string playerId)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            if (!room.Players.Remove(playerId)) return GameOperationResult.Failure("player_not_in_room", "The player is not in this room.");
            room.ReadyPlayers.Remove(playerId);
            room.Scores.Remove(playerId);
            room.Streaks.Remove(playerId);
            var events = new List<GameEvent> { new("PlayerLeft", new PlayerLeft(room.RoomId, playerId)) };
            if (room.Mode == GameMode.AI || room.Status == GameStatus.Playing)
                FinishRoomLocked(room, events, "player_left");
            else if (room.Players.Count == 0)
                room.Status = GameStatus.Finished;
            return GameOperationResult.Success(ToState(room), events);
        }
    }

    public GameOperationResult FlipCard(string roomId, string playerId, int position)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            if (room.Status != GameStatus.Playing) return GameOperationResult.Failure("game_not_playing", "The game is not currently playing.");
            if (room.Mode == GameMode.Time && DateTimeOffset.UtcNow - room.StartedAt >= room.Duration)
            {
                var expired = new List<GameEvent>();
                FinishRoomLocked(room, expired, "time_expired");
                return GameOperationResult.Success(ToState(room), expired);
            }
            if (room.CurrentTurn != playerId) return GameOperationResult.Failure("wrong_turn", "It is not this player's turn.");
            return FlipCardLocked(room, playerId, position);
        }
    }

    public GameState? GetState(string roomId)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate) return ToState(room);
    }

    public IReadOnlyList<RoomEvents> ExpireDueRooms()
    {
        var expired = new List<RoomEvents>();
        lock (_roomsGate)
        {
            foreach (var room in _rooms.Values)
            {
                lock (room.Gate)
                {
                    if (room.Mode != GameMode.Time || room.Status != GameStatus.Playing ||
                        room.StartedAt is null || room.Duration is null ||
                        DateTimeOffset.UtcNow - room.StartedAt < room.Duration)
                        continue;

                    var events = new List<GameEvent>();
                    FinishRoomLocked(room, events, "time_expired");
                    expired.Add(new RoomEvents(room.RoomId, events));
                }
            }
        }

        return expired;
    }

    private GameOperationResult FlipCardLocked(Room room, string playerId, int position)
    {
        if (position < 0 || position >= room.Board.Count) return GameOperationResult.Failure("invalid_position", "The card position is invalid.");
        var card = room.Board[position];
        if (card.IsMatched) return GameOperationResult.Failure("card_matched", "The card has already been matched.");
        if (card.IsRevealed) return GameOperationResult.Failure("card_selected", "The card is already selected.");
        card.IsRevealed = true;
        room.PendingPosition = position;
        var events = new List<GameEvent> { new("CardRevealed", new CardRevealed(room.RoomId, position, card.AssetReference)) };
        if (room.FirstPosition is null)
        {
            room.FirstPosition = position;
            return GameOperationResult.Success(ToState(room), events);
        }

        var first = room.Board[room.FirstPosition.Value];
        var second = card;
        room.PendingPosition = null;
        room.FirstPosition = null;
        if (first.PairKey == second.PairKey)
        {
            first.IsMatched = second.IsMatched = true;
            var streak = ++room.Streaks[playerId];
            var earned = streak == 1 ? BaseScore : (int)Math.Floor(BaseScore * Math.Pow(ScoreMultiplier, streak - 1));
            room.Scores[playerId] += earned;
            events.Add(new("PairMatched", new PairMatched(room.RoomId, first.Position, second.Position, playerId, earned, room.Scores[playerId], streak)));
            events.Add(new("ScoreUpdated", new ScoreUpdated(room.RoomId, playerId, earned, room.Scores[playerId], streak)));
            if (room.Board.All(x => x.IsMatched)) FinishRoomLocked(room, events, "all_pairs_matched");
        }
        else
        {
            first.IsRevealed = second.IsRevealed = false;
            room.Streaks[playerId] = 0;
            events.Add(new("PairMissed", new PairMissed(room.RoomId, first.Position, second.Position)));
            ChangeTurnLocked(room, playerId, events);
            if (room.Mode == GameMode.AI && room.Status == GameStatus.Playing && room.CurrentTurn == room.AiPlayerId)
                PlayAiTurnLocked(room, events);
        }

        return GameOperationResult.Success(ToState(room), events);
    }

    private void PlayAiTurnLocked(Room room, List<GameEvent> events)
    {
        var ai = room.AiPlayerId!;
        var available = room.Board.Where(x => !x.IsMatched).ToList();
        if (available.Count < 2) return;
        var first = available[Random.Shared.Next(available.Count)];
        var second = available.FirstOrDefault(x => x.PairKey == first.PairKey && x.Position != first.Position);
        if (second is null || Random.Shared.NextDouble() > AiAccuracy[room.Difficulty])
            second = available.Where(x => x.Position != first.Position).OrderBy(_ => Random.Shared.Next()).First();
        var firstResult = FlipCardLocked(room, ai, first.Position);
        events.AddRange(firstResult.Events);
        var secondResult = FlipCardLocked(room, ai, second.Position);
        events.AddRange(secondResult.Events);
        if (room.Status == GameStatus.Playing && room.CurrentTurn == ai && room.PendingPosition is null && room.Board.Any(x => !x.IsMatched))
            PlayAiTurnLocked(room, events);
    }

    private void PrepareRoomLocked(Room room, List<GameEvent> events)
    {
        if (room.Status is not GameStatus.Waiting) return;
        room.Status = GameStatus.Preparing;
        events.Add(new("GamePreparing", new { room.RoomId }));
        var pairCount = PairCounts[room.Difficulty];
        var selected = _themeProvider.SelectAssets(pairCount);
        if (selected.Count < pairCount) throw new InvalidOperationException("The theme does not contain enough playable assets.");
        foreach (var asset in selected)
        {
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            room.Assets[token] = asset;
            room.PublicAssets.Add(new AssetManifestEntry(token, asset.ContentType, asset.Content.LongLength));
            room.Board.Add(new Card(token));
            room.Board.Add(new Card(token));
        }
        for (var i = room.Board.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (room.Board[i], room.Board[j]) = (room.Board[j], room.Board[i]);
        }
        for (var i = 0; i < room.Board.Count; i++) room.Board[i].Position = i;
        room.Manifest = new AssetManifest(room.RoomId, room.PublicAssets.ToArray());
        events.Add(new("AssetsAvailable", new AssetsAvailable(room.RoomId, room.Manifest)));
    }

    private static void StartRoomLocked(Room room, List<GameEvent> events)
    {
        if (room.Status is not GameStatus.Preparing) return;
        room.CurrentTurn = room.Players[0];
        room.StartedAt = DateTimeOffset.UtcNow;
        room.Duration = room.Mode == GameMode.Time ? TimeLimits[room.Difficulty] : null;
        room.Status = GameStatus.Playing;
        events.Add(new("GameStarted", new GameStarted(room.RoomId, room.StartedAt.Value, room.Duration)));
        events.Add(new("TurnChanged", new TurnChanged(room.RoomId, room.CurrentTurn)));
    }

    private static void ChangeTurnLocked(Room room, string playerId, List<GameEvent> events)
    {
        room.CurrentTurn = room.Mode == GameMode.AI ? room.AiPlayerId : room.Players.FirstOrDefault(x => x != playerId);
        events.Add(new("TurnChanged", new TurnChanged(room.RoomId, room.CurrentTurn)));
    }

    private static void FinishRoomLocked(Room room, List<GameEvent> events, string reason)
    {
        room.Status = GameStatus.Finished;
        room.CurrentTurn = null;
        events.Add(new("GameFinished", new GameFinished(room.RoomId, new Dictionary<string, int>(room.Scores), reason)));
    }

    private Room? FindRoom(string roomId)
    {
        lock (_roomsGate) return _rooms.GetValueOrDefault(roomId);
    }

    private string CreateRoomId()
    {
        lock (_roomsGate)
        {
            string id;
            do id = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant(); while (_rooms.ContainsKey(id));
            return id;
        }
    }

    private static JoinRoomResponse ToJoinResponse(Room room, string playerId) => new(room.RoomId, playerId, room.Mode, room.Difficulty, room.Status, ToCards(room), room.CurrentTurn);
    private static GameState ToState(Room room) => new(room.RoomId, room.Mode, room.Difficulty, room.Status, room.Players.ToArray(), ToCards(room), room.CurrentTurn, new Dictionary<string, int>(room.Scores), new Dictionary<string, int>(room.Streaks), room.StartedAt, room.Duration);
    private static IReadOnlyList<CardView> ToCards(Room room) => room.Board.Select(x => new CardView(x.Position, x.IsRevealed, x.IsMatched, x.IsRevealed || x.IsMatched ? x.AssetReference : null)).ToArray();

    private sealed class Room(string roomId, GameMode mode, GameDifficulty difficulty)
    {
        public string RoomId { get; } = roomId;
        public GameMode Mode { get; } = mode;
        public GameDifficulty Difficulty { get; } = difficulty;
        public List<Card> Board { get; } = [];
        public Dictionary<string, ThemeAsset> Assets { get; } = new(StringComparer.Ordinal);
        public List<AssetManifestEntry> PublicAssets { get; } = [];
        public AssetManifest? Manifest { get; set; }
        public List<string> Players { get; } = [];
        public HashSet<string> ReadyPlayers { get; } = [];
        public Dictionary<string, int> Scores { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Streaks { get; } = new(StringComparer.Ordinal);
        public object Gate { get; } = new();
        public GameStatus Status { get; set; } = GameStatus.Waiting;
        public string? CurrentTurn { get; set; }
        public string? AiPlayerId { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public int? FirstPosition { get; set; }
        public int? PendingPosition { get; set; }
    }

    private sealed class Card(string assetReference)
    {
        public string PairKey { get; } = assetReference;
        public int Position { get; set; }
        public bool IsRevealed { get; set; }
        public bool IsMatched { get; set; }
        public string AssetReference { get; } = assetReference;
    }
}
