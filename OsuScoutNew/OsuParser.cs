using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

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
    }

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
    }
}