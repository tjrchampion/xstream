namespace XStream.Core.Stability;

/// <summary>
/// Holds decoded (or decodable) frames until their scheduled display time = sender time mapped
/// to local clock + target delay. Absorbs uneven arrival so frames are shown at an even pace.
/// Thread-safe: the network thread enqueues, the render thread dequeues.
///
/// Frames come out in the order they went in. They arrive in decode order, and H.264 frames depend on the ones
/// before them, so the order must never change. (It used to be a priority queue on due time: frames released
/// together after a resend all got due = now, and equal priorities came out shuffled, e.g. 1,4,3,2.)
/// Due times are kept from going backwards for the same reason.
/// </summary>
public sealed class PlayoutBuffer<T>
{
    private readonly object _lock = new();
    private readonly Queue<(T frame, double due)> _queue = new();
    private readonly double _clockKhz;
    private double _offsetMs = double.NaN; // local arrival - sender time, minimum seen (fastest path)
    private double _offsetSetAtMs;
    private double _lastDueMs = double.NegativeInfinity;

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
            due = Math.Max(due, _lastDueMs); // never ahead of the frame before it
            _lastDueMs = due;
            _queue.Enqueue((frame, due));
        }
    }

    /// <summary>Returns the next frame whose display time has come, or false.</summary>
    public bool TryDequeue(double nowMs, out T frame)
    {
        lock (_lock)
        {
            if (_queue.TryPeek(out var item) && item.due <= nowMs)
            {
                _queue.Dequeue();
                frame = item.frame;
                return true;
            }
        }
        frame = default!;
        return false;
    }

    /// <summary>When the next frame is due (NaN if the buffer is empty): lets the decoder sleep until then.</summary>
    public double NextDueMs
    {
        get { lock (_lock) return _queue.TryPeek(out var item) ? item.due : double.NaN; }
    }

    /// <summary>Drop everything (e.g. on reconnect or a new keyframe after a freeze).</summary>
    public void Reset()
    {
        lock (_lock) { _queue.Clear(); _offsetMs = double.NaN; _lastDueMs = double.NegativeInfinity; }
    }
}
