using memoana.Contracts;
using memoana.Services.Abstract;
using Microsoft.AspNetCore.Mvc;

namespace memoana.Controllers;

[ApiController]
[Route("api/game")]
public sealed class GameController(IGameService gameService) : ControllerBase
{
    [HttpPost("rooms")]
    public ActionResult<CreateRoomResponse> CreateRoom(CreateRoomRequest request)
    {
        var result = gameService.CreateRoom(request);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new GameError(result.ErrorCode!, result.ErrorMessage!));
    }

    [HttpGet("rooms/{roomId}")]
    public ActionResult<GameState> GetRoom(string roomId)
    {
        var state = gameService.GetState(roomId);
        return state is null ? NotFound(new GameError("room_not_found", "The room does not exist.")) : Ok(state);
    }
}
