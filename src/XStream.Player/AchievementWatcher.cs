using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// During a stream: looks at the running game's achievements about every 30 s (the cheap unlocked-only list) and
/// reports each one unlocked since the stream started. Best effort: a failed look is skipped, not retried at once.
/// </summary>
public sealed class AchievementWatcher(XboxSocial social, string locale)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts polling on a background thread until <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <param name="currentTitle">The console's running title id (from the stream's presence check; null/empty = none yet),
    /// or null to look it up here (cloud streams).</param>
    /// <param name="enabled">Whether alerts are on right now (the setting can change mid-stream).</param>
    /// <param name="unlocked">Called for each new achievement, a few seconds apart when several arrive together.</param>
    public void Start(Func<string?>? currentTitle, Func<bool> enabled, Action<Achievement> unlocked, Action<string>? log, CancellationToken ct)
    {
        var tracker = new AchievementTracker(DateTimeOffset.UtcNow);
        new Thread(() =>
        {
            try { RunAsync(tracker, currentTitle, enabled, unlocked, log, ct).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }) { IsBackground = true, Name = "xstream-achievements" }.Start();
    }

    private async Task RunAsync(AchievementTracker tracker, Func<string?>? currentTitle, Func<bool> enabled,
        Action<Achievement> unlocked, Action<string>? log, CancellationToken ct)
    {
        var wait = TimeSpan.FromSeconds(10); // the console's title is known ~5 s in
        int failures = 0;
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(wait, ct);
            wait = Interval;
            if (!enabled()) continue;
            try
            {
                string? title = currentTitle is not null ? currentTitle() : await social.GetNowPlayingAnyDeviceAsync(ct);
                if (string.IsNullOrEmpty(title)) { wait = TimeSpan.FromSeconds(10); continue; }
                if (title != last) { last = title; failures = 0; }
                bool first = !tracker.Knows(title);
                var fresh = tracker.Update(title, await social.GetAchievementsAsync(title, locale, unlockedOnly: true, ct));
                if (first) log?.Invoke($"Achievements: watching title {title}");
                failures = 0;
                foreach (var a in fresh)
                {
                    log?.Invoke($"Achievement unlocked ({a.Gamerscore}G)");
                    unlocked(a);
                    if (fresh.Count > 1) await Task.Delay(TimeSpan.FromSeconds(6), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Titles without achievements (apps) answer with an error: quietly look again later, less often.
                if (++failures == 1) log?.Invoke("Achievements: " + ex.Message.Split('\n')[0]);
                if (failures > 3) wait = TimeSpan.FromMinutes(2);
            }
        }
    }
}
