using System.Diagnostics;

namespace OsuScoutNew.Core
{
    // Decisions that depend on which osu! client the user plays.
    public static class GameClients
    {
        // The process names both clients run under. Settings can override it.
        public static readonly IReadOnlyList<string> DefaultProcessNames = new[] { "osu!" };

        // Which client to start with when the user has never chosen: the only one that
        // is installed, or null to ask when both are. With neither, stable, which is
        // what the app always did (it then asks for the Songs folder).
        public static OsuClient? PickOnFirstRun(bool stableFound, bool lazerFound) =>
            (stableFound, lazerFound) switch
            {
                (true, true) => null,
                (false, true) => OsuClient.Lazer,
                _ => OsuClient.Stable
            };

        // What to paste into song select. lazer matches a lone number against beatmap
        // IDs (osu.Game BeatmapCarouselFilterMatching), which lands on exactly this map;
        // stable keeps the artist/title/difficulty search it always used, as does a map
        // with no online ID.
        public static string SongSelectSearch(OsuClient client, string beatmapId, string artist, string title, string version) =>
            client == OsuClient.Lazer && int.TryParse(beatmapId, out int id) && id > 0
                ? id.ToString()
                : $"{artist} {title} {version}";

        // Both clients run as osu!.exe. lazer is built on osu-framework, so its install
        // folder holds osu.Framework.dll; stable's never does.
        public static bool IsLazerInstall(string exeFolder) =>
            !string.IsNullOrEmpty(exeFolder) && File.Exists(Path.Combine(exeFolder, "osu.Framework.dll"));

        // The running game for this client, or null. When the executable can't be
        // inspected (access denied), a process with the right name is the best guess.
        public static Process FindRunningGame(OsuClient client, IEnumerable<string> processNames)
        {
            Process fallback = null;
            foreach (string name in processNames ?? DefaultProcessNames)
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    OsuClient? actual = ClientOf(process);
                    if (actual == client) return process;
                    if (actual == null) fallback ??= process;
                }
            }
            return fallback;
        }

        private static OsuClient? ClientOf(Process process)
        {
            try
            {
                string exe = process.MainModule?.FileName;
                if (exe == null) return null;
                return IsLazerInstall(Path.GetDirectoryName(exe)) ? OsuClient.Lazer : OsuClient.Stable;
            }
            catch
            {
                return null;
            }
        }
    }
}
