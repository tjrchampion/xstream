namespace XStream.Core.Together;

/// <summary>A whole video frame put back together on the guest.</summary>
public sealed record AssembledFrame(uint FrameId, byte[] Data, bool Keyframe, uint RtpTimestamp, uint HostMs, double CompletedAtMs);

/// <summary>
/// Puts the host's video fragments back into frames, in order, and decides what to do about the missing ones.
///
/// The host sends each frame's pieces in order and frames in order, so a gap shows itself as soon as a later piece
/// arrives: a skipped index within a frame, the tail of the previous frame when the next one starts, or whole frame
/// ids skipped. Each missing piece is asked for again (NACK) once when the gap shows. If the frame is still incomplete
/// about one round trip later, what's still missing is asked for once more (twice for a keyframe): a resend costs one
/// more round trip, a fresh keyframe costs more (the console encodes it, the host passes one on per second, and at
/// 5% loss a big keyframe often loses a piece too). Only after that is the frame given up: a keyframe is requested
/// and frames are dropped until one arrives, so the decoder never sees a frame whose reference is missing (a short
/// freeze, not smeared garbage).
///
/// Single-threaded: the client's receive loop calls everything.
/// </summary>
public sealed class FrameAssembler
{
    private sealed class Pending
    {
        public ushort Count;               // 0: nothing of it has arrived yet (known missing)
        public byte[]?[] Parts = [];
        public int Have;
        public int MaxIndex = -1;
        public bool Key;
        public uint RtpTs, HostMs;
        public double FirstSeenMs, LastPieceMs;
        public double NackedAtMs = double.NaN; // when its first NACK went out (the give-up clock)
        public bool[] Nacked = [];
        public bool WholeNacked;
        public int Rounds;                 // NACK rounds sent for it
        public bool Complete => Count > 0 && Have == Count;
    }

    private readonly SortedDictionary<uint, Pending> _frames = new();
    private bool _started;
    private uint _next;      // the next frame id to hand to the decoder
    private uint _highest;   // highest frame id seen
    private bool _waitingForKeyframe = true; // until the first keyframe, and after giving up on a frame
    private double _lastKeyRequestMs = double.NegativeInfinity;
    private bool _keyRequestPending;

    /// <summary>The current round trip to the host (sets how long a retransmission gets before giving up).</summary>
    public double RttMs { get; set; } = 30;
    /// <summary>How long a NACKed frame gets: about one round trip, with room for the host's pacing.</summary>
    public double GiveUpMs => Math.Max(40, RttMs * 1.5 + 25);
    /// <summary>While waiting for a keyframe, ask again this often (the host only passes one a second to the console).</summary>
    public double KeyframeRepeatMs { get; set; } = 500;
    /// <summary>NACK rounds before a frame is given up (keyframes, and frames we know nothing of, get one more).</summary>
    public int NackRounds { get; set; } = 2;

    // Counters (whole session) for stats and the log.
    public long FragmentsReceived { get; private set; }
    public long FragmentsMissed { get; private set; }     // noticed missing (each once)
    public long FragmentsRecovered { get; private set; }  // arrived after being NACKed
    public long Duplicates { get; private set; }
    public long FramesDelivered { get; private set; }
    public long FramesDropped { get; private set; }
    public long FramesGivenUp { get; private set; }
    public long KeyframeRequests { get; private set; }
    public long NackRepeats { get; private set; }
    public long BytesReceived { get; private set; }
    public bool WaitingForKeyframe => _waitingForKeyframe;

    /// <summary>A keyframe should be asked for now (true once per request).</summary>
    public bool TakeKeyframeRequest() { bool k = _keyRequestPending; _keyRequestPending = false; return k; }

    /// <summary>Start again (the host restarted its stream, or the guest rejoined).</summary>
    public void Reset()
    {
        _frames.Clear();
        _started = false;
        _waitingForKeyframe = true;
        RequestKeyframe(double.NegativeInfinity, force: true);
    }

    public void OnFragment(in VideoFragment f, double nowMs, List<AssembledFrame> output, List<NackEntry> nacks)
    {
        FragmentsReceived++;
        BytesReceived += f.Payload.Length;
        if (!_started)
        {
            _started = true;
            _next = f.FrameId;
            _highest = f.FrameId;
            if (_waitingForKeyframe) RequestKeyframe(nowMs, force: true);
        }
        int ahead = (int)(f.FrameId - _highest);
        if (ahead > 600 || (int)(f.FrameId - _next) < -600)
        {
            // Far from anything we know (the host's stream restarted): begin again from here.
            _frames.Clear();
            _next = _highest = f.FrameId;
            _waitingForKeyframe = true;
            RequestKeyframe(nowMs, force: true);
            ahead = 0;
        }
        if ((int)(f.FrameId - _next) < 0) { Duplicates++; return; } // already delivered or dropped

        if (ahead > 0)
        {
            // The previous highest frame should be complete now: its tail was lost if not.
            if (_frames.TryGetValue(_highest, out var prev) && prev.Count > 0 && !prev.Complete)
                for (int i = prev.MaxIndex + 1; i < prev.Count; i++) NackOne(prev, _highest, (ushort)i, nowMs, nacks);
            // Frames skipped entirely: ask for the whole of each.
            for (uint id = _highest + 1; id != f.FrameId; id++)
            {
                if ((int)(id - _next) < 0 || _frames.ContainsKey(id)) continue;
                var gone = new Pending { FirstSeenMs = nowMs };
                _frames[id] = gone;
                NackWhole(gone, id, nowMs, nacks);
            }
            _highest = f.FrameId;
        }

        if (!_frames.TryGetValue(f.FrameId, out var p)) { p = new Pending { FirstSeenMs = nowMs }; _frames[f.FrameId] = p; }
        if (p.Count == 0)
        {
            bool wasNacked = p.WholeNacked;
            p.Count = f.Count; p.Parts = new byte[f.Count][]; p.Nacked = new bool[f.Count];
            if (wasNacked) Array.Fill(p.Nacked, true); // the host resends all of it: don't ask piece by piece too
        }
        if (f.Count != p.Count || f.Index >= p.Count) return; // inconsistent: ignore the piece
        if (p.Parts[f.Index] is not null) { Duplicates++; return; }
        p.Parts[f.Index] = f.Payload.ToArray();
        p.Have++;
        p.LastPieceMs = nowMs;
        p.Key |= f.Keyframe;
        p.RtpTs = f.RtpTimestamp; p.HostMs = f.HostMs;
        if (p.Nacked[f.Index] || p.WholeNacked) FragmentsRecovered++;
        if (f.Index > p.MaxIndex)
        {
            // Pieces skipped within this frame.
            for (int i = p.MaxIndex + 1; i < f.Index; i++) NackOne(p, f.FrameId, (ushort)i, nowMs, nacks);
            p.MaxIndex = f.Index;
        }
        Drain(nowMs, output);
    }

    /// <summary>Called often (every few ms): gives up on frames whose resend didn't come, and repeats keyframe requests.</summary>
    public void Tick(double nowMs, List<AssembledFrame> output, List<NackEntry>? nacks = null)
    {
        if (!_started) return;
        uint giveUpThrough = 0; bool giveUp = false;
        foreach (var (id, p) in _frames)
        {
            if (p.Complete || double.IsNaN(p.NackedAtMs)) continue;
            if (nowMs - p.NackedAtMs < GiveUpMs) continue;
            int allowed = p.Key || p.Count == 0 ? NackRounds + 1 : NackRounds;
            if (nacks is not null && p.Rounds < allowed)
            {
                // The resend didn't come (lost too): ask again for what's still missing.
                p.Rounds++;
                p.NackedAtMs = nowMs;
                if (p.Count == 0) nacks.Add(new NackEntry(id, NackEntry.WholeFrame));
                else for (int i = 0; i < p.Count; i++) if (p.Parts[i] is null) nacks.Add(new NackEntry(id, (ushort)i));
                NackRepeats++;
                continue;
            }
            giveUp = true; giveUpThrough = id;
        }
        // Not even NACKed but stuck (the last frame before a pause lost its tail, so nothing showed the gap): treat the same.
        if (!giveUp && _frames.TryGetValue(_next, out var head) && !head.Complete && nowMs - Math.Max(head.FirstSeenMs, head.LastPieceMs) > 250)
        {
            giveUp = true; giveUpThrough = _next;
        }
        if (giveUp)
        {
            // Everything up to the frame that didn't make it is lost to the decoder: drop it and wait for a keyframe.
            foreach (var id in _frames.Keys.Where(k => (int)(k - giveUpThrough) <= 0).ToList())
            {
                FramesDropped++;
                _frames.Remove(id);
            }
            FramesGivenUp++;
            _next = giveUpThrough + 1;
            _waitingForKeyframe = true;
            RequestKeyframe(nowMs, force: true);
        }
        if (_waitingForKeyframe) RequestKeyframe(nowMs, force: false);
        Drain(nowMs, output);
        // Nothing is kept for long: anything over 3 s old is gone for good.
        foreach (var id in _frames.Where(kv => nowMs - kv.Value.FirstSeenMs > 3000).Select(kv => kv.Key).ToList())
        {
            _frames.Remove(id);
            FramesDropped++;
        }
    }

    private void Drain(double nowMs, List<AssembledFrame> output)
    {
        if (_waitingForKeyframe)
        {
            // Jump straight to a complete keyframe if one is already here: everything before it is useless.
            uint? key = null;
            foreach (var (id, p) in _frames) if (p.Complete && p.Key) { key = id; break; }
            if (key is { } k)
            {
                foreach (var id in _frames.Keys.Where(x => (int)(x - k) < 0).ToList()) { _frames.Remove(id); FramesDropped++; }
                _next = k;
            }
        }
        while (_frames.TryGetValue(_next, out var p) && p.Complete)
        {
            _frames.Remove(_next);
            if (_waitingForKeyframe && !p.Key) { FramesDropped++; _next++; continue; }
            if (p.Key) _waitingForKeyframe = false;
            int len = 0;
            foreach (var part in p.Parts) len += part!.Length;
            var data = new byte[len];
            int o = 0;
            foreach (var part in p.Parts) { part!.CopyTo(data, o); o += part!.Length; }
            output.Add(new AssembledFrame(_next, data, p.Key, p.RtpTs, p.HostMs, nowMs));
            FramesDelivered++;
            _next++;
        }
        // (Complete frames further ahead stay until the drain reaches them: dropping them early would leave a hole the
        // drain could never step over once the keyframe in front of them completes.)
    }

    private void NackOne(Pending p, uint frameId, ushort index, double nowMs, List<NackEntry> nacks)
    {
        if (p.Parts[index] is not null || p.Nacked[index] || p.WholeNacked) return;
        p.Nacked[index] = true;
        FragmentsMissed++;
        if (double.IsNaN(p.NackedAtMs)) { p.NackedAtMs = nowMs; p.Rounds = 1; }
        nacks.Add(new NackEntry(frameId, index));
    }

    private void NackWhole(Pending p, uint frameId, double nowMs, List<NackEntry> nacks)
    {
        p.WholeNacked = true;
        FragmentsMissed++;
        p.NackedAtMs = nowMs;
        p.Rounds = 1;
        nacks.Add(new NackEntry(frameId, NackEntry.WholeFrame));
    }

    private void RequestKeyframe(double nowMs, bool force)
    {
        if (!force && nowMs - _lastKeyRequestMs < KeyframeRepeatMs) return;
        _lastKeyRequestMs = nowMs;
        _keyRequestPending = true;
        KeyframeRequests++;
    }
}
