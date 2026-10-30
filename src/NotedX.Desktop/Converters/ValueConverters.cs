using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NotedX.Desktop.Services;

namespace NotedX.Desktop.Converters;

/// <summary>Shows an element when a boolean is <c>false</c>.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Negates a boolean value.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>Maps a toast tone to the accent or tinted background brush.</summary>
public sealed class ToastKindToBrushConverter : IValueConverter
{
    private static readonly Dictionary<ToastKind, Color> Colors = new()
    {
        [ToastKind.Info] = Color.FromRgb(0x63, 0x66, 0xF1),
        [ToastKind.Success] = Color.FromRgb(0x22, 0xC5, 0x5E),
        [ToastKind.Warning] = Color.FromRgb(0xF5, 0x9E, 0x0B),
        [ToastKind.Error] = Color.FromRgb(0xEF, 0x44, 0x44)
    };

    private static readonly Dictionary<ToastKind, SolidColorBrush> AccentBrushes = Colors.ToDictionary(
        pair => pair.Key,
        pair => Freeze(new SolidColorBrush(pair.Value)));

    private static readonly Dictionary<ToastKind, SolidColorBrush> SoftBrushes = Colors.ToDictionary(
        pair => pair.Key,
        pair => Freeze(new SolidColorBrush(Color.FromArgb(0x24, pair.Value.R, pair.Value.G, pair.Value.B))));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is ToastKind toastKind ? toastKind : ToastKind.Info;
        var soft = string.Equals(parameter as string, "soft", StringComparison.OrdinalIgnoreCase);
        return soft ? SoftBrushes[kind] : AccentBrushes[kind];
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>Returns a compact glyph for a toast tone.</summary>
public sealed class ToastKindToGlyphConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ToastKind kind
            ? kind switch
            {
                ToastKind.Success => "\u2713",
                ToastKind.Warning => "!",
                ToastKind.Error => "\u2715",
                _ => "i"
            }
            : "i";

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
