using memoana.Contracts;
using memoana.Services.Abstract;

namespace memoana.Services.Concrete;

public sealed class GameAssetService(IGameService gameService) : IGameAssetService
{
    public AssetManifest? GetManifest(string roomId, string accessToken) => gameService.GetAssetManifest(roomId, accessToken);
    public (byte[] Content, string ContentType)? GetAsset(string roomId, string accessToken, string token) => gameService.GetAsset(roomId, accessToken, token);
}
