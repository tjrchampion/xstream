using SkiaSharp;
using XStream.Core.Together;

namespace XStream.Player;

/// <summary>Which stream menu: the normal one, the host's (with Play together), or a guest's (watching a friend's stream).</summary>
public enum TogetherMode { None, Host, Guest }

/// <summary>What the host's Play together panel shows.</summary>
public sealed record TogetherPanel
{
    /// <summary>Sharing is on (the invite works). Off: the panel offers to start again.</summary>
    public bool Sharing { get; init; }
    /// <summary>The invite, or null while the addresses are still being gathered.</summary>
    public string? Invite { get; init; }
    public IReadOnlyList<TogetherGuestInfo> Guests { get; init; } = [];
    /// <summary>The console's bitrate cap while together (null: nobody's in).</summary>
    public double? CapMbps { get; init; }
    public double MaxMbps { get; init; } = 15;
    /// <summary>A line about the last action ("Reply code accepted: connecting to your friend…").</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Play together in the stream menu. Host: a "Play together…" item opens a panel with the invite, who's in (with
/// Remove), Paste reply code and Stop sharing. Guest: a short menu (Resume, Full screen, Stats, Volume, and Leave), and a
/// status card while joining, showing the reply code when the host's router needs one.
/// </summary>
public sealed partial class StreamOverlay
{
    private readonly TogetherMode _together;

    // The guest's own short menu; Leave sits in the bottom row.
    private static readonly (Tab, Item[])[] GuestTabs =
    [
        (Tab.Game, [Item.Resume]),
        (Tab.Picture, [Item.Fullscreen, Item.Stats]),
        (Tab.Sound, [Item.Volume]),
    ];

    private bool _togetherOpen;
    private int _tSel;
    private TogetherPanel? _tPanel;
    private readonly List<(SKRect rect, int action)> _tHits = [];
    private SKRect _tPanelRect;
    private string _guestMeta = "Player 2";
    private (string title, string detail, string? code)? _guestStatus;

    /// <summary>"Play together…" chosen: start sharing (if it isn't on) and show the panel.</summary>
    public event Action? TogetherOpened;
    public event Action? TogetherStopRequested;
    public event Action<int>? TogetherRemoveRequested;
    /// <summary>"Paste reply code": the session reads the clipboard.</summary>
    public event Action? TogetherPasteReplyRequested;
    public event Action? TogetherCopyInviteRequested;

    /// <summary>The panel's contents (any thread).</summary>
    public void SetTogether(TogetherPanel? panel) { lock (_gate) { _tPanel = panel; _dirty = true; } }

    /// <summary>Guest: the host's name, once known (it heads the menu and the reconnecting screen).</summary>
    public void SetConsoleName(string name) { lock (_gate) { _console = name; _dirty = true; } }

    /// <summary>Guest: the line under the host's name in the menu ("Player 2 · Forza Horizon 5").</summary>
    public void SetGuestMeta(string meta) { lock (_gate) { _guestMeta = meta; _dirty = true; } }
    private string GuestMeta { get { lock (_gate) return _guestMeta; } }

    /// <summary>
    /// Guest: a card over the picture while joining (title, what's happening, and the reply code if one is needed);
    /// null hides it. While it's up, B or Leave ends the session.
    /// </summary>
    public void SetGuestStatus(string? title, string? detail = null, string? code = null)
    {
        lock (_gate) _guestStatus = title is null ? null : (title, detail ?? "", code);
        SetReconnecting(title is null ? null : detail ?? "");
    }
    private bool GuestStatusShown { get { lock (_gate) return _guestStatus is not null; } }

    private bool TogetherOpen { get { lock (_gate) return _togetherOpen; } }

    private string? TogetherValue()
    {
        TogetherPanel? p;
        lock (_gate) p = _tPanel;
        if (p is not { Sharing: true }) return "Off";
        return p.Guests.Count == 0 ? "Waiting for a friend" : $"{p.Guests.Count + 1} players";
    }

    /// <summary>Open the menu straight on the Play together panel (testing and screenshots).</summary>
    public void OpenTogetherPanel()
    {
        lock (_gate) { _menu = true; _barUntilMs = 0; ResetMenu(); }
        OpenTogether();
    }

    private void OpenTogether()
    {
        lock (_gate) { _togetherOpen = true; _tSel = 0; _dirty = true; }
        TogetherOpened?.Invoke();
    }

    /// <summary>B in the panel: back to the menu. False when the panel isn't open.</summary>
    private bool TogetherBack()
    {
        lock (_gate)
        {
            if (!_togetherOpen) return false;
            _togetherOpen = false; _dirty = true;
            return true;
        }
    }

    // Panel actions: Remove for each guest, then the buttons along the bottom.
    private const int ActPaste = 100, ActCopy = 101, ActStop = 102, ActBack = 103, ActStart = 104;

    private List<int> Actions()
    {
        var p = _tPanel;
        var a = new List<int>();
        if (p is { Sharing: true })
        {
            foreach (var g in p.Guests) a.Add(g.Slot);
            a.AddRange([ActPaste, ActCopy, ActStop]);
        }
        else a.Add(ActStart);
        a.Add(ActBack);
        return a;
    }

    private bool TogetherMove(int d)
    {
        lock (_gate)
        {
            if (!_togetherOpen) return false;
            int n = Actions().Count;
            _tSel = ((_tSel + d) % n + n) % n;
            _dirty = true;
            return true;
        }
    }

    private bool TogetherChange(int dir, bool activate)
    {
        int act;
        lock (_gate)
        {
            if (!_togetherOpen) return false;
            var acts = Actions();
            if (!activate)
            {
                // Left / right move along the buttons too.
                _tSel = ((_tSel + dir) % acts.Count + acts.Count) % acts.Count;
                _dirty = true;
                return true;
            }
            act = acts[Math.Clamp(_tSel, 0, acts.Count - 1)];
        }
        Run(act);
        return true;
    }

    private void Run(int act)
    {
        switch (act)
        {
            case ActBack: TogetherBack(); break;
            case ActPaste: TogetherPasteReplyRequested?.Invoke(); break;
            case ActCopy: TogetherCopyInviteRequested?.Invoke(); break;
            case ActStop: lock (_gate) _tSel = 0; TogetherStopRequested?.Invoke(); break;
            case ActStart: lock (_gate) _tSel = 0; TogetherOpened?.Invoke(); break;
            default: lock (_gate) _tSel = 0; TogetherRemoveRequested?.Invoke(act); break;
        }
    }

    /// <summary>Taps while the panel is open. True when handled here.</summary>
    private bool TogetherClick(int x, int y)
    {
        int act = -1;
        lock (_gate)
        {
            if (!_menu || !_togetherOpen) return false;
            var acts = Actions();
            foreach (var (r, a) in _tHits)
                if (r.Contains(x, y)) { act = a; _tSel = Math.Max(0, acts.IndexOf(a)); _dirty = true; break; }
            if (act < 0)
            {
                if (!_tPanelRect.Contains(x, y)) { _menu = false; _dirty = true; } // tap outside closes
                return true;
            }
        }
        Run(act);
        return true;
    }

    // ---- Drawing ---------------------------------------------------------------------------------------

    private static readonly SKTypeface Mono = SKTypeface.FromFamilyName(OperatingSystem.IsWindows() ? "Consolas" : "Menlo", SKFontStyle.Bold) ?? Bold;

    /// <summary>Breaks a code into lines that fit (codes have no spaces to wrap at).</summary>
    private static List<string> CodeLines(string code, SKPaint p, float max)
    {
        var lines = new List<string>();
        string line = "";
        foreach (char ch in code)
        {
            if (p.MeasureText(line + ch) > max && line.Length > 0) { lines.Add(line); line = ""; }
            line += ch;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private void DrawTogether(SKCanvas c)
    {
        TogetherPanel? p;
        int sel;
        List<int> acts;
        lock (_gate) { p = _tPanel; sel = _tSel; acts = Actions(); }
        p ??= new TogetherPanel { Sharing = true };
        var hits = new List<(SKRect, int)>();

        using (var dim = Fill(new SKColor(0, 0, 0, 0x8C))) c.DrawRect(0, 0, CanvasWidth, CanvasHeight, dim);
        float pw = 1560, ph = 960, px = (CanvasWidth - pw) / 2, py = (CanvasHeight - ph) / 2;
        var panel = new SKRect(px, py, px + pw, py + ph);
        using (var bg = Fill(Panel)) c.DrawRoundRect(panel, 32, 32, bg);
        using (var border = new SKPaint { Color = new SKColor(0x3D, 0xDC, 0x97, 0x50), IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            c.DrawRoundRect(panel, 32, 32, border);

        float lx = px + 64, w = pw - 128, y = py + 76;
        using (var label = Font(Bold, 20, Accent)) c.DrawText("PLAY TOGETHER", lx, y, label);
        using (var title = Font(Bold, 46, Text)) c.DrawText(p.Sharing ? "Invite a friend" : "Sharing is off", lx, y + 58, title);
        using var body = Font(Regular, 24, Muted);
        y = DrawWrapped(c, p.Sharing
            ? "Your friend needs Vexel on their computer, not Game Pass or an Xbox. In Vexel they choose Play together, paste this invite and press Join. Their controller becomes the next player on your console."
            : "Nobody can join. Start sharing to make a new invite (the old one stops working).", lx, y + 104, w, body, 2);

        // The invite.
        y += 26;
        var box = new SKRect(lx, y, lx + w, y + 142);
        using (var tile = Fill(Tile)) c.DrawRoundRect(box, 18, 18, tile);
        if (p.Sharing)
        {
            if (p.Invite is { } code)
            {
                using var mono = new SKPaint { Typeface = Mono, TextSize = 31, Color = Text, IsAntialias = true, SubpixelText = true };
                var lines = CodeLines(code, mono, w - 56);
                float ly = box.Top + (box.Height - lines.Count * 40) / 2 + 30;
                foreach (var line in lines.Take(3)) { c.DrawText(line, box.Left + 28, ly, mono); ly += 40; }
            }
            else
                using (var wait = Font(Bold, 28, Muted)) c.DrawText("Preparing your invite…", box.Left + 28, box.MidY + 10, wait);
        }
        else using (var off = Font(Bold, 28, Faint)) c.DrawText("No invite while sharing is off", box.Left + 28, box.MidY + 10, off);
        y = box.Bottom + 36;
        using (var small = Font(Regular, 21, p.Note is null ? Muted : Accent))
            c.DrawText(p.Note ?? (p.Invite is null || !p.Sharing ? "" : "Copied to the clipboard: send it to your friend in a message."), lx, y, small);

        // Who's in.
        y += 52;
        using (var sec = Font(Bold, 17, Faint)) c.DrawText("PLAYERS", lx, y, sec);
        y += 16;
        using var name = Font(Bold, 26, Text);
        using var meta = Font(Regular, 22, Muted);
        for (int slot = 0; slot < 4; slot++)
        {
            var g = p.Guests.FirstOrDefault(x => x.Slot == slot);
            var row = new SKRect(lx, y, lx + w, y + 58);
            bool free = slot > 0 && g is null;
            using (var dot = Fill(slot == 0 || g is not null ? Accent : Faint)) c.DrawCircle(row.Left + 14, row.MidY, 7, dot);
            name.Color = free ? Faint : Text;
            c.DrawText($"Player {slot + 1}", row.Left + 38, row.MidY + 9, name);
            string who = slot == 0 ? "You" : g?.Name ?? "Free";
            c.DrawText(Trim(who, name, 420), row.Left + 210, row.MidY + 9, name);
            if (g is not null)
            {
                string stats = (double.IsNaN(g.PingMs) ? "measuring" : $"{g.PingMs:F0} ms ping") + $"  ·  {g.LossPct:F1}% loss";
                meta.Color = g.LossPct > 2 || g.PingMs > 80 ? Warn : Muted;
                c.DrawText(stats, row.Left + 660, row.MidY + 8, meta);
                int idx = acts.IndexOf(g.Slot);
                var btn = new SKRect(row.Right - 190, row.Top + 4, row.Right, row.Bottom - 4);
                DrawButton(c, btn, "Remove", idx == sel, danger: true);
                hits.Add((btn, g.Slot));
            }
            y += 62;
        }

        // The bitrate note.
        y += 16;
        using (var note = Font(Regular, 21, Muted))
        {
            string cap = p.CapMbps is { } m && m < p.MaxMbps - 0.1
                ? $"The picture is capped at {m:F0} Mbps for now: a friend's connection is losing packets."
                : $"While you play together the picture is capped at {p.MaxMbps:F0} Mbps: your upload carries your friend's picture.";
            c.DrawText(cap, lx, y, note);
        }

        // Buttons.
        float by = py + ph - 170, bx = lx, bh = 64;
        foreach (int act in acts.Where(a => a >= 100))
        {
            string label = act switch { ActPaste => "Paste reply code", ActCopy => "Copy invite", ActStop => "Stop sharing", ActStart => "Start sharing", _ => "Back" };
            using var bf = Font(Bold, 26, Text);
            float bw = bf.MeasureText(label) + 64;
            var r = new SKRect(bx, by, bx + bw, by + bh);
            DrawButton(c, r, label, acts.IndexOf(act) == sel, danger: act == ActStop);
            hits.Add((r, act));
            bx += bw + 18;
        }
        using (var hint = Font(Regular, 20, Faint))
        {
            if (p.Sharing)
                c.DrawText("Friend can't get in? After a few seconds their Vexel shows a reply code. Copy it, then choose Paste reply code.", lx, by + bh + 46, hint);
            c.DrawText("D-pad or arrows  move   ·   A or Enter  choose   ·   B  back to the menu", lx, by + bh + 78, hint);
        }
        lock (_gate) { _tHits.Clear(); _tHits.AddRange(hits); _tPanelRect = panel; }
    }

    private static void DrawButton(SKCanvas c, SKRect r, string label, bool selected, bool danger)
    {
        var tone = danger ? Bad : Accent;
        using (var bg = Fill(selected ? tone.WithAlpha(0x40) : new SKColor(0x26, 0x2B, 0x33))) c.DrawRoundRect(r, r.Height / 2, r.Height / 2, bg);
        if (selected)
            using (var ring = new SKPaint { Color = tone, IsStroke = true, StrokeWidth = 2.5f, IsAntialias = true }) c.DrawRoundRect(r, r.Height / 2, r.Height / 2, ring);
        using var f = Font(Bold, 25, danger ? Bad : selected ? Accent : Text);
        c.DrawText(label, r.MidX - f.MeasureText(label) / 2, r.MidY + 9, f);
    }

    /// <summary>Guest: joining (or can't reach the host yet: the reply code), over the dimmed window.</summary>
    private void DrawGuestStatus(SKCanvas c)
    {
        (string title, string detail, string? code) st;
        lock (_gate) { if (_guestStatus is not { } g) return; st = g; }
        using (var dim = Fill(new SKColor(0x0C, 0x0E, 0x12, 0xF0))) c.DrawRect(0, 0, CanvasWidth, CanvasHeight, dim);
        float w = 1240, h = st.code is null ? 360 : 600, x = (CanvasWidth - w) / 2, y = (CanvasHeight - h) / 2;
        using (var bg = Fill(Panel)) c.DrawRoundRect(x, y, w, h, 28, 28, bg);
        using (var edge = new SKPaint { Color = (st.code is null ? Accent : Warn).WithAlpha(0x60), IsStroke = true, StrokeWidth = 2, IsAntialias = true })
            c.DrawRoundRect(x, y, w, h, 28, 28, edge);
        float cy = y + 64;
        if (st.code is null)
        {
            int phase = (int)(Now / 250) % 3;
            for (int i = 0; i < 3; i++)
                using (var dot = Fill(i == phase ? Accent : Accent.WithAlpha(0x50))) c.DrawCircle(CanvasWidth / 2f - 24 + i * 24, cy - 8, 8, dot);
            cy += 50;
            _dirty = true; // keep the dots moving
        }
        else using (var label = Font(Bold, 20, Warn)) { Center(c, "ONE MORE STEP", cy, label); cy += 50; }
        using (var t = Font(Bold, 38, Text)) Center(c, Trim(st.title, t, w - 80), cy + 8, t);
        cy += 58;
        using (var sub = Font(Regular, 24, Muted))
        {
            // Centred wrapping: measure the lines first.
            var words = st.detail.Split(' ');
            var lines = new List<string>();
            string line = "";
            foreach (var wd in words)
            {
                string trial = line.Length == 0 ? wd : line + " " + wd;
                if (sub.MeasureText(trial) > w - 120 && line.Length > 0) { lines.Add(line); line = wd; } else line = trial;
            }
            if (line.Length > 0) lines.Add(line);
            foreach (var l in lines.Take(3)) { Center(c, l, cy, sub); cy += 34; }
        }
        if (st.code is { } code)
        {
            cy += 16;
            var box = new SKRect(x + 60, cy, x + w - 60, cy + 150);
            using (var tile = Fill(Tile)) c.DrawRoundRect(box, 18, 18, tile);
            using var mono = new SKPaint { Typeface = Mono, TextSize = 30, Color = Text, IsAntialias = true, SubpixelText = true };
            var lines = CodeLines(code, mono, box.Width - 56);
            float ly = box.Top + (box.Height - lines.Count * 40) / 2 + 30;
            foreach (var l in lines.Take(3)) { c.DrawText(l, box.MidX - mono.MeasureText(l) / 2, ly, mono); ly += 40; }
            cy = box.Bottom + 40;
            using (var ok = Font(Regular, 22, Accent)) Center(c, "Copied to the clipboard. Vexel keeps trying while you wait.", cy, ok);
        }
        using var bf = Font(Bold, 26, Bad);
        const string leave = "Leave  (B)";
        float bw = bf.MeasureText(leave) + 72, bh = 60, bx = (CanvasWidth - bw) / 2, by = y + h - bh - 30;
        lock (_gate) _reconnectDisconnectRect = new SKRect(bx, by, bx + bw, by + bh);
        using (var bbg = Fill(new SKColor(0xFF, 0x6B, 0x6B, 0x26))) c.DrawRoundRect(new SKRect(bx, by, bx + bw, by + bh), bh / 2, bh / 2, bbg);
        Center(c, leave, by + bh / 2 + 9, bf);
    }
}
