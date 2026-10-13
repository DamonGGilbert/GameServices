namespace GameServices.Api.Online;

/// <summary>Sweeps away rooms nobody is using (a waiting room left open, a finished duel's room).</summary>
public class RoomSweeperService : BackgroundService
{
    private readonly RoomRegistry rooms;

    public RoomSweeperService(RoomRegistry rooms)
    {
        this.rooms = rooms;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            rooms.Sweep(waitingTimeout: TimeSpan.FromMinutes(15), finishedTimeout: TimeSpan.FromMinutes(2));
        }
    }
}
