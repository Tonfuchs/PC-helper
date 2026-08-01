using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PCHelper.Diagnostics;

namespace PCHelper.Controls;

/// <summary>Ordnet jedem Schweregrad eine Farbe zu.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Critical = Freeze("#F2555A");
    public static readonly SolidColorBrush Warning = Freeze("#F3B13C");
    public static readonly SolidColorBrush Ok = Freeze("#35C48A");
    public static readonly SolidColorBrush Info = Freeze("#6FA8FF");

    /// <summary>Bei true wird eine transparente Variante fuer Hintergruende geliefert.</summary>
    public bool Background { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var brush = (value as Severity?) switch
        {
            Severity.Critical => Critical,
            Severity.Warning => Warning,
            Severity.Ok => Ok,
            _ => Info,
        };

        if (!Background) return brush;

        var c = brush.Color;
        var result = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B));
        result.Freeze();
        return result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Zeigt ein Element nur, wenn der Text nicht leer ist.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Zeigt ein Element nur, wenn der Wert nicht null ist.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Kehrt einen Wahrheitswert um und liefert eine Sichtbarkeit.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
