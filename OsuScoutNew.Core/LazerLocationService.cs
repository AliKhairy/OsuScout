namespace OsuScoutNew.Core
{
    // Finds osu!lazer's data folder. Only ever reads from it.
    public static class LazerLocationService
    {
        // Where lazer keeps its data unless the user moved it: %APPDATA%\osu on Windows.
        // Elsewhere .NET maps LocalApplicationData to the folders lazer uses
        // (~/.local/share on Linux, ~/Library/Application Support on macOS).
        public static string DefaultDataFolder => Path.Combine(
            Environment.GetFolderPath(OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.ApplicationData
                : Environment.SpecialFolder.LocalApplicationData),
            "osu");

        public static string FindDataFolder(string userSetting = null) =>
            FindDataFolder(userSetting, DefaultDataFolder);

        // A folder the user picked wins; then the one storage.ini says lazer moved its
        // data to; then the default. Returns null when none of them is a lazer data folder.
        public static string FindDataFolder(string userSetting, string defaultFolder)
        {
            if (IsDataFolder(userSetting)) return userSetting;

            string moved = ReadMovedDataFolder(defaultFolder);
            if (IsDataFolder(moved)) return moved;

            return IsDataFolder(defaultFolder) ? defaultFolder : null;
        }

        // A moved install can leave an old files/ behind in the default folder, so
        // client.realm is required as well.
        public static bool IsDataFolder(string folder) =>
            !string.IsNullOrEmpty(folder)
            && File.Exists(Path.Combine(folder, "client.realm"))
            && Directory.Exists(Path.Combine(folder, "files"));

        // When the user moves lazer's data, lazer leaves a storage.ini in the default
        // folder with a "FullPath = <new folder>" line (osu.Game StorageConfigManager).
        public static string ReadMovedDataFolder(string defaultFolder)
        {
            try
            {
                string ini = Path.Combine(defaultFolder, "storage.ini");
                if (!File.Exists(ini)) return null;

                foreach (string line in File.ReadLines(ini))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0 || line[..eq].Trim() != "FullPath") continue;

                    string value = line[(eq + 1)..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return null;
        }
    }
}
