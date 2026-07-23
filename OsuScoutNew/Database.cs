using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

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
        public DbSet<BeatmapRecord> Beatmaps { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // This creates a tiny file called 'osuscout.db' in the folder where your app runs
            optionsBuilder.UseSqlite("Data Source=osuscout.db");
        }
    }
}
