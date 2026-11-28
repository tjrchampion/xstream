using XStream.Core.Audio;
using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Rtp;
using XStream.Core.Stability;
using XStream.Core.Together;
using XStream.Core.Video;
using XStream.Core.Xbox;

namespace XStream.Player;

public sealed record GuestOptions
{
    public required TogetherInvite Invite { get; init; }
    public string Name { get; init; } = "Friend";
    public AppSettings Settings { get; init; } = new();
    public bool Fullscreen { get; init; }
    public WindowPlace? Place { get; init; }
    /// <summary>Keep the window hidden until the first picture (the launcher shows its own joining screen meanwhile).</summary>
    public bool StartHidden { get; init; } = true;
    /// <summary>No window, sound or controller: just join and count frames (testing).</summary>
    public bool Headless { get; init; }
    public bool HardwareDecode { get; init; } = true;
    /// <summary>Testing: drop this share of what arrives (0-100).</summary>
    public double SimulatedLossPct { get; init; }
    public bool Stun { get; init; } = true;
    /// <summary>Testing (headless): send a pretend controller that presses A once a second.</summary>
    public bool FakeInput { get; init; }
    /// <summary>Testing and screenshots: open the stream menu this long after joining (0: never).</summary>
    public double OpenMenuAfterSeconds { get; init; }
}

/// <summary>
/// Watching a friend's stream (Play together): the picture and sound arrive from the friend's Vexel, and this
/// device's controller is sent back as the next player on their console. No Xbox account or sign-in is needed.
/// Same window, decoder, overlay and sound as a normal stream; <see cref="Run"/> blocks on the main thread.
/// </summary>
public sealed class GuestSession
{
    private readonly ManualResetEventSlim _stop = new();
    private TogetherClient? _client;

    public event Action<PlayerStage, string>? StageChanged;
    public event Action<string>? Log;
    public event Action<LiveStats>? Stats;
    public event Action<TogetherStatus>? TogetherChanged;
    /// <summary>Raw numbers once a second (the CLI harness prints them).</summary>
    public event Action<TogetherClientStats>? ClientStats;

    public PlayerStage Stage { get; private set; } = PlayerStage.Starting;
    public WindowPlace? LastWindow { get; private set; }
    private long _ticks;
    public long MainLoopTicks => Interlocked.Read(ref _ticks);
    public bool InMainLoop { get; private set; }

    /// <summary>Leave (any thread).</summary>
    public void Stop() => _stop.Set();

    private void SetStage(PlayerStage s, string m) { Stage = s; StageChanged?.Invoke(s, m); }

    public int Run(GuestOptions o)
    {
        var live = o.Settings;
        string host = "your friend";
        int slot = -1;
        TogetherEnd? ended = null;
        bool firstPicture = false;
        SetStage(PlayerStage.Starting, "Getting ready");

        using var client = _client = new TogetherClient(o.Invite, new TogetherClientOptions
        {
            Name = o.Name, SimulatedLossPct = o.SimulatedLossPct, Stun = o.Stun,
        });
        client.Log += m => Log?.Invoke(m);

        // Picture: frames from the friend -> playout buffer -> decoder -> window (the same path as a console stream).
        VideoWindow? window = null;
        FrameMailbox? mailbox = null;
        StreamOverlay? overlay = null;
        VideoPipeline? video = null;
        var buffer = new PlayoutBuffer<AccessUnit> { TargetDelayMs = live.Profile switch { StreamProfile.Competitive => 0, StreamProfile.Smooth => 40, _ => 8 } };
        if (!o.Headless)
        {
            try
            {
                mailbox = new FrameMailbox();
                window = new VideoWindow(mailbox, () => client.NowMs, new VideoWindowOptions
                {
                    Title = "Vexel: Play together", Fullscreen = o.Fullscreen, StartHidden = o.StartHidden,
                    X = o.Place?.X, Y = o.Place?.Y,
                    Width = o.Place is { W: >= 320 } p ? p.W : 1280, Height = o.Place is { H: >= 200 } q ? q.H : 720,
                });
                window.Sharpen = (int)live.Clarity;
                window.Enhance = live.Enhance;
                window.Scaling = live.Scaling;
                overlay = new StreamOverlay(host, live, o.Fullscreen, together: TogetherMode.Guest);
                window.Overlay = overlay;
                window.KeyDown = overlay.OnKey;
                window.KeyUp = overlay.OnKeyUp;
                window.Click = overlay.OnClick;
                overlay.DisconnectRequested += Stop;
                video = new VideoPipeline(buffer, VideoDecoder.Create(o.HardwareDecode, window.D3D11Device), mailbox, () => client.NowMs);
                var win = window;
                video.FormatChanged = f => { win.Format = f; Log?.Invoke("Video stream: " + f); };
                window.FirstPicture += () =>
                {
                    firstPicture = true;
                    SetStage(PlayerStage.Streaming, $"Playing with {host}. Press Esc or hold {{view}} + {{menu}} for the menu.");
                };
                // XSTREAM_SCREENSHOT=file.png;seconds (testing): capture what the window shows, overlay included.
                foreach (var shot in (Environment.GetEnvironmentVariable("XSTREAM_SCREENSHOT") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                    if (shot.Split(';') is [var shotPath, var shotAt])
                        _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(shotAt, System.Globalization.CultureInfo.InvariantCulture)))
                            .ContinueWith(_ =>
                            {
                                // No picture yet (still joining): the window is black under the overlay, so draw that.
                                if (!firstPicture) { Screenshots.SaveWithOverlay(new byte[2560 * 1440 * 4], 2560, 1440, overlay, shotPath); Log?.Invoke("Screenshot: " + shotPath); return; }
                                win.RequestScreenshot((px, w, h) => { Screenshots.SaveWithOverlay(px, w, h, overlay, shotPath); Log?.Invoke("Screenshot: " + shotPath); });
                            });
            }
            catch (Exception ex)
            {
                Log?.Invoke("No video window (" + ex.Message + "). Install FFmpeg + SDL2.");
                window?.Dispose(); window = null; mailbox = null; overlay = null; video = null;
            }
        }
        if (overlay is not null)
            overlay.SettingsChanged += change =>
            {
                var next = change(AppSettings.Load());
                try { next.Save(); } catch (IOException) { }
                live = next;
                overlay.Sync(next);
            };

        // Sound.
        AudioPlayer? audio = null;
        if (!o.Headless && live.Audio)
            try { audio = new AudioPlayer { Volume = Math.Clamp(live.Volume, 0, 100) / 100f, TargetMs = 50 }; }
            catch (Exception ex) { Log?.Invoke("No sound (" + ex.Message + ")"); }

        // This device's controller, with this device's own button layout, sent as the friend's next player.
        IGamepadBackend? backend = null;
        InputPump? pump = null;
        int lastPad = 0;
        if (!o.Headless)
            try
            {
                backend = GamepadBackends.CreateDefault(needExtras: live.NeedsControllerExtras);
                var ctl = live.DefaultController;
                var router = new HostPadRouter(_ => false) { Menu = overlay is null ? null : f => overlay.OnPad(f) };
                var kbm = KeyboardMouseInput.FromSettings(live);
                if (overlay is not null) { kbm.Blocked = () => overlay.HoldsInput; if (window is not null) window.Input = kbm; }
                pump = new InputPump(backend, new InputOptions
                {
                    NexusComboMs = 0, Mapping = ControllerMapping.Parse(ctl.ButtonMapping),
                    Gyro = ctl.GyroAim ? new GyroAim(ctl.GyroSensitivity) : null, GyroHold = ctl.GyroHoldButton, KeyboardMouse = kbm,
                }, frame =>
                {
                    if (router.Route(frame) is not { Length: > 0 } f) return;
                    lastPad = f[0].Index;
                    client.SendInput(f[0]);
                });
                Log?.Invoke($"Controller: {backend.Name}, {ctl.Summary}");
            }
            catch (Exception ex) { Log?.Invoke("No controller (" + ex.Message + "): watching only."); }

        client.Frame += f =>
        {
            double now = client.NowMs;
            buffer.Enqueue(new AccessUnit(f.Data, f.RtpTimestamp, f.Keyframe, now), f.RtpTimestamp, now);
            video?.Notify();
        };
        client.Audio += (opus, seq) => audio?.Push(opus, seq);
        client.Rumble += r =>
        {
            float k = Math.Clamp(live.RumbleStrength, 0, 100) / 100f;
            backend?.Rumble(lastPad, r.Low * k, r.High * k, live.TriggerRumble ? r.LeftTrigger * k : 0, live.TriggerRumble ? r.RightTrigger * k : 0, r.DurationMs);
        };

        void Status()
        {
            var w = client.Welcome;
            TogetherChanged?.Invoke(new TogetherStatus
            {
                Role = "guest", HostName = w?.HostName, Slot = w?.Slot ?? -1, ReplyCode = client.State == TogetherClientState.NeedReply ? client.ReplyCode : null,
                Players = client.State is TogetherClientState.Joined or TogetherClientState.Lost ? 2 : 0,
            });
        }
        var clipboard = new System.Collections.Concurrent.ConcurrentQueue<string>();
        client.StateChanged += st =>
        {
            switch (st)
            {
                case TogetherClientState.NeedReply:
                    overlay?.SetGuestStatus("Can't reach your friend directly",
                        "Send them this reply code. On their stream they open the menu, choose Play together, then Paste reply code.", client.ReplyCode);
                    if (client.ReplyCode is { } rc) clipboard.Enqueue(rc);
                    SetStage(PlayerStage.Connecting, "Can't reach your friend directly: send them the reply code");
                    break;
                case TogetherClientState.Joined when slot >= 0:
                    overlay?.SetReconnecting(null);
                    overlay?.ShowToast("Back with " + host, 2500, ToastKind.Good);
                    break;
                case TogetherClientState.Lost:
                    overlay?.SetReconnecting($"Connection to {host} lost, retrying…");
                    SetStage(PlayerStage.Reconnecting, $"Connection to {host} lost, retrying…");
                    break;
            }
            Status();
        };
        client.Joined += w =>
        {
            host = w.HostName.Length > 0 ? w.HostName : "your friend";
            bool first = slot < 0;
            slot = w.Slot;
            overlay?.SetConsoleName(host);
            overlay?.SetGuestMeta($"You're Player {slot + 1}" + (w.GameName.Length > 0 ? " · " + w.GameName : ""));
            overlay?.SetGuestStatus(null);
            overlay?.ShowToast($"Joined {host}'s stream as Player {slot + 1}", 5000, ToastKind.Good);
            Log?.Invoke($"Joined {host}'s stream as Player {slot + 1}{(w.GameName.Length > 0 ? " (" + w.GameName + ")" : "")}");
            if (first && o.OpenMenuAfterSeconds > 0 && overlay is { } ovm)
                _ = Task.Delay(TimeSpan.FromSeconds(o.OpenMenuAfterSeconds)).ContinueWith(_ => ovm.OnKey(27));
            if (first) SetStage(window is null ? PlayerStage.Streaming : PlayerStage.StartingVideo, window is null ? $"Joined {host} (no picture)" : "Starting the picture");
            else SetStage(firstPicture ? PlayerStage.Streaming : PlayerStage.StartingVideo, "Back with " + host);
            Status();
        };
        client.Ended += why =>
        {
            ended = why;
            string msg = why switch
            {
                TogetherEnd.HostEnded => $"{host} ended the stream",
                TogetherEnd.Kicked => $"{host} removed you from the stream",
                TogetherEnd.Full => "Every controller on your friend's console is taken. Ask them to free one, then join again.",
                TogetherEnd.WrongVersion => "Your friend has a different version of Vexel. Update both to the latest, then try again.",
                TogetherEnd.Unreachable => "Couldn't reach your friend. Check the invite is new (it changes each time they share), or use the reply code.",
                TogetherEnd.Lost => $"Lost the connection to {host}",
                _ => "You left",
            };
            Log?.Invoke(msg);
            if (why != TogetherEnd.Left)
            {
                overlay?.SetGuestStatus(null);
                overlay?.ShowToast(msg, 4000, why is TogetherEnd.HostEnded or TogetherEnd.Left ? ToastKind.Warn : ToastKind.Bad);
                // Long enough to read the message, then close.
                _ = Task.Delay(why == TogetherEnd.HostEnded ? 3500 : 4500).ContinueWith(_ => Stop());
            }
            _endMessage = msg;
        };

        overlay?.SetGuestStatus("Joining your friend's stream", "Reaching them now. This usually takes a second or two.");
        SetStage(PlayerStage.Connecting, "Reaching your friend");
        video?.Start();
        client.Start();
        pump?.Start();
        Status();
        if (o.FakeInput)
            new Thread(() =>
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                while (!_stop.IsSet)
                {
                    bool a = (t.ElapsedMilliseconds / 1000) % 2 == 1;
                    client.SendInput(new GamepadState(0, a ? GamepadButtons.A : GamepadButtons.None, a ? 0.5f : 0, 0, 0, 0, 0, 0));
                    Thread.Sleep(8);
                }
            }) { IsBackground = true, Name = "vexel-fake-pad" }.Start();

        // Once a second: numbers for the menu, the launcher and the log.
        double lastStats = 0;
        void EverySecond()
        {
            var s = client.Stats;
            ClientStats?.Invoke(s);
            var l = new LiveStats
            {
                TimeS = client.NowMs / 1000, Mbps = s.Kbps / 1000, LossPct = s.LossPct, PingMs = double.IsNaN(s.RttMs) ? 0 : s.RttMs,
                Link = client.State == TogetherClientState.Joined ? "Connected" : client.State.ToString(),
                Verdict = slot >= 0 ? $"Player {slot + 1} on {host}'s console · {s.LossPct:F1}% loss · {s.Recovered} lost pieces resent" : "",
                Pads = pump?.Stats().PadsConnected ?? 0,
            };
            if (window is not null && video is not null)
            {
                var d = window.TakeStats();
                var (vw, vh) = window.VideoSize;
                l = l with
                {
                    Fps = d.Fps, NetToScreenP50Ms = d.ArrivalToPresentP50Ms, NetToScreenP99Ms = d.ArrivalToPresentP99Ms, DecodeMs = video.DecodeP50Ms,
                    VideoWidth = vw, VideoHeight = vh, AudioQueuedMs = audio?.QueuedMs ?? 0,
                };
                overlay?.UpdateStats(l, (vw, vh));
            }
            if (video?.TakeDecodeError() == true) client.RequestKeyframe();
            Stats?.Invoke(l);
        }

        if (window is not null)
        {
            window.Run(() =>
            {
                Interlocked.Increment(ref _ticks); InMainLoop = true;
                overlay?.Apply(window);
                while (clipboard.TryDequeue(out var text))
                    if (SdlClipboard.Set(text)) overlay?.ShowToast("Reply code copied: send it to your friend", 4000, ToastKind.Good);
                if (client.NowMs - lastStats >= 1000) { lastStats = client.NowMs; EverySecond(); }
                return !_stop.IsSet;
            });
            if (window.QuitRequested) Stop();
        }
        else
            while (!_stop.Wait(50))
            {
                Interlocked.Increment(ref _ticks); InMainLoop = true;
                if (client.NowMs - lastStats >= 1000) { lastStats = client.NowMs; EverySecond(); }
            }
        InMainLoop = false;

        SetStage(PlayerStage.Stopping, "Leaving");
        if (ended is null) client.Leave();
        pump?.Dispose();
        video?.Dispose();
        audio?.Dispose();
        LastWindow = window?.WindowedRect is { } r ? new WindowPlace(r.X, r.Y, r.W, r.H) : null;
        window?.Dispose();
        TogetherChanged?.Invoke(new TogetherStatus { Role = "guest", Players = 0 });
        bool failed = ended is TogetherEnd.Unreachable or TogetherEnd.Full or TogetherEnd.WrongVersion || (ended is TogetherEnd.Lost && slot < 0);
        SetStage(failed ? PlayerStage.Failed : PlayerStage.Ended, _endMessage ?? "You left");
        return failed ? 1 : 0;
    }

    private string? _endMessage;
}
