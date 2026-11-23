using XStream.Core.Xbox;

namespace XStream.Core.Input;

/// <summary>
/// A source of controller state. Implementations are polled from one dedicated high-priority thread,
/// so Poll must be cheap (microseconds) and must not allocate.
/// </summary>
public interface IGamepadBackend : IDisposable
{
    string Name { get; }
    int MaxPads { get; }

    /// <summary>Read every pad into <paramref name="states"/>; set <paramref name="connected"/>[i].</summary>
    void Poll(Span<GamepadState> states, Span<bool> connected);

    /// <summary>Rumble, all values 0..1. Triggers are ignored where the hardware has no trigger motors.</summary>
    void Rumble(int pad, float low, float high, float leftTrigger, float rightTrigger, int durationMs);
}

public static class GamepadBackends
{
    /// <summary>XInput on Windows (ROG Ally X built-in controller, Xbox pads); SDL2 elsewhere (macOS).</summary>
    public static IGamepadBackend CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new XInputBackend(); }
            catch (DllNotFoundException) { /* fall through to SDL2 */ }
        }
        return new Sdl2Backend();
    }
}
