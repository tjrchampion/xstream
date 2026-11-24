#if XS_PATCHED_SIPSORCERY
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using SIPSorcery.Net;
using XStream.Net.Srtp;
using XStream.Net.Stun;

namespace XStream.WebRtc;

/// <summary>
/// Vexel's own media receive path: a dedicated high-priority thread reads the connection's UDP socket into one
/// reused buffer, decrypts SRTP in place (hardware AES, <see cref="SrtpContext"/>) and hands RTP straight to the
/// peer. Nothing is allocated per packet and there is no thread-pool hop, so packets reach the depacketizer sooner
/// and more evenly, and the garbage collector (whose pauses delay frames) has far less to do.
///
/// Only RTP media is handled here. Everything else on the socket (ICE/STUN, DTLS and the data channels, RTCP,
/// TURN-relayed traffic, unknown payload types) is copied and handed back to SIPSorcery exactly as before.
/// SIPSorcery's receiver-report bookkeeping is fed from here so the reports the console gets stay accurate.
/// </summary>
internal sealed class MediaReceiver : IDisposable
{
    /// <summary>Header fields and payload of one decrypted RTP packet (valid only during the call).</summary>
    public delegate void RtpHandler(bool audio, uint ssrc, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> payload);

    private readonly Socket _socket;
    private readonly UdpReceiver _sipReceiver;
    private readonly SrtpContext _srtp;
    private readonly RtpHandler _handler;
    private readonly HashSet<int> _audioPts, _videoPts;
    private readonly RTCPSession? _audioRtcp, _videoRtcp;
    private readonly MediaStreamTrack? _audioRemote, _videoRemote;
    private readonly IPEndPoint _endpointTemplate;
    private readonly Thread _thread;
    private volatile bool _running = true;

    // Round-trip probe: our own ICE connectivity checks on the nominated pair, timed when the console answers.
    private readonly RtpIceChannel _ice;
    private readonly ulong _tiebreaker;
    private readonly Thread _probeThread;
    private const int ProbeSlots = 8, ProbeIntervalMs = 250;
    private readonly byte[] _probeIds = new byte[ProbeSlots * StunBinding.TransactionIdLength];
    private readonly long[] _probeSentAt = new long[ProbeSlots];
    private readonly object _probeLock = new();
    private int _probeNext;

    /// <summary>Smoothed round trip to the console from STUN checks (NaN until the first answer).</summary>
    public double RttMs { get; private set; } = double.NaN;
    public double RttMinMs { get; private set; } = double.PositiveInfinity;
    public double RttMaxMs { get; private set; }
    public long RttProbes { get; private set; }
    public long RttAnswers { get; private set; }

    public long Packets { get; private set; }
    public long HandedBack { get; private set; }
    public SrtpContext Srtp => _srtp;
    /// <summary>Why the loop stopped and SIPSorcery took over again (null while running normally).</summary>
    public string? Stopped { get; private set; }
    public event Action<string>? Log;

    private MediaReceiver(Socket socket, UdpReceiver sipReceiver, SrtpContext srtp, RTCPeerConnection pc, RtpHandler handler)
    {
        _socket = socket;
        _sipReceiver = sipReceiver;
        _srtp = srtp;
        _handler = handler;
        _audioPts = PayloadTypes(pc.AudioStream, 111);
        _videoPts = PayloadTypes(pc.VideoStream, 102, 104, 106);
        _audioRtcp = pc.AudioStream?.RtcpSession; _videoRtcp = pc.VideoStream?.RtcpSession;
        _audioRemote = pc.AudioStream?.RemoteTrack; _videoRemote = pc.VideoStream?.RemoteTrack;
        _endpointTemplate = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        _thread = new Thread(Loop) { IsBackground = true, Name = "vexel-receive", Priority = ThreadPriority.Highest };
        _ice = pc.GetRtpChannel();
        // Same tiebreaker as SIPSorcery's own checks, so the console sees one consistent ICE agent.
        _tiebreaker = typeof(RtpIceChannel).GetField("_iceTiebreaker", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_ice) is ulong tb ? tb : (ulong)Random.Shared.NextInt64();
        _probeThread = new Thread(ProbeLoop) { IsBackground = true, Name = "vexel-rtt" };
    }

    /// <summary>
    /// Take over receiving on the connection's socket, once DTLS has produced the SRTP keys. Null (and nothing
    /// changes) if that isn't possible: not connected yet, keys not available, or an SRTP profile we don't support.
    /// </summary>
    public static MediaReceiver? TryStart(RTCPeerConnection pc, RtpHandler handler, out string why)
    {
        var channel = pc.GetRtpChannel();
        var socket = channel?.RtpSocket;
        var receiver = channel?.RtpReceiver;
        if (socket is null || receiver is null) { why = "no RTP socket"; return null; }
        if (!pc.TryGetRemoteSrtpMasterKey(out int profileId, out var key, out var salt)) { why = "SRTP keys not available"; return null; }
        var profile = (SrtpProfile)profileId;
        if (!Enum.IsDefined(profile) || key.Length != profile.KeyLength() || salt.Length != profile.SaltLength())
        { why = $"unsupported SRTP profile {profileId}"; return null; }

        var r = new MediaReceiver(socket, receiver, new SrtpContext(profile, key, salt), pc, handler);
        try { socket.ReceiveTimeout = 250; } catch { } // lets the loop notice Dispose when nothing arrives
        receiver.UseExternalReceiveLoop();
        r._thread.Start();
        r._probeThread.Start();
        why = $"own receive path ({profile.Name()}, hardware AES)";
        return r;
    }

    private static HashSet<int> PayloadTypes(MediaStream? stream, params int[] fallback)
    {
        var set = new HashSet<int>(fallback);
        foreach (var track in new[] { stream?.LocalTrack, stream?.RemoteTrack })
            if (track?.Capabilities is { } caps) foreach (var f in caps) set.Add(f.ID);
        return set;
    }

    private void Loop()
    {
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread(); // MMCSS "Games" on Windows
        var buf = new byte[65_536]; // any datagram fits: never truncated
        var from = new SocketAddress(_socket.AddressFamily);
        int consecutiveErrors = 0;
        while (_running)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(buf, SocketFlags.None, from);
                consecutiveErrors = 0;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { continue; }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize or SocketError.NetworkReset)
            {
                continue; // ICMP from an earlier send (port unreachable): the socket is fine
            }
            catch (ObjectDisposedException) { break; } // connection closed
            catch (SocketException e)
            {
                if (!_running || ++consecutiveErrors > 20) { Stop(e.SocketErrorCode.ToString(), handBack: _running); break; }
                continue;
            }
            if (n <= 0) continue;
            if (InOutage()) continue;

            try { Dispatch(buf, n, from); }
            catch (Exception ex) { Log?.Invoke("Receive path: dropped a packet (" + ex.Message + ")"); }
        }
    }

    private void Dispatch(byte[] buf, int n, SocketAddress from)
    {
        // RFC 7983: first byte 128-191 is RTP/RTCP; second byte 192-223 is RTCP (RFC 5761).
        if (n >= 12 && buf[0] is >= 128 and <= 191 && buf[1] is < 192 or > 223)
        {
            int pt = buf[1] & 0x7F;
            bool audio = _audioPts.Contains(pt);
            if (audio || _videoPts.Contains(pt))
            {
                if (!audio && TestDrop(BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2)))) return;
                var packet = buf.AsSpan(0, n);
                long authFailuresBefore = _srtp.AuthFailures;
                if (_srtp.UnprotectRtp(packet, out int len))
                {
                    Deliver(audio, buf, len);
                    return;
                }
                if (_srtp.AuthFailures == authFailuresBefore) return; // replay/duplicate: drop, as SIPSorcery would
                if (_srtp.AuthFailures > 200 && Packets < _srtp.AuthFailures)
                    Stop("most packets failed authentication with our SRTP", handBack: true);
                // Not ours to judge further: let SIPSorcery try (it logs and drops what it can't decrypt either).
            }
        }
        // RFC 7983: first byte 0-3 is STUN. Our own round-trip probes end here; everything else goes to SIPSorcery.
        if (buf[0] < 4 && StunBinding.IsBindingSuccess(buf.AsSpan(0, n), out var txId) && TakeProbe(txId)) return;
        HandBack(buf, n, from);
    }

    // XSTREAM_TEST_HICCUP="seconds,ms,percent" (testing): drop that share of incoming video packets for that long,
    // that many seconds after the receive path starts. Done before SRTP, so like real loss the console's resends
    // still get through (dropping after decryption made every resend look like a replay).
    private readonly (long from, long to, uint pct) _hiccup = ParseHiccup();
    private bool _hiccupLogged;

    private static (long, long, uint) ParseHiccup()
    {
        var parts = Environment.GetEnvironmentVariable("XSTREAM_TEST_HICCUP")?.Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parts is not { Length: 3 } || !double.TryParse(parts[0], inv, out var at) || !double.TryParse(parts[1], inv, out var ms)
            || !uint.TryParse(parts[2], out var pct)) return (long.MaxValue, long.MinValue, 0);
        long from = Stopwatch.GetTimestamp() + (long)(at * Stopwatch.Frequency);
        return (from, from + (long)(ms / 1000 * Stopwatch.Frequency), Math.Min(pct, 100));
    }

    // XSTREAM_TEST_OUTAGE="seconds,ms[,repeatSeconds]" (testing): drop EVERY incoming packet (video, audio, ICE, DTLS,
    // RTCP) for that long, like Wi-Fi dropping out, starting that many seconds in and repeating every repeatSeconds.
    // Incoming only: what we send still reaches the console.
    private readonly (double at, double ms, double every) _outage = ParseOutage();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private int _outageLogged = -1;

    private static (double, double, double) ParseOutage()
    {
        var parts = Environment.GetEnvironmentVariable("XSTREAM_TEST_OUTAGE")?.Split(',');
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (parts is not { Length: 2 or 3 } || !double.TryParse(parts[0], inv, out var at) || !double.TryParse(parts[1], inv, out var ms))
            return (double.NaN, 0, 0);
        double every = parts.Length == 3 && double.TryParse(parts[2], inv, out var e) ? e : 0;
        return (at, ms, every);
    }

    private bool InOutage()
    {
        if (double.IsNaN(_outage.at)) return false;
        double t = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds - _outage.at;
        if (t < 0) return false;
        int k = _outage.every > 0 ? (int)(t / _outage.every) : 0;
        if (_outage.every <= 0 && t * 1000 >= _outage.ms) return false;
        double into = t - k * (_outage.every > 0 ? _outage.every : 0);
        if (into * 1000 >= _outage.ms) return false;
        if (_outageLogged != k) { _outageLogged = k; Log?.Invoke($"Test mode: network outage now ({_outage.ms:F0} ms, all incoming packets)"); }
        return true;
    }

    private bool TestDrop(ushort seq)
    {
        if (_hiccup.pct == 0) return false;
        long now = Stopwatch.GetTimestamp();
        if (now < _hiccup.from || now >= _hiccup.to) return false;
        if (!_hiccupLogged) { _hiccupLogged = true; Log?.Invoke($"Test mode: dropping {_hiccup.pct}% of video packets now"); }
        return (uint)Random.Shared.Next(100) < _hiccup.pct; // per transmission, like real loss: a resend can get through
    }

    /// <summary>
    /// Sends an ICE connectivity check (STUN Binding Request) on the nominated pair every 250 ms and times the answer.
    /// The console's ICE agent has to answer these (they are also how a WebRTC peer confirms consent, RFC 7675), so
    /// this is the real network round trip: without it the only RTT signal was NACK -> resend, which on a clean
    /// wired link almost never happens, and everything timed off RTT used an assumed 30 ms.
    /// </summary>
    private void ProbeLoop()
    {
        var buf = new byte[512];
        var id = new byte[StunBinding.TransactionIdLength];
        while (_running)
        {
            Thread.Sleep(ProbeIntervalMs);
            try
            {
                var pair = _ice.NominatedEntry;
                // A TURN-relayed path needs each packet wrapped for the relay: leave those to the NACK estimate.
                if (pair?.RemoteCandidate?.DestinationEndPoint is not { } remote || pair.LocalCandidate?.type == RTCIceCandidateType.relay
                    || _ice.RemoteIceUser is not { } ruser || _ice.RemoteIcePassword is not { } rpwd) continue;
                RandomNumberGenerator.Fill(id);
                // SIPSorcery writes PRIORITY with BitConverter (host byte order); send the same bytes it does.
                uint prio = BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(pair.LocalPriority) : pair.LocalPriority;
                int len = StunBinding.BuildRequest(buf, id, $"{ruser}:{_ice.LocalIceUser}", rpwd, prio, _ice.IsController, _tiebreaker);
                lock (_probeLock)
                {
                    int slot = _probeNext++ % ProbeSlots;
                    id.CopyTo(_probeIds, slot * StunBinding.TransactionIdLength);
                    _probeSentAt[slot] = Stopwatch.GetTimestamp();
                }
                _socket.SendTo(buf, 0, len, SocketFlags.None, remote);
                RttProbes++;
            }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Log?.Invoke("Round-trip probe failed (" + ex.Message + ")"); Thread.Sleep(2_000); }
        }
    }

    private bool TakeProbe(ReadOnlySpan<byte> txId)
    {
        long now = Stopwatch.GetTimestamp();
        double sample;
        lock (_probeLock)
        {
            int slot = -1;
            for (int i = 0; i < ProbeSlots; i++)
                if (_probeSentAt[i] != 0 && txId.SequenceEqual(_probeIds.AsSpan(i * StunBinding.TransactionIdLength, StunBinding.TransactionIdLength)))
                { slot = i; break; }
            if (slot < 0) return false;
            sample = Stopwatch.GetElapsedTime(_probeSentAt[slot], now).TotalMilliseconds;
            _probeSentAt[slot] = 0;
        }
        RttAnswers++;
        RttMinMs = Math.Min(RttMinMs, sample);
        RttMaxMs = Math.Max(RttMaxMs, sample);
        RttMs = double.IsNaN(RttMs) ? sample : RttMs + (sample - RttMs) * 0.125; // TCP-style smoothing (RFC 6298)
        return true;
    }

    private void Deliver(bool audio, byte[] buf, int len)
    {
        var p = buf.AsSpan(0, len);
        int hlen = SrtpContext.RtpHeaderLength(p);
        int payloadLen = len - hlen;
        if ((p[0] & 0x20) != 0 && payloadLen > 0) payloadLen -= Math.Min(payloadLen, p[len - 1]); // padding
        if (payloadLen < 0) return;
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        bool marker = (p[1] & 0x80) != 0;
        Packets++;

        // Keep SIPSorcery's receiver reports (sent to the console) and its stream bookkeeping current.
        var remote = audio ? _audioRemote : _videoRemote;
        if (remote is not null && remote.Ssrc == 0) remote.Ssrc = ssrc; // lets incoming RTCP match the stream
        (audio ? _audioRtcp : _videoRtcp)?.RecordRtpPacketReceived(ssrc, seq, ts, p[1] & 0x7F, payloadLen, DateTime.Now);

        _handler(audio, ssrc, seq, ts, marker, p.Slice(hlen, payloadLen));
    }

    private void HandBack(byte[] buf, int n, SocketAddress from)
    {
        HandedBack++;
        var ep = (IPEndPoint)_endpointTemplate.Create(from);
        _sipReceiver.DeliverReceived(ep, buf.AsSpan(0, n).ToArray());
    }

    /// <summary>Stop our loop; with <paramref name="handBack"/>, SIPSorcery resumes receiving (the stream continues).</summary>
    private void Stop(string why, bool handBack)
    {
        if (!_running) return;
        _running = false;
        Stopped = why;
        if (handBack)
        {
            try { _socket.ReceiveTimeout = 0; } catch { }
            try { _sipReceiver.StopExternalReceiveLoop(); } catch { }
            Log?.Invoke($"Own receive path stopped ({why}); SIPSorcery's took over.");
        }
    }

    public void Dispose()
    {
        _running = false;
        if (Thread.CurrentThread != _thread) _thread.Join(500);
        _probeThread.Join(500);
        _srtp.Dispose();
    }
}
#endif
