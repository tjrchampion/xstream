using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XStream.App.Services;
using XStream.Core.Config;

namespace XStream.App.ViewModels;

public sealed class SessionItemViewModel(PastSession p)
{
    public string Date { get; } = p.When.ToString("ddd d MMM, HH:mm");
    public string Profile { get; } = p.Summary.Profile;
    public string Duration { get; } = p.Summary.Minutes >= 60
        ? $"{(int)(p.Summary.Minutes / 60)} h {(int)(p.Summary.Minutes % 60)} min"
        : $"{Math.Max(1, Math.Round(p.Summary.Minutes))} min";
    public string Bitrate { get; } = $"{p.Summary.AvgMbps:F1} Mbps";
    public string Ping { get; } = $"{p.Summary.MedianRttMs:F0} ms";
    public string Loss { get; } = $"{p.Summary.AvgLossPct:F2}%";
    public string Freezes { get; } = p.Summary.Freezes == 0 ? "No freezes" : $"{p.Summary.Freezes} freeze{(p.Summary.Freezes == 1 ? "" : "s")} ({p.Summary.FrozenSec:F1} s)";
    public string Quality { get; } = p.Summary.Failed ? "bad" : p.Summary.FreezesPerHour > 10 || p.Summary.AvgLossPct > 2 ? "warn" : "good";
    public string MainVerdict { get; } = p.Summary.VerdictPct.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault() ?? "";
    public string Caps { get; } = p.Summary.Caps switch
    {
        "Honoured" => "Console follows bitrate caps",
        "Ignored" => "Console ignored bitrate caps: lower the maximum bitrate",
        _ => "",
    };
    public bool HasCaps => Caps.Length > 0;
    public bool CapsIgnored { get; } = p.Summary.Caps == "Ignored";
    public bool Failed { get; } = p.Summary.Failed;
    public string CsvPath { get; } = p.CsvPath;
}

/// <summary>Past sessions, newest first, from the logs folder.</summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string Totals { get; set; } = "";

    [RelayCommand]
    public void Refresh()
    {
        Sessions.Clear();
        var list = SessionHistory.Load();
        foreach (var p in list) Sessions.Add(new SessionItemViewModel(p));
        IsEmpty = Sessions.Count == 0;
        double hours = list.Sum(p => p.Summary.Minutes) / 60;
        int freezes = list.Sum(p => p.Summary.Freezes);
        Totals = list.Count == 0 ? "" : $"{list.Count} session{(list.Count == 1 ? "" : "s")} · {hours:F1} h played · {(hours > 0 ? freezes / hours : 0):F1} freezes per hour";
    }

    [RelayCommand] private void OpenLogs() => Platform.Open(AppPaths.Logs);
    [RelayCommand] private void Reveal(SessionItemViewModel? s) { if (s is not null) Platform.Open(Path.GetDirectoryName(s.CsvPath)!); }
}
