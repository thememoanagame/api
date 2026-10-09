using memoana.Contracts;
using memoana.Services.Abstract;
using Microsoft.AspNetCore.Mvc;

namespace memoana.Controllers;

[ApiController]
[Route("api/game")]
public sealed class GameController(IGameService gameService, IGameAssetService assetService) : ControllerBase
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

    [HttpGet("rooms/{roomId}/assets")]
    public ActionResult<AssetManifest> GetAssets(string roomId)
    {
        var playerId = Request.Headers["X-Player-Id"].ToString();
        var manifest = assetService.GetManifest(roomId, playerId);
        return manifest is null ? NotFound(new GameError("assets_not_available", "Assets are not available to this player.")) : Ok(manifest);
    }

    [HttpGet("rooms/{roomId}/assets/{token}")]
    public IActionResult GetAsset(string roomId, string token)
    {
        var playerId = Request.Headers["X-Player-Id"].ToString();
        var asset = assetService.GetAsset(roomId, playerId, token);
        return asset is null ? NotFound(new GameError("asset_not_found", "The asset is not available to this player.")) : File(asset.Value.Content, asset.Value.ContentType);
    }
}
