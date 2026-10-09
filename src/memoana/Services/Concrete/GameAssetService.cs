using memoana.Contracts;
using memoana.Services.Abstract;

namespace memoana.Services.Concrete;

public sealed class GameAssetService(IGameService gameService) : IGameAssetService
{
    public AssetManifest? GetManifest(string roomId, string playerId) => (gameService as GameService)?.GetAssetManifest(roomId, playerId);
    public (byte[] Content, string ContentType)? GetAsset(string roomId, string playerId, string token) => (gameService as GameService)?.GetAsset(roomId, playerId, token);
}
