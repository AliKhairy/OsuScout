using OsuScout;
using OsuScoutNew.Core;
using OsuScoutNew.Services;

namespace OsuScoutNew.Tests;

// OsuDbContext.DataFolder is static, so tests that scan into a database run one at a time.
[CollectionDefinition(nameof(LibraryDatabase), DisableParallelization = true)]
public class LibraryDatabase { }

// Loads the five ONNX models once for all scan tests.
public sealed class ClassifierFixture : IDisposable
{
    public OsuClassifier Classifier { get; } = new OsuClassifier();

    public ClassifierFixture() => Classifier.Initialize(RepoPaths.ModelAssets);

    public void Dispose() => Classifier.Dispose();
}

// The real scan pipeline (parser, features, model, rosu-pp, SQLite) run on a fake
// Songs folder and a fake lazer data folder holding the same maps.
[Collection(nameof(LibraryDatabase))]
public sealed class LibraryScanTests : IClassFixture<ClassifierFixture>, IDisposable
{
    private static readonly string[] Fixtures = { "stream", "jump", "tech" };

    private readonly OsuLibraryService _library;
    private readonly TempFolder _data = new TempFolder();
    private readonly TempFolder _songs = new TempFolder();
    private readonly TempFolder _lazer = new TempFolder();

    public LibraryScanTests(ClassifierFixture fixture)
    {
        OsuDbContext.DataFolder = _data.Path;
        _library = new OsuLibraryService(fixture.Classifier);
        FakeLazer.MakeDataFolder(_lazer.Path);
        foreach (var client in new[] { OsuClient.Stable, OsuClient.Lazer })
        {
            using var db = new OsuDbContext(client);
            db.EnsureSchema();
        }
    }

    public void Dispose()
    {
        _data.Dispose();
        _songs.Dispose();
        _lazer.Dispose();
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(RepoPaths.ParityFixtures, name + ".osu"));

    // Map file name -> path, in a Songs folder the way stable lays it out.
    private Dictionary<string, string> FillSongs() =>
        Fixtures.ToDictionary(n => n, n =>
        {
            string path = Path.Combine(_songs.Path, $"1 Artist - {n}", $"Artist - {n} (Mapper) [Hard].osu");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Fixture(n));
            return path;
        });

    // Map file name -> path, in lazer's files/ store, next to a non-map file.
    private Dictionary<string, string> FillLazer()
    {
        FakeLazer.AddFile(_lazer.Path, "ID3\u0004 audio"u8.ToArray());
        return Fixtures.ToDictionary(n => n, n => FakeLazer.AddFile(_lazer.Path, Fixture(n)));
    }

    private static List<BeatmapRecord> Records(OsuClient client)
    {
        using var db = new OsuDbContext(client);
        return db.Beatmaps.ToList();
    }

    [Fact]
    public async Task AMapGetsTheSameRecordFromSongsAsFromLazerFiles()
    {
        var stablePaths = FillSongs();
        var lazerPaths = FillLazer();

        await _library.ScanLibraryAsync(new StableSongsSource(_songs.Path));
        await _library.ScanLibraryAsync(new LazerFilesSource(_lazer.Path));

        var stable = Records(OsuClient.Stable).ToDictionary(r => r.FilePath);
        var lazer = Records(OsuClient.Lazer).ToDictionary(r => r.FilePath);
        Assert.Equal(Fixtures.Length, stable.Count);
        Assert.Equal(Fixtures.Length, lazer.Count);

        foreach (string name in Fixtures)
        {
            var s = stable[stablePaths[name]];
            var l = lazer[lazerPaths[name]];

            Assert.Equal(s.Title, l.Title);
            Assert.Equal(s.Artist, l.Artist);
            Assert.Equal(s.Version, l.Version);
            Assert.Equal(s.BeatmapID, l.BeatmapID);
            Assert.Equal(s.Tags, l.Tags);
            Assert.Equal(s.StarRating, l.StarRating);
            Assert.Equal(s.BPM, l.BPM);
            Assert.Equal(s.LengthSeconds, l.LengthSeconds);
            Assert.Equal(s.Mapper, l.Mapper);
            Assert.Equal(s.CS, l.CS);
            Assert.Equal(s.AR, l.AR);
            Assert.Equal(s.OD, l.OD);
            Assert.Equal(s.HP, l.HP);

            // rosu-pp reads lazer's extension-less files too: a failed read would leave 0 stars.
            Assert.True(l.StarRating > 0, $"{name}: no star rating from {l.FilePath}");
            Assert.False(string.IsNullOrEmpty(l.Tags), $"{name}: no tags");
        }
    }

    [Fact]
    public async Task AScanStoresTheMapperAndDifficultySettings()
    {
        var paths = FillSongs();

        await _library.ScanLibraryAsync(new StableSongsSource(_songs.Path));

        // parity/fixtures/jump.osu: Creator:Zel, CircleSize:4, ApproachRate:9, OverallDifficulty:8.5, HPDrainRate:5.6
        var jump = Records(OsuClient.Stable).Single(r => r.FilePath == paths["jump"]);
        Assert.Equal("Zel", jump.Mapper);
        Assert.Equal(4, jump.CS);
        Assert.Equal(9, jump.AR);
        Assert.Equal(8.5, jump.OD);
        Assert.Equal(5.6, jump.HP);
    }

    [Fact]
    public async Task ALibraryFromAnOlderVersionGainsTheNewColumnsFilledIn()
    {
        var paths = FillSongs();
        // The table exactly as versions before the mapper and CS/AR/OD/HP created it, with one map in it.
        OsuDbContext.DataFolder = Path.Combine(_data.Path, "old");
        Directory.CreateDirectory(OsuDbContext.DataFolder);
        string dbFile = Path.Combine(OsuDbContext.DataFolder, OsuDbContext.FileName(OsuClient.Stable));
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbFile};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE \"Beatmaps\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Beatmaps\" PRIMARY KEY AUTOINCREMENT, " +
                "\"FilePath\" TEXT NULL, \"Title\" TEXT NULL, \"Artist\" TEXT NULL, \"Version\" TEXT NULL, \"Tags\" TEXT NULL, " +
                "\"StarRating\" REAL NOT NULL, \"BeatmapID\" TEXT NULL, \"BPM\" REAL NOT NULL, \"LengthSeconds\" INTEGER NOT NULL);" +
                "INSERT INTO \"Beatmaps\" (FilePath, Title, Artist, Version, Tags, StarRating, BeatmapID, BPM, LengthSeconds) " +
                "VALUES ($path, 'Old title', 'Old artist', 'Hard', 'aim,jumps', 6.43, '123', 175, 193);";
            command.Parameters.AddWithValue("$path", paths["jump"]);
            command.ExecuteNonQuery();
        }

        using (var db = new OsuDbContext(OsuClient.Stable)) db.EnsureSchema();
        await _library.FillMissingDetailsAsync(OsuClient.Stable);

        var record = Assert.Single(Records(OsuClient.Stable));
        // What the old version stored is untouched...
        Assert.Equal(("Old title", "Old artist", "Hard", "aim,jumps", 6.43, "123", 175.0, 193),
                     (record.Title, record.Artist, record.Version, record.Tags, record.StarRating, record.BeatmapID, record.BPM, record.LengthSeconds));
        // ...and the new columns are read from the map.
        Assert.Equal(("Zel", 4.0, 9.0, 8.5, 5.6), (record.Mapper, record.CS, record.AR, record.OD, record.HP));
        // Running it again changes nothing and doesn't fail on the columns already being there.
        using (var db = new OsuDbContext(OsuClient.Stable)) db.EnsureSchema();
        await _library.FillMissingDetailsAsync(OsuClient.Stable);
        Assert.Equal("Zel", Assert.Single(Records(OsuClient.Stable)).Mapper);
    }

    [Fact]
    public async Task SearchFindsAMapByItsMapper()
    {
        var paths = FillSongs();
        await _library.ScanLibraryAsync(new StableSongsSource(_songs.Path));

        var hits = await _library.SearchBeatmapsAsync(OsuClient.Stable, "zel", new(), new(),
            double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity,
            double.NegativeInfinity, double.PositiveInfinity);

        Assert.Contains(hits, h => h.FilePath == paths["jump"]);
    }

    [Fact]
    public async Task LazerAndStableLibrariesAreKeptApart()
    {
        FillSongs();
        FillLazer();

        await _library.ScanLibraryAsync(new LazerFilesSource(_lazer.Path));

        Assert.Equal(Fixtures.Length, Records(OsuClient.Lazer).Count);
        Assert.Empty(Records(OsuClient.Stable));
        Assert.True(File.Exists(Path.Combine(_data.Path, "osuscout-lazer.db")));
    }

    [Fact]
    public async Task ALazerMapWhoseFileIsGoneIsPrunedOnTheNextScan()
    {
        var paths = FillLazer();
        var source = new LazerFilesSource(_lazer.Path);
        await _library.ScanLibraryAsync(source);

        File.Delete(paths["jump"]);
        await _library.ScanLibraryAsync(source);

        Assert.Equal(new[] { paths["stream"], paths["tech"] }.OrderBy(p => p),
                     Records(OsuClient.Lazer).Select(r => r.FilePath).OrderBy(p => p));
    }

    [Fact]
    public async Task StableRecordsAreNotPruned()
    {
        var paths = FillSongs();
        var source = new StableSongsSource(_songs.Path);
        await _library.ScanLibraryAsync(source);

        File.Delete(paths["jump"]);
        await _library.ScanLibraryAsync(source);

        Assert.Equal(Fixtures.Length, Records(OsuClient.Stable).Count);
    }

    [Fact]
    public async Task AMapLazerAddsWhileTrackingIsStoredOnce()
    {
        var source = new LazerFilesSource(_lazer.Path);
        byte[] content = Fixture("tech");
        string final = Path.Combine(_lazer.Path, "files", LazerFilesSource.StoragePath(FakeLazer.Hash(content)));
        Directory.CreateDirectory(Path.GetDirectoryName(final));

        int processed = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tracker = new OsuLiveTrackerService(_library);
        tracker.MapProcessed += () => { Interlocked.Increment(ref processed); first.TrySetResult(); };
        tracker.StartTracking(source);

        string temp = Path.Combine(Path.GetDirectoryName(final), $"_{Path.GetFileName(final)}_{Guid.NewGuid()}");
        File.WriteAllBytes(temp, content);
        File.Move(temp, final);

        await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        tracker.StopTracking();

        Assert.Equal(1, processed);
        var record = Assert.Single(Records(OsuClient.Lazer));
        Assert.Equal(final, record.FilePath);
    }
}
