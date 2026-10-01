using System;
using System.Globalization;
using System.Windows.Data;

namespace OsuScoutNew
{
    // 125 -> "2:05", for the map list's length column.
    public class SecondsToClockConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is int seconds && seconds > 0 ? $"{seconds / 60}:{seconds % 60:00}" : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
