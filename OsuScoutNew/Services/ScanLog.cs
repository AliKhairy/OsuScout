using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace OsuScoutNew.Services
{
    // Breadcrumbs for a scan that takes the whole app down. Running out of memory, or a crash
    // inside native code (star rating is Rust), ends the process without an exception anything
    // can catch, so the only record of which maps were being read is what reached the disk first.
    //
    // A map gets a START line when a worker picks it up and a DONE line when it is finished,
    // flushed to the OS straight away so they survive the process dying. Every 2 seconds a MEM
    // line records memory use, and a SLOW line names any map still running after 15 seconds.
    // On the next launch RecoverFromCrash() reads a log with no END line, finds the maps that
    // were mid-scan and puts them on the skip list, so one bad map can't crash every launch.
    //
    // Paths are written relative to the Songs folder: the full path contains the user's Windows
    // username, and this file is meant to be attached to public GitHub issues.
    public sealed class ScanLog : IDisposable
    {
        // Next to the library databases (%LOCALAPPDATA%\OsuScout), and moved with them by tests.
        private static string Folder => OsuScout.OsuDbContext.DataFolder;
        private static string LogPath => Path.Combine(Folder, "scan.log");
        public static string PreviousLogPath => Path.Combine(Folder, "scan-previous.log");
        private static string SkipListPath => Path.Combine(Folder, "skipped-maps.txt");

        // Normal maps take milliseconds; the slowest of 9,684 real maps took 2.5 s.
        private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(15);
        private static readonly object SkipListLock = new object();
        private static ScanLog _current;

        private readonly object _writeLock = new object();
        private readonly StreamWriter _writer;
        private readonly string _songsRoot;
        private readonly ConcurrentDictionary<string, long> _inFlight = new ConcurrentDictionary<string, long>();
        private readonly HashSet<string> _reportedSlow = new HashSet<string>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Timer _watchdog;
        private int _done;
        private bool _ended;

        public ScanLog(string kind, string songsRoot, int filesInSongs, int filesToScan)
        {
            _songsRoot = songsRoot;
            try
            {
                Directory.CreateDirectory(Folder);
                // FileShare.Read so the log can be opened while a scan is still running.
                _writer = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                                           new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                // Logging must never stop a scan (e.g. a second scan while the first holds the file).
                _writer = null;
            }

            var inv = CultureInfo.InvariantCulture;
            string version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                     ?.InformationalVersion.Split('+')[0] ?? "?";
            bool nonEnglishPath = songsRoot != null && songsRoot.Any(c => c > 127);
            double ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
            Write(string.Format(inv, "Scoutsu {0} {1} started {2:yyyy-MM-dd HH:mm:ss}", version, kind, DateTime.Now));
            Write(string.Format(inv, ".osu files in Songs folder: {0:N0}, to process now: {1:N0}", filesInSongs, filesToScan));
            Write(string.Format(inv, "Songs path has non-English characters: {0}", nonEnglishPath ? "yes" : "no"));
            Write(string.Format(inv, "Windows language: {0}, RAM: {1:F1} GB, CPU threads: {2}",
                                CultureInfo.CurrentCulture.Name, ramGb, Environment.ProcessorCount));

            _current = this;
            _watchdog = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public void Started(string path)
        {
            _inFlight[path] = _clock.ElapsedTicks;
            Write(Line("START", Relative(path)));
        }

        public void Finished(string path)
        {
            _inFlight.TryRemove(path, out _);
            Interlocked.Increment(ref _done);
            Write(Line("DONE ", Relative(path)));
        }

        public void Complete() => End(string.Format(CultureInfo.InvariantCulture, "finished, {0:N0} maps", _done));

        // A normal exit mid-scan is not a crash: without this marker the next launch would skip
        // whatever happened to be in flight every time someone closed the app during a scan.
        public static void MarkAppClosed() => _current?.End("app closed");

        public void Dispose()
        {
            // Reaching Dispose without Complete means a managed error ended the scan, which the
            // app already reported. Only a process that died outright leaves no END line.
            End("stopped early");
            _watchdog?.Dispose();
            lock (_writeLock) { _writer?.Dispose(); }
            if (_current == this) _current = null;
        }

        // Maps a previous scan was reading when the process died, or an empty list if the last
        // scan ended normally. Moves that log aside as scan-previous.log for bug reports.
        public static List<string> RecoverFromCrash()
        {
            var open = new List<string>();
            try
            {
                if (!File.Exists(LogPath)) return open;
                var lines = File.ReadAllLines(LogPath);
                if (lines.Length == 0 || lines.Any(l => l.StartsWith("END "))) return open;

                var started = new List<string>();
                var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines)
                {
                    if (line.StartsWith("START ")) started.Add(PathOf(line));
                    else if (line.StartsWith("DONE  ")) finished.Add(PathOf(line));
                }
                open = started.Where(p => !finished.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                File.Move(LogPath, PreviousLogPath, overwrite: true);
                AddToSkipList(open);
            }
            catch
            {
                // A damaged log must not stop the app from starting.
            }
            return open;
        }

        public static HashSet<string> LoadSkipList()
        {
            try
            {
                lock (SkipListLock)
                {
                    if (File.Exists(SkipListPath))
                        return new HashSet<string>(File.ReadAllLines(SkipListPath).Where(l => l.Length > 0),
                                                   StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { }
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // Skip-list entries are relative to the Songs folder, like the log.
        public static bool IsSkipped(HashSet<string> skipList, string songsRoot, string path)
            => skipList.Contains(Relative(songsRoot, path));

        private void Watch()
        {
            long privateMb;
            using (var p = Process.GetCurrentProcess()) privateMb = p.PrivateMemorySize64 / (1024 * 1024);
            Write(Line("MEM  ", string.Format(CultureInfo.InvariantCulture,
                "private {0:N0} MB, {1:N0} maps done, {2} in progress", privateMb, _done, _inFlight.Count)));

            long now = _clock.ElapsedTicks;
            foreach (var entry in _inFlight)
            {
                double seconds = (now - entry.Value) / (double)Stopwatch.Frequency;
                if (seconds < SlowAfter.TotalSeconds) continue;
                lock (_reportedSlow) { if (!_reportedSlow.Add(entry.Key)) continue; }
                string rel = Relative(entry.Key);
                Write(Line("SLOW ", string.Format(CultureInfo.InvariantCulture, "running for {0:F0}s: {1}", seconds, rel)));
                // Skip it from now on even if the app survives, so a map that hangs or eats memory
                // isn't retried on every launch.
                AddToSkipList(new[] { rel });
            }
        }

        private void End(string how)
        {
            lock (_writeLock)
            {
                if (_ended) return;
                _ended = true;
            }
            Write(Line("END  ", how));
            if (_current == this) _current = null;
        }

        private string Line(string tag, string text) =>
            string.Format(CultureInfo.InvariantCulture, "{0} {1,8:F1}s {2}", tag, _clock.Elapsed.TotalSeconds, text);

        private void Write(string line)
        {
            if (_writer == null) return;
            try { lock (_writeLock) { _writer.WriteLine(line); } } catch { }
        }

        private string Relative(string path) => Relative(_songsRoot, path);

        private static string Relative(string root, string path)
        {
            if (!string.IsNullOrEmpty(root))
            {
                string rel = Path.GetRelativePath(root, path);
                if (!rel.StartsWith("..") && !Path.IsPathRooted(rel)) return rel;
            }
            return Path.GetFileName(path);   // never write a full path: it contains the username
        }

        // "START     12.3s 123 Artist - Title\map.osu" -> "123 Artist - Title\map.osu"
        private static string PathOf(string line)
        {
            int s = line.IndexOf("s ", 6, StringComparison.Ordinal);
            return s < 0 ? "" : line.Substring(s + 2);
        }

        private static void AddToSkipList(IEnumerable<string> relativePaths)
        {
            var list = relativePaths.Where(p => p.Length > 0).ToList();
            if (list.Count == 0) return;
            try
            {
                lock (SkipListLock)
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllLines(SkipListPath, list);
                }
            }
            catch { }
        }
    }
}
