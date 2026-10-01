using System.Collections.Concurrent;
using System.Text;
using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

public class LazerFilesSourceTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(1.5);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("osu file format v14\r\n\r\n[General]")]
    [InlineData("osu file format v3\n[General]")]
    [InlineData("\uFEFFosu file format v14\r\n")]
    [InlineData("\r\n\r\n  osu file format v14\r\n")]
    [InlineData("\uFEFF\r\n\tosu file format v128")]
    public void RecognisesAMapHeader(string start)
    {
        Assert.True(LazerFilesSource.HasOsuHeader(Bytes(start)));
    }

    [Theory]
    [InlineData("[Events]\r\n//Background and Video events")] // .osb storyboard
    [InlineData("ID3\u0004\0\0\0\0")]                          // mp3
    [InlineData("")]                                           // empty file
    [InlineData("osu file format")]                            // cut off before the version
    [InlineData("OSU FILE FORMAT V14")]
    public void RejectsAnythingElse(string start)
    {
        Assert.False(LazerFilesSource.HasOsuHeader(Bytes(start)));
    }

    [Fact]
    public void RejectsBinaryFiles()
    {
        // PNG signature.
        Assert.False(LazerFilesSource.HasOsuHeader(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
    }

    [Fact]
    public void StoresAHashUnderItsFirstOneAndTwoCharacters()
    {
        // SHA-256 of an empty file.
        const string hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Assert.Equal(Path.Combine("e", "e3", hash), LazerFilesSource.StoragePath(hash));
    }

    [Theory]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", true)]
    [InlineData("_e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855_6f9619ff-8b86-d011-b42d-00c04fc964ff", false)]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b85", false)]
    [InlineData("xyz0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", false)]
    [InlineData("map.osu", false)]
    public void OnlyHashNamedFilesAreStoredFiles(string name, bool stored)
    {
        Assert.Equal(stored, LazerFilesSource.IsStoredFileName(name));
    }

    [Fact]
    public void IsALazerSourceRootedAtTheDataFolder()
    {
        var source = new LazerFilesSource(@"D:\osu-data");

        Assert.Equal(OsuClient.Lazer, source.Kind);
        Assert.Equal(@"D:\osu-data", source.Root);
        Assert.Equal(Path.Combine(@"D:\osu-data", "files"), source.FilesFolder);
    }

    [Fact]
    public void EnumeratesOnlyTheMapsAmongStoredFiles()
    {
        using var lazer = new TempFolder();
        FakeLazer.MakeDataFolder(lazer.Path);
        var maps = new[]
        {
            FakeLazer.AddFile(lazer.Path, Bytes("osu file format v14\r\n[General]\r\nMode: 0\r\n")),
            FakeLazer.AddFile(lazer.Path, Bytes("\uFEFFosu file format v9\r\n[General]\r\n")),
        };
        FakeLazer.AddFile(lazer.Path, Bytes("[Events]\r\n"));
        FakeLazer.AddFile(lazer.Path, Bytes("ID3\u0004"));
        FakeLazer.AddFile(lazer.Path, Array.Empty<byte>());
        // A write lazer hasn't finished: right content, temporary name.
        string pending = Path.Combine(Path.GetDirectoryName(maps[0]), $"_{Path.GetFileName(maps[0])}_{Guid.NewGuid()}");
        File.Copy(maps[0], pending);

        var found = new LazerFilesSource(lazer.Path).EnumerateMapFiles().ToList();

        Assert.Equal(maps.OrderBy(p => p), found.OrderBy(p => p));
    }

    [Fact]
    public async Task WatcherReportsAMapWrittenToATempNameThenRenamedOnce()
    {
        using var lazer = new TempFolder();
        FakeLazer.MakeDataFolder(lazer.Path);
        byte[] content = Bytes("osu file format v14\r\n[General]\r\n");
        string final = Path.Combine(lazer.Path, "files", LazerFilesSource.StoragePath(FakeLazer.Hash(content)));
        Directory.CreateDirectory(Path.GetDirectoryName(final));
        var reported = new ConcurrentQueue<string>();
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (new LazerFilesSource(lazer.Path).CreateWatcher(path =>
        {
            reported.Enqueue(path);
            first.TrySetResult(path);
        }))
        {
            // What osu-framework's Storage.CreateFileSafely does.
            string temp = Path.Combine(Path.GetDirectoryName(final), $"_{Path.GetFileName(final)}_{Guid.NewGuid()}");
            File.WriteAllBytes(temp, content);
            File.Move(temp, final);

            Assert.Equal(final, await first.Task.WaitAsync(EventTimeout));
            await Task.Delay(QuietPeriod);
        }

        Assert.Single(reported);
    }

    [Fact]
    public async Task WatcherReportsAMapCreatedUnderItsFinalName()
    {
        using var lazer = new TempFolder();
        FakeLazer.MakeDataFolder(lazer.Path);
        byte[] content = Bytes("osu file format v14\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(lazer.Path, "files", LazerFilesSource.StoragePath(FakeLazer.Hash(content)))));
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (new LazerFilesSource(lazer.Path).CreateWatcher(path => first.TrySetResult(path)))
        {
            // Like the hard link lazer makes when it can.
            string path = FakeLazer.AddFile(lazer.Path, content);

            Assert.Equal(path, await first.Task.WaitAsync(EventTimeout));
        }
    }

    [Fact]
    public async Task WatcherIgnoresStoredFilesThatAreNotMaps()
    {
        using var lazer = new TempFolder();
        FakeLazer.MakeDataFolder(lazer.Path);
        byte[] audio = Bytes("ID3\u0004 not a map");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(lazer.Path, "files", LazerFilesSource.StoragePath(FakeLazer.Hash(audio)))));
        var reported = new ConcurrentQueue<string>();

        using (new LazerFilesSource(lazer.Path).CreateWatcher(reported.Enqueue))
        {
            FakeLazer.AddFile(lazer.Path, audio);
            await Task.Delay(QuietPeriod);
        }

        Assert.Empty(reported);
    }
}
