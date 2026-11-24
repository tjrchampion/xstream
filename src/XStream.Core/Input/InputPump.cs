using System.Diagnostics;
using System.Runtime.InteropServices;
using XStream.Core.Xbox;

namespace XStream.Core.Input;

public sealed record InputOptions
{
    /// <summary>How often the controller is read. 1000 Hz = at most 1 ms between a change and seeing it.</summary>
    public int PollHz { get; init; } = 1000;
    /// <summary>
    /// Resend the current state at least this often even if nothing changed. Kept short on purpose: the
    /// input channel is ordered and reliable, so if a packet is lost the console only notices (and asks
    /// for it again) when the next one arrives. A steady 8 ms stream means a loss is repaired within
    /// about one round trip instead of stalling input.
    /// </summary>
    public double HeartbeatMs { get; init; } = 8;
    /// <summary>Radial stick deadzone, 0..1. Small by default: games apply their own.</summary>
    public float StickDeadzone { get; init; } = 0.04f;
    public float TriggerDeadzone { get; init; } = 0.0f;
    /// <summary>Smallest axis movement that counts as a change (filters sensor noise, not intent).</summary>
    public float ChangeThreshold { get; init; } = 1f / 512;
    /// <summary>Extra copies of each change, a few ms apart (only useful on an unreliable channel; off by default).</summary>
    public int Redundancy { get; init; } = 0;
    public double RedundancyGapMs { get; init; } = 2;
    /// <summary>Hold View + Menu this long to press the Xbox button (for pads/handhelds where Guide isn't readable).</summary>
    public int NexusComboMs { get; init; } = 500;
    /// <summary>Spin for the last fraction of each millisecond for precise timing (costs a little CPU/battery).</summary>
    public bool PreciseTiming { get; init; } = true;
}

/// <summary>Live numbers for the overlay and session log.</summary>
public readonly record struct InputStats(
    long Sends, long Changes, double DetectToSendP50Us, double DetectToSendP99Us,
    double PollHzActual, double WorstPollGapMs, int PadsConnected);

/// <summary>
/// The controller loop. Runs on its own highest-priority thread, reads the controller up to 1000 times
/// a second, and sends a packet the moment anything changes: no fixed send tick, no batching, no
/// waiting for a video frame. Typical cost from "change seen" to "packet handed to the network" is
/// tens of microseconds.
/// </summary>
public sealed class InputPump : IDisposable
{
    private readonly IGamepadBackend _backend;
    private readonly InputOptions _o;
    private readonly Action<GamepadState[]> _send;
    private readonly Action<int, bool>? _padChanged;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private readonly GamepadState[] _raw = new GamepadState[4];
    private readonly bool[] _conn = new bool[4];
    private readonly bool[] _prevConn = new bool[4];
    private readonly GamepadState[] _sent = new GamepadState[4];
    private readonly double[] _comboSince = { double.NaN, double.NaN, double.NaN, double.NaN };

    private double _lastSendMs = double.NegativeInfinity;
    private int _repeatsLeft;
    private double _nextRepeatMs;
    private long _sends, _changes;

    private readonly double[] _lat = new double[2048];
    private int _latN;
    private double _lastStepMs = double.NaN, _worstGapMs;
    private long _steps;
    private double _statsSinceMs;
    private readonly object _statLock = new();

    private Thread? _thread;
    private volatile bool _running;

    public InputPump(IGamepadBackend backend, InputOptions options, Action<GamepadState[]> send, Action<int, bool>? padChanged = null)
    {
        _backend = backend;
        _o = options;
        _send = send;
        _padChanged = padChanged;
    }

    public double NowMs => _sw.Elapsed.TotalMilliseconds;
    public IGamepadBackend Backend => _backend;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "xstream-input", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(500);
        // Never leave a button or stick held on the console.
        var neutral = Enumerable.Range(0, 4).Where(i => _prevConn[i]).Select(i => new GamepadState((byte)i, 0, 0, 0, 0, 0, 0, 0)).ToArray();
        if (neutral.Length > 0) _send(neutral);
    }

    private void Loop()
    {
        bool win = OperatingSystem.IsWindows();
        if (win) TimeBeginPeriod(1);
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread(); // MMCSS on Windows, P-cores on macOS
        try
        {
            double period = 1000.0 / Math.Clamp(_o.PollHz, 60, 8000);
            double next = NowMs;
            while (_running)
            {
                Step(NowMs);
                next += period;
                double now = NowMs;
                if (now - next > period * 5) next = now; // fell behind (sleep/suspend): don't burst
                while (true)
                {
                    double remaining = next - NowMs;
                    if (remaining <= 0) break;
                    if (remaining > 1.5 || !_o.PreciseTiming) Thread.Sleep(1);
                    else Thread.SpinWait(40);
                }
            }
        }
        finally { if (win) TimeEndPeriod(1); }
    }

    /// <summary>One poll-and-maybe-send iteration. Public so tests can drive time.</summary>
    public void Step(double nowMs)
    {
        if (!double.IsNaN(_lastStepMs)) _worstGapMs = Math.Max(_worstGapMs, nowMs - _lastStepMs);
        _lastStepMs = nowMs;
        _steps++;

        _backend.Poll(_raw, _conn);
        long t0 = Stopwatch.GetTimestamp();

        bool changed = false;
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            if (_conn[i] != _prevConn[i])
            {
                changed = true;
                if (!_conn[i]) _send(new[] { new GamepadState((byte)i, 0, 0, 0, 0, 0, 0, 0) }); // release everything first
                _prevConn[i] = _conn[i];
                _padChanged?.Invoke(i, _conn[i]);
                if (!_conn[i]) { _sent[i] = default; continue; }
            }
            if (!_conn[i]) continue;
            count++;
            var s = Process(_raw[i], (byte)i, nowMs);
            if (Differs(s, _sent[i])) { changed = true; _sent[i] = s; }
        }

        bool send = false;
        if (changed && count > 0)
        {
            send = true;
            _changes++;
            _repeatsLeft = _o.Redundancy;
            _nextRepeatMs = nowMs + _o.RedundancyGapMs;
        }
        else if (_repeatsLeft > 0 && nowMs >= _nextRepeatMs)
        {
            send = true;
            _repeatsLeft--;
            _nextRepeatMs = nowMs + _o.RedundancyGapMs;
        }
        else if (count > 0 && nowMs - _lastSendMs >= _o.HeartbeatMs) send = true;

        // No controller connected: the console ends remote play after about a minute without input (measured: 58 s).
        // Keep the session alive with an idle controller (nothing pressed) a few times a second, so it survives
        // starting a stream before the controller is on, or just watching.
        if (!send && count == 0 && nowMs - _lastSendMs >= IdleKeepaliveMs)
        {
            _send(IdlePad);
            _lastSendMs = nowMs;
            return;
        }

        if (!send) return;
        var frame = new GamepadState[count];
        for (int i = 0, k = 0; i < 4; i++) if (_conn[i]) frame[k++] = _sent[i];
        _send(frame);
        _sends++;
        _lastSendMs = nowMs;
        if (changed)
        {
            double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
            lock (_statLock) { _lat[_latN++ % _lat.Length] = us; }
        }
    }

    /// <summary>How often an idle controller is sent while none is connected (keeps the console's session alive).</summary>
    public const double IdleKeepaliveMs = 250;
    private static readonly GamepadState[] IdlePad = [new GamepadState(0, GamepadButtons.None, 0, 0, 0, 0, 0, 0)];

    private GamepadState Process(in GamepadState r, byte index, double nowMs)
    {
        var (lx, ly) = Radial(r.LeftX, r.LeftY, _o.StickDeadzone);
        var (rx, ry) = Radial(r.RightX, r.RightY, _o.StickDeadzone);
        float lt = Linear(r.LeftTrigger, _o.TriggerDeadzone), rt = Linear(r.RightTrigger, _o.TriggerDeadzone);
        var b = r.Buttons;

        const GamepadButtons combo = GamepadButtons.View | GamepadButtons.Menu;
        if ((b & combo) == combo && _o.NexusComboMs > 0)
        {
            if (double.IsNaN(_comboSince[index])) _comboSince[index] = nowMs;
            if (nowMs - _comboSince[index] >= _o.NexusComboMs) b = (b & ~combo) | GamepadButtons.Nexus;
        }
        else _comboSince[index] = double.NaN;

        return new GamepadState(index, b, lx, ly, rx, ry, lt, rt);
    }

    private bool Differs(in GamepadState a, in GamepadState b)
    {
        float t = _o.ChangeThreshold;
        return a.Buttons != b.Buttons
            || Math.Abs(a.LeftX - b.LeftX) > t || Math.Abs(a.LeftY - b.LeftY) > t
            || Math.Abs(a.RightX - b.RightX) > t || Math.Abs(a.RightY - b.RightY) > t
            || Math.Abs(a.LeftTrigger - b.LeftTrigger) > t || Math.Abs(a.RightTrigger - b.RightTrigger) > t
            // returning exactly to rest must always be sent, however small the step
            || (a.LeftX == 0 && a.LeftY == 0) != (b.LeftX == 0 && b.LeftY == 0)
            || (a.RightX == 0 && a.RightY == 0) != (b.RightX == 0 && b.RightY == 0)
            || (a.LeftTrigger == 0) != (b.LeftTrigger == 0) || (a.RightTrigger == 0) != (b.RightTrigger == 0);
    }

    /// <summary>Radial deadzone with rescale, so movement starts smoothly from the edge of the deadzone.</summary>
    public static (float x, float y) Radial(float x, float y, float dz)
    {
        float mag = MathF.Sqrt(x * x + y * y);
        if (mag <= dz || mag == 0) return (0, 0);
        float scaled = Math.Min(1, (mag - dz) / (1 - dz));
        return (x / mag * scaled, y / mag * scaled);
    }

    private static float Linear(float v, float dz) => v <= dz ? 0 : Math.Min(1, (v - dz) / (1 - dz));

    public InputStats Stats()
    {
        double[] lat;
        lock (_statLock) lat = _lat.Take(Math.Min(_latN, _lat.Length)).OrderBy(x => x).ToArray();
        double now = NowMs, hz = now > _statsSinceMs ? _steps * 1000 / (now - _statsSinceMs) : 0;
        var s = new InputStats(_sends, _changes,
            lat.Length > 0 ? lat[lat.Length / 2] : 0, lat.Length > 0 ? lat[(int)(lat.Length * 0.99)] : 0,
            hz, _worstGapMs, _prevConn.Count(c => c));
        return s;
    }

    /// <summary>Restart the rate/gap window (call once a second from the overlay).</summary>
    public void ResetWindow()
    {
        _steps = 0;
        _statsSinceMs = NowMs;
        _worstGapMs = 0;
    }

    public void Dispose() { Stop(); _backend.Dispose(); }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint ms);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint ms);
}
