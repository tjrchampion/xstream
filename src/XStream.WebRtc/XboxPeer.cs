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

    public RtpReceiveMonitor Monitor { get; } = new();
    public StabilityEngine Engine { get; }
    public H264Depacketizer Depacketizer { get; } = new();
    public NackGenerator Nacks { get; } = new();
    public PlayoutBuffer<AccessUnit> Frames { get; } = new();
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
    private readonly object _rxLock = new();
    private Timer? _fastTick;

    public event Action<StabilitySnapshot>? Stats;
    /// <summary>A complete frame was queued in <see cref="Frames"/> (wake the decoder).</summary>
    public event Action? FrameQueued;
    /// <summary>Optional extra decoder-error source (the real decoder), checked every tick.</summary>
    public Func<bool>? DecoderErrorSource { get; set; }
    /// <summary>Every engine tick: raw sample, engine snapshot, picture stats (for <see cref="SessionLogger"/>).</summary>
    public event Action<NetworkSample, StabilitySnapshot, PictureTick>? Sampled;
    public event Action<RecoveryAction>? RecoveryNeeded;   // app decides how to renegotiate / restart
    public event Action<RTCPeerConnectionState>? StateChanged;
    public event Action<(int pad, int left, int right, int lt, int rt, int durationMs)>? Rumble;
    public event Action<byte[], int>? AudioRtp;              // opus payload, for the audio sink
    /// <summary>Non-fatal problems worth showing the user.</summary>
    public event Action<string>? Log;

    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public XboxPeer(StabilityOptions? options = null)
    {
        _options = options ?? new StabilityOptions();
        Engine = new StabilityEngine(_options);
    }

    public async Task ConnectAsync(StreamSession session, CancellationToken ct = default)
    {
        _session = session;
        _pc = new RTCPeerConnection(new RTCConfiguration { X_UseRtpFeedbackProfile = true });

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
        await Task.WhenAny(gathered.Task, Task.Delay(3_000, ct));

        var answer = await session.ExchangeSdpAsync(offer.sdp, ct);
        RembNegotiated = SdpTools.AnswerSupportsRemb(answer);
        NackNegotiated = SdpTools.AnswerSupportsNack(answer);
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

        _tick = new Timer(_ => Tick(), null, 100, 100);
        _fastTick = new Timer(_ => FastTick(), null, 10, 10);
    }

    private void OnRtp(IPEndPoint ep, SDPMediaTypesEnum media, RTPPacket pkt)
    {
        if (media == SDPMediaTypesEnum.audio) { AudioRtp?.Invoke(pkt.Payload, pkt.Header.SequenceNumber); return; }
        if (media != SDPMediaTypesEnum.video) return;
        _remoteVideoSsrc = pkt.Header.SyncSource;
        double now = NowMs;
        lock (_rxLock)
        {
            Monitor.OnPacket(pkt.Header.SequenceNumber, pkt.Header.Timestamp, pkt.Header.MarkerBit == 1,
                pkt.Payload.Length, now, H264.IsKeyframeStart(pkt.Payload));
            Nacks.OnPacket(pkt.Header.SequenceNumber, now);
            foreach (var au in Depacketizer.Push(pkt.Header.SequenceNumber, pkt.Header.Timestamp, pkt.Header.MarkerBit == 1, pkt.Payload, now))
            {
                Frames.Enqueue(au, au.RtpTimestamp, now);
                FrameQueued?.Invoke();
            }
        }
    }

    /// <summary>Every 10 ms: ask for lost packets, give up on frames that can't be completed in time.</summary>
    private void FastTick()
    {
        if (_pc is null) return;
        double now = NowMs;
        IReadOnlyList<ushort> lost;
        lock (_rxLock)
        {
            foreach (var au in Depacketizer.Tick(now)) { Frames.Enqueue(au, au.RtpTimestamp, now); FrameQueued?.Invoke(); }
            lost = NackNegotiated ? Nacks.Poll(now, Monitor.RttMs) : Array.Empty<ushort>();
        }
        if (lost.Count > 0 && _remoteVideoSsrc != 0)
            _pc.SendRtcpRaw(SDPMediaTypesEnum.video, NackGenerator.BuildNack(_pc.VideoLocalTrack?.Ssrc ?? 1u, _remoteVideoSsrc, lost));
    }

    /// <summary>Called every 100 ms: measure, decide, act.</summary>
    private void Tick()
    {
        if (_pc is null) return;
        NetworkSample sample;
        PictureTick pic;
        bool decoderError;
        lock (_rxLock)
        {
            if (!double.IsNaN(Nacks.RttEstimateMs)) Monitor.RttMs = Nacks.RttEstimateMs;
            sample = Monitor.TakeSample(NowMs);
            decoderError = Depacketizer.TakeDecoderError();
            double since = double.IsNaN(Depacketizer.LastDeliveredMs) ? sample.MsSinceLastFrame : NowMs - Depacketizer.LastDeliveredMs;
            pic = new PictureTick(Depacketizer.FramesDelivered, Depacketizer.FramesLost, Depacketizer.PictureBroken, since,
                Nacks.Requested, Nacks.Recovered);
            // Wait for retransmissions roughly one round trip, never less than 60 ms.
            Depacketizer.MaxWaitMs = Math.Clamp(Monitor.RttMs * 2 + 20, 60, 250);
        }
        if (DecoderErrorSource?.Invoke() == true) decoderError = true;
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
        if (_session is not null) { try { await _session.StopAsync(); } catch { /* best effort */ } }
    }
}
