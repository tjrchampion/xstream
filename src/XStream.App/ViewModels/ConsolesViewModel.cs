using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Xbox;
using XStream.Player;

namespace XStream.App.ViewModels;

public sealed partial class ConsoleItemViewModel : ObservableObject
{
    public ConsoleItemViewModel(XboxConsole c, bool lastUsed, IRelayCommand<ConsoleItemViewModel> stream)
    {
        Name = c.DeviceName;
        ServerId = c.ServerId;
        TypeLabel = c.ConsoleType switch
        {
            "XboxSeriesX" => "Xbox Series X",
            "XboxSeriesS" => "Xbox Series S",
            "XboxOneX" => "Xbox One X",
            "XboxOneS" => "Xbox One S",
            "XboxOne" => "Xbox One",
            "" => "Console",
            var t => t,
        };
        (PowerLabel, PowerKind) = c.PowerState switch
        {
            "On" => ("On", "on"),
            "ConnectedStandby" => ("Sleeping, ready to wake", "sleep"),
            "Off" => ("Off", "off"),
            "" => ("Unknown", "off"),
            var p => (p, "off"),
        };
        CanWake = c.PowerState is "On" or "ConnectedStandby";
        WirelessWarning = c.WirelessWarning;
        OutOfHomeWarning = c.OutOfHomeWarning;
        IsLastUsed = lastUsed;
        StreamCommand = stream;
    }

    public string Name { get; }
    public string ServerId { get; }
    public string ShortId => ServerId.Length > 8 ? ServerId[..8] + "…" : ServerId;
    public string TypeLabel { get; }
    public string PowerLabel { get; }
    /// <summary>on | sleep | off (drives the status dot colour).</summary>
    public string PowerKind { get; }
    public bool CanWake { get; }
    public bool WirelessWarning { get; }
    public bool OutOfHomeWarning { get; }
    public bool IsLastUsed { get; }
    public IRelayCommand<ConsoleItemViewModel> StreamCommand { get; }
    public IAsyncRelayCommand<ConsoleItemViewModel>? TurnOffCommand { get; init; }
    public IAsyncRelayCommand<ConsoleItemViewModel>? RestartCommand { get; init; }
    /// <summary>On and not streaming: the card offers Turn off / Restart.</summary>
    public bool ShowPower => IsOn && !IsStreaming;
    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowPower));

    /// <summary>A stream to this console is starting or running (the card's button gives way to a status).</summary>
    [ObservableProperty] public partial bool IsStreaming { get; set; }
    /// <summary>The picture is up (vs still connecting).</summary>
    [ObservableProperty] public partial bool IsLive { get; set; }
    /// <summary>Shown in the "Now playing" header instead (live or just ended): its card steps aside, no duplicate.</summary>
    [ObservableProperty] public partial bool IsInHeader { get; set; }

    // Game art across the top of the card: what's running on it, else the last game played.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGame))] public partial string GameName { get; set; } = "";
    [ObservableProperty] public partial string GameLabel { get; set; } = "";
    [ObservableProperty] public partial bool IsPlayingNow { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArt))] public partial Avalonia.Media.Imaging.Bitmap? GameArt { get; set; }
    public string GameTitleId { get; private set; } = "";
    public bool HasGame => GameName.Length > 0;
    public bool HasArt => GameArt is not null;
    public bool IsOn => PowerKind == "on";

    private string? _artPath;

    public async void ShowGame(string titleId, string name, string? artPath, bool now)
    {
        GameTitleId = titleId;
        GameName = name;
        IsPlayingNow = now;
        GameLabel = now ? "NOW PLAYING" : "LAST PLAYED";
        if (artPath == _artPath) return;
        _artPath = artPath;
        if (artPath is null) { GameArt = null; return; }
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var f = File.OpenRead(artPath);
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(f, 640);
            });
            if (_artPath == artPath) GameArt = bmp; else bmp.Dispose();
        }
        catch { if (_artPath == artPath) GameArt = null; }
    }
}

/// <summary>A recently played game on the Consoles page: art and name; choosing it starts it on the console and streams.</summary>
public sealed partial class RecentGameViewModel : ObservableObject
{
    public RecentGameViewModel(GameArt art, IAsyncRelayCommand<RecentGameViewModel> play)
    {
        Name = art.Name; TitleId = art.TitleId; ProductId = art.ProductId; PlayCommand = play;
        _ = Load(art.Path);
    }

    public string Name { get; }
    public string TitleId { get; }
    public string? ProductId { get; }
    public IAsyncRelayCommand<RecentGameViewModel> PlayCommand { get; }
    [ObservableProperty] public partial Avalonia.Media.Imaging.Bitmap? Art { get; set; }

    private async Task Load(string path)
    {
        try
        {
            Art = await Task.Run(() => { using var f = File.OpenRead(path); return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(f, 400); });
        }
        catch { /* no art: the tile shows the name */ }
    }
}

/// <summary>Home page: the console list, plus the live stream panel when one is running.</summary>
public sealed partial class ConsolesViewModel : ObservableObject
{
    private readonly XboxService _xbox;
    private readonly SettingsStore _settings;
    private readonly GameArtService _art;

    public ConsolesViewModel(XboxService xbox, SettingsStore settings, StreamViewModel stream, GameArtService art)
    {
        _xbox = xbox;
        _settings = settings;
        _art = art;
        Stream = stream;
        art.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { ShowGames(); ShowRecent(); NotifyController(); });
        settings.Changed += _ => NotifyController();
        // While streaming a console, keep "what's running" current (the header's game and its controller layout).
        _presence = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(30), Avalonia.Threading.DispatcherPriority.Background,
            async (_, _) => { if (Stream.IsStreaming && !Stream.IsCloud) await _art.RefreshNowPlayingAsync(_settings.Current); });
        stream.RunningChanged += running => { if (running) _presence.Start(); else _presence.Stop(); };
        stream.StreamAgainRequested += id =>
        {
            var c = Consoles.FirstOrDefault(c => c.ServerId == id);
            if (c is not null && StartStreamCommand.CanExecute(c)) StartStreamCommand.Execute(c);
        };
        stream.RunningChanged += _ => { SyncCards(); StartStreamCommand.NotifyCanExecuteChanged(); PlayGameCommand.NotifyCanExecuteChanged(); };
        Consoles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentGames));
        stream.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StreamViewModel.IsStreaming) or nameof(StreamViewModel.ServerId) or nameof(StreamViewModel.HasHero)) SyncCards();
        };
    }

    public StreamViewModel Stream { get; }
    public ObservableCollection<ConsoleItemViewModel> Consoles { get; } = [];
    private readonly Avalonia.Threading.DispatcherTimer _presence;

    /// <summary>The console in the "Now playing" header (streaming now, or the session that just ended). Null for cloud.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHeroArt))] public partial ConsoleItemViewModel? ActiveConsole { get; set; }
    public bool HasHeroArt => ActiveConsole?.GameArt is not null;

    private void SyncCards()
    {
        ConsoleItemViewModel? active = null;
        foreach (var c in Consoles)
        {
            bool mine = c.ServerId == Stream.ServerId;
            c.IsStreaming = Stream.IsRunning && mine;
            c.IsLive = Stream.IsStreaming && mine;
            if (mine && (Stream.IsRunning || Stream.HasHero)) active = c;
            c.IsInHeader = mine && Stream.HasHero;
        }
        ActiveConsole = active;
        NotifyController();
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] public partial bool IsLoading { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string LastRefreshed { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEmpty))] public partial bool Loaded { get; set; }

    public bool HasError => Error.Length > 0;
    public bool IsEmpty => Loaded && !IsLoading && !HasError && Consoles.Count == 0;

    /// <summary>Raised when the service says the sign-in is no longer valid.</summary>
    public event Action? SignInExpired;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = "";
        try
        {
            var list = await _xbox.GetConsolesAsync(_settings.Current);
            Consoles.Clear();
            var last = _settings.Current.LastServerId;
            foreach (var c in list.OrderByDescending(c => c.ServerId == last).ThenBy(c => c.DeviceName))
                Consoles.Add(new ConsoleItemViewModel(c, c.ServerId == last, StartStreamCommand)
                {
                    IsStreaming = Stream.IsRunning && c.ServerId == Stream.ServerId,
                    TurnOffCommand = TurnOffCommand, RestartCommand = RestartCommand,
                });
            SyncCards();
            LastRefreshed = $"Updated {DateTime.Now:HH:mm}";
            Loaded = true;
            ShowGames();
            _installedOn = null; // look again: games may have been installed or removed
            ShowRecent();
            _ = _art.RefreshNowPlayingAsync(_settings.Current);
        }
        catch (XboxApiException ex) when (ex.Status is 401 or 403)
        {
            SignInExpired?.Invoke();
        }
        catch (Exception ex)
        {
            Error = PlayerSession.Friendly(ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    // ---- Remote control: power, and starting a game on the console ----------------------------------------
    /// <summary>A short result line under the header ("Turning off Series X…").</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] public partial string Notice { get; set; } = "";
    public bool HasNotice => Notice.Length > 0;

    private async void ShowNotice(string text, int ms = 6000)
    {
        Notice = text;
        await Task.Delay(ms);
        if (Notice == text) Notice = "";
    }

    [RelayCommand]
    private async Task TurnOffAsync(ConsoleItemViewModel? c)
    {
        if (c is null) return;
        try { await _xbox.TurnOffAsync(c.ServerId, c.Name); ShowNotice($"Turning off {c.Name}…"); await RefreshSoonAsync(); }
        catch (Exception ex) { ShowNotice($"Couldn't turn off {c.Name}: {PlayerSession.Friendly(ex)}", 9000); }
    }

    [RelayCommand]
    private async Task RestartAsync(ConsoleItemViewModel? c)
    {
        if (c is null) return;
        try { await _xbox.RestartAsync(c.ServerId, c.Name); ShowNotice($"Restarting {c.Name}. It takes about a minute."); }
        catch (Exception ex) { ShowNotice($"Couldn't restart {c.Name}: {PlayerSession.Friendly(ex)}", 9000); }
    }

    private async Task RefreshSoonAsync()
    {
        await Task.Delay(8000);
        if (!Stream.IsRunning) await RefreshAsync();
    }

    /// <summary>Recently played console games (with store ids): pick one to start it on the console and stream.</summary>
    public ObservableCollection<RecentGameViewModel> RecentGames { get; } = [];
    public bool HasRecentGames => RecentGames.Count > 0 && Consoles.Count > 0;

    // What's installed on the console games start on (product and title ids); null until looked up.
    private HashSet<string>? _installed;
    private string? _installedOn;

    private void ShowRecent()
    {
        var target = PlayTarget();
        if (target is not null && target.ServerId != _installedOn) { _installedOn = target.ServerId; _installed = null; _ = LoadInstalledAsync(target); }
        // Only installed games: launching one that isn't just opens its Store page.
        var items = _installed is null ? [] : _art.Items
            .Where(a => !string.IsNullOrEmpty(a.ProductId) && (_installed.Contains(a.ProductId!) || _installed.Contains(a.TitleId)))
            .Take(8).ToList();
        if (items.Select(i => i.TitleId).SequenceEqual(RecentGames.Select(r => r.TitleId))) return;
        RecentGames.Clear();
        foreach (var a in items) RecentGames.Add(new RecentGameViewModel(a, PlayGameCommand));
        OnPropertyChanged(nameof(HasRecentGames));
    }

    private async Task LoadInstalledAsync(ConsoleItemViewModel c)
    {
        try
        {
            var apps = await _xbox.Control.GetInstalledAsync(await _xbox.ControlIdAsync(c.ServerId, c.Name));
            _installed = apps.SelectMany(a => new[] { a.ProductId, a.TitleId }).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { _installedOn = null; return; } // remote features off: no row
        Avalonia.Threading.Dispatcher.UIThread.Post(ShowRecent);
    }

    private ConsoleItemViewModel? PlayTarget() =>
        Consoles.FirstOrDefault(x => x.IsLastUsed && x.CanWake) ?? Consoles.FirstOrDefault(x => x.CanWake) ?? Consoles.FirstOrDefault();

    /// <summary>Start the game on the last used console (waking it), then stream it.</summary>
    [RelayCommand(CanExecute = nameof(CanPlayGame))]
    private async Task PlayGameAsync(RecentGameViewModel? game)
    {
        var c = PlayTarget();
        if (game?.ProductId is null || c is null || Stream.IsRunning) return;
        await PlayOnAsync(c, game.ProductId, game.TitleId, game.Name);
    }

    private bool CanPlayGame(RecentGameViewModel? game) => !Stream.IsRunning;

    /// <summary>Launch by store id, then stream (the stream wakes the console if needed; the game opens as it connects).</summary>
    public async Task PlayOnAsync(ConsoleItemViewModel c, string productId, string titleId, string name)
    {
        try
        {
            if (!c.IsOn)
            {
                // Asleep: wake it first, so the launch has somewhere to go.
                try { await _xbox.Control.WakeAsync(await _xbox.ControlIdAsync(c.ServerId, c.Name)); } catch { /* the stream wakes it too */ }
                await Task.Delay(5000);
            }
            await _xbox.LaunchAsync(c.ServerId, c.Name, productId);
            ShowNotice($"Starting {name} on {c.Name}…");
        }
        catch (Exception ex) { ShowNotice($"Couldn't start {name}: {PlayerSession.Friendly(ex)}. Streaming the console instead.", 9000); }
        _settings.Update(s => s with { LastServerId = c.ServerId });
        Stream.Art = _art.StartingWith(titleId);
        if (!Stream.IsRunning) Stream.Start(c, _settings.Current.Profile);
    }

    /// <summary>
    /// Card art: the game running now goes on the console that's on (presence is per account, so with several
    /// consoles on we can't tell which; all of them show it). Otherwise the account's last played game.
    /// </summary>
    private void ShowGames()
    {
        var now = _art.Current;
        var last = _art.Items.FirstOrDefault();
        foreach (var c in Consoles)
        {
            if (now is not null && c.IsOn) c.ShowGame(now.TitleId, now.Name, now.Art?.Path, now: true);
            else if (last is not null) c.ShowGame(last.TitleId, last.Name, last.Path, now: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStream))]
    private void StartStream(ConsoleItemViewModel? console)
    {
        if (console is null) return;
        _settings.Update(s => s with { LastServerId = console.ServerId });
        // The connecting screen opens on the game that's running (or was last played), then moves through the rest.
        Stream.Art = _art.StartingWith(console.GameTitleId);
        Stream.Start(console, _settings.Current.Profile);
    }

    private bool CanStream(ConsoleItemViewModel? console) => !Stream.IsRunning;

    /// <summary>Connect on launch: stream the last used console if it's there and can be woken.</summary>
    public void StreamLast(string? serverId)
    {
        var c = Consoles.FirstOrDefault(c => c.ServerId == serverId && c.CanWake);
        if (c is not null && StartStreamCommand.CanExecute(c)) StartStreamCommand.Execute(c);
    }

    // ---- Controller layout for the game being played (Now playing header) ----------------------------------
    /// <summary>Open Settings on this game's controller layout (game key, name).</summary>
    public event Action<string, string>? EditControlsRequested;

    /// <summary>The game in the header (cloud: the cloud game; console: what's running on it).</summary>
    private (string Key, string Name)? HeaderGame =>
        Stream.IsCloud
            ? (Stream.ServerId.StartsWith(PlayerHost.CloudPrefix) ? ("cloud:" + Stream.ServerId[PlayerHost.CloudPrefix.Length..], Stream.ConsoleName) : null)
            : ActiveConsole is { HasGame: true } c && c.GameTitleId.Length > 0 ? (c.GameTitleId, c.GameName) : null;

    /// <summary>"Controller: Halo layout" / "Controller: default layout" under the header.</summary>
    public string ControllerLine
    {
        get
        {
            if (HeaderGame is not { } g) return "";
            var p = _settings.Current.ControllerFor(g.Key);
            return p.GameKey.Length > 0 ? $"Controller: {g.Name} layout ({p.Summary})" : $"Controller: default layout ({p.Summary})";
        }
    }
    public bool HasControllerLine => ControllerLine.Length > 0;
    public string EditControlsText => HeaderGame is { } g && _settings.Current.ControllerFor(g.Key).GameKey.Length > 0
        ? "Edit controls" : "Controls for this game";

    [RelayCommand]
    private void EditControls()
    {
        if (HeaderGame is { } g) EditControlsRequested?.Invoke(g.Key, g.Name);
    }

    private void NotifyController()
    {
        OnPropertyChanged(nameof(ControllerLine)); OnPropertyChanged(nameof(HasControllerLine)); OnPropertyChanged(nameof(EditControlsText));
    }

    /// <summary>A shortcut's console: by name or id, else the last used one.</summary>
    public ConsoleItemViewModel? Find(string? nameOrId) =>
        (nameOrId is null ? null : Consoles.FirstOrDefault(c => c.ServerId.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)
                                                             || c.Name.Equals(nameOrId, StringComparison.OrdinalIgnoreCase)))
        ?? (nameOrId is null ? Consoles.FirstOrDefault(c => c.IsLastUsed) ?? Consoles.FirstOrDefault() : null);

    /// <summary>Command line: --stream [console], optionally --game productId.</summary>
    public async Task<string?> LaunchAsync(LaunchArgs a)
    {
        if (Consoles.Count == 0) await RefreshAsync();
        if (Find(a.Console) is not { } c) return a.Console is null ? "No consoles on this account" : $"No console called \"{a.Console}\"";
        if (a.GameProductId is { } product)
        {
            var known = _art.Items.FirstOrDefault(i => string.Equals(i.ProductId, product, StringComparison.OrdinalIgnoreCase));
            await PlayOnAsync(c, product, known?.TitleId ?? "", known?.Name ?? "the game");
        }
        else if (StartStreamCommand.CanExecute(c)) StartStreamCommand.Execute(c);
        return null;
    }

    /// <summary>Menu button on the controller: stream the first (last used) console.</summary>
    public void StreamDefault()
    {
        var c = Consoles.FirstOrDefault(c => c.CanWake) ?? Consoles.FirstOrDefault();
        if (c is not null && StartStreamCommand.CanExecute(c)) StartStreamCommand.Execute(c);
    }
}
