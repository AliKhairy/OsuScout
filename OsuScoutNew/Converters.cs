using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    // One tag in the AI TAGS column. Each is laid out on its own, so a long list wraps between
    // tags and never inside one ("no-" / "triples").
    public record TagChip(string Text, string Separator, bool IsMatch);

    // A map's stored tags ("aim,jumps,streams") plus the tags the filter asks for -> the
    // column's chips, with the ones the filter matched marked so they can be highlighted.
    // Matching works like the search: "streams" also matches "spaced streams".
    public class TagListConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var tags = (values.Length > 0 ? values[0] as string : null)?
                           .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
            var wanted = values.Length > 1 && values[1] is IEnumerable<string> terms ? terms.ToList() : new List<string>();

            return tags.Select((tag, i) => new TagChip(
                tag,
                i < tags.Length - 1 ? ", " : "",
                wanted.Any(w => tag.Contains(w, StringComparison.OrdinalIgnoreCase)))).ToList();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
