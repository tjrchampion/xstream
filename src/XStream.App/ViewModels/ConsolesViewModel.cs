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

    [ObservableProperty] public partial bool IsStreaming { get; set; }

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
        art.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(ShowGames);
        stream.StreamAgainRequested += id =>
        {
            var c = Consoles.FirstOrDefault(c => c.ServerId == id);
            if (c is not null && StartStreamCommand.CanExecute(c)) StartStreamCommand.Execute(c);
        };
        stream.RunningChanged += running =>
        {
            foreach (var c in Consoles) c.IsStreaming = running && c.ServerId == stream.ServerId;
            StartStreamCommand.NotifyCanExecuteChanged();
        };
    }

    public StreamViewModel Stream { get; }
    public ObservableCollection<ConsoleItemViewModel> Consoles { get; } = [];

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
                });
            LastRefreshed = $"Updated {DateTime.Now:HH:mm}";
            Loaded = true;
            ShowGames();
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

    /// <summary>Menu button on the controller: stream the first (last used) console.</summary>
    public void StreamDefault()
    {
        var c = Consoles.FirstOrDefault(c => c.CanWake) ?? Consoles.FirstOrDefault();
        if (c is not null && StartStreamCommand.CanExecute(c)) StartStreamCommand.Execute(c);
    }
}
