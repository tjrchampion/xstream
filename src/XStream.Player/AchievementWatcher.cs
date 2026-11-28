using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// During a stream: looks at the running game's achievements about every 30 s (the cheap unlocked-only list) and
/// reports each one unlocked since the stream started. Best effort: a failed look is skipped, not retried at once.
/// </summary>
public sealed class AchievementWatcher(XboxSocial social, string locale)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    /// <summary>How often to look (shorter while achievement clips are on, so the moment is still in the replay buffer).</summary>
    public TimeSpan Every { get; set; } = Interval;

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
            wait = Every;
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

/// <summary>
/// Achievement clips: which part of the replay buffer shows the unlock. The service says when it unlocked (the
/// console's clock), and the watcher notices up to <see cref="AchievementWatcher.Every"/> later, so the moment is
/// placed on the buffer's clock from how long ago it was.
/// </summary>
public static class AchievementClip
{
    public const double BeforeMs = 20_000, AfterMs = 5_000;
    /// <summary>When the unlock time is missing or implausible (clocks far apart): assume it was this long ago.</summary>
    public const double UnknownAgeMs = 12_000;

    /// <summary>The clip's range on the buffer's clock, and how long to wait before saving so it includes the moments after.</summary>
    public static (double FromMs, double ToMs, int WaitMs) Window(double bufferNowMs, DateTimeOffset now, DateTimeOffset? unlockedAt)
    {
        double age = unlockedAt is { } at ? (now - at).TotalMilliseconds : double.NaN;
        if (double.IsNaN(age) || age < -5_000 || age > 120_000) age = UnknownAgeMs;
        age = Math.Max(0, age);
        double moment = bufferNowMs - age;
        double to = moment + AfterMs;
        return (moment - BeforeMs, to, (int)Math.Max(0, Math.Ceiling(to - bufferNowMs)));
    }

    /// <summary>"Vexel Achievement - Name 2026-11-26 20.15.03.mp4" in Videos › Vexel.</summary>
    public static string NewPath(string achievement)
    {
        string safe = string.Concat(achievement.Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-')).Trim();
        if (safe.Length > 60) safe = safe[..60].Trim();
        return Path.Combine(Clips.Folder, $"Vexel Achievement{(safe.Length > 0 ? " - " + safe : "")} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4");
    }
}
