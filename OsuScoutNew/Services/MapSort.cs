using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using OsuScout;

namespace OsuScoutNew.Services
{
    // Sorts the map list the way the column headers ask (Shift-click adds columns), reading
    // each property directly. WPF's own sort (SortDescriptions) looks properties up by name
    // for every comparison: 80-160 ms for 9,360 maps against about 5 ms here, paid on every
    // filter change, which made dragging a slider stutter.
    public sealed class MapSort : IComparer
    {
        private readonly (Comparison<BeatmapRecord> Compare, int Sign)[] _keys;

        // Each column is named by its SortMemberPath, first column first.
        public MapSort(IEnumerable<(string Property, bool Descending)> columns)
        {
            _keys = columns.Select(c => (For(c.Property), c.Descending ? -1 : 1))
                           .Where(k => k.Item1 != null)
                           .ToArray();
        }

        public bool IsEmpty => _keys.Length == 0;

        public int Compare(object x, object y)
        {
            var a = (BeatmapRecord)x;
            var b = (BeatmapRecord)y;
            foreach (var (compare, sign) in _keys)
            {
                int result = compare(a, b);
                if (result != 0) return result * sign;
            }
            return 0;
        }

        private static Comparison<BeatmapRecord> For(string property) => property switch
        {
            "Artist" => (a, b) => Text(a.Artist, b.Artist),
            "Title" => (a, b) => Text(a.Title, b.Title),
            "Version" => (a, b) => Text(a.Version, b.Version),
            "Mapper" => (a, b) => Text(a.Mapper, b.Mapper),
            "Tags" => (a, b) => Text(a.Tags, b.Tags),
            "StarRating" => (a, b) => a.StarRating.CompareTo(b.StarRating),
            "BPM" => (a, b) => a.BPM.CompareTo(b.BPM),
            "LengthSeconds" => (a, b) => a.LengthSeconds.CompareTo(b.LengthSeconds),
            // Maps whose CS/AR/OD/HP haven't been read yet (null) come first, as WPF sorted them.
            "CS" => (a, b) => Nullable.Compare(a.CS, b.CS),
            "AR" => (a, b) => Nullable.Compare(a.AR, b.AR),
            "OD" => (a, b) => Nullable.Compare(a.OD, b.OD),
            "HP" => (a, b) => Nullable.Compare(a.HP, b.HP),
            _ => null
        };

        private static int Text(string a, string b) => string.Compare(a, b, StringComparison.CurrentCulture);
    }
}
