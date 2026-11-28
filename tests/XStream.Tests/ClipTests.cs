using XStream.Core.Video;
using XStream.Player;

/// <summary>Achievement clips: placing the unlock on the replay buffer's clock, and cutting that part out.</summary>
static class ClipTests
{
    private static void Eq<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}");
    }
    private static void True(bool c, string what) { if (!c) throw new Exception("expected true: " + what); }

    public static IEnumerable<(string, Action)> All =>
    [
        ("Achievement clip: the unlock 10 s ago is placed 10 s back, with 20 s before and 5 s after", () =>
        {
            var now = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
            var (from, to, wait) = AchievementClip.Window(100_000, now, now.AddSeconds(-10));
            Eq(70_000.0, from); Eq(95_000.0, to); Eq(0, wait);
        }),
        ("Achievement clip: waits for the moments after a fresh unlock", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var (_, to, wait) = AchievementClip.Window(50_000, now, now.AddSeconds(-2));
            Eq(53_000.0, to); Eq(3000, wait);
        }),
        ("Achievement clip: a missing or far-off unlock time falls back to a typical delay", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var a = AchievementClip.Window(80_000, now, null);
            var b = AchievementClip.Window(80_000, now, now.AddHours(-3));   // console clock far out
            var c = AchievementClip.Window(80_000, now, now.AddMinutes(2));  // in the future
            Eq(80_000 - AchievementClip.UnknownAgeMs - AchievementClip.BeforeMs, a.FromMs);
            Eq(a, b); Eq(a, c);
        }),
        ("Replay range starts on the keyframe before the moment and stops at its end", () =>
        {
            double t = 0;
            var rb = new ReplayBuffer(120) { Clock = () => t };
            for (int i = 0; i < 60 * 60; i++) // 60 s at 60 fps, a keyframe every 10 s
            {
                t = i * (1000.0 / 60);
                rb.AddVideo([0, 0, 1, (byte)(i % 600 == 0 ? 0x65 : 0x41)], i % 600 == 0);
                if (i % 3 == 0) rb.AddAudio([1, 2]);
            }
            var (video, audio) = rb.Range(25_000, 40_000);
            True(video[0].Keyframe, "starts on a keyframe");
            Eq(0.0, video[0].AtMs);
            // keyframe at 20 s, so 20..40 s
            True(Math.Abs(video[^1].AtMs - 20_000) < 20, $"ends at the window's end ({video[^1].AtMs})");
            True(audio.Count > 0 && audio[^1].AtMs <= video[^1].AtMs && audio[0].AtMs >= 0, "audio inside the video's span");
            var early = rb.Range(-5_000, 3_000);
            True(early.Video.Count > 0 && early.Video[0].Keyframe, "before the buffer: from its first keyframe");
            Eq(0, rb.Range(-20_000, -10_000).Video.Count);
        }),
    ];
}
