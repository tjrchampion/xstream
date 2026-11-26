using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Diagnostics;
using XStream.Core.Xbox;

namespace XStream.App.ViewModels;

public sealed record FindingItem(string Title, string Detail, string Kind); // Kind: good | fair | poor

/// <summary>The Consoles page's "Check connection" panel: progress while the test stream runs, then the advice.</summary>
public sealed partial class ConnectionCheckViewModel : ObservableObject
{
    private readonly ConnectionChecker _checker = new();
    private readonly SettingsStore _settings;
    private CancellationTokenSource? _cts;
    private CheckResult? _result;

    public ConnectionCheckViewModel(SettingsStore settings)
    {
        _settings = settings;
        _checker.Progress += (text, f) => { Status = text; Fraction = f * 100; };
    }

    /// <summary>Raised when a check starts or ends (the page blocks streaming meanwhile).</summary>
    public event Action<bool>? RunningChanged;

    [ObservableProperty] public partial bool IsOpen { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasResult))] public partial bool IsRunning { get; set; }
    [ObservableProperty] public partial string ConsoleName { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial string Headline { get; set; } = "";
    /// <summary>good | fair | poor</summary>
    [ObservableProperty] public partial string Level { get; set; } = "";
    [ObservableProperty] public partial string Numbers { get; set; } = "";
    [ObservableProperty] public partial string LinkText { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanApply))] public partial string ApplyText { get; set; } = "";
    [ObservableProperty] public partial string Applied { get; set; } = "";
    public ObservableCollection<FindingItem> Findings { get; } = [];
    public bool HasResult => !IsRunning && Headline.Length > 0;
    public bool CanApply => ApplyText.Length > 0;

    public async Task StartAsync(string serverId, string name, bool consoleOnWifi)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        IsOpen = true;
        IsRunning = true;
        RunningChanged?.Invoke(true);
        ConsoleName = name;
        Headline = ""; Findings.Clear(); ApplyText = ""; Applied = ""; Numbers = ""; LinkText = "";
        Status = "Connecting to the console…"; Fraction = 2;
        try
        {
            var r = await _checker.RunAsync(serverId, name, consoleOnWifi, _cts.Token);
            Show(r);
        }
        catch (OperationCanceledException) { IsOpen = false; }
        catch (Exception ex)
        {
            Show(new CheckResult { Level = CheckLevel.Poor, Headline = "The check couldn't run", Findings = [new(CheckLevel.Poor, "Error", ex.Message)] });
        }
        finally
        {
            IsRunning = false;
            RunningChanged?.Invoke(false);
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>Show a one-line reason the test can't run.</summary>
    public void ShowMessage(string title, string detail)
    {
        IsOpen = true;
        Show(new CheckResult { Level = CheckLevel.Fair, Headline = title, Findings = [new(CheckLevel.Fair, title, detail)] });
        OnPropertyChanged(nameof(HasResult));
    }

    /// <summary>Testing: a sample result for a 2.4 GHz device and a console on Wi-Fi (nothing runs).</summary>
    public void Demo()
    {
        var samples = Enumerable.Range(0, 12).Select(i => new CheckSample(i < 6 ? 14 : 22, 50, i < 6 ? 0.2 : 2.5, 9, i < 6 ? 3 : 16)).ToList();
        var link = new LocalLink { Kind = "Wi-Fi", BandGHz = 2.4, SignalPct = 62, LinkMbps = 144 };
        IsOpen = true; ConsoleName = "Xbox Series X";
        Show(ConnectionCheck.Analyze(samples, link, consoleOnWifi: true, "Home network (direct)") with { Link = link, Route = "Home network (direct)" });
        OnPropertyChanged(nameof(HasResult));
    }

    private void Show(CheckResult r)
    {
        _result = r;
        Level = r.Level switch { CheckLevel.Good => "good", CheckLevel.Fair => "fair", _ => "poor" };
        Headline = r.Headline;
        Findings.Clear();
        foreach (var f in r.Findings)
            Findings.Add(new FindingItem(f.Title, f.Detail, f.Level switch { CheckLevel.Good => "good", CheckLevel.Fair => "fair", _ => "poor" }));
        Numbers = r.PingMs > 0 || r.PeakMbps > 0
            ? $"{r.PingMs:F0} ms to the console  ·  {r.JitterMs:F1} ms jitter  ·  {r.LossPct:F1}% packet loss  ·  up to {r.PeakMbps:F0} Mbps seen"
            : "";
        string route = r.Route.StartsWith("Home") ? "on your home network" : r.Route.StartsWith("Internet") ? "over the internet"
            : r.Route.StartsWith("Relayed") ? "through a relay" : "";
        LinkText = "This device: " + r.Link.Describe() + (route.Length > 0 ? "  ·  Reached the console " + route : "");

        var s = _settings.Current;
        var parts = new List<string>();
        if (r.RecommendFastest && s.Resolution != StreamResolution.R720) parts.Add("Quality: Fastest (720p)");
        if (r.RecommendedMaxMbps is { } max && Math.Abs(max - s.MaxMbps) >= 1) parts.Add($"Maximum bitrate: {max:F0} Mbps");
        if (r.Level == CheckLevel.Poor && s.Profile != Core.Stability.StreamProfile.Smooth && r.JitterMs >= 15) parts.Add("Profile: Smooth");
        ApplyText = parts.Count > 0 ? "Use " + string.Join(", ", parts) : "";
    }

    [RelayCommand]
    private void Apply()
    {
        if (_result is not { } r) return;
        _settings.Update(s =>
        {
            if (r.RecommendFastest) s = s with { Resolution = StreamResolution.R720 };
            if (r.RecommendedMaxMbps is { } max) s = s with { MaxMbps = max, StartMbps = Math.Min(s.StartMbps, max) };
            if (r.Level == CheckLevel.Poor && r.JitterMs >= 15) s = s with { Profile = Core.Stability.StreamProfile.Smooth };
            return s;
        });
        Applied = "Saved. It applies from your next stream.";
        ApplyText = "";
    }

    [RelayCommand]
    private void Close()
    {
        if (IsRunning) { _cts?.Cancel(); _checker.Cancel(); }
        IsOpen = false;
    }
}
