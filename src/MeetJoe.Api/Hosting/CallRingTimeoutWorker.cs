using MeetJoe.Application.Calls;

namespace MeetJoe.Api.Hosting;

/// <summary>Ends unanswered ringing calls after <see cref="Domain.Call.RingTimeout"/>.</summary>
public sealed class CallRingTimeoutWorker(IServiceScopeFactory scopes, ILogger<CallRingTimeoutWorker> log)
    : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var calls = scope.ServiceProvider.GetRequiredService<CallService>();
                var ended = await calls.ExpireUnansweredRingsAsync(stoppingToken);
                if (ended > 0)
                    log.LogInformation("Ended {Count} unanswered call(s) after the ring timeout.", ended);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed while expiring unanswered calls.");
            }
        }
    }
}
