using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using XStream.App.ViewModels;

namespace XStream.App.Views;

/// <summary>Xbox Cloud Gaming library: a hero banner, recently played, then every playable game, with search.</summary>
public partial class CloudView : UserControl
{
    public CloudView()
    {
        InitializeComponent();
        // The hero banner follows whichever recently played game is selected (controller focus) or under the mouse.
        RecentRow.AddHandler(GotFocusEvent, (_, e) => Feature(e.Source), RoutingStrategies.Bubble);
        RecentRow.AddHandler(PointerMovedEvent, (_, e) => Feature(e.Source), RoutingStrategies.Bubble);
        // Esc in the search box clears it (instead of the launcher's Back).
        SearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is CloudViewModel vm && vm.IsFiltering) { vm.ClearSearchCommand.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }

    private void Feature(object? source)
    {
        if (DataContext is CloudViewModel vm && (source as Control)?.DataContext is CloudGameViewModel g) { vm.Feature(g); vm.PauseCarousel(); }
    }
}
