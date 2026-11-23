using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;

namespace XStream.App.ViewModels;

public sealed record NavItem(string Title, string Icon, ObservableObject Page);

/// <summary>The shell: sign-in gate, sidebar navigation, and the shared stream state.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly XboxService _xbox;

    private readonly GameArtService _art;

    public MainViewModel(XboxService xbox, SettingsStore settings, StreamLauncher launcher)
    {
        _xbox = xbox;
        _art = new GameArtService(xbox);
        Updates = new Updater(settings);
        Updates.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateText = Updates.Ready is { } r && Updater.CanSelfUpdate ? $"XStream {r.Version} is ready" : "";
            SettingsPage.UpdateStatus = Updates.Status;
        });
        Settings = settings;
        Stream = new StreamViewModel(launcher);
        SignIn = new SignInViewModel(xbox);
        Consoles = new ConsolesViewModel(xbox, settings, Stream, _art);
        Sessions = new SessionsViewModel();
        SettingsPage = new SettingsViewModel(settings) { Stream = Stream };
        SettingsPage.Updates = Updates;

        Nav =
        [
            new("Consoles", "Icon.Console", Consoles),
            new("Sessions", "Icon.History", Sessions),
            new("Settings", "Icon.Settings", SettingsPage),
        ];
        SelectedNav = Nav[0];

        Stream.Art = _art.Items;
        _art.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!Stream.IsRunning) Stream.Art = _art.Items; });
        SignIn.SignedIn += () => { IsSignedIn = true; SelectedNav = Nav[0]; _ = Consoles.RefreshAsync(); _ = _art.RefreshSoon(Settings.Current); };
        Consoles.SignInExpired += SignOut;
        SettingsPage.SignOutRequested += SignOut;
        Stream.RunningChanged += running => { if (!running) Sessions.Refresh(); };
        IsSignedIn = xbox.SignedIn;
    }

    public SettingsStore Settings { get; }
    public GameArtService Art => _art;
    public Updater Updates { get; }

    // "XStream 1.2.0 is ready. Restart to update" banner.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUpdate))] public partial string UpdateText { get; set; } = "";
    [ObservableProperty] public partial bool UpdateDismissed { get; set; }
    public bool HasUpdate => UpdateText.Length > 0 && !UpdateDismissed;
    partial void OnUpdateDismissedChanged(bool value) => OnPropertyChanged(nameof(HasUpdate));
    public event Action? RestartRequested;

    // Controller status (from the launcher's controller polling; -1 = not known).
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ControllerKnown), nameof(HasController), nameof(ControllerText))]
    public partial int Controllers { get; set; } = -1;
    public bool ControllerKnown => Controllers >= 0;
    public bool HasController => Controllers > 0;
    public string ControllerText => Controllers switch
    {
        < 0 => "",
        0 => "No controller connected",
        1 => "Controller connected",
        var n => $"{n} controllers connected",
    };
    partial void OnControllersChanged(int value) => Stream.NoController = value == 0;

    [RelayCommand] private void RestartToUpdate() { if (!Stream.IsRunning) RestartRequested?.Invoke(); }
    [RelayCommand] private void DismissUpdate() => UpdateDismissed = true;
    public StreamViewModel Stream { get; }
    public SignInViewModel SignIn { get; }
    public ConsolesViewModel Consoles { get; }
    public SessionsViewModel Sessions { get; }
    public SettingsViewModel SettingsPage { get; }
    public ObservableCollection<NavItem> Nav { get; }

    [ObservableProperty] public partial bool IsSignedIn { get; set; }
    [ObservableProperty] public partial NavItem SelectedNav { get; set; }
    [ObservableProperty] public partial ObservableObject? CurrentPage { get; set; }

    partial void OnSelectedNavChanged(NavItem value)
    {
        CurrentPage = value?.Page;
        if (value?.Page == Sessions) Sessions.Refresh();
        if (value?.Page == Consoles && !Consoles.Loaded && IsSignedIn) _ = Consoles.RefreshAsync();
    }

    public async Task StartupAsync()
    {
        if (!IsSignedIn) return;
        _ = _art.RefreshSoon(Settings.Current);
        await Consoles.RefreshAsync();
    }

    public void SwitchPage(int delta)
    {
        if (!IsSignedIn) return;
        int i = (Nav.IndexOf(SelectedNav) + delta + Nav.Count) % Nav.Count;
        SelectedNav = Nav[i];
    }

    /// <summary>B on the controller / Escape: close the finished-stream panel, cancel sign-in, or go home.</summary>
    public void Back()
    {
        if (Stream.IsAway) { Stream.ReturnToGameCommand.Execute(null); return; }
        if (Stream.ShowLoader) { Stream.StopCommand.Execute(null); return; }
        if (!IsSignedIn) { SignIn.CancelCommand.Execute(null); return; }
        if (Stream.HasResult && !Stream.IsRunning) { Stream.DismissCommand.Execute(null); return; }
        if (SelectedNav != Nav[0]) SelectedNav = Nav[0];
    }

    /// <summary>Close XStream (ends a running stream cleanly first).</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    [RelayCommand]
    private void SignOut()
    {
        if (Stream.IsRunning) Stream.StopCommand.Execute(null);
        _xbox.SignOut();
        _art.Forget();
        Consoles.Consoles.Clear();
        Consoles.Loaded = false;
        IsSignedIn = false;
    }
}
