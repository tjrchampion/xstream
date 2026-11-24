using System.Diagnostics;
using System.Net;
using System.Text;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using XStream.Core.Input;
using XStream.Core.Rtp;
using XStream.Core.Stability;
using XStream.Core.Xbox;

namespace XStream.WebRtc;

/// <summary>
/// WebRTC connection to an Xbox console using SIPSorcery (pure C#, runs on macOS and Windows),
/// with the stability engine wired into the receive path:
///   RTP video packets -> NackGenerator (ask for lost packets) + H264Depacketizer (our own frame
///   assembly, so retransmitted packets repair frames) + RtpReceiveMonitor -> StabilityEngine
///   (every 100 ms) -> REMB bitrate cap / keyframe requests / playout delay / reconnect actions.
///
/// NOTE: written against the SIPSorcery API but not yet run against a console. Expect to adjust
/// small things (codec parameters, candidate formats) during the first real tests.
/// </summary>
public sealed class XboxPeer : IAsyncDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StabilityOptions _options;
    private RTCPeerConnection? _pc;
    private RTCDataChannel? _control, _input, _message, _chat;
    private readonly MessageChannelProtocol _messages = new();
    private Timer? _tick;
    private uint _inputSeq;
    private uint _remoteVideoSsrc;
    private StreamSession? _session;
#if XS_PATCHED_SIPSORCERY
    private MediaReceiver? _rx;
    private readonly bool _rttFromNacksOnly = Environment.GetEnvironmentVariable("XSTREAM_RTT") == "nack"; // A/B: the old estimate
#endif

    /// <summary>Which code receives and decrypts the media (ours, or SIPSorcery's as the fallback), once connected.</summary>
    public string ReceivePath { get; private set; } = "not connected";

    public StabilityEngine Engine { get; }
    /// <summary>Video receive logic: measurements, NACKs, frame assembly, playout buffer (WebRTC-library independent).</summary>
    public VideoReceiver Video { get; }
    public RtpReceiveMonitor Monitor => Video.Monitor;
    public H264Depacketizer Depacketizer => Video.Depacketizer;
    public NackGenerator Nacks => Video.Nacks;
    public PlayoutBuffer<AccessUnit> Frames => Video.Frames;
    public bool RembNegotiated { get; private set; }
    public bool NackNegotiated { get; private set; }
    /// <summary>
    /// True when built against the patched SIPSorcery (scripts/get-sipsorcery.*): the input channel is then
    /// unordered with no retransmissions, so a lost controller packet never delays later ones. Every packet
    /// carries the full controller state, so the next one (at most 8 ms later, or the 2 ms redundant copy)
    /// simply replaces a lost one.
    /// </summary>
    public static bool UnreliableInputSupported { get; } =
        typeof(RTCPeerConnection).Assembly.GetType("SIPSorcery.Net.SctpForwardTsnChunk") is not null;

    /// <summary>True once the console has accepted input (message handshake done, input channel open).</summary>
    public bool InputReady => _input?.readyState == RTCDataChannelState.open && _messages.HandshakeDone;
    private Timer? _fastTick;

    public event Action<StabilitySnapshot>? Stats;
    /// <summary>A complete frame was queued in <see cref="Frames"/> (wake the decoder).</summary>
    public event Action? FrameQueued { add => Video.FrameQueued += value; remove => Video.FrameQueued -= value; }
    /// <summary>Optional extra decoder-error source (the real decoder), checked every 10 ms.</summary>
    public Func<bool>? DecoderErrorSource { get => Video.DecoderErrorSource; set => Video.DecoderErrorSource = value; }
    /// <summary>Every engine tick: raw sample, engine snapshot, picture stats (for <see cref="SessionLogger"/>).</summary>
    public event Action<NetworkSample, StabilitySnapshot, PictureTick>? Sampled;
    public event Action<RecoveryAction>? RecoveryNeeded;   // app decides how to renegotiate / restart
    public event Action<RTCPeerConnectionState>? StateChanged;
    public event Action<(int pad, int left, int right, int lt, int rt, int durationMs)>? Rumble;
    public event Action<byte[], int>? AudioRtp;              // opus payload, for the audio sink
    /// <summary>Non-fatal problems worth showing the user.</summary>
    public event Action<string>? Log;

    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// Public STUN servers (the Xbox web player uses STUN the same way). They tell us our public address so the
    /// console can reach us from outside the home network; without them only same-network play works reliably.
    /// </summary>
    public static readonly string[] DefaultIceServers = ["stun:stun.l.google.com:19302", "stun:stun.cloudflare.com:3478"];

    private readonly IReadOnlyList<string> _iceServers;

    /// <param name="iceServers">STUN/TURN servers ("url" or "url;username;password"); null/empty = <see cref="DefaultIceServers"/>.</param>
    public XboxPeer(StabilityOptions? options = null, IReadOnlyList<string>? iceServers = null)
    {
        _options = options ?? new StabilityOptions();
        Engine = new StabilityEngine(_options);
        Video = new VideoReceiver(Engine, RecoveryOptions.FromEnvironment())
        {
            SendRtcp = rtcp => _pc?.SendRtcpRaw(SDPMediaTypesEnum.video, rtcp),
            KeyframeNeeded = RequestKeyframe,
        };
        _iceServers = iceServers is { Count: > 0 } ? iceServers : DefaultIceServers;
    }

    /// <summary>How the connection reached the console (set once connected): home network, internet direct, or relayed.</summary>
    public string Route { get; private set; } = "unknown";

    public async Task ConnectAsync(StreamSession session, CancellationToken ct = default)
    {
        _session = session;
        _pc = new RTCPeerConnection(new RTCConfiguration
        {
            X_UseRtpFeedbackProfile = true,
            iceServers = _iceServers.Select(RTCIceServer.Parse).ToList(),
        });

        var audio = new MediaStreamTrack(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1"),
            MediaStreamStatusEnum.SendRecv);
        var video = new MediaStreamTrack(new List<VideoFormat>
        {
            new(VideoCodecsEnum.H264, 102, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=4d001f"),
            new(VideoCodecsEnum.H264, 104, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"),
            new(VideoCodecsEnum.H264, 106, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f"),
        }, MediaStreamStatusEnum.RecvOnly);
        _pc.addTrack(audio);
        _pc.addTrack(video);

        _chat = await _pc.createDataChannel("chat", new RTCDataChannelInit { ordered = true, protocol = "chatV1" });
        _control = await _pc.createDataChannel(ControlMessages.ChannelName, new RTCDataChannelInit { ordered = true, protocol = ControlMessages.Protocol });
        // Input: unordered + never retransmit with the patched SIPSorcery; otherwise ordered + reliable
        // (what the browser client does), where a lost packet holds up every later one until resent.
        _input = await _pc.createDataChannel("input", UnreliableInputSupported
            ? new RTCDataChannelInit { ordered = false, maxRetransmits = 0, protocol = "1.0" }
            : new RTCDataChannelInit { ordered = true, protocol = "1.0" });
        _message = await _pc.createDataChannel(MessageChannelProtocol.ChannelName, new RTCDataChannelInit { ordered = true, protocol = MessageChannelProtocol.Protocol });
        WireChannels();

        _pc.OnRtpPacketReceived += OnRtp;
        EnlargeReceiveBuffer();
        _pc.onconnectionstatechange += s => StateChanged?.Invoke(s);

        var gathered = new TaskCompletionSource();
        var local = new List<IceCandidate>();
        _pc.onicecandidate += c => { if (c is not null) lock (local) local.Add(new IceCandidate(c.candidate, c.sdpMid, c.sdpMLineIndex)); };
        _pc.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };

        var offer = _pc.createOffer();
        // b=AS is the console's ceiling for the whole session (it doesn't go above it even when REMB asks), so
        // offer the user's maximum and let REMB, which starts at StartKbps, steer below it. Offering StartKbps
        // here held a healthy 1080p stream at ~10 Mbps all session.
        offer.sdp = SdpTools.PrepareOffer(offer.sdp, (int)_options.MaxKbps);
        await _pc.setLocalDescription(offer);
        // Gathering includes asking the STUN servers for our public address (usually well under a second).
        await Task.WhenAny(gathered.Task, Task.Delay(4_000, ct));
        lock (local)
            Log?.Invoke("Local candidates: " + string.Join(", ", local.GroupBy(c => CandidateType(c.Candidate)).Select(g => $"{g.Count()} {g.Key}")));

        var answer = await session.ExchangeSdpAsync(offer.sdp, ct);
        RembNegotiated = SdpTools.AnswerSupportsRemb(answer);
        NackNegotiated = SdpTools.AnswerSupportsNack(answer);
        Video.NackEnabled = NackNegotiated;
        var result = _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException("Console SDP answer rejected: " + result);

        List<IceCandidate> mine;
        lock (local) mine = local.ToList();
        IReadOnlyList<IceCandidate> remote = [];
        try { remote = await session.ExchangeIceAsync(mine, ct); }
        catch (Exception ex) when (ex is XboxApiException or TimeoutException)
        {
            // Both sides' candidates are already in the SDP offer/answer, so the link often connects without
            // this exchange. Only fail if it doesn't come up shortly.
            if (!await WaitConnectedAsync(TimeSpan.FromSeconds(5), ct)) throw;
            Log?.Invoke("ICE exchange failed but the connection is up, continuing (" + ex.Message + ")");
        }
        foreach (var c in Teredo.Expand(remote))
        {
            try
            {
                _pc.addIceCandidate(new RTCIceCandidateInit
                {
                    candidate = c.Candidate.Replace("a=candidate:", "candidate:"),
                    sdpMid = c.SdpMid ?? "0",
                    sdpMLineIndex = c.SdpMLineIndex,
                });
            }
            catch (Exception ex) { Debug.WriteLine($"Skipping candidate {c.Candidate}: {ex.Message}"); }
        }

        Log?.Invoke("Console candidates: " + string.Join(", ", remote.GroupBy(c => CandidateType(c.Candidate)).Select(g => $"{g.Count()} {g.Key}")));

        // From outside the home network this is where it either finds a path or doesn't: say so plainly.
        if (!await WaitConnectedAsync(TimeSpan.FromSeconds(20), ct))
            throw new ConsoleUnreachableException();
        Route = DescribeRoute();
        Log?.Invoke("Route: " + Route + (_pc?.DtlsTransportInfo.SrtpProtectionProfile is { } srtp ? $"; encryption {srtp}" : ""));
        Video.LocalSsrc = _pc?.VideoLocalTrack?.Ssrc ?? 1u;
        if (Video.Options != new RecoveryOptions()) Log?.Invoke("Loss recovery: " + Video.Options);
        StartOwnReceivePath();
        StartFakeInputIfAsked();

        _tick = new Timer(_ => Tick(), null, 100, 100);
        _fastTick = new Timer(_ => FastTick(), null, 10, 10);
    }

    private static string CandidateType(string candidate)
    {
        var parts = candidate.Split(' ');
        int i = Array.IndexOf(parts, "typ");
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : "unknown";
    }

    /// <summary>The candidate pair ICE settled on, in plain words.</summary>
    private string DescribeRoute()
    {
        try
        {
            var pair = _pc?.GetRtpChannel()?.NominatedEntry;
            if (pair is null) return "connected";
            var l = pair.LocalCandidate; var r = pair.RemoteCandidate;
            string detail = $"{l.type} {l.address} -> {r.type} {r.address}:{r.port}";
            if (l.type == RTCIceCandidateType.relay || r.type == RTCIceCandidateType.relay) return "Relayed through TURN (" + detail + ")";
            return IPAddress.TryParse(r.address, out var ip) && IsPrivate(ip)
                ? "Home network (" + detail + ")"
                : "Internet, direct (" + detail + ")";
        }
        catch { return "connected"; }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    /// <summary>Profiling: called per media packet with the ticks spent in our own handling of it.</summary>
    public Action<PacketCost>? PacketTimed { get; set; }

    /// <summary>
    /// What one received packet cost: time in our handler, bytes our handler allocated, and bytes the receive
    /// thread allocated since our previous packet on it (the WebRTC library's receive path, socket to RTP packet).
    /// </summary>
    public readonly record struct PacketCost(long Ticks, long OurBytes, long LibraryBytes);

    [ThreadStatic] private static long t_allocAfterLast;

    private void OnRtp(IPEndPoint ep, SDPMediaTypesEnum media, RTPPacket pkt)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread(), t0 = Stopwatch.GetTimestamp();
        try { HandleRtp(media, pkt); }
        finally
        {
            long t1 = Stopwatch.GetTimestamp(), a1 = GC.GetAllocatedBytesForCurrentThread();
            long lib = t_allocAfterLast > 0 ? a0 - t_allocAfterLast : 0;
            t_allocAfterLast = a1;
            PacketTimed?.Invoke(new PacketCost(t1 - t0, a1 - a0, lib));
        }
    }

    private void HandleRtp(SDPMediaTypesEnum media, RTPPacket pkt)
    {
        if (media is not (SDPMediaTypesEnum.audio or SDPMediaTypesEnum.video)) return;
        var h = pkt.Header;
        HandleRtp(media == SDPMediaTypesEnum.audio, h.SyncSource, h.SequenceNumber, h.Timestamp, h.MarkerBit == 1, pkt.Payload);
    }

    /// <summary>One RTP media packet, from whichever receive path is active.</summary>
    private void HandleRtp(bool audio, uint ssrc, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> payload)
    {
        if (audio) { AudioRtp?.Invoke(payload.ToArray(), seq); return; } // 50 a second: a copy is fine
        _remoteVideoSsrc = ssrc;
        Video.OnPacket(ssrc, seq, timestamp, marker, payload, NowMs);
    }

    /// <summary>
    /// Switch media receiving to our own path (<see cref="MediaReceiver"/>) once DTLS has the keys. SIPSorcery's stays
    /// as the fallback: unpatched builds, XSTREAM_RECEIVE=sipsorcery (for A/B comparisons), or if ours can't start.
    /// </summary>
    private void StartOwnReceivePath()
    {
#if XS_PATCHED_SIPSORCERY
        if (_pc is null || _rx is not null) return;
        if (Environment.GetEnvironmentVariable("XSTREAM_RECEIVE") is "sipsorcery")
            ReceivePath = "SIPSorcery (XSTREAM_RECEIVE=sipsorcery)";
        else
        {
            _rx = MediaReceiver.TryStart(_pc, OnOwnRtp, out var why);
            if (_rx is not null) _rx.Log += m => Log?.Invoke(m);
            ReceivePath = _rx is null ? $"SIPSorcery ({why})" : why;
        }
#else
        ReceivePath = "SIPSorcery (unpatched build)";
#endif
        Log?.Invoke("Receive path: " + ReceivePath);
    }

#if XS_PATCHED_SIPSORCERY
    private void OnOwnRtp(bool audio, uint ssrc, ushort seq, uint timestamp, bool marker, ReadOnlySpan<byte> payload)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread(), t0 = Stopwatch.GetTimestamp();
        try { HandleRtp(audio, ssrc, seq, timestamp, marker, payload); }
        finally
        {
            long t1 = Stopwatch.GetTimestamp(), a1 = GC.GetAllocatedBytesForCurrentThread();
            long lib = t_allocAfterLast > 0 ? a0 - t_allocAfterLast : 0;
            t_allocAfterLast = a1;
            PacketTimed?.Invoke(new PacketCost(t1 - t0, a1 - a0, lib));
        }
    }

    /// <summary>Packets our receive path decrypted / rejected (authentication) / handed back to SIPSorcery.</summary>
    public (long packets, long authFailures, long replays, long handedBack) ReceiveCounters =>
        _rx is null ? default : (_rx.Packets, _rx.Srtp.AuthFailures, _rx.Srtp.Replays, _rx.HandedBack);
#endif

    /// <summary>Every 10 ms: ask for lost packets, give up on frames that can't be completed in time.</summary>
    private void FastTick()
    {
        if (_pc is null) return;
        Video.FastTick(NowMs);
    }

    /// <summary>Called every 100 ms: measure, decide, act.</summary>
    private void Tick()
    {
        if (_pc is null) return;
        // Measured round trip (our STUN checks) when we have it; the NACK -> resend estimate otherwise.
        double rtt = double.NaN;
#if XS_PATCHED_SIPSORCERY
        if (_rx is not null && !_rttFromNacksOnly) rtt = _rx.RttMs;
#endif
        var (sample, pic, decoderError) = Video.Sample(NowMs, rtt);
        var d = Engine.Update(sample, decoderError);

        if (d.SendRemb && RembNegotiated && _remoteVideoSsrc != 0)
        {
            var localSsrc = _pc.VideoLocalTrack?.Ssrc ?? 1u;
            _pc.SendRtcpRaw(SDPMediaTypesEnum.video, Rtcp.BuildRemb(localSsrc, d.CapKbps, _remoteVideoSsrc));
        }
        if (d.RequestKeyframe) RequestKeyframe();
        Frames.TargetDelayMs = d.BufferTargetMs;

        switch (d.Recovery)
        {
            case RecoveryAction.RestartIce:
                // SIPSorcery restarts ICE locally; the console needs fresh candidates, so the app
                // re-runs the SDP/ICE exchange on the same session (see RecoveryNeeded).
                try { _pc.restartIce(); } catch (Exception ex) { Debug.WriteLine(ex); }
                RecoveryNeeded?.Invoke(d.Recovery);
                break;
            case RecoveryAction.Renegotiate:
            case RecoveryAction.StartNewSession:
            case RecoveryAction.GiveUp:
                RecoveryNeeded?.Invoke(d.Recovery);
                break;
        }
        Stats?.Invoke(Engine.Snapshot);
        Sampled?.Invoke(sample, Engine.Snapshot, pic);
    }

    private async Task<bool> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (_pc?.connectionState == RTCPeerConnectionState.connected) return true;
            await Task.Delay(100, ct);
        }
        return _pc?.connectionState == RTCPeerConnectionState.connected;
    }

    /// <summary>
    /// A keyframe at 1440p and 30-40 Mbps arrives as a burst of several hundred KB at line rate. The OS's
    /// default UDP receive buffer (64 KB on Windows) overflows on that burst and drops packets, which then
    /// need resending or a fresh keyframe: a visible hitch every time. Ask for 8 MB (the OS may cap it).
    /// </summary>
    private void EnlargeReceiveBuffer()
    {
        try
        {
            var socket = _pc?.GetRtpChannel()?.RtpSocket;
            if (socket is null) return;
            int before = socket.ReceiveBufferSize;
            foreach (int size in new[] { 8 << 20, 4 << 20, 2 << 20, 1 << 20 })
            {
                try { socket.ReceiveBufferSize = size; break; }
                catch (System.Net.Sockets.SocketException) { } // over the OS limit: try smaller
            }
            Log?.Invoke($"UDP receive buffer {before / 1024} KB -> {socket.ReceiveBufferSize / 1024} KB");
        }
        catch (Exception ex) { Log?.Invoke("Couldn't enlarge the receive buffer: " + ex.Message); }
    }

    public void RequestKeyframe()
    {
        if (_control?.readyState == RTCDataChannelState.open) _control.send(ControlMessages.KeyframeRequest(true));
        if (_pc is not null && _remoteVideoSsrc != 0)
            _pc.SendRtcpRaw(SDPMediaTypesEnum.video, Rtcp.BuildPli(_pc.VideoLocalTrack?.Ssrc ?? 1u, _remoteVideoSsrc));
    }

    /// <summary>Send controller state. Called by <see cref="InputPump"/> the instant anything changes.</summary>
    /// <summary>
    /// Send one 20 ms Opus frame from the microphone (party and game chat) on the audio track the session
    /// negotiated as send-and-receive. Any thread.
    /// </summary>
    public void SendMicrophone(byte[] opusFrame, uint samples = 960)
    {
        var pc = _pc;
        if (pc is null || pc.connectionState != RTCPeerConnectionState.connected) return;
        try { pc.SendAudio(samples, opusFrame); } catch { /* connection closing */ }
    }

    public void SendGamepad(GamepadState[] pads)
    {
        if (!InputReady) return;
        _input!.send(InputPacket.Gamepad(Interlocked.Increment(ref _inputSeq), NowMs, pads));
    }

    /// <summary>
    /// XSTREAM_FAKE_INPUT=hz (testing): send an idle controller (nothing pressed, sticks centred) that often, so the
    /// input path's delays can be measured without a controller in hand. Harmless to the console.
    /// </summary>
    private void StartFakeInputIfAsked()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("XSTREAM_FAKE_INPUT"), out int hz) || hz <= 0) return;
        Log?.Invoke($"Test mode: sending an idle controller {hz} times a second");
#if XS_PATCHED_SIPSORCERY
        SctpSendDelay.Reset();
#endif
        var idle = new[] { new GamepadState(0, GamepadButtons.None, 0, 0, 0, 0, 0, 0) };
        new Thread(() =>
        {
            var sw = Stopwatch.StartNew(); long n = 0;
            while (_pc is not null && _pc.connectionState != RTCPeerConnectionState.closed)
            {
                SendGamepad(idle);
                n++;
                while (sw.Elapsed.TotalSeconds * hz < n) Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "fake-input" }.Start();
    }

    /// <summary>Tell the console a controller was plugged in or removed.</summary>
    public void PadConnectionChanged(int index, bool added)
    {
        if (_control?.readyState == RTCDataChannelState.open && _messages.HandshakeDone)
            _control.send(ControlMessages.GamepadChanged(index, added));
    }

    private void WireChannels()
    {
        _message!.onopen += () => _message.send(_messages.Handshake());
        _message.onmessage += (_, _, data) =>
        {
            foreach (var reply in _messages.OnMessage(Encoding.UTF8.GetString(data))) _message.send(reply);
        };
        _messages.HandshakeCompleted += () =>
        {
            _control!.send(ControlMessages.Authorization());
            _control.send(ControlMessages.GamepadChanged(0, true));
            _input!.send(InputPacket.ClientMetadata(Interlocked.Increment(ref _inputSeq), NowMs));
        };
        _messages.ServerDisconnected += () => RecoveryNeeded?.Invoke(RecoveryAction.GiveUp);
        _input!.onmessage += (_, _, data) =>
        {
            if (InputPacket.TryParseVibration(data, out var v)) Rumble?.Invoke(v);
        };
    }

    public async ValueTask DisposeAsync()
    {
        _tick?.Dispose();
        _fastTick?.Dispose();
        _pc?.close();
        if (Depacketizer.FramesDelivered > 0)
            Log?.Invoke($"Loss recovery: {Depacketizer.FramesLost} frames lost, {Depacketizer.FramesSkipped} lost non-reference frames skipped " +
                        $"without a freeze, {Video.FastKeyframeRequests} keyframes asked for at once, {Engine.KeyframeHolds} repeat requests held until loss eased; NACKs: {Nacks.Requested} asked, " +
                        $"{Nacks.Recovered} recovered, {Nacks.GivenUp} given up, {Nacks.Abandoned} dropped with their frame; " +
                        $"resend round trip {Nacks.RttEstimateMs:F1} ms; " +
                        $"{Monitor.Outages} outages (nothing arriving for 1+ s, {Monitor.OutagePackets} packets missed, not counted as loss)");
#if XS_PATCHED_SIPSORCERY
        if (SctpSendDelay.Count > 0)
            Log?.Invoke($"Data channel send delay: {SctpSendDelay.Count} messages, {SctpSendDelay.Over1Ms} waited >1 ms, " +
                        $"{SctpSendDelay.Over5Ms} >5 ms, {SctpSendDelay.Over20Ms} >20 ms, worst {SctpSendDelay.MaxMs:F1} ms; {SctpSendDelay.Unacknowledged} unacknowledged at the end");
        if (_rx is not null)
        {
            var (n, auth, dup, back) = ReceiveCounters;
            Log?.Invoke($"Own receive path: {n} media packets, {auth} failed authentication, {dup} duplicates dropped, " +
                        $"{back} other packets handed to SIPSorcery" + (_rx.Stopped is { } why ? $"; stopped early: {why}" : ""));
            Log?.Invoke(_rx.RttAnswers > 0
                ? $"Round trip (STUN checks): {_rx.RttAnswers}/{_rx.RttProbes} answered, smoothed {_rx.RttMs:F2} ms, min {_rx.RttMinMs:F2} ms, max {_rx.RttMaxMs:F2} ms"
                : $"Round trip (STUN checks): no answers to {_rx.RttProbes} probes; using the NACK estimate");
            _rx.Dispose();
        }
#endif
        if (_session is not null) { try { await _session.StopAsync(); } catch { /* best effort */ } }
    }
}

/// <summary>The connection to the console never came up (typically: away from home and no path through the routers).</summary>
public sealed class ConsoleUnreachableException() : Exception(
    "Couldn't reach the console over the network. If you're away from home: on the Xbox, check Settings → General → " +
    "Network settings shows NAT type Open or Moderate (turn on UPnP on your home router if it says Strict), and try another " +
    "network if this one blocks it (some workplaces and mobile carriers do). A TURN server can be added in settings.json for strict networks.");
