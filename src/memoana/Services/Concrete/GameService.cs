using memoana.Contracts;
using memoana.Services.Abstract;
using memoana.Persistence;
using System.Text.Json;
using System.Security.Cryptography;

namespace memoana.Services.Concrete;

/// <summary>Authoritative, in-memory game engine shared by HTTP and SignalR.</summary>
public sealed class GameService : IGameService
{
    private readonly IThemeProvider _themeProvider;
    private readonly IGameStateStore? _stateStore;
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
        [GameDifficulty.Easy] = TimeSpan.FromSeconds(75),
        [GameDifficulty.Medium] = TimeSpan.FromSeconds(100),
        [GameDifficulty.Hard] = TimeSpan.FromSeconds(150)
    };

    private readonly object _roomsGate = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public GameService(IThemeProvider themeProvider, IGameStateStore? stateStore = null)
    {
        _themeProvider = themeProvider;
        _stateStore = stateStore;
        RestoreRooms();
    }

    public IReadOnlyList<ThemeSummary> ListThemes() => _themeProvider.ListThemes();

    public IReadOnlyList<DifficultyOption> ListDifficulties() => PairCounts.Select(x => new DifficultyOption(x.Key, x.Value, x.Value * 2)).ToArray();

    public GameOperationResult CreateRoom(CreateRoomRequest request)
    {
        if (!Enum.IsDefined(request.Mode))
            return GameOperationResult.Failure("invalid_mode", "Game mode is not supported.");
        if (!PairCounts.TryGetValue(request.Difficulty, out var pairCount))
            return GameOperationResult.Failure("invalid_difficulty", "Difficulty is not supported.");

        var themes = _themeProvider.ListThemes();
        var themeId = request.ThemeId ?? _themeProvider.DefaultThemeId ?? themes.FirstOrDefault()?.Id ?? "default";
        var theme = themes.FirstOrDefault(x => string.Equals(x.Id, themeId, StringComparison.OrdinalIgnoreCase));
        if (themes.Count > 0 && theme is null)
            return GameOperationResult.Failure("theme_not_found", "The requested theme does not exist or is unavailable.");
        if (theme is not null && (!theme.Available || theme.CardCount < pairCount))
            return GameOperationResult.Failure(theme.Available ? "theme_insufficient_content" : "theme_unavailable", "The selected theme cannot support this difficulty.");

        var room = new Room(CreateRoomId(), request.Mode, request.Difficulty, themeId);
        lock (_roomsGate)
        {
            _rooms.Add(room.RoomId, room);
        }
        Persist(room);

        return GameOperationResult.Success(new CreateRoomResponse(room.RoomId, room.Mode, room.Difficulty, room.Status, room.ThemeId, pairCount, pairCount * 2));
    }

    public GameOperationResult JoinRoom(string roomId, string connectionId, string? requestedPlayerId = null, string? accessToken = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return GameOperationResult.Failure("invalid_player", "A connection identity is required.");

        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");

        lock (room.Gate)
        {
            if (room.Status == GameStatus.Finished)
                return GameOperationResult.Failure("game_finished", "The game has already finished.");
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var existingPlayer = FindPlayerByToken(room, accessToken);
                if (existingPlayer is null || (!string.IsNullOrWhiteSpace(requestedPlayerId) && existingPlayer != requestedPlayerId))
                    return GameOperationResult.Failure("invalid_participation_token", "The participation token is invalid for this room.");
                if (room.Connections.Values.Any(x => x == existingPlayer && x != connectionId))
                    return GameOperationResult.Failure("player_already_connected", "The participant already has an active connection.");
                room.Connections[connectionId] = existingPlayer;
                return GameOperationResult.Success(ToJoinResponse(room, existingPlayer, accessToken));
            }
            if (room.Connections.ContainsKey(connectionId))
                return GameOperationResult.Failure("already_connected", "This connection already belongs to a room.");
            var playerId = string.IsNullOrWhiteSpace(requestedPlayerId) ? $"player:{Guid.NewGuid():N}" : requestedPlayerId;
            if (room.Players.Contains(playerId))
                return GameOperationResult.Failure("player_already_joined", "This participant already belongs to the room.");
            if (room.Mode == GameMode.AI && room.Players.Count >= 1)
                return GameOperationResult.Failure("room_full", "An AI room accepts one human player.");
            if (room.Mode == GameMode.PVP && room.Players.Count >= 2)
                return GameOperationResult.Failure("room_full", "A PVP room accepts at most two players.");

            room.Players.Add(playerId);
            room.Connections[connectionId] = playerId;
            room.AccessTokens[playerId] = CreateAccessToken();
            room.AccessTokenHashes[playerId] = HashToken(room.AccessTokens[playerId]);
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
            {
                try { PrepareRoomLocked(room, events); }
                catch (Exception)
                {
                    room.Status = GameStatus.Finished;
                    room.Players.Remove(playerId);
                    room.Connections.Remove(connectionId);
                    room.AccessTokens.Remove(playerId);
                    room.Scores.Remove(playerId);
                    room.Streaks.Remove(playerId);
                    return GameOperationResult.Failure("assets_preparation_failed", "The game assets could not be prepared.");
                }
            }

            Persist(room);
            return GameOperationResult.Success(ToJoinResponse(room, playerId), events);
        }
    }

    public GameOperationResult AssetsReady(string roomId, string playerId)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            playerId = ResolvePlayerId(room, playerId) ?? playerId;
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
            Persist(room);
            return GameOperationResult.Success(ToState(room), events);
        }
    }

    public AssetManifest? GetAssetManifest(string roomId, string accessToken)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate) return FindPlayerByToken(room, accessToken) is not null ? room.Manifest : null;
    }

    public (byte[] Content, string ContentType)? GetAsset(string roomId, string accessToken, string token)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate)
        {
            if (FindPlayerByToken(room, accessToken) is null || room.Status == GameStatus.Finished || !room.Assets.TryGetValue(token, out var asset)) return null;
            return (asset.Content, asset.ContentType);
        }
    }

    public GameOperationResult LeaveRoom(string roomId, string playerId)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            playerId = ResolvePlayerId(room, playerId) ?? playerId;
            if (!room.Players.Remove(playerId)) return GameOperationResult.Failure("player_not_in_room", "The player is not in this room.");
            foreach (var connection in room.Connections.Where(x => x.Value == playerId).Select(x => x.Key).ToArray()) room.Connections.Remove(connection);
            room.ReadyPlayers.Remove(playerId);
            room.AccessTokens.Remove(playerId);
            room.AccessTokenHashes.Remove(playerId);
            room.Scores.Remove(playerId);
            room.Streaks.Remove(playerId);
            var events = new List<GameEvent> { new("PlayerLeft", new PlayerLeft(room.RoomId, playerId)) };
            if (room.Mode == GameMode.AI || room.Status == GameStatus.Playing)
                FinishRoomLocked(room, events, "player_left");
            else if (room.Players.Count == 0)
                room.Status = GameStatus.Finished;
            Persist(room);
            return GameOperationResult.Success(ToState(room), events);
        }
    }

    public GameOperationResult Disconnect(string roomId, string connectionId)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            if (!room.Connections.Remove(connectionId)) return GameOperationResult.Failure("connection_not_found", "The connection is not attached to this room.");
            Persist(room);
            return GameOperationResult.Success(ToState(room));
        }
    }

    public GameOperationResult FlipCard(string roomId, string playerId, int position)
    {
        var room = FindRoom(roomId);
        if (room is null) return GameOperationResult.Failure("room_not_found", "The room does not exist.");
        lock (room.Gate)
        {
            playerId = ResolvePlayerId(room, playerId) ?? playerId;
            if (room.Status != GameStatus.Playing) return GameOperationResult.Failure("game_not_playing", "The game is not currently playing.");
            if (room.Mode == GameMode.Time && DateTimeOffset.UtcNow - room.StartedAt >= room.Duration)
            {
                var expired = new List<GameEvent>();
                FinishRoomLocked(room, expired, "time_expired");
                return GameOperationResult.Success(ToState(room), expired);
            }
            if (room.CurrentTurn != playerId) return GameOperationResult.Failure("wrong_turn", "It is not this player's turn.");
            var result = FlipCardLocked(room, playerId, position);
            if (result.Succeeded) Persist(room);
            return result;
        }
    }

    public GameState? GetState(string roomId)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate) return ToState(room);
    }

    public GameState? GetState(string roomId, string accessToken)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate) return FindPlayerByToken(room, accessToken) is null ? null : ToState(room);
    }

    public GameState? GetStateForPlayer(string roomId, string playerId)
    {
        var room = FindRoom(roomId);
        if (room is null) return null;
        lock (room.Gate) return ResolvePlayerId(room, playerId) is not null ? ToState(room) : null;
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
                    Persist(room);
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
        while (room.Status == GameStatus.Playing && room.CurrentTurn == ai && room.PendingPosition is null)
        {
            var available = room.Board.Where(x => !x.IsMatched).ToList();
            if (available.Count < 2) return;
            var first = available[Random.Shared.Next(available.Count)];
            var second = available.FirstOrDefault(x => x.PairKey == first.PairKey && x.Position != first.Position);
            if (second is null || Random.Shared.NextDouble() > AiAccuracy[room.Difficulty])
                second = available.Where(x => x.Position != first.Position).OrderBy(_ => Random.Shared.Next()).First();
            events.AddRange(FlipCardLocked(room, ai, first.Position).Events);
            events.AddRange(FlipCardLocked(room, ai, second.Position).Events);
        }
    }

    private void PrepareRoomLocked(Room room, List<GameEvent> events)
    {
        if (room.Status is not GameStatus.Waiting) return;
        room.Status = GameStatus.Preparing;
        events.Add(new("GamePreparing", new { room.RoomId }));
        var pairCount = PairCounts[room.Difficulty];
        var selected = _themeProvider.SelectAssets(room.ThemeId, pairCount);
        if (selected.Count < pairCount) throw new InvalidOperationException("The theme does not contain enough playable assets.");
        foreach (var asset in selected)
        {
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            room.Assets[token] = asset;
            room.PublicAssets.Add(new AssetManifestEntry(token, asset.ContentType, asset.Content.LongLength, $"/api/game/rooms/{room.RoomId}/assets/{token}"));
            room.Board.Add(new Card(token));
            room.Board.Add(new Card(token));
        }
        for (var i = room.Board.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (room.Board[i], room.Board[j]) = (room.Board[j], room.Board[i]);
        }
        for (var i = 0; i < room.Board.Count; i++) room.Board[i].Position = i;
        room.Manifest = new AssetManifest(room.RoomId, room.ThemeId, pairCount, room.PublicAssets.ToArray());
        events.Add(new("AssetsAvailable", new AssetsAvailable(room.RoomId, room.Manifest)));
    }

    private static void StartRoomLocked(Room room, List<GameEvent> events)
    {
        if (room.Status is not GameStatus.Preparing) return;
        room.CurrentTurn = room.Players[0];
        room.StartedAt = DateTimeOffset.UtcNow;
        room.Duration = room.Mode == GameMode.Time ? TimeLimits[room.Difficulty] : null;
        room.ExpiresAt = room.Duration is null ? null : room.StartedAt + room.Duration;
        room.Status = GameStatus.Playing;
        events.Add(new("GameStarted", new GameStarted(room.RoomId, room.StartedAt.Value, room.Duration)));
        events.Add(new("TurnChanged", new TurnChanged(room.RoomId, room.CurrentTurn)));
    }

    private static void ChangeTurnLocked(Room room, string playerId, List<GameEvent> events)
    {
        room.CurrentTurn = room.Mode == GameMode.AI
            ? (playerId == room.AiPlayerId ? room.Players[0] : room.AiPlayerId)
            : room.Players.FirstOrDefault(x => x != playerId);
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

    private static JoinRoomResponse ToJoinResponse(Room room, string playerId, string? suppliedToken = null) => new(room.RoomId, playerId, suppliedToken ?? room.AccessTokens[playerId], room.Mode, room.Difficulty, room.Status, room.ThemeId, PairCounts[room.Difficulty], ToCards(room), room.CurrentTurn);
    private static GameState ToState(Room room) => new(room.RoomId, room.Mode, room.Difficulty, room.Status, room.ThemeId, room.Players.ToArray(), ToCards(room), room.CurrentTurn, new Dictionary<string, int>(room.Scores), new Dictionary<string, int>(room.Streaks), room.StartedAt, room.Duration);
    private static IReadOnlyList<CardView> ToCards(Room room) => room.Board.Select(x => new CardView(x.Position, x.IsRevealed, x.IsMatched, x.IsRevealed || x.IsMatched ? x.AssetReference : null)).ToArray();

    private static string CreateAccessToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string? FindPlayerByToken(Room room, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        foreach (var entry in room.AccessTokenHashes)
        {
            try
            {
                if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.Value), Convert.FromHexString(HashToken(accessToken)))) return entry.Key;
            }
            catch (FormatException) { return null; }
        }
        return null;
    }

    private static string? ResolvePlayerId(Room room, string id) => room.Players.Contains(id) ? id : room.Connections.GetValueOrDefault(id);

    private void Persist(Room room)
    {
        if (_stateStore is null) return;
        room.Version++;
        var snapshot = new RoomSnapshot(
            room.Players.Select(player => new PlayerSnapshot(player, room.AccessTokenHashes.GetValueOrDefault(player) ?? string.Empty, room.ReadyPlayers.Contains(player))).ToArray(),
            room.Board.Select(card => new CardSnapshot(card.Position, card.PairKey, card.AssetReference, card.IsRevealed, card.IsMatched)).ToArray(),
            room.Assets.Select(asset => new AssetSnapshot(asset.Key, asset.Value.SourceId, asset.Value.ContentType, asset.Value.Content)).ToArray(),
            new Dictionary<string, int>(room.Scores), new Dictionary<string, int>(room.Streaks), new List<AssetManifestEntry>(room.PublicAssets),
            room.CurrentTurn, room.AiPlayerId, room.StartedAt, room.Duration, room.FirstPosition, room.PendingPosition);
        _stateStore.Save(new PersistedRoom
        {
            RoomId = room.RoomId, Mode = (int)room.Mode, Difficulty = (int)room.Difficulty, Status = (int)room.Status,
            ThemeId = room.ThemeId, CreatedAt = room.CreatedAt, ExpiresAt = room.ExpiresAt, UpdatedAt = DateTimeOffset.UtcNow,
            Version = room.Version, SnapshotJson = JsonSerializer.Serialize(snapshot)
        });
    }

    private void RestoreRooms()
    {
        if (_stateStore is null) return;
        foreach (var persisted in _stateStore.LoadRooms())
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RoomSnapshot>(persisted.SnapshotJson);
                if (snapshot is null) continue;
                var room = new Room(persisted.RoomId, (GameMode)persisted.Mode, (GameDifficulty)persisted.Difficulty, persisted.ThemeId)
                {
                    Status = (GameStatus)persisted.Status, CreatedAt = persisted.CreatedAt, ExpiresAt = persisted.ExpiresAt,
                    Version = persisted.Version, CurrentTurn = snapshot.CurrentTurn, AiPlayerId = snapshot.AiPlayerId,
                    StartedAt = snapshot.StartedAt, Duration = snapshot.Duration, FirstPosition = snapshot.FirstPosition, PendingPosition = snapshot.PendingPosition
                };
                foreach (var player in snapshot.Players)
                {
                    room.Players.Add(player.PlayerId);
                    room.AccessTokenHashes[player.PlayerId] = player.TokenHash;
                    if (player.Ready) room.ReadyPlayers.Add(player.PlayerId);
                }
                foreach (var score in snapshot.Scores) room.Scores[score.Key] = score.Value;
                foreach (var streak in snapshot.Streaks) room.Streaks[streak.Key] = streak.Value;
                foreach (var asset in snapshot.Assets) room.Assets[asset.AssetToken] = new ThemeAsset(asset.SourceId, asset.ContentType, asset.Content);
                room.PublicAssets.AddRange(snapshot.Manifest);
                foreach (var card in snapshot.Cards) room.Board.Add(new Card(card.AssetReference, card.PairKey) { Position = card.Position, IsRevealed = card.IsRevealed, IsMatched = card.IsMatched });
                if (room.Status == GameStatus.Playing && room.ExpiresAt is not null && room.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    room.Status = GameStatus.Finished;
                    room.CurrentTurn = null;
                }
                room.Manifest = room.PublicAssets.Count == 0 ? null : new AssetManifest(room.RoomId, room.ThemeId, PairCounts[room.Difficulty], room.PublicAssets.ToArray());
                lock (_roomsGate) _rooms[room.RoomId] = room;
                if (room.Status == GameStatus.Finished && persisted.Status != (int)GameStatus.Finished) Persist(room);
            }
            catch (JsonException) { }
        }
    }

    private sealed record RoomSnapshot(IReadOnlyList<PlayerSnapshot> Players, IReadOnlyList<CardSnapshot> Cards, IReadOnlyList<AssetSnapshot> Assets, IReadOnlyDictionary<string, int> Scores, IReadOnlyDictionary<string, int> Streaks, IReadOnlyList<AssetManifestEntry> Manifest, string? CurrentTurn, string? AiPlayerId, DateTimeOffset? StartedAt, TimeSpan? Duration, int? FirstPosition, int? PendingPosition);
    private sealed record PlayerSnapshot(string PlayerId, string TokenHash, bool Ready);
    private sealed record CardSnapshot(int Position, string PairKey, string AssetReference, bool IsRevealed, bool IsMatched);
    private sealed record AssetSnapshot(string AssetToken, string SourceId, string ContentType, byte[] Content);

    private sealed class Room(string roomId, GameMode mode, GameDifficulty difficulty, string themeId)
    {
        public string RoomId { get; } = roomId;
        public GameMode Mode { get; } = mode;
        public GameDifficulty Difficulty { get; } = difficulty;
        public string ThemeId { get; } = themeId;
        public List<Card> Board { get; } = [];
        public Dictionary<string, ThemeAsset> Assets { get; } = new(StringComparer.Ordinal);
        public List<AssetManifestEntry> PublicAssets { get; } = [];
        public AssetManifest? Manifest { get; set; }
        public List<string> Players { get; } = [];
        public Dictionary<string, string> AccessTokens { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> AccessTokenHashes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Connections { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ReadyPlayers { get; } = [];
        public Dictionary<string, int> Scores { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Streaks { get; } = new(StringComparer.Ordinal);
        public object Gate { get; } = new();
        public GameStatus Status { get; set; } = GameStatus.Waiting;
        public string? CurrentTurn { get; set; }
        public string? AiPlayerId { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public TimeSpan? Duration { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ExpiresAt { get; set; }
        public long Version { get; set; }
        public int? FirstPosition { get; set; }
        public int? PendingPosition { get; set; }
    }

    private sealed class Card(string assetReference, string? pairKey = null)
    {
        public string PairKey { get; } = pairKey ?? assetReference;
        public int Position { get; set; }
        public bool IsRevealed { get; set; }
        public bool IsMatched { get; set; }
        public string AssetReference { get; } = assetReference;
    }
}
