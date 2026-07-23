using OsuScout;
using Rosu;
using Rosu.Net;
using Rosu.Net.Attributes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OsuScoutNew.Services
{
    public class OsuLibraryService
    {
        private readonly OsuClassifier _classifier;
        private readonly object _aiLock = new object();

        public OsuLibraryService(OsuClassifier classifier)
        {
            _classifier = classifier;
        }

        // --- DATABASE QUERYING ---
        public async Task<List<BeatmapRecord>> SearchBeatmapsAsync(string searchText, List<string> requiredTags, List<string> excludedTags, double minStars, double maxStars, double minBpm, double maxBpm, double minLength, double maxLength)
        {
            return await Task.Run(() =>
            {
                using var db = new OsuDbContext();
                var query = db.Beatmaps.AsQueryable();

                query = query.Where(m => m.StarRating >= minStars && m.StarRating <= maxStars);
                query = query.Where(m => m.BPM >= minBpm && m.BPM <= maxBpm);
                query = query.Where(m => m.LengthSeconds >= (minLength * 60) && m.LengthSeconds <= (maxLength * 60));

                if (!string.IsNullOrEmpty(searchText))
                {
                    query = query.Where(m => m.Title.Contains(searchText) || m.Artist.Contains(searchText));
                }

                foreach (var tag in requiredTags) query = query.Where(m => m.Tags.Contains(tag));
                foreach (var tag in excludedTags) query = query.Where(m => !m.Tags.Contains(tag));

                return query.OrderByDescending(m => m.StarRating).ToList();
            });
        }

        // --- HEAVY FILE SCANNING ---
        public async Task ScanLibraryAsync(string osuSongsPath, IProgress<int> progress = null)
        {
            if (!Directory.Exists(osuSongsPath)) return;

            await Task.Run(() =>
            {
                var allOsuFiles = Directory.GetFiles(osuSongsPath, "*.osu", SearchOption.AllDirectories);

                HashSet<string> existingPaths;
                using (var db = new OsuDbContext())
                {
                    existingPaths = db.Beatmaps.Select(b => b.FilePath).ToHashSet();
                }

                var filesToProcess = allOsuFiles.Where(f => !existingPaths.Contains(f)).ToArray();
                if (filesToProcess.Length == 0) return;

                int totalFiles = filesToProcess.Length;
                int processedFiles = 0;
                int lastReportedPercent = -1;

                var batchRecords = new ConcurrentBag<BeatmapRecord>();

                Parallel.ForEach(filesToProcess, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, filePath =>
                {
                    try
                    {
                        var parser = new OsuParser(filePath);
                        parser.ReadFile();
                        if (parser.Metadata.GetValueOrDefault("Mode", "0") != "0") return;

                        var rawObjects = parser.ExtractRawHitObjects();
                        if (rawObjects.Count == 0) return;

                        var sections = FeatureExtractor.SplitIntoSections(rawObjects);
                        float[] networkInputs = FeatureExtractor.AggregateMapFeatures(sections);
                        if (networkInputs == null) return;

                        List<string> predictedTags;
                        lock (_aiLock)
                        {
                            predictedTags = _classifier.Predict(networkInputs);
                        }
                        if (predictedTags.Count == 0) return;

                        double calculatedStars = 0;
                        try
                        {
                            using Beatmap ppMap = Beatmap.FromPath(filePath);
                            DifficultyAttributes diffAttrs = ppMap.CalculateDifficulty(mods: 0);
                            calculatedStars = diffAttrs.Values.stars;
                        }
                        catch { }

                        var stats = ExtractBpmAndLength(filePath);

                        batchRecords.Add(new BeatmapRecord
                        {
                            FilePath = filePath,
                            Title = parser.Metadata.GetValueOrDefault("Title", "Unknown"),
                            Artist = parser.Metadata.GetValueOrDefault("Artist", "Unknown"),
                            Version = parser.Metadata.GetValueOrDefault("Version", "Unknown"),
                            BeatmapID = parser.Metadata.GetValueOrDefault("BeatmapID", "0"),
                            BPM = stats.bpm,
                            LengthSeconds = stats.length,
                            Tags = string.Join(",", predictedTags),
                            StarRating = calculatedStars
                        });
                    }
                    catch { }

                    int currentCount = Interlocked.Increment(ref processedFiles);
                    int currentPercent = (int)((currentCount / (double)totalFiles) * 100);

                    if (currentPercent > lastReportedPercent && progress != null)
                    {
                        progress.Report(currentPercent);
                        lastReportedPercent = currentPercent;
                    }
                });

                if (batchRecords.Count > 0)
                {
                    using (var db = new OsuDbContext())
                    {
                        db.ChangeTracker.AutoDetectChangesEnabled = false;
                        db.Beatmaps.AddRange(batchRecords);
                        db.SaveChanges();
                    }
                }
            });
        }

        // --- FILE PARSING UTILITY ---
        public (double bpm, int length) ExtractBpmAndLength(string filePath)
        {
            double bpm = 0;
            int length = 0;
            try
            {
                var lines = File.ReadAllLines(filePath);
                bool inTiming = false, inObjects = false;
                int firstTime = -1, lastTime = 0;

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("["))
                    {
                        inTiming = line == "[TimingPoints]";
                        inObjects = line == "[HitObjects]";
                        continue;
                    }

                    if (inTiming && bpm == 0)
                    {
                        var parts = line.Split(',');
                        if (parts.Length > 1 && double.TryParse(parts[1], out double beatLen) && beatLen > 0)
                        {
                            bpm = 60000.0 / beatLen;
                        }
                    }

                    if (inObjects)
                    {
                        var parts = line.Split(',');
                        if (parts.Length > 2 && int.TryParse(parts[2], out int time))
                        {
                            if (firstTime == -1) firstTime = time;
                            lastTime = time;
                        }
                    }
                }
                if (firstTime != -1) length = (lastTime - firstTime) / 1000;
            }
            catch { }

            return (Math.Round(bpm), length);
        }
        // Add this to OsuLibraryService.cs
        public void ProcessAndSaveSingleMap(string filePath)
        {
            try
            {
                var parser = new OsuParser(filePath);
                parser.ReadFile();
                if (parser.Metadata.GetValueOrDefault("Mode", "0") != "0") return;

                var rawObjects = parser.ExtractRawHitObjects();
                if (rawObjects.Count == 0) return;

                var sections = FeatureExtractor.SplitIntoSections(rawObjects);
                float[] networkInputs = FeatureExtractor.AggregateMapFeatures(sections);
                if (networkInputs == null) return;

                List<string> predictedTags;
                lock (_aiLock)
                {
                    predictedTags = _classifier.Predict(networkInputs);
                }
                if (predictedTags.Count == 0) return;

                double calculatedStars = 0;
                try
                {
                    using Beatmap ppMap = Beatmap.FromPath(filePath);
                    DifficultyAttributes diffAttrs = ppMap.CalculateDifficulty(mods: 0);
                    calculatedStars = diffAttrs.Values.stars;
                }
                catch { }

                var stats = ExtractBpmAndLength(filePath);

                using (var db = new OsuDbContext())
                {
                    if (db.Beatmaps.Any(b => b.FilePath == filePath)) return;

                    db.Beatmaps.Add(new BeatmapRecord
                    {
                        FilePath = filePath,
                        Title = parser.Metadata.GetValueOrDefault("Title", "Unknown"),
                        Artist = parser.Metadata.GetValueOrDefault("Artist", "Unknown"),
                        Version = parser.Metadata.GetValueOrDefault("Version", "Unknown"),
                        BeatmapID = parser.Metadata.GetValueOrDefault("BeatmapID", "0"),
                        BPM = stats.bpm,
                        LengthSeconds = stats.length,
                        Tags = string.Join(",", predictedTags),
                        StarRating = calculatedStars
                    });
                    db.SaveChanges();
                }
            }
            catch
            {
                // Swallow corrupted maps during live processing
            }
        }
    }
}