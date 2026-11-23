using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace XStream.App;

/// <summary>Converters used in XAML.</summary>
public static class Conv
{
    /// <summary>True when the value's text equals the converter parameter (for Classes.x bindings).</summary>
    public static readonly IValueConverter Is = new EqualsConverter();

    /// <summary>true -> 1, false -> 0 (fade something in once it's ready).</summary>
    public static readonly IValueConverter BoolToOpacity = new FuncValueConverter<bool, double>(b => b ? 1 : 0);

    /// <summary>"Icon.Console" resource key -> geometry.</summary>
    public static readonly IValueConverter Icon = new FuncValueConverter<string?, object?>(key =>
        key is not null && Avalonia.Application.Current!.TryFindResource(key, out var g) ? g : null);

    private sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
