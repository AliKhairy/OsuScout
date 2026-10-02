namespace OsuScoutNew.Core
{
    // osu! stable keeps each mapset in its own folder under Songs, as plain .osu files.
    public class StableSongsSource : IBeatmapSource
    {
        public StableSongsSource(string songsFolder)
        {
            Root = songsFolder;
        }

        public OsuClient Kind => OsuClient.Stable;
        public string Root { get; }

        // The editor saves over a map in place, so nothing about a file can be remembered.
        public bool FilesAreImmutable => false;

        public IEnumerable<string> EnumerateCandidateFiles() =>
            Directory.EnumerateFiles(Root, "*.osu", SearchOption.AllDirectories);

        // The extension already says so.
        public bool IsMapFile(string path) => true;

        public IEnumerable<string> EnumerateMapFiles() => EnumerateCandidateFiles();

        public IDisposable CreateWatcher(Action<string> onNewMapFile)
        {
            var watcher = new FileSystemWatcher(Root)
            {
                Filter = "*.osu",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            watcher.Created += async (_, e) =>
            {
                if (await FileReadiness.WaitUntilReadableAsync(e.FullPath)) onNewMapFile(e.FullPath);
            };

            return watcher;
        }
    }
}
