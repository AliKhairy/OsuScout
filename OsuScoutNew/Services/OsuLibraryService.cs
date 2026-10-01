using OsuScout;
using OsuScoutNew.Core;
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
        public async Task<List<BeatmapRecord>> SearchBeatmapsAsync(OsuClient client, string searchText, List<string> requiredTags, List<string> excludedTags, double minStars, double maxStars, double minBpm, double maxBpm, double minLength, double maxLength)
        {
            return await Task.Run(() =>
            {
                using var db = new OsuDbContext(client);
                var query = db.Beatmaps.AsQueryable();

                // An infinite bound means "no limit", so it's left out of the SQL entirely.
                double minSeconds = minLength * 60;
                double maxSeconds = maxLength * 60;
                if (!double.IsInfinity(minStars)) query = query.Where(m => m.StarRating >= minStars);
                if (!double.IsInfinity(maxStars)) query = query.Where(m => m.StarRating <= maxStars);
                if (!double.IsInfinity(minBpm)) query = query.Where(m => m.BPM >= minBpm);
                if (!double.IsInfinity(maxBpm)) query = query.Where(m => m.BPM <= maxBpm);
                if (!double.IsInfinity(minSeconds)) query = query.Where(m => m.LengthSeconds >= minSeconds);
                if (!double.IsInfinity(maxSeconds)) query = query.Where(m => m.LengthSeconds <= maxSeconds);

                if (!string.IsNullOrEmpty(searchText))
                {
                    // SQLite's instr() (what Contains translates to) is case-sensitive, so both
                    // sides must be lowered or a query like "sugar life" never matches "Sugar Life".
                    string needle = searchText.ToLowerInvariant();
                    query = query.Where(m => m.Title.ToLower().Contains(needle)
                                          || m.Artist.ToLower().Contains(needle)
                                          || m.Version.ToLower().Contains(needle));
                }

                foreach (var tag in requiredTags) query = query.Where(m => m.Tags.Contains(tag));
                foreach (var tag in excludedTags) query = query.Where(m => !m.Tags.Contains(tag));

                return query.OrderByDescending(m => m.StarRating).ToList();
            });
        }

        // --- HEAVY FILE SCANNING ---
        public async Task ScanLibraryAsync(IBeatmapSource source, IProgress<int> progress = null)
        {
            if (!Directory.Exists(source.Root)) return;

            await Task.Run(() =>
            {
                var allOsuFiles = source.EnumerateMapFiles().ToArray();

                HashSet<string> existingPaths;
                using (var db = new OsuDbContext(source.Kind))
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

                        float[] networkInputs = ComputeFeatures(parser, rawObjects);
                        if (networkInputs == null) return;

                        List<string> predictedTags;
                        lock (_aiLock)
                        {
                            predictedTags = _classifier.Predict(networkInputs);
                        }
                        // A map the model has no confident opinion on is still a map the user owns:
                        // store it with no tags so it stays searchable instead of vanishing.

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
                    using (var db = new OsuDbContext(source.Kind))
                    {
                        db.ChangeTracker.AutoDetectChangesEnabled = false;
                        db.Beatmaps.AddRange(batchRecords);
                        db.SaveChanges();
                    }
                }
            });
        }

        // The model's input vector, from whichever extractor its model_config.json
        // names: v1 (90 features) for older model files, v2 (72) for newer ones.
        // Both require at least one playable section, as the training rows did.
        private float[] ComputeFeatures(OsuParser parser, List<RawHitObject> rawObjects)
        {
            var sections = FeatureExtractor.SplitIntoSections(rawObjects);
            if (sections.Count == 0) return null;
            if (_classifier.Config.FeatureVersion == 2)
                return FeatureExtractorV2.Extract(rawObjects, parser.GetDifficulty(), parser.GetTimingPoints());
            return FeatureExtractor.AggregateMapFeatures(sections);
        }

        // --- RE-TAGGING AFTER A MODEL CHANGE ---
        // ScanLibraryAsync only tags files it has never seen, so after a model update
        // every map already in the library would keep the old model's tags. This
        // re-computes the tags of every stored map with the current model and leaves
        // everything else about the record alone. Maps whose file is gone or no longer
        // parses keep their old tags rather than being dropped.
        public async Task RetagLibraryAsync(OsuClient client, IProgress<int> progress = null)
        {
            await Task.Run(() =>
            {
                List<(int id, string path)> rows;
                using (var db = new OsuDbContext(client))
                {
                    rows = db.Beatmaps.Select(b => new { b.Id, b.FilePath }).AsEnumerable()
                                      .Select(b => (b.Id, b.FilePath)).ToList();
                }
                if (rows.Count == 0) return;

                var newTags = new ConcurrentDictionary<int, string>();
                int done = 0, lastReported = -1;

                Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, row =>
                {
                    try
                    {
                        if (!File.Exists(row.path)) return;
                        var parser = new OsuParser(row.path);
                        parser.ReadFile();
                        var rawObjects = parser.ExtractRawHitObjects();
                        if (rawObjects.Count == 0) return;

                        float[] networkInputs = ComputeFeatures(parser, rawObjects);
                        if (networkInputs == null) return;

                        List<string> predictedTags;
                        lock (_aiLock)
                        {
                            predictedTags = _classifier.Predict(networkInputs);
                        }
                        newTags[row.id] = string.Join(",", predictedTags);
                    }
                    catch { }
                    finally
                    {
                        int percent = (int)((Interlocked.Increment(ref done) / (double)rows.Count) * 100);
                        if (percent > lastReported && progress != null)
                        {
                            progress.Report(percent);
                            lastReported = percent;
                        }
                    }
                });

                using (var db = new OsuDbContext(client))
                {
                    foreach (var record in db.Beatmaps)
                    {
                        if (newTags.TryGetValue(record.Id, out string tags)) record.Tags = tags;
                    }
                    db.SaveChanges();
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
        public void ProcessAndSaveSingleMap(IBeatmapSource source, string filePath)
        {
            try
            {
                var parser = new OsuParser(filePath);
                parser.ReadFile();
                if (parser.Metadata.GetValueOrDefault("Mode", "0") != "0") return;

                var rawObjects = parser.ExtractRawHitObjects();
                if (rawObjects.Count == 0) return;

                float[] networkInputs = ComputeFeatures(parser, rawObjects);
                if (networkInputs == null) return;

                List<string> predictedTags;
                lock (_aiLock)
                {
                    predictedTags = _classifier.Predict(networkInputs);
                }
                // Untagged maps are kept (see ScanLibraryAsync) so they remain searchable.

                double calculatedStars = 0;
                try
                {
                    using Beatmap ppMap = Beatmap.FromPath(filePath);
                    DifficultyAttributes diffAttrs = ppMap.CalculateDifficulty(mods: 0);
                    calculatedStars = diffAttrs.Values.stars;
                }
                catch { }

                var stats = ExtractBpmAndLength(filePath);

                using (var db = new OsuDbContext(source.Kind))
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