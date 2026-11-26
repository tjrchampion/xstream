using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Xbox;
using XStream.Player;

namespace XStream.App.ViewModels;

/// <summary>Small pictures (gamerpics, box art, achievement icons) fetched once per launch and kept in memory.</summary>
public sealed class PictureCache(XboxService xbox)
{
    private readonly Dictionary<string, Task<Bitmap?>> _cache = [];

    public Task<Bitmap?> GetAsync(string? url, int size, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<Bitmap?>(null);
        string key = url + "|" + size;
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var t)) return t;
            return _cache[key] = Load(XboxSocial.Thumb(url, size), decodeWidth, key);
        }
    }

    private async Task<Bitmap?> Load(string url, int decodeWidth, string key)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var bytes = await xbox.DownloadAsync(url, cts.Token);
            return await Task.Run(() => { using var m = new MemoryStream(bytes); return Bitmap.DecodeToWidth(m, decodeWidth); });
        }
        catch
        {
            lock (_cache) _cache.Remove(key); // try again next time
            return null;
        }
    }

    public void Clear() { lock (_cache) _cache.Clear(); }
}

public sealed partial class FriendViewModel : ObservableObject
{
    public FriendViewModel(Friend f, DateTimeOffset now)
    {
        Friend = f;
        Status = f.Status(now);
    }

    public Friend Friend { get; }
    public string Gamertag => Friend.Gamertag;
    public string Initial => Friend.Gamertag.Length > 0 ? Friend.Gamertag[..1].ToUpperInvariant() : "?";
    public string Status { get; }
    /// <summary>What the game says they're doing ("Racing in Mexico").</summary>
    public string Detail => Friend.IsPlaying ? Friend.RichPresence ?? "" : "";
    public bool HasDetail => Detail.Length > 0;
    public bool IsOnline => Friend.IsOnline;
    public bool IsPlaying => Friend.IsPlaying;
    public bool IsFavorite => Friend.IsFavorite;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPicture))] public partial Bitmap? Picture { get; set; }
    public bool HasPicture => Picture is not null;

    public async void LoadPicture(PictureCache pictures) => Picture = await pictures.GetAsync(Friend.PictureUrl, 128, 96);
}

public sealed partial class GameProgressViewModel(GameProgress game, IRelayCommand<GameProgressViewModel> open) : ObservableObject
{
    public GameProgress Game { get; } = game;
    public string Name => Game.Name;
    public string ScoreText => $"{Game.Gamerscore.ToString("N0", CultureInfo.CurrentCulture)} / {Game.TotalGamerscore.ToString("N0", CultureInfo.CurrentCulture)} G";
    public double Percent => Game.TotalGamerscore > 0 ? Math.Clamp(100.0 * Game.Gamerscore / Game.TotalGamerscore, 0, 100) : 0;
    public string PercentText => $"{Math.Round(Percent):0}%";
    public string CountText => Game.Unlocked == 1 ? "1 achievement" : $"{Game.Unlocked} achievements";
    public string PlayedText => Game.LastPlayed == DateTimeOffset.MinValue ? "" : "Played " + XboxSocial.Ago(DateTimeOffset.UtcNow - Game.LastPlayed);
    public bool IsComplete => Game.TotalGamerscore > 0 && Game.Gamerscore >= Game.TotalGamerscore;
    public IRelayCommand<GameProgressViewModel> OpenCommand { get; } = open;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArt))] public partial Bitmap? Art { get; set; }
    public bool HasArt => Art is not null;

    public async void LoadArt(PictureCache pictures) => Art ??= await pictures.GetAsync(Game.ImageUrl, 208, 160);
}

public sealed partial class AchievementViewModel(Achievement achievement) : ObservableObject
{
    public Achievement Achievement { get; } = achievement;
    private Achievement a => Achievement;
    public bool Unlocked => a.Unlocked;
    private bool Hidden => a.IsSecret && !a.Unlocked;
    public string Name => Hidden ? "Secret achievement" : a.Name;
    public string Description => a.Unlocked ? a.Description : Hidden ? "Keep playing to find out." : a.LockedDescription;
    public string ScoreText => a.Gamerscore > 0 ? $"{a.Gamerscore}G" : "";
    public bool HasScore => a.Gamerscore > 0;
    public string DateText => a.UnlockedAt is { } at ? "Unlocked " + at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) : a.Unlocked ? "Unlocked" : "";
    // Progress toward a locked one, once it has started ("80 / 100").
    public bool HasProgress => !a.Unlocked && a.ProgressTarget is > 0 && a.ProgressCurrent is > 0;
    public double ProgressPercent => HasProgress ? 100.0 * a.ProgressCurrent!.Value / a.ProgressTarget!.Value : 0;
    public string ProgressText => HasProgress ? $"{a.ProgressCurrent} / {a.ProgressTarget}" : "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIcon))] public partial Bitmap? Icon { get; set; }
    public bool HasIcon => Icon is not null;

    public async void LoadIcon(PictureCache pictures)
    {
        if (!Hidden) Icon = await pictures.GetAsync(a.IconUrl, 208, 160);
    }
}

/// <summary>
/// The Friends page: who's online and what they're playing, and the account's achievements (recently played
/// games with gamerscore progress, and each game's list). Refreshed when the page opens; results are kept for
/// a minute so flicking between pages doesn't refetch.
/// </summary>
public sealed partial class FriendsViewModel : ObservableObject
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(60);
    private readonly XboxService _xbox;
    private readonly SettingsStore _settings;
    private readonly PictureCache _pictures;
    private DateTime _friendsAt, _gamesAt;
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<Achievement> List)> _achievements = [];

    public FriendsViewModel(XboxService xbox, SettingsStore settings)
    {
        _xbox = xbox;
        _settings = settings;
        _pictures = new PictureCache(xbox);
    }

    // ---- Tabs ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsFriendsTab), nameof(IsAchievementsTab), nameof(ShowGames), nameof(ShowGame))]
    public partial string Tab { get; set; } = "Friends";
    public bool IsFriendsTab => Tab == "Friends";
    public bool IsAchievementsTab => Tab == "Achievements";
    [RelayCommand] private void ShowTab(string tab) { Tab = tab; _ = RefreshAsync(force: false); }

    // ---- Friends ----
    public ObservableCollection<FriendViewModel> Online { get; } = [];
    public ObservableCollection<FriendViewModel> Offline { get; } = [];
    [ObservableProperty] public partial bool IsLoadingFriends { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFriendsError))] public partial string FriendsError { get; set; } = "";
    [ObservableProperty] public partial bool FriendsLoaded { get; set; }
    [ObservableProperty] public partial string FriendsSummary { get; set; } = "";
    public bool HasFriendsError => FriendsError.Length > 0;
    public bool HasOnline => Online.Count > 0;
    public bool HasOffline => Offline.Count > 0;
    public bool NoFriends => FriendsLoaded && Online.Count == 0 && Offline.Count == 0;
    public bool NobodyOnline => FriendsLoaded && Online.Count == 0 && Offline.Count > 0;

    // ---- Achievements: games ----
    public ObservableCollection<GameProgressViewModel> Games { get; } = [];
    [ObservableProperty] public partial bool IsLoadingGames { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGamesError))] public partial string GamesError { get; set; } = "";
    [ObservableProperty] public partial bool GamesLoaded { get; set; }
    [ObservableProperty] public partial string GamesSummary { get; set; } = "";
    public bool HasGamesError => GamesError.Length > 0;
    public bool NoGames => GamesLoaded && Games.Count == 0;

    // ---- Achievements: one game ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowGames), nameof(ShowGame))] public partial GameProgressViewModel? SelectedGame { get; set; }
    public bool ShowGames => IsAchievementsTab && SelectedGame is null;
    public bool ShowGame => IsAchievementsTab && SelectedGame is not null;
    public ObservableCollection<AchievementViewModel> Achievements { get; } = [];
    [ObservableProperty] public partial bool IsLoadingAchievements { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAchievementsError))] public partial string AchievementsError { get; set; } = "";
    [ObservableProperty] public partial string GameSummary { get; set; } = "";
    [ObservableProperty] public partial bool NoAchievements { get; set; }
    public bool HasAchievementsError => AchievementsError.Length > 0;
    private int _gameVersion;

    /// <summary>The page was opened: refresh whatever is showing unless it's only just been fetched.</summary>
    public void Opened() => _ = RefreshAsync(force: false);

    /// <summary>Y on the controller / the refresh button.</summary>
    [RelayCommand] private Task Refresh() => RefreshAsync(force: true);

    public async Task RefreshAsync(bool force)
    {
        if (IsFriendsTab) await LoadFriendsAsync(force);
        else if (SelectedGame is { } g) await LoadAchievementsAsync(g, force);
        else await LoadGamesAsync(force);
    }

    /// <summary>B: leave a game's list for the games. False when there's nothing to go back from.</summary>
    public bool Back()
    {
        if (ShowGame) { CloseGame(); return true; }
        return false;
    }

    [RelayCommand]
    private void CloseGame()
    {
        _gameVersion++;
        SelectedGame = null;
        Achievements.Clear();
        AchievementsError = "";
    }

    [RelayCommand]
    private async Task OpenGame(GameProgressViewModel? g)
    {
        if (g is null) return;
        SelectedGame = g;
        Achievements.Clear();
        GameSummary = g.ScoreText;
        await LoadAchievementsAsync(g, force: false);
    }

    private async Task LoadFriendsAsync(bool force)
    {
        if (IsLoadingFriends || !force && FriendsLoaded && DateTime.UtcNow - _friendsAt < FreshFor) return;
        IsLoadingFriends = true;
        FriendsError = "";
        try
        {
            var friends = await _xbox.Social.GetFriendsAsync(_settings.Current.Locale);
            _friendsAt = DateTime.UtcNow;
            var now = DateTimeOffset.UtcNow;
            Online.Clear();
            Offline.Clear();
            foreach (var f in friends)
            {
                var vm = new FriendViewModel(f, now);
                (f.IsOnline ? Online : Offline).Add(vm);
                vm.LoadPicture(_pictures);
            }
            FriendsSummary = friends.Count == 0 ? "" : $"{Online.Count} online · {friends.Count} {(friends.Count == 1 ? "friend" : "friends")}";
            FriendsLoaded = true;
        }
        catch (Exception ex) { FriendsError = Problem(ex, "your friends"); }
        finally
        {
            IsLoadingFriends = false;
            OnPropertyChanged(nameof(HasOnline)); OnPropertyChanged(nameof(HasOffline));
            OnPropertyChanged(nameof(NoFriends)); OnPropertyChanged(nameof(NobodyOnline));
        }
    }

    private async Task LoadGamesAsync(bool force)
    {
        if (IsLoadingGames || !force && GamesLoaded && DateTime.UtcNow - _gamesAt < FreshFor) return;
        IsLoadingGames = true;
        GamesError = "";
        try
        {
            var games = await _xbox.Social.GetGamesAsync(_settings.Current.Locale);
            _gamesAt = DateTime.UtcNow;
            var open = OpenGameCommand;
            Games.Clear();
            foreach (var g in games)
            {
                var vm = new GameProgressViewModel(g, open);
                Games.Add(vm);
                vm.LoadArt(_pictures);
            }
            int score = games.Sum(g => g.Gamerscore);
            GamesSummary = games.Count == 0 ? "" : $"{games.Count} recently played · {score.ToString("N0", CultureInfo.CurrentCulture)} G earned";
            GamesLoaded = true;
        }
        catch (Exception ex) { GamesError = Problem(ex, "your games"); }
        finally { IsLoadingGames = false; OnPropertyChanged(nameof(NoGames)); }
    }

    private async Task LoadAchievementsAsync(GameProgressViewModel g, bool force)
    {
        string id = g.Game.TitleId;
        int v = ++_gameVersion;
        if (!force && _achievements.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.At < FreshFor)
        {
            ShowAchievements(g, cached.List);
            return;
        }
        IsLoadingAchievements = true;
        AchievementsError = "";
        NoAchievements = false;
        try
        {
            var list = await _xbox.Social.GetAchievementsAsync(id, _settings.Current.Locale);
            _achievements[id] = (DateTime.UtcNow, list);
            if (v == _gameVersion) ShowAchievements(g, list);
        }
        catch (Exception ex) { if (v == _gameVersion) AchievementsError = Problem(ex, "this game's achievements"); }
        finally { if (v == _gameVersion) IsLoadingAchievements = false; }
    }

    private void ShowAchievements(GameProgressViewModel g, IReadOnlyList<Achievement> list)
    {
        Achievements.Clear();
        foreach (var a in list)
        {
            var vm = new AchievementViewModel(a);
            Achievements.Add(vm);
            vm.LoadIcon(_pictures);
        }
        int unlocked = list.Count(a => a.Unlocked);
        GameSummary = list.Count == 0 ? g.ScoreText : $"{unlocked} of {list.Count} unlocked · {g.ScoreText}";
        NoAchievements = list.Count == 0;
        AchievementsError = "";
        IsLoadingAchievements = false;
    }

    /// <summary>Signed out (another account may sign in): forget everything.</summary>
    public void Forget()
    {
        CloseGame();
        Online.Clear(); Offline.Clear(); Games.Clear();
        _achievements.Clear();
        _pictures.Clear();
        FriendsLoaded = GamesLoaded = false;
        Tab = "Friends";
    }

    private static string Problem(Exception ex, string what) => ex switch
    {
        HttpRequestException or TaskCanceledException => $"Couldn't load {what}: you seem to be offline. Check the connection, then press Refresh (Y).",
        XboxApiException { Status: 403 } => $"Xbox Live won't share {what} with Vexel. Check the account's privacy settings on xbox.com.",
        XboxApiException { Status: >= 500 } => $"Xbox Live isn't answering right now. Try again in a minute.",
        _ => $"Couldn't load {what}. " + PlayerSession.Friendly(ex),
    };
}
