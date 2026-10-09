using memoana.Contracts;
using memoana.Services.Abstract;
using Microsoft.AspNetCore.Mvc;

namespace memoana.Controllers;

[ApiController]
[Route("api/game")]
public sealed class GameController(IGameService gameService, IGameAssetService assetService) : ControllerBase
{
    [HttpGet("themes")]
    public ActionResult<IReadOnlyList<ThemeSummary>> ListThemes() => Ok(gameService.ListThemes());

    [HttpGet("difficulties")]
    public ActionResult<IReadOnlyList<DifficultyOption>> ListDifficulties() => Ok(gameService.ListDifficulties());

    [HttpPost("rooms")]
    public ActionResult<CreateRoomResponse> CreateRoom(CreateRoomRequest request)
    {
        var result = gameService.CreateRoom(request);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new GameError(result.ErrorCode!, result.ErrorMessage!));
    }

    [HttpGet("rooms/{roomId}")]
    public ActionResult<GameState> GetRoom(string roomId)
    {
        var state = gameService.GetState(roomId, Request.Headers["X-Player-Token"].ToString());
        return state is null ? NotFound(new GameError("room_not_found_or_unauthorized", "The room does not exist or the participant is not authorized.")) : Ok(state);
    }

    [HttpGet("rooms/{roomId}/assets")]
    public ActionResult<AssetManifest> GetAssets(string roomId)
    {
        var accessToken = Request.Headers["X-Player-Token"].ToString();
        var manifest = assetService.GetManifest(roomId, accessToken);
        return manifest is null ? NotFound(new GameError("assets_not_available", "Assets are not available to this player.")) : Ok(manifest);
    }

    [HttpGet("rooms/{roomId}/assets/{token}")]
    public IActionResult GetAsset(string roomId, string token)
    {
        var accessToken = Request.Headers["X-Player-Token"].ToString();
        var asset = assetService.GetAsset(roomId, accessToken, token);
        return asset is null ? NotFound(new GameError("asset_not_found", "The asset is not available to this player.")) : File(asset.Value.Content, asset.Value.ContentType);
    }
}
