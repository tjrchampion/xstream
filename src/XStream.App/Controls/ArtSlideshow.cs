using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using XStream.App.Services;

namespace XStream.App.Controls;

/// <summary>
/// Full-bleed game art that cross-fades from one picture to the next with a slow zoom and drift.
/// Runs only while IsActive (and only when there is art), so it costs nothing in the background.
/// </summary>
public sealed class ArtSlideshow : Panel
{
    public static readonly StyledProperty<IReadOnlyList<GameArt>?> ItemsProperty =
        AvaloniaProperty.Register<ArtSlideshow, IReadOnlyList<GameArt>?>(nameof(Items));
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ArtSlideshow, bool>(nameof(IsActive));
    public static readonly StyledProperty<string> CurrentNameProperty =
        AvaloniaProperty.Register<ArtSlideshow, string>(nameof(CurrentName), "");
    public static readonly StyledProperty<bool> HasArtProperty =
        AvaloniaProperty.Register<ArtSlideshow, bool>(nameof(HasArt));

    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan Fade = TimeSpan.FromSeconds(1.4);
    private static readonly TimeSpan Drift = Hold + Fade + TimeSpan.FromSeconds(1);

    private readonly Image[] _layers = [NewLayer(), NewLayer()];
    private readonly DispatcherTimer _timer;
    private int _front = -1, _next, _version;
    private int[] _order = [];

    public ArtSlideshow()
    {
        ClipToBounds = true;
        foreach (var l in _layers) Children.Add(l);
        _timer = new DispatcherTimer { Interval = Hold };
        _timer.Tick += (_, _) => _ = ShowNextAsync();
    }

    public IReadOnlyList<GameArt>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    /// <summary>The game whose art is showing.</summary>
    public string CurrentName { get => GetValue(CurrentNameProperty); private set => SetValue(CurrentNameProperty, value); }
    public bool HasArt { get => GetValue(HasArtProperty); private set => SetValue(HasArtProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty || change.Property == IsActiveProperty) Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    private void Restart()
    {
        Stop();
        var items = Items;
        HasArt = items is { Count: > 0 };
        if (!IsActive || !HasArt) return;
        // Most recently played first, then the rest in a fresh order each time.
        _order = [0, .. Enumerable.Range(1, items!.Count - 1).OrderBy(_ => Random.Shared.Next())];
        _next = 0;
        _ = ShowNextAsync();
        if (items!.Count > 1) _timer.Start();
    }

    private void Stop()
    {
        _timer.Stop();
        _version++;
        foreach (var l in _layers)
        {
            l.Opacity = 0;
            (l.Source as IDisposable)?.Dispose();
            l.Source = null;
        }
        _front = -1;
        CurrentName = "";
    }

    private async Task ShowNextAsync()
    {
        var items = Items;
        if (items is not { Count: > 0 }) return;
        int version = _version;
        var art = items[_order.Length == items.Count ? _order[_next % items.Count] : _next % items.Count];
        _next++;

        Bitmap? bmp = null;
        try
        {
            bmp = await Task.Run(() =>
            {
                using var f = File.OpenRead(art.Path);
                return Bitmap.DecodeToWidth(f, 1920, BitmapInterpolationMode.HighQuality);
            });
        }
        catch { /* unreadable file: skip to the next one on the next tick */ }
        if (bmp is null) return;
        if (version != _version) { bmp.Dispose(); return; }

        int back = _front < 0 ? 0 : 1 - _front;
        var layer = _layers[back];
        (layer.Source as IDisposable)?.Dispose();
        layer.Source = bmp;

        // Jump to the start pose without animating, then glide to the end pose.
        bool even = _next % 2 == 0;
        layer.Transitions = null;
        layer.Opacity = 0;
        layer.RenderTransform = TransformOperations.Parse(even ? "scale(1.04) translate(-14px, 0px)" : "scale(1.12) translate(14px, -6px)");
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _version) return;
            layer.Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = Fade, Easing = new CubicEaseInOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Drift, Easing = new SineEaseInOut() },
            ];
            layer.Opacity = 1;
            layer.RenderTransform = TransformOperations.Parse(even ? "scale(1.12) translate(14px, -6px)" : "scale(1.04) translate(-14px, 0px)");
            // The new picture fades in on top; the old one drops out once it's covered.
            layer.ZIndex = 1;
            if (_front >= 0)
            {
                var old = _layers[_front];
                old.ZIndex = 0;
                DispatcherTimer.RunOnce(() => { if (version == _version && old != _layers[_front]) old.Opacity = 0; }, Fade);
            }
            _front = back;
            CurrentName = art.Name;
        }, DispatcherPriority.Background);
    }

    private static Image NewLayer() => new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        RenderTransformOrigin = RelativePoint.Center,
        Opacity = 0,
    };
}
