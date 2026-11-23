using System.Globalization;
using System.Text;
using System.Text.Json;
using XStream.Core.Input;
using XStream.Core.Rtp;

namespace XStream.Core.Stability;

/// <summary>What the picture did during one engine tick (from the frame assembler).</summary>
public readonly record struct PictureTick(long FramesDelivered, long FramesLost, bool Broken, double MsSinceLastFrame,
    long NacksSent, long NacksRecovered);

/// <summary>Totals for one play session. Written next to the CSV as .summary.json.</summary>
public sealed record SessionSummary
{
    public string App { get; init; } = "XStream";
    public string Profile { get; init; } = "";
    public DateTimeOffset Started { get; init; }
    public double Minutes { get; init; }
    public double FrozenSec { get; init; }
    public int Freezes { get; init; }
    public double FreezesPerHour => Minutes > 0 ? Freezes / (Minutes / 60) : 0;
    public double FrozenPct => Minutes > 0 ? FrozenSec / (Minutes * 60) * 100 : 0;
    public double AvgMbps { get; init; }
    public double MedianRttMs { get; init; }
    public double AvgLossPct { get; init; }
    public long FramesLost { get; init; }
    public long NacksRecovered { get; init; }
    public int Reconnects { get; init; }
    public bool Failed { get; init; }
    public string Caps { get; init; } = "Unknown";
    public bool RembNegotiated { get; init; }
    public bool NackNegotiated { get; init; }
    public Dictionary<string, double> VerdictPct { get; init; } = new();
    /// <summary>Controller pipeline: time from seeing a change to handing the packet to the network.</summary>
    public double InputP50Us { get; init; }
    public double InputP99Us { get; init; }
    public double InputPollHz { get; init; }
    public double InputWorstGapMs { get; init; }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Rebuild a summary from a session CSV (used by `xstream report`).</summary>
    public static SessionSummary FromCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        var meta = lines.FirstOrDefault(l => l.StartsWith('#')) ?? "";
        var rows = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1).Select(l => l.Split(',')).Where(c => c.Length >= 18).ToList();
        var acc = new Accumulator();
        foreach (var c in rows)
        {
            double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
            acc.Add(D(0) * 1000, D(1) * 1000, D(6), D(3), c[9] == "1", (long)D(10), (long)D(12), (int)D(16), c[13], c[14], c[17]);
        }
        string M(string key) => meta.Split(' ').FirstOrDefault(x => x.StartsWith(key + "="))?[(key.Length + 1)..] ?? "";
        return acc.Build(M("profile"), DateTimeOffset.TryParse(M("started"), out var st) ? st : default,
            M("remb") == "True", M("nack") == "True");
    }

    internal sealed class Accumulator
    {
        private double _firstMs = double.NaN, _lastMs, _frozenMs, _run, _lastT = double.NaN;
        private int _freezes, _reconnects, _n;
        private bool _failed, _counted;
        private double _sumKbps, _sumLoss;
        private long _lost, _recovered;
        private readonly List<double> _rtt = new();
        private readonly Dictionary<string, int> _verdicts = new();
        private string _caps = "Unknown";

        public void Add(double tMs, double kbps, double rtt, double lossPct, bool frozen, long lostTotal, long recoveredTotal,
            int reconnectsTotal, string link, string caps, string verdict)
        {
            if (double.IsNaN(_firstMs)) _firstMs = tMs;
            double dt = double.IsNaN(_lastT) ? 100 : Math.Clamp(tMs - _lastT, 0, 1000);
            _lastT = tMs; _lastMs = tMs; _n++;
            _sumKbps += kbps; _sumLoss += lossPct; _rtt.Add(rtt);
            if (frozen)
            {
                _frozenMs += dt; _run += dt;
                if (_run >= 250 && !_counted) { _freezes++; _counted = true; }
            }
            else { _run = 0; _counted = false; }
            _lost = lostTotal; _recovered = recoveredTotal; _reconnects = reconnectsTotal;
            if (link == nameof(LinkState.Failed)) _failed = true;
            _caps = caps;
            _verdicts[verdict] = _verdicts.GetValueOrDefault(verdict) + 1;
        }

        public SessionSummary Build(string profile, DateTimeOffset started, bool remb, bool nack)
        {
            _rtt.Sort();
            return new SessionSummary
            {
                Profile = profile, Started = started,
                Minutes = _n == 0 ? 0 : (_lastMs - _firstMs + 100) / 60_000,
                FrozenSec = _frozenMs / 1000, Freezes = _freezes,
                AvgMbps = _n == 0 ? 0 : _sumKbps / _n / 1000,
                MedianRttMs = _rtt.Count == 0 ? 0 : _rtt[_rtt.Count / 2],
                AvgLossPct = _n == 0 ? 0 : _sumLoss / _n,
                FramesLost = _lost, NacksRecovered = _recovered, Reconnects = _reconnects, Failed = _failed,
                Caps = _caps, RembNegotiated = remb, NackNegotiated = nack,
                VerdictPct = _verdicts.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value * 100.0 / Math.Max(1, _n), 1)),
            };
        }
    }
}

/// <summary>
/// Writes one row per engine tick (100 ms) to a CSV, counts freezes, and writes a summary when disposed.
/// A "freeze" is the picture not updating for 250 ms or more: either no new frame, or frames held back
/// because a lost frame broke the picture until the next keyframe.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly StreamWriter _w;
    private readonly SessionSummary.Accumulator _acc = new();
    private readonly string _path;
    private readonly string _profile;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private long _lastDelivered;
    private double _lastT = double.NaN;

    public bool RembNegotiated { get; set; }
    public bool NackNegotiated { get; set; }
    /// <summary>Set before Dispose to include controller latency in the summary.</summary>
    public InputStats? Input { get; set; }
    public string CsvPath => _path;
    public string SummaryPath => Path.ChangeExtension(_path, ".summary.json");

    public SessionLogger(string path, string profile)
    {
        _path = path;
        _profile = profile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _w = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = false };
        _w.WriteLine($"# xstream session started={_started:O} profile={profile}");
        _w.WriteLine("t_s,mbps,cap_mbps,loss_pct,jitter_ms,queue_ms,rtt_ms,buffer_ms,fps,frozen,frames_lost,nacks_sent,nacks_recovered,link,caps,keyframes,reconnects,verdict");
    }

    public void Record(in NetworkSample s, in StabilitySnapshot snap, in PictureTick pic)
    {
        double dt = double.IsNaN(_lastT) ? 100 : Math.Max(1, s.TimeMs - _lastT);
        _lastT = s.TimeMs;
        double fps = (pic.FramesDelivered - _lastDelivered) * 1000 / dt;
        _lastDelivered = pic.FramesDelivered;
        bool frozen = pic.Broken || pic.MsSinceLastFrame > 100;
        string verdict = snap.Verdict.Replace(',', ';');

        _w.WriteLine(string.Join(',',
            F(s.TimeMs / 1000, 1), F(s.ReceiveKbps / 1000, 2), F(snap.CapKbps / 1000, 2), F(s.LossFraction * 100, 2),
            F(s.JitterMs, 1), F(s.QueueDelayMs, 1), F(s.RttMs, 0), F(snap.BufferMs, 0), F(fps, 0), frozen ? "1" : "0",
            pic.FramesLost, pic.NacksSent, pic.NacksRecovered, snap.Link, snap.Caps, snap.KeyframeRequests, snap.Reconnects, verdict));
        _acc.Add(s.TimeMs, s.ReceiveKbps, s.RttMs, s.LossFraction * 100, frozen, pic.FramesLost, pic.NacksRecovered,
            snap.Reconnects, snap.Link.ToString(), snap.Caps.ToString(), verdict);
        if ((long)(s.TimeMs / 100) % 50 == 0) _w.Flush(); // ~every 5 s, so a crash loses little
    }

    public SessionSummary Summary
    {
        get
        {
            var sum = _acc.Build(_profile, _started, RembNegotiated, NackNegotiated);
            return Input is { } i
                ? sum with { InputP50Us = Math.Round(i.DetectToSendP50Us, 1), InputP99Us = Math.Round(i.DetectToSendP99Us, 1), InputPollHz = Math.Round(i.PollHzActual), InputWorstGapMs = Math.Round(i.WorstPollGapMs, 2) }
                : sum;
        }
    }

    public void Dispose()
    {
        _w.WriteLine($"# end remb={RembNegotiated} nack={NackNegotiated}");
        _w.Dispose();
        // Store negotiation flags in the header-style meta for `report` too.
        var all = File.ReadAllLines(_path).ToList();
        all[0] += $" remb={RembNegotiated} nack={NackNegotiated}";
        File.WriteAllLines(_path, all);
        File.WriteAllText(SummaryPath, JsonSerializer.Serialize(Summary, SessionSummary.Json));
    }

    private static string F(double v, int digits) => Math.Round(v, digits).ToString(CultureInfo.InvariantCulture);
}
