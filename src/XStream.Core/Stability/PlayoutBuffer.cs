namespace XStream.Core.Stability;

/// <summary>
/// Holds decoded (or decodable) frames until their scheduled display time = sender time mapped
/// to local clock + target delay. Absorbs uneven arrival so frames are shown at an even pace.
/// Thread-safe: the network thread enqueues, the render thread dequeues.
/// </summary>
public sealed class PlayoutBuffer<T>
{
    private readonly object _lock = new();
    private readonly PriorityQueue<(T frame, double due), double> _queue = new();
    private readonly double _clockKhz;
    private double _offsetMs = double.NaN; // local arrival - sender time, minimum seen (fastest path)
    private double _offsetSetAtMs;

    public double TargetDelayMs { get; set; }
    public int LateFrames { get; private set; }
    public int Count { get { lock (_lock) return _queue.Count; } }

    public PlayoutBuffer(int clockRateHz = 90000) => _clockKhz = clockRateHz / 1000.0;

    public void Enqueue(T frame, uint rtpTimestamp, double arrivalMs)
    {
        double senderMs = rtpTimestamp / _clockKhz;
        double offset = arrivalMs - senderMs;
        lock (_lock)
        {
            // Track the fastest-path offset; let it drift up slowly so clock skew can't pin it.
            if (double.IsNaN(_offsetMs) || offset < _offsetMs) { _offsetMs = offset; _offsetSetAtMs = arrivalMs; }
            else if (arrivalMs - _offsetSetAtMs > 10_000) { _offsetMs += 1; _offsetSetAtMs = arrivalMs; }

            double due = senderMs + _offsetMs + TargetDelayMs;
            if (arrivalMs > due) { LateFrames++; due = arrivalMs; }
            _queue.Enqueue((frame, due), due);
        }
    }

    /// <summary>Returns the next frame whose display time has come, or false.</summary>
    public bool TryDequeue(double nowMs, out T frame)
    {
        lock (_lock)
        {
            if (_queue.TryPeek(out var item, out _) && item.due <= nowMs)
            {
                _queue.Dequeue();
                frame = item.frame;
                return true;
            }
        }
        frame = default!;
        return false;
    }

    /// <summary>Drop everything (e.g. on reconnect or a new keyframe after a freeze).</summary>
    public void Reset()
    {
        lock (_lock) { _queue.Clear(); _offsetMs = double.NaN; }
    }
}
