using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace XStream.App.Services;

/// <summary>Small OS helpers: open a link or folder, copy text.</summary>
public static class Platform
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }

    /// <summary>Show a file selected in Explorer / Finder.</summary>
    public static void Reveal(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", file]);
            else Open(Path.GetDirectoryName(file)!);
        }
        catch { }
    }

    public static async Task CopyAsync(string text)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
            && TopLevel.GetTopLevel(w)?.Clipboard is { } cb)
            await cb.SetTextAsync(text);
    }

    public static Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
