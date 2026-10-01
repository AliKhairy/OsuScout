using System;
using System.Collections.Generic;
using System.Linq;

namespace OsuScout
{
    public static class FeatureExtractor
    {
        // --- TUNING CONSTANTS ---
        //
        // Every one of these is duplicated in osu_tagger/features/v1.py in the training repo,
        // under the SAME NAME. The model is trained on Python's numbers and runs on
        // these, so a value that differs between the two silently corrupts every
        // prediction - no crash, no error. Change one side, change the other, then
        // run the parity harness (see parity/README.md) before shipping.

        // Section splitting: a 2s pause reliably marks a gameplay break.
        private const int BreakThresholdMs = 2000;
        private const int MinSectionLength = 15;
        private const int MinObjectsForFeatures = 5;
        private const int SectionFeatureCount = 29;

        // Stream/burst detection. The distance cap stops high-BPM cross-screen jumps
        // from being counted as streams.
        private const float StreamGapMs = 165f;
        private const float StreamMaxSpacingPx = 120f;
        private const int BurstMinLength = 3;
        private const int BurstMaxLength = 7;
        private const int StreamMinLength = 8;

        // Buzz sliders: many repeats packed into a short pixel length.
        private const int BuzzSliderMinSlides = 4;
        private const float BuzzSliderMaxLengthPx = 100f;

        // Global rhythm: ignore break-length gaps; a >15ms shift is a deliberate
        // snap change (1/2 vs 1/3 vs 1/4).
        private const float ActiveGapMaxMs = 750f;
        private const float RhythmChangeMinDeltaMs = 15f;

        // A slider wedged between two fast gaps disrupts tapping.
        private const float SliderDisruptionGapMs = 160f;

        // Hand-tuned weights for the composite finger-control score.
        private const float FingerControlRhythmWeight = 1.2f;
        private const float FingerControlSpacingWeight = 0.3f;
        private const float FingerControlSliderWeight = 3.0f;

        // Angle buckets, in radians (pi = 180 degrees). Note these do not tile the
        // full range - angles falling between buckets are counted in none of them.
        private const float AngleSharpMax = 1.04f;    // < ~60 deg  (snap / awkward)
        private const float AngleSquareMin = 1.3f;    // ~90 deg    (square jumps)
        private const float AngleSquareMax = 1.8f;
        private const float AngleWideMin = 2.09f;     // > ~120 deg (flow aim)
        private const float AngleWideMax = 2.6f;
        private const float AngleLinearMin = 2.7f;    // ~180 deg   (linear / 1-2)

        // Vertical jump: large Y movement with almost no X movement.
        private const float VerticalJumpMinDyPx = 120f;
        private const float VerticalJumpMaxDxPx = 40f;

        // An object landing on top of where one sat two steps ago.
        private const float PerfectOverlapMaxPx = 10f;

        // Collinearity over 4-object chunks.
        private const double LinearChunkMinLengthPx = 50.0;
        private const double LinearMaxDeviationPx = 15.0;

        // Map-level hybrid flags, applied to the max-pooled vector.
        private const float PeakStreamMinLength = 12f;
        private const float PeakJumpMinP95Px = 180f;

        // Index of the aggregate features the hybrid flags read.
        private const int IdxMaxContinuousStream = 2;
        private const int IdxPercentile95Distance = 16;

        // 1. The Section Splitter
        public static List<List<RawHitObject>> SplitIntoSections(List<RawHitObject> hitObjects)
        {
            var sections = new List<List<RawHitObject>>();
            if (hitObjects == null || hitObjects.Count == 0) return sections;

            var currentSection = new List<RawHitObject>();
            int breakThreshold = BreakThresholdMs;
            int minSectionLength = MinSectionLength;

            for (int i = 0; i < hitObjects.Count; i++)
            {
                if (i > 0 && (hitObjects[i].Time - hitObjects[i - 1].Time > breakThreshold))
                {
                    if (currentSection.Count >= minSectionLength)
                        sections.Add(new List<RawHitObject>(currentSection));
                    currentSection.Clear();
                }
                currentSection.Add(hitObjects[i]);
            }

            if (currentSection.Count >= minSectionLength)
                sections.Add(currentSection);

            if (sections.Count == 0 && hitObjects.Count >= minSectionLength)
                sections.Add(hitObjects);

            return sections;
        }

        // 2. The Core Feature Extraction (29 Features)
        public static float[] ExtractSectionFeatures(List<RawHitObject> objects)
        {
            int featureCount = SectionFeatureCount;
            if (objects == null || objects.Count < MinObjectsForFeatures) return new float[featureCount];

            int numObjects = objects.Count;
            float totalDuration = (objects.Last().Time - objects.First().Time) / 1000f;
            if (totalDuration <= 0) return new float[featureCount];

            float objectsPerSec = numObjects / totalDuration;

            // Arrays for NumPy-like vectorized operations
            float[] timeGaps = new float[numObjects - 1];
            float[] distances = new float[numObjects - 1];
            bool[] isSlider = new bool[numObjects];

            for (int i = 0; i < numObjects; i++)
                isSlider[i] = !string.IsNullOrEmpty(objects[i].CurveType);

            for (int i = 0; i < numObjects - 1; i++)
            {
                float gap = objects[i + 1].Time - objects[i].Time;
                timeGaps[i] = gap == 0 ? 1 : gap; // Avoid division by zero

                float dx = objects[i + 1].X - objects[i].X;
                float dy = objects[i + 1].Y - objects[i].Y;
                distances[i] = (float)Math.Sqrt(dx * dx + dy * dy);
            }

            // Sequence Tracking
            List<int> sequenceLengths = new List<int>();
            int currentLen = 0;
            for (int i = 0; i < timeGaps.Length; i++)
            {
                if (timeGaps[i] < StreamGapMs && distances[i] < StreamMaxSpacingPx)
                {
                    currentLen++;
                }
                else
                {
                    if (currentLen > 0) sequenceLengths.Add(currentLen + 1);
                    currentLen = 0;
                }
            }
            if (currentLen > 0) sequenceLengths.Add(currentLen + 1);

            float burstCount = sequenceLengths.Count(l => l >= BurstMinLength && l <= BurstMaxLength);
            float streamCount = sequenceLengths.Count(l => l >= StreamMinLength);
            float maxContinuousStream = sequenceLengths.Count > 0 ? sequenceLengths.Max() : 0;
            float totalStreamNotes = sequenceLengths.Where(l => l >= StreamMinLength).Sum();

            // Local Instability & Variances
            List<float> rhythmInstabilities = new List<float>();
            List<float> spacingInstabilities = new List<float>();
            List<int> denseIndices = new List<int>();

            for (int i = 0; i < timeGaps.Length; i++)
            {
                if (timeGaps[i] < StreamGapMs) denseIndices.Add(i);
            }

            float maxStreamSpacingVariance = 0;
            if (denseIndices.Count > 0)
            {
                var groups = SplitConsecutive(denseIndices);
                foreach (var group in groups)
                {
                    if (group.Count >= StreamMinLength)
                    {
                        var groupDistances = group.Select(idx => distances[idx]).ToArray();
                        maxStreamSpacingVariance = Math.Max(maxStreamSpacingVariance, CalculatePopStdDev(groupDistances));
                    }
                    if (group.Count >= 2)
                    {
                        rhythmInstabilities.Add(CalculatePopStdDev(group.Select(idx => timeGaps[idx]).ToArray()));
                        spacingInstabilities.Add(CalculatePopStdDev(group.Select(idx => distances[idx]).ToArray()));
                    }
                }
            }

            float buzzSliderCount = objects.Count(o => isSlider[objects.IndexOf(o)]
                                                       && o.Slides >= BuzzSliderMinSlides
                                                       && o.Length < BuzzSliderMaxLengthPx);

            var activeGaps = timeGaps.Where(g => g < ActiveGapMaxMs).ToArray();
            float rhythmChangeRatio = 0;
            float globalRhythmVariance = 0;
            if (activeGaps.Length > 1)
            {
                int changes = 0;
                for (int i = 0; i < activeGaps.Length - 1; i++)
                {
                    if (Math.Abs(activeGaps[i + 1] - activeGaps[i]) > RhythmChangeMinDeltaMs) changes++;
                }
                rhythmChangeRatio = (float)changes / activeGaps.Length;
                globalRhythmVariance = CalculatePopStdDev(activeGaps);
            }

            float avgRhythmInstability = rhythmInstabilities.Count > 0 ? rhythmInstabilities.Average() : 0;
            float avgSpacingInstability = spacingInstabilities.Count > 0 ? spacingInstabilities.Average() : 0;

            int sliderDisruptions = 0;
            for (int i = 1; i < numObjects - 1; i++)
            {
                if (isSlider[i] && timeGaps[i - 1] < SliderDisruptionGapMs && timeGaps[i] < SliderDisruptionGapMs) sliderDisruptions++;
            }
            float sliderDisruptionRate = (float)sliderDisruptions / numObjects;
            float fingerControlScore = (avgRhythmInstability * FingerControlRhythmWeight)
                                     + (avgSpacingInstability * FingerControlSpacingWeight)
                                     + (sliderDisruptionRate * FingerControlSliderWeight);

            // Micro-patterns & Geometry
            float sliderRatio = (float)isSlider.Count(s => s) / numObjects;
            List<float> angles = new List<float>();
            int sharpAngles = 0, squareAngles = 0, wideAngles = 0, linearAngles = 0, perfectOverlaps = 0;
            int verticalJumps = 0;

            for (int i = 0; i < numObjects - 2; i++)
            {
                float dx1 = objects[i + 1].X - objects[i].X;
                float dy1 = objects[i + 1].Y - objects[i].Y;
                float dx2 = objects[i + 2].X - objects[i + 1].X;
                float dy2 = objects[i + 2].Y - objects[i + 1].Y;

                float dist1 = (float)Math.Sqrt(dx1 * dx1 + dy1 * dy1);
                float dist2 = (float)Math.Sqrt(dx2 * dx2 + dy2 * dy2);

                if (dist1 > 0 && dist2 > 0)
                {
                    float dot = (dx1 * dx2 + dy1 * dy2) / (dist1 * dist2);
                    dot = Math.Max(-1.0f, Math.Min(1.0f, dot));
                    float angle = (float)Math.Acos(dot);
                    angles.Add(angle);

                    if (angle < AngleSharpMax) sharpAngles++;
                    else if (angle > AngleSquareMin && angle < AngleSquareMax) squareAngles++;
                    else if (angle > AngleWideMin && angle < AngleWideMax) wideAngles++;
                    else if (angle > AngleLinearMin) linearAngles++;
                }

                float dist2Steps = (float)Math.Sqrt(Math.Pow(objects[i + 2].X - objects[i].X, 2) + Math.Pow(objects[i + 2].Y - objects[i].Y, 2));
                if (dist2Steps < PerfectOverlapMaxPx) perfectOverlaps++;
            }

            for (int i = 0; i < numObjects - 1; i++)
            {
                float dx = Math.Abs(objects[i + 1].X - objects[i].X);
                float dy = Math.Abs(objects[i + 1].Y - objects[i].Y);
                if (dy > VerticalJumpMinDyPx && dx < VerticalJumpMaxDxPx) verticalJumps++;
            }

            // True linear pattern detection (collinearity over 4-object chunks).
            // Ported to match Python osu_tagger.features.v1 extract_meaningful_features exactly.
            // Computed in double to stay close to NumPy's float64 geometry.
            int trueLinearSequences = 0;
            if (numObjects >= 4)
            {
                for (int i = 0; i < numObjects - 3; i++)
                {
                    double sx = objects[i].X, sy = objects[i].Y;
                    double ex = objects[i + 3].X, ey = objects[i + 3].Y;
                    double lineVecX = ex - sx, lineVecY = ey - sy;
                    double lineLen = Math.Sqrt(lineVecX * lineVecX + lineVecY * lineVecY);

                    if (lineLen > LinearChunkMinLengthPx) // sequence must cover some distance
                    {
                        double dirX = lineVecX / lineLen, dirY = lineVecY / lineLen;
                        // Normal vector perpendicular to the line: (-dir.y, dir.x)
                        double normX = -dirY, normY = dirX;
                        double dev1 = Math.Abs((objects[i + 1].X - sx) * normX + (objects[i + 1].Y - sy) * normY);
                        double dev2 = Math.Abs((objects[i + 2].X - sx) * normX + (objects[i + 2].Y - sy) * normY);
                        if (dev1 < LinearMaxDeviationPx && dev2 < LinearMaxDeviationPx) trueLinearSequences++;
                    }
                }
            }

            return new float[]
            {
                burstCount, streamCount, maxContinuousStream, totalStreamNotes,
                rhythmChangeRatio, globalRhythmVariance,
                maxStreamSpacingVariance, buzzSliderCount,
                fingerControlScore, avgRhythmInstability, avgSpacingInstability, sliderDisruptionRate,
                numObjects, objectsPerSec,
                distances.Length > 0 ? distances.Average() : 0,
                distances.Length > 0 ? CalculatePopStdDev(distances) : 0,
                distances.Length > 0 ? CalculatePercentile(distances, 0.95f) : 0,
                timeGaps.Length > 0 ? timeGaps.Average() : 0,
                timeGaps.Length > 0 ? CalculatePopStdDev(timeGaps) : 0,
                sliderRatio,
                angles.Count > 0 ? angles.Average() : 0,
                angles.Count > 0 ? CalculatePopStdDev(angles) : 0,
                numObjects > 0 ? (float)sharpAngles / numObjects : 0,
                numObjects > 0 ? (float)squareAngles / numObjects : 0,
                numObjects > 0 ? (float)wideAngles / numObjects : 0,
                numObjects > 0 ? (float)linearAngles / numObjects : 0,
                numObjects > 0 ? (float)verticalJumps / numObjects : 0,
                numObjects > 0 ? (float)perfectOverlaps / numObjects : 0,
                numObjects > 0 ? (float)trueLinearSequences / numObjects : 0 // Index 28: true linear sequences (now computed, matches Python)
            };
        }

        // 3. The 90-Feature Aggregator
        public static float[] AggregateMapFeatures(List<List<RawHitObject>> sections)
        {
            var sectionFeatures = sections.Select(ExtractSectionFeatures).Where(f => f != null).ToList();
            if (sectionFeatures.Count == 0) return null;

            int featureLength = sectionFeatures[0].Length;
            float[] max = new float[featureLength];
            float[] mean = new float[featureLength];
            float[] std = new float[featureLength];

            for (int i = 0; i < featureLength; i++)
            {
                var col = sectionFeatures.Select(f => f[i]).ToArray();
                max[i] = col.Max();
                mean[i] = col.Average();
                std[i] = CalculatePopStdDev(col);
            }

            float hasPeakStream = max[IdxMaxContinuousStream] >= PeakStreamMinLength ? 1 : 0;
            float hasPeakJump = max[IdxPercentile95Distance] > PeakJumpMinP95Px ? 1 : 0;
            float isHybrid = (hasPeakStream == 1 && hasPeakJump == 1) ? 1 : 0;

            var finalFeatures = new List<float>();
            finalFeatures.AddRange(max);
            finalFeatures.AddRange(mean);
            finalFeatures.AddRange(std);
            finalFeatures.AddRange(new[] { hasPeakStream, hasPeakJump, isHybrid });

            // Exactly 90 Features for the ONNX Tensor
            return finalFeatures.ToArray();
        }

        // --- MATH HELPERS (Ensuring strict NumPy equivalence) ---

        // Matches numpy.std (ddof=0)
        private static float CalculatePopStdDev(IEnumerable<float> values)
        {
            var count = values.Count();
            if (count == 0) return 0;
            var avg = values.Average();
            var sum = values.Sum(d => (d - avg) * (d - avg));
            return (float)Math.Sqrt(sum / count);
        }

        // Matches numpy.percentile
        private static float CalculatePercentile(IEnumerable<float> seq, float percentile)
        {
            var elements = seq.OrderBy(x => x).ToArray();
            int N = elements.Length;
            if (N == 0) return 0;
            float n = (N - 1) * percentile + 1;
            if (n == 1f) return elements[0];
            else if (n == N) return elements[N - 1];
            else
            {
                int k = (int)n;
                float d = n - k;
                return elements[k - 1] + d * (elements[k] - elements[k - 1]);
            }
        }

        private static List<List<int>> SplitConsecutive(List<int> indices)
        {
            var result = new List<List<int>>();
            if (indices.Count == 0) return result;
            var current = new List<int> { indices[0] };
            for (int i = 1; i < indices.Count; i++)
            {
                if (indices[i] == indices[i - 1] + 1)
                    current.Add(indices[i]);
                else
                {
                    result.Add(current);
                    current = new List<int> { indices[i] };
                }
            }
            result.Add(current);
            return result;
        }
    }
}