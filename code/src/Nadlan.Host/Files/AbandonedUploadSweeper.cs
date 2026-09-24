using Nadlan.Core.Files;

namespace Nadlan.Host.Files;

/// <summary>Background job: shortly after startup and then every 6 hours, removes uploads abandoned for over a day.</summary>
public sealed class AbandonedUploadSweeper : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(1);

    private readonly FileService _files;
    private readonly ILogger<AbandonedUploadSweeper> _log;

    public AbandonedUploadSweeper(FileService files, ILogger<AbandonedUploadSweeper> log)
    {
        _files = files;
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
                    var swept = await _files.SweepAbandonedUploadsAsync(AbandonedAfter, stoppingToken);
                    if (swept > 0)
                    {
                        _log.LogInformation("Removed {Count} abandoned upload(s).", swept);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Storage or DB hiccup: log and try again next round; never take the app down.
                    _log.LogWarning(ex, "Abandoned-upload sweep failed; will retry in {Interval}.", Interval);
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
