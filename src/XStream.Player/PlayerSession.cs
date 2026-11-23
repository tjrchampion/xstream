using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Stability;
using XStream.Core.Video;
using XStream.Core.Xbox;
using XStream.WebRtc;

namespace XStream.Player;

public sealed record PlayerOptions
{
    public required string ServerId { get; init; }
    /// <summary>Set for Xbox Cloud Gaming: the game to start (ServerId is then unused). The client must hold a cloud token.</summary>
    public string? CloudTitleId { get; init; }
    public bool IsCloud => !string.IsNullOrEmpty(CloudTitleId);
    public StabilityOptions Stability { get; init; } = new();
    public bool Fullscreen { get; init; } = true;
    public bool VSync { get; init; }
    public bool HardwareDecode { get; init; } = true;
    /// <summary>No video window: still connects, measures, and logs.</summary>
    public bool Headless { get; init; }
    public string WindowTitle { get; init; } = "Vexel";
    /// <summary>Controller rumble strength, 0-100%.</summary>
    public int RumbleStrength { get; init; } = 100;
    /// <summary>Pass impulse-trigger rumble through (off: main motors only, like the xbox.com client).</summary>
    public bool TriggerRumble { get; init; }
    /// <summary>Shown in the in-stream menu.</summary>
    public string ConsoleName { get; init; } = "Xbox";
    /// <summary>Start with the stats readout over the video.</summary>
    public bool ShowStatsHud { get; init; }
    public bool Audio { get; init; } = true;
    /// <summary>Game audio volume, 0-100%.</summary>
    public int Volume { get; init; } = 100;
    /// <summary>Clarity boost (sharpening before the upscale to the display): 0 off, 1 low, 2 medium, 3 high.</summary>
    public int Sharpen { get; init; } = 1;
    public VideoScaling Scaling { get; init; } = VideoScaling.Fit;
    /// <summary>The full settings the session started with (the in-stream menu shows and edits them).</summary>
    public AppSettings? Settings { get; init; }

    public static PlayerOptions From(AppSettings s, string serverId) => new()
    {
        ServerId = serverId, Stability = s.ToStabilityOptions(), Fullscreen = s.Fullscreen, VSync = s.VSync,
        HardwareDecode = s.HardwareDecode, RumbleStrength = s.RumbleStrength, TriggerRumble = s.TriggerRumble,
        ShowStatsHud = s.ShowStatsHud, Audio = s.Audio, Volume = s.Volume, Sharpen = (int)s.Clarity, Scaling = s.Scaling, Settings = s,
    };
}

/// <summary>
/// One play session: start the console session, connect WebRTC, run video + controller, log, and clean
/// up. <see cref="Run"/> blocks and must be called on the main thread (macOS needs the video window
/// there). <see cref="Stop"/> can be called from any thread.
/// </summary>
public sealed class PlayerSession
{
    private readonly ManualResetEventSlim _stop = new();
    private readonly CancellationTokenSource _cts = new();

    public event Action<PlayerStage, string>? StageChanged;
    public event Action<string>? Log;
    public event Action<LiveStats>? Stats;
    public event Action<SessionInfo>? Info;
    public event Action<SessionSummary, string>? Ended;
    /// <summary>"More settings…" chosen in the stream menu: the launcher should show its Settings page.</summary>
    public event Action? MoreSettingsRequested;
    /// <summary>The player came back to the game from the launcher's Settings (tapped the video).</summary>
    public event Action? ReturnedToGame;

    private StreamOverlay? _overlay;

    /// <summary>The launcher's Settings were closed: give the controller back to the game. Any thread.</summary>
    public void ReturnToGame() => _overlay?.ComeBack();

    /// <summary>Watches settings.json (the launcher may change it mid-stream); debounced.</summary>
    private static IDisposable? WatchSettings(Action onChange)
    {
        try
        {
            Timer? debounce = null;
            var w = new FileSystemWatcher(AppPaths.Root, Path.GetFileName(AppPaths.Settings))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler h = (_, _) => { debounce?.Dispose(); debounce = new Timer(_ => onChange(), null, 120, Timeout.Infinite); };
            w.Changed += h; w.Created += h; w.Renamed += (_, _) => h(null!, null!);
            w.EnableRaisingEvents = true;
            return w;
        }
        catch { return null; }
    }

    public PlayerStage Stage { get; private set; } = PlayerStage.Starting;

    // Watchdog: the main loop (window events and drawing) ticks this; PlayerHost reports it in its heartbeat so
    // the launcher can tell a frozen stream from a busy one. Not armed until the window loop starts.
    private long _mainTicks;
    public long MainLoopTicks => Interlocked.Read(ref _mainTicks);
    public bool InMainLoop { get; private set; }
    private void Tick() { Interlocked.Increment(ref _mainTicks); InMainLoop = true; }
    private bool TickTrue() { Tick(); return true; }

    public void Stop()
    {
        _stop.Set();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>How many times a dropped connection is retried before the stream gives up.</summary>
    public const int ReconnectAttempts = 4;
    private static readonly int[] ReconnectBackoffMs = [0, 2_000, 5_000, 10_000];

    /// <returns>0 if the session ran and ended normally, 1 if it failed.</returns>
    /// <param name="refreshClient">Fresh streaming client (sign-in refreshed) for reconnects; the first one may have expired by then.</param>
    public int Run(PlayerOptions o, XboxStreamingClient client, Func<Task<string>> getTransferToken,
        Func<Task<XboxStreamingClient>>? refreshClient = null)
    {
        StreamSession? session = null;
        XboxPeer? cur = null;            // the live connection; replaced when the stream reconnects
        XboxPeer? lastPeer = null;
        VideoPipeline? video = null;     // per connection (bound to that connection's frame buffer)
        try
        {
            var profile = o.Stability.Profile;

            StreamSession OpenSession(XboxStreamingClient c, bool quiet)
            {
                if (!quiet) SetStage(PlayerStage.RequestingSession, o.IsCloud
                    ? $"Finding a cloud server ({profile})"
                    : $"Requesting a session ({profile}, up to {o.Stability.MaxKbps / 1000:F0} Mbps)");
                var s = o.IsCloud
                    ? c.StartCloudStreamAsync(o.CloudTitleId!, _cts.Token).GetAwaiter().GetResult()
                    : c.StartHomeStreamAsync(o.ServerId, _cts.Token).GetAwaiter().GetResult();
                if (!quiet) SetStage(PlayerStage.WakingConsole, o.IsCloud ? "Starting the game in the cloud" : "Waiting for the console to get ready");
                // Cloud: the queue (WaitingForResources) can take minutes; the wait keeps extending while in it.
                void OnQueue(int? seconds)
                {
                    string eta = seconds is > 0 ? (seconds >= 90 ? $"about {Math.Round(seconds.Value / 60.0)} min" : $"about {seconds} s") : "a short wait";
                    SetStage(PlayerStage.InQueue, $"In the queue for a cloud server: {eta}");
                }
                s.WaitUntilProvisionedAsync(getTransferToken, quiet ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(2), _cts.Token,
                    quiet ? null : OnQueue).GetAwaiter().GetResult();
                return s;
            }
            session = OpenSession(client, quiet: false);

            SetStage(PlayerStage.Connecting, "Connecting video and controller");
            // A controller-only session looks idle to Windows: keep the screen on and the device awake.
            using var awake = XStream.Core.Native.WindowsDisplay.KeepAwake();
            // The console sends 60 fps: run a 120/144 Hz panel at 60 for the session (steadier, uses less power).
            var (refreshChange, refreshUndo) = o.Fullscreen && !o.Headless && (o.Settings?.MatchRefreshRate ?? false)
                ? XStream.Core.Native.WindowsDisplay.MatchRefreshRate(60)
                : (null, NoUndo.Instance);
            using var _refresh = refreshUndo;
            if (refreshChange is not null) Log?.Invoke(refreshChange);
            // Wi-Fi without the once-a-minute scan spikes; game-class scheduling for this (present) thread.
            var (wifiChange, wifiUndo) = XStream.Core.Native.WindowsStreaming.QuietWifi();
            using var _wifi = wifiUndo;
            if (wifiChange is not null) Log?.Invoke(wifiChange);
            XStream.Core.Native.WindowsStreaming.RaiseProcessPriority();
            using var _mmcss = XStream.Core.Native.WindowsStreaming.GameThread();
            var logPath = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{profile.ToString().ToLowerInvariant()}.csv");
            var logger = new SessionLogger(logPath, profile.ToString());
            // Settings that can change mid-stream (in-stream menu, or the launcher's Settings page).
            AppSettings live = o.Settings ?? AppSettings.Load();
            // Set once shutdown starts. Timer and network threads can still fire for a moment after that, so
            // every callback checks it instead of touching objects that are being disposed.
            bool ending = false;
            // Set (to a reason) by the connection's callbacks when it has failed; the main loop reconnects.
            string? reconnectWhy = null;
            bool reconnecting = false;
            UnhandledExceptionEventHandler onCrash = (_, e) => Log?.Invoke("Crash: " + e.ExceptionObject);
            AppDomain.CurrentDomain.UnhandledException += onCrash;
            double Now() => cur?.NowMs ?? lastPeer?.NowMs ?? 0;

            // Video window and menu: created once, kept across reconnects (the last picture stays up).
            VideoWindow? window = null;
            FrameMailbox? mailbox = null;
            StreamOverlay? overlay = null;
            if (!o.Headless)
            {
                try
                {
                    mailbox = new FrameMailbox();
                    window = new VideoWindow(mailbox, Now, new VideoWindowOptions
                    {
                        Title = o.WindowTitle, Fullscreen = o.Fullscreen, VSync = o.VSync, StartHidden = true,
                    });
                    window.Sharpen = o.Sharpen;
                    window.Scaling = o.Scaling;
                    overlay = new StreamOverlay(o.ConsoleName, live, o.Fullscreen);
                    window.Overlay = overlay;
                    window.KeyDown = overlay.OnKey;
                    window.Click = overlay.OnClick;
                    overlay.DisconnectRequested += Stop;
                    overlay.MoreSettingsRequested += () => MoreSettingsRequested?.Invoke();
                    overlay.CameBack += () => ReturnedToGame?.Invoke();
                    var win = window; var ov0 = overlay;
                    overlay.ScreenshotRequested += () => win.RequestScreenshot((px, w, h) => Task.Run(() =>
                    {
                        try
                        {
                            var path = Screenshots.Save(px, w, h, o.ConsoleName);
                            ov0.ShowToast("Screenshot saved to Pictures › Vexel");
                            Log?.Invoke("Screenshot: " + path);
                        }
                        catch (Exception ex) { ov0.ShowToast("Couldn't save the screenshot"); Log?.Invoke("Screenshot failed: " + ex.Message); }
                    }));
                }
                catch (Exception ex)
                {
                    Log?.Invoke("No video window (" + ex.Message + "). Install FFmpeg + SDL2. Streaming without picture.");
                    window?.Dispose(); window = null; mailbox = null; overlay = null;
                }
            }
            bool closed = false;

            // Controller: read at 1 kHz on its own thread, sent the instant anything changes (to whichever
            // connection is live; nothing goes out while reconnecting).
            InputPump? pump = null;
            IGamepadBackend? backend = null;
            var padToastsFrom = DateTime.MaxValue; // set when the pump starts (+3 s: it reports the pads already plugged in)
            try
            {
                backend = GamepadBackends.CreateDefault();
                // The stream menu sees the controller first; while it's open the console gets a neutral pad.
                var ov = overlay;
                GamepadState[] neutral = [];
                void Send(GamepadState[] frame)
                {
                    var p = cur;
                    if (ov is not null && ov.OnPad(frame))
                    {
                        if (neutral.Length != frame.Length || neutral.Where((n, i) => n.Index != frame[i].Index).Any())
                        {
                            neutral = frame.Select(f => new GamepadState(f.Index, GamepadButtons.None, 0, 0, 0, 0, 0, 0)).ToArray();
                            p?.SendGamepad(neutral);
                        }
                        return;
                    }
                    neutral = [];
                    p?.SendGamepad(frame);
                }
                // When the hold opens the menu there's no new controller report (nothing changed), so tell the
                // console straight away that nothing is pressed.
                GamepadState[] lastFrame = [];
                var inner = Send;
                void Track(GamepadState[] frame) { lastFrame = frame; inner(frame); }
                if (ov is not null)
                    ov.InputCaptured += () =>
                    {
                        var f = lastFrame;
                        if (f.Length > 0 && !ending)
                        {
                            neutral = f.Select(x => new GamepadState(x.Index, GamepadButtons.None, 0, 0, 0, 0, 0, 0)).ToArray();
                            cur?.SendGamepad(neutral);
                        }
                    };
                // Controllers coming and going mid-stream: tell the console, and the player (a toast). The first
                // few seconds are the pump finding the pads already plugged in: no toasts for those.
                var pads = new HashSet<int>();
                void PadChanged(int i, bool added)
                {
                    cur?.PadConnectionChanged(i, added);
                    int count;
                    lock (pads) { if (added) pads.Add(i); else pads.Remove(i); count = pads.Count; }
                    if (ov is null || DateTime.UtcNow < padToastsFrom || ending) return;
                    if (added) ov.ShowToast(count > 1 ? $"Controller {count} connected" : "Controller connected", 2500, ToastKind.Good);
                    else if (count == 0) ov.ShowToast("Controller disconnected. Reconnect it to keep playing", 6000, ToastKind.Bad);
                    else ov.ShowToast("A controller disconnected", 3000, ToastKind.Warn);
                    Log?.Invoke($"Controller {(added ? "connected" : "disconnected")} (pad {i}); {count} connected");
                }
                // View + Menu is the stream menu's combo, so it isn't also turned into the Xbox button here (both
                // fired at 0.5 s and the menu often lost). The menu has an "Xbox button" item instead.
                pump = new InputPump(backend, new InputOptions { Redundancy = XboxPeer.UnreliableInputSupported ? 1 : 0, NexusComboMs = 0 }, Track, PadChanged);
                if (ov is not null)
                    ov.XboxButtonRequested += () =>
                    {
                        // Press and release the Xbox button on every connected pad (the menu just closed, so the
                        // console has a neutral pad; no real buttons are held).
                        var f = lastFrame;
                        if (f.Length == 0 || cur is not { } p) return;
                        p.SendGamepad(f.Select(x => new GamepadState(x.Index, GamepadButtons.Nexus, 0, 0, 0, 0, 0, 0)).ToArray());
                        _ = Task.Delay(150).ContinueWith(_ => cur?.SendGamepad(f.Select(x => new GamepadState(x.Index, GamepadButtons.None, 0, 0, 0, 0, 0, 0)).ToArray()));
                        Log?.Invoke("Xbox button pressed from the stream menu");
                    };
            }
            catch (Exception ex) { Log?.Invoke("No controller input (" + ex.Message + "). Streaming without it."); }

            // Game audio: Opus from the console -> decoder -> speakers, kept ~40 ms behind (more on Smooth).
            XStream.Core.Audio.AudioPlayer? audio = null;
            if (o.Audio && !o.Headless)
            {
                try
                {
                    audio = new XStream.Core.Audio.AudioPlayer
                    {
                        Volume = Math.Clamp(o.Volume, 0, 100) / 100f,
                        TargetMs = profile switch { StreamProfile.Competitive => 30, StreamProfile.Smooth => 80, _ => 40 },
                    };
                }
                catch (Exception ex) { Log?.Invoke("No game audio (" + ex.Message + ")."); audio = null; }
            }

            // Microphone (party chat): only open while the setting is on, so the OS mic indicator matches.
            XStream.Core.Audio.Microphone? mic = null;
            void UpdateMic(bool on)
            {
                if (o.Headless) return;
                try
                {
                    if (on && mic is null)
                    {
                        var m = new XStream.Core.Audio.Microphone();
                        m.Frame += f => { if (!ending) cur?.SendMicrophone(f); };
                        m.Start();
                        mic = m;
                        Log?.Invoke("Microphone on (" + m.DeviceInfo + ")");
                    }
                    else if (!on && mic is not null)
                    {
                        mic.Dispose();
                        mic = null;
                        Log?.Invoke("Microphone off");
                    }
                }
                catch (Exception ex)
                {
                    mic = null;
                    Log?.Invoke("Microphone: " + ex.Message);
                    overlay?.ShowToast("No microphone found");
                }
            }

            // Apply settings live: from the in-stream menu (saved here), or settings.json changed by the launcher.
            void ApplySettings(AppSettings next)
            {
                live = next;
                if (window is not null) { window.Sharpen = (int)next.Clarity; window.Scaling = next.Scaling; }
                if (audio is not null)
                {
                    audio.Volume = next.Audio ? Math.Clamp(next.Volume, 0, 100) / 100f : 0;
                    audio.TargetMs = next.Profile switch { StreamProfile.Competitive => 30, StreamProfile.Smooth => 80, _ => 40 };
                }
                cur?.Engine.Retune(next.Profile, next.MaxMbps * 1000);
                if (next.Microphone != (mic is not null)) UpdateMic(next.Microphone);
                overlay?.Sync(next);
            }
            if (overlay is not null)
                overlay.SettingsChanged += change =>
                {
                    var next = change(AppSettings.Load());
                    try { next.Save(); } catch (IOException ex) { Log?.Invoke("Couldn't save settings: " + ex.Message); }
                    ApplySettings(next);
                };
            using var watcher = WatchSettings(() =>
            {
                if (ending) return;
                var disk = AppSettings.Load();
                if (disk != live) ApplySettings(disk);
            });

            void Finish()
            {
                lock (logger)
                {
                    if (closed) return;
                    closed = true;
                    if (pump is not null) { logger.Input = pump.Stats(); pump.Dispose(); }
                    logger.RembNegotiated = lastPeer?.RembNegotiated ?? false;
                    logger.NackNegotiated = lastPeer?.NackNegotiated ?? false;
                    logger.Dispose();
                }
                Ended?.Invoke(logger.Summary, logger.CsvPath);
            }

            int tick = 0;
            bool fallbackLogged = false;
            var health = new StreamHealth();
            int batteryWarned = 101; // lowest level already announced (20, 10, 5)
            AppSettings Settings() => live;
            LinkState lastLink = LinkState.Connecting;

            void OnEngineStats(StabilitySnapshot s)
            {
                if (s.Link != lastLink)
                {
                    bool bad = s.Link is LinkState.Degraded or LinkState.IceRestarting or LinkState.Renegotiating or LinkState.NewSession;
                    if (bad && Stage == PlayerStage.Streaming) SetStage(PlayerStage.Reconnecting, $"Connection trouble: {s.Link}");
                    else if (s.Link == LinkState.Connected && Stage == PlayerStage.Reconnecting && !reconnecting) SetStage(PlayerStage.Streaming, "Back to normal");
                    lastLink = s.Link;
                }
                if (tick++ % 10 != 0) return; // once a second
                if (video?.Decoder.FallbackReason is { } why && !fallbackLogged) { fallbackLogged = true; Log?.Invoke("Video: " + why); }
                var live = new LiveStats
                {
                    TimeS = s.TimeMs / 1000, Mbps = s.ReceiveKbps / 1000, CapMbps = s.CapKbps / 1000, LossPct = s.LossPct,
                    PingMs = s.RttMs, JitterMs = s.JitterMs, BufferMs = s.BufferMs, Link = s.Link.ToString(),
                    Caps = s.Caps.ToString(), Verdict = s.Verdict,
                };
                if (pump is not null)
                {
                    var i = pump.Stats();
                    pump.ResetWindow();
                    live = live with { Pads = i.PadsConnected, PadHz = i.PollHzActual, PadP99Us = i.DetectToSendP99Us };
                }
                if (window is not null && video is not null)
                {
                    var d = window.TakeStats();
                    var (vw, vh) = window.VideoSize;
                    live = live with
                    {
                        Fps = d.Fps, NetToScreenP50Ms = d.ArrivalToPresentP50Ms, NetToScreenP99Ms = d.ArrivalToPresentP99Ms,
                        DecodeMs = video.DecodeP50Ms, VideoWidth = vw, VideoHeight = vh,
                    };
                    overlay?.UpdateStats(live, (vw, vh));
                }
                var cfg = Settings();
                if (overlay is not null && cfg.ConnectionAlerts && Stage is PlayerStage.Streaming or PlayerStage.Reconnecting && !reconnecting)
                {
                    if (health.Update(live, Stage == PlayerStage.Reconnecting, (int)cfg.Clarity) is { } toast)
                    {
                        overlay.ShowToast(toast.Text, toast.Kind == ToastKind.Good ? 3000 : 5000, toast.Kind);
                        Log?.Invoke("Alert: " + toast.Text);
                    }
                    // Low battery (ROG Ally): at 20%, 10% and 5% while unplugged.
                    if (tick % 300 == 1 && XStream.Core.Native.WindowsStreaming.Battery() is { OnBattery: true } b)
                    {
                        int level = new[] { 5, 10, 20 }.FirstOrDefault(l => b.Percent <= l);
                        if (level > 0 && level < batteryWarned)
                        {
                            batteryWarned = level;
                            overlay.ShowToast($"Battery at {b.Percent}%. Plug in to keep playing", 6000, level <= 10 ? ToastKind.Bad : ToastKind.Warn);
                        }
                    }
                    else if (tick % 300 == 1 && XStream.Core.Native.WindowsStreaming.Battery() is { OnBattery: false }) batteryWarned = 101;
                }
                Stats?.Invoke(live);
            }

            // ---- One connection: a peer on a session, with its decoder and callbacks ----------------------
            CancellationTokenSource? connCts = null;

            void Connect(StreamSession s)
            {
                var p = new XboxPeer(o.Stability, live.IceServers);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                VideoPipeline? v = null;
                if (window is not null && mailbox is not null)
                {
                    v = new VideoPipeline(p.Frames, new H264Decoder(preferHardware: o.HardwareDecode, d3d11Device: window.D3D11Device), mailbox, () => p.NowMs);
                    p.FrameQueued += () => { if (!ending && p == cur) v.Notify(); };
                    p.DecoderErrorSource = v.TakeDecodeError;
                }
                // Every callback checks it's still the live connection: a replaced one can fire while closing.
                p.Sampled += (smp, snap, pic) => { if (p != cur) return; lock (logger) if (!closed) logger.Record(smp, snap, pic); };
                var b = backend;
                if (b is not null)
                    p.Rumble += r =>
                    {
                        if (ending || p != cur) return;
                        var c = live;
                        float strength = Math.Clamp(c.RumbleStrength, 0, 100) / 100f / 100f; // packet values are percent
                        // Trigger motors are off by default: the xbox.com and Chromium-based clients only play the
                        // two main motors, and racing games send trigger rumble constantly on the throttle.
                        float lt = c.TriggerRumble ? r.lt * strength : 0, rt = c.TriggerRumble ? r.rt * strength : 0;
                        b.Rumble(r.pad, r.left * strength, r.right * strength, lt, rt, r.durationMs);
                    };
                var a = audio;
                if (a is not null) p.AudioRtp += (payload, seq) => { if (!ending && p == cur) a.Push(payload, seq); };
                p.StateChanged += st =>
                {
                    Log?.Invoke($"Connection: {st}");
                    if (st == SIPSorcery.Net.RTCPeerConnectionState.failed && p == cur && !ending) reconnectWhy ??= "the connection failed";
                };
                p.Log += m => Log?.Invoke(m);
                p.RecoveryNeeded += act =>
                {
                    if (p != cur || ending) return;
                    Log?.Invoke($"Recovery needed: {act}");
                    switch (act)
                    {
                        // No video for 8 s or more: a local ICE restart can't fix that (the console never hears
                        // about it), so get a fresh session on the same console.
                        case RecoveryAction.Renegotiate or RecoveryAction.StartNewSession:
                            reconnectWhy ??= "no video for a while";
                            break;
                        case RecoveryAction.GiveUp:
                            Log?.Invoke("The console ended the session.");
                            Stop();
                            break;
                    }
                };
                p.Stats += st =>
                {
                    if (ending || p != cur) return;
                    try { OnEngineStats(st); }
                    catch (Exception ex) { Log?.Invoke("Stats error: " + ex.Message); }
                };

                lastLink = LinkState.Connecting;
                cur = p; lastPeer = p; video = v; connCts = cts;
                p.Engine.Retune(live.Profile, live.MaxMbps * 1000);
                // The service drops a session it doesn't hear from.
                _ = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        try { await Task.Delay(30_000, cts.Token); await s.KeepaliveAsync(cts.Token); } catch { }
                    }
                });
                p.ConnectAsync(s, cts.Token).GetAwaiter().GetResult();
                v?.Start();
                Info?.Invoke(new SessionInfo
                {
                    Decoder = v is null ? "none" : $"{v.Decoder.Backend} ({H264Decoder.Version})",
                    Renderer = window?.RendererName ?? "none",
                    Controller = backend?.Name ?? "none",
                    Audio = audio?.DeviceInfo ?? "off",
                    LowLatencyInput = XboxPeer.UnreliableInputSupported,
                    Remb = p.RembNegotiated, Nack = p.NackNegotiated, LogPath = logPath, Route = p.Route,
                });
                overlay?.SetRoute(p.Route);
            }

            // Close the live connection (tells the service the session is over) and its decoder.
            void Disconnect()
            {
                var p = cur; var v = video; var cts = connCts;
                cur = null; video = null; connCts = null;
                try { cts?.Cancel(); } catch { }
                if (p is not null)
                {
                    try { p.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
                    catch (Exception ex) { Log?.Invoke("Closing the connection: " + ex.Message); }
                }
                Thread.Sleep(30); // let any callback that was already running finish
                v?.Dispose();
                cts?.Dispose();
            }

            // Keep the window responsive (menu, Disconnect) while something slow runs on another thread.
            void PumpWhile(Func<bool> waiting)
            {
                while (waiting() && !_stop.IsSet)
                {
                    Tick();
                    if (window is not null) { window.PumpOnce(8); overlay?.Apply(window); if (window.QuitRequested) Stop(); }
                    else Thread.Sleep(8);
                }
            }

            Connect(session);
            pump?.Start();
            padToastsFrom = DateTime.UtcNow.AddSeconds(3);
            UpdateMic(live.Microphone);
            _overlay = overlay;

            const string streamingText = "Streaming. Press Esc or hold {view} + {menu} for the stream menu.";
            bool firstPictureShown = false, failedToReconnect = false;
            if (window is not null)
                window.FirstPicture += () =>
                {
                    firstPictureShown = true;
                    SetStage(PlayerStage.Streaming, streamingText);
                };
            else SetStage(PlayerStage.Streaming, "Streaming (no picture)");
            if (window is not null) SetStage(PlayerStage.StartingVideo, "Waiting for the first picture");

            while (true)
            {
                // ---- Streaming: until Stop, or the connection fails ------------------------------------------
                var connectedAt = System.Diagnostics.Stopwatch.StartNew();
                long postedAtConnect = mailbox?.Posted ?? 0;
                bool awaitingPicture = window is not null;
                double lastNudge = -1;
                if (window is not null)
                {
                    window.Run(() =>
                    {
                        if (awaitingPicture)
                        {
                            // Ask for a keyframe now and every second until a picture arrives: the console's first one
                            // can be lost or sent before we were listening.
                            double t = connectedAt.Elapsed.TotalSeconds;
                            if (t - lastNudge >= 1) { lastNudge = t; cur?.RequestKeyframe(); }
                            if (mailbox!.Posted > postedAtConnect)
                            {
                                awaitingPicture = false;
                                Log?.Invoke($"First picture {t:F2} s after connecting");
                            }
                            else if (t > 10 && !firstPictureShown)
                            {
                                // Safety net: show the window anyway (menu, Disconnect) and say what's going on.
                                firstPictureShown = true;
                                window.Show();
                                Log?.Invoke("No picture 10 s after connecting; showing the window anyway");
                                SetStage(PlayerStage.Streaming, "Connected, waiting for the picture. " + streamingText);
                            }
                        }
                        overlay?.Apply(window);
                        Tick();
                        return !_stop.IsSet && reconnectWhy is null;
                    });
                    if (window.QuitRequested) Stop();
                }
                else
                {
                    while (!_stop.Wait(2) && reconnectWhy is null && TickTrue())
                        while (cur is { } p && p.Frames.TryDequeue(p.NowMs, out _)) { } // keep the buffer drained
                }
                if (_stop.IsSet || reconnectWhy is null) break;

                // ---- Reconnecting: same window, menu and controller; new session and connection --------------
                string why = reconnectWhy;
                reconnecting = true;
                Log?.Invoke($"Connection lost ({why}); reconnecting");
                SetStage(PlayerStage.Reconnecting, "Connection lost, reconnecting…");
                overlay?.SetReconnecting("Connection lost. Getting it back…");
                if (!firstPictureShown) { firstPictureShown = true; window?.Show(); }
                var closing = Task.Run(Disconnect);
                PumpWhile(() => !closing.IsCompleted);
                closing.Wait();

                bool ok = false;
                for (int attempt = 1; attempt <= ReconnectAttempts && !_stop.IsSet && !ok; attempt++)
                {
                    int wait = ReconnectBackoffMs[Math.Min(attempt - 1, ReconnectBackoffMs.Length - 1)];
                    if (wait > 0)
                    {
                        overlay?.SetReconnecting($"Trying again in {wait / 1000} s (attempt {attempt} of {ReconnectAttempts})");
                        var until = DateTime.UtcNow.AddMilliseconds(wait);
                        PumpWhile(() => DateTime.UtcNow < until);
                        if (_stop.IsSet) break;
                    }
                    overlay?.SetReconnecting($"Attempt {attempt} of {ReconnectAttempts}");
                    var n = attempt;
                    var task = Task.Run(() =>
                    {
                        var c = refreshClient is null ? client : refreshClient().GetAwaiter().GetResult();
                        session = OpenSession(c, quiet: true);
                        Connect(session);
                    });
                    PumpWhile(() => !task.IsCompleted);
                    try { task.Wait(); ok = true; }
                    catch (Exception ex)
                    {
                        var inner = ex is AggregateException ag ? ag.InnerException ?? ex : ex;
                        Log?.Invoke($"Reconnect attempt {n} failed: {Friendly(inner)}");
                        if (cur is not null) { var c2 = Task.Run(Disconnect); PumpWhile(() => !c2.IsCompleted); }
                        if (_stop.IsSet) break;
                    }
                }
                reconnecting = false;
                reconnectWhy = null;
                overlay?.SetReconnecting(null);
                if (_stop.IsSet) break;
                if (!ok) { failedToReconnect = true; break; }
                Log?.Invoke("Reconnected");
                overlay?.ShowToast("Reconnected", 3000, ToastKind.Good);
                health = new StreamHealth();
                SetStage(PlayerStage.Streaming, "Reconnected. " + streamingText);
            }

            InMainLoop = false; // shutting down can block for a few seconds (telling the service): not a freeze
            SetStage(PlayerStage.Stopping, "Ending the session");
            // Order matters: stop everything that calls in (connection timers, packets, rumble) before
            // disposing what they call into. Disposing the controller/video first crashed the process on Esc.
            ending = true;
            _cts.Cancel();
            Disconnect(); // also tells the service to end the session
            Finish();
            mic?.Dispose();
            audio?.Dispose();
            window?.Dispose();
            backend?.Dispose();
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
            if (failedToReconnect)
            {
                SetStage(PlayerStage.Failed, $"Lost the connection to {o.ConsoleName} and couldn't get it back after {ReconnectAttempts} tries. Check the network, then stream again.");
                return 1;
            }
            SetStage(PlayerStage.Ended, "Session ended");
            return 0;
        }
        catch (Exception ex) when (ex is OperationCanceledException && _stop.IsSet)
        {
            Cleanup(session, cur);
            video?.Dispose();
            SetStage(PlayerStage.Ended, "Cancelled");
            return 0;
        }
        catch (Exception ex)
        {
            Cleanup(session, cur);
            video?.Dispose();
            SetStage(PlayerStage.Failed, Friendly(ex));
            return 1;
        }
        finally { _cts.Dispose(); }
    }

    private static void Cleanup(StreamSession? session, XboxPeer? peer)
    {
        try
        {
            if (peer is not null) peer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            else session?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch { /* best effort */ }
    }

    private void SetStage(PlayerStage stage, string message)
    {
        Stage = stage;
        StageChanged?.Invoke(stage, message);
    }

    /// <summary>Plain-language version of the usual failures.</summary>
    public static string Friendly(Exception ex) => ex switch
    {
        XboxApiException { Status: 401 } a when a.Message.StartsWith("Not signed in") => "You're not signed in. Sign in first.",
        XboxApiException { Status: 401 or 403 } => "Your sign-in has expired. Sign in again.",
        XboxApiException a when a.Message.Contains("Session failed") =>
            "The console refused the session. Check that remote features are on and power mode is Sleep, and that nobody else is streaming from it.",
        XboxApiException a => "Xbox service error: " + a.Message,
        XStream.WebRtc.ConsoleUnreachableException u => u.Message,
        TimeoutException t => "The console didn't answer in time (" + t.Message + "). Is it on or in Sleep mode?",
        HttpRequestException h => "Network error: " + h.Message,
        _ => ex.Message,
    };
}

internal sealed class NoUndo : IDisposable
{
    public static readonly NoUndo Instance = new();
    public void Dispose() { }
}
