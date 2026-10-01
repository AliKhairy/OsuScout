using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using OsuScoutNew.Core;

namespace OsuScout
{
    // 1. The blueprint for a single row in our database
    public class BeatmapRecord
    {
        [Key] // Tells SQLite this is the primary key
        public int Id { get; set; }

        public string FilePath { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string Version { get; set; }
        public string Tags { get; set; }
        public double StarRating { get; set; }
        public string BeatmapID { get; set; }
        public double BPM { get; set; } 
        public int LengthSeconds { get; set; }
    }

    // 2. The Database Context (The actual connection engine)
    public class OsuDbContext : DbContext
    {
        // Each client's library lives in its own file, so they can never mix.
        private readonly OsuClient _client;

        public OsuDbContext(OsuClient client)
        {
            _client = client;
        }

        public DbSet<BeatmapRecord> Beatmaps { get; set; }

        // Stable keeps the original name, so existing users keep their library.
        public static string FileName(OsuClient client) =>
            client == OsuClient.Lazer ? "osuscout-lazer.db" : "osuscout.db";

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string folder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "OsuScout");
            System.IO.Directory.CreateDirectory(folder);
            string dbPath = System.IO.Path.Combine(folder, FileName(_client));
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }
}
