using XStream.Core.Stability;

namespace XStream.Core.Rtp;

/// <summary>
/// How lost packets and frames are recovered. The defaults are the current behaviour; <see cref="Legacy"/> is what
/// Vexel 0.4.0 did, kept for A/B comparisons (XSTREAM_RECOVERY=legacy live, and the packet simulator).
/// </summary>
public sealed record RecoveryOptions
{
    /// <summary>Ask for a keyframe the moment a frame is given up on (else: at the next 100 ms engine tick).</summary>
    public bool FastKeyframe { get; init; } = true;
    /// <summary>Stop asking for resends of packets whose frame was already given up on.</summary>
    public bool ForgetAbandoned { get; init; } = true;
    /// <summary>Shortest wait before asking for a resend again (0.4.0: 20 ms).</summary>
    public double NackMinRetryMs { get; init; } = 6;
    /// <summary>
    /// How long a frame waits for its resends: until every NACK try has had its round trip (plus margin), at least
    /// this long. 0.4.0 waited 2 x RTT + 20 ms, at least 60 ms, however quick the link.
    /// </summary>
    public double MinWaitMs { get; init; } = 40;
    public bool LegacyWait { get; init; }
    /// <summary>
    /// Double the wait before each further NACK try (see <see cref="NackGenerator.Backoff"/>). Off: in the packet
    /// simulator it only helped long bursts, and made random loss and short bursts freeze 2-3x longer (a packet
    /// needing a 3rd or 4th try holds every later frame up for longer).
    /// </summary>
    public bool NackBackoff { get; init; }
    /// <summary>Tries per lost packet (0.4.0: 3). The 4th try (~30 ms in on a home link) gets many packets past a short burst.</summary>
    public int NackMaxTries { get; init; } = 4;
    /// <summary>
    /// With NACK on, tell the engine a frame was broken only when the frame assembler actually lost it. The monitor
    /// counts every frame that arrived with a gap, including those a resend repaired: on 1% loss that's a third of
    /// all frames, and "3 broken frames" asked for a keyframe (300 KB at 1440p) several times a second and raised
    /// the playout buffer.
    /// </summary>
    public bool CountOnlyLostFrames { get; init; } = true;
    /// <summary>Hold repeat keyframe requests while packets are being lost (see <see cref="KeyframeRecovery.Lossy"/>).</summary>
    public bool HoldKeyframeInLoss { get; init; } = true;
    /// <summary>Hold the first request too (see <see cref="KeyframeRecovery.HoldFirst"/>; off by default).</summary>
    public bool HoldFirstKeyframe { get; init; }
    public double MaxKeyframeHoldMs { get; init; } = 100;

    public static RecoveryOptions Legacy { get; } = new()
    {
        FastKeyframe = false, ForgetAbandoned = false, NackMinRetryMs = 20, LegacyWait = true, NackMaxTries = 3, CountOnlyLostFrames = false,
        HoldKeyframeInLoss = false,
    };

    /// <summary>From XSTREAM_RECOVERY ("legacy" = the 0.4.0 behaviour); the defaults otherwise.</summary>
    public static RecoveryOptions FromEnvironment() =>
        Environment.GetEnvironmentVariable("XSTREAM_RECOVERY") is "legacy" ? Legacy : new RecoveryOptions();
}

/// <summary>
/// The video half of the receive path, independent of the WebRTC library: every RTP video packet goes through
/// the monitor (network measurements), the NACK generator (ask for lost packets) and the frame assembler, and
/// complete frames go into the playout buffer. Sends NACKs and asks for keyframes through callbacks, so the same
/// code runs against the console and inside the packet simulator. Thread-safe: packets, the 10 ms tick and the
/// 100 ms sample may come from different threads.
/// </summary>
public sealed class VideoReceiver
{
    private readonly object _lock = new();
    private readonly StabilityEngine _engine;
    private bool _pictureLost; // since the last Sample: tells the engine's tick too (its retry rules use it)
    private long _lostAtLastSample;

    public VideoReceiver(StabilityEngine engine, RecoveryOptions? options = null)
    {
        _engine = engine;
        Options = options ?? new RecoveryOptions();
        Nacks = new NackGenerator { MinRetryMs = Options.NackMinRetryMs, Backoff = Options.NackBackoff, MaxTries = Options.NackMaxTries };
        if (Options.HoldKeyframeInLoss) engine.HoldKeyframesWhile(Loss.IsLossy, Options.HoldFirstKeyframe, Options.MaxKeyframeHoldMs);
    }

    /// <summary>Packet loss over the last few tens of milliseconds (decides when a keyframe request is worth sending).</summary>
    public RecentLoss Loss { get; } = new();

    public RecoveryOptions Options { get; }
    public RtpReceiveMonitor Monitor { get; } = new();
    public H264Depacketizer Depacketizer { get; } = new();
    public NackGenerator Nacks { get; }
    public PlayoutBuffer<AccessUnit> Frames { get; } = new();

    /// <summary>Ask for lost packets (the console answered the SDP with NACK support).</summary>
    public bool NackEnabled { get; set; }
    public uint LocalSsrc { get; set; } = 1;
    public uint RemoteSsrc { get; private set; }
    /// <summary>Keyframes asked for straight from the receive path (not by the engine's tick).</summary>
    public long FastKeyframeRequests { get; private set; }

    /// <summary>Send one raw RTCP packet (a NACK) to the console.</summary>
    public Action<byte[]>? SendRtcp { get; set; }
    /// <summary>Ask the console for a keyframe now.</summary>
    public Action? KeyframeNeeded { get; set; }
    /// <summary>The real decoder's errors (it couldn't decode a frame), checked every tick.</summary>
    public Func<bool>? DecoderErrorSource { get; set; }
    /// <summary>A complete frame was queued in <see cref="Frames"/> (wake the decoder).</summary>
    public event Action? FrameQueued;

    /// <summary>One RTP video packet (header fields and payload; the payload is only read during the call).</summary>
    public void OnPacket(uint ssrc, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> payload, double now)
    {
        byte[]? nack = null;
        bool keyframe;
        int queued = 0;
        lock (_lock)
        {
            RemoteSsrc = ssrc;
            Monitor.OnPacket(seq, timestamp, marker, payload.Length, now, H264.IsKeyframeStart(payload));
            Nacks.OnPacket(seq, now);
            Loss.OnPacket(seq, now);
            foreach (var au in Depacketizer.Push(seq, timestamp, marker, payload, now)) { Frames.Enqueue(au, au.RtpTimestamp, now); queued++; }
            keyframe = AfterDrain(now, decoderFailed: false);
            // Ask for a lost packet as soon as its reorder wait is over (packets arrive every ~0.3 ms), not at the next
            // 10 ms tick: the resend, and the frame waiting on it, arrive up to 10 ms sooner.
            if (NackEnabled && Nacks.HasGaps) nack = BuildNack(Nacks.Poll(now, Monitor.RttMs));
        }
        Act(queued, nack, keyframe);
    }

    /// <summary>Every ~10 ms: give up on frames that can't be completed in time, ask for lost packets.</summary>
    public void FastTick(double now)
    {
        byte[]? nack = null;
        bool keyframe;
        int queued = 0;
        bool decoderFailed = DecoderErrorSource?.Invoke() == true;
        lock (_lock)
        {
            foreach (var au in Depacketizer.Tick(now)) { Frames.Enqueue(au, au.RtpTimestamp, now); queued++; }
            keyframe = AfterDrain(now, decoderFailed);
            if (Options.HoldKeyframeInLoss && _engine.PollHeldKeyframe(now)) keyframe = true;
            if (NackEnabled) nack = BuildNack(Nacks.Poll(now, Monitor.RttMs));
        }
        Act(queued, nack, keyframe);
    }

    /// <summary>
    /// The 100 ms measurement for the engine: network sample, picture counters, and whether the picture broke since
    /// the last one. Also retunes how long frames wait for resends.
    /// </summary>
    /// <param name="measuredRttMs">Round trip from our own STUN checks (NaN: use the NACK -> resend estimate).</param>
    public (NetworkSample sample, PictureTick picture, bool pictureLost) Sample(double now, double measuredRttMs)
    {
        lock (_lock)
        {
            if (!double.IsNaN(measuredRttMs)) Monitor.RttMs = measuredRttMs;
            else if (!double.IsNaN(Nacks.RttEstimateMs)) Monitor.RttMs = Nacks.RttEstimateMs;
            var sample = Monitor.TakeSample(now);
            // A keyframe the frame assembler completed counts even if the monitor saw it broken (it was repaired by a
            // resend): otherwise recovery kept asking for another every 0.5 s.
            if (Depacketizer.TakeKeyframeDelivered()) sample = sample with { KeyframeSeen = true };
            if (Options.CountOnlyLostFrames && NackEnabled)
            {
                sample = sample with { BrokenFrames = (int)(Depacketizer.FramesLost - _lostAtLastSample) };
                _lostAtLastSample = Depacketizer.FramesLost;
            }
            bool lost = _pictureLost | Depacketizer.TakeDecoderError();
            _pictureLost = false;
            double since = double.IsNaN(Depacketizer.LastDeliveredMs) ? sample.MsSinceLastFrame : now - Depacketizer.LastDeliveredMs;
            var pic = new PictureTick(Depacketizer.FramesDelivered, Depacketizer.FramesLost, Depacketizer.PictureBroken, since,
                Nacks.Requested, Nacks.Recovered);
            Depacketizer.MaxWaitMs = WaitForResendsMs(Monitor.RttMs);
            return (sample, pic, lost);
        }
    }

    private double WaitForResendsMs(double rttMs)
    {
        if (Options.LegacyWait) return Math.Clamp(rttMs * 2 + 20, 60, 250);
        // Without NACK nothing is resent: only reordering is worth waiting for.
        if (!NackEnabled) return Options.MinWaitMs;
        return Math.Clamp(Nacks.GiveUpAfterMs(rttMs) + 10, Options.MinWaitMs, 250);
    }

    // After the frame assembler ran (under the lock): pass on what it gave up on; true = ask for a keyframe now.
    private bool AfterDrain(double now, bool decoderFailed)
    {
        if (Options.ForgetAbandoned && Depacketizer.TakeAbandoned(out ushort through)) Nacks.ForgetThrough(through);
        bool lost = decoderFailed;
        if (Options.FastKeyframe && Depacketizer.TakeDecoderError()) lost = true;
        if (!lost) return false;
        _pictureLost = true;
        return Options.FastKeyframe && _engine.OnPictureLost(now, Monitor.RttMs);
    }

    private byte[]? BuildNack(IReadOnlyList<ushort> lost) =>
        lost.Count > 0 && RemoteSsrc != 0 ? NackGenerator.BuildNack(LocalSsrc, RemoteSsrc, lost) : null;

    // Outside the lock: these call into the WebRTC library and the decoder thread.
    private void Act(int queued, byte[]? nack, bool keyframe)
    {
        if (nack is not null) SendRtcp?.Invoke(nack);
        if (keyframe) { FastKeyframeRequests++; KeyframeNeeded?.Invoke(); }
        for (int i = 0; i < queued; i++) FrameQueued?.Invoke();
    }
}

/// <summary>
/// Loss over the last ~40 ms, from sequence numbers as packets first arrive (resends don't count). Read from other
/// threads without the receiver's lock (the engine asks while holding its own), so the state is a few plain fields.
/// </summary>
public sealed class RecentLoss
{
    private const double SliceMs = 10;
    private const int Slices = 4;
    private readonly int[] _expected = new int[Slices], _received = new int[Slices];
    private readonly long[] _slice = new long[Slices];
    private int _highest = -1;
    private double _lastPacketMs = double.NaN;

    /// <summary>Over this fraction lost in the window counts as lossy.</summary>
    public double Threshold { get; init; } = 0.10;
    /// <summary>Nothing at all for this long counts as lossy too (a burst so heavy nothing gets through).</summary>
    public double SilenceMs { get; init; } = 25;

    public void OnPacket(ushort seq, double now)
    {
        _lastPacketMs = now;
        if (_highest < 0) { _highest = seq; return; }
        int diff = (short)(seq - (ushort)_highest);
        if (diff <= 0) return; // late or resent
        _highest = seq;
        long slice = (long)(now / SliceMs);
        int i = (int)(slice % Slices);
        if (_slice[i] != slice) { _slice[i] = slice; _expected[i] = 0; _received[i] = 0; }
        _expected[i] += diff;
        _received[i]++;
    }

    public bool IsLossy(double now)
    {
        if (double.IsNaN(_lastPacketMs)) return false;
        if (now - _lastPacketMs > SilenceMs) return true;
        long slice = (long)(now / SliceMs);
        int expected = 0, received = 0;
        for (int i = 0; i < Slices; i++)
            if (slice - _slice[i] < Slices) { expected += _expected[i]; received += _received[i]; }
        return expected >= 8 && expected - received > expected * Threshold;
    }
}
