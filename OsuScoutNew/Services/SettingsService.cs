using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using OsuScoutNew.Core;

namespace OsuScoutNew.Services
{
    // What the user had the app set to, so it comes back the same after a restart.
    public class AppSettings
    {
        // Which client's library is shown. null only on a first run, before it is picked
        // (see SettingsService.Load: settings saved by older versions mean stable).
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public OsuClient? Client { get; set; }

        // Only differs from auto-detection when the user picked a folder with ⚙ DIR.
        public string SongsFolder { get; set; }
        public string LazerDataFolder { get; set; }

        // Keep the window above the game. Off, it behaves like a normal window, which suits a
        // second monitor and avoids two cursors over a game that draws its own.
        public bool AlwaysOnTop { get; set; } = true;

        // Process names to look for when focusing the game. Both clients run as "osu!".
        public List<string> GameProcessNames { get; set; } = GameClients.DefaultProcessNames.ToList();

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

        // OsuClassifier.ModelId of the model that produced the library's stored tags.
        // A different model on launch means those tags are stale (see RetagLibraryAsync).
        // null for libraries tagged before this was recorded, which are re-tagged once.
        // Each client's library is tagged separately: TaggedWithModel is stable's.
        public string TaggedWithModel { get; set; }
        public string LazerTaggedWithModel { get; set; }

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
                {
                    var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
                    // Saved before lazer support existed: the user was on stable all along.
                    settings.Client ??= OsuClient.Stable;
                    return settings;
                }
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
