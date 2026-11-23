using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XStream.App.Controls;

/// <summary>Tiny line chart of the last minute of a live number (bitrate, ping, latency).</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<double[]?> ValuesProperty = AvaloniaProperty.Register<Sparkline, double[]?>(nameof(Values));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.White);
    /// <summary>Lowest top-of-chart value, so a flat 2 ms ping doesn't fill the whole height.</summary>
    public static readonly StyledProperty<double> MinRangeProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(MinRange), 1);
    /// <summary>Samples the chart is wide enough for (it fills from the right).</summary>
    public static readonly StyledProperty<int> CapacityProperty = AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, MinRangeProperty, CapacityProperty);

    public double[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double MinRange { get => GetValue(MinRangeProperty); set => SetValue(MinRangeProperty, value); }
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var v = Values;
        var b = Bounds;
        if (v is null || v.Length < 2 || b.Width < 4 || b.Height < 4) return;
        double max = Math.Max(MinRange, v.Max()) * 1.15;
        double step = b.Width / Math.Max(1, Capacity - 1);
        double x0 = b.Width - (v.Length - 1) * step;
        Point P(int i) => new(x0 + i * step, b.Height - 2 - (b.Height - 4) * Math.Clamp(v[i] / max, 0, 1));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(P(0), false);
            for (int i = 1; i < v.Length; i++) g.LineTo(P(i));
            g.EndFigure(false);
        }
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(x0, b.Height), true);
            for (int i = 0; i < v.Length; i++) g.LineTo(P(i));
            g.LineTo(new Point(b.Width, b.Height));
            g.EndFigure(true);
        }
        if (Stroke is ISolidColorBrush sc)
        {
            var c = sc.Color;
            ctx.DrawGeometry(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(70, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
            }, null, area);
        }
        ctx.DrawGeometry(null, new Pen(Stroke, 1.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        var last = P(v.Length - 1);
        ctx.DrawEllipse(Stroke, null, last, 2.5, 2.5);
    }
}
