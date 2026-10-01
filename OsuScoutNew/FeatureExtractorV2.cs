using System;
using System.Collections.Generic;
using System.Linq;

namespace OsuScout
{
    // The v2 map feature vector: a line-by-line port of features_v2.py in the
    // training repo, which is the normative source (see docs/feature_v2_spec.md
    // there). v1 (FeatureExtractor.cs) is left untouched so older model files
    // keep working; model_config.json's feature_version says which one to run.
    //
    // Everything is computed in double, as numpy does, and only cast to float for
    // the ONNX input. The parity harness (parity/ParityDump --feature-version 2)
    // compares this against Python's output for the same .osu file.
    public static class FeatureExtractorV2
    {
        // --- CONSTANTS: every one mirrors features_v2.py under the same name ---
        private const double DefaultCircleSize = 4.0;
        private const double DefaultApproachRate = 9.0;
        private const double DefaultOverallDifficulty = 8.0;
        private const double DefaultSliderMultiplier = 1.4;
        private const double FallbackBeatMs = 500.0;
        private const double MinBeatMs = 6.0;          // osu! (lazer) beat-length clamp
        private const double MaxBeatMs = 60000.0;

        private const double PlayfieldWidthPx = 512;
        private const double PlayfieldHeightPx = 384;

        private const int MinObjects = 5;
        private const double BreakMs = 2000;

        private static readonly int[] SnapDivisors = { 2, 3, 4, 6, 8 };
        private const double SnapTolerance = 0.12;
        private const double WholeBeatMinRatio = 0.88;
        private static readonly string[] SnapClasses = { "1_1", "1_2", "1_3", "1_4", "1_6", "1_8", "other" };

        private const double ChainMaxRatio = 0.30;
        private const int BurstMinNotes = 4;
        private const int BurstMaxNotes = 8;
        private const int StreamMinNotes = 9;
        private const int DeathstreamMinNotes = 61;
        private const double SpacedMinRadii = 2.0;
        private const double CutSpacingFactor = 3.0;
        private const double CutMinRadii = 1.0;
        private const double SharpTurnDeg = 90.0;

        private const double JumpMinRatio = 0.40;
        private const double JumpMaxRatio = 1.10;
        private const double JumpMinRadii = 2.0;
        private const double LargeJumpMinPx = 256.0;
        private const double CrossScreenEdgePx = 96.0;
        private const double AngleSharpMaxDeg = 60.0;
        private const double AngleSquareMinDeg = 75.0;
        private const double AngleSquareMaxDeg = 105.0;
        private const double AngleWideMinDeg = 120.0;
        private const double AngleLinearMinDeg = 160.0;
        private const double RealTurnMinDeg = 15.0;
        private const double ReversalMinTurnDeg = 150.0;
        private const double ReversalMinRadii = 1.0;
        private const int BackForthMinReversals = 3;
        private const double VerticalMaxDeg = 20.0;
        private const double ReturnMaxRadii = 1.0;
        private const double SquareSideTolerance = 0.25;
        private const int ClosedShapeMaxSteps = 5;
        private const double MicroMinRadii = 0.5;
        private const double MicroMaxRadii = 2.0;
        private const double MinMoveMs = 10.0;

        private const double SvMin = 0.1;
        private const double SvMax = 10.0;
        private const double SliderSpeedChangeMin = 0.05;
        private const int BurstSliderMinSlides = 3;
        private const double BurstSliderMaxRatio = 0.30;
        private const int BuzzSliderMinSlides = 2;
        private const double BuzzSliderMaxRatio = 0.10;

        private const double OverlapMaxRadii = 0.2;
        private const double OverlapAwayMinRadii = 1.0;
        private const double StackMaxPx = 3.0;

        private const int WindowMs = 4000;
        private const int WindowMinObjects = 2;
        private const double SustainFraction = 0.75;
        // Spread below this fraction of the size is rounding noise, not a trend.
        private const double FlatLoadRelStd = 1e-9;

        // FEATURE_NAMES_V2, in order. The vector is emitted in exactly this order.
        public static readonly string[] FeatureNames =
        {
            "cs", "ar", "od", "log_dominant_bpm", "bpm_count", "log_bpm_range_ratio",
            "notes_per_sec", "slider_ratio", "log_active_minutes",
            "snap_frac_1_1", "snap_frac_1_2", "snap_frac_1_3", "snap_frac_1_4",
            "snap_frac_1_6", "snap_frac_1_8", "snap_frac_other", "snap_change_rate",
            "doubles_per_min", "triples_per_min", "bursts_per_min", "streams_per_min",
            "deathstreams_per_min", "log_longest_chain", "stream_note_frac",
            "burst_spacing_radii", "stream_spacing_radii", "stream_spaced_frac",
            "stream_spacing_cv", "stream_cut_rate", "stream_turn_mean_deg",
            "stream_sharp_turn_frac", "chain_slider_frac",
            "jump_frac", "jump_dist_p50_radii", "jump_dist_p90_radii", "jump_large_frac",
            "jump_cross_screen_frac", "jump_velocity_p50", "jump_velocity_p90",
            "jump_angle_sharp_frac", "jump_angle_square_frac", "jump_angle_wide_frac",
            "jump_angle_linear_frac", "jump_angle_mean_deg", "jump_angle_std_deg",
            "move_reversal_frac", "back_forth_run_frac", "jump_rotation_consistency", "jump_vertical_frac",
            "jump_spacing_change", "jump_square_frac", "jump_closed_shape_frac",
            "micro_move_frac",
            "log_slider_velocity_p50", "log_slider_velocity_p90", "log_slider_length_p50_radii",
            "log_slider_length_p90_radii", "slider_repeat_frac", "burst_sliders_per_min",
            "buzz_sliders_per_min", "jump_to_slider_frac", "jump_from_slider_frac", "slider_speed_change_frac",
            "log_slider_anchor_mean",
            "overlap_frac", "stack_frac",
            "log_aim_spike", "log_speed_spike", "aim_sustain", "speed_sustain", "aim_trend",
            "speed_trend",
        };

        public static int FeatureCount => FeatureNames.Length;

        public static double CircleRadius(double cs) => 54.4 - 4.48 * cs;

        // --- Timing -------------------------------------------------------------

        private static double FallbackBeatLength(double[] times)
        {
            var ioi = new List<double>();
            for (int i = 0; i + 1 < times.Length; i++)
            {
                double d = times[i + 1] - times[i];
                if (d > 30 && d < 1000) ioi.Add(Math.Round(d));
            }
            if (ioi.Count == 0) return FallbackBeatMs;
            // np.unique is sorted; argmax takes the first maximum -> ties go to the smallest value.
            var best = ioi.GroupBy(v => v).Select(g => (value: g.Key, count: g.Count()))
                          .OrderByDescending(g => g.count).ThenBy(g => g.value).First();
            return 2.0 * best.value;
        }

        private sealed class Timing
        {
            private readonly double[] _redTimes, _redBeats, _svTimes, _svValues;

            public Timing(List<TimingPoint> timingPoints, double[] objectTimes)
            {
                var points = (timingPoints ?? new List<TimingPoint>()).OrderBy(p => p.Time).ToList();
                var red = points.Where(p => p.Uninherited && p.BeatLength > 0)
                                .Select(p => (time: p.Time, beat: Math.Min(Math.Max(p.BeatLength, MinBeatMs), MaxBeatMs)))
                                .ToList();
                if (red.Count == 0) red.Add((0.0, FallbackBeatLength(objectTimes)));
                _redTimes = red.Select(r => r.time).ToArray();
                _redBeats = red.Select(r => r.beat).ToArray();

                // A red point resets slider velocity to 1; a green point sets it to
                // -100 / value. The state after each point, in time order.
                var svTimes = new List<double>();
                var svValues = new List<double>();
                double sv = 1.0;
                foreach (var p in points)
                {
                    if (p.Uninherited && p.BeatLength > 0) sv = 1.0;
                    else if (!p.Uninherited && p.BeatLength < 0) sv = Math.Min(Math.Max(-100.0 / p.BeatLength, SvMin), SvMax);
                    else continue;
                    svTimes.Add(p.Time);
                    svValues.Add(sv);
                }
                _svTimes = svTimes.ToArray();
                _svValues = svValues.ToArray();
            }

            // numpy.searchsorted(times, at, side='right') - 1, clipped at 0.
            private static double Lookup(double[] times, double at, double[] values, double dflt)
            {
                if (times.Length == 0) return dflt;
                int lo = 0, hi = times.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (times[mid] <= at) lo = mid + 1; else hi = mid;
                }
                return values[Math.Max(lo - 1, 0)];
            }

            public double BeatLength(double at) => Lookup(_redTimes, at, _redBeats, FallbackBeatMs);
            public double SliderVelocity(double at) => Lookup(_svTimes, at, _svValues, 1.0);
        }

        // --- Slider geometry ------------------------------------------------------

        // The point `distance` along a polyline, extended straight past its last
        // segment if the polyline is shorter. The documented approximation for
        // every non-arc slider: it has to agree with Python, not with osu!.
        private static (double x, double y) PolylinePoint(List<(double x, double y)> points, double distance)
        {
            var starts = new List<(double x, double y)>();
            var segs = new List<(double x, double y)>();
            var lengths = new List<double>();
            for (int i = 0; i + 1 < points.Count; i++)
            {
                double sx = points[i + 1].x - points[i].x, sy = points[i + 1].y - points[i].y;
                double len = Math.Sqrt(sx * sx + sy * sy);
                if (len > 0) { starts.Add(points[i]); segs.Add((sx, sy)); lengths.Add(len); }
            }
            if (lengths.Count == 0) return points[0];
            for (int i = 0; i < lengths.Count; i++)
            {
                if (distance <= lengths[i])
                    return (starts[i].x + segs[i].x * (distance / lengths[i]), starts[i].y + segs[i].y * (distance / lengths[i]));
                distance -= lengths[i];
            }
            int last = lengths.Count - 1;
            return (starts[last].x + segs[last].x + segs[last].x / lengths[last] * distance,
                    starts[last].y + segs[last].y + segs[last].y / lengths[last] * distance);
        }

        // The point `distance` along the circular arc p0 -> p1 -> p2, or null if collinear.
        private static (double x, double y)? ArcPoint((double x, double y) p0, (double x, double y) p1,
                                                     (double x, double y) p2, double distance)
        {
            double ax = p1.x - p0.x, ay = p1.y - p0.y;
            double bx = p2.x - p1.x, by = p2.y - p1.y;
            double cross = ax * by - ay * bx;
            if (Math.Abs(cross) < 1e-6) return null;
            double d = 2 * (p0.x * (p1.y - p2.y) + p1.x * (p2.y - p0.y) + p2.x * (p0.y - p1.y));
            double s0 = p0.x * p0.x + p0.y * p0.y, s1 = p1.x * p1.x + p1.y * p1.y, s2 = p2.x * p2.x + p2.y * p2.y;
            double cx = (s0 * (p1.y - p2.y) + s1 * (p2.y - p0.y) + s2 * (p0.y - p1.y)) / d;
            double cy = (s0 * (p2.x - p1.x) + s1 * (p0.x - p2.x) + s2 * (p1.x - p0.x)) / d;
            double rx = p0.x - cx, ry = p0.y - cy;
            double radius = Math.Sqrt(rx * rx + ry * ry);
            double theta = Math.Atan2(p0.y - cy, p0.x - cx);
            theta += Math.CopySign(distance / radius, cross);
            return (cx + radius * Math.Cos(theta), cy + radius * Math.Sin(theta));
        }

        public static (double x, double y) SliderPathEnd(double hx, double hy, string curveType,
                                                         List<int[]> curvePoints, double length)
        {
            var points = new List<(double x, double y)> { (hx, hy) };
            points.AddRange(curvePoints.Select(p => ((double)p[0], (double)p[1])));
            if (curveType == "P" && points.Count == 3)
            {
                var end = ArcPoint(points[0], points[1], points[2], length);
                if (end.HasValue) return end.Value;
            }
            return PolylinePoint(points, length);
        }

        // --- numpy-equivalent helpers ---------------------------------------------

        private static int[] ClassifySnaps(double[] ratio)
        {
            int other = Array.IndexOf(SnapClasses, "other"), whole = Array.IndexOf(SnapClasses, "1_1"),
                eighth = Array.IndexOf(SnapClasses, "1_8");
            var result = new int[ratio.Length];
            for (int i = 0; i < ratio.Length; i++)
            {
                double r = ratio[i];
                int c = other;
                if (r >= WholeBeatMinRatio) c = whole;
                foreach (int d in SnapDivisors)
                {
                    double ideal = 1.0 / d;
                    if (Math.Abs(r - ideal) <= SnapTolerance * ideal) c = Array.IndexOf(SnapClasses, "1_" + d);
                }
                if (r < (1.0 / 8) * (1 - SnapTolerance)) c = eighth;
                result[i] = c;
            }
            return result;
        }

        // (start, stop) of each run of true values.
        private static List<(int start, int stop)> Runs(bool[] mask)
        {
            var runs = new List<(int, int)>();
            int i = 0;
            while (i < mask.Length)
            {
                if (!mask[i]) { i++; continue; }
                int s = i;
                while (i < mask.Length && mask[i]) i++;
                runs.Add((s, i));
            }
            return runs;
        }

        // Angle between two vectors in degrees (0 = same direction), as _turn_deg.
        private static double TurnDeg((double x, double y) a, (double x, double y) b)
        {
            double na = Math.Sqrt(a.x * a.x + a.y * a.y), nb = Math.Sqrt(b.x * b.x + b.y * b.y);
            double denom = na * nb > 0 ? na * nb : 1.0;
            double cos = Math.Clamp((a.x * b.x + a.y * b.y) / denom, -1.0, 1.0);
            return Math.Acos(cos) * (180.0 / Math.PI);
        }

        private static double Cross((double x, double y) a, (double x, double y) b) => a.x * b.y - a.y * b.x;

        // numpy.percentile, method='linear', including numpy's two-sided lerp.
        private static double Percentile(IEnumerable<double> values, double percent)
        {
            var s = values.OrderBy(v => v).ToArray();
            int n = s.Length;
            if (n == 0) return 0.0;
            double q = percent / 100.0;
            double virtualIndex = n * q + (1.0 + q * (1.0 - 1.0 - 1.0)) - 1.0;
            double prev = Math.Floor(virtualIndex);
            int lo = (int)Math.Clamp(prev, 0, n - 1);
            int hi = Math.Clamp(lo + 1, 0, n - 1);
            double gamma = virtualIndex - prev;
            double a = s[lo], b = s[hi], diff = b - a;
            return gamma >= 0.5 ? b - diff * (1 - gamma) : a + diff * gamma;
        }

        private static double Median(IList<double> values)
        {
            var s = values.OrderBy(v => v).ToArray();
            int n = s.Length;
            if (n == 0) return 0.0;
            return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
        }

        private static double Mean(IList<double> values) => values.Count > 0 ? values.Sum() / values.Count : 0.0;

        private static double PopStd(IList<double> values)
        {
            if (values.Count == 0) return 0.0;
            double m = values.Sum() / values.Count;
            double ss = 0;
            foreach (var v in values) ss += (v - m) * (v - m);
            return Math.Sqrt(ss / values.Count);
        }

        private static double Frac(IList<bool> mask) => mask.Count > 0 ? (double)mask.Count(b => b) / mask.Count : 0.0;

        private static double Ratio(double num, double den) => den != 0 ? num / den : 0.0;

        private static (double spike, double sustain, double trend) LoadProfile(List<double> loads, List<int> order)
        {
            if (loads.Count == 0) return (0, 0, 0);
            double p95 = Percentile(loads, 95);
            double median = Median(loads);
            double spike = median > 0 ? p95 / median : 0.0;
            double sustain = p95 > 0 ? (double)loads.Count(l => l >= SustainFraction * p95) / loads.Count : 0.0;
            double trend = 0.0;
            if (loads.Count >= 3 && PopStd(loads) > FlatLoadRelStd * loads.Average(Math.Abs))
            {
                double mx = order.Average(), my = loads.Average();
                double sxy = 0, sxx = 0, syy = 0;
                for (int i = 0; i < loads.Count; i++)
                {
                    double dx = order[i] - mx, dy = loads[i] - my;
                    sxy += dx * dy; sxx += dx * dx; syy += dy * dy;
                }
                trend = Math.Clamp(sxy / Math.Sqrt(sxx * syy), -1.0, 1.0);
            }
            return (spike, sustain, trend);
        }

        // --- The feature vector -------------------------------------------------------

        public static float[] Extract(List<RawHitObject> hitObjects, Dictionary<string, double> difficulty,
                                      List<TimingPoint> timingPoints)
            => ExtractDouble(hitObjects, difficulty, timingPoints).Select(v => (float)v).ToArray();

        public static double[] ExtractDouble(List<RawHitObject> hitObjects, Dictionary<string, double> difficulty,
                                             List<TimingPoint> timingPoints)
        {
            var zeros = new double[FeatureCount];
            difficulty ??= new Dictionary<string, double>();

            // Spinners are not aim or tapping.
            var objs = hitObjects.Where(o => (o.TypeBit & 8) == 0).ToList();
            int n = objs.Count;
            if (n < MinObjects) return zeros;

            double cs = difficulty.GetValueOrDefault("CircleSize", DefaultCircleSize);
            double ar = difficulty.GetValueOrDefault("ApproachRate", DefaultApproachRate);
            double od = difficulty.GetValueOrDefault("OverallDifficulty", DefaultOverallDifficulty);
            double sliderMultiplier = difficulty.GetValueOrDefault("SliderMultiplier", DefaultSliderMultiplier);
            double radius = CircleRadius(cs);

            double[] t = objs.Select(o => (double)o.Time).ToArray();
            var head = objs.Select(o => ((double)o.X, (double)o.Y)).ToArray();
            var timing = new Timing(timingPoints, t);
            double[] beat = t.Select(timing.BeatLength).ToArray();
            double[] sliderPxPerMs = new double[n];
            for (int i = 0; i < n; i++)
                sliderPxPerMs[i] = sliderMultiplier * 100.0 * timing.SliderVelocity(t[i]) / beat[i];

            // --- where and when each object ends ---
            var isSlider = new bool[n];
            var slides = new double[n];
            var length = new double[n];
            var anchors = new double[n];
            var spanMs = new double[n];
            var end = ((double x, double y)[])head.Clone();
            var endT = (double[])t.Clone();
            for (int i = 0; i < n; i++)
            {
                slides[i] = 1;
                var o = objs[i];
                if ((o.TypeBit & 2) == 0 || o.CurveType == null || o.LengthExact <= 0) continue;
                isSlider[i] = true;
                slides[i] = Math.Max(o.Slides, 1);
                length[i] = o.LengthExact;
                anchors[i] = o.CurvePoints.Count;
                spanMs[i] = length[i] / sliderPxPerMs[i];
                endT[i] = t[i] + spanMs[i] * slides[i];
                // An even number of slides ends back at the head.
                if ((int)slides[i] % 2 == 1)
                    end[i] = SliderPathEnd(head[i].Item1, head[i].Item2, o.CurveType, o.CurvePoints, length[i]);
            }

            // --- transitions between consecutive objects ---
            int m = n - 1;
            var ioi = new double[m];
            var active = new bool[m];
            var move = new (double x, double y)[m];
            var movePx = new double[m];
            var moveR = new double[m];
            var velocity = new double[m];
            var ratio = new double[m];
            double activeMs = 0;
            int nActive = 0;
            for (int i = 0; i < m; i++)
            {
                ioi[i] = t[i + 1] - t[i];
                active[i] = ioi[i] <= BreakMs;
                if (active[i]) { activeMs += ioi[i]; nActive++; }
                move[i] = (head[i + 1].Item1 - end[i].x, head[i + 1].Item2 - end[i].y);
                movePx[i] = Math.Sqrt(move[i].x * move[i].x + move[i].y * move[i].y);
                moveR[i] = movePx[i] / radius;
                double moveMs = Math.Max(t[i + 1] - endT[i], MinMoveMs);
                velocity[i] = movePx[i] / moveMs;
                ratio[i] = ioi[i] / beat[i];
            }
            if (activeMs <= 0) return zeros;
            double minutes = activeMs / 60000.0;

            var f = new Dictionary<string, double>();

            // --- tempo and settings ---
            double[] bpm = beat.Select(b => Math.Round(60000.0 / b)).ToArray();   // half-to-even, like np.round
            var bpmCounts = bpm.GroupBy(v => v).Select(g => (value: g.Key, count: g.Count())).ToList();
            f["cs"] = cs; f["ar"] = ar; f["od"] = od;
            f["log_dominant_bpm"] = Math.Log(bpmCounts.OrderByDescending(g => g.count).ThenBy(g => g.value).First().value);
            f["bpm_count"] = bpmCounts.Count;
            f["log_bpm_range_ratio"] = Math.Log(bpm.Max() / bpm.Min());

            // --- density ---
            f["notes_per_sec"] = n / (activeMs / 1000.0);
            f["slider_ratio"] = (double)isSlider.Count(s => s) / n;
            f["log_active_minutes"] = Math.Log(1.0 + minutes);

            // --- rhythm ---
            int[] snap = ClassifySnaps(ratio);
            var snapActive = Enumerable.Range(0, m).Where(i => active[i]).Select(i => snap[i]).ToList();
            for (int k = 0; k < SnapClasses.Length; k++)
                f["snap_frac_" + SnapClasses[k]] = Frac(snapActive.Select(s => s == k).ToList());
            var changes = new List<bool>();
            for (int i = 0; i + 1 < m; i++)
                if (active[i] && active[i + 1]) changes.Add(snap[i] != snap[i + 1]);
            f["snap_change_rate"] = Frac(changes);

            // --- chains: runs of 1/4-or-faster onsets ---
            var chainStep = Enumerable.Range(0, m).Select(i => active[i] && ratio[i] <= ChainMaxRatio).ToArray();
            var chains = Runs(chainStep).Select(r => (s: r.start, e: r.stop, notes: r.stop - r.start + 1)).ToList();
            f["doubles_per_min"] = chains.Count(c => c.notes == 2) / minutes;
            f["triples_per_min"] = chains.Count(c => c.notes == 3) / minutes;
            f["bursts_per_min"] = chains.Count(c => c.notes >= BurstMinNotes && c.notes <= BurstMaxNotes) / minutes;
            f["streams_per_min"] = chains.Count(c => c.notes >= StreamMinNotes && c.notes < DeathstreamMinNotes) / minutes;
            f["deathstreams_per_min"] = chains.Count(c => c.notes >= DeathstreamMinNotes) / minutes;
            f["log_longest_chain"] = chains.Count > 0 ? Math.Log(1.0 + chains.Max(c => c.notes)) : 0.0;
            f["stream_note_frac"] = (double)chains.Where(c => c.notes >= StreamMinNotes).Sum(c => c.notes) / n;

            // --- stream shape ---
            var burstSteps = chains.Where(c => c.notes >= 3 && c.notes <= BurstMaxNotes)
                                   .SelectMany(c => Enumerable.Range(c.s, c.e - c.s).Select(i => moveR[i])).ToList();
            f["burst_spacing_radii"] = Mean(burstSteps);
            var streamChains = chains.Where(c => c.notes >= StreamMinNotes).ToList();
            if (streamChains.Count > 0)
            {
                var steps = new List<double>();
                var cvs = new List<double>();
                var weights = new List<double>();
                var turns = new List<double>();
                int cuts = 0;
                foreach (var c in streamChains)
                {
                    var chain = Enumerable.Range(c.s, c.e - c.s).Select(i => moveR[i]).ToList();
                    steps.AddRange(chain);
                    double mean = chain.Average();
                    cvs.Add(mean > 0 ? PopStd(chain) / mean : 0.0);
                    weights.Add(chain.Count);
                    double median = Median(chain);
                    cuts += chain.Count(v => v > CutSpacingFactor * median && v > CutMinRadii);
                    for (int i = c.s; i + 1 < c.e; i++)
                    {
                        bool moving1 = Norm(move[i]) > 0, moving2 = Norm(move[i + 1]) > 0;
                        if (moving1 && moving2) turns.Add(TurnDeg(move[i], move[i + 1]));
                    }
                }
                f["stream_spacing_radii"] = steps.Average();
                f["stream_spaced_frac"] = Frac(steps.Select(v => v >= SpacedMinRadii).ToList());
                f["stream_spacing_cv"] = cvs.Zip(weights, (cv, w) => cv * w).Sum() / weights.Sum();
                f["stream_cut_rate"] = (double)cuts / steps.Count;
                f["stream_turn_mean_deg"] = Mean(turns);
                f["stream_sharp_turn_frac"] = Frac(turns.Select(v => v > SharpTurnDeg).ToList());
            }
            else
            {
                foreach (var name in new[] { "stream_spacing_radii", "stream_spaced_frac", "stream_spacing_cv",
                                             "stream_cut_rate", "stream_turn_mean_deg", "stream_sharp_turn_frac" })
                    f[name] = 0.0;
            }
            var chainNotes = new bool[n];
            foreach (var c in chains)
                for (int i = c.s; i <= c.e; i++) chainNotes[i] = true;
            f["chain_slider_frac"] = Frac(Enumerable.Range(0, n).Where(i => chainNotes[i]).Select(i => isSlider[i]).ToList());

            // --- jumps ---
            var jump = Enumerable.Range(0, m).Select(i => active[i] && ratio[i] >= JumpMinRatio
                                                          && ratio[i] <= JumpMaxRatio && moveR[i] >= JumpMinRadii).ToArray();
            var jumpIdx = Enumerable.Range(0, m).Where(i => jump[i]).ToList();
            f["jump_frac"] = Ratio(jumpIdx.Count, nActive);
            f["jump_dist_p50_radii"] = Percentile(jumpIdx.Select(i => moveR[i]), 50);
            f["jump_dist_p90_radii"] = Percentile(jumpIdx.Select(i => moveR[i]), 90);
            f["jump_large_frac"] = Frac(jumpIdx.Select(i => movePx[i] > LargeJumpMinPx).ToList());
            double lo = CrossScreenEdgePx, hiX = PlayfieldWidthPx - CrossScreenEdgePx, hiY = PlayfieldHeightPx - CrossScreenEdgePx;
            f["jump_cross_screen_frac"] = Frac(jumpIdx.Select(i =>
            {
                var a = end[i];
                var b = head[i + 1];
                return (a.x < lo && b.Item1 > hiX) || (b.Item1 < lo && a.x > hiX)
                    || (a.y < lo && b.Item2 > hiY) || (b.Item2 < lo && a.y > hiY);
            }).ToList());
            f["jump_velocity_p50"] = Percentile(jumpIdx.Select(i => velocity[i]), 50);
            f["jump_velocity_p90"] = Percentile(jumpIdx.Select(i => velocity[i]), 90);

            // Pairs of consecutive jumps: the angle at the note between them.
            var pair = Enumerable.Range(0, Math.Max(m - 1, 0)).Where(k => jump[k] && jump[k + 1]).ToList();
            var turn = pair.Select(k => TurnDeg(move[k], move[k + 1])).ToList();
            var angle = turn.Select(v => 180.0 - v).ToList();
            f["jump_angle_sharp_frac"] = Frac(angle.Select(a => a < AngleSharpMaxDeg).ToList());
            f["jump_angle_square_frac"] = Frac(angle.Select(a => a >= AngleSquareMinDeg && a <= AngleSquareMaxDeg).ToList());
            f["jump_angle_wide_frac"] = Frac(angle.Select(a => a > AngleWideMinDeg).ToList());
            f["jump_angle_linear_frac"] = Frac(angle.Select(a => a > AngleLinearMinDeg).ToList());
            f["jump_angle_mean_deg"] = Mean(angle);
            f["jump_angle_std_deg"] = PopStd(angle);

            // echosu 1-2: 'back and forth jumps' - direction reversals at any rhythm.
            var moving = Enumerable.Range(0, m).Select(i => active[i] && moveR[i] >= ReversalMinRadii).ToArray();
            var reversal = new bool[m];
            var revFlags = new List<bool>();
            for (int k = 0; k + 1 < m; k++)
            {
                if (!(moving[k] && moving[k + 1])) continue;
                reversal[k] = TurnDeg(move[k], move[k + 1]) > ReversalMinTurnDeg;
                revFlags.Add(reversal[k]);
            }
            f["move_reversal_frac"] = Frac(revFlags);
            f["back_forth_run_frac"] = Ratio(Runs(reversal).Count(r => r.stop - r.start >= BackForthMinReversals), nActive);

            // Flow keeps turning the same way; snap zig-zags. Neutral 0.5 with nothing to compare.
            var real = turn.Select(v => v > RealTurnMinDeg && v < 180.0 - RealTurnMinDeg).ToList();
            var side = pair.Select(k => Math.Sign(Cross(move[k], move[k + 1]))).ToList();
            var same = new List<bool>();
            for (int i = 0; i + 1 < pair.Count; i++)
                if (pair[i + 1] == pair[i] + 1 && real[i + 1] && real[i]) same.Add(side[i + 1] == side[i]);
            f["jump_rotation_consistency"] = same.Count > 0 ? Frac(same) : 0.5;

            double tanVertical = Math.Tan(VerticalMaxDeg * (Math.PI / 180.0));
            f["jump_vertical_frac"] = Frac(jumpIdx.Select(i => Math.Abs(move[i].x) <= tanVertical * Math.Abs(move[i].y)).ToList());
            f["jump_spacing_change"] = Mean(pair.Select(k =>
            {
                double d1 = moveR[k], d2 = moveR[k + 1];
                return Math.Abs(d2 - d1) / ((d1 + d2) / 2);
            }).ToList());

            // Three jumps in a row: squares and closed shapes.
            var triple = Enumerable.Range(0, Math.Max(m - 2, 0)).Where(k => jump[k] && jump[k + 1] && jump[k + 2]).ToList();
            if (triple.Count > 0)
            {
                var square = new List<bool>();
                var closed = new List<bool>();
                foreach (int k in triple)
                {
                    double ang1 = 180.0 - TurnDeg(move[k], move[k + 1]);
                    double ang2 = 180.0 - TurnDeg(move[k + 1], move[k + 2]);
                    bool squareAngles = ang1 >= AngleSquareMinDeg && ang1 <= AngleSquareMaxDeg
                                        && ang2 >= AngleSquareMinDeg && ang2 <= AngleSquareMaxDeg;
                    bool sameWay = Math.Sign(Cross(move[k], move[k + 1])) == Math.Sign(Cross(move[k + 1], move[k + 2]));
                    double s1 = moveR[k], s2 = moveR[k + 1], s3 = moveR[k + 2];
                    bool even = Math.Max(s1, Math.Max(s2, s3)) <= (1 + SquareSideTolerance) * Math.Min(s1, Math.Min(s2, s3));
                    square.Add(squareAngles && sameWay && even);

                    bool returnsEarly = Dist(head[k + 2], head[k]) / radius < ReturnMaxRadii;
                    bool closes = false;
                    for (int stepsBack = 3; stepsBack <= ClosedShapeMaxSteps; stepsBack++)
                        if (k + stepsBack < n && Dist(head[k + stepsBack], head[k]) / radius < ReturnMaxRadii) closes = true;
                    closed.Add(closes && !returnsEarly);
                }
                f["jump_square_frac"] = Frac(square);
                f["jump_closed_shape_frac"] = Frac(closed);
            }
            else
            {
                f["jump_square_frac"] = 0.0;
                f["jump_closed_shape_frac"] = 0.0;
            }

            int micro = Enumerable.Range(0, m).Count(i => active[i] && ratio[i] >= JumpMinRatio
                                                          && moveR[i] >= MicroMinRadii && moveR[i] < MicroMaxRadii);
            f["micro_move_frac"] = Ratio(micro, nActive);

            // --- sliders ---
            var sl = Enumerable.Range(0, n).Where(i => isSlider[i]).ToList();
            if (sl.Count > 0)
            {
                var pxPerMs = sl.Select(i => sliderPxPerMs[i]).ToList();
                f["log_slider_velocity_p50"] = Math.Log(1.0 + Percentile(pxPerMs, 50));
                f["log_slider_velocity_p90"] = Math.Log(1.0 + Percentile(pxPerMs, 90));
                f["log_slider_length_p50_radii"] = Math.Log(1.0 + Percentile(sl.Select(i => length[i] / radius), 50));
                f["log_slider_length_p90_radii"] = Math.Log(1.0 + Percentile(sl.Select(i => length[i] / radius), 90));
                f["slider_repeat_frac"] = Frac(sl.Select(i => slides[i] >= 2).ToList());
                f["burst_sliders_per_min"] = sl.Count(i => slides[i] >= BurstSliderMinSlides
                                                           && spanMs[i] / beat[i] <= BurstSliderMaxRatio) / minutes;
                f["buzz_sliders_per_min"] = sl.Count(i => slides[i] >= BuzzSliderMinSlides
                                                          && spanMs[i] / beat[i] <= BuzzSliderMaxRatio) / minutes;
                var speedChange = new List<bool>();
                for (int i = 0; i + 1 < pxPerMs.Count; i++)
                    speedChange.Add(Math.Abs(pxPerMs[i + 1] - pxPerMs[i]) / Math.Max(pxPerMs[i], 1e-9) > SliderSpeedChangeMin);
                f["slider_speed_change_frac"] = Frac(speedChange);
                f["log_slider_anchor_mean"] = Math.Log(1.0 + sl.Average(i => anchors[i]));
            }
            else
            {
                foreach (var name in new[] { "log_slider_velocity_p50", "log_slider_velocity_p90", "log_slider_length_p50_radii",
                                             "log_slider_length_p90_radii", "slider_repeat_frac", "burst_sliders_per_min",
                                             "buzz_sliders_per_min", "slider_speed_change_frac", "log_slider_anchor_mean" })
                    f[name] = 0.0;
            }
            f["jump_to_slider_frac"] = Ratio(jumpIdx.Count(i => isSlider[i + 1]), nActive);
            f["jump_from_slider_frac"] = Ratio(jumpIdx.Count(i => isSlider[i]), nActive);

            // --- overlaps ---
            if (n >= 3)
            {
                int overlaps = 0;
                for (int k = 2; k < n; k++)
                {
                    double back2 = Dist(head[k], head[k - 2]) / radius;
                    double away = Dist(head[k - 1], head[k - 2]) / radius;
                    if (back2 < OverlapMaxRadii && away >= OverlapAwayMinRadii) overlaps++;
                }
                f["overlap_frac"] = (double)overlaps / n;
            }
            else f["overlap_frac"] = 0.0;
            f["stack_frac"] = Ratio(Enumerable.Range(0, m).Count(i => active[i] && Dist(head[i + 1], head[i]) < StackMaxPx), nActive);

            // --- difficulty over time: fixed windows, breaks skipped ---
            int[] window = t.Select(v => (int)Math.Floor((v - t[0]) / WindowMs)).ToArray();
            var aimLoads = new List<double>();
            var speedLoads = new List<double>();
            var order = new List<int>();
            foreach (int w in window.Distinct().OrderBy(w => w))
            {
                int count = window.Count(x => x == w);
                if (count < WindowMinObjects) continue;
                var inW = Enumerable.Range(0, m).Where(i => window[i] == w && active[i]).Select(i => velocity[i]).ToList();
                speedLoads.Add(count / (WindowMs / 1000.0));
                aimLoads.Add(Mean(inW));
                order.Add(w);
            }
            var (aimSpike, aimSustain, aimTrend) = LoadProfile(aimLoads, order);
            var (speedSpike, speedSustain, speedTrend) = LoadProfile(speedLoads, order);
            f["log_aim_spike"] = Math.Log(1.0 + aimSpike);
            f["log_speed_spike"] = Math.Log(1.0 + speedSpike);
            f["aim_sustain"] = aimSustain;
            f["speed_sustain"] = speedSustain;
            f["aim_trend"] = aimTrend;
            f["speed_trend"] = speedTrend;

            if (f.Count != FeatureCount || FeatureNames.Any(nm => !f.ContainsKey(nm)))
                throw new InvalidOperationException("v2 features out of step with FeatureNames");
            return FeatureNames.Select(nm => f[nm]).ToArray();
        }

        private static double Norm((double x, double y) v) => Math.Sqrt(v.x * v.x + v.y * v.y);

        private static double Dist((double x, double y) a, (double x, double y) b)
        {
            double dx = a.x - b.x, dy = a.y - b.y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
