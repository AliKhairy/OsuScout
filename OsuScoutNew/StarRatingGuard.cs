using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OsuScout
{
    // rosu-pp, which computes star ratings, builds every tick, repeat and tail of
    // every slider up front, in native code. A gimmick map with one long, slow slider
    // at a high tick rate comes to tens of millions of them: rosu allocates until
    // Windows runs out of memory, and the process dies outright. That is a native
    // abort, not an exception, so no catch block sees it (issue #12). rosu's own
    // IsSuspicious check misses it too: it flags slider repeats only once more than
    // 128 sliders have over 1000 of them, and doesn't look at ticks at all.
    //
    // So before a map goes to rosu, this counts the ticks rosu would build. It reads
    // [HitObjects] itself rather than using RawHitObject, which mirrors the training
    // repo's parser and drops details rosu still acts on, such as a slider's repeats
    // when its length is missing.
    public static class StarRatingGuard
    {
        // rosu-pp needed 40-70 bytes per tick when measured, so this keeps one map
        // under 70 MB and the scan's parallel workers under 1 GB together. Real maps
        // are far below it: across 9,375 osu!standard maps the median was 561 and
        // the most, an Aspire map, about 55,000.
        public const double MaxSliderTicks = 1_000_000;

        // rosu-map's clamps (section/difficulty.rs, control_points/difficulty.rs).
        // The tick rate's lower bound is the one that matters: unclamped, a tick rate
        // of 0.001 would look like no ticks at all where rosu reads it as 0.5.
        private const double MinSliderMultiplier = 0.4, MaxSliderMultiplier = 3.6;
        private const double MinTickRate = 0.5, MaxTickRate = 8.0;
        private const double MinVelocity = 0.1, MaxVelocity = 10.0;
        private const double BaseScoringDistance = 100.0;

        // False for a map with too many ticks, and for one that can't be estimated
        // (the file wasn't read, or a value is NaN): neither should reach rosu.
        public static bool IsSafeToRate(OsuParser parser)
            => parser.Lines is { Length: > 0 } && EstimateSliderTicks(parser) <= MaxSliderTicks;

        // An upper bound on what rosu-pp builds: per slider, spans * (length / tick
        // distance + 2), where tick distance is 100 * SliderMultiplier * velocity /
        // SliderTickRate. Every shortcut errs towards more ticks: velocity is capped
        // at 1 (maps before format v8 don't shrink tick distance with it), length and
        // spans are left uncapped, and a slider with no length is given twice its
        // control polygon (a bezier path is never longer than its polygon, and a
        // perfect-circle arc at most pi/2 times as long).
        public static double EstimateSliderTicks(OsuParser parser)
        {
            var difficulty = parser.GetDifficulty();
            double multiplier = Math.Clamp(difficulty.GetValueOrDefault("SliderMultiplier", 1.4), MinSliderMultiplier, MaxSliderMultiplier);
            double tickRate = Math.Clamp(difficulty.GetValueOrDefault("SliderTickRate", 1.0), MinTickRate, MaxTickRate);
            var velocity = new VelocityLookup(parser.GetTimingPoints());

            double ticks = 0;
            foreach (string line in HitObjectLines(parser.Lines))
            {
                string[] parts = line.Split(',');
                if (parts.Length < 6 || !int.TryParse(parts[3], out int type) || (type & 2) == 0) continue;
                if (!TryParse(parts[2], out double time)) continue;

                double spans = parts.Length > 6 && TryParse(parts[6], out double s) ? Math.Max(s, 1) : 1;
                double length = parts.Length > 7 && TryParse(parts[7], out double l) ? l : 0;
                if (!(length > 0)) length = 2 * ControlPolygonLength(parts);

                double tickDistance = BaseScoringDistance * multiplier * Math.Min(velocity.At(time), 1.0) / tickRate;
                ticks += spans * (length / tickDistance + 2);
            }
            return ticks;
        }

        private static IEnumerable<string> HitObjectLines(string[] lines)
        {
            bool inside = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inside = line == "[HitObjects]";
                    continue;
                }
                if (inside && line.Length > 0 && !line.StartsWith("//")) yield return line;
            }
        }

        // From the head through every control point in "B|x:y|x:y".
        private static double ControlPolygonLength(string[] parts)
        {
            if (!TryParse(parts[0], out double x) || !TryParse(parts[1], out double y)) return 0;
            double total = 0;
            foreach (string point in parts[5].Split('|').Skip(1))
            {
                string[] xy = point.Split(':');
                if (xy.Length != 2 || !TryParse(xy[0], out double nx) || !TryParse(xy[1], out double ny)) continue;
                total += Math.Sqrt((nx - x) * (nx - x) + (ny - y) * (ny - y));
                (x, y) = (nx, ny);
            }
            return total;
        }

        private static bool TryParse(string s, out double value)
            => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        // Slider velocity over time. Every timing line sets it, as rosu-map reads them:
        // a negative beat length to -100 / beat length, anything else back to 1.
        // Lines sharing a timestamp keep the slowest, and before the first line the
        // slower of 1 and the first line's: both are cheap over-estimates.
        private sealed class VelocityLookup
        {
            private readonly List<double> _times = new();
            private readonly List<double> _values = new();

            public VelocityLookup(List<TimingPoint> timingPoints)
            {
                foreach (var p in timingPoints)
                {
                    double v = p.BeatLength < 0 ? Math.Clamp(-100.0 / p.BeatLength, MinVelocity, MaxVelocity) : 1.0;
                    if (_times.Count > 0 && _times[^1] == p.Time)
                        _values[^1] = Math.Min(_values[^1], v);
                    else
                    {
                        _times.Add(p.Time);
                        _values.Add(v);
                    }
                }
            }

            public double At(double time)
            {
                if (_times.Count == 0) return 1.0;
                int i = _times.BinarySearch(time);
                if (i < 0) i = ~i - 1;   // the last line at or before `time`
                return i < 0 ? Math.Min(_values[0], 1.0) : _values[i];
            }
        }
    }
}
