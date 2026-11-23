using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XStream.App.Views;

/// <summary>Shown on the first launch of a new version while SDL2/FFmpeg are unpacked from the exe.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    public void Report(double fraction, string what)
    {
        Bar.Value = fraction * 100;
        Percent.Text = $"{fraction * 100:F0}%";
        Detail.Text = fraction >= 1 ? "Starting XStream…" : "Unpacking " + what;
    }

    public void ShowError(string message)
    {
        Working.IsVisible = false;
        Failed.IsVisible = true;
        ErrorText.Text = message;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
