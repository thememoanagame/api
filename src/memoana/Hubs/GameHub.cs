using memoana.Contracts;
using memoana.Services.Abstract;
using Microsoft.AspNetCore.SignalR;

namespace memoana.Hubs;

public sealed class GameHub(IGameService gameService) : Hub
{
    public async Task<GameOperationResult> JoinRoom(string roomId)
    {
        if (Context.Items.TryGetValue("roomId", out var existingRoom) &&
            !string.Equals(existingRoom?.ToString(), roomId, StringComparison.OrdinalIgnoreCase))
            return await Reject(GameOperationResult.Failure("already_in_room", "This connection already belongs to another room."));

        var result = gameService.JoinRoom(roomId, Context.ConnectionId);
        if (!result.Succeeded) return await Reject(result);
        Context.Items["roomId"] = roomId;
        await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
        await Publish(roomId, result.Events);
        return result;
    }

    public async Task<GameOperationResult> LeaveRoom(string roomId)
    {
        var result = gameService.LeaveRoom(roomId, Context.ConnectionId);
        if (result.Succeeded)
        {
            await Publish(roomId, result.Events);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
            Context.Items.Remove("roomId");
        }
        return result;
    }

    public async Task<GameOperationResult> AssetsReady(string roomId)
    {
        var result = gameService.AssetsReady(roomId, Context.ConnectionId);
        if (!result.Succeeded) return await Reject(result);
        await Publish(roomId, result.Events);
        return result;
    }

    public Task<GameState?> GetState(string roomId) => Task.FromResult(gameService.GetStateForPlayer(roomId, Context.ConnectionId));

    public async Task<GameOperationResult> FlipCard(string roomId, int position)
    {
        var result = gameService.FlipCard(roomId, Context.ConnectionId, position);
        if (!result.Succeeded) return await Reject(result);
        await Publish(roomId, result.Events);
        return result;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var rooms = new[] { Context.Items.TryGetValue("roomId", out var value) ? value?.ToString() : null };
        foreach (var roomId in rooms.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var result = gameService.LeaveRoom(roomId!, Context.ConnectionId);
            if (result.Succeeded)
            {
                await Publish(roomId!, result.Events);
                Context.Items.Remove("roomId");
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    private async Task Publish(string roomId, IReadOnlyList<GameEvent> events)
    {
        foreach (var gameEvent in events)
            await Clients.Group(roomId).SendAsync(gameEvent.Name, gameEvent.Payload);
    }

    private async Task<GameOperationResult> Reject(GameOperationResult result)
    {
        await Clients.Caller.SendAsync("Error", new GameError(result.ErrorCode!, result.ErrorMessage!));
        return result;
    }
}
