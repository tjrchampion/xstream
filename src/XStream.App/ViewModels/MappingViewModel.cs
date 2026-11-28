using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Config;
using XStream.Core.Input;

namespace XStream.App.ViewModels;

/// <summary>A game tile on the Mapping page (or the "All games" tile, with an empty key).</summary>
public sealed partial class MappingGameViewModel : ObservableObject
{
    public MappingGameViewModel(string key, string name, string? artPath, IRelayCommand<MappingGameViewModel> select)
    {
        Key = key; Name = name; SelectCommand = select;
        if (artPath is not null) _ = Load(artPath);
    }

    public string Key { get; }
    public string Name { get; }
    public bool IsDefault => Key.Length == 0;
    public bool IsCloud => Key.StartsWith("cloud:", StringComparison.Ordinal);
    public IRelayCommand<MappingGameViewModel> SelectCommand { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasArt))] public partial Bitmap? Art { get; set; }
    public bool HasArt => Art is not null;
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsPlayingNow { get; set; }
    /// <summary>Has its own layout (vs using the default).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSettingsChip))] public partial bool HasLayout { get; set; }
    /// <summary>Has its own keyboard, mouse or picture settings.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowSettingsChip))] public partial bool HasSettings { get; set; }
    /// <summary>"Own settings" chip: only when there's no "Own layout" chip already.</summary>
    public bool ShowSettingsChip => HasSettings && !HasLayout;

    public async Task Load(string path)
    {
        try { Art = await Task.Run(() => { using var f = File.OpenRead(path); return Bitmap.DecodeToWidth(f, 480); }); }
        catch { /* no art: the tile shows the name */ }
    }
}

/// <summary>One button on the controller drawing: what pressing it does now.</summary>
public sealed partial class ButtonSlotViewModel(string source, string label, IRelayCommand<ButtonSlotViewModel> pick) : ObservableObject
{
    /// <summary>Name used in the mapping (A, LeftShoulder, Paddle1…).</summary>
    public string Source { get; } = source;
    /// <summary>Short name on the drawing (A, LB, P1…).</summary>
    public string Label { get; } = label;
    public IRelayCommand<ButtonSlotViewModel> PickCommand { get; } = pick;
    public bool IsPaddle => Source.StartsWith("Paddle", StringComparison.Ordinal);
    public bool IsTrigger => Source is "LeftTrigger" or "RightTrigger";

    /// <summary>Normal, turbo or toggle (applies to what the button presses, after remapping).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ModeBadge), nameof(ShowModeBadge), nameof(IsToggle), nameof(Describe))]
    public partial ButtonMode Mode { get; set; }
    public bool IsToggle => Mode == ButtonMode.Toggle;
    /// <summary>Small round tag on the drawing: T for turbo, a loop for toggle.</summary>
    public string ModeBadge => Mode switch { ButtonMode.Turbo => "T", ButtonMode.Toggle => "⟳", _ => "" };
    public bool ShowModeBadge => !ShowKeys && Mode != ButtonMode.Normal;

    /// <summary>What it does: a button name, "None" (off), or "" (itself).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsRemapped), nameof(IsOff), nameof(Badge), nameof(Describe), nameof(ShowBadge))]
    public partial string Target { get; set; } = "";
    [ObservableProperty] public partial bool IsSelected { get; set; }

    /// <summary>Keyboard & mouse view: the tag shows this button's keys instead of its remapping.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowBadge), nameof(ShowModeBadge))] public partial bool ShowKeys { get; set; }
    /// <summary>Its keys, short ("Space", "LMB / C"), "" when none.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(KeyTag), nameof(NoKeys))] public partial string Keys { get; set; } = "";
    public string KeyTag => Keys.Length == 0 ? "none" : Keys;
    public bool NoKeys => Keys.Length == 0;
    /// <summary>The remapping tag (controller view only).</summary>
    public bool ShowBadge => !ShowKeys && IsRemapped;

    public bool IsRemapped => Target.Length > 0;
    public bool IsOff => Target == "None";
    /// <summary>Little tag on the drawing: what it's been changed to.</summary>
    public string Badge => Target.Length == 0 ? "" : Target == "None" ? "off" : "→ " + MappingViewModel.Short(Target);
    public string Describe => (Target.Length == 0
        ? (IsPaddle ? "Not used" : "Unchanged")
        : Target == "None" ? "Turned off" : "Acts as " + MappingViewModel.Long(Target))
        + Mode switch { ButtonMode.Turbo => ", turbo", ButtonMode.Toggle => ", toggle", _ => "" };
}

/// <summary>A choice in the "When you press X" panel (highlighted when it's what the button does now).</summary>
public sealed partial class TargetChoice(string target, string label, IRelayCommand<TargetChoice> choose) : ObservableObject
{
    public string Target { get; } = target;
    public string Label { get; } = label;
    public IRelayCommand<TargetChoice> Choose { get; } = choose;
    [ObservableProperty] public partial bool IsCurrent { get; set; }
}

/// <summary>
/// The Mapping page: pick a game (art tiles: what's playing now, recent games, ones with their own layout, or
/// "All games"), then change what each button does by clicking it on a drawing of the controller. Changes save at
/// once; a running stream switches to them straight away.
/// </summary>
public sealed partial class MappingViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly GameArtService _art;
    private bool _loading;

    public MappingViewModel(SettingsStore store, GameArtService art)
    {
        _store = store;
        _art = art;
        ButtonSlotViewModel S(string source, string label) => new(source, label, PickSlotCommand);
        A = S("A", "A"); B = S("B", "B"); X = S("X", "X"); Y = S("Y", "Y");
        LB = S("LeftShoulder", "LB"); RB = S("RightShoulder", "RB");
        LS = S("LeftThumb", "LS"); RS = S("RightThumb", "RS");
        View = S("View", "View"); Menu = S("Menu", "Menu"); Nexus = S("Nexus", "Xbox");
        Up = S("DPadUp", "Up"); Down = S("DPadDown", "Down"); Left = S("DPadLeft", "Left"); Right = S("DPadRight", "Right");
        P1 = S("Paddle1", "P1"); P2 = S("Paddle2", "P2"); P3 = S("Paddle3", "P3"); P4 = S("Paddle4", "P4");
        Slots = [A, B, X, Y, LB, RB, LS, RS, View, Menu, Nexus, Up, Down, Left, Right, P1, P2, P3, P4];
        Choices = [.. new[] { "A", "B", "X", "Y", "LeftShoulder", "RightShoulder", "LeftThumb", "RightThumb", "DPadUp", "DPadDown",
                              "DPadLeft", "DPadRight", "View", "Menu", "Nexus" }
            .Select(t => new TargetChoice(t, Short(t), ChooseCommand)), new TargetChoice("None", "Off", ChooseCommand)];
        ModeChoices = [new TargetChoice(nameof(ButtonMode.Normal), "Normal", ChooseModeCommand),
                       new TargetChoice(nameof(ButtonMode.Turbo), "Turbo", ChooseModeCommand),
                       new TargetChoice(nameof(ButtonMode.Toggle), "Toggle", ChooseModeCommand)];
        InitKeyboard();
        ModeSlots = [.. Slots.Where(s => !s.IsPaddle), LT, RT];
        art.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshGames);
        store.Changed += _ => { if (!_saving) { RefreshGames(); LoadSelected(); LoadKeyboard(); } };
        RefreshGames();
        Select(Games.FirstOrDefault());
    }

    // ---- Games --------------------------------------------------------------------------------------------
    public ObservableCollection<MappingGameViewModel> Games { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title), nameof(Scope), nameof(EditingGame), nameof(CanUseDefault), nameof(ShowGameSection))]
    public partial MappingGameViewModel? Selected { get; set; }
    public string Title => Selected is null || Selected.IsDefault ? "All games" : Selected.Name;
    public bool EditingGame => Selected is { IsDefault: false };
    /// <summary>The "This game" section: a game is chosen, on the controller view.</summary>
    public bool ShowGameSection => EditingGame && !KeyboardView;
    public bool CanUseDefault => Selected is { IsDefault: false, HasLayout: true };
    public string Scope => Selected switch
    {
        null or { IsDefault: true } => "Your layout for every game that doesn't have its own.",
        { HasLayout: true } g => $"Used whenever {g.Name} is running. Vexel switches to it by itself, even mid-stream.",
        var g => $"{g.Name} uses your default layout. Change anything below to give it its own.",
    };

    /// <summary>Set by the shell: recent cloud games (key cloud:ID, name, art path or null).</summary>
    public Func<IEnumerable<(string Key, string Name, Func<Task<string?>> Art)>>? CloudGames { get; set; }

    /// <summary>Refill the tiles: All games, what's playing now, recent console games, games with layouts, cloud games.</summary>
    public void RefreshGames()
    {
        var s = _store.Current;
        string? keep = Selected?.Key;
        string? now = _art.Current?.TitleId;
        var list = new List<MappingGameViewModel> { new("", "All games", null, SelectGameCommand) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        void Add(string key, string name, string? art)
        {
            if (key.Length == 0 || !seen.Add(key)) return;
            list.Add(new MappingGameViewModel(key, name, art, SelectGameCommand));
        }
        if (_art.Current is { } playing) Add(playing.TitleId, playing.Name, playing.Art?.Path);
        foreach (var a in _art.Items) Add(a.TitleId, a.Name, a.Path);
        foreach (var p in s.GameProfiles ?? []) if (!p.GameKey.StartsWith("cloud:")) Add(p.GameKey, p.GameName, null);
        foreach (var (key, name, art) in CloudGames?.Invoke() ?? [])
        {
            if (!seen.Add(key)) continue;
            var vm = new MappingGameViewModel(key, name, null, SelectGameCommand);
            list.Add(vm);
            _ = Task.Run(async () => { if (await art() is { } path) await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => vm.Load(path)); });
        }
        foreach (var p in s.GameProfiles ?? []) if (p.GameKey.StartsWith("cloud:")) Add(p.GameKey, p.GameName, null);

        foreach (var g in list)
        {
            g.IsPlayingNow = g.Key.Length > 0 && g.Key == now;
            var own = s.GameProfile(g.Key);
            g.HasLayout = own is { HasOwnLayout: true };
            g.HasSettings = own is { HasOverrides: true };
        }
        Games.Clear();
        foreach (var g in list) Games.Add(g);
        var again = Games.FirstOrDefault(g => g.Key == keep) ?? Games[0];
        Select(again, load: false);
    }

    [RelayCommand] private void SelectGame(MappingGameViewModel? g) => Select(g);

    /// <summary>Open this game's layout (from the Consoles page's "Controls for this game").</summary>
    public void SelectKey(string key, string name)
    {
        RefreshGames();
        var g = Games.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (g is null) { g = new MappingGameViewModel(key, name, null, SelectGameCommand); Games.Insert(1, g); }
        Select(g);
    }

    private void Select(MappingGameViewModel? g, bool load = true)
    {
        if (g is null) return;
        foreach (var x in Games) x.IsSelected = x == g;
        Selected = g;
        if (load || _loadedKey != g.Key) LoadSelected();
    }

    private string? _loadedKey;

    // ---- The controller -----------------------------------------------------------------------------------
    public ButtonSlotViewModel A { get; }
    public ButtonSlotViewModel B { get; }
    public ButtonSlotViewModel X { get; }
    public ButtonSlotViewModel Y { get; }
    public ButtonSlotViewModel LB { get; }
    public ButtonSlotViewModel RB { get; }
    public ButtonSlotViewModel LS { get; }
    public ButtonSlotViewModel RS { get; }
    public ButtonSlotViewModel View { get; }
    public ButtonSlotViewModel Menu { get; }
    public ButtonSlotViewModel Nexus { get; }
    public ButtonSlotViewModel Up { get; }
    public ButtonSlotViewModel Down { get; }
    public ButtonSlotViewModel Left { get; }
    public ButtonSlotViewModel Right { get; }
    public ButtonSlotViewModel P1 { get; }
    public ButtonSlotViewModel P2 { get; }
    public ButtonSlotViewModel P3 { get; }
    public ButtonSlotViewModel P4 { get; }
    public IReadOnlyList<ButtonSlotViewModel> Slots { get; }
    public IReadOnlyList<TargetChoice> Choices { get; }

    /// <summary>The button being changed ("When you press A…").</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSlot), nameof(SlotTitle), nameof(SlotCanRemap), nameof(SlotCanMode), nameof(ModeHint))]
    public partial ButtonSlotViewModel? Slot { get; set; }
    public bool HasSlot => Slot is not null;
    public string SlotTitle => Slot is null ? "" : $"When you press {TargetName(Slot.Source)}";
    /// <summary>The triggers can't be remapped, only made turbo or toggle.</summary>
    public bool SlotCanRemap => Slot is { IsTrigger: false };
    /// <summary>Paddles take the mode of the button they press.</summary>
    public bool SlotCanMode => Slot is { IsPaddle: false };
    public string ModeHint => Slot switch
    {
        null => "",
        { IsPaddle: true } => "A paddle is turbo or toggle when the button it presses is.",
        { Mode: ButtonMode.Turbo } => $"Presses and lets go {TurboRateText} while you hold it.",
        { Mode: ButtonMode.Toggle, IsTrigger: true } => "Pull once to hold it fully; pull again to let go.",
        { Mode: ButtonMode.Toggle } => "Tap once to hold it down; tap again to let go.",
        _ => "Works as you press it.",
    };
    /// <summary>Normal / Turbo / Toggle for the chosen button.</summary>
    public IReadOnlyList<TargetChoice> ModeChoices { get; }
    /// <summary>Buttons that can be turbo or toggle (everything but the paddles; the triggers too).</summary>
    public IReadOnlyList<ButtonSlotViewModel> ModeSlots { get; }
    public int ChangedCount => Slots.Count(s => s.IsRemapped);
    public string ChangedText
    {
        get
        {
            string text = ChangedCount switch { 0 => "Standard layout", 1 => "1 button changed", var n => $"{n} buttons changed" };
            int turbo = ModeSlots.Count(s => s.Mode == ButtonMode.Turbo), toggle = ModeSlots.Count(s => s.Mode == ButtonMode.Toggle);
            if (turbo > 0) text += $" · {turbo} turbo";
            if (toggle > 0) text += $" · {toggle} toggle";
            return text;
        }
    }

    [RelayCommand]
    private void PickSlot(ButtonSlotViewModel? slot)
    {
        if (KeyboardView && slot is not null) { PickKey(slot); return; }
        foreach (var s in Slots.Concat([LT, RT])) s.IsSelected = s == slot;
        Slot = slot;
        MarkCurrent();
    }

    /// <summary>Highlight what the chosen button does now (itself when unchanged), and its mode.</summary>
    private void MarkCurrent()
    {
        string does = Slot is null ? "" : Slot.Target.Length == 0 ? Slot.Source : Slot.Target;
        foreach (var c in Choices) c.IsCurrent = c.Target == does;
        foreach (var c in ModeChoices) c.IsCurrent = Slot is not null && c.Target == Slot.Mode.ToString();
        OnPropertyChanged(nameof(ModeHint));
    }

    [RelayCommand]
    private void ChooseMode(TargetChoice? c)
    {
        if (Slot is not { IsPaddle: false } slot || c is null || !Enum.TryParse<ButtonMode>(c.Target, out var mode)) return;
        slot.Mode = mode;
        MarkCurrent();
        SaveLayout();
    }

    /// <summary>The turbo/toggle buttons as text ("A:turbo,LT:toggle").</summary>
    public string ModesText => ModeSlots.Aggregate(ButtonModes.None, (m, s) => m.With(s.Source, s.Mode)).ToText();

    [RelayCommand]
    private void Choose(TargetChoice? c)
    {
        if (Slot is not { } slot || c is null) return;
        // Choosing the button itself means "unchanged".
        slot.Target = c.Target == slot.Source ? "" : c.Target;
        MarkCurrent();
        SaveLayout();
    }

    [RelayCommand]
    private void ResetSlot()
    {
        if (Slot is null) return;
        Slot.Target = "";
        Slot.Mode = ButtonMode.Normal;
        MarkCurrent();
        SaveLayout();
    }

    [RelayCommand] private void CloseSlot() => PickSlot(null);

    // ---- Presets ------------------------------------------------------------------------------------------
    public static readonly (string Name, string Mapping)[] Presets =
    [
        ("Standard", ""),
        ("Swap A/B and X/Y", "A=B,B=A,X=Y,Y=X"),
        ("Paddles as A B X Y", "Paddle1=A,Paddle2=B,Paddle3=X,Paddle4=Y"),
        ("Paddles as sticks and bumpers", "Paddle1=LeftThumb,Paddle2=RightThumb,Paddle3=LeftShoulder,Paddle4=RightShoulder"),
    ];
    public IReadOnlyList<string> PresetNames { get; } = Presets.Select(p => p.Name).ToList();

    [RelayCommand]
    private void ApplyPreset(string? name)
    {
        var p = Presets.FirstOrDefault(x => x.Name == name);
        if (p.Name is null) return;
        ApplyMapping(p.Mapping);
        MarkCurrent();
        SaveLayout();
    }

    // ---- Turbo ---------------------------------------------------------------------------------------------
    /// <summary>Turbo presses a second, for this layout.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TurboRateText), nameof(ModeHint))] public partial double TurboRate { get; set; } = ButtonModes.DefaultRate;
    public string TurboRateText => $"{TurboRate:0} times a second";
    partial void OnTurboRateChanged(double value) => SaveLayout();

    // ---- Gyro ---------------------------------------------------------------------------------------------
    [ObservableProperty] public partial bool GyroAim { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(GyroText))] public partial double GyroSensitivity { get; set; } = 1;
    [ObservableProperty] public partial int GyroHoldIndex { get; set; }
    public string GyroText => $"{GyroSensitivity:0.0}x";
    public static readonly string[] GyroHoldButtons = ["", "LeftShoulder", "RightShoulder", "LeftThumb", "Paddle1", "Paddle2"];
    public string[] GyroHoldNames { get; } = ["Always", "While LB is held", "While RB is held", "While LS is pressed", "While P1 is held", "While P2 is held"];
    partial void OnGyroAimChanged(bool value) => SaveLayout();
    partial void OnGyroSensitivityChanged(double value) => SaveLayout();
    partial void OnGyroHoldIndexChanged(int value) => SaveLayout();

    /// <summary>This game goes back to the default layout (its own is deleted).</summary>
    [RelayCommand]
    private void UseDefault()
    {
        if (Selected is not { IsDefault: false } g) return;
        // Its keyboard, mouse and picture settings stay; only the layout goes back to the default.
        Save(s => s.GameProfile(g.Key) is { HasOverrides: true } p
            ? s.WithProfile(p with { OwnLayout = false, ButtonMapping = "", GyroAim = false, GyroSensitivity = 1, GyroHoldButton = "", ButtonModes = null, TurboRate = null })
            : s.WithoutProfile(g.Key));
        g.HasLayout = false;
        LoadSelected();
        Notify();
    }

    // ---- Load / save --------------------------------------------------------------------------------------
    private void LoadSelected()
    {
        _loading = true;
        try
        {
            var c = _store.Current.ControllerFor(Selected?.Key);
            _loadedKey = Selected?.Key;
            ApplyMapping(c.ButtonMapping);
            GyroAim = c.GyroAim;
            GyroSensitivity = c.GyroSensitivity;
            GyroHoldIndex = Math.Max(0, Array.IndexOf(GyroHoldButtons, c.GyroHoldButton));
            var modes = ButtonModes.Parse(c.ButtonModes);
            foreach (var slot in ModeSlots) slot.Mode = modes.Get(slot.Source);
            TurboRate = Math.Clamp(c.TurboRate ?? ButtonModes.DefaultRate, ButtonModes.MinRate, ButtonModes.MaxRate);
            LoadGame();
            MarkCurrent();
        }
        finally { _loading = false; }
        Notify();
    }

    private void ApplyMapping(string mapping)
    {
        foreach (var s in Slots) s.Target = "";
        foreach (var pair in mapping.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length != 2) continue;
            var slot = Slots.FirstOrDefault(s => s.Source.Equals(Canonical(kv[0]), StringComparison.OrdinalIgnoreCase));
            if (slot is not null) slot.Target = Canonical(kv[1]) is var t && t.Equals(slot.Source, StringComparison.OrdinalIgnoreCase) ? "" : t;
        }
    }

    /// <summary>The slots as a mapping string ("A=B,Paddle1=LeftThumb").</summary>
    public string MappingText => string.Join(",", Slots.Where(s => s.IsRemapped).Select(s => $"{s.Source}={s.Target}"));

    private bool _saving;

    private void SaveLayout()
    {
        if (_loading || Selected is not { } g) return;
        string mapping = MappingText, modes = ModesText, hold = GyroHoldButtons[Math.Clamp(GyroHoldIndex, 0, GyroHoldButtons.Length - 1)];
        double gyro = Math.Round(GyroSensitivity, 2), rate = Math.Round(Math.Clamp(TurboRate, ButtonModes.MinRate, ButtonModes.MaxRate));
        if (g.IsDefault)
            Save(s => s with { ButtonMapping = mapping, GyroAim = GyroAim, GyroSensitivity = gyro, GyroHoldButton = hold, ButtonModes = modes, TurboRate = rate });
        else
        {
            // Keep the game's keyboard, mouse and picture settings: only its layout is written here.
            Save(s => s.WithProfile((s.GameProfile(g.Key) ?? new ControllerProfile(g.Key, g.Name)) with
            {
                GameName = g.Name, ButtonMapping = mapping, GyroAim = GyroAim, GyroSensitivity = gyro, GyroHoldButton = hold,
                OwnLayout = null, ButtonModes = modes, TurboRate = rate,
            }));
            g.HasLayout = true;
        }
        Notify();
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        _saving = true;
        try { _store.Update(change); } finally { _saving = false; }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ChangedCount)); OnPropertyChanged(nameof(ChangedText)); OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(CanUseDefault)); OnPropertyChanged(nameof(MappingText)); OnPropertyChanged(nameof(ModesText));
    }

    // ---- Names --------------------------------------------------------------------------------------------
    private static string Canonical(string name) => name.ToLowerInvariant() switch
    {
        "lb" => "LeftShoulder", "rb" => "RightShoulder", "ls" or "l3" => "LeftThumb", "rs" or "r3" => "RightThumb",
        "start" => "Menu", "back" or "select" => "View", "guide" or "xbox" => "Nexus",
        "up" => "DPadUp", "down" => "DPadDown", "left" => "DPadLeft", "right" => "DPadRight", "none" => "None",
        _ => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name,
    };

    public static string Short(string target) => target switch
    {
        "LeftShoulder" => "LB", "RightShoulder" => "RB", "LeftThumb" => "LS", "RightThumb" => "RS",
        "DPadUp" => "Up", "DPadDown" => "Down", "DPadLeft" => "Left", "DPadRight" => "Right",
        "Nexus" => "Xbox", "None" => "Off", _ => target,
    };

    public static string Long(string target) => target switch
    {
        "LeftShoulder" => "LB (left bumper)", "RightShoulder" => "RB (right bumper)",
        "LeftThumb" => "LS (left stick click)", "RightThumb" => "RS (right stick click)",
        "DPadUp" => "D-pad up", "DPadDown" => "D-pad down", "DPadLeft" => "D-pad left", "DPadRight" => "D-pad right",
        "Nexus" => "the Xbox button", "View" => "View", "Menu" => "Menu",
        "Paddle1" => "paddle P1", "Paddle2" => "paddle P2", "Paddle3" => "paddle P3", "Paddle4" => "paddle P4",
        _ => target,
    };
}
