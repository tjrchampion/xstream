using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace XStream.App.Controls;

/// <summary>
/// Text with controller buttons drawn inline: "{view}" and "{menu}" become round button badges with the
/// View / Menu symbols, so "hold {view} + {menu}" shows the actual buttons instead of their names.
/// </summary>
public sealed partial class HintText : TextBlock
{
    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<HintText, string?>(nameof(Hint));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }

    /// <summary>The hint as plain words (for logs and tooltips).</summary>
    public static string Plain(string text) => text.Replace("{view}", "View").Replace("{menu}", "Menu");

    [GeneratedRegex(@"(\{view\}|\{menu\})")]
    private static partial Regex Token();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HintProperty || change.Property == FontSizeProperty) Build();
    }

    private void Build()
    {
        var inlines = new InlineCollection();
        foreach (var part in Token().Split(Hint ?? ""))
        {
            if (part.Length == 0) continue;
            if (part is "{view}" or "{menu}")
                inlines.Add(new InlineUIContainer(Badge(part == "{view}" ? "Icon.PadView" : "Icon.PadMenu"))
                    { BaselineAlignment = BaselineAlignment.Baseline });
            else inlines.Add(new Run(part));
        }
        Inlines = inlines;
    }

    private Control Badge(string icon)
    {
        double d = Math.Round(FontSize * 1.55);
        var glyph = new PathIcon
        {
            Width = d * 0.55, Height = d * 0.55,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (Application.Current!.TryGetResource(icon, null, out var g) && g is Geometry geo) glyph.Data = geo;
        return new Border
        {
            // Sit on the baseline, then drop so the badge centres on the lowercase letters.
            Width = d, Height = d, CornerRadius = new CornerRadius(d / 2), Margin = new Thickness(2, 0, 2, -(d / 2 - FontSize * 0.35)),
            Background = new SolidColorBrush(Color.Parse("#3A424E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#5A6472")), BorderThickness = new Thickness(1),
            Child = glyph,
        };
    }
}
