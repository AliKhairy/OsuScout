using System.Collections.Concurrent;
using System.Text;

namespace OsuScoutNew.Core
{
    // osu!lazer stores every file it imports under files/, named after the SHA-256 of
    // its content with no extension (files/a/ab/ab12…), so maps sit next to audio and
    // images. A map is recognised by its first line instead of its name.
    // Read-only: nothing here writes to, moves or locks out lazer's files.
    public class LazerFilesSource : IBeatmapSource
    {
        private const string Header = "osu file format v";
        private const int SniffBytes = 64;
        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        // lazer may open its files at any moment; never stop it reading or deleting them.
        private const FileShare LetLazerIn = FileShare.ReadWrite | FileShare.Delete;

        public LazerFilesSource(string dataFolder)
        {
            Root = dataFolder;
        }

        public OsuClient Kind => OsuClient.Lazer;
        public string Root { get; }
        public string FilesFolder => Path.Combine(Root, "files");

        public IEnumerable<string> EnumerateMapFiles() =>
            Directory.EnumerateFiles(FilesFolder, "*", SearchOption.AllDirectories)
                .AsParallel()
                .Where(path => IsStoredFileName(Path.GetFileName(path)) && IsMapFile(path));

        // No filter: the files have no extension. lazer either hard-links a file straight
        // to its final name (Created) or writes _<hash>_<guid> and renames it (Renamed).
        public IDisposable CreateWatcher(Action<string> onNewMapFile)
        {
            var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var watcher = new FileSystemWatcher(FilesFolder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true
            };

            async void Consider(string path)
            {
                if (!IsStoredFileName(Path.GetFileName(path)) || !seen.TryAdd(path, 0)) return;
                if (await FileReadiness.WaitUntilReadableAsync(path, LetLazerIn) && IsMapFile(path))
                    onNewMapFile(path);
            }

            watcher.Created += (_, e) => Consider(e.FullPath);
            watcher.Renamed += (_, e) => Consider(e.FullPath);
            return watcher;
        }

        // Where lazer stores a file with this hash, relative to files/ (osu.Game ModelExtensions.GetStoragePath).
        public static string StoragePath(string hash) => Path.Combine(hash[..1], hash[..2], hash);

        // A finished file is named by its SHA-256: 64 hex digits. Anything else under
        // files/ is a write in progress (_<hash>_<guid>) and is skipped.
        public static bool IsStoredFileName(string name) =>
            name.Length == 64 && name.All(Uri.IsHexDigit);

        public static bool IsMapFile(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LetLazerIn);
                var buffer = new byte[SniffBytes];
                int read = stream.ReadAtLeast(buffer, SniffBytes, throwOnEndOfStream: false);
                return HasOsuHeader(buffer.AsSpan(0, read));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // True for "osu file format v…", after an optional UTF-8 BOM and any leading whitespace.
        public static bool HasOsuHeader(ReadOnlySpan<byte> start)
        {
            if (start.StartsWith(Utf8Bom)) start = start[Utf8Bom.Length..];
            return Encoding.UTF8.GetString(start).TrimStart().StartsWith(Header, StringComparison.Ordinal);
        }
    }
}
