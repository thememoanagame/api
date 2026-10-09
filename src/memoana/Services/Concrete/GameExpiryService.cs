using memoana.Hubs;
using memoana.Services.Abstract;
using Microsoft.AspNetCore.SignalR;

namespace memoana.Services.Concrete;

public sealed class GameExpiryService(IGameService gameService, IHubContext<GameHub> hubContext) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var room in gameService.ExpireDueRooms())
                foreach (var gameEvent in room.Events)
                    await hubContext.Clients.Group(room.RoomId).SendAsync(gameEvent.Name, gameEvent.Payload, stoppingToken);
        }
    }
}
