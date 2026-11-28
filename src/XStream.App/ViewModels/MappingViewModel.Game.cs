using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.Core.Config;
using XStream.Core.Input;

namespace XStream.App.ViewModels;

/// <summary>
/// The Mapping page's "This game" section: a game's own keyboard and mouse and picture settings, each "Same as all
/// games" (not stored, so it follows the defaults) or a value. Saved at once into the game's profile; a running stream
/// switches when that game is on.
/// </summary>
public sealed partial class MappingViewModel
{
    public string[] GameKbmNames { get; } = ["Same as all games", "Real keyboard and mouse", "Controller emulation", "Off"];
    public string[] GameMouseNames { get; } = ["Same as all games", "Its own speed"];
    public string[] GameInvertNames { get; } = ["Same as all games", "Normal", "Inverted"];
    public string[] GameEnhanceNames { get; } = ["Same as all games", "On", "Off"];
    public string[] GameClarityNames { get; } = ["Same as all games", "Off", "Low", "Medium", "High"];

    [ObservableProperty] public partial int GameKbmIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GameMouseOwn))] public partial int GameMouseIndex { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GameMouseText))] public partial double GameMouseSensitivity { get; set; } = 1;
    [ObservableProperty] public partial int GameInvertIndex { get; set; }
    [ObservableProperty] public partial int GameEnhanceIndex { get; set; }
    [ObservableProperty] public partial int GameClarityIndex { get; set; }
    public bool GameMouseOwn => GameMouseIndex == 1;
    public string GameMouseText => $"{GameMouseSensitivity:0.00}x";
    /// <summary>What "Same as all games" means right now.</summary>
    [ObservableProperty] public partial string GameDefaultsText { get; set; } = "";
    [ObservableProperty] public partial bool GameHasOverrides { get; set; }

    partial void OnGameKbmIndexChanged(int value) => SaveGame();
    partial void OnGameMouseIndexChanged(int value) => SaveGame();
    partial void OnGameMouseSensitivityChanged(double value) => SaveGame();
    partial void OnGameInvertIndexChanged(int value) => SaveGame();
    partial void OnGameEnhanceIndexChanged(int value) => SaveGame();
    partial void OnGameClarityIndexChanged(int value) => SaveGame();

    private bool _loadingGame;

    private void LoadGame()
    {
        _loadingGame = true;
        try
        {
            var s = _store.Current;
            var p = s.GameProfile(Selected is { IsDefault: false } g ? g.Key : null);
            GameKbmIndex = p?.KeyboardMouse == KeyboardMouseMode.Off ? 3 : p?.KeyboardMouseNative switch { true => 1, false => 2, null => 0 };
            GameMouseIndex = p?.MouseSensitivity is null ? 0 : 1;
            GameMouseSensitivity = Math.Clamp(p?.MouseSensitivity ?? s.MouseSensitivity, 0.1, 4);
            GameInvertIndex = p?.MouseInvertY switch { false => 1, true => 2, null => 0 };
            GameEnhanceIndex = p?.Enhance switch { true => 1, false => 2, null => 0 };
            GameClarityIndex = p?.Clarity is { } c ? (int)c + 1 : 0;
            GameHasOverrides = p is { HasOverrides: true };
            string kbm = s.KeyboardMouse == KeyboardMouseMode.Off ? "keyboard and mouse off"
                : s.KeyboardMouseNative ? "real keyboard and mouse" : "controller emulation";
            GameDefaultsText = $"All games: {kbm}, mouse {s.MouseSensitivity:0.00}x, Enhance {(s.Enhance ? "on" : "off")}, Clarity {s.Clarity}.";
        }
        finally { _loadingGame = false; }
    }

    private void SaveGame()
    {
        if (_loading || _loadingGame || Selected is not { IsDefault: false } g) return;
        KeyboardMouseMode? mode = GameKbmIndex == 3 ? KeyboardMouseMode.Off : null;
        bool? native = GameKbmIndex switch { 1 => true, 2 => false, _ => null };
        double? sens = GameMouseIndex == 1 ? Math.Round(GameMouseSensitivity, 2) : null;
        bool? invert = GameInvertIndex switch { 1 => false, 2 => true, _ => null };
        bool? enhance = GameEnhanceIndex switch { 1 => true, 2 => false, _ => null };
        ClarityLevel? clarity = GameClarityIndex > 0 ? (ClarityLevel)Math.Clamp(GameClarityIndex - 1, 0, 3) : null;
        Save(s =>
        {
            // A game without a layout of its own gets a settings-only profile: its buttons still follow all games.
            var p = (s.GameProfile(g.Key) ?? new ControllerProfile(g.Key, g.Name) { OwnLayout = false }) with
            {
                KeyboardMouse = mode, KeyboardMouseNative = native, MouseSensitivity = sens, MouseInvertY = invert, Enhance = enhance, Clarity = clarity,
            };
            return p is { HasOwnLayout: false, HasOverrides: false } ? s.WithoutProfile(g.Key) : s.WithProfile(p);
        });
        var now = _store.Current.GameProfile(g.Key);
        g.HasSettings = GameHasOverrides = now is { HasOverrides: true };
    }

    /// <summary>This game's keyboard, mouse and picture back to "Same as all games" (its layout stays).</summary>
    [RelayCommand]
    private void ResetGame()
    {
        if (Selected is not { IsDefault: false }) return;
        _loadingGame = true;
        try { GameKbmIndex = GameMouseIndex = GameInvertIndex = GameEnhanceIndex = GameClarityIndex = 0; }
        finally { _loadingGame = false; }
        SaveGame();
    }
}
