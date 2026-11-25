using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Xbox;
using XStream.Player;

namespace XStream.App.ViewModels;

/// <summary>One cloud game tile: name, publisher, cover art (loaded when the tile is first shown).</summary>
public sealed partial class CloudGameViewModel(CloudGame game, CloudLibrary library, IRelayCommand<CloudGameViewModel> stream) : ObservableObject
{
    public CloudGame Game { get; } = game;
    public string Name => Game.Name;
    public string Publisher => Game.Publisher;
    public bool FreeToPlay => Game.FreeToPlay;
    public IRelayCommand<CloudGameViewModel> StreamCommand { get; } = stream;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPoster))] public partial Bitmap? Poster { get; set; }
    /// <summary>Shown in the hero banner right now (its carousel dot is lit).</summary>
    [ObservableProperty] public partial bool IsFeatured { get; set; }
    public string GenreText => Game.Genres is { Count: > 0 } g ? string.Join(" · ", g.Take(2)) : "";
    public bool HasPoster => Poster is not null;
    private bool _loading;

    public async void LoadArt()
    {
        if (_loading || Poster is not null) return;
        _loading = true;
        var path = await library.ArtAsync(Game);
        if (path is null) return;
        try
        {
            var bmp = await Task.Run(() => { using var f = File.OpenRead(path); return Bitmap.DecodeToWidth(f, 240); });
            Poster = bmp;
        }
        catch { }
    }
}

/// <summary>
/// The Cloud page: Xbox Cloud Gaming games this account can play (Game Pass, or free-to-play only), recently played
/// first, with search. Starting one goes through the same connecting screen and player as a console.
/// </summary>
public sealed partial class CloudViewModel : ObservableObject
{
    private const int PageSize = 48;
    private readonly XboxService _xbox;
    private readonly SettingsStore _settings;
    private readonly CloudLibrary _library;
    private List<CloudGameViewModel> _all = [];
    private int _shown = PageSize;

    public CloudViewModel(XboxService xbox, SettingsStore settings, StreamViewModel stream)
    {
        _xbox = xbox;
        _settings = settings;
        _library = new CloudLibrary(xbox);
        Stream = stream;
        stream.RunningChanged += _ => StartStreamCommand.NotifyCanExecuteChanged();
        stream.StreamAgainRequested += id =>
        {
            if (!id.StartsWith(PlayerHost.CloudPrefix)) return;
            var g = _all.FirstOrDefault(x => x.Game.TitleId == id[PlayerHost.CloudPrefix.Length..]);
            if (g is not null && StartStreamCommand.CanExecute(g)) StartStreamCommand.Execute(g);
        };
        IsListView = settings.Current.CloudListView;
        _carousel.Tick += (_, _) => { if (DateTime.UtcNow >= _carouselPausedUntil && Search.Length == 0) StepHero(+1, manual: false); };
        if (_library.Cached is { } cached) Apply(cached);
    }

    public StreamViewModel Stream { get; }
    public CloudLibrary Library => _library;
    public ObservableCollection<CloudGameViewModel> Recent { get; } = [];
    public ObservableCollection<CloudGameViewModel> Games { get; } = [];

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    [ObservableProperty] public partial string AccessText { get; set; } = "";
    [ObservableProperty] public partial bool Loaded { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMore))] public partial int MatchCount { get; set; }
    [ObservableProperty] public partial string Search { get; set; } = "";
    public bool HasError => Error.Length > 0;

    // ---- Hero banner: the featured game (most recently played; follows the selection in the recent row) ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFeatured))] public partial CloudGameViewModel? Featured { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFeaturedArt))] public partial Bitmap? FeaturedArt { get; set; }
    public bool HasFeatured => Featured is not null && !IsFiltering;
    public bool HasFeaturedArt => FeaturedArt is not null;
    private int _featureVersion;

    /// <summary>Show this game in the hero banner (wide art, name, Play).</summary>
    public async void Feature(CloudGameViewModel? g)
    {
        if (g is null || g == Featured) return;
        if (Featured is not null) Featured.IsFeatured = false;
        Featured = g;
        g.IsFeatured = true;
        int v = ++_featureVersion;
        var path = await _library.ArtAsync(g.Game, hero: true);
        if (v != _featureVersion) return;
        if (path is null) { FeaturedArt = null; return; }
        try
        {
            var bmp = await Task.Run(() => { using var f = File.OpenRead(path); return Bitmap.DecodeToWidth(f, 1600); });
            if (v == _featureVersion) FeaturedArt = bmp; else bmp.Dispose();
        }
        catch { if (v == _featureVersion) FeaturedArt = null; }
    }

    [RelayCommand] private void PlayFeatured() { if (Featured is { } g && StartStreamCommand.CanExecute(g)) StartStreamCommand.Execute(g); }
    public bool HasRecent => Recent.Count > 0 && !IsFiltering;
    public bool HasMore => MatchCount > Games.Count;

    partial void OnSearchChanged(string value) { _shown = PageSize; Filter(); }

    // ---- Search: genre chips, sort, grid/list ----
    public ObservableCollection<string> Genres { get; } = [];
    [ObservableProperty] public partial string? SelectedGenre { get; set; }
    partial void OnSelectedGenreChanged(string? value) { _shown = PageSize; Filter(); }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SortNewest), nameof(SortName), nameof(SortRelevance))] public partial CloudSort Sort { get; set; }
    public bool SortRelevance => Sort == CloudSort.Relevance;
    public bool SortName => Sort == CloudSort.Name;
    public bool SortNewest => Sort == CloudSort.Newest;
    partial void OnSortChanged(CloudSort value) => Filter();
    [RelayCommand] private void SetSort(string mode) => Sort = Enum.TryParse<CloudSort>(mode, out var s) ? s : CloudSort.Relevance;
    [ObservableProperty] public partial string ResultText { get; set; } = "";
    [ObservableProperty] public partial bool NoResults { get; set; }
    public bool IsFiltering => Search.Trim().Length > 0 || SelectedGenre is { Length: > 0 } g && g != AllGenres;
    public const string AllGenres = "All";
    [RelayCommand] private void ClearSearch() { Search = ""; SelectedGenre = AllGenres; }

    /// <summary>All games as a list (cover, name, publisher, genres) instead of a grid of covers. Remembered.</summary>
    [ObservableProperty] public partial bool IsListView { get; set; }
    partial void OnIsListViewChanged(bool value) { if (_settings.Current.CloudListView != value) _settings.Update(s => s with { CloudListView = value }); }
    [RelayCommand] private void ShowGrid() => IsListView = false;
    [RelayCommand] private void ShowList() => IsListView = true;

    // ---- Hero carousel: recently played, rotating every few seconds; ‹ › and dots ----
    public ObservableCollection<CloudGameViewModel> HeroItems { get; } = [];
    private readonly DispatcherTimer _carousel = new() { Interval = TimeSpan.FromSeconds(9) };
    private DateTime _carouselPausedUntil;
    [RelayCommand] private void NextHero() => StepHero(+1, manual: true);
    [RelayCommand] private void PrevHero() => StepHero(-1, manual: true);
    [RelayCommand] private void ShowHero(CloudGameViewModel? g) { if (g is not null) { Feature(g); RestartCarousel(); } }

    private void StepHero(int dir, bool manual)
    {
        if (HeroItems.Count == 0) return;
        int i = Featured is null ? -1 : HeroItems.IndexOf(Featured);
        Feature(HeroItems[((i < 0 ? 0 : i + dir) % HeroItems.Count + HeroItems.Count) % HeroItems.Count]);
        if (manual) RestartCarousel();
    }

    private void RestartCarousel() { _carousel.Stop(); _carousel.Start(); }

    /// <summary>The player is looking at the recently played row: hold the carousel still for a while.</summary>
    public void PauseCarousel() => _carouselPausedUntil = DateTime.UtcNow.AddSeconds(20);

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        Error = "";
        try { Apply(await _library.RefreshAsync(_settings.Current)); }
        catch (XboxApiException ex) when (ex.Status == 403)
        {
            Error = "Xbox Cloud Gaming isn't available for this account or in your region. It needs Xbox Game Pass Ultimate " +
                    "(free-to-play games like Fortnite work without it where cloud gaming is offered).";
        }
        catch (Exception ex) { Error = PlayerSession.Friendly(ex); }
        finally { IsLoading = false; }
    }

    [RelayCommand] private void ShowMore() { _shown += PageSize; Filter(); }

    [RelayCommand(CanExecute = nameof(CanStream))]
    private async Task StartStream(CloudGameViewModel? g)
    {
        if (g is null) return;
        // Connecting screen: this game's wide art first, then the rest of the library art.
        var hero = await _library.ArtAsync(g.Game, hero: true);
        Stream.StartCloud(g.Game, hero);
    }

    private bool CanStream(CloudGameViewModel? g) => !Stream.IsRunning;

    /// <summary>Command line --cloud TITLEID: stream that cloud game (from the library if it's there).</summary>
    public async Task<string?> StreamTitleAsync(string titleId)
    {
        if (_all.Count == 0) await RefreshAsync();
        var g = _all.FirstOrDefault(x => x.Game.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase)
                                       || x.Game.ProductId.Equals(titleId, StringComparison.OrdinalIgnoreCase));
        if (g is null) return $"No cloud game with the id {titleId} for this account";
        if (StartStreamCommand.CanExecute(g)) await StartStreamCommand.ExecuteAsync(g);
        return null;
    }

    private void Apply(CloudLibrary.Snapshot snap)
    {
        var cmd = StartStreamCommand;
        _all = snap.Games.Select(g => new CloudGameViewModel(g, _library, cmd)).ToList();
        var byId = _all.ToDictionary(g => g.Game.TitleId, StringComparer.OrdinalIgnoreCase);
        Recent.Clear();
        foreach (var id in snap.RecentTitleIds) if (byId.TryGetValue(id, out var g)) Recent.Add(g);
        AccessText = snap.Offering == "xgpuweb"
            ? $"Game Pass Ultimate · {snap.Games.Count} games you can stream"
            : $"Free-to-play games · {snap.Games.Count} games (Game Pass Ultimate unlocks the full library)";
        Loaded = true;
        Genres.Clear();
        Genres.Add(AllGenres);
        foreach (var genre in CloudSearch.Genres(snap.Games)) Genres.Add(genre);
        SelectedGenre ??= AllGenres;
        Filter();
        foreach (var r in Recent) r.LoadArt();
        HeroItems.Clear();
        foreach (var h in (Recent.Count > 0 ? Recent : (IEnumerable<CloudGameViewModel>)_all).Take(8)) HeroItems.Add(h);
        if (Featured is not null) Featured.IsFeatured = false;
        Featured = null;
        Feature(HeroItems.FirstOrDefault());
        RestartCarousel();
    }

    private void Filter()
    {
        var byId = _all.ToDictionary(g => g.Game.TitleId, StringComparer.OrdinalIgnoreCase);
        string? genre = SelectedGenre is { Length: > 0 } g && g != AllGenres ? g : null;
        var matches = CloudSearch.Filter(_all.Select(x => x.Game), Search, genre, Sort)
            .Select(m => byId[m.TitleId]).ToList();
        MatchCount = matches.Count;
        Games.Clear();
        foreach (var vm in matches.Take(_shown)) { Games.Add(vm); vm.LoadArt(); }
        string q = Search.Trim();
        NoResults = Loaded && matches.Count == 0;
        ResultText = q.Length == 0 && genre is null ? ""
            : matches.Count == 0 ? $"No games match {(q.Length > 0 ? $"\u201c{q}\u201d" : "")}{(genre is null ? "" : $" in {genre}")}. Try fewer words or another genre."
            : $"{matches.Count} {(matches.Count == 1 ? "game" : "games")}{(q.Length > 0 ? $" for \u201c{q}\u201d" : "")}{(genre is null ? "" : $" in {genre}")}";
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(HasFeatured));
        OnPropertyChanged(nameof(IsFiltering));
    }
}
