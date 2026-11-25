using Avalonia.Controls;
using Avalonia.Interactivity;
using XStream.App.ViewModels;

namespace XStream.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(text);
    }

    private void CopyTarget(object? sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) Copy(vm.ShortcutTarget); }
    private void CopyOptions(object? sender, RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) Copy(vm.ShortcutOptions); }
}
