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
                                 "Check there's about 120 MB free, then open XStream again.");
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
                else if (vm.CurrentPage == vm.SettingsPage) vm.SettingsPage.RunChecksCommand.Execute(null);
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
                if (settings.Current.ControllerNavigation && (!vm.Stream.IsRunning || vm.Stream.IsAway || vm.Stream.ShowLoader)) _pad.Enable();
                else _pad.Disable();
                vm.SettingsPage.ControllerStatus = _pad.Available ? $"Controller: {_pad.BackendName}" : "No controller library found";
            }
            void ShowLauncher()
            {
                window.Show();
                window.WindowState = settings.Current.LauncherFullscreen ? WindowState.FullScreen : WindowState.Normal;
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
                    vm.SelectedNav = vm.Nav[2];
                    ShowLauncher();
                    _pad.FocusDefaultSoon();
                }
                else if (vm.Stream.IsRunning && settings.Current.HideLauncherWhileStreaming && settings.Current.Fullscreen)
                    window.WindowState = WindowState.Minimized;
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
                if (e.PropertyName == nameof(StreamViewModel.ShowLoader))
                {
                    UpdatePad();
                    _pad.FocusDefaultSoon();
                }
                if (e.PropertyName == nameof(StreamViewModel.IsStreaming) && vm.Stream.IsStreaming
                    && settings.Current.HideLauncherWhileStreaming && settings.Current.Fullscreen)
                    window.WindowState = WindowState.Minimized;
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
            DispatcherTimer.RunOnce(() => _ = vm.Updates.CheckAsync(), TimeSpan.FromSeconds(15));
            vm.RestartRequested += () =>
            {
                if (vm.Stream.IsRunning) return;
                launcher.DropStandby();
                if (vm.Updates.ApplyAndRestart()) desktop.Shutdown();
                else vm.SettingsPage.UpdateStatus = vm.Updates.Status;
            };

            // Discord: "Playing <game> via XStream" while the picture is up. What's running is looked up when the
            // stream starts and every 3 minutes (the game can change mid-stream).
            var discord = new DiscordPresence();
            string? shown = null;
            async Task UpdateDiscord()
            {
                var s = settings.Current;
                string appId = s.DiscordAppId is { Length: > 0 } id ? id : DiscordPresence.DefaultAppId;
                if (!vm.Stream.IsStreaming || !s.DiscordStatus || appId.Length == 0) { if (shown is not null) discord.Clear(); shown = null; return; }
                var now = await vm.Art.RefreshNowPlayingAsync(s);
                string game = now?.Name is { Length: > 0 } g ? g : "Xbox remote play";
                if (!vm.Stream.IsStreaming || game == shown) return;
                shown = game;
                discord.Show(appId, game, $"Streaming from {vm.Stream.ConsoleName}",
                    now?.Art?.Url is { Length: > 0 } u ? XStream.Core.Xbox.XboxLibrary.Sized(u, 512) : null, now?.Name);
            }
            var discordTimer = new DispatcherTimer(TimeSpan.FromMinutes(3), DispatcherPriority.Background, async (_, _) => await UpdateDiscord());
            discordTimer.Stop();
            vm.Stream.PropertyChanged += async (_, e) =>
            {
                if (e.PropertyName != nameof(StreamViewModel.IsStreaming)) return;
                if (vm.Stream.IsStreaming) discordTimer.Start(); else discordTimer.Stop();
                await UpdateDiscord();
            };
            desktop.Exit += (_, _) => discord.Dispose();

            window.Closing += (_, _) => { if (vm.Stream.IsRunning) launcher.Stop(); };
            desktop.Exit += (_, _) => { launcher.Stop(); launcher.DropStandby(); _pad.Dispose(); _xbox.Dispose(); };

            window.Opened += async (_, _) =>
            {
                if (settings.Current.LauncherFullscreen) window.WindowState = WindowState.FullScreen;
                UpdatePad();
                _pad.FocusDefaultSoon();
                if (vm.IsSignedIn) PrepareSoon(800);
                await vm.StartupAsync();
                // "Connect on launch": straight into the last console (B or Cancel on the connecting screen stays here).
                if (settings.Current.AutoConnect && vm.IsSignedIn && !vm.Stream.IsRunning)
                    vm.Consoles.StreamLast(settings.Current.LastServerId);
            };
            return window;
        }
    }
}
