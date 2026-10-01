using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;

namespace OsuScout
{
    // A structured class to hold the raw data, just like your Python inner lists
    public class RawHitObject
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Time { get; set; }
        public int TypeBit { get; set; }
        public string CurveType { get; set; }
        public List<int[]> CurvePoints { get; set; } = new List<int[]>();
        public int Slides { get; set; } = 1;
        public float Length { get; set; } = 0f;

        // The same length parsed as a double. The v2 extractor needs it: Python
        // parses it with float() (64-bit), and slider timing and end positions
        // are computed from it, so the 32-bit Length above would drift.
        public double LengthExact { get; set; } = 0.0;
    }

    // One [TimingPoints] line. Uninherited ("red") points set the beat length in
    // ms; inherited ("green") points carry a negative value, -100 / slider velocity.
    public readonly record struct TimingPoint(double Time, double BeatLength, bool Uninherited);

    public class OsuParser
    {
        public string FilePath { get; private set; }
        public string[] Lines { get; private set; }
        public Dictionary<string, string> Metadata { get; private set; } = new Dictionary<string, string>();

        public OsuParser(string filepath)
        {
            FilePath = filepath;
        }

        public void ReadFile()
        {
            if (!File.Exists(FilePath))
            {
                Console.WriteLine($"Error: Could not find file at {FilePath}");
                Lines = Array.Empty<string>();
                return;
            }

            FileInfo info = new FileInfo(FilePath);
            if (info.Length > 5 * 1024 * 1024)
            {
                // Skip massive files to prevent RAM exhaustion (DoS protection)
                Lines = Array.Empty<string>();
                return;
            }

            Lines = File.ReadAllLines(FilePath);
            ParseMetadata();
        }

        private void ParseMetadata()
        {
            foreach (var line in Lines)
            {
                string trimmed = line.Trim();

                // Stop parsing at section headers
                if (trimmed.StartsWith("["))
                {
                    string lower = trimmed.ToLower();
                    if (lower == "[difficulty]" || lower == "[events]" ||
                        lower == "[timingpoints]" || lower == "[hitobjects]")
                        break;
                }

                int separatorIndex = trimmed.IndexOf(':');
                if (separatorIndex > 0)
                {
                    string key = trimmed.Substring(0, separatorIndex).Trim();
                    string value = trimmed.Substring(separatorIndex + 1).Trim();
                    Metadata[key] = value;
                }
            }
        }

        public List<RawHitObject> ExtractRawHitObjects()
        {
            var hitObjects = new List<RawHitObject>();
            if (Lines == null || Lines.Length == 0) return hitObjects;

            int startIndex = -1;
            for (int i = 0; i < Lines.Length; i++)
            {
                if (Lines[i].Trim() == "[HitObjects]")
                {
                    startIndex = i + 1;
                    break;
                }
            }

            if (startIndex == -1) return hitObjects;

            for (int i = startIndex; i < Lines.Length; i++)
            {
                string line = Lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;

                string[] parts = line.Split(',');
                if (parts.Length < 4) continue;

                try
                {
                    var obj = new RawHitObject
                    {
                        X = int.Parse(parts[0]),
                        Y = int.Parse(parts[1]),
                        Time = int.Parse(parts[2]),
                        TypeBit = int.Parse(parts[3])
                    };

                    bool isSlider = (obj.TypeBit & 2) != 0;
                    if (isSlider && parts.Length >= 8)
                    {
                        string[] curveParts = parts[5].Split('|');
                        obj.CurveType = curveParts[0];

                        for (int j = 1; j < curveParts.Length; j++)
                        {
                            string[] coords = curveParts[j].Split(':');
                            if (coords.Length == 2)
                            {
                                obj.CurvePoints.Add(new int[] { int.Parse(coords[0]), int.Parse(coords[1]) });
                            }
                        }

                        obj.Slides = int.Parse(parts[6]);
                        // CultureInfo.InvariantCulture ensures decimals parse correctly regardless of the user's OS language
                        obj.Length = float.Parse(parts[7], CultureInfo.InvariantCulture);
                        obj.LengthExact = double.Parse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture);
                    }

                    hitObjects.Add(obj);
                }
                catch (Exception)
                {
                    // Silently skip malformed lines, mirroring your Python logic
                }
            }

            return hitObjects;
        }

        // The non-empty, non-comment lines of one section, e.g. "[Difficulty]".
        // Mirrors osu_parser._section_lines in the training repo.
        private List<string> SectionLines(string header)
        {
            var lines = new List<string>();
            if (Lines == null) return lines;
            bool inside = false;
            foreach (var raw in Lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inside = line == header;
                    continue;
                }
                if (inside && line.Length > 0 && !line.StartsWith("//"))
                    lines.Add(line);
            }
            return lines;
        }

        // [Difficulty] as doubles. Very old maps have no ApproachRate line; the game
        // uses OverallDifficulty for it there, and so does this (as Python does).
        public Dictionary<string, double> GetDifficulty()
        {
            var difficulty = new Dictionary<string, double>();
            foreach (var line in SectionLines("[Difficulty]"))
            {
                int sep = line.IndexOf(':');
                if (sep < 0) continue;
                if (double.TryParse(line.Substring(sep + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    difficulty[line.Substring(0, sep).Trim()] = value;
            }
            if (!difficulty.ContainsKey("ApproachRate") && difficulty.TryGetValue("OverallDifficulty", out double od))
                difficulty["ApproachRate"] = od;
            return difficulty;
        }

        // [TimingPoints], stably sorted by time so points sharing a timestamp keep
        // their file order. Files without the uninherited column (very old format)
        // mark inherited points with a negative beat length.
        public List<TimingPoint> GetTimingPoints()
        {
            var points = new List<TimingPoint>();
            foreach (var line in SectionLines("[TimingPoints]"))
            {
                string[] parts = line.Split(',');
                if (parts.Length < 2) continue;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double time)) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLength)) continue;
                bool uninherited = parts.Length >= 7 ? parts[6].Trim() == "1" : beatLength > 0;
                points.Add(new TimingPoint(time, beatLength, uninherited));
            }
            // LINQ's OrderBy is a stable sort, like Python's sorted(); List.Sort is not.
            return points.OrderBy(p => p.Time).ToList();
        }
    }
}