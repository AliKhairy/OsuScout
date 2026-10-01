using System.Collections.Concurrent;
using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

public class StableSongsSourceTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    // How long to wait before concluding that a watcher reported nothing.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void IsAStableSourceRootedAtTheSongsFolder()
    {
        var source = new StableSongsSource(@"C:\osu!\Songs");

        Assert.Equal(OsuClient.Stable, source.Kind);
        Assert.Equal(@"C:\osu!\Songs", source.Root);
    }

    [Fact]
    public void EnumeratesEveryOsuFileInEveryMapsetFolder()
    {
        using var songs = new TempFolder();
        var expected = new[]
        {
            songs.Write("123 Artist - Title/Artist - Title (Mapper) [Easy].osu"),
            songs.Write("123 Artist - Title/Artist - Title (Mapper) [Hard].osu"),
            songs.Write("456 Other - Song/nested/deep.osu"),
            songs.Write("loose.osu"),
        };
        songs.Write("123 Artist - Title/Artist - Title (Mapper).osb", "[Events]");
        songs.Write("123 Artist - Title/audio.mp3", "ID3");
        songs.Write("123 Artist - Title/bg.jpg", "");

        var found = new StableSongsSource(songs.Path).EnumerateMapFiles().ToList();

        Assert.Equal(expected.OrderBy(p => p), found.OrderBy(p => p));
        Assert.All(found, p => Assert.True(System.IO.Path.IsPathFullyQualified(p)));
    }

    [Fact]
    public async Task WatcherReportsANewMapOnceItIsWritten()
    {
        using var songs = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(songs.Path, "789 New - Map"));
        var reported = new ConcurrentQueue<string>();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (new StableSongsSource(songs.Path).CreateWatcher(path =>
        {
            reported.Enqueue(path);
            first.TrySetResult(path);
        }))
        {
            string map = songs.Write("789 New - Map/New - Map (Mapper) [Insane].osu");

            Assert.Equal(map, await first.Task.WaitAsync(EventTimeout));
            await Task.Delay(QuietPeriod);
        }

        Assert.Single(reported);
    }

    [Fact]
    public async Task WatcherIgnoresFilesThatAreNotMaps()
    {
        using var songs = new TempFolder();
        Directory.CreateDirectory(System.IO.Path.Combine(songs.Path, "789 New - Map"));
        var reported = new ConcurrentQueue<string>();

        using (new StableSongsSource(songs.Path).CreateWatcher(reported.Enqueue))
        {
            songs.Write("789 New - Map/audio.mp3", "ID3");
            songs.Write("789 New - Map/New - Map (Mapper).osb", "[Events]");
            await Task.Delay(QuietPeriod);
        }

        Assert.Empty(reported);
    }

    [Fact]
    public async Task DisposedWatcherStopsReporting()
    {
        using var songs = new TempFolder();
        var reported = new ConcurrentQueue<string>();

        new StableSongsSource(songs.Path).CreateWatcher(reported.Enqueue).Dispose();
        songs.Write("late.osu");
        await Task.Delay(QuietPeriod);

        Assert.Empty(reported);
    }
}
