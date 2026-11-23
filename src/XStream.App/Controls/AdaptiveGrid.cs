using Avalonia;
using Avalonia.Controls;

namespace XStream.App.Controls;

/// <summary>
/// A grid that always fills its width: as many columns as fit at <see cref="MinItemWidth"/> or wider, then every item
/// is stretched to share the row exactly (no ragged gap on the right). Rows are as tall as their tallest item.
/// </summary>
public sealed class AdaptiveGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(MinItemWidth), 150);
    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(ColumnSpacing), 16);
    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(RowSpacing), 20);

    static AdaptiveGrid() => AffectsMeasure<AdaptiveGrid>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        if (double.IsInfinity(width) || width <= 0) width = MinItemWidth * 4 + ColumnSpacing * 3;
        int cols = Math.Max(1, (int)Math.Floor((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)));
        return (cols, (width - ColumnSpacing * (cols - 1)) / cols);
    }

    protected override Size MeasureOverride(Size available)
    {
        var (cols, w) = Layout(available.Width);
        double total = 0, row = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(w, double.PositiveInfinity));
            row = Math.Max(row, Children[i].DesiredSize.Height);
            if (i % cols == cols - 1 || i == Children.Count - 1) { total += row + (total > 0 ? RowSpacing : 0); row = 0; }
        }
        double width = double.IsInfinity(available.Width) ? cols * w + (cols - 1) * ColumnSpacing : available.Width;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (cols, w) = Layout(final.Width);
        double y = 0;
        for (int start = 0; start < Children.Count; start += cols)
        {
            int end = Math.Min(start + cols, Children.Count);
            double rowH = 0;
            for (int i = start; i < end; i++) rowH = Math.Max(rowH, Children[i].DesiredSize.Height);
            for (int i = start; i < end; i++)
                Children[i].Arrange(new Rect((i - start) * (w + ColumnSpacing), y, w, rowH));
            y += rowH + RowSpacing;
        }
        return final;
    }
}

/// <summary>Sizes its child to a fixed height/width ratio of the width it's given (e.g. 1.5 for 2:3 cover art).</summary>
public sealed class AspectBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty = AvaloniaProperty.Register<AspectBox, double>(nameof(Ratio), 1.5);
    static AspectBox() => AffectsMeasure<AspectBox>(RatioProperty);
    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 150 : available.Width;
        var size = new Size(w, w * Ratio);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size final)
    {
        Child?.Arrange(new Rect(0, 0, final.Width, final.Width * Ratio));
        return new Size(final.Width, final.Width * Ratio);
    }
}
