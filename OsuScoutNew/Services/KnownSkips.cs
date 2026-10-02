using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OsuScoutNew.Core;

namespace OsuScoutNew.Services
{
    // Files a scan never needs to open again: lazer files that aren't maps (audio, images,
    // skins) and maps for another mode. Only for sources whose files never change
    // (IBeatmapSource.FilesAreImmutable): lazer names each file after the hash of its content,
    // so a name always means the same content. Without this, every launch opened all ~58,000
    // files in lazer's store, which took two minutes on a cold cache.
    // One file name per line, next to the library databases. Deleting it is always safe;
    // the next scan just rebuilds it.
    public static class KnownSkips
    {
        private static readonly object Lock = new object();

        private static string PathFor(OsuClient client) =>
            Path.Combine(OsuScout.OsuDbContext.DataFolder, client == OsuClient.Lazer ? "lazer-files-to-skip.txt" : "stable-files-to-skip.txt");

        public static HashSet<string> Load(OsuClient client)
        {
            lock (Lock)
            {
                try
                {
                    string path = PathFor(client);
                    if (File.Exists(path))
                        return new HashSet<string>(File.ReadLines(path).Where(l => l.Length > 0), StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    // Unreadable: the scan just opens those files again.
                }
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void Add(OsuClient client, IEnumerable<string> fileNames)
        {
            var names = fileNames.ToList();
            if (names.Count == 0) return;
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(OsuScout.OsuDbContext.DataFolder);
                    File.AppendAllLines(PathFor(client), names);
                }
                catch
                {
                    // Not remembering only costs time on the next launch.
                }
            }
        }
    }
}
