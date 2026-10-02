using Microsoft.EntityFrameworkCore;
using OsuScout;
using OsuScoutNew.Core;
using Rosu;
using Rosu.Net;
using Rosu.Net.Attributes;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

        // Maps scanned between saves to the database.
        private const int ScanChunkSize = 1000;

        public OsuLibraryService(OsuClassifier classifier)
        {
            _classifier = classifier;
        }

        // --- DATABASE QUERYING ---
        public async Task<List<BeatmapRecord>> SearchBeatmapsAsync(OsuClient client, MapFilter filter)
        {
            return await Task.Run(() =>
            {
                using var db = new OsuDbContext(client);
                var query = db.Beatmaps.AsQueryable();

                // An infinite bound means "no limit", so it's left out of the SQL entirely. Plain
                // locals (not filter.X.Min) keep each value a SQL parameter.
                double minStars = filter.Stars.Min, maxStars = filter.Stars.Max;
                double minBpm = filter.Bpm.Min, maxBpm = filter.Bpm.Max;
                double minSeconds = filter.LengthMinutes.Min * 60, maxSeconds = filter.LengthMinutes.Max * 60;
                double minCs = filter.CS.Min, maxCs = filter.CS.Max;
                double minAr = filter.AR.Min, maxAr = filter.AR.Max;
                double minOd = filter.OD.Min, maxOd = filter.OD.Max;
                double minHp = filter.HP.Min, maxHp = filter.HP.Max;
                if (!double.IsInfinity(minStars)) query = query.Where(m => m.StarRating >= minStars);
                if (!double.IsInfinity(maxStars)) query = query.Where(m => m.StarRating <= maxStars);
                if (!double.IsInfinity(minBpm)) query = query.Where(m => m.BPM >= minBpm);
                if (!double.IsInfinity(maxBpm)) query = query.Where(m => m.BPM <= maxBpm);
                if (!double.IsInfinity(minSeconds)) query = query.Where(m => m.LengthSeconds >= minSeconds);
                if (!double.IsInfinity(maxSeconds)) query = query.Where(m => m.LengthSeconds <= maxSeconds);
                if (!double.IsInfinity(minCs)) query = query.Where(m => m.CS >= minCs);
                if (!double.IsInfinity(maxCs)) query = query.Where(m => m.CS <= maxCs);
                if (!double.IsInfinity(minAr)) query = query.Where(m => m.AR >= minAr);
                if (!double.IsInfinity(maxAr)) query = query.Where(m => m.AR <= maxAr);
                if (!double.IsInfinity(minOd)) query = query.Where(m => m.OD >= minOd);
                if (!double.IsInfinity(maxOd)) query = query.Where(m => m.OD <= maxOd);
                if (!double.IsInfinity(minHp)) query = query.Where(m => m.HP >= minHp);
                if (!double.IsInfinity(maxHp)) query = query.Where(m => m.HP <= maxHp);

                string searchText = filter.SearchText;
                if (!string.IsNullOrEmpty(searchText))
                {
                    // SQLite's instr() (what Contains translates to) is case-sensitive, so both
                    // sides must be lowered or a query like "sugar life" never matches "Sugar Life".
                    string needle = searchText.ToLowerInvariant();
                    query = query.Where(m => m.Title.ToLower().Contains(needle)
                                          || m.Artist.ToLower().Contains(needle)
                                          || m.Mapper.ToLower().Contains(needle)
                                          || m.Version.ToLower().Contains(needle));
                }

                foreach (var tag in filter.RequiredTags) query = query.Where(m => m.Tags.Contains(tag));
                foreach (var tag in filter.ExcludedTags) query = query.Where(m => !m.Tags.Contains(tag));

                // A map the model found no tags for neither has nor lacks any tag, so it only
                // shows while no tag filter is set. Before, every "-tag" filter let them all through.
                if (filter.RequiredTags.Count + filter.ExcludedTags.Count > 0)
                    query = query.Where(m => m.Tags != null && m.Tags != "");

                return query.OrderByDescending(m => m.StarRating).ToList();
            });
        }

        // --- HEAVY FILE SCANNING ---
        // finding reports (files checked, files to check) while looking for maps among files
        // the library has never seen: free for stable, where every .osu is one, but a read of
        // each file's first bytes for lazer, whose store holds audio and images too.
        public async Task ScanLibraryAsync(IBeatmapSource source, IProgress<int> progress = null,
                                           IProgress<(int Checked, int Total)> finding = null)
        {
            if (!Directory.Exists(source.Root)) return;

            await Task.Run(() =>
            {
                HashSet<string> existingPaths;
                using (var db = new OsuDbContext(source.Kind))
                {
                    existingPaths = db.Beatmaps.Select(b => b.FilePath).ToHashSet();
                }

                // A map deleted or updated in lazer (an update is a new file, with a new hash)
                // loses its file at lazer's next startup cleanup; drop its record with it.
                // Stable libraries are left as they always were.
                if (source.Kind == OsuClient.Lazer) PruneMissingFiles(source.Kind, existingPaths);

                // Maps that were mid-scan when a previous scan killed the app (see ScanLog), and
                // files an earlier scan of a source whose files never change found it can skip.
                var skipList = ScanLog.LoadSkipList();
                var known = source.FilesAreImmutable ? KnownSkips.Load(source.Kind) : new HashSet<string>();
                var candidates = source.EnumerateCandidateFiles()
                                       .Where(f => !existingPaths.Contains(f)
                                                && !known.Contains(Path.GetFileName(f))
                                                && !ScanLog.IsSkipped(skipList, source.Root, f))
                                       .ToArray();

                var notMaps = new ConcurrentBag<string>();
                int checkedFiles = 0, lastFindPercent = -1;
                var filesToProcess = candidates.AsParallel().Where(f =>
                {
                    bool isMap = source.IsMapFile(f);
                    if (!isMap && source.FilesAreImmutable) notMaps.Add(Path.GetFileName(f));

                    int done = Interlocked.Increment(ref checkedFiles);
                    int percent = (int)(done * 100L / candidates.Length);
                    if (finding != null && percent > lastFindPercent)
                    {
                        lastFindPercent = percent;
                        finding.Report((done, candidates.Length));
                    }
                    return isMap;
                }).ToArray();
                KnownSkips.Add(source.Kind, notMaps);

                if (filesToProcess.Length == 0)
                {
                    if (source.Kind == OsuClient.Lazer) DropSupersededCopies(source.Kind);
                    return;
                }

                int totalFiles = filesToProcess.Length;
                int processedFiles = 0;
                int lastReportedPercent = -1;
                // osu!taiko, catch and mania maps: never stored, and on lazer never read again.
                var otherModes = new ConcurrentBag<string>();

                using var log = new ScanLog("scan", source.Root, existingPaths.Count + totalFiles, totalFiles);
                // Saved a chunk at a time, so a scan that's cut short (the app closed or
                // killed) keeps what it finished and the next launch scans only the rest.
                foreach (string[] chunk in filesToProcess.Chunk(ScanChunkSize))
                {
                    var batchRecords = new ConcurrentBag<BeatmapRecord>();

                    Parallel.ForEach(chunk, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, filePath =>
                    {
                        log.Started(filePath);
                        try
                        {
                            var parser = new OsuParser(filePath);
                            parser.ReadFile();
                            if (parser.Metadata.GetValueOrDefault("Mode", "0") != "0")
                            {
                                if (source.FilesAreImmutable) otherModes.Add(Path.GetFileName(filePath));
                                return;
                            }

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

                            var stats = ExtractBpmAndLength(filePath);

                            batchRecords.Add(WithDetails(parser, new BeatmapRecord
                            {
                                FilePath = filePath,
                                Title = parser.Metadata.GetValueOrDefault("Title", "Unknown"),
                                Artist = parser.Metadata.GetValueOrDefault("Artist", "Unknown"),
                                Version = parser.Metadata.GetValueOrDefault("Version", "Unknown"),
                                BeatmapID = parser.Metadata.GetValueOrDefault("BeatmapID", "0"),
                                BPM = stats.bpm,
                                LengthSeconds = stats.length,
                                Tags = string.Join(",", predictedTags),
                                StarRating = CalculateStars(filePath, parser)
                            }));
                        }
                        catch { }
                        finally { log.Finished(filePath); }

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
                }
                KnownSkips.Add(source.Kind, otherModes);
                if (source.Kind == OsuClient.Lazer) DropSupersededCopies(source.Kind);
                log.Complete();
            });
        }

        // lazer keeps a replaced file until its own next startup, so after an editor save or a
        // map update the old and the new .osu both sit in files/ for a while and both would be
        // listed. Of records that are the same difficulty (the same online ID; for a map that
        // was never uploaded, the same artist, title, difficulty and mapper), only the one
        // whose file was written last is kept.
        private static void DropSupersededCopies(OsuClient client)
        {
            using var db = new OsuDbContext(client);
            var rows = db.Beatmaps.Select(b => new { b.Id, b.FilePath, b.BeatmapID, b.Artist, b.Title, b.Version, b.Mapper }).ToList();
            var superseded = rows.GroupBy(r => SameDifficultyKey(r.BeatmapID, r.Artist, r.Title, r.Version, r.Mapper))
                                 .Where(g => g.Count() > 1)
                                 .SelectMany(g => g.OrderByDescending(r => WrittenAt(r.FilePath)).Skip(1))
                                 .Select(r => r.Id)
                                 .ToList();
            if (superseded.Count == 0) return;
            db.Beatmaps.Where(b => superseded.Contains(b.Id)).ExecuteDelete();
        }

        private static string SameDifficultyKey(string beatmapId, string artist, string title, string version, string mapper) =>
            int.TryParse(beatmapId, out int id) && id > 0 ? "id:" + id : $"map:{artist}\n{title}\n{version}\n{mapper}";

        private static DateTime WrittenAt(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return DateTime.MinValue;
                // A hard link made by lazer's import from stable keeps the stable file's times;
                // a file lazer wrote itself is created when it is written.
                return info.LastWriteTimeUtc > info.CreationTimeUtc ? info.LastWriteTimeUtc : info.CreationTimeUtc;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        // Star rating from rosu-pp, or 0 when it can't or shouldn't be computed.
        // rosu runs in native code, where a map that makes it build too many slider
        // ticks takes the whole app down with it (issue #12), so StarRatingGuard
        // vets the map first. rosu's own IsSuspicious is not used: it rejects real
        // gimmick maps (Centipede's visualisation diff, for one) that it rates in
        // milliseconds.
        // The map is read in .NET and handed over as bytes: Rosu.Net's FromPath marshals the
        // path as ANSI and fails ("IoError") on any non-English character, e.g. a Cyrillic
        // Windows username, which gave every map 0 stars. Bytes also don't care that lazer's
        // stored files have no extension.
        private static double CalculateStars(string filePath, OsuParser parser)
        {
            if (!StarRatingGuard.IsSafeToRate(parser)) return 0;
            try
            {
                using Beatmap ppMap = Beatmap.FromBytes(File.ReadAllBytes(filePath));
                using DifficultyAttributes diffAttrs = ppMap.CalculateDifficulty(mods: 0);
                return diffAttrs.Values.stars;
            }
            catch
            {
                return 0;
            }
        }

        private static void PruneMissingFiles(OsuClient client, IEnumerable<string> storedPaths)
        {
            var missing = storedPaths.Where(p => !File.Exists(p)).ToList();
            if (missing.Count == 0) return;

            using var db = new OsuDbContext(client);
            db.Beatmaps.Where(b => missing.Contains(b.FilePath)).ExecuteDelete();
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
        public async Task RetagLibraryAsync(IBeatmapSource source, IProgress<int> progress = null)
        {
            await Task.Run(() =>
            {
                List<(int id, string path)> rows;
                using (var db = new OsuDbContext(source.Kind))
                {
                    rows = db.Beatmaps.Select(b => new { b.Id, b.FilePath }).AsEnumerable()
                                      .Select(b => (b.Id, b.FilePath)).ToList();
                }
                // It runs the same feature extractor as the scan, so it gets the same protection.
                var skipList = ScanLog.LoadSkipList();
                rows = rows.Where(r => !ScanLog.IsSkipped(skipList, source.Root, r.path)).ToList();
                if (rows.Count == 0) return;

                var newTags = new ConcurrentDictionary<int, string>();
                int done = 0, lastReported = -1;

                using var log = new ScanLog("re-tag", source.Root, rows.Count, rows.Count);
                Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, row =>
                {
                    log.Started(row.path);
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
                        log.Finished(row.path);
                        int percent = (int)((Interlocked.Increment(ref done) / (double)rows.Count) * 100);
                        if (percent > lastReported && progress != null)
                        {
                            progress.Report(percent);
                            lastReported = percent;
                        }
                    }
                });

                using (var db = new OsuDbContext(source.Kind))
                {
                    foreach (var record in db.Beatmaps)
                    {
                        if (newTags.TryGetValue(record.Id, out string tags)) record.Tags = tags;
                    }
                    db.SaveChanges();
                }
                log.Complete();
            });
        }

        // The map's mapper and difficulty settings (CS, AR, OD, HP), straight from the file.
        private static BeatmapRecord WithDetails(OsuParser parser, BeatmapRecord record)
        {
            var difficulty = parser.GetDifficulty();
            record.Mapper = parser.Metadata.GetValueOrDefault("Creator", "");
            record.CS = difficulty.GetValueOrDefault("CircleSize");
            record.AR = difficulty.GetValueOrDefault("ApproachRate");
            record.OD = difficulty.GetValueOrDefault("OverallDifficulty");
            record.HP = difficulty.GetValueOrDefault("HPDrainRate");
            return record;
        }

        // --- FILLING IN NEWER COLUMNS ---
        // Maps stored before the mapper and CS/AR/OD/HP were recorded have them empty. This
        // reads them from each map's file once; maps whose file is gone or unreadable stay
        // empty (and are retried next launch).
        public async Task FillMissingDetailsAsync(OsuClient client, IProgress<int> progress = null)
        {
            await Task.Run(() =>
            {
                List<(int id, string path)> rows;
                using (var db = new OsuDbContext(client))
                {
                    rows = db.Beatmaps.Where(b => b.CS == null).Select(b => new { b.Id, b.FilePath }).AsEnumerable()
                                      .Select(b => (b.Id, b.FilePath)).ToList();
                }
                if (rows.Count == 0) return;

                var details = new ConcurrentDictionary<int, BeatmapRecord>();
                int done = 0, lastReported = -1;

                Parallel.ForEach(rows, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, row =>
                {
                    try
                    {
                        if (!File.Exists(row.path)) return;
                        var parser = new OsuParser(row.path);
                        parser.ReadFile();
                        details[row.id] = WithDetails(parser, new BeatmapRecord());
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
                    foreach (var record in db.Beatmaps.Where(b => b.CS == null))
                    {
                        if (!details.TryGetValue(record.Id, out var found)) continue;
                        record.Mapper = found.Mapper;
                        record.CS = found.CS;
                        record.AR = found.AR;
                        record.OD = found.OD;
                        record.HP = found.HP;
                    }
                    db.SaveChanges();
                }
            });
        }

        // --- FILE PARSING UTILITY ---
        // .osu files always write decimals with '.', so numbers are parsed with the invariant
        // culture. With the PC's locale, Russian Windows (decimal ',') rejected "333.33" and
        // every map with a fractional beat length got 0 BPM.
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
                        if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double beatLen) && beatLen > 0)
                        {
                            bpm = 60000.0 / beatLen;
                        }
                    }

                    if (inObjects)
                    {
                        var parts = line.Split(',');
                        if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int time))
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
        // A map that appeared while the app is open (see OsuLiveTrackerService).
        public void ProcessAndSaveSingleMap(IBeatmapSource source, string filePath)
        {
            try
            {
                var parser = new OsuParser(filePath);
                parser.ReadFile();
                if (parser.Metadata.GetValueOrDefault("Mode", "0") != "0")
                {
                    if (source.FilesAreImmutable) KnownSkips.Add(source.Kind, new[] { Path.GetFileName(filePath) });
                    return;
                }

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

                double calculatedStars = CalculateStars(filePath, parser);
                var stats = ExtractBpmAndLength(filePath);

                using (var db = new OsuDbContext(source.Kind))
                {
                    if (db.Beatmaps.Any(b => b.FilePath == filePath)) return;

                    db.Beatmaps.Add(WithDetails(parser, new BeatmapRecord
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
                    }));
                    db.SaveChanges();
                }
                // An editor save or a map update in lazer: the new file replaces the old one.
                if (source.Kind == OsuClient.Lazer) DropSupersededCopies(source.Kind);
            }
            catch
            {
                // Swallow corrupted maps during live processing
            }
        }
    }
}