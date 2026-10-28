using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wisp.App.Views;

public static class Converters
{
    /// <summary>Connection state → colour of the little status dot.</summary>
    public static readonly IValueConverter StatusBrush = new FuncValueConverter<string?, IBrush?>(kind => kind switch
    {
        "online" => Resource("WispOk"),
        "syncing" => Resource("WispAccent"),
        "degraded" => Resource("WispNotice"),
        "error" => Resource("WispDanger"),
        _ => Resource("WispMuted"),
    });

    public static readonly IValueConverter HexToBrush = new FuncValueConverter<string?, IBrush?>(hex =>
        Color.TryParse(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent);

    public static readonly IValueConverter NotNull = new FuncValueConverter<object?, bool>(value => value is not null);

    public static readonly IValueConverter IsNull = new FuncValueConverter<object?, bool>(value => value is null);

    static IBrush? Resource(string key) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;
}

public static class ResourceExtensions
{
    /// <summary>Looks up a brush including the light/dark theme dictionaries (plain FindResource skips those).</summary>
    public static IBrush? Brush(this StyledElement element, string key) =>
        element.TryFindResource(key, element.ActualThemeVariant, out var value) ? value as IBrush : null;
}

public sealed class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
