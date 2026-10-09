using memoana.Contracts;

namespace memoana.Services.Abstract;

public interface IGameService
{
    GameOperationResult CreateRoom(CreateRoomRequest request);
    GameOperationResult JoinRoom(string roomId, string playerId);
    GameOperationResult AssetsReady(string roomId, string playerId);
    GameOperationResult LeaveRoom(string roomId, string playerId);
    GameOperationResult FlipCard(string roomId, string playerId, int position);
    AssetManifest? GetAssetManifest(string roomId, string accessToken);
    (byte[] Content, string ContentType)? GetAsset(string roomId, string accessToken, string token);
    GameState? GetState(string roomId);
    IReadOnlyList<RoomEvents> ExpireDueRooms();
}
