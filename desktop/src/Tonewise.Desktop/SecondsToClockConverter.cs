using Microsoft.UI.Xaml.Data;

namespace Tonewise.Desktop;

/// <summary>Formats a seconds value (seek slider thumb tooltip) as m:ss / h:mm:ss.</summary>
public sealed class SecondsToClockConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        double seconds = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0.0,
        };
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
