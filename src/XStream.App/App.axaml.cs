using XStream.Player;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XStream.App.Services;
using XStream.App.ViewModels;
using XStream.App.Views;

namespace XStream.App;

public partial class App : Application
{
    private XboxService? _xbox;
    private GamepadNavigator? _pad;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            if (NativeBundle.IsReady) StartMain(desktop);
            else ShowSetup(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>First launch of this version (single-file Windows build): unpack SDL2/FFmpeg with a progress window.</summary>
    private void ShowSetup(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashWindow();
        desktop.MainWindow = splash;
        splash.Opened += async (_, _) =>
        {
            var progress = new Progress<(double fraction, string what)>(p => splash.Report(p.fraction, p.what));
            try { await Task.Run(() => NativeBundle.EnsureExtracted(progress)); }
            catch (Exception ex)
            {
                splash.ShowError($"Couldn't unpack the video and controller libraries to {NativeBundle.Location}: {ex.Message} " +
                                 "Check there's about 120 MB free, then open Vexel again.");
                return;
            }
            var main = StartMain(desktop); // becomes the main window before the splash closes, so the app keeps running
            main.Show();
            splash.Close();
        };
    }

    private Window StartMain(IClassicDesktopStyleApplicationLifetime desktop)
    {
        {
            _xbox = new XboxService();
            var settings = new SettingsStore();
            var launcher = new StreamLauncher();
            var vm = new MainViewModel(_xbox, settings, launcher);
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            // Controller navigation (paused while streaming: the stream reads the pad itself).
            _pad = new GamepadNavigator(window);
            _pad.Back += vm.Back;
            _pad.ConnectedChanged += () => vm.Controllers = _pad.Connected;
            _pad.SwitchPage += vm.SwitchPage;
            _pad.Start += () => { if (vm.IsSignedIn && vm.CurrentPage == vm.Consoles) vm.Consoles.StreamDefault(); };
            _pad.Refresh += () =>
            {
                if (vm.IsSignedIn && vm.CurrentPage == vm.Consoles) vm.Consoles.RefreshCommand.Execute(null);
                else if (vm.IsSignedIn && vm.CurrentPage == vm.Friends) vm.Friends.RefreshCommand.Execute(null);
                else if (vm.CurrentPage == vm.SettingsPage) { vm.SettingsPage.Section = "System"; vm.SettingsPage.RunChecksCommand.Execute(null); }
            };
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.IsSignedIn) or nameof(MainViewModel.CurrentPage)) _pad.FocusDefaultSoon();
            };
            vm.SignIn.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SignInViewModel.State)) _pad.FocusDefaultSoon(); };
            vm.Consoles.Consoles.CollectionChanged += (_, _) => _pad.FocusDefaultSoon();
            // The launcher uses the controller when no stream is running, or while its Settings are open over one.
            void UpdatePad()
            {
                // ...and on the connecting screen, so B cancels.
                if (settings.Current.ControllerNavigation && (!vm.Stream.IsRunning || vm.Stream.IsAway || vm.Stream.ShowStartScreen)) _pad.Enable();
                else _pad.Disable();
                vm.SettingsPage.ControllerStatus = _pad.Available ? $"Controller: {_pad.BackendName}" : "No controller library found";
            }
            // One window, not two: the stream opens over the launcher's own window (same place, size and display), the
            // launcher steps aside when the picture is up, and comes back where the stream's window was.
            // Screen units: Windows works in physical pixels in both processes (per-monitor DPI aware); on macOS SDL
            // works in points, i.e. Avalonia's desktop units.
            double ToSdl() => OperatingSystem.IsWindows() ? 1 : 1 / Math.Max(0.5, window.DesktopScaling);
            PixelPoint frameOffset = default; // client area's top-left minus the window's position (title bar, border)
            launcher.Placement = () =>
            {
                if (!window.IsVisible || window.WindowState == WindowState.Minimized) return null;
                var tl = window.PointToScreen(new Point(0, 0));
                var br = window.PointToScreen(new Point(window.ClientSize.Width, window.ClientSize.Height));
                frameOffset = new PixelPoint(tl.X - window.Position.X, tl.Y - window.Position.Y);
                double k = ToSdl();
                return new WindowPlace((int)Math.Round(tl.X * k), (int)Math.Round(tl.Y * k),
                    (int)Math.Round((br.X - tl.X) * k), (int)Math.Round((br.Y - tl.Y) * k));
            };
            WindowPlace? returnTo = null;
            launcher.WindowClosedAt += p => returnTo = p;
            void HideForStream()
            {
                if (OperatingSystem.IsWindows()) window.Hide(); // no second taskbar button next to the stream's
                else window.WindowState = WindowState.Minimized; // macOS: keep it in the Dock's window list
            }
            void ShowLauncher()
            {
                window.Show();
                window.WindowState = settings.Current.LauncherFullscreen ? WindowState.FullScreen : WindowState.Normal;
                if (returnTo is { } p && window.WindowState == WindowState.Normal)
                {
                    double k = 1 / ToSdl();
                    window.Position = new PixelPoint((int)Math.Round(p.X * k) - frameOffset.X, (int)Math.Round(p.Y * k) - frameOffset.Y);
                    double toDip = OperatingSystem.IsWindows() ? 1 / window.RenderScaling : 1;
                    window.Width = p.W * toDip;
                    window.Height = p.H * toDip;
                }
                returnTo = null;
                window.Activate();
            }
            bool fullscreenSetting = settings.Current.LauncherFullscreen;
            settings.Changed += s =>
            {
                UpdatePad();
                if (s.LauncherFullscreen != fullscreenSetting && window.WindowState != WindowState.Minimized)
                    window.WindowState = s.LauncherFullscreen ? WindowState.FullScreen : WindowState.Normal;
                fullscreenSetting = s.LauncherFullscreen;
            };
            window.ToggleFullscreen = () => settings.Update(s => s with { LauncherFullscreen = !s.LauncherFullscreen });

            // "More settings…" in the stream menu: show Settings over the stream; going back hands the controller back.
            vm.Stream.AwayChanged += () =>
            {
                UpdatePad();
                if (vm.Stream.IsAway)
                {
                    settings.Reload();
                    vm.SelectedNav = vm.Nav.First(n => n.Page == vm.SettingsPage);
                    ShowLauncher();
                    _pad.FocusDefaultSoon();
                }
                else if (vm.Stream.IsStreaming && settings.Current.HideLauncherWhileStreaming)
                    HideForStream();
            };

            // Exit: end a running stream cleanly (the console is told), then close.
            vm.ExitRequested += () =>
            {
                if (!vm.Stream.IsRunning) { desktop.Shutdown(); return; }
                launcher.Exited += _ => desktop.Shutdown();
                vm.Stream.StopCommand.Execute(null);
                DispatcherTimer.RunOnce(() => desktop.Shutdown(), TimeSpan.FromSeconds(8));
            };

            // The connecting screen is meant to fill the screen: on Windows go full screen for it if the
            // launcher is windowed and the stream will be full screen, and put the window back afterwards.
            bool tempFullscreen = false;
            vm.Stream.RunningChanged += running =>
            {
                if (running && OperatingSystem.IsWindows() && settings.Current.Fullscreen && window.WindowState == WindowState.Normal)
                {
                    tempFullscreen = true;
                    window.WindowState = WindowState.FullScreen;
                }
                else if (!running && tempFullscreen)
                {
                    tempFullscreen = false;
                    if (!settings.Current.LauncherFullscreen) window.WindowState = WindowState.Normal;
                }
            };
            vm.Stream.RunningChanged += running =>
            {
                UpdatePad();
                if (!settings.Current.HideLauncherWhileStreaming) return;
                // Hide behind the video once it's actually up; come back when the stream ends.
                if (!running) { settings.Reload(); ShowLauncher(); _pad.FocusDefaultSoon(); }
            };
            vm.Stream.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(StreamViewModel.ShowLoader) or nameof(StreamViewModel.ShowStartScreen))
                {
                    UpdatePad();
                    _pad.FocusDefaultSoon();
                }
                if (e.PropertyName == nameof(StreamViewModel.IsStreaming) && vm.Stream.IsStreaming
                    && settings.Current.HideLauncherWhileStreaming)
                    HideForStream();
            };

            // Keep a warmed-up stream process ready so Stream starts quickly. It's recycled every 20 minutes
            // (fresh sign-in, live connection to the service) and replaced after each stream.
            void PrepareSoon(int ms) => DispatcherTimer.RunOnce(() => { if (vm.IsSignedIn && !vm.Stream.IsRunning) launcher.Prepare(); },
                TimeSpan.FromMilliseconds(ms));
            var recycle = new DispatcherTimer(TimeSpan.FromMinutes(20), DispatcherPriority.Background, (_, _) =>
            {
                if (!vm.IsSignedIn || vm.Stream.IsRunning) return;
                launcher.DropStandby();
                PrepareSoon(500);
            });
            recycle.Start();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(MainViewModel.IsSignedIn)) return;
                if (vm.IsSignedIn) PrepareSoon(1500); else launcher.DropStandby();
            };
            vm.Stream.RunningChanged += running => { if (!running) PrepareSoon(2500); };

            // Updates: clean up after the last one, check shortly after launch and every 6 hours.
            Updater.CleanUp();
            var updateTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await vm.Updates.CheckAsync());
            updateTimer.Start();
            DispatcherTimer.RunOnce(() => _ = vm.Updates.CheckAsync(), TimeSpan.FromSeconds(3));
            vm.RestartRequested += () =>
            {
                if (vm.Stream.IsRunning) return;
                launcher.DropStandby();
                if (vm.Updates.ApplyAndRestart()) desktop.Shutdown();
                else vm.SettingsPage.UpdateStatus = vm.Updates.Status;
            };

            // Discord: "Playing <game> via Vexel" while the picture is up. What's running is looked up when the
            // stream starts and every 3 minutes (the game can change mid-stream).
            var discord = new DiscordPresence();
            string? shown = null;
            async Task UpdateDiscord()
            {
                var s = settings.Current;
                string appId = s.DiscordAppId is { Length: > 0 } id ? id : DiscordPresence.DefaultAppId;
                if (!vm.Stream.IsStreaming || !s.DiscordStatus || appId.Length == 0) { if (shown is not null) discord.Clear(); shown = null; return; }
                // A guest on a friend's stream: their game isn't on this account, so just whose stream it is.
                var now = vm.Stream.IsGuest ? null : await vm.Art.RefreshNowPlayingAsync(s);
                string game = vm.Stream.IsGuest ? "Playing together on " + vm.Stream.ConsoleName
                    : now?.Name is { Length: > 0 } g ? g : "Xbox remote play";
                int players = vm.Stream.TogetherPlayers;
                string key = $"{game}|{players}";
                if (!vm.Stream.IsStreaming || key == shown) return;
                bool sameGame = shown?.StartsWith(game + "|", StringComparison.Ordinal) == true;
                shown = key;
                // Play together: "Playing together (2 of 4)"; the clock keeps running when only the party changes.
                discord.Show(appId, game, players > 1 ? "Playing together" : $"Streaming from {vm.Stream.ConsoleName}",
                    now?.Art?.Url is { Length: > 0 } u ? XStream.Core.Xbox.XboxLibrary.Sized(u, 512) : null, now?.Name,
                    partySize: players > 1 ? players : 0, partyMax: 4, keepTime: sameGame);
            }
            var discordTimer = new DispatcherTimer(TimeSpan.FromMinutes(3), DispatcherPriority.Background, async (_, _) => await UpdateDiscord());
            discordTimer.Stop();
            vm.Stream.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName != nameof(StreamViewModel.IsStreaming)) return;
                if (vm.Stream.IsStreaming) discordTimer.Start(); else discordTimer.Stop();
                await UpdateDiscord();
            };
            vm.Stream.TogetherPlayersChanged += async _ => await UpdateDiscord();
            desktop.Exit += (_, _) => discord.Dispose();

            window.Closing += (_, _) => { if (vm.Stream.IsRunning) launcher.Stop(); };
            desktop.Exit += (_, _) => { launcher.Stop(); launcher.DropStandby(); _pad.Dispose(); _xbox.Dispose(); };

            // Another launch handed over (a shortcut, or Vexel opened again): come to the front and start its stream.
            SingleInstance.Listen(args => Dispatcher.UIThread.Post(async () =>
            {
                Console.Error.WriteLine($"Hand-over from another launch: {(args.Length == 0 ? "(no arguments)" : string.Join(' ', args))}");
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = settings.Current.LauncherFullscreen ? WindowState.FullScreen : WindowState.Normal;
                window.Activate();
                var handed = LaunchArgs.Parse(args);
                if (!handed.Any || !vm.IsSignedIn) return;
                if (vm.Stream.IsRunning) { vm.Stream.ReturnToGameCommand.Execute(null); return; } // already streaming: back to it
                await vm.LaunchAsync(handed);
            }));
            desktop.Exit += (_, _) => SingleInstance.Stop();

            window.Opened += async (_, _) =>
            {
                if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_STREAM") is { Length: > 0 } demo)
                    vm.Consoles.Consoles.CollectionChanged += (_, _) =>
                    {
                        if (vm.Consoles.Consoles.FirstOrDefault() is { } first && !vm.Stream.HasHero)
                            vm.Stream.Demo(demo, first.ServerId, first.Name);
                    };
                // XSTREAM_DEMO_CHECK=1 (testing): show the Test connection panel (Sessions page) with a sample result (nothing runs).
                if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_CHECK") is { Length: > 0 }) vm.Consoles.Check.Demo();
                // XSTREAM_DEMO_PAGE=Mapping (testing): open on that page.
                if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_PAGE") is { Length: > 0 } page && vm.Nav.FirstOrDefault(n => n.Title == page) is { } nav)
                    DispatcherTimer.RunOnce(() =>
                    {
                        vm.SelectedNav = nav;
                        // XSTREAM_DEMO_SECTION=Sound (testing): on Settings, that tab.
                        if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_SECTION") is { Length: > 0 } section) { vm.IsSignedIn = true; vm.SettingsPage.ShowSectionCommand.Execute(section); }
                        // XSTREAM_DEMO_SLOT=A (testing): on Mapping, open the second game and that button's picker (nothing is saved).
                        // Shown signed out too, like XSTREAM_DEMO_KEYS (a test data folder has no sign-in).
                        if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_SLOT") is { Length: > 0 } slot && vm.Mapping.Games.Count > 1)
                        {
                            vm.IsSignedIn = true;
                            vm.Mapping.SelectGameCommand.Execute(vm.Mapping.Games[1]);
                            vm.Mapping.PickSlotCommand.Execute(vm.Mapping.Slots.FirstOrDefault(x => x.Label == slot));
                            // XSTREAM_DEMO_SCROLL=120 (testing): scroll the page down this far (a window can't be taller than the screen).
                            if (double.TryParse(Environment.GetEnvironmentVariable("XSTREAM_DEMO_SCROLL"), out double down))
                                DispatcherTimer.RunOnce(() =>
                                {
                                    if (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<MappingView>().FirstOrDefault() is { } mv
                                        && Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(mv).OfType<ScrollViewer>().FirstOrDefault() is { } sv)
                                        sv.Offset = new Vector(0, down);
                                }, TimeSpan.FromSeconds(1));
                        }
                        // XSTREAM_DEMO_KEYS=A (testing): on Mapping, the keyboard & mouse view with that button waiting for a key ("-": none;
                        // "controller": the controller view). Shown even when signed out (a test data folder has no sign-in; the page needs none).
                        if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_KEYS") is { Length: > 0 } key)
                        {
                            vm.IsSignedIn = true;
                            vm.Mapping.KeyboardView = key != "controller";
                            vm.Mapping.PickSlotCommand.Execute(vm.Mapping.KeySlots.FirstOrDefault(x => x.Label == key));
                        }
                        // XSTREAM_DEMO_TAB=Achievements (testing): on Friends, that tab; XSTREAM_DEMO_GAME=N also opens the Nth game's list.
                        if (page == "Friends" && Environment.GetEnvironmentVariable("XSTREAM_DEMO_TAB") is { Length: > 0 } tab)
                        {
                            vm.Friends.ShowTabCommand.Execute(tab);
                            if (int.TryParse(Environment.GetEnvironmentVariable("XSTREAM_DEMO_GAME"), out int n))
                                DispatcherTimer.RunOnce(() => vm.Friends.OpenGameCommand.Execute(vm.Friends.Games.ElementAtOrDefault(n - 1)), TimeSpan.FromSeconds(2.5));
                        }
                    }, TimeSpan.FromSeconds(2));
                // XSTREAM_DEMO_PAGE="Play together" (testing): sample invite and name in the Join box (nothing is joined).
                if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_PAGE") == "Play together")
                    DispatcherTimer.RunOnce(() =>
                    {
                        vm.IsSignedIn = true;
                        // XSTREAM_DEMO_TOGETHER=ready|host|guest (testing): a pretend stream on the page, made-up names, nothing sent.
                        // The Join box stays empty then (your own invite in it would only confuse).
                        if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_TOGETHER") is { Length: > 0 } together)
                        {
                            vm.Stream.DemoTogether(together);
                            vm.Together.Name = together == "guest" ? "Sam" : "Alex"; // not the computer's user name in a screenshot
                        }
                        else vm.Together.Demo();
                    }, TimeSpan.FromSeconds(2.2));
                // XSTREAM_DEMO_SHOT=file.png (testing): save the window as it looks after a few seconds, then quit.
                if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_SHOT") is { Length: > 0 } shot)
                    DispatcherTimer.RunOnce(() =>
                    {
                        double k = window.RenderScaling;
                        using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                            new PixelSize((int)(window.Bounds.Width * k), (int)(window.Bounds.Height * k)), new Vector(96 * k, 96 * k));
                        rtb.Render(window);
                        rtb.Save(shot);
                        desktop.Shutdown();
                    }, TimeSpan.FromSeconds(8));
                // XSTREAM_DEMO_SIZE=1400x1200 (testing): open the window at this size, so a screenshot shows a whole page.
                if (Environment.GetEnvironmentVariable("XSTREAM_DEMO_SIZE")?.Split('x') is [var dw, var dh] && double.TryParse(dw, out double ww) && double.TryParse(dh, out double wh))
                { window.Width = ww; window.Height = wh; }
                else if (settings.Current.LauncherFullscreen) window.WindowState = WindowState.FullScreen;
                UpdatePad();
                _pad.FocusDefaultSoon();
                if (vm.IsSignedIn) PrepareSoon(800);
                await vm.StartupAsync();
                // Shortcut from Steam / Armoury Crate / a desktop icon (see LaunchArgs): straight into that stream.
                var launch = LaunchArgs.Parse(desktop.Args ?? []);
                if (launch.Any && vm.IsSignedIn && !vm.Stream.IsRunning) await vm.LaunchAsync(launch);
                // "Connect on launch": straight into the last console (B or Cancel on the connecting screen stays here).
                else if (settings.Current.AutoConnect && vm.IsSignedIn && !vm.Stream.IsRunning)
                    vm.Consoles.StreamLast(settings.Current.LastServerId);
            };
            return window;
        }
    }
}
