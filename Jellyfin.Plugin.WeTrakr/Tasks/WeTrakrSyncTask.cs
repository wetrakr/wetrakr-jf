using Jellyfin.Plugin.WeTrakr.Sync;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WeTrakr.Tasks;

/// <summary>
/// COMPLETE sync pass with WeTrakr (both directions, no shortcuts): the daily
/// reconciliation, at a quiet hour. Day to day SyncScheduler handles changes
/// with cheap checks; this is the safety net. It can be run by hand from
/// Scheduled Tasks or with "Sync now" on the plugin page.
/// </summary>
public class WeTrakrSyncTask : IScheduledTask
{
    private readonly SyncEngine _engine;

    public WeTrakrSyncTask(SyncEngine engine)
    {
        _engine = engine;
    }

    public string Name => "Sync library with WeTrakr";

    public string Key => "WeTrakrSync";

    public string Description => "Full daily reconciliation with WeTrakr: watched history, resume points, ratings, favorites and the Planning playlist, both ways. Changes during the day are synced as they happen.";

    public string Category => "WeTrakr";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _engine.RunAll(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Between 03:00 and 05:00 server time, with a random minute per server
        // so they do not all ask at once. Jellyfin stores the trigger the first
        // time, so the minute stays fixed for that server.
        var minute = Random.Shared.Next(0, 120);
        return new[]
        {
            new TaskTriggerInfo
            {
#if JF1010
                Type = TaskTriggerInfo.TriggerDaily,
#else
                Type = TaskTriggerInfoType.DailyTrigger,
#endif
                TimeOfDayTicks = TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(minute)).Ticks
            }
        };
    }
}
