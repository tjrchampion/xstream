using XStream.Core.Video;
using XStream.Core.Xbox;

namespace XStream.Player;

/// <summary>
/// Automated input-to-picture test (XSTREAM_LATENCY_TEST=presses, or --latency-test=N on the command line).
/// Presses D-pad right and left alternately (on the Xbox dashboard that just moves the highlight) and times how
/// long until a decoded picture changes: input send -> console -> game/UI -> capture -> encode -> network ->
/// decode. That is the whole chain except the controller's own link (we time from the send) and the last step
/// to the display, which the renderer measures separately ("On screen").
///
/// Change detection: a 64x36 grid of luma samples per picture, mean absolute difference from the previous
/// picture. A press counts as seen at the first picture after it whose difference is well above the recent
/// quiet-frame level. Needs a mostly still screen (the dashboard), and CPU-readable pictures (not D3D11).
/// </summary>
internal sealed unsafe class LatencyProbe
{
    private const int GridW = 64, GridH = 36;
    private readonly Func<double> _clock;
    private readonly Action<GamepadState[]> _send;
    private readonly int _presses;
    private readonly byte[] _prev = new byte[GridW * GridH], _cur = new byte[GridW * GridH];
    private bool _havePrev;
    private readonly Queue<double> _quiet = new();
    private double _pressAt = double.NaN, _lastPressAt = double.NegativeInfinity;
    private readonly List<(double arrived, double decoded)> _results = new();
    private int _unreadable;
    private readonly object _gate = new();
    private static readonly bool Debug = Environment.GetEnvironmentVariable("XSTREAM_LATENCY_DEBUG") == "1";

    public LatencyProbe(Func<double> clock, Action<GamepadState[]> send, int presses)
    {
        _clock = clock; _send = send; _presses = presses;
    }

    public void Start(Action<string> log)
    {
        new Thread(() => Run(log)) { IsBackground = true, Name = "latency-probe" }.Start();
    }

    private void Run(Action<string> log)
    {
        Thread.Sleep(6_000); // let the picture settle and the noise level fill in
        log($"Latency test: {_presses} D-pad presses (right/left), 1.5 s apart");
        for (int i = 0; i < _presses; i++)
        {
            var button = i % 2 == 0 ? GamepadButtons.DPadRight : GamepadButtons.DPadLeft;
            lock (_gate) _pressAt = _lastPressAt = _clock();
            _send([new GamepadState(0, button, 0, 0, 0, 0, 0, 0)]);
            Thread.Sleep(80);
            _send([new GamepadState(0, GamepadButtons.None, 0, 0, 0, 0, 0, 0)]);
            Thread.Sleep(1_420);
            lock (_gate) _pressAt = double.NaN; // not seen within the window: missed
        }
        log(Summary);
    }

    /// <summary>Decode thread: one call per picture.</summary>
    public void OnPicture(DecodedPicture p)
    {
        if (p.Layout is not (PlaneLayout.Nv12 or PlaneLayout.Yuv420p)) { _unreadable++; return; }
        byte* y = p.Plane(0); int pitch = p.Pitch(0);
        if (y == null) { _unreadable++; return; }
        for (int gy = 0; gy < GridH; gy++)
        {
            byte* row = y + (long)(gy * p.Height / GridH + p.Height / GridH / 2) * pitch;
            for (int gx = 0; gx < GridW; gx++) _cur[gy * GridW + gx] = row[gx * p.Width / GridW + p.Width / GridW / 2];
        }
        double diff = 0;
        if (_havePrev) for (int i = 0; i < _cur.Length; i++) diff += Math.Abs(_cur[i] - _prev[i]);
        diff /= _cur.Length;
        Buffer.BlockCopy(_cur, 0, _prev, 0, _cur.Length);
        bool had = _havePrev; _havePrev = true;
        if (!had) return;

        lock (_gate)
        {
            if (double.IsNaN(_pressAt) || p.ArrivalMs < _pressAt)
            {
                // The UI keeps animating for a few hundred ms after a press: only truly still frames set the noise level.
                if (p.ArrivalMs - _lastPressAt < 700) return;
                _quiet.Enqueue(diff);
                while (_quiet.Count > 120) _quiet.Dequeue();
                return;
            }
            double noise = _quiet.Count > 0 ? _quiet.OrderBy(x => x).ElementAt((int)(_quiet.Count * 0.95)) : 0;
            if (Debug) Console.WriteLine($"  probe +{p.ArrivalMs - _pressAt:F0} ms diff {diff:F2} noise {noise:F2}");
            if (diff > Math.Max(noise * 3, 0.5))
            {
                _results.Add((p.ArrivalMs - _pressAt, p.DecodedMs - _pressAt));
                _pressAt = double.NaN;
            }
        }
    }

    public string Summary
    {
        get
        {
            lock (_gate)
            {
                if (_results.Count == 0)
                    return _unreadable > 0 ? "Latency test: pictures aren't CPU-readable with this decoder (D3D11); run with --software"
                                           : $"Latency test: no picture changes seen after {_presses} presses (is the dashboard showing?)";
                var a = _results.Select(r => r.arrived).OrderBy(x => x).ToArray();
                var d = _results.Select(r => r.decoded).OrderBy(x => x).ToArray();
                static double P(double[] v, double q) => v[Math.Min(v.Length - 1, (int)(v.Length * q))];
                return $"Latency test: {_results.Count}/{_presses} presses seen. Press -> changed frame fully arrived p50 {P(a, 0.5):F1} ms " +
                       $"(min {a[0]:F1}, p90 {P(a, 0.9):F1}); -> decoded p50 {P(d, 0.5):F1} ms (min {d[0]:F1}, p90 {P(d, 0.9):F1}). " +
                       "Add the 'On screen' time for the display step.";
            }
        }
    }
}
