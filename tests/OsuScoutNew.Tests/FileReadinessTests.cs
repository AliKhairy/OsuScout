using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

public class FileReadinessTests
{
    [Fact]
    public async Task AFinishedFileIsReadyAtOnce()
    {
        using var folder = new TempFolder();
        string file = folder.Write("map.osu");

        Assert.True(await FileReadiness.WaitUntilReadableAsync(file, maxRetries: 1, delayMs: 0));
    }

    [Fact]
    public async Task AFileStillBeingWrittenBecomesReadyWhenTheWriterCloses()
    {
        using var folder = new TempFolder();
        string file = System.IO.Path.Combine(folder.Path, "map.osu");
        var writer = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var wait = FileReadiness.WaitUntilReadableAsync(file, maxRetries: 100, delayMs: 50);
        await Task.Delay(300);
        Assert.False(wait.IsCompleted);

        writer.Dispose();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AFileThatNeverAppearsIsNotReady()
    {
        using var folder = new TempFolder();

        Assert.False(await FileReadiness.WaitUntilReadableAsync(System.IO.Path.Combine(folder.Path, "missing.osu"), maxRetries: 3, delayMs: 10));
    }
}
