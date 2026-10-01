namespace OsuScoutNew.Core
{
    public enum OsuClient { Stable, Lazer }

    // Where .osu files come from. Everything after this (parsing, features, the
    // model, star ratings) is the same whichever client the files belong to.
    public interface IBeatmapSource
    {
        OsuClient Kind { get; }

        // The Songs folder for stable, the data folder for lazer.
        string Root { get; }

        // Absolute paths of every map file under Root.
        IEnumerable<string> EnumerateMapFiles();

        // Calls onNewMapFile with the path of each map file that appears under Root,
        // once it is complete and readable. Dispose the result to stop watching.
        IDisposable CreateWatcher(Action<string> onNewMapFile);
    }
}
