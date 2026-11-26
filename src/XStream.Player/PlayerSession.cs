using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Stability;
using XStream.Core.Video;
using XStream.Core.Xbox;
using XStream.WebRtc;

namespace XStream.Player;

/// <summary>A window's client area in screen coordinates ("x,y,w,h" between the launcher and the stream process).</summary>
public readonly record struct WindowPlace(int X, int Y, int W, int H)
{
    public override string ToString() => FormattableString.Invariant($"{X},{Y},{W},{H}");
    public static WindowPlace? Parse(string? s)
    {
        var p = s?.Split(',');
        if (p is not { Length: 4 }) return null;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(p[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return null;
        return n[2] > 0 && n[3] > 0 ? new WindowPlace(n[0], n[1], n[2], n[3]) : null;
    }
}

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
    /// <summary>Connection check: no controller either (nothing reaches the console), and Ended after this many seconds of streaming.</summary>
    public int CheckSeconds { get; init; }
    public string WindowTitle { get; init; } = "Vexel";
    /// <summary>The launcher window's client area (screen coordinates) to open the video window over; null: centred.</summary>
    public WindowPlace? Place { get; init; }
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
    public bool Enhance { get; init; } = true;
    public VideoScaling Scaling { get; init; } = VideoScaling.Fit;
    /// <summary>The full settings the session started with (the in-stream menu shows and edits them).</summary>
    public AppSettings? Settings { get; init; }

    public static PlayerOptions From(AppSettings s, string serverId) => new()
    {
        ServerId = serverId, Stability = s.ToStabilityOptions(), Fullscreen = s.Fullscreen, VSync = s.VSync,
        HardwareDecode = s.HardwareDecode, RumbleStrength = s.RumbleStrength, TriggerRumble = s.TriggerRumble,
        ShowStatsHud = s.ShowStatsHud, Audio = s.Audio, Volume = s.Volume, Sharpen = (int)s.Clarity, Enhance = s.Enhance, Scaling = s.Scaling, Settings = s,
    };
}

/// <summary>
/// One play session: start the console session, connect WebRTC, run video + controller, log, and clean
/// up. <see cref="Run"/> blocks and must be called on the main thread (macOS needs the video window
/// there). <see cref="Stop"/> can be called from any thread.
/// </summary>
public sealed class PlayerSession
{
    /// <summary>Where the video window was when the session ended (null: full screen or none): the launcher reopens there.</summary>
    public WindowPlace? LastWindow { get; private set; }

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

    /// <summary>Remote control of the streamed console (typing, Turn off); null for cloud or when unavailable.</summary>
    public ConsoleRemote? Remote { get; set; }
    /// <summary>The game running on the console now (Xbox title id, null if none): its controller profile is used.</summary>
    public Func<CancellationToken, Task<string?>>? NowPlaying { get; set; }
    /// <summary>Watches for achievements unlocked during the stream (null: no pop-ups).</summary>
    public AchievementWatcher? Achievements { get; set; }
    /// <summary>"End and turn off Xbox" was chosen: the console is switched off after the stream closes.</summary>
    public bool TurnOffAfter { get; private set; }

    public void Stop()
    {
        _stop.Set();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>How many times a dropped connection is retried before the stream gives up.</summary>
    public const int ReconnectAttempts = 4;
    private static readonly int[] ReconnectBackoffMs = [0, 2_000, 5_000, 10_000];
    /// <summary>
    /// Tries at starting a stream before giving up. A console just woken from Sleep often turns the first session
    /// down, or its first connection times out, and pressing Stream again worked: live on a ROG Ally it took
    /// several presses. Now Vexel presses again itself, while the connecting screen stays up.
    /// </summary>
    public const int StartAttempts = 4;
    private static readonly int[] StartBackoffMs = [0, 2_000, 4_000, 6_000];

    /// <summary>An interface other than loopback is up with an address (Wi-Fi has rejoined after sleep).</summary>
    private static bool NetworkUp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                && n.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                && n.GetIPProperties().GatewayAddresses.Count > 0);
        }
        catch { return true; }
    }

    /// <summary>Worth trying again: not a sign-in problem, and not the user cancelling.</summary>
    private bool Retryable(Exception ex) =>
        !_stop.IsSet && ex is not OperationCanceledException && ex is not XboxApiException { Status: 401 or 403 };

    private bool WaitForRetry(int attempt, string what)
    {
        int ms = StartBackoffMs[Math.Min(attempt, StartBackoffMs.Length - 1)];
        SetStage(PlayerStage.WakingConsole, $"{what} (try {attempt + 1} of {StartAttempts})");
        return !_stop.Wait(ms);
    }

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
            for (int attempt = 1; ; attempt++)
            {
                try { session = OpenSession(client, quiet: false); break; }
                catch (Exception ex) when (attempt < StartAttempts && Retryable(ex))
                {
                    Log?.Invoke($"Start attempt {attempt} failed: {Friendly(ex)}");
                    if (!WaitForRetry(attempt, o.IsCloud ? "The cloud server isn't ready yet, trying again" : "The console is still waking up, trying again")) throw;
                    if (refreshClient is not null) client = refreshClient().GetAwaiter().GetResult();
                }
            }

            SetStage(PlayerStage.Connecting, "Connecting video and controller");
            // A controller-only session looks idle to Windows: keep the screen on and the device awake.
            using var awake = XStream.Core.Native.WindowsDisplay.KeepAwake();
            // The console sends 60 fps: run a 120/144 Hz panel at 60 for the session (steadier, uses less power).
            var (refreshChange, refreshUndo) = o.Fullscreen && !o.Headless && (o.Settings?.MatchRefreshRate ?? false)
                ? XStream.Core.Native.WindowsDisplay.MatchRefreshRate(60)
                : (null, NoUndo.Instance);
            using var _refresh = refreshUndo;
            // Wi-Fi without the once-a-minute scan spikes; game-class scheduling for this (present) thread.
            var (wifiChange, wifiUndo) = XStream.Core.Native.WindowsStreaming.QuietWifi();
            using var _wifi = wifiUndo;
            XStream.Core.Native.WindowsStreaming.RaiseProcessPriority();
            using var _mmcss = XStream.Core.Native.WindowsStreaming.GameThread();
            var logPath = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{profile.ToString().ToLowerInvariant()}.csv");
            var logger = new SessionLogger(logPath, profile.ToString());
            // The session's text log (connection, decoder, renderer and timing lines) next to its .csv, so a diagnostics
            // export carries it. These lines never contain sign-in tokens.
            var textLog = new StreamWriter(Path.ChangeExtension(logPath, ".log"), append: false) { AutoFlush = true };
            Action<string> toFile = m => { lock (textLog) { try { textLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {m}"); } catch { } } };
            Log += toFile;
            // Logged only now, so they reach the session's log file (they used to be written before it was open).
            if (refreshChange is not null) Log?.Invoke(refreshChange);
            Log?.Invoke(wifiChange ?? "Wi-Fi: no connected Wi-Fi adapter to tune");
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

            // Instant replay: the last 30 s as they arrived, for "Save last 30 seconds".
            var replay = live.InstantReplay && !o.Headless ? new ReplayBuffer(Math.Clamp(live.ReplaySeconds, 10, 120)) : null;

            // The device slept (power button, lid): the connection is gone even if it doesn't know yet. Reconnect
            // on wake straight away, once the network is back, instead of waiting for it to time out.
            bool woke = false;
            new Thread(() =>
            {
                var sleep = new SuspendDetector();
                while (!_stop.Wait(1000) && !ending)
                {
                    if (sleep.Observe(DateTime.UtcNow) is not { } away) continue;
                    Log?.Invoke($"Device was asleep for {away.TotalSeconds:F0} s; reconnecting");
                    if (cur is not null && !reconnecting) { woke = true; reconnectWhy ??= "the device was asleep"; }
                }
            }) { IsBackground = true, Name = "xstream-sleep" }.Start();

            // Video window and menu: created once, kept across reconnects (the last picture stays up).
            VideoWindow? window = null;
            FrameMailbox? mailbox = null;
            StreamOverlay? overlay = null;
            string streamRoute = "";
            KeyboardMouseInput? kbm = null;
            if (!o.Headless)
            {
                try
                {
                    mailbox = new FrameMailbox();
                    window = new VideoWindow(mailbox, Now, new VideoWindowOptions
                    {
                        Title = o.WindowTitle, Fullscreen = o.Fullscreen, VSync = o.VSync, StartHidden = true,
                        X = o.Place?.X, Y = o.Place?.Y,
                        Width = o.Place is { W: >= 320 } p ? p.W : 1280, Height = o.Place is { H: >= 200 } q ? q.H : 720,
                    });
                    window.Sharpen = o.Sharpen;
                    window.Enhance = o.Enhance;
                    window.Scaling = o.Scaling;
                    var remote = o.IsCloud ? null : Remote;
                    overlay = new StreamOverlay(o.ConsoleName, live, o.Fullscreen, consoleControl: remote is not null, clips: replay is not null);
                    window.Overlay = overlay;
                    window.KeyDown = overlay.OnKey;
                    window.KeyUp = overlay.OnKeyUp;
                    window.Click = overlay.OnClick;
                    overlay.DisconnectRequested += Stop;
                    overlay.MoreSettingsRequested += () => MoreSettingsRequested?.Invoke();
                    overlay.CameBack += () => ReturnedToGame?.Invoke();
                    var win = window; var ov0 = overlay;
                    // Keyboard and mouse as a controller: pauses while the menu / typing / launcher has the input.
                    // While it's in use, typed letters are game keys: typing to the console is from the menu only.
                    var km = kbm = KeyboardMouseInput.FromSettings(live);
                    km.Blocked = () => ov0.HoldsInput;
                    km.NativeKeys = keys => cur?.SendKeys(keys);
                    ov0.EscGoesToGame = () => km.Native && km.Captured && km.Active;
                    ov0.EscTapped += () => { cur?.SendKeys([(0x1B, true)]); _ = Task.Delay(60).ContinueWith(_ => cur?.SendKeys([(0x1B, false)])); };
                    km.NativeMouse = (dx, dy, wx, wy, b) => cur?.SendMouse(dx, dy, wx, wy, b);
                    km.Hint += () => ov0.ShowToast(km.Native ? "Keyboard and mouse: click the picture to play. Hold Esc for the menu" : "Keyboard and mouse: click the picture to play. Esc for the menu", 5000);
                    window.Input = km;
                    window.TextInput = t => { if (!km.Active || ov0.Typing) ov0.OnText(t); };
                    if (replay is not null)
                        overlay.SaveClipRequested += () => Task.Run(() =>
                        {
                            try
                            {
                                var path = Clips.NewPath(o.ConsoleName);
                                double secs = replay.SaveTo(path);
                                if (secs <= 0) { File.Delete(path); ov0.ShowToast("Nothing to save yet", 2500, ToastKind.Warn); return; }
                                ov0.ShowToast($"Saved the last {secs:F0} s to Videos › Vexel");
                                Log?.Invoke($"Clip: {path} ({secs:F1} s, {new FileInfo(path).Length / 1e6:F1} MB)");
                            }
                            catch (Exception ex) { ov0.ShowToast("Couldn't save the clip", 3000, ToastKind.Bad); Log?.Invoke("Clip failed: " + ex.Message); }
                        });
                    if (remote is not null)
                    {
                        remote.Log += m => Log?.Invoke(m);
                        remote.Problem += m => ov0.ShowToast(m, 5000, ToastKind.Bad);
                        overlay.TextTyped += (text, done) =>
                        {
                            remote.Type(text);
                            if (done) ov0.ShowToast("Sent to the console", 1500);
                        };
                        overlay.TouchKeyboardWanted += XStream.Core.Native.WindowsTouchKeyboard.Show;
                        overlay.TurnOffRequested += () => { TurnOffAfter = true; Stop(); };
                        _ = remote.IdAsync(); // look the console up now, so the first keystroke goes straight out
                    }
                    // XSTREAM_SCREENSHOT=file.png;seconds[|file2.png;seconds] (testing): capture what the renderer shows, to check it.
                    foreach (var shot in (Environment.GetEnvironmentVariable("XSTREAM_SCREENSHOT") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                        if (shot.Split(';') is [var shotPath, var shotAt])
                            _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(shotAt, System.Globalization.CultureInfo.InvariantCulture)))
                                .ContinueWith(_ => win.RequestScreenshot((px, w, h) =>
                                {
                                    Screenshots.SaveTo(px, w, h, shotPath);
                                    Log?.Invoke($"Screenshot {w}x{h}: {shotPath}");
                                }));
                    // XSTREAM_TEST_KEYS=5B@6,1B@14 (testing): press these keys (hex Windows virtual-key codes) on the console's
                    // native keyboard at these seconds, to check the console takes keyboard input.
                    foreach (var key in (Environment.GetEnvironmentVariable("XSTREAM_TEST_KEYS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                        if (key.Split('@') is [var vkHex, var at])
                        {
                            byte vk = Convert.ToByte(vkHex, 16);
                            _ = Task.Delay(TimeSpan.FromSeconds(double.Parse(at, System.Globalization.CultureInfo.InvariantCulture))).ContinueWith(async _ =>
                            {
                                cur?.SendKeys([(vk, true)]);
                                await Task.Delay(120);
                                cur?.SendKeys([(vk, false)]);
                                Log?.Invoke($"Test: pressed native key 0x{vk:X2}");
                            });
                        }
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
                    window?.Dispose(); window = null; mailbox = null; overlay = null; kbm = null;
                }
            }
            bool closed = false;

            // Controller: read at 1 kHz on its own thread, sent the instant anything changes (to whichever
            // connection is live; nothing goes out while reconnecting).
            InputPump? pump = null;
            // Microphone controls: F9 or the mic button mutes (open mic) or is held to talk (push to talk); the
            // stream menu mutes it while open.
            bool micMuted = false, micKeyHeld = false, micButtonHeld = false;
            double micTalkUntil = 0;
            void MicPress(bool down, bool fromButton)
            {
                if (fromButton) micButtonHeld = down; else micKeyHeld = down;
                if (live.PushToTalk || !down || !live.Microphone) return;
                micMuted = !micMuted;
                overlay?.ShowToast(micMuted ? "Microphone muted" : "Microphone on", 1500, micMuted ? ToastKind.Warn : ToastKind.Good);
                Log?.Invoke(micMuted ? "Microphone muted" : "Microphone unmuted");
            }
            bool MicGated() => (overlay?.MenuOpen ?? false) || (live.PushToTalk ? !(micKeyHeld || micButtonHeld) : micMuted);
            string? gameKey = null;                       // the game running now (for its controller profile)
            ControllerProfile activeController = live.DefaultController;
            // Use the profile for the current game (if it changed, or the profiles were edited in the launcher).
            void ApplyController(bool announce)
            {
                var want = live.ControllerFor(gameKey);
                if (pump is null || (want.SameSetup(activeController) && want.GameKey == activeController.GameKey)) return;
                bool changed = !want.SameSetup(activeController);
                activeController = want;
                pump.SetController(ControllerMapping.Parse(want.ButtonMapping), want.GyroAim ? new GyroAim(want.GyroSensitivity) : null, want.GyroHoldButton);
                Log?.Invoke($"Controller profile: {want.GameName}: {want.Summary}");
                if (announce && changed) overlay?.ShowToast(want.GameKey.Length == 0 ? "Controller: your default layout" : $"Controller: {want.GameName} layout", 3000);
            }
            IGamepadBackend? backend = null;
            var padToastsFrom = DateTime.MaxValue; // set when the pump starts (+3 s: it reports the pads already plugged in)
            if (o.CheckSeconds <= 0)
            try
            {
                // Controller setup: the game's own profile if it has one (cloud: known now; console: from presence,
                // looked up below and every 20 s), else the default. Gyro or paddles anywhere -> the SDL2 backend.
                gameKey = o.IsCloud ? "cloud:" + o.CloudTitleId : null;
                activeController = live.ControllerFor(gameKey);
                var mapping = ControllerMapping.Parse(activeController.ButtonMapping, out var mapProblems);
                if (mapProblems.Count > 0) Log?.Invoke("Controller mapping: ignored " + string.Join(", ", mapProblems));
                backend = GamepadBackends.CreateDefault(needExtras: live.NeedsControllerExtras);
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
                pump = new InputPump(backend, new InputOptions
                {
                    Redundancy = XboxPeer.UnreliableInputSupported ? 1 : 0, NexusComboMs = 0,
                    Mapping = mapping, Gyro = activeController.GyroAim ? new GyroAim(activeController.GyroSensitivity) : null,
                    GyroHold = activeController.GyroHoldButton, KeyboardMouse = kbm, MicButton = live.MicButton,
                }, Track, PadChanged) { MicButtonChanged = held => MicPress(held, fromButton: true) };
                Log?.Invoke($"Controller: {backend.Name}, {activeController.GameName}: {activeController.Summary}");
                // Press and release the Xbox button on every connected pad (the menu just closed, so the console has
                // a neutral pad; no real buttons are held).
                void PressXbox()
                {
                    var f = lastFrame;
                    if (f.Length == 0) f = [new GamepadState(0, GamepadButtons.None, 0, 0, 0, 0, 0, 0)];
                    if (cur is not { } p) return;
                    p.SendGamepad(f.Select(x => new GamepadState(x.Index, GamepadButtons.Nexus, 0, 0, 0, 0, 0, 0)).ToArray());
                    _ = Task.Delay(150).ContinueWith(_ => cur?.SendGamepad(f.Select(x => new GamepadState(x.Index, GamepadButtons.None, 0, 0, 0, 0, 0, 0)).ToArray()));
                }
                if (ov is not null)
                {
                    ov.XboxButtonRequested += () => { PressXbox(); Log?.Invoke("Xbox button pressed from the stream menu"); };
                    // Party chat runs on the console: open its guide (Parties & chats is there) and say how Discord joins in.
                    ov.PartyRequested += () =>
                    {
                        PressXbox();
                        ov.ShowToast((live.Microphone ? "" : "Turn the microphone on in this menu to talk. ")
                            + "Guide open: Parties & chats is there. Discord: join a voice channel in the Discord app and choose Transfer to Xbox", 9000);
                        Log?.Invoke("Party: opened the console guide");
                    };
                }
            }
            catch (Exception ex) { Log?.Invoke("No controller input (" + ex.Message + "). Streaming without it."); }
            if (overlay is not null) overlay.MicKey += down => MicPress(down, fromButton: false);

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
            string micDevice = "";
            void UpdateMic(bool on)
            {
                if (o.Headless) return;
                try
                {
                    if (on && mic is null)
                    {
                        var m = new XStream.Core.Audio.Microphone();
                        m.Frame += f => { if (!ending && !MicGated()) cur?.SendMicrophone(f); };
                        m.Start(live.MicDevice);
                        micDevice = live.MicDevice;
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
                if (window is not null) { window.Sharpen = (int)next.Clarity; window.Enhance = next.Enhance; window.Scaling = next.Scaling; }
                if (audio is not null)
                {
                    audio.Volume = next.Audio ? Math.Clamp(next.Volume, 0, 100) / 100f : 0;
                    audio.TargetMs = next.Profile switch { StreamProfile.Competitive => 30, StreamProfile.Smooth => 80, _ => 40 };
                }
                cur?.Engine.Retune(next.Profile, next.MaxMbps * 1000);
                if (next.Microphone && mic is not null && next.MicDevice != micDevice) UpdateMic(false); // another microphone chosen
                if (next.Microphone != (mic is not null)) UpdateMic(next.Microphone);
                pump?.SetMicButton(next.MicButton);
                overlay?.Sync(next);
                kbm?.Configure(next);
                ApplyController(announce: true);
            }
            // Console streams: which game is running (presence), every 20 s, so its controller profile follows it.
            if (!o.IsCloud && NowPlaying is { } nowPlaying)
                new Thread(() =>
                {
                    while (!_stop.Wait(gameKey is null ? 5_000 : 20_000) && !ending)
                    {
                        try
                        {
                            var key = nowPlaying(_cts.Token).GetAwaiter().GetResult() ?? "";
                            if (key == (gameKey ?? "") && gameKey is not null) continue;
                            gameKey = key;
                            if ((live.GameProfiles?.Length ?? 0) > 0) Log?.Invoke($"Game now: {(key.Length > 0 ? key : "none")}");
                            ApplyController(announce: true);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { /* presence is best effort */ }
                        catch (OperationCanceledException) { return; }
                    }
                }) { IsBackground = true, Name = "xstream-game" }.Start();
            // Achievements unlocked during the stream pop up over the game (console: the title above; cloud: looked up).
            if (Achievements is { } achievements && overlay is { } achOverlay)
                achievements.Start(o.IsCloud ? null : () => gameKey, () => live.AchievementAlerts && !ending,
                    a => achOverlay.ShowToast(a.UnlockText, 6000, ToastKind.Good), m => Log?.Invoke(m), _cts.Token);
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
            // What the stream costs this device (allocations, GC pauses, CPU, per-packet time); summary at the end.
            var profiler = new StreamProfiler();
            string? profileMode = Environment.GetEnvironmentVariable("XSTREAM_PROFILE");
            bool profileEachSecond = profileMode is "1" or "true" or "alloc";
            if (profileMode == "alloc") profiler.TrackAllocationTypes();
            var health = new StreamHealth();
            var advisor = new ConnectionAdvisor(); // connection tips from this stream's own numbers
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
                if (overlay is not null)
                {
                    // Mic indicator (10 times a second): muted / hold to talk, or lit while you talk.
                    var m = mic;
                    bool gated = MicGated();
                    if (m is not null && !gated && m.Level > 0.06f) micTalkUntil = Now() + 300;
                    overlay.SetMic(m is not null, gated, m is not null && !gated && Now() < micTalkUntil, Settings().PushToTalk);
                }
                if (tick++ % 10 != 0) return; // once a second
                var prof = profiler.Tick();
                if (profileEachSecond)
                    Log?.Invoke($"profile {prof.TimeS:F0}s: alloc {prof.AllocMBps:F1} MB/s, GC {prof.Gen0}/{prof.Gen1}/{prof.Gen2} pause {prof.GcPauseMsPerS:F2} ms, " +
                                $"CPU {prof.CpuPct:F1}%, {prof.PacketsPerS:F0} pkt/s, ours {prof.OurUsPerPacket:F1} us/pkt");
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
                        AudioQueuedMs = audio?.QueuedMs ?? 0,
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
                // Keyboard and mouse, every 10 s while in use: is mouse movement arriving, and moving the stick?
                if (kbm is { } kk && tick % 100 == 1)
                {
                    var (ev, counts, peak) = kk.TakeMouseStats();
                    if (kk.Active && (kk.Captured || ev > 0))
                        Log?.Invoke($"Keyboard and mouse: {(kk.Captured ? "captured" : "not captured")}, {ev} mouse moves, {counts} counts while captured, right stick up to {peak:0.00}");
                }
                if (Stage == PlayerStage.Streaming && !reconnecting && s.Link == LinkState.Connected && o.CheckSeconds <= 0)
                {
                    if (advisor.Tick(new XStream.Core.Diagnostics.CheckSample(live.Mbps, live.CapMbps, live.LossPct, live.PingMs, live.JitterMs), streamRoute))
                    {
                        Log?.Invoke("Connection tips: " + string.Join("; ", advisor.Latest!.Findings.Where(f => f.Level != XStream.Core.Diagnostics.CheckLevel.Good).Select(f => f.Title)));
                        if (cfg.ConnectionAlerts) overlay?.ShowToast("Connection could be better: tips in the stream menu", 6000, ToastKind.Warn);
                    }
                    overlay?.SetTips(advisor.Latest);
                }
                Stats?.Invoke(live);
            }

            // ---- One connection: a peer on a session, with its decoder and callbacks ----------------------
            CancellationTokenSource? connCts = null;

            void Connect(StreamSession s)
            {
                var p = new XboxPeer(o.Stability, live.IceServers) { PacketTimed = profiler.Packet };
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                VideoPipeline? v = null;
                if (window is not null && mailbox is not null)
                {
                    v = new VideoPipeline(p.Frames, VideoDecoder.Create(o.HardwareDecode, window.D3D11Device), mailbox, () => p.NowMs);
                    if (replay is not null) { replay.Clear(); v.Tap = au => replay.AddVideo(au.Data, au.IsKeyframe); }
                    if (int.TryParse(Environment.GetEnvironmentVariable("XSTREAM_LATENCY_TEST"), out int presses) && presses > 0)
                    {
                        var probe = new LatencyProbe(() => p.NowMs, p.SendGamepad, presses);
                        v.PictureDecoded = probe.OnPicture;
                        probe.Start(m => Log?.Invoke(m));
                    }
                    var win = window;
                    v.FormatChanged = f => { win.Format = f; Log?.Invoke("Video stream: " + f); };
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
                if (replay is not null) p.AudioRtp += (payload, _) => { if (!ending && p == cur) replay.AddAudio(payload); };
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
                    Decoder = v is null ? "none" : $"{v.Decoder.Backend} ({v.Decoder.Version})",
                    Renderer = window?.RendererName ?? "none",
                    Controller = backend?.Name ?? "none",
                    Audio = audio is null ? "off" : $"{audio.DeviceInfo}, {audio.Decoder}",
                    LowLatencyInput = XboxPeer.UnreliableInputSupported,
                    Remb = p.RembNegotiated, Nack = p.NackNegotiated, LogPath = logPath, Route = p.Route,
                });
                overlay?.SetRoute(p.Route);
                streamRoute = p.Route;
            }

            // Close the live connection (tells the service the session is over) and its decoder.
            void Disconnect()
            {
                var p = cur; var v = video; var cts = connCts;
                cur = null; video = null; connCts = null;
                if (v is not null) Log?.Invoke(v.Summary);
                if (v is not null && window is not null) Log?.Invoke(window.RenderSummary);
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

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (attempt > 1) session = OpenSession(client, quiet: true);
                    Connect(session);
                    break;
                }
                catch (Exception ex) when (attempt < StartAttempts && Retryable(ex))
                {
                    Log?.Invoke($"Connect attempt {attempt} failed: {Friendly(ex)}");
                    if (cur is not null) Disconnect(); else { try { session.StopAsync().Wait(TimeSpan.FromSeconds(5)); } catch { } }
                    if (!WaitForRetry(attempt, "Couldn't connect yet, trying again")) throw;
                    try { if (refreshClient is not null) client = refreshClient().GetAwaiter().GetResult(); }
                    catch (Exception rex) when (Retryable(rex)) { Log?.Invoke("Sign-in refresh failed: " + Friendly(rex)); }
                }
            }
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
                        // Instant replay needs a keyframe every few seconds (a clip has to start on one).
                        if (!awaitingPicture && replay is not null && replay.ShouldAskKeyframe()) cur?.RequestKeyframe();
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
                if (woke)
                {
                    // Just woken: Wi-Fi takes a few seconds to rejoin. Wait for it (up to 30 s) rather than spend
                    // the reconnect attempts on a network that isn't there yet.
                    woke = false;
                    overlay?.SetReconnecting("Waking up. Waiting for the network…");
                    var until = DateTime.UtcNow.AddSeconds(30);
                    PumpWhile(() => DateTime.UtcNow < until && !NetworkUp());
                    Log?.Invoke(NetworkUp() ? "Network is back after sleep" : "Network still down 30 s after sleep; trying anyway");
                }

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
            Log?.Invoke(profiler.Summary());
            if (profiler.TopAllocations() is { } top) Log?.Invoke(top);
            if (audio is not null)
                Log?.Invoke($"Audio: {audio.PacketsDecoded} packets played; lost packets {audio.PacketsRecovered} rebuilt (FEC), {audio.PacketsConcealed} concealed, {audio.PacketsSkipped} skipped; " +
                             $"{audio.CaughtUpMs:F0} ms of built-up lag removed, {audio.Resyncs} resets");
            audio?.Dispose();
            LastWindow = window?.WindowedRect is { } r ? new WindowPlace(r.X, r.Y, r.W, r.H) : null;
            kbm?.Release(); // the mouse back to the desktop
            window?.Dispose();
            backend?.Dispose();
            AppDomain.CurrentDomain.UnhandledException -= onCrash;
            Log -= toFile;
            lock (textLog) textLog.Dispose();
            if (TurnOffAfter && Remote is { } rem)
            {
                SetStage(PlayerStage.Stopping, $"Turning off {o.ConsoleName}");
                bool off = false;
                try { var turning = rem.TurnOffAsync(); off = turning.Wait(TimeSpan.FromSeconds(10)) && turning.Result; } catch { }
                SetStage(PlayerStage.Ended, off ? $"Session ended. {o.ConsoleName} is turning off." : $"Session ended. Couldn't turn off {o.ConsoleName}: check that remote features are on.");
                return 0;
            }
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
