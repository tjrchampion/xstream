using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using XStream.Core.Xbox;

namespace XStream.Core.Together;

public sealed record TogetherClientOptions
{
    /// <summary>The name the host sees ("Sam joined as Player 2").</summary>
    public string Name { get; init; } = "Friend";
    /// <summary>With no answer this long, offer a reply code for the host to paste.</summary>
    public TimeSpan ReplyAfter { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Give up joining after this long without an answer (the reply code needs time to be sent).</summary>
    public TimeSpan GiveUpAfter { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>Joined, then nothing from the host for this long: the stream is over.</summary>
    public TimeSpan LostGiveUpAfter { get; init; } = TimeSpan.FromSeconds(45);
    public bool Stun { get; init; } = true;
    /// <summary>Testing: drop this share of incoming datagrams (0-100), as if the download were lossy.</summary>
    public double SimulatedLossPct { get; init; }
}

public enum TogetherClientState { Connecting, NeedReply, Joined, Lost, Ended }

/// <summary>Why a guest's session ended.</summary>
public enum TogetherEnd { Left, HostEnded, Kicked, Full, WrongVersion, Unreachable, Lost }

/// <summary>Once-a-second numbers for the guest's stats readout and log.</summary>
public readonly record struct TogetherClientStats(
    double RttMs, double LossPct, double Kbps, long Recovered, long Missed, long FramesDropped, long KeyframeRequests,
    double HostToGuestP50Ms, double HostToGuestP99Ms, long FramesDelivered);

/// <summary>
/// The guest's end of Play together: reaches the host using the addresses in the invite (every one at once, the
/// first to answer wins), then receives the picture and sound and sends this controller back.
///
/// Everything network-side runs on one thread (receive, frame assembly, timers), so the frame assembler needs no
/// locks. <see cref="SendInput"/> may be called from the controller thread.
/// </summary>
public sealed class TogetherClient : IDisposable
{
    private readonly TogetherInvite _invite;
    private readonly TogetherClientOptions _o;
    private readonly Socket _socket;
    private readonly TogetherCipher _cipher;
    private readonly FrameAssembler _assembler = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Candidate> _targets;
    private readonly Random _loss = new();
    private readonly object _sendLock = new();
    private readonly byte[] _sendBuf = new byte[2048];
    private Thread? _thread;
    private volatile bool _running;
    private volatile IPEndPoint? _host;
    private uint _inputSeq;
    private double _startedMs, _lastHeardMs = double.NaN, _lastHelloMs = double.NegativeInfinity, _lastPingMs, _lastStatsMs;
    private readonly Dictionary<uint, double> _pings = new();
    private uint _nextPing;
    private byte[]? _stunTx; // STUN transaction in flight
    private List<Candidate> _own = [];
    private readonly double[] _lat = new double[600];
    private int _latN;

    public ulong ConnectionId => _cipher.ConnectionId;
    public TogetherClientState State { get; private set; } = TogetherClientState.Connecting;
    public WelcomeMsg? Welcome { get; private set; }
    public string? ReplyCode { get; private set; }
    public double RttMs { get; private set; } = double.NaN;
    public IPEndPoint? HostEndPoint => _host;
    public FrameAssembler Assembler => _assembler;
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public event Action<string>? Log;
    public event Action<TogetherClientState>? StateChanged;
    /// <summary>The host let us in (again, after a drop: the slot may differ).</summary>
    public event Action<WelcomeMsg>? Joined;
    /// <summary>A whole video frame, in order, ready to decode. Network thread.</summary>
    public event Action<AssembledFrame>? Frame;
    public event Action<byte[], ushort>? Audio;
    public event Action<RumbleMsg>? Rumble;
    /// <summary>No answer yet: send this to the host (they paste it under Play together).</summary>
    public event Action<string>? ReplyCodeReady;
    public event Action<TogetherEnd>? Ended;

    public TogetherClient(TogetherInvite invite, TogetherClientOptions options)
    {
        _invite = invite;
        _o = options;
        _targets = invite.Candidates.ToList();
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 1 << 21 };
        TogetherHost.IgnoreConnReset(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        ulong conn = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        _cipher = new TogetherCipher(invite.Key, invite.SessionId, conn, isHost: false);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _startedMs = NowMs;
        Log?.Invoke($"Play together: trying {string.Join(", ", _targets)}");
        _thread = new Thread(Loop) { IsBackground = true, Name = "vexel-together-guest", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (_o.Stun) _ = Task.Run(StunAsync);
    }

    /// <summary>This controller, sent the moment it changes (the input pump calls this). Any thread.</summary>
    public void SendInput(in GamepadState state)
    {
        if (State is not (TogetherClientState.Joined or TogetherClientState.Lost)) return;
        Span<byte> b = stackalloc byte[24];
        int n = TogetherMessages.Input(b, Interlocked.Increment(ref _inputSeq), state);
        Send(b[..n]);
    }

    /// <summary>Ask the host for a fresh keyframe (the decoder choked on something). Any thread.</summary>
    public void RequestKeyframe()
    {
        if (State != TogetherClientState.Joined) return;
        Span<byte> k = stackalloc byte[1];
        Send(k[..TogetherMessages.Simple(k, TogetherMsg.KeyframeRequest)]);
    }

    /// <summary>Leave the stream (the host is told, and Player N is freed straight away).</summary>
    public void Leave()
    {
        if (!_running) return;
        Span<byte> b = stackalloc byte[2];
        TogetherMessages.Bye(b, ByeReason.Left);
        for (int i = 0; i < 3; i++) Send(b);
        End(TogetherEnd.Left);
    }

    /// <summary>The last second's numbers.</summary>
    public TogetherClientStats Stats { get; private set; }

    private void Loop()
    {
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread(); // see TogetherHost.ReceiveLoop
        var buf = new byte[2048];
        var body = new byte[2048];
        var frames = new List<AssembledFrame>();
        var nacks = new List<NackEntry>();
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        long lastFrags = 0, lastMissed = 0, lastRecovered = 0, lastBytes = 0;
        double lastTick = 0, smoothKbps = double.NaN;
        while (_running)
        {
            try
            {
                if (_socket.Poll(2000, SelectMode.SelectRead))
                {
                    int n = _socket.ReceiveFrom(buf, ref from);
                    if (_o.SimulatedLossPct > 0 && _loss.NextDouble() * 100 < _o.SimulatedLossPct) continue;
                    OnPacket(buf.AsSpan(0, n), body, (IPEndPoint)from, frames, nacks);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            double now = NowMs;
            if (now - lastTick >= 4)
            {
                lastTick = now;
                _assembler.RttMs = double.IsNaN(RttMs) ? 30 : RttMs;
                nacks.Clear();
                _assembler.Tick(now, frames, nacks);
                if (nacks.Count > 0 && State is TogetherClientState.Joined) SendNacks(nacks);
                Deliver(frames);
                if (_assembler.TakeKeyframeRequest() && State is TogetherClientState.Joined)
                {
                    Span<byte> k = stackalloc byte[1];
                    Send(k[..TogetherMessages.Simple(k, TogetherMsg.KeyframeRequest)]);
                }
            }
            Timers(now);
            if (now - _lastStatsMs >= 1000)
            {
                double secs = (now - _lastStatsMs) / 1000;
                _lastStatsMs = now;
                long frags = _assembler.FragmentsReceived, missed = _assembler.FragmentsMissed, rec = _assembler.FragmentsRecovered, bytes = _assembler.BytesReceived;
                long dMissed = missed - lastMissed, firstTime = (frags - lastFrags) - (rec - lastRecovered) + dMissed;
                double loss = firstTime > 0 ? 100.0 * dMissed / firstTime : 0;
                // Smoothed: keyframes come every second or two, so a single second swings between bursts and nothing.
                double rawKbps = (bytes - lastBytes) * 8 / 1000.0 / Math.Max(0.001, secs);
                double kbps = smoothKbps = double.IsNaN(smoothKbps) ? rawKbps : smoothKbps * 0.6 + rawKbps * 0.4;
                lastFrags = frags; lastMissed = missed; lastRecovered = rec; lastBytes = bytes;
                double[] lat;
                lock (_lat) lat = _lat.Take(Math.Min(_latN, _lat.Length)).OrderBy(x => x).ToArray();
                Stats = new TogetherClientStats(RttMs, loss, kbps, rec, missed, _assembler.FramesDropped, _assembler.KeyframeRequests,
                    lat.Length > 0 ? lat[lat.Length / 2] : 0, lat.Length > 0 ? lat[(int)(lat.Length * 0.99)] : 0, _assembler.FramesDelivered);
                if (State == TogetherClientState.Joined)
                {
                    Span<byte> s = stackalloc byte[12];
                    Send(s[..TogetherMessages.Stats(s, new StatsMsg((float)loss, (float)kbps, (float)(double.IsNaN(RttMs) ? 0 : RttMs)))]);
                }
            }
        }
    }

    private void OnPacket(ReadOnlySpan<byte> pkt, byte[] bodyBuf, IPEndPoint src, List<AssembledFrame> frames, List<NackEntry> nacks)
    {
        if (NatTraversal.IsStun(pkt))
        {
            if (_stunTx is { } tx && NatTraversal.TryParseStunResponse(pkt, out var got, out var mapped) && got.SequenceEqual(tx) && mapped is not null)
            {
                _stunTx = null;
                lock (_own) if (!_own.Any(c => c.EndPoint.Equals(mapped))) _own.Insert(0, new Candidate(CandidateKind.Stun, mapped));
                Log?.Invoke($"STUN: our public address is {mapped}");
            }
            return;
        }
        if (!_cipher.Open(pkt, bodyBuf, out int n)) return;
        var body = bodyBuf.AsSpan(0, n);
        double now = NowMs;
        _lastHeardMs = now;
        if (!src.Equals(_host)) { if (_host is not null) Log?.Invoke($"Play together: host now at {src}"); _host = new IPEndPoint(src.Address, src.Port); }
        if (State == TogetherClientState.Lost) SetState(TogetherClientState.Joined);
        switch (TogetherMessages.TypeOf(body))
        {
            case TogetherMsg.Video when TogetherMessages.ReadVideo(body, out var f):
                if (State != TogetherClientState.Joined) break;
                nacks.Clear();
                _assembler.OnFragment(f, now, frames, nacks);
                if (nacks.Count > 0) SendNacks(nacks);
                Deliver(frames);
                break;
            case TogetherMsg.Audio when TogetherMessages.ReadAudio(body, out ushort seq, out var opus):
                if (State == TogetherClientState.Joined) Audio?.Invoke(opus.ToArray(), seq);
                break;
            case TogetherMsg.Welcome when TogetherMessages.ReadWelcome(body, out var w, out byte version):
                if (version != TogetherMessages.ProtocolVersion) { End(TogetherEnd.WrongVersion); break; }
                bool fresh = Welcome is not { } old || old.Slot != w.Slot || State != TogetherClientState.Joined;
                Welcome = w;
                if (!fresh) break;
                if (State is TogetherClientState.Connecting or TogetherClientState.NeedReply)
                    Log?.Invoke($"Play together: joined {w.HostName} as Player {w.Slot + 1} via {src} after {(now - _startedMs) / 1000:F1} s");
                _assembler.Reset();
                SetState(TogetherClientState.Joined);
                Joined?.Invoke(w);
                break;
            case TogetherMsg.Punch:
                // The host pasted our reply code and is sending to us: say Hello straight back to where it came from.
                if (State is TogetherClientState.Connecting or TogetherClientState.NeedReply) SendHello(src);
                break;
            case TogetherMsg.Rumble when TogetherMessages.ReadRumble(body, out var r): Rumble?.Invoke(r); break;
            case TogetherMsg.Ping when TogetherMessages.ReadPing(body, out uint id):
            {
                Span<byte> p = stackalloc byte[5];
                Send(p[..TogetherMessages.Ping(p, id, pong: true)]);
                break;
            }
            case TogetherMsg.Pong when TogetherMessages.ReadPing(body, out uint id):
                if (_pings.Remove(id, out double sent)) RttMs = double.IsNaN(RttMs) ? now - sent : RttMs * 0.75 + (now - sent) * 0.25;
                break;
            case TogetherMsg.Bye: End(TogetherMessages.ReadBye(body) == ByeReason.WrongVersion ? TogetherEnd.WrongVersion : TogetherEnd.HostEnded); break;
            case TogetherMsg.Kicked: End(TogetherEnd.Kicked); break;
            case TogetherMsg.Full: End(TogetherEnd.Full); break;
        }
    }

    private void SendNacks(List<NackEntry> nacks)
    {
        Span<byte> nb = stackalloc byte[2 + 180 * 6];
        var all = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(nacks);
        for (int at = 0; at < all.Length; at += 180)
            Send(nb[..TogetherMessages.Nack(nb, all[at..Math.Min(all.Length, at + 180)])]);
    }

    private void Deliver(List<AssembledFrame> frames)
    {
        if (frames.Count == 0) return;
        uint now = TogetherClock.Tenths;
        foreach (var f in frames)
        {
            lock (_lat) _lat[_latN++ % _lat.Length] = (int)(now - f.HostMs) / 10.0;
            Frame?.Invoke(f);
        }
        frames.Clear();
    }

    private void Timers(double now)
    {
        switch (State)
        {
            case TogetherClientState.Connecting or TogetherClientState.NeedReply:
                // Hello to every address four times a second: whichever answers first is the way in. Sending also opens
                // our own router for the host's answer (and for its Punch, if the reply code is needed).
                if (now - _lastHelloMs >= 250)
                {
                    _lastHelloMs = now;
                    foreach (var c in _targets) SendHello(c.EndPoint);
                }
                if (State == TogetherClientState.Connecting && now - _startedMs >= _o.ReplyAfter.TotalMilliseconds) OfferReply();
                if (now - _startedMs >= _o.GiveUpAfter.TotalMilliseconds) End(TogetherEnd.Unreachable);
                break;
            case TogetherClientState.Joined:
                if (now - _lastHeardMs > 3000) { SetState(TogetherClientState.Lost); Log?.Invoke("Play together: nothing from the host for 3 s"); }
                break;
            case TogetherClientState.Lost:
                // Keep knocking (the host may have timed us out: a Hello gets us back in).
                if (now - _lastHelloMs >= 500)
                {
                    _lastHelloMs = now;
                    if (_host is { } h) SendHello(h);
                    foreach (var c in _targets) SendHello(c.EndPoint);
                }
                if (now - _lastHeardMs > _o.LostGiveUpAfter.TotalMilliseconds) End(TogetherEnd.Lost);
                break;
        }
        if (State is TogetherClientState.Joined or TogetherClientState.Lost && now - _lastPingMs >= 500)
        {
            _lastPingMs = now;
            uint id = _nextPing++;
            _pings[id] = now;
            if (_pings.Count > 20) foreach (var k in _pings.Where(kv => now - kv.Value > 5000).Select(kv => kv.Key).ToList()) _pings.Remove(k);
            Span<byte> p = stackalloc byte[5];
            Send(p[..TogetherMessages.Ping(p, id, pong: false)]);
        }
    }

    private void OfferReply()
    {
        List<Candidate> own;
        lock (_own) own = [.. _own];
        int port = (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;
        foreach (var a in NatTraversal.LanAddresses().Take(3)) own.Add(new Candidate(CandidateKind.Lan, new IPEndPoint(a, port)));
        if (own.Count == 0) own.Add(new Candidate(CandidateKind.Loopback, new IPEndPoint(IPAddress.Loopback, port)));
        ReplyCode = new TogetherReply(ConnectionId, own).Encode();
        Log?.Invoke($"Play together: no answer after {_o.ReplyAfter.TotalSeconds:F0} s; reply code offered ({string.Join(", ", own)})");
        SetState(TogetherClientState.NeedReply);
        ReplyCodeReady?.Invoke(ReplyCode);
    }

    private void SendHello(IPEndPoint to)
    {
        Span<byte> b = stackalloc byte[80];
        int n = TogetherMessages.Hello(b, _invite.SessionId, _o.Name);
        SendTo(b[..n], to);
    }

    private void Send(ReadOnlySpan<byte> body)
    {
        if (_host is { } h) SendTo(body, h);
    }

    private void SendTo(ReadOnlySpan<byte> body, IPEndPoint to)
    {
        lock (_sendLock)
        {
            try
            {
                int n = _cipher.Seal(body, _sendBuf);
                _socket.SendTo(_sendBuf, 0, n, SocketFlags.None, to);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task StunAsync()
    {
        foreach (var (host, port) in NatTraversal.StunServers)
        {
            try
            {
                var ip = (await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(1.5))).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip is null) continue;
                var req = NatTraversal.StunRequest(out var tx);
                _stunTx = tx;
                for (int i = 0; i < 3 && _stunTx is not null; i++)
                {
                    lock (_sendLock) { try { _socket.SendTo(req, new IPEndPoint(ip, port)); } catch { } }
                    await Task.Delay(300);
                }
                if (_stunTx is null) return; // answered
            }
            catch { /* try the next server */ }
        }
    }

    private void SetState(TogetherClientState s)
    {
        if (State == s || State == TogetherClientState.Ended) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private void End(TogetherEnd why)
    {
        if (State == TogetherClientState.Ended) return;
        Log?.Invoke($"Play together: session over ({why}); {_assembler.FramesDelivered} frames shown, {_assembler.FragmentsRecovered} pieces recovered by resend, " +
                    $"{_assembler.FramesGivenUp} frames given up, {_assembler.KeyframeRequests} keyframes asked for");
        SetState(TogetherClientState.Ended);
        _running = false;
        Ended?.Invoke(why);
    }

    public void Dispose()
    {
        _running = false;
        if (_thread is { } t && t != Thread.CurrentThread) t.Join(500);
        try { _socket.Close(); } catch { }
        _cipher.Dispose();
    }
}
