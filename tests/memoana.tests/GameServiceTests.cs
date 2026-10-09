using memoana.Contracts;
using memoana.Services.Abstract;
using memoana.Services.Concrete;

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
