using memoana.Contracts;

namespace memoana.Services.Abstract;

public interface IGameAssetService
{
    AssetManifest? GetManifest(string roomId, string playerId);
    (byte[] Content, string ContentType)? GetAsset(string roomId, string playerId, string token);
}
