using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using XStream.App.ViewModels;

namespace XStream.App.Views;

public partial class MappingView : UserControl
{
    public MappingView()
    {
        InitializeComponent();
        // Keyboard & mouse: while a button waits for its key, the next key press anywhere on the page is taken (before
        // Tab moves focus or Space presses a button), and mouse buttons / the wheel in the box.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        var box = this.FindControl<Border>("KeyCatcher")!;
        box.PointerPressed += (_, e) =>
        {
            string? name = e.GetCurrentPoint(box).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonPressed => "MouseLeft",
                PointerUpdateKind.RightButtonPressed => "MouseRight",
                PointerUpdateKind.MiddleButtonPressed => "MouseMiddle",
                PointerUpdateKind.XButton1Pressed => "Mouse4",
                PointerUpdateKind.XButton2Pressed => "Mouse5",
                _ => null,
            };
            if (name is not null && Listening is { } vm) { vm.BindKey(name); e.Handled = true; }
        };
        box.PointerWheelChanged += (_, e) =>
        {
            if (Listening is not { } vm || e.Delta.Y == 0) return;
            vm.BindKey(e.Delta.Y > 0 ? "WheelUp" : "WheelDown");
            e.Handled = true;
        };
    }

    private MappingViewModel? Listening => DataContext is MappingViewModel { KeyboardView: true, HasKeySlot: true } vm ? vm : null;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Listening is not { } vm) return;
        if (e.PhysicalKey == PhysicalKey.Escape) { vm.CloseKeyCommand.Execute(null); e.Handled = true; return; }
        if (e.PhysicalKey != PhysicalKey.None && vm.BindKey(e.PhysicalKey.ToString())) e.Handled = true;
    }
}
