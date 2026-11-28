using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using XStream.Core.Xbox;

namespace XStream.Core.Together;

public sealed record TogetherHostOptions
{
    /// <summary>Shown to guests ("Joined Sam's stream").</summary>
    public string HostName { get; init; } = "Your friend";
    /// <summary>UDP port to use (0: any free one).</summary>
    public int Port { get; init; }
    public int MaxGuests { get; init; } = 3;
    /// <summary>Ask the router to forward the port (UPnP, NAT-PMP). Removed again on stop.</summary>
    public bool PortMapping { get; init; } = true;
    public bool Stun { get; init; } = true;
    /// <summary>Also offer 127.0.0.1 (testing two copies on one machine).</summary>
    public bool Loopback { get; init; }
    /// <summary>The console's bitrate while anyone is together (the host's upload carries every guest's picture).</summary>
    public double MaxKbps { get; init; } = 15_000;
    /// <summary>Lowest the cap goes when guests report loss.</summary>
    public double MinKbps { get; init; } = 5_000;
    /// <summary>Let guests press the Xbox button (it opens the guide over everyone's game).</summary>
    public bool AllowGuestXboxButton { get; init; }
    /// <summary>Testing: drop this share of outgoing datagrams (0-100), to prove recovery.</summary>
    public double SimulatedLossPct { get; init; }
    /// <summary>Time to wait for STUN and the router before the invite is made.</summary>
    public TimeSpan GatherTimeout { get; init; } = TimeSpan.FromSeconds(2.5);
}

/// <summary>A guest as the host sees it (for the panel, toasts and the log).</summary>
public sealed record TogetherGuestInfo(int Slot, string Name, double PingMs, double LossPct, double Kbps, string Address)
{
    /// <summary>"Player 2" (slots are controller indexes: 0 is the host's first controller).</summary>
    public string PlayerName => $"Player {Slot + 1}";
}

/// <summary>
/// The host's end of Play together: one UDP socket, up to three guests, each with a controller slot on the console.
/// Every access unit and audio packet the console sends is forwarded to each guest; the guest's controller comes back
/// and goes into its slot. There's no server: the invite carries the addresses to try, and the reply code covers the
/// routers that need both sides to send at once.
///
/// Thread model: a receive thread (guest packets), a sender thread (paced video, everything else first), and a 50 ms
/// housekeeping timer (pings, timeouts, punching, the bitrate cap). Send* can be called from any thread.
/// </summary>
public sealed class TogetherHost : IDisposable
{
    private sealed class Guest
    {
        public required ulong ConnId;
        public required TogetherCipher Cipher;
        public required IPEndPoint EndPoint;
        public string Name = "";
        public int Slot = -1;
        public bool Joined;
        public double LastHeardMs, LastFromEndPointMs, JoinedMs;
        public uint LastInputSeq;
        public bool AnyInput;
        public double RttMs = double.NaN;
        public float LossPct, Kbps;
        public int HighLossSeconds, LowLossSeconds;
        public readonly Dictionary<uint, double> Pings = new();
        public uint NextPing;
        public long BytesSent, BytesSentWindow;
        public GamepadState LastInput;
    }

    private sealed record Punching(TogetherCipher Cipher, IReadOnlyList<Candidate> Targets, double UntilMs);

    private readonly TogetherHostOptions _o;
    private readonly Func<int, bool> _slotTakenLocally;
    private readonly Socket _socket;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _lock = new();
    private readonly Dictionary<ulong, Guest> _guests = new();
    private readonly Dictionary<ulong, Punching> _punching = new();
    private readonly ConcurrentQueue<(Guest g, byte[] body, int len, bool paced)> _urgent = new();
    private readonly ConcurrentQueue<(Guest g, byte[] body, int len)> _video = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TokenBucket _bucket = new(5_000_000, 16 * 1024);
    private readonly Random _loss = new();
    private readonly List<NatTraversal.PortMapping> _mappings = new();
    private readonly Dictionary<string, TaskCompletionSource<IPEndPoint>> _stunWaits = new();
    private Thread? _recv, _send;
    private Timer? _timer;
    private volatile bool _running;
    private TogetherInvite? _invite;
    private string _game = "", _art = "";

    // Video: frame ids and a short cache of what was sent (for NACKs).
    private uint _nextFrameId = 1;
    private readonly Dictionary<uint, (byte[][] parts, double at)> _cache = new();
    private readonly Queue<uint> _cacheOrder = new();
    public const double CacheMs = 500;

    // Keyframes are passed on to the console at most once a second, however many guests ask.
    private double _lastKeyframeMs = double.NegativeInfinity;
    private double? _capKbps;
    private double _lastCapCheckMs;

    public event Action<string>? Log;
    public event Action<TogetherGuestInfo>? GuestJoined;
    public event Action<TogetherGuestInfo, string>? GuestLeft;
    /// <summary>A guest's controller (already in its slot's index, Xbox button removed unless allowed). Receive thread.</summary>
    public event Action<int, GamepadState>? GuestInput;
    /// <summary>A guest needs a keyframe (at most once a second): ask the console for one.</summary>
    public event Action? KeyframeNeeded;
    /// <summary>The console's bitrate should be at most this (kbps), or back to normal (null).</summary>
    public event Action<double?>? CapChanged;
    /// <summary>The invite is ready (addresses gathered).</summary>
    public event Action<string>? InviteReady;
    /// <summary>Once a second while anyone is in, and whenever someone joins or leaves (refresh the panel).</summary>
    public event Action? Changed;

    public bool AllowGuestXboxButton { get; set; }
    public int Port => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;
    public string? Invite { get; private set; }
    public IReadOnlyList<Candidate> Candidates => _invite?.Candidates ?? [];
    public double? CapKbps => _capKbps;
    public long FragmentsSent { get; private set; }
    public long FragmentsResent { get; private set; }
    public long FramesSent { get; private set; }
    public long KeyframesAsked { get; private set; }
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <param name="slotTakenLocally">Whether a controller slot is used by one of the host's own pads.</param>
    public TogetherHost(TogetherHostOptions options, Func<int, bool> slotTakenLocally)
    {
        _o = options;
        _slotTakenLocally = slotTakenLocally;
        AllowGuestXboxButton = options.AllowGuestXboxButton;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.ReceiveBufferSize = 1 << 20;
        _socket.SendBufferSize = 1 << 21;
        IgnoreConnReset(_socket);
        try { _socket.Bind(new IPEndPoint(IPAddress.Any, options.Port)); }
        catch (SocketException) when (options.Port != 0) { _socket.Bind(new IPEndPoint(IPAddress.Any, 0)); }
    }

    /// <summary>Windows reports an ICMP "port unreachable" as an error on the next receive; for UDP that must be ignored.</summary>
    internal static void IgnoreConnReset(Socket s)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { s.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null); } catch { }
    }

    /// <summary>Open for guests and start gathering addresses (the invite follows in a second or two).</summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _recv = new Thread(ReceiveLoop) { IsBackground = true, Name = "vexel-together-recv", Priority = ThreadPriority.AboveNormal };
        _send = new Thread(SendLoop) { IsBackground = true, Name = "vexel-together-send", Priority = ThreadPriority.AboveNormal };
        _recv.Start(); _send.Start();
        _timer = new Timer(_ => Housekeeping(), null, 50, 50);
        _ = Task.Run(GatherAsync);
    }

    /// <summary>The game now running (sent to guests as they join).</summary>
    public void SetGame(string? name, string? artUrl) { _game = name ?? ""; _art = artUrl ?? ""; }

    private async Task GatherAsync()
    {
        int port = Port;
        var cands = new List<Candidate>();
        var gathering = new List<Task<Candidate?>>();
        if (_o.Stun) gathering.Add(StunAsync());
        if (_o.PortMapping)
        {
            gathering.Add(MapAsync(NatTraversal.UpnpMapAsync(port, _o.GatherTimeout, m => Log?.Invoke(m)), CandidateKind.Upnp));
            gathering.Add(MapAsync(NatTraversal.NatPmpMapAsync(port, _o.GatherTimeout, m => Log?.Invoke(m)), CandidateKind.NatPmp));
        }
        try { await Task.WhenAll(gathering).WaitAsync(_o.GatherTimeout + TimeSpan.FromSeconds(1)); } catch { }
        foreach (var t in gathering)
            if (t.IsCompletedSuccessfully && t.Result is { } c && !cands.Any(x => x.EndPoint.Equals(c.EndPoint))) cands.Add(c);
        // Router mappings first (they work from anywhere), then the public address, then the local network.
        cands = cands.OrderBy(c => c.Kind switch { CandidateKind.Upnp => 0, CandidateKind.NatPmp => 1, _ => 2 }).ToList();
        foreach (var a in NatTraversal.LanAddresses().Take(3)) cands.Add(new Candidate(CandidateKind.Lan, new IPEndPoint(a, port)));
        if (_o.Loopback || cands.Count == 0) cands.Add(new Candidate(CandidateKind.Loopback, new IPEndPoint(IPAddress.Loopback, port)));
        _invite = TogetherInvite.New(cands);
        Invite = _invite.Encode();
        Log?.Invoke($"Play together: open on UDP port {port}; addresses in the invite: {string.Join(", ", cands)}");
        if (_running) InviteReady?.Invoke(Invite);
    }

    private async Task<Candidate?> MapAsync(Task<NatTraversal.PortMapping?> mapping, CandidateKind kind)
    {
        var m = await mapping;
        if (m is null) return null;
        lock (_mappings) _mappings.Add(m);
        if (!_running) { await m.RemoveAsync(); return null; }
        return new Candidate(kind, m.External);
    }

    /// <summary>Our public address as a STUN server sees this socket (the same mapping guests will reach).</summary>
    private async Task<Candidate?> StunAsync()
    {
        var results = new List<Task<IPEndPoint>>();
        foreach (var (host, port) in NatTraversal.StunServers)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(1.5));
                var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip is null) continue;
                var req = NatTraversal.StunRequest(out var tx);
                var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_stunWaits) _stunWaits[Convert.ToHexString(tx)] = tcs;
                for (int i = 0; i < 3; i++) { try { _socket.SendTo(req, new IPEndPoint(ip, port)); } catch { } }
                results.Add(tcs.Task);
            }
            catch { /* this server isn't reachable */ }
        }
        if (results.Count == 0) { Log?.Invoke("STUN: no server reachable"); return null; }
        try
        {
            var first = await Task.WhenAny(results).WaitAsync(TimeSpan.FromSeconds(2));
            var ep = await first;
            Log?.Invoke($"STUN: public address {ep}");
            return new Candidate(CandidateKind.Stun, ep);
        }
        catch { Log?.Invoke("STUN: no answer"); return null; }
    }

    // ---- From the console -----------------------------------------------------------------------------

    /// <summary>One access unit from the console (Annex B). Any thread; copies what it needs and returns at once.</summary>
    public void SendVideo(ReadOnlySpan<byte> accessUnit, bool keyframe, uint rtpTimestamp)
    {
        Guest[] targets;
        lock (_lock) targets = _guests.Values.Where(g => g.Joined).ToArray();
        if (targets.Length == 0 || accessUnit.Length == 0) return;
        uint id = _nextFrameId++;
        uint hostMs = TogetherClock.Tenths;
        int count = (accessUnit.Length + TogetherMessages.MaxFragmentPayload - 1) / TogetherMessages.MaxFragmentPayload;
        if (count > ushort.MaxValue) return;
        var parts = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            var chunk = accessUnit.Slice(i * TogetherMessages.MaxFragmentPayload, Math.Min(TogetherMessages.MaxFragmentPayload, accessUnit.Length - i * TogetherMessages.MaxFragmentPayload));
            var body = new byte[TogetherMessages.VideoHeaderSize + chunk.Length];
            TogetherMessages.VideoHeader(body, id, (ushort)i, (ushort)count, keyframe, rtpTimestamp, hostMs);
            chunk.CopyTo(body.AsSpan(TogetherMessages.VideoHeaderSize));
            parts[i] = body;
        }
        double now = NowMs;
        lock (_cache)
        {
            _cache[id] = (parts, now);
            _cacheOrder.Enqueue(id);
            while (_cacheOrder.TryPeek(out var old) && now - _cache[old].at > CacheMs) _cache.Remove(_cacheOrder.Dequeue());
        }
        // Interleave guests piece by piece so each gets the frame at the same pace.
        foreach (var part in parts)
            foreach (var g in targets) _video.Enqueue((g, part, part.Length));
        FramesSent++;
        _wake.Set();
    }

    /// <summary>One Opus packet from the console. Any thread.</summary>
    public void SendAudio(ReadOnlySpan<byte> opus, ushort seq)
    {
        Guest[] targets;
        lock (_lock) targets = _guests.Values.Where(g => g.Joined).ToArray();
        if (targets.Length == 0 || opus.Length > 1400) return;
        var body = new byte[3 + opus.Length];
        int n = TogetherMessages.Audio(body, seq, opus);
        foreach (var g in targets) _urgent.Enqueue((g, body, n, true));
        _wake.Set();
    }

    /// <summary>Rumble the console sent for a guest's slot. False if no guest is in it.</summary>
    public bool SendRumble(int slot, in RumbleMsg r)
    {
        Guest? g;
        lock (_lock) g = _guests.Values.FirstOrDefault(x => x.Joined && x.Slot == slot);
        if (g is null) return false;
        var body = new byte[8];
        int n = TogetherMessages.Rumble(body, r);
        _urgent.Enqueue((g, body, n, false));
        _wake.Set();
        return true;
    }

    public bool IsGuestSlot(int slot) { lock (_lock) return _guests.Values.Any(g => g.Joined && g.Slot == slot); }

    public IReadOnlyList<TogetherGuestInfo> Guests
    {
        get { lock (_lock) return _guests.Values.Where(g => g.Joined).OrderBy(g => g.Slot).Select(Info).ToList(); }
    }

    private static TogetherGuestInfo Info(Guest g) => new(g.Slot, g.Name, g.RttMs, g.LossPct, g.Kbps, g.EndPoint.ToString());

    /// <summary>Remove a guest ("Remove" in the panel). They're told, and their controller is let go.</summary>
    public void Kick(int slot)
    {
        Guest? g;
        lock (_lock) g = _guests.Values.FirstOrDefault(x => x.Joined && x.Slot == slot);
        if (g is null) return;
        var body = new byte[1];
        TogetherMessages.Simple(body, TogetherMsg.Kicked);
        for (int i = 0; i < 3; i++) SendNow(g, body, 1);
        Remove(g, "removed by the host");
    }

    /// <summary>The guest's reply code: start sending to its addresses so both routers open. False (with why) if it isn't one.</summary>
    public bool AcceptReply(string text, out string problem)
    {
        var reply = TogetherReply.TryParse(text, out problem);
        if (reply is null || _invite is null) { if (_invite is null) problem = "The invite isn't ready yet."; return false; }
        lock (_lock)
        {
            if (_guests.TryGetValue(reply.ConnectionId, out var g) && g.Joined) { problem = $"{g.Name} is already in."; return false; }
            var cipher = new TogetherCipher(_invite.Key, _invite.SessionId, reply.ConnectionId, isHost: true);
            _punching[reply.ConnectionId] = new Punching(cipher, reply.Candidates, NowMs + 20_000);
        }
        Log?.Invoke($"Play together: reply code accepted; reaching the guest at {string.Join(", ", reply.Candidates)}");
        return true;
    }

    // ---- Receiving -----------------------------------------------------------------------------------

    private void ReceiveLoop()
    {
        // Interactive priority (MMCSS on Windows, user-interactive QoS on macOS): macOS otherwise slows a process that
        // isn't in front, and NACKs, pings and guests' controllers would wait.
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread();
        var buf = new byte[2048];
        var body = new byte[2048];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            // Poll first: closing a socket doesn't reliably wake a thread blocked in ReceiveFrom (it hung on macOS).
            try { if (!_socket.Poll(20_000, SelectMode.SelectRead)) continue; n = _socket.ReceiveFrom(buf, ref from); }
            catch (SocketException) { if (!_running) return; continue; }
            catch (ObjectDisposedException) { return; }
            var src = (IPEndPoint)from;
            var pkt = buf.AsSpan(0, n);
            if (NatTraversal.IsStun(pkt))
            {
                if (NatTraversal.TryParseStunResponse(pkt, out var tx, out var mapped) && mapped is not null)
                {
                    TaskCompletionSource<IPEndPoint>? w;
                    lock (_stunWaits) { _stunWaits.Remove(Convert.ToHexString(tx), out w); }
                    w?.TrySetResult(mapped);
                }
                continue;
            }
            if (!TogetherCipher.TryPeek(pkt, out ulong conn, out bool fromHost) || fromHost) continue;
            try { Handle(conn, pkt, body, new IPEndPoint(src.Address, src.Port)); }
            catch (Exception ex) { Log?.Invoke("Play together: bad packet: " + ex.Message); }
        }
    }

    private void Handle(ulong conn, ReadOnlySpan<byte> pkt, byte[] bodyBuf, IPEndPoint src)
    {
        Guest? g;
        lock (_lock) _guests.TryGetValue(conn, out g);
        if (g is null)
        {
            // Unknown connection: only a Hello made with this session's key starts one.
            var inv = _invite;
            if (inv is null) return;
            TogetherCipher? cipher;
            lock (_lock) cipher = _punching.Remove(conn, out var p) ? p.Cipher : null;
            cipher ??= new TogetherCipher(inv.Key, inv.SessionId, conn, isHost: true);
            if (!cipher.Open(pkt, bodyBuf, out int len) || !TogetherMessages.ReadHello(bodyBuf.AsSpan(0, len), out var hello))
            {
                cipher.Dispose();
                return;
            }
            g = new Guest { ConnId = conn, Cipher = cipher, EndPoint = src, LastHeardMs = NowMs, LastFromEndPointMs = NowMs };
            lock (_lock) _guests[conn] = g;
            OnHello(g, hello, src);
            return;
        }
        if (!g.Cipher.Open(pkt, bodyBuf, out int n)) return;
        var body = bodyBuf.AsSpan(0, n);
        double now = NowMs;
        g.LastHeardMs = now;
        var type = TogetherMessages.TypeOf(body);
        // Follow the guest to a new address (its router changed the mapping), but don't flip between two working
        // paths while Hellos are still arriving on both.
        if (!g.EndPoint.Equals(src) && (type != TogetherMsg.Hello || now - g.LastFromEndPointMs > 2000)) g.EndPoint = src;
        if (g.EndPoint.Equals(src)) g.LastFromEndPointMs = now;
        switch (type)
        {
            case TogetherMsg.Hello when TogetherMessages.ReadHello(body, out var hello): OnHello(g, hello, src); break;
            case TogetherMsg.Input when g.Joined && TogetherMessages.ReadInput(body, out uint seq, out var st):
                // Unordered over UDP: only newer states count (the guest sends one at least every 8 ms anyway).
                if (g.AnyInput && (int)(seq - g.LastInputSeq) <= 0) break;
                g.AnyInput = true; g.LastInputSeq = seq;
                var b = st.Buttons;
                if (!AllowGuestXboxButton) b &= ~GamepadButtons.Nexus;
                var placed = st with { Index = (byte)g.Slot, Buttons = b };
                g.LastInput = placed;
                GuestInput?.Invoke(g.Slot, placed);
                break;
            case TogetherMsg.Nack when g.Joined: OnNack(g, body); break;
            case TogetherMsg.KeyframeRequest when g.Joined: AskKeyframe("guest " + g.Name); break;
            case TogetherMsg.Ping when TogetherMessages.ReadPing(body, out uint pid):
            {
                var pong = new byte[5];
                TogetherMessages.Ping(pong, pid, pong: true);
                _urgent.Enqueue((g, pong, 5, false)); _wake.Set();
                break;
            }
            case TogetherMsg.Pong when TogetherMessages.ReadPing(body, out uint pid):
                lock (g.Pings) if (g.Pings.Remove(pid, out double sent))
                {
                    double rtt = now - sent;
                    g.RttMs = double.IsNaN(g.RttMs) ? rtt : g.RttMs * 0.7 + rtt * 0.3;
                }
                break;
            case TogetherMsg.Stats when TogetherMessages.ReadStats(body, out var s): g.LossPct = s.LossPct; g.Kbps = s.Kbps; break;
            case TogetherMsg.Bye: Remove(g, "left"); break;
        }
    }

    private void OnHello(Guest g, in HelloMsg hello, IPEndPoint src)
    {
        if (hello.Version != TogetherMessages.ProtocolVersion)
        {
            var bye = new byte[2];
            TogetherMessages.Bye(bye, ByeReason.WrongVersion);
            SendNow(g, bye, 2);
            Log?.Invoke($"Play together: {hello.Name} has a different version of Vexel (protocol {hello.Version}, ours {TogetherMessages.ProtocolVersion})");
            lock (_lock) _guests.Remove(g.ConnId);
            g.Cipher.Dispose();
            return;
        }
        if (_invite is null || hello.SessionId != _invite.SessionId) return;
        bool isNew = false;
        lock (_lock)
        {
            if (!g.Joined)
            {
                int slot = FreeSlot();
                if (_guests.Values.Count(x => x.Joined) >= _o.MaxGuests || slot < 0)
                {
                    var full = new byte[1];
                    TogetherMessages.Simple(full, TogetherMsg.Full);
                    SendNow(g, full, 1);
                    _guests.Remove(g.ConnId);
                    g.Cipher.Dispose();
                    Log?.Invoke($"Play together: {Clean(hello.Name)} couldn't join: every controller slot is taken");
                    return;
                }
                g.Slot = slot;
                g.Name = Clean(hello.Name);
                g.Joined = true;
                g.JoinedMs = NowMs;
                isNew = true;
            }
        }
        // Welcome again on every Hello: the first one may have been lost.
        var w = new byte[TogetherMessages.MaxBody];
        int n = TogetherMessages.Welcome(w, new WelcomeMsg((byte)g.Slot, _o.HostName, _game, _art));
        _urgent.Enqueue((g, w, n, false)); _wake.Set();
        if (!isNew) return;
        Log?.Invoke($"Play together: {g.Name} joined as Player {g.Slot + 1} from {src}");
        if (_capKbps is null) SetCap(_o.MaxKbps);
        AskKeyframe("new guest", force: true);
        GuestJoined?.Invoke(Info(g));
        Changed?.Invoke();
    }

    /// <summary>The lowest controller slot not used by the host's own pads or another guest. Guests start at Player 2.</summary>
    private int FreeSlot()
    {
        for (int i = 1; i < 4; i++)
            if (!_slotTakenLocally(i) && !_guests.Values.Any(x => x.Joined && x.Slot == i)) return i;
        return -1;
    }

    private static string Clean(string name)
    {
        var s = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return s.Length == 0 ? "Friend" : s.Length > 24 ? s[..24] : s;
    }

    private void OnNack(Guest g, ReadOnlySpan<byte> body)
    {
        Span<NackEntry> entries = stackalloc NackEntry[180];
        int n = TogetherMessages.ReadNack(body, entries);
        for (int i = 0; i < n; i++)
        {
            byte[][]? parts;
            lock (_cache) parts = _cache.TryGetValue(entries[i].FrameId, out var e) ? e.parts : null;
            if (parts is null) continue; // too old: the guest will give up on it and ask for a keyframe
            if (entries[i].Index == NackEntry.WholeFrame)
                foreach (var p in parts) { _urgent.Enqueue((g, p, p.Length, true)); FragmentsResent++; }
            else if (entries[i].Index < parts.Length)
            {
                var p = parts[entries[i].Index];
                _urgent.Enqueue((g, p, p.Length, true));
                FragmentsResent++;
            }
        }
        _wake.Set();
    }

    private void AskKeyframe(string why, bool force = false)
    {
        double now = NowMs;
        lock (_lock)
        {
            if (!force && now - _lastKeyframeMs < 1000) return;
            _lastKeyframeMs = now;
        }
        KeyframesAsked++;
        KeyframeNeeded?.Invoke();
    }

    private void Remove(Guest g, string why)
    {
        bool was;
        lock (_lock)
        {
            was = g.Joined;
            g.Joined = false;
            _guests.Remove(g.ConnId);
        }
        g.Cipher.Dispose();
        if (!was) return;
        Log?.Invoke($"Play together: {g.Name} (Player {g.Slot + 1}) {why}");
        GuestLeft?.Invoke(Info(g), why);
        bool none;
        lock (_lock) none = !_guests.Values.Any(x => x.Joined);
        if (none) SetCap(null);
        Changed?.Invoke();
    }

    private void SetCap(double? kbps)
    {
        if (_capKbps == kbps) return;
        _capKbps = kbps;
        Log?.Invoke(kbps is { } k ? $"Play together: console bitrate capped at {k / 1000:F1} Mbps" : "Play together: console bitrate back to normal");
        CapChanged?.Invoke(kbps);
    }

    // ---- Sending ------------------------------------------------------------------------------------

    private readonly byte[] _sealBuf = new byte[2048];

    private void SendLoop()
    {
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread();
        while (_running)
        {
            bool sent = false;
            while (_urgent.TryDequeue(out var u))
            {
                if (u.paced) _bucket.Force(u.len + TogetherCipher.Overhead, NowMs);
                SendNow(u.g, u.body, u.len);
                sent = true;
            }
            // Video waits for tokens: spread over a few ms, never one big burst.
            int guests;
            lock (_lock) guests = Math.Max(1, _guests.Values.Count(x => x.Joined));
            double capMbps = (_capKbps ?? _o.MaxKbps) / 1000;
            _bucket.RateBytesPerSecond = Math.Max(30, capMbps * 2.5) * 1e6 / 8 * guests;
            _bucket.BurstBytes = 16 * 1024 * guests;
            if (_video.Count > 6000)
            {
                // Over a second behind (the upload can't keep up): drop it all and start from a keyframe.
                _video.Clear();
                Log?.Invoke("Play together: the upload fell behind; skipping ahead to a fresh picture");
                AskKeyframe("backlog", force: true);
            }
            while (_video.TryPeek(out var v))
            {
                int size = v.len + TogetherCipher.Overhead;
                if (!_bucket.TryTake(size, NowMs)) break;
                _video.TryDequeue(out _);
                if (v.g.Joined) { SendNow(v.g, v.body, v.len); FragmentsSent++; }
                sent = true;
                if (!_urgent.IsEmpty) break;
            }
            if (sent && !_urgent.IsEmpty) continue;
            if (_video.TryPeek(out var next))
            {
                double wait = _bucket.WaitMs(next.len + TogetherCipher.Overhead, NowMs);
                if (wait >= 1.5) _wake.WaitOne(1);
                else if (wait > 0) Thread.SpinWait(200);
            }
            else _wake.WaitOne(5);
        }
    }

    private void SendNow(Guest g, byte[] body, int len)
    {
        lock (_sealBuf)
        {
            int n;
            try { n = g.Cipher.Seal(body.AsSpan(0, len), _sealBuf); }
            catch (ObjectDisposedException) { return; } // the guest just left
            g.BytesSent += n;
            if (_o.SimulatedLossPct > 0 && _loss.NextDouble() * 100 < _o.SimulatedLossPct) return;
            try { _socket.SendTo(_sealBuf, 0, n, SocketFlags.None, g.EndPoint); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private void SendTo(TogetherCipher cipher, ReadOnlySpan<byte> body, IPEndPoint to)
    {
        lock (_sealBuf)
        {
            int n;
            try { n = cipher.Seal(body, _sealBuf); } catch (ObjectDisposedException) { return; }
            try { _socket.SendTo(_sealBuf, 0, n, SocketFlags.None, to); } catch (SocketException) { } catch (ObjectDisposedException) { }
        }
    }

    // ---- Housekeeping (every 50 ms) -------------------------------------------------------------------

    private int _tick;

    private void Housekeeping()
    {
        if (!_running) return;
        try
        {
            double now = NowMs;
            _tick++;
            Guest[] all;
            lock (_lock) all = _guests.Values.ToArray();
            foreach (var g in all)
            {
                if (now - g.LastHeardMs > 10_000) { Remove(g, g.Joined ? "timed out (no word for 10 s)" : "never joined"); continue; }
                if (!g.Joined) continue;
                if (_tick % 20 == 0)
                {
                    // A ping a second: the round trip shown in the panel, and keeps the routers' mappings open.
                    uint id = g.NextPing++;
                    lock (g.Pings) { g.Pings[id] = now; foreach (var old in g.Pings.Where(kv => now - kv.Value > 5000).Select(kv => kv.Key).ToList()) g.Pings.Remove(old); }
                    var ping = new byte[5];
                    TogetherMessages.Ping(ping, id, pong: false);
                    _urgent.Enqueue((g, ping, 5, false));
                }
            }
            // Reply codes: send to every address the guest gave, ten times a second, until it says Hello.
            if (_tick % 2 == 0)
            {
                KeyValuePair<ulong, Punching>[] punch;
                lock (_lock) punch = _punching.ToArray();
                var body = new byte[1];
                TogetherMessages.Simple(body, TogetherMsg.Punch);
                foreach (var (conn, p) in punch)
                {
                    if (now > p.UntilMs) { lock (_lock) _punching.Remove(conn); Log?.Invoke("Play together: the guest from the reply code never answered"); continue; }
                    foreach (var c in p.Targets) SendTo(p.Cipher, body, c.EndPoint);
                }
            }
            if (_tick % 20 == 0) AdjustCap(all.Where(g => g.Joined).ToArray());
            if (_tick % 20 == 0 && all.Any(g => g.Joined)) Changed?.Invoke();
            _wake.Set();
        }
        catch (Exception ex) { Log?.Invoke("Play together: " + ex.Message); }
    }

    /// <summary>Lower the console's bitrate while a guest keeps losing packets (their download or our upload is full); creep back up when it's clean.</summary>
    private void AdjustCap(Guest[] joined)
    {
        if (joined.Length == 0 || _capKbps is not { } cap) return;
        bool lossy = false, clean = true;
        foreach (var g in joined)
        {
            if (NowMs - g.JoinedMs < 3000) { clean = false; continue; } // joining: a keyframe burst isn't a trend
            if (g.LossPct > 2) g.HighLossSeconds++; else g.HighLossSeconds = 0;
            if (g.LossPct < 0.5) g.LowLossSeconds++; else g.LowLossSeconds = 0;
            lossy |= g.HighLossSeconds >= 3;
            clean &= g.LowLossSeconds >= 10;
        }
        if (lossy)
        {
            foreach (var g in joined) g.HighLossSeconds = 0;
            double next = Math.Max(_o.MinKbps, Math.Round(cap * 0.8 / 500) * 500);
            if (next < cap) SetCap(next);
        }
        else if (clean && cap < _o.MaxKbps)
        {
            foreach (var g in joined) g.LowLossSeconds = 0;
            SetCap(Math.Min(_o.MaxKbps, Math.Round(cap * 1.1 / 500) * 500));
        }
    }

    // ---- Stop -----------------------------------------------------------------------------------------

    /// <summary>Tell every guest the stream ended, close the port on the router, close the socket.</summary>
    public void Stop()
    {
        if (!_running) return;
        Guest[] all;
        lock (_lock) all = _guests.Values.Where(g => g.Joined).ToArray();
        var bye = new byte[2];
        TogetherMessages.Bye(bye, ByeReason.HostEnded);
        for (int i = 0; i < 3; i++) foreach (var g in all) SendNow(g, bye, 2);
        _running = false;
        _timer?.Dispose();
        _wake.Set();
        foreach (var g in all) Remove(g, "left: the host stopped sharing");
        _recv?.Join(500); _send?.Join(500);
        try { _socket.Close(); } catch { }
        NatTraversal.PortMapping[] maps;
        lock (_mappings) { maps = _mappings.ToArray(); _mappings.Clear(); }
        if (maps.Length > 0)
            try { Task.WhenAll(maps.Select(m => m.RemoveAsync())).Wait(TimeSpan.FromSeconds(2)); Log?.Invoke("Play together: router port mapping removed"); } catch { }
        lock (_lock) { foreach (var p in _punching.Values) p.Cipher.Dispose(); _punching.Clear(); }
        SetCap(null);
    }

    public void Dispose() { Stop(); _socket.Dispose(); }
}
