using memoana.Contracts;

namespace memoana.Services.Abstract;

public interface IGameService
{
    GameOperationResult CreateRoom(CreateRoomRequest request);
    GameOperationResult JoinRoom(string roomId, string playerId);
    GameOperationResult Ready(string roomId, string playerId);
    GameOperationResult LeaveRoom(string roomId, string playerId);
    GameOperationResult FlipCard(string roomId, string playerId, int position);
    GameState? GetState(string roomId);
}
