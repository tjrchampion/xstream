using XStream.Player;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;

namespace XStream.App.ViewModels;

public sealed partial class NavItem(string title, string icon, ObservableObject page) : ObservableObject
{
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public ObservableObject Page { get; } = page;
    /// <summary>A small count beside the title (Play together: players while sharing); empty for none.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBadge))] public partial string Badge { get; set; } = "";
    public bool HasBadge => Badge.Length > 0;
}

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
            UpdateText = Updates.Ready is { } r && Updater.CanSelfUpdate ? $"Vexel {r.Version} is ready" : "";
            SettingsPage.UpdateStatus = Updates.Status;
            SettingsPage.CanInstallUpdate = Updates.CanInstall;
            SettingsPage.UpdateAvailableElsewhere = Updates.Ready is not null && !Updater.CanSelfUpdate;
        });
        Settings = settings;
        Stream = new StreamViewModel(launcher);
        SignIn = new SignInViewModel(xbox);
        Consoles = new ConsolesViewModel(xbox, settings, Stream, _art);
        Cloud = new CloudViewModel(xbox, settings, Stream);
        Friends = new FriendsViewModel(xbox, settings);
        Together = new TogetherViewModel(settings, Stream, xbox);
        Stream.ProfileProvider = () => settings.Current.Profile;
        Stream.CurrentSettings = () => settings.Current;
        Stream.SaveSettings = change => settings.Update(change);
        Sessions = new SessionsViewModel { Check = Consoles.Check, RunTest = Consoles.TestLastAsync };
        SettingsPage = new SettingsViewModel(settings) { Stream = Stream };
        SettingsPage.Updates = Updates;
        SettingsPage.InstallUpdateRequested += RestartToUpdate;
        SettingsPage.OpenMappingRequested += () => SelectedNav = Nav.First(n => n.Page == Mapping);
        Consoles.EditControlsRequested += (key, name) =>
        {
            Mapping.SelectKey(key, name);
            SelectedNav = Nav.First(n => n.Page == Mapping);
        };

        Mapping = new MappingViewModel(settings, _art)
        {
            CloudGames = () => Cloud.Recent.Select(g => ("cloud:" + g.Game.TitleId, g.Game.Name, (Func<Task<string?>>)(() => Cloud.Library.ArtAsync(g.Game, hero: true)))),
        };
        Nav =
        [
            new("Consoles", "Icon.Console", Consoles),
            new("Cloud", "Icon.Cloud", Cloud),
            new("Friends", "Icon.Friends", Friends),
            new("Play together", "Icon.Together", Together),
            new("Mapping", "Icon.Gamepad", Mapping),
            new("Sessions", "Icon.History", Sessions),
            new("Settings", "Icon.Settings", SettingsPage),
        ];
        SelectedNav = Nav[0];
        // Play together: the page from the Consoles page's stream header, Consoles from the page; the sidebar shows who's in.
        Stream.TogetherPageRequested += () => SelectedNav = Nav.First(n => n.Page == Together);
        Together.ConsolesRequested += () => SelectedNav = Nav.First(n => n.Page == Consoles);
        Stream.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StreamViewModel.Together) or nameof(StreamViewModel.IsRunning))
                Nav.First(n => n.Page == Together).Badge = Stream.IsSharing ? Stream.Together!.Players.ToString() : "";
        };

        Stream.Art = _art.Items;
        _art.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!Stream.IsRunning) Stream.Art = _art.Items; });
        SignIn.SignedIn += () => { IsSignedIn = true; SelectedNav = Nav[0]; _ = Consoles.RefreshAsync(); _ = _art.RefreshSoon(Settings.Current); };
        Consoles.SignInExpired += SignOut;
        SettingsPage.SignOutRequested += SignOut;
        Stream.RunningChanged += running => { if (!running) Sessions.Refresh(); };
        IsSignedIn = xbox.SignedIn;
    }

    public SettingsStore Settings { get; }
    public MappingViewModel Mapping { get; }
    public GameArtService Art => _art;
    public Updater Updates { get; }

    // "Vexel 1.2.0 is ready. Restart to update" banner.
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
    public CloudViewModel Cloud { get; }
    public FriendsViewModel Friends { get; }
    public TogetherViewModel Together { get; }
    /// <summary>Signed out, "Join a friend's stream" chosen: the Play together page instead of sign-in (no account needed).</summary>
    [ObservableProperty] public partial bool JoinWithoutSignIn { get; set; }
    [RelayCommand] private void ShowJoin() { JoinWithoutSignIn = true; Together.Opened(); }
    [RelayCommand] private void HideJoin() => JoinWithoutSignIn = false;
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
        if (value?.Page == Mapping) Mapping.RefreshGames();
        if (value?.Page == Consoles && !Consoles.Loaded && IsSignedIn) _ = Consoles.RefreshAsync();
        // The cloud library: shown from the cache straight away, refreshed when the page is opened.
        if (value?.Page == Cloud && IsSignedIn && !Cloud.IsLoading) _ = Cloud.RefreshAsync();
        if (value?.Page == Friends && IsSignedIn) Friends.Opened();
        if (value?.Page == Together) Together.Opened();
    }

    public async Task StartupAsync()
    {
        if (!IsSignedIn) return;
        _ = _art.RefreshSoon(Settings.Current);
        await Consoles.RefreshAsync();
    }

    /// <summary>A command-line shortcut (see <see cref="LaunchArgs"/>): start that stream straight away.</summary>
    public async Task LaunchAsync(LaunchArgs a)
    {
        string? problem;
        if (a.CloudTitleId is { } title) problem = await Cloud.StreamTitleAsync(title);
        else problem = await Consoles.LaunchAsync(a);
        if (problem is not null) Consoles.Error = $"Shortcut: {problem}.";
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
        if (Stream.StartFailed && !Stream.IsRunning) { Stream.DismissCommand.Execute(null); return; }
        if (!IsSignedIn && JoinWithoutSignIn) { JoinWithoutSignIn = false; return; }
        if (!IsSignedIn) { SignIn.CancelCommand.Execute(null); return; }
        if (Stream.HasResult && !Stream.IsRunning) { Stream.DismissCommand.Execute(null); return; }
        if (CurrentPage == Friends && Friends.Back()) return;
        if (SelectedNav != Nav[0]) SelectedNav = Nav[0];
    }

    /// <summary>Close Vexel (ends a running stream cleanly first).</summary>
    public event Action? ExitRequested;

    [RelayCommand]
    private void Exit() => ExitRequested?.Invoke();

    [RelayCommand]
    private void SignOut()
    {
        if (Stream.IsRunning) Stream.StopCommand.Execute(null);
        _xbox.SignOut();
        _art.Forget();
        CloudLibrary.Forget();
        Friends.Forget();
        Consoles.Consoles.Clear();
        Consoles.Loaded = false;
        IsSignedIn = false;
    }
}
