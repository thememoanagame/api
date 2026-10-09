using memoana.Contracts;

namespace memoana.Services.Abstract;

public interface IGameService
{
    IReadOnlyList<ThemeSummary> ListThemes();
    IReadOnlyList<DifficultyOption> ListDifficulties();
    GameOperationResult CreateRoom(CreateRoomRequest request);
    GameOperationResult JoinRoom(string roomId, string connectionId, string? playerId = null, string? accessToken = null);
    GameOperationResult Disconnect(string roomId, string connectionId);
    GameOperationResult AssetsReady(string roomId, string playerId);
    GameOperationResult LeaveRoom(string roomId, string playerId);
    GameOperationResult FlipCard(string roomId, string playerId, int position);
    AssetManifest? GetAssetManifest(string roomId, string accessToken);
    (byte[] Content, string ContentType)? GetAsset(string roomId, string accessToken, string token);
    GameState? GetState(string roomId);
    GameState? GetState(string roomId, string accessToken);
    GameState? GetStateForPlayer(string roomId, string playerId);
    IReadOnlyList<RoomEvents> ExpireDueRooms();
}
