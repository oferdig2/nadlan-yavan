using Nadlan.Core.Work;

namespace Nadlan.Host.Work;

/// <summary>
/// The work module's background job (pattern: AbandonedUploadSweeper): shortly after startup and then daily, deletes the
/// contractors' track points older than 90 days and removes work picture uploads abandoned for over a day.
/// </summary>
public sealed class WorkSweeper : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly WorkMediaService _media;
    private readonly ILogger<WorkSweeper> _log;

    public WorkSweeper(WorkMediaService media, ILogger<WorkSweeper> log)
    {
        _media = media;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    var (locations, uploads) = await _media.SweepAsync(stoppingToken);
                    if (locations > 0 || uploads > 0)
                    {
                        _log.LogInformation("Work requests: deleted {Locations} track point(s) older than 90 days, {Uploads} abandoned upload(s).", locations, uploads);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Storage or DB hiccup: log and try again tomorrow; never take the app down.
                    _log.LogWarning(ex, "Work requests clean-up failed; will retry in {Interval}.", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
