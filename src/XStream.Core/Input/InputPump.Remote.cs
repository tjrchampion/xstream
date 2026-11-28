using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>
/// Play together: controllers that aren't plugged into this device (a friend's, arriving over the network) put into
/// a slot of their own. They count as connected pads, so they go out in the same packets as the local ones, on the
/// next 1 ms poll, and the console sees one more controller (connect and disconnect are reported as for a real pad).
///
/// A remote pad is sent exactly as it arrives: no deadzones, remapping, gyro or keyboard mixing (the friend's own
/// Vexel did its own mapping). If a local pad appears in a slot a remote one holds, the remote one wins until it's
/// released.
/// </summary>
public sealed partial class InputPump
{
    private readonly object _remoteLock = new();
    private readonly GamepadState[] _remoteState = new GamepadState[4];
    private int _remoteMask;   // slots with a remote pad (bit per slot)
    private int _localMask;    // slots with a local pad in the last poll

    /// <summary>
    /// Put a remote controller in a slot (its state now), or take it out (null: the console gets a neutral pad, then a
    /// disconnect). Thread-safe; takes effect on the next poll.
    /// </summary>
    public void SetRemotePad(int index, GamepadState? state)
    {
        if (index is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(index));
        lock (_remoteLock)
        {
            if (state is { } s)
            {
                _remoteState[index] = s with { Index = (byte)index };
                _remoteMask |= 1 << index;
            }
            else
            {
                _remoteState[index] = default;
                _remoteMask &= ~(1 << index);
            }
        }
    }

    /// <summary>Whether a slot holds a remote pad (any thread).</summary>
    public bool IsRemote(int index) => index is >= 0 and < 4 && (Volatile.Read(ref _remoteMask) & (1 << index)) != 0;

    /// <summary>Any remote pads at all (fast check for the send path).</summary>
    public bool HasRemote => Volatile.Read(ref _remoteMask) != 0;

    /// <summary>Whether one of this device's own controllers (or keyboard and mouse) is in a slot, as of the last poll.</summary>
    public bool IsLocalPad(int index) => index is >= 0 and < 4 && (Volatile.Read(ref _localMask) & (1 << index)) != 0;

    /// <summary>Take every remote pad out (on stop, and when sharing ends).</summary>
    public void ClearRemotePads()
    {
        lock (_remoteLock) { Array.Clear(_remoteState); _remoteMask = 0; }
    }

    /// <summary>Mix the remote pads into this poll's states; returns the slots that are remote (skip local processing for those).</summary>
    private int MixRemote()
    {
        int local = 0;
        for (int i = 0; i < 4; i++) if (_conn[i]) local |= 1 << i;
        Volatile.Write(ref _localMask, local & ~Volatile.Read(ref _remoteMask));
        if (Volatile.Read(ref _remoteMask) == 0) return 0;
        int mask;
        lock (_remoteLock)
        {
            mask = _remoteMask;
            for (int i = 0; i < 4; i++)
                if ((mask & (1 << i)) != 0) { _conn[i] = true; _raw[i] = _remoteState[i]; }
        }
        // The local pad that was in a remote slot is not this slot's owner any more.
        Volatile.Write(ref _localMask, local & ~mask);
        return mask;
    }
}
