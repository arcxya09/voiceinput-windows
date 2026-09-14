using Microsoft.UI.Xaml.Data;
using System.Globalization;

namespace RealtimeTranscription.Desktop;

public sealed class DateTimeLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            DateTimeOffset time => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            DateTime time => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            _ => string.Empty
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
