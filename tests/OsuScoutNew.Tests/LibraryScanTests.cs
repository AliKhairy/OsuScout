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
            db.Database.EnsureCreated();
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

            // rosu-pp reads lazer's extension-less files too: a failed read would leave 0 stars.
            Assert.True(l.StarRating > 0, $"{name}: no star rating from {l.FilePath}");
            Assert.False(string.IsNullOrEmpty(l.Tags), $"{name}: no tags");
        }
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
