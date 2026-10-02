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

        // True when a file under a given name can never change (lazer names each file by
        // the hash of its content), so what was learned about it can be remembered.
        bool FilesAreImmutable { get; }

        // Every file under Root that might be a map, found without opening any of them.
        IEnumerable<string> EnumerateCandidateFiles();

        // Whether a candidate really is a map. May read the file.
        bool IsMapFile(string path);

        // Absolute paths of every map file under Root.
        IEnumerable<string> EnumerateMapFiles();

        // Calls onNewMapFile with the path of each map file that appears under Root,
        // once it is complete and readable. Dispose the result to stop watching.
        IDisposable CreateWatcher(Action<string> onNewMapFile);
    }
}
