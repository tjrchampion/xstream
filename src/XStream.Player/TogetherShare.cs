using XStream.Core.Config;
using XStream.Core.Input;
using XStream.Core.Rtp;
using XStream.Core.Together;
using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// The host's side of Play together inside a stream: connects a <see cref="TogetherHost"/> to the stream menu's panel,
/// the controller loop (guests' pads in their slots), the console (keyframes, bitrate cap) and the clipboard. The
/// stream feeds it every access unit and audio packet; it does nothing until sharing starts.
/// </summary>
public sealed class TogetherShare : IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _hostName;
    private readonly StreamOverlay? _overlay;
    private readonly Func<InputPump?> _pump;
    private readonly Action _requestKeyframe;
    private readonly Action<double?> _capChanged;
    private readonly Action<string> _log;
    private readonly Func<TogetherHostOptions, TogetherHostOptions>? _tweak;
    private readonly object _lock = new();
    private readonly double[] _releasedAt = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private TogetherHost? _host;
    private string? _note;
    private bool _noteIsProblem;
    private int _lastGuestCount;
    private string? _game, _art;
    private bool _copiedThisInvite;
    // Clipboard work waits for the window's thread (macOS wants the pasteboard used from there).
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _main = new();

    /// <summary>Call from the window loop (main thread): runs the clipboard work queued from other threads.</summary>
    public void OnMainThread() { while (_main.TryDequeue(out var a)) a(); }

    /// <summary>Who's in changed, or sharing started or stopped (for the launcher and Discord's party size).</summary>
    public event Action<TogetherStatus>? Changed;
    /// <summary>A guest's controller state arrived (the CLI harness prints them).</summary>
    public event Action<int, GamepadState>? GuestInput;

    public Func<string> ClipboardGet { get; set; } = SdlClipboard.Get;
    public Func<string, bool> ClipboardSet { get; set; } = SdlClipboard.Set;

    public bool Sharing => _host is not null;
    public double? CapKbps => _host?.CapKbps;
    public string? Invite => _host?.Invite;
    public TogetherHost? Host => _host;

    /// <param name="pump">The controller loop (looked up when needed: it's made after this).</param>
    /// <param name="capChanged">Cap the console's bitrate at this (kbps), or back to the setting (null).</param>
    /// <param name="tweak">Testing: change the host's options (loopback, simulated loss, no router mapping).</param>
    public TogetherShare(AppSettings settings, string hostName, StreamOverlay? overlay, Func<InputPump?> pump,
        Action requestKeyframe, Action<double?> capChanged, Action<string> log, Func<TogetherHostOptions, TogetherHostOptions>? tweak = null)
    {
        _settings = settings;
        _hostName = hostName;
        _overlay = overlay;
        _pump = pump;
        _requestKeyframe = requestKeyframe;
        _capChanged = capChanged;
        _log = log;
        _tweak = tweak;
        if (overlay is null) return;
        overlay.TogetherOpened += () => { if (_host is null) Start(); else { _main.Enqueue(() => CopyInvite(quiet: true)); Refresh(); } };
        overlay.TogetherStopRequested += () => { Stop(); _overlay?.ShowToast("Stopped sharing. Your friends have left", 3000, ToastKind.Warn); };
        overlay.TogetherRemoveRequested += slot => _host?.Kick(slot);
        overlay.TogetherCopyInviteRequested += () => _main.Enqueue(() => CopyInvite(quiet: false));
        overlay.TogetherPasteReplyRequested += () => _main.Enqueue(PasteReply);
        overlay.SetTogether(new TogetherPanel { Sharing = false, MaxMbps = settings.TogetherMaxMbps });
    }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>Start sharing: open the port, gather addresses, make the invite (copied to the clipboard when ready).</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_host is not null) return;
            var o = new TogetherHostOptions
            {
                HostName = _hostName, Port = _settings.TogetherPort, MaxKbps = Math.Clamp(_settings.TogetherMaxMbps, 5, 100) * 1000,
                AllowGuestXboxButton = _settings.TogetherGuestXboxButton,
            };
            if (_tweak is not null) o = _tweak(o);
            var h = new TogetherHost(o, slot => _pump()?.IsLocalPad(slot) ?? slot == 0);
            h.Log += m => _log(m);
            h.InviteReady += _ => { _copiedThisInvite = false; _main.Enqueue(() => CopyInvite(quiet: false)); Refresh(); };
            h.KeyframeNeeded += () => _requestKeyframe();
            h.CapChanged += k => _capChanged(k);
            h.Changed += Refresh;
            h.GuestJoined += OnJoined;
            h.GuestLeft += OnLeft;
            h.GuestInput += (slot, s) => { _pump()?.SetRemotePad(slot, s); GuestInput?.Invoke(slot, s); };
            if (_game is not null) h.SetGame(_game, _art);
            _note = null; _noteIsProblem = false;
            _host = h;
            h.Start();
        }
        _log("Play together: sharing started");
        Refresh();
    }

    /// <summary>Stop sharing: guests are told and their controllers let go; the bitrate goes back to normal.</summary>
    public void Stop()
    {
        TogetherHost? h;
        lock (_lock) { h = _host; _host = null; }
        if (h is null) return;
        h.Stop();
        h.Dispose();
        _note = null; _noteIsProblem = false;
        _log("Play together: sharing stopped");
        Refresh();
    }

    public void SetGame(string? name, string? art)
    {
        _game = name; _art = art;
        _host?.SetGame(name, art);
    }

    /// <summary>From the console: forwarded to every guest. Decode thread; copies and returns at once.</summary>
    public void OnVideo(AccessUnit au) => _host?.SendVideo(au.Data, au.IsKeyframe, au.RtpTimestamp);

    public void OnAudio(byte[] opus, int seq) => _host?.SendAudio(opus, (ushort)seq);

    /// <summary>Rumble from the console (values 0..1). True when it's for a guest's slot (sent to them, not played here).</summary>
    public bool OnRumble(int pad, float low, float high, float lt, float rt, int durationMs) =>
        _host?.SendRumble(pad, new RumbleMsg(low, high, lt, rt, durationMs)) ?? false;

    /// <summary>A guest has (or just had) this controller slot: the stream's own "controller connected" toasts skip it.</summary>
    public bool OwnsSlot(int slot) =>
        slot is >= 0 and < 4 && ((_host?.IsGuestSlot(slot) ?? false) || _pump()?.IsRemote(slot) == true || Now - _releasedAt[slot] < 3000);

    private void OnJoined(TogetherGuestInfo g)
    {
        // A neutral pad first: the console is told a controller connected (as if it were plugged in) on the next poll.
        _pump()?.SetRemotePad(g.Slot, new GamepadState((byte)g.Slot, GamepadButtons.None, 0, 0, 0, 0, 0, 0));
        _log($"Play together: {g.Name}'s controller goes to the console as pad index {g.Slot} (Player {g.Slot + 1}). " +
             "If the console doesn't show a new controller, it didn't take the extra pad index.");
        _overlay?.ShowToast($"{g.Name} joined as {g.PlayerName}", 4000, ToastKind.Good);
        Refresh();
    }

    private void OnLeft(TogetherGuestInfo g, string why)
    {
        _releasedAt[g.Slot] = Now;
        _pump()?.SetRemotePad(g.Slot, null); // neutral, then the console is told the controller went
        if (why.StartsWith("timed out")) _overlay?.ShowToast($"Lost {g.Name}'s connection. {g.PlayerName} is free", 5000, ToastKind.Bad);
        else if (why.StartsWith("removed")) _overlay?.ShowToast($"Removed {g.Name}", 3000, ToastKind.Warn);
        else if (!why.Contains("stopped sharing")) _overlay?.ShowToast($"{g.Name} left", 3000, ToastKind.Warn);
        Refresh();
    }

    private void CopyInvite(bool quiet)
    {
        var code = _host?.Invite;
        if (code is null) return;
        if (quiet && _copiedThisInvite) return;
        bool ok = ClipboardSet(code);
        _copiedThisInvite = ok;
        if (ok) _overlay?.ShowToast("Invite copied: send it to your friend", 3500, ToastKind.Good);
        else _overlay?.ShowToast("Couldn't copy the invite. Write it down from the panel", 4000, ToastKind.Warn);
    }

    private void PasteReply()
    {
        if (_host is null) return;
        string text = ClipboardGet();
        if (text.Trim().Length == 0)
        {
            _overlay?.ShowToast("The clipboard is empty. Copy your friend's reply code first", 4000, ToastKind.Warn);
            return;
        }
        AcceptReply(text);
    }

    /// <summary>A guest's reply code, from the panel's paste or the launcher. The outcome goes in the note (both sides see it).</summary>
    private void AcceptReply(string text)
    {
        var h = _host;
        if (h is null) return;
        if (h.AcceptReply(text, out var problem))
        {
            _note = "Reply code accepted: connecting to your friend…";
            _noteIsProblem = false;
            _overlay?.ShowToast("Reply code accepted: connecting to your friend", 3500, ToastKind.Good);
        }
        else
        {
            _note = problem;
            _noteIsProblem = true;
            _overlay?.ShowToast(problem, 5000, ToastKind.Warn);
        }
        Refresh();
    }

    /// <summary>
    /// A command from the launcher's Play together page. Any thread: it runs on the window's thread, where the panel's
    /// own buttons act, so the two never act on the share at the same moment. Each one ends in a status event.
    /// </summary>
    public void Run(TogetherCommand c) => _main.Enqueue(() =>
    {
        switch (c.Kind)
        {
            case TogetherCommandKind.Share when _host is null: Start(); break;
            case TogetherCommandKind.Share: Refresh(); break; // already sharing: just say so
            case TogetherCommandKind.Stop when _host is not null:
                Stop();
                _overlay?.ShowToast("Stopped sharing. Your friends have left", 3000, ToastKind.Warn);
                break;
            case TogetherCommandKind.Remove: _host?.Kick(c.Slot); Refresh(); break;
            case TogetherCommandKind.Reply: AcceptReply(c.Code); break;
            default: Refresh(); break;
        }
    });

    /// <summary>Send the current status (so the launcher knows this stream can be shared before anyone presses anything).</summary>
    public void Announce() => Refresh();

    /// <summary>Update the panel and tell the launcher.</summary>
    private void Refresh()
    {
        var h = _host;
        var guests = h?.Guests ?? [];
        // Reply notes (accepted, or why not) are done with once someone new is in.
        if (guests.Count > _lastGuestCount && _note is not null) { _note = null; _noteIsProblem = false; }
        _lastGuestCount = guests.Count;
        _overlay?.SetTogether(new TogetherPanel
        {
            Sharing = h is not null, Invite = h?.Invite, Guests = guests, CapMbps = h?.CapKbps / 1000, MaxMbps = _settings.TogetherMaxMbps, Note = _note,
        });
        Changed?.Invoke(new TogetherStatus
        {
            Role = "host", Players = h is null ? 0 : guests.Count + 1, Guests = guests.Select(g => g.Name).ToArray(), Invite = h?.Invite,
            People = guests.Select(g => new TogetherPlayer { Slot = g.Slot, Name = g.Name, PingMs = g.PingMs, LossPct = g.LossPct }).ToArray(),
            CapMbps = h?.CapKbps / 1000, Note = h is null ? null : _note, NoteIsProblem = h is not null && _noteIsProblem,
        });
    }

    /// <summary>
    /// XSTREAM_TOGETHER_TEST_PAD=1 (testing on a real stream, with nobody else): put a pretend guest controller in
    /// slot 1, press A once after 3 s, and let it go after 60 s. Shows whether the console takes a second pad index.
    /// </summary>
    public void RunTestPad()
    {
        _ = Task.Run(async () =>
        {
            var pump = _pump();
            if (pump is null) { _log("Test pad: no controller loop"); return; }
            int slot = Enumerable.Range(1, 3).FirstOrDefault(i => !pump.IsLocalPad(i), 1);
            _log($"Test pad: connecting a pretend controller as pad index {slot} (Player {slot + 1})");
            pump.SetRemotePad(slot, new GamepadState((byte)slot, GamepadButtons.None, 0, 0, 0, 0, 0, 0));
            await Task.Delay(3000);
            pump.SetRemotePad(slot, new GamepadState((byte)slot, GamepadButtons.A, 0, 0, 0, 0, 0, 0));
            _log($"Test pad: pressing A on pad index {slot}");
            await Task.Delay(150);
            pump.SetRemotePad(slot, new GamepadState((byte)slot, GamepadButtons.None, 0, 0, 0, 0, 0, 0));
            await Task.Delay(60_000);
            pump.SetRemotePad(slot, null);
            _log($"Test pad: pad index {slot} disconnected");
        });
    }

    public void Dispose() => Stop();
}
