using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Together;
using XStream.Player;

namespace XStream.App.ViewModels;

/// <summary>One row of the "Your stream" card's player list: you (Player 1), then each guest.</summary>
public sealed partial class TogetherPlayerRow(int slot) : ObservableObject
{
    public int Slot { get; } = slot;
    public string Label => $"Player {Slot + 1}";
    public bool IsHost => Slot == 0;
    public bool CanRemove => !IsHost;
    [ObservableProperty] public partial string Name { get; set; } = "";
    /// <summary>"23 ms ping · 0.2% loss" for a guest; empty for you.</summary>
    [ObservableProperty] public partial string Detail { get; set; } = "";
    /// <summary>The guest's connection is struggling (loss over 2% or ping over 120 ms).</summary>
    [ObservableProperty] public partial bool Poor { get; set; }
}

/// <summary>
/// The Play together page: share the running stream and manage who's in (the same as the stream menu's panel, from the
/// main window), and join a friend's stream with their invite. Works signed out too: a friend joining needs Vexel,
/// not an Xbox account.
/// </summary>
public sealed partial class TogetherViewModel : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly XboxService _xbox;

    public StreamViewModel Stream { get; }

    public TogetherViewModel(SettingsStore settings, StreamViewModel stream, XboxService xbox)
    {
        _settings = settings;
        _xbox = xbox;
        Stream = stream;
        Name = settings.Current.TogetherName is { Length: > 0 } n ? n : Environment.UserName;
        stream.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StreamViewModel.Together) or nameof(StreamViewModel.IsRunning) or nameof(StreamViewModel.IsGuest)
                or nameof(StreamViewModel.IsStreaming))
            {
                UpdatePlayers();
                foreach (var n in (string[])[nameof(SharingText), nameof(SharingInvite), nameof(HasInvite), nameof(ShowShareCard), nameof(ShowGuestCard),
                             nameof(NoStream), nameof(ShareNote), nameof(HasShareNote), nameof(ShareNoteIsProblem), nameof(CapText), nameof(CanJoin)])
                    OnPropertyChanged(n);
                if (!Stream.IsSharing) { ReplyText = ""; ReplyError = ""; }
            }
        };
    }

    // ---- Your stream (host) ----------------------------------------------------------------------------------
    /// <summary>The "Your stream" card: everywhere except while watching a friend's stream.</summary>
    public bool ShowShareCard => !(Stream.IsRunning && Stream.IsGuest);
    public bool ShowGuestCard => Stream.IsRunning && Stream.IsGuest;
    public bool NoStream => !Stream.IsRunning;
    public string SharingText => Stream.Together is { Role: "host", Players: > 0 } t
        ? t.Players <= 1 ? "Sharing. Waiting for a friend to join" : $"Playing together: {t.Players} players"
        : "";
    public string SharingInvite => Stream.Together?.Invite ?? "";
    public bool HasInvite => SharingInvite.Length > 0;
    public string ShareNote => Stream.Together?.Note ?? "";
    public bool HasShareNote => ShareNote.Length > 0;
    public bool ShareNoteIsProblem => Stream.Together?.NoteIsProblem ?? false;
    /// <summary>"Picture capped at 12.5 Mbps right now" once it's below the usual cap (a friend losing packets).</summary>
    public string CapText => Stream.Together is { CapMbps: { } c } && c < 14.5 ? $"Lowered to {c:F1} Mbps for now: a friend's connection is losing packets." : "";
    public System.Collections.ObjectModel.ObservableCollection<TogetherPlayerRow> Players { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReplyError))] public partial string ReplyText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasReplyError))] public partial string ReplyError { get; set; } = "";
    public bool HasReplyError => ReplyError.Length > 0;
    [ObservableProperty] public partial string CopiedText { get; set; } = "";
    partial void OnReplyTextChanged(string value) => ReplyError = "";
    /// <summary>"Start a stream" pressed with nothing running: the shell shows Consoles.</summary>
    public event Action? ConsolesRequested;

    [RelayCommand] private void GoToConsoles() => ConsolesRequested?.Invoke();

    /// <summary>Update the rows in place (not rebuilt): status comes every second while anyone's in, and a rebuilt list would
    /// take the controller's focus off a Remove button.</summary>
    private void UpdatePlayers()
    {
        if (!Stream.IsSharing) { Players.Clear(); return; }
        var t = Stream.Together!;
        // Older status (no People): names only, slots in order.
        var people = t.People ?? t.Guests.Select((n, i) => new TogetherPlayer { Slot = i + 1, Name = n }).ToArray();
        var want = new List<(int slot, string name, string detail, bool poor)> { (0, "You", "", false) };
        foreach (var p in people.OrderBy(p => p.Slot))
        {
            var bits = new List<string>();
            if (double.IsFinite(p.PingMs)) bits.Add($"{p.PingMs:F0} ms ping");
            if (t.People is not null) bits.Add($"{p.LossPct:F1}% loss");
            want.Add((p.Slot, p.Name, string.Join("  ·  ", bits), p.LossPct > 2 || p.PingMs > 120));
        }
        for (int i = Players.Count - 1; i >= 0; i--)
            if (!want.Any(w => w.slot == Players[i].Slot)) Players.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            var w = want[i];
            var row = Players.FirstOrDefault(r => r.Slot == w.slot);
            if (row is null) { row = new TogetherPlayerRow(w.slot); Players.Insert(Math.Min(i, Players.Count), row); }
            row.Name = w.name; row.Detail = w.detail; row.Poor = w.poor;
        }
    }

    [RelayCommand]
    private void AcceptReply()
    {
        if (TogetherReply.TryParse(ReplyText, out var problem) is null) { ReplyError = problem; return; }
        ReplyError = "";
        Stream.AcceptReply(ReplyText.Trim());
        ReplyText = "";
    }

    /// <summary>Paste the reply code from the clipboard and accept it in one go (a controller can't reach the text box).</summary>
    [RelayCommand]
    private async Task PasteReply()
    {
        string text = Platform.MainWindow?.Clipboard is { } cb ? await cb.GetTextAsync() ?? "" : "";
        if (text.Trim().Length == 0) { ReplyError = "The clipboard is empty. Copy your friend's reply code first."; return; }
        ReplyText = text.Trim();
        AcceptReply();
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanJoin))] public partial string Invite { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanJoin))] public partial string Name { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] public partial string Error { get; set; } = "";
    public bool HasError => Error.Length > 0;
    public bool CanJoin => Invite.Trim().Length > 0 && Name.Trim().Length > 0 && !Stream.IsRunning;

    partial void OnInviteChanged(string value) => Error = "";


    /// <summary>Use the gamertag as the default name, once (only if the name is still the computer's user name).</summary>
    public async void Opened()
    {
        if (_settings.Current.TogetherName.Length > 0 || Name != Environment.UserName) return;
        if (await _xbox.GamertagAsync() is { Length: > 0 } tag && Name == Environment.UserName) Name = tag;
    }

    [RelayCommand]
    private async Task PasteInvite()
    {
        if (Platform.MainWindow?.Clipboard is { } cb && await cb.GetTextAsync() is { Length: > 0 } text) Invite = text.Trim();
    }

    [RelayCommand]
    private async Task CopyInvite()
    {
        if (SharingInvite.Length == 0) return;
        await Platform.CopyAsync(SharingInvite);
        CopiedText = "Copied. Send it to your friend in a message.";
        Avalonia.Threading.DispatcherTimer.RunOnce(() => CopiedText = "", TimeSpan.FromSeconds(4));
    }

    [RelayCommand]
    private void Join()
    {
        if (Stream.IsRunning) { Error = "A stream is already running. End it first."; return; }
        if (TogetherInvite.TryParse(Invite, out var problem) is null) { Error = problem; return; }
        string name = Name.Trim();
        if (name.Length == 0) { Error = "Type the name your friend will see."; return; }
        Error = "";
        _settings.Update(s => s with { TogetherName = name });
        Stream.StartGuest(Invite.Trim(), name);
    }

    /// <summary>XSTREAM_DEMO_PAGE=Play together (testing): sample text in the boxes (nothing is joined).</summary>
    public void Demo()
    {
        Name = "Sam";
        Invite = "VX1-06FW5V588ZF27SQFC2S7311GYW9HMWDV4PT0PXBQEJZ4ZERKH6PF4G1V3XT04060N123VJ270HZG0001S13R6E0";
    }
}
