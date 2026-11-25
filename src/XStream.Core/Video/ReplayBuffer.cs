using System.Diagnostics;

namespace XStream.Core.Video;

/// <summary>
/// Instant replay: keeps the last <see cref="Seconds"/> of the stream as it arrived (H.264 frames and Opus audio,
/// no re-encoding) so "Save last 30 seconds" can write them to an MP4 at once. A clip has to start on a keyframe,
/// and the console only sends one when asked, so <see cref="KeyframeDue"/> says when to ask (every
/// <see cref="KeyframeEverySeconds"/>); the buffer keeps everything from the last keyframe before the window.
/// Thread-safe: video comes from the decode thread, audio from the network thread.
/// </summary>
public sealed class ReplayBuffer(double seconds = 30, double keyframeEverySeconds = 10)
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly LinkedList<ClipFrame> _video = new();
    private readonly LinkedList<ClipAudio> _audio = new();
    private double _lastKeyMs = double.NegativeInfinity;
    private long _bytes;

    public double Seconds { get; } = seconds;
    public double KeyframeEverySeconds { get; } = keyframeEverySeconds;
    /// <summary>Bytes held now (for the log).</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }

    /// <summary>Time for a fresh keyframe (ask the console for one).</summary>
    public bool KeyframeDue { get { lock (_gate) return _clock.Elapsed.TotalMilliseconds - _lastKeyMs >= KeyframeEverySeconds * 1000; } }

    private double _lastAskMs = double.NegativeInfinity;

    /// <summary>True when a keyframe is due and none was asked for in the last 2 s (call from the frame loop).</summary>
    public bool ShouldAskKeyframe()
    {
        lock (_gate)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (now - _lastKeyMs < KeyframeEverySeconds * 1000 || now - _lastAskMs < 2000) return false;
            _lastAskMs = now;
            return true;
        }
    }

    /// <summary>Clock used for the timestamps (ms); exposed for tests.</summary>
    public Func<double>? Clock { get; init; }
    private double Now => Clock?.Invoke() ?? _clock.Elapsed.TotalMilliseconds;

    public void AddVideo(ReadOnlySpan<byte> annexB, bool keyframe)
    {
        if (annexB.IsEmpty) return;
        var f = new ClipFrame(annexB.ToArray(), Now, keyframe);
        lock (_gate)
        {
            if (_video.Count == 0 && !keyframe) return; // nothing usable until the first keyframe
            if (keyframe) _lastKeyMs = _clock.Elapsed.TotalMilliseconds;
            _video.AddLast(f);
            _bytes += f.AnnexB.Length;
            Trim(f.AtMs);
        }
    }

    public void AddAudio(byte[] opus)
    {
        if (opus.Length == 0) return;
        lock (_gate)
        {
            if (_video.Count == 0) return;
            _audio.AddLast(new ClipAudio(opus, Now));
            _bytes += opus.Length;
        }
    }

    /// <summary>Forget everything (the stream reconnected: the old frames don't lead into the new ones).</summary>
    public void Clear()
    {
        lock (_gate) { _video.Clear(); _audio.Clear(); _bytes = 0; _lastKeyMs = double.NegativeInfinity; }
    }

    // Drop whole keyframe-led groups that are entirely older than the window.
    private void Trim(double now)
    {
        double from = now - Seconds * 1000;
        while (true)
        {
            // The second keyframe in the buffer: if it's still at or before the window's start, the first group can go.
            var node = _video.First?.Next;
            while (node is not null && !node.Value.Keyframe) node = node.Next;
            if (node is null || node.Value.AtMs > from) break;
            while (_video.First != node) { _bytes -= _video.First!.Value.AnnexB.Length; _video.RemoveFirst(); }
        }
        double start = _video.First?.Value.AtMs ?? now;
        while (_audio.First is { } a && a.Value.AtMs < start) { _bytes -= a.Value.Opus.Length; _audio.RemoveFirst(); }
    }

    /// <summary>The buffered clip, times from its first frame. Empty if there's no keyframe yet.</summary>
    public (List<ClipFrame> Video, List<ClipAudio> Audio) Snapshot()
    {
        lock (_gate)
        {
            if (_video.First is not { } first) return ([], []);
            double t0 = first.Value.AtMs;
            return (_video.Select(f => f with { AtMs = f.AtMs - t0 }).ToList(),
                    _audio.Select(a => a with { AtMs = a.AtMs - t0 }).ToList());
        }
    }

    /// <summary>Write the buffer to an MP4 file. Returns the clip's length in seconds (0 if there was nothing to save).</summary>
    public double SaveTo(string path)
    {
        var (video, audio) = Snapshot();
        if (video.Count < 2) return 0;
        using (var f = File.Create(path)) Mp4Writer.Write(f, video, audio);
        return video[^1].AtMs / 1000.0;
    }
}
