namespace XStream.Core.Rtp;

/// <summary>
/// One measurement window of the incoming video stream. Produced by <see cref="RtpReceiveMonitor"/>.
/// All times are in milliseconds.
/// </summary>
public readonly record struct NetworkSample(
    double TimeMs,
    double LossFraction,      // 0..1 packets lost in this window (after reordering)
    double JitterMs,          // RFC 3550 interarrival jitter
    double QueueDelayMs,      // estimated queueing delay (one-way delay above the observed floor)
    double DelayTrendMs,      // change in queueing delay across this window (+ = queue building)
    double ReceiveKbps,       // measured incoming video bitrate
    double MsSinceLastFrame,  // time since the last complete frame arrived
    int CompleteFrames,
    int BrokenFrames,         // frames with missing packets
    bool KeyframeSeen,
    double RttMs);

/// <summary>
/// Transport-agnostic RTP video receive statistics. Feed it every RTP video packet
/// (sequence number, RTP timestamp, marker bit, payload size, arrival time) and call
/// <see cref="TakeSample"/> on a timer. Works from raw header fields so it can sit behind
/// any WebRTC stack (SIPSorcery today, something else tomorrow) and inside the simulator.
/// </summary>
public sealed class RtpReceiveMonitor
{
    private readonly double _clockRateKhz;

    // Sequence tracking (RFC 3550 style, extended to 32 bits)
    private bool _started;
    private long _highestExtSeq;
    private int _windowReceived;
    private readonly HashSet<long> _missing = new();

    // Jitter
    private double _jitterTs; // in RTP timestamp units
    private double _lastTransit = double.NaN;

    // Queue delay estimation: relative one-way delay = arrival - rtpTime; floor tracked over a window.
    private readonly Queue<(double t, double owd)> _owdHistory = new();
    private double _owdFloor = double.PositiveInfinity;
    private double _lastQueueDelay;
    private double _windowStartQueueDelay;
    private double _queueEwma;

    // Bitrate
    private long _windowBytes;
    private double _windowStartMs = double.NaN;

    // Frames
    private uint _currentFrameTs;
    private bool _frameOpen;
    private long _frameLastExtSeq;
    private bool _frameBroken;
    private bool _frameIsKey;
    private int _completeFrames, _brokenFrames;
    private bool _keySeen;
    private double _lastCompleteFrameMs = double.NaN;
    private double _firstPacketMs = double.NaN;

    public double RttMs { get; set; } = 30;
    public long TotalReceived { get; private set; }
    public long TotalLost { get; private set; }

    public RtpReceiveMonitor(int clockRateHz = 90000) => _clockRateKhz = clockRateHz / 1000.0;

    /// <summary>Called for each received RTP packet of the video stream.</summary>
    /// <param name="isKeyframeStart">True if the payload starts an IDR (see <see cref="H264.IsKeyframeStart"/>).</param>
    public void OnPacket(ushort seq, uint rtpTimestamp, bool marker, int payloadBytes, double arrivalMs, bool isKeyframeStart = false)
    {
        if (double.IsNaN(_windowStartMs)) _windowStartMs = arrivalMs;
        if (double.IsNaN(_firstPacketMs)) _firstPacketMs = arrivalMs;
        TotalReceived++;
        _windowBytes += payloadBytes + 12;
        _windowReceived++;

        long ext = Extend(seq);
        if (!_started)
        {
            _started = true;
            _highestExtSeq = ext;
        }
        else if (ext > _highestExtSeq)
        {
            for (long s = _highestExtSeq + 1; s < ext; s++) _missing.Add(s);
            _highestExtSeq = ext;
        }
        else
        {
            _missing.Remove(ext); // late / retransmitted packet repairs a gap
        }

        // Jitter (RFC 3550 6.4.1)
        double arrivalTs = arrivalMs * _clockRateKhz;
        double transit = arrivalTs - rtpTimestamp;
        if (!double.IsNaN(_lastTransit))
        {
            double d = Math.Abs(transit - _lastTransit);
            if (d < 10 * 90000) _jitterTs += (d - _jitterTs) / 16.0;
        }
        _lastTransit = transit;

        // One-way delay relative to sender clock (offset unknown, so only changes matter)
        double owd = arrivalMs - rtpTimestamp / _clockRateKhz;
        UpdateOwd(arrivalMs, owd);

        // Frame assembly. A packet from an older frame (retransmission arriving late) only repairs
        // sequence gaps above; it must not close the frame currently being assembled.
        if (_frameOpen && (int)(rtpTimestamp - _currentFrameTs) < 0) return;
        if (!_frameOpen || rtpTimestamp != _currentFrameTs)
        {
            if (_frameOpen) CloseFrame(arrivalMs, complete: false); // previous frame never saw its marker
            _frameOpen = true;
            _currentFrameTs = rtpTimestamp;
            _frameBroken = false;
            _frameIsKey = false;
            _frameLastExtSeq = ext;
        }
        else
        {
            if (ext != _frameLastExtSeq + 1) _frameBroken = true;
            _frameLastExtSeq = Math.Max(_frameLastExtSeq, ext);
        }
        if (isKeyframeStart) _frameIsKey = true;
        if (marker) CloseFrame(arrivalMs, complete: true);
    }

    private void CloseFrame(double nowMs, bool complete)
    {
        _frameOpen = false;
        if (complete && !_frameBroken)
        {
            _completeFrames++;
            _lastCompleteFrameMs = nowMs;
            if (_frameIsKey) _keySeen = true;
        }
        else _brokenFrames++;
    }

    private long Extend(ushort seq)
    {
        if (!_started) return seq + 65536L; // headroom so early wrap-backs stay positive
        long cycles = _highestExtSeq & ~0xFFFFL;
        long candidate = cycles | seq;
        if (candidate < _highestExtSeq - 32768) candidate += 65536;
        else if (candidate > _highestExtSeq + 32768) candidate -= 65536;
        return candidate;
    }

    private void UpdateOwd(double nowMs, double owd)
    {
        _owdHistory.Enqueue((nowMs, owd));
        while (_owdHistory.Count > 0 && nowMs - _owdHistory.Peek().t > 10_000) _owdHistory.Dequeue();
        // Recompute floor lazily; history is bounded (~10 s of packets).
        if (owd < _owdFloor) _owdFloor = owd;
        else if (_owdHistory.Count % 256 == 0) _owdFloor = _owdHistory.Min(x => x.owd);
        double q = Math.Max(0, owd - _owdFloor);
        _queueEwma += (q - _queueEwma) * 0.1;
        _lastQueueDelay = _queueEwma;
    }

    /// <summary>Close the current window and return its statistics.</summary>
    public NetworkSample TakeSample(double nowMs)
    {
        double windowMs = double.IsNaN(_windowStartMs) ? 0 : Math.Max(1, nowMs - _windowStartMs);

        // Packets still missing that are older than a reorder/NACK grace period count as lost.
        long graceSeq = _highestExtSeq - 2;
        int lost = 0;
        if (_missing.Count > 0)
        {
            var settled = _missing.Where(s => s <= graceSeq).ToList();
            lost = settled.Count;
            foreach (var s in settled) _missing.Remove(s);
        }
        TotalLost += lost;
        int expected = _windowReceived + lost;
        double loss = expected > 0 ? (double)lost / expected : 0;

        double kbps = windowMs > 0 ? _windowBytes * 8.0 / windowMs : 0; // bits/ms == kbps
        double sinceFrame = !_started ? 0
            : double.IsNaN(_lastCompleteFrameMs) ? nowMs - _firstPacketMs
            : nowMs - _lastCompleteFrameMs;

        var sample = new NetworkSample(
            nowMs, loss, _jitterTs / _clockRateKhz, _lastQueueDelay,
            _lastQueueDelay - _windowStartQueueDelay, kbps, sinceFrame,
            _completeFrames, _brokenFrames, _keySeen, RttMs);

        _windowStartMs = nowMs;
        _windowBytes = 0;
        _windowReceived = 0;
        _completeFrames = 0;
        _brokenFrames = 0;
        _keySeen = false;
        _windowStartQueueDelay = _lastQueueDelay;
        return sample;
    }
}

/// <summary>Minimal H.264 RTP payload inspection (RFC 6184) to spot IDR frames.</summary>
public static class H264
{
    public static bool IsKeyframeStart(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return false;
        int type = payload[0] & 0x1F;
        switch (type)
        {
            case 5: return true;                       // single NAL, IDR slice
            case 7: return true;                       // SPS usually precedes IDR
            case 24:                                   // STAP-A: walk aggregated NALs
                int i = 1;
                while (i + 2 < payload.Length)
                {
                    int size = (payload[i] << 8) | payload[i + 1];
                    int nal = payload[i + 2] & 0x1F;
                    if (nal == 5 || nal == 7) return true;
                    i += 2 + size;
                }
                return false;
            case 28:                                   // FU-A: start bit + inner type
                bool start = (payload[1] & 0x80) != 0;
                int inner = payload[1] & 0x1F;
                return start && inner == 5;
            default: return false;
        }
    }
}
