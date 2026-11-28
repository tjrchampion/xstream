using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// Decides what each controller frame sends to the console, given the stream menu. The menu only ever sees this
/// device's own controllers: a friend playing together can't open (or steer) the host's menu, and while the host has
/// it open the friend keeps playing.
/// </summary>
public sealed class HostPadRouter(Func<int, bool> isRemote)
{
    /// <summary>The stream menu: true when it owns this device's controllers (they then send nothing pressed).</summary>
    public Func<GamepadState[], bool>? Menu { get; set; }

    private GamepadState[] _neutral = [];

    /// <summary>What to send for <paramref name="frame"/> (null: nothing this time).</summary>
    public GamepadState[]? Route(GamepadState[] frame)
    {
        if (Menu is not { } menu) return frame;
        int remote = 0;
        foreach (var f in frame) if (isRemote(f.Index)) remote++;
        if (remote == 0)
        {
            // Only local pads (the usual case): the menu sees them; while it's open the console gets one neutral frame.
            if (menu(frame))
            {
                if (_neutral.Length != frame.Length || _neutral.Where((n, i) => n.Index != frame[i].Index).Any())
                {
                    _neutral = Neutral(frame);
                    return _neutral;
                }
                return null;
            }
            _neutral = [];
            return frame;
        }
        _neutral = [];
        var local = new GamepadState[frame.Length - remote];
        for (int i = 0, k = 0; i < frame.Length; i++) if (!isRemote(frame[i].Index)) local[k++] = frame[i];
        // No local pad in the frame (keyboard only, or watching): there's nothing of ours for the menu to read.
        if (local.Length == 0 || !menu(local)) return frame;
        var routed = new GamepadState[frame.Length];
        for (int i = 0; i < frame.Length; i++)
            routed[i] = isRemote(frame[i].Index) ? frame[i] : new GamepadState(frame[i].Index, GamepadButtons.None, 0, 0, 0, 0, 0, 0);
        return routed;
    }

    /// <summary>This device's pads only (Xbox button from the menu, "release everything" when the menu opens).</summary>
    public GamepadState[] LocalOnly(GamepadState[] frame) => frame.Where(f => !isRemote(f.Index)).ToArray();

    public static GamepadState[] Neutral(GamepadState[] frame) =>
        frame.Select(f => new GamepadState(f.Index, GamepadButtons.None, 0, 0, 0, 0, 0, 0)).ToArray();
}
