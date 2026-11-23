using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using XStream.App.ViewModels;

namespace XStream.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Keyboard equivalents of the controller shortcuts.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            // Esc in a text box with text in it clears the text (e.g. Cloud search), rather than going back.
            if (e.Key == Key.Escape && e.Source is TextBox { Text.Length: > 0 }) return;
            if (e.Key == Key.Escape) { vm.Back(); e.Handled = true; }
            else if (e.Key == Key.F11) { ToggleFullscreen?.Invoke(); e.Handled = true; }
            else if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Tab) { vm.SwitchPage(+1); e.Handled = true; }
            else if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.Tab) { vm.SwitchPage(-1); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>F11: switch the launcher between full screen and a window (saved as a setting).</summary>
    public Action? ToggleFullscreen { get; set; }

    private void OnStreamPillClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedNav = vm.Nav[0];
    }
}
