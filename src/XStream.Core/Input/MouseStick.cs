namespace XStream.Core.Input;

/// <summary>
/// Mouse movement to right-stick deflection. Games read the right stick as a turn speed, so mouse speed (not
/// distance) becomes deflection: moving the mouse turns, stopping it stops.
///
/// Per poll: velocity = movement / time, smoothed by two short low-pass stages (the input loop polls at 1 kHz but a
/// mouse reports at 125-1000 Hz, so raw per-poll velocity is a train of spikes; two stages remove that ripple with
/// about <c>smoothingMs</c> of lag). The smoothed velocity times the sensitivity gives the deflection, and an
/// anti-deadzone lifts the smallest movements past the game's own stick deadzone so a slow nudge still aims.
/// When the mouse stops, the filter carries the stick back to centre over a few tens of milliseconds and it
/// snaps to exactly zero once the speed falls below a noise floor. Pure: no clock or threads, so it's testable.
/// </summary>
public sealed class MouseStick
{
    /// <summary>Mouse speed (counts, i.e. pixels in relative mode, per second) that gives full stick at sensitivity 1.</summary>
    public const double FullScaleCountsPerSecond = 2000;
    /// <summary>Below this speed (counts/s) the mouse counts as still.</summary>
    public const double RestCountsPerSecond = 8;

    private readonly double _gain;      // stick per (count/s)
    private readonly float _antiDeadzone;
    private readonly double _tau;       // each stage's time constant, ms
    private double _ax, _ay, _bx, _by;  // stage 1 and stage 2 (counts/s)

    /// <param name="sensitivity">1 = default; 0.1 to 10.</param>
    /// <param name="antiDeadzone">Smallest deflection a movement gives (0..0.5).</param>
    /// <param name="smoothingMs">Total smoothing (two stages of half this each). 0 = none.</param>
    /// <param name="invertY">Mouse forward looks down (flight-style).</param>
    public MouseStick(double sensitivity = 1, float antiDeadzone = 0.15f, double smoothingMs = 16, bool invertY = false)
    {
        Sensitivity = Math.Clamp(sensitivity, 0.1, 10);
        _gain = Sensitivity / FullScaleCountsPerSecond;
        _antiDeadzone = Math.Clamp(antiDeadzone, 0, 0.5f);
        _tau = Math.Max(0, smoothingMs) / 2;
        InvertY = invertY;
    }

    public double Sensitivity { get; }
    public bool InvertY { get; }

    /// <summary>Forget any motion (capture released): the next update starts from rest.</summary>
    public void Reset() => _ax = _ay = _bx = _by = 0;

    /// <summary>
    /// Feed the movement since the last call (<paramref name="dx"/> right, <paramref name="dy"/> down, as the mouse reports
    /// it) and the time it covers; returns the stick (-1..1, Y up).
    /// </summary>
    public (float X, float Y) Update(double dx, double dy, double dtMs)
    {
        if (dtMs <= 0) dtMs = 0.001;
        double vx = dx * 1000 / dtMs, vy = dy * 1000 / dtMs;
        double a = _tau <= 0 ? 1 : 1 - Math.Exp(-dtMs / _tau);
        _ax += (vx - _ax) * a; _ay += (vy - _ay) * a;
        _bx += (_ax - _bx) * a; _by += (_ay - _by) * a;

        double speed = Math.Sqrt(_bx * _bx + _by * _by);
        if (speed < RestCountsPerSecond)
        {
            // Settled: drop the tail completely so the next movement starts clean (and "back at rest" is exact).
            if (Math.Sqrt(_ax * _ax + _ay * _ay) < RestCountsPerSecond) Reset();
            return (0, 0);
        }
        double mag = speed * _gain;
        double lifted = Math.Min(1, _antiDeadzone + (1 - _antiDeadzone) * Math.Min(1, mag));
        double k = lifted / speed;
        // Mouse right = stick right; mouse back (towards you, +dy) = stick down (the wire's Y is up).
        return ((float)(_bx * k), (float)((InvertY ? _by : -_by) * k));
    }
}
