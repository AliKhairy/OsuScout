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
        // spans are left uncapped, and a slider with no length gets a path length that
        // is never shorter than the one rosu computes (see UnspecifiedPathLength).
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
                if (!(length > 0)) length = UnspecifiedPathLength(parts);

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

        // A slider without a length follows its control points all the way. A bezier,
        // linear or catmull path stays within twice its control polygon. A perfect-circle
        // arc does not: when its middle point lies beyond the other two, it runs most of
        // the way round a huge circle, so a polygon of 300 px can be a 60,000 px arc
        // (measured: a 20-span one estimated 24,000 ticks made rosu use 157 MB). Its
        // length is computed the way osu!'s path code does, instead of bounded.
        private static double UnspecifiedPathLength(string[] parts)
        {
            var points = ControlPoints(parts);
            double polygon = 0;
            for (int i = 1; i < points.Count; i++) polygon += Distance(points[i - 1], points[i]);

            if (parts[5].StartsWith("P") && points.Count == 3
                && PerfectArcLength(points[0], points[1], points[2]) is double arc)
                return Math.Max(arc, 2 * polygon);
            return 2 * polygon;
        }

        // The head, then every control point in "B|x:y|x:y".
        private static List<(double x, double y)> ControlPoints(string[] parts)
        {
            var points = new List<(double x, double y)>();
            if (!TryParse(parts[0], out double x) || !TryParse(parts[1], out double y)) return points;
            points.Add((x, y));
            foreach (string point in parts[5].Split('|').Skip(1))
            {
                string[] xy = point.Split(':');
                if (xy.Length == 2 && TryParse(xy[0], out double nx) && TryParse(xy[1], out double ny))
                    points.Add((nx, ny));
            }
            return points;
        }

        // Length of the circular arc from a through b to c, as osu-framework's
        // CircularArcProperties draws it; null when the points are (nearly) collinear,
        // where osu! falls back to a bezier and the polygon bound holds.
        private static double? PerfectArcLength((double x, double y) a, (double x, double y) b, (double x, double y) c)
        {
            double det = (b.y - a.y) * (c.x - a.x) - (b.x - a.x) * (c.y - a.y);
            if (Math.Abs(det) < 1e-3) return null;

            double d = 2 * (a.x * (b.y - c.y) + b.x * (c.y - a.y) + c.x * (a.y - b.y));
            double aSq = a.x * a.x + a.y * a.y, bSq = b.x * b.x + b.y * b.y, cSq = c.x * c.x + c.y * c.y;
            double cx = (aSq * (b.y - c.y) + bSq * (c.y - a.y) + cSq * (a.y - b.y)) / d;
            double cy = (aSq * (c.x - b.x) + bSq * (a.x - c.x) + cSq * (b.x - a.x)) / d;
            double radius = Distance(a, (cx, cy));

            double thetaStart = Math.Atan2(a.y - cy, a.x - cx);
            double thetaEnd = Math.Atan2(c.y - cy, c.x - cx);
            while (thetaEnd < thetaStart) thetaEnd += 2 * Math.PI;
            double range = thetaEnd - thetaStart;
            // The arc goes the other way round when b is on the other side of a->c.
            if ((c.y - a.y) * (b.x - a.x) - (c.x - a.x) * (b.y - a.y) < 0) range = 2 * Math.PI - range;
            return radius * range;
        }

        private static double Distance((double x, double y) p, (double x, double y) q)
            => Math.Sqrt((q.x - p.x) * (q.x - p.x) + (q.y - p.y) * (q.y - p.y));

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
