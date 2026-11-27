using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SpaceLens.Core;
using SpaceLens.Core.Advisor;
using SpaceLens.Core.Diagnostics;

namespace SpaceLens;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        long l => ByteFormat.Format(l),
        int i => ByteFormat.Format(i),
        double d => ByteFormat.Format(d),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool x => x,
            int i => i != 0,
            string s => !string.IsNullOrEmpty(s),
            null => false,
            _ => true,
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public bool Soft { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            Severity.Critical => "Critical",
            Severity.Warning => "Warning",
            Severity.Info => "Info",
            Severity.Good => "Good",
            RiskLevel.Safe => "Good",
            RiskLevel.Review => "Warning",
            RiskLevel.Advanced => "Critical",
            _ => "Info",
        };
        return Application.Current.Resources[key + (Soft ? "SoftBrush" : "Brush")] as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => "", // ErrorBadge
        Severity.Warning => "",  // Warning
        Severity.Info => "",     // Info
        _ => "",                 // CheckMark
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Maps a 0–100 percentage to the accent/warning/critical colour.</summary>
public sealed class LoadBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        string key = v >= 90 ? "CriticalBrush" : v >= 75 ? "WarningBrush" : "AccentBrush";
        return Application.Current.Resources[key] as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
