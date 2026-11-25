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

    /// <summary>Back paddles and gyro for a pad, read in the last <see cref="Poll"/> (none where the backend can't see them).</summary>
    PadExtras Extras(int pad) => default;

    /// <summary>Start reading the gyro (it costs a little battery on the controller, so only when gyro aim is on).</summary>
    void EnableGyro(bool on) { }
}

public static class GamepadBackends
{
    /// <summary>XInput on Windows (ROG Ally X built-in controller, Xbox pads); SDL2 elsewhere (macOS).</summary>
    /// <param name="needExtras">Gyro aim or paddle mapping is on: use SDL2, which reads DualSense / Switch Pro gyros and
    /// Elite / Edge paddles (XInput can't), if it's available.</param>
    public static IGamepadBackend CreateDefault(bool needExtras = false)
    {
        if (needExtras)
        {
            try { return new Sdl2Backend(); }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException) { /* XInput below */ }
        }
        if (OperatingSystem.IsWindows())
        {
            try { return new XInputBackend(); }
            catch (DllNotFoundException) { /* fall through to SDL2 */ }
        }
        return new Sdl2Backend();
    }
}
