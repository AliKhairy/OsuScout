using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OsuScoutNew.Services
{
    // What the user had the app set to, so it comes back the same after a restart.
    public class AppSettings
    {
        // Only differs from auto-detection when the user picked a folder with ⚙ DIR.
        public string SongsFolder { get; set; }

        public string SearchText { get; set; } = "";
        public string TagText { get; set; } = "";

        // null = that end of the slider is left open ("no limit"). Storing null rather than the
        // slider's end value keeps meaning "no limit" even if a slider's range changes later.
        public double? MinStars { get; set; }
        public double? MaxStars { get; set; }
        public double? MinBpm { get; set; }
        public double? MaxBpm { get; set; }
        public double? MinLength { get; set; }
        public double? MaxLength { get; set; }

        // First launch: hardest maps first.
        public List<SortSetting> Sort { get; set; } = new List<SortSetting>
        {
            new SortSetting { Column = "StarRating", Descending = true }
        };
    }

    public class SortSetting
    {
        public string Column { get; set; }
        public bool Descending { get; set; }
    }

    public static class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsuScout", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
            }
            catch
            {
                // A corrupt settings file just means starting from the defaults.
            }
            return new AppSettings();
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Losing the saved filters isn't worth crashing the app over.
            }
        }
    }
}
