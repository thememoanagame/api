using memoana.Contracts;
using memoana.Services.Abstract;
using memoana.Services.Concrete;
using memoana.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace memoana.tests;

public sealed class GameServiceTests
{
    [Theory]
    [InlineData(GameDifficulty.Easy, 12, 6)]
    [InlineData(GameDifficulty.Medium, 20, 10)]
    [InlineData(GameDifficulty.Hard, 30, 15)]
    public void JoinAndReadyCreatesExpectedBoard(GameDifficulty difficulty, int cards, int pairs)
    {
        var service = CreateService();
        var room = (CreateRoomResponse)service.CreateRoom(new CreateRoomRequest(GameMode.Time, difficulty)).Value!;
        var joined = (JoinRoomResponse)service.JoinRoom(room.RoomId, "player-1").Value!;

        Assert.Equal(GameStatus.Preparing, joined.Status);
        Assert.Equal(cards, joined.Board.Count);
        Assert.Equal(pairs, joined.Board.Count / 2);
        Assert.All(joined.Board, card => Assert.Null(card.AssetReference));
        Assert.NotEqual(joined.PlayerId, joined.AccessToken);
        Assert.Equal(GameStatus.Playing, ((GameState)service.AssetsReady(room.RoomId, joined.PlayerId).Value!).Status);
    }

    [Fact]
    public void AssetTokenOnlyWorksWithOwningAccessToken()
    {
        var service = CreateService();
        var room = (CreateRoomResponse)service.CreateRoom(new(GameMode.Time, GameDifficulty.Easy)).Value!;
        var joined = (JoinRoomResponse)service.JoinRoom(room.RoomId, "player-1").Value!;
        var manifest = service.GetAssetManifest(room.RoomId, joined.AccessToken)!;

        Assert.Null(service.GetAssetManifest(room.RoomId, "not-a-token"));
        Assert.Null(service.GetAsset(room.RoomId, "not-a-token", manifest.Assets[0].AssetToken));
        Assert.NotNull(service.GetAsset(room.RoomId, joined.AccessToken, manifest.Assets[0].AssetToken));
    }

    [Fact]
    public void InvalidPreparationIsControlledAndDoesNotExposeException()
    {
        var service = new GameService(new ThrowingThemeProvider());
        var room = (CreateRoomResponse)service.CreateRoom(new(GameMode.Time, GameDifficulty.Easy)).Value!;

        var result = service.JoinRoom(room.RoomId, "player-1");

        Assert.False(result.Succeeded);
        Assert.Equal("assets_preparation_failed", result.ErrorCode);
        Assert.DoesNotContain("internal asset failure", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PvpWaitsForBothParticipantsAndBothReadySignals()
    {
        var service = CreateService();
        var room = (CreateRoomResponse)service.CreateRoom(new(GameMode.PVP, GameDifficulty.Easy)).Value!;
        var first = (JoinRoomResponse)service.JoinRoom(room.RoomId, "connection-a").Value!;
        var second = (JoinRoomResponse)service.JoinRoom(room.RoomId, "connection-b").Value!;

        Assert.Equal(GameStatus.Waiting, first.Status);
        Assert.Equal(GameStatus.Preparing, second.Status);
        Assert.Equal(GameStatus.Preparing, ((GameState)service.AssetsReady(room.RoomId, first.PlayerId).Value!).Status);
        Assert.Equal(GameStatus.Playing, ((GameState)service.AssetsReady(room.RoomId, second.PlayerId).Value!).Status);
    }

    [Fact]
    public void AiDoesNotStartBeforeAssetsAreReady()
    {
        var service = CreateService();
        var room = (CreateRoomResponse)service.CreateRoom(new(GameMode.AI, GameDifficulty.Easy)).Value!;
        var joined = (JoinRoomResponse)service.JoinRoom(room.RoomId, "connection-a").Value!;

        Assert.Equal(GameStatus.Preparing, joined.Status);
        Assert.Null(joined.CurrentTurn);
        var started = (GameState)service.AssetsReady(room.RoomId, joined.PlayerId).Value!;
        Assert.Equal(GameStatus.Playing, started.Status);
        Assert.Equal(joined.PlayerId, started.CurrentTurn);
    }

    [Fact]
    public void SqliteStateSurvivesServiceReconstructionAndAllowsReconnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), "memoana-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "game.db");
        ServiceProvider? provider = null;
        try
        {
            var services = new ServiceCollection();
            services.AddDbContextFactory<MemoAnaDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
            services.AddSingleton<IGameStateStore, SqliteGameStateStore>();
            provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope()) scope.ServiceProvider.GetRequiredService<MemoAnaDbContext>().Database.Migrate();
            var store = provider.GetRequiredService<IGameStateStore>();

            var first = new GameService(new TestThemeProvider(), store);
            var created = (CreateRoomResponse)first.CreateRoom(new(GameMode.Time, GameDifficulty.Easy)).Value!;
            var joined = (JoinRoomResponse)first.JoinRoom(created.RoomId, "connection-a").Value!;
            first.AssetsReady(created.RoomId, joined.PlayerId);

            var restored = new GameService(new TestThemeProvider(), store);
            var state = restored.GetState(created.RoomId, joined.AccessToken);
            Assert.NotNull(state);
            Assert.Equal(GameStatus.Playing, state!.Status);
            Assert.Equal(joined.Board.Count, state.Board.Count);
            Assert.Equal(joined.ThemeId, state.ThemeId);

            var reconnected = restored.JoinRoom(created.RoomId, "connection-b", joined.PlayerId, joined.AccessToken);
            Assert.True(reconnected.Succeeded, reconnected.ErrorMessage);
            Assert.Equal(joined.PlayerId, ((JoinRoomResponse)reconnected.Value!).PlayerId);
        }
        finally
        {
            provider?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static GameService CreateService() => new(new TestThemeProvider());

    private sealed class TestThemeProvider : IThemeProvider
    {
        public IReadOnlyList<ThemeAsset> SelectAssets(int count) => Enumerable.Range(0, count)
            .Select(x => new ThemeAsset(Guid.NewGuid().ToString(), "image/webp", [1, 2, 3])).ToArray();
    }

    private sealed class ThrowingThemeProvider : IThemeProvider
    {
        public IReadOnlyList<ThemeAsset> SelectAssets(int count) => throw new InvalidOperationException("internal asset failure");
    }
}
