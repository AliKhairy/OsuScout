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

        // Added after the first release, so null in rows an older version stored until
        // OsuLibraryService.FillMissingDetailsAsync reads them from the map.
        public string Mapper { get; set; }
        public double? CS { get; set; }
        public double? AR { get; set; }
        public double? OD { get; set; }
        public double? HP { get; set; }
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

        // Where the library files live. Only tests change it, so they never touch the real ones.
        public static string DataFolder { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "OsuScout");

        // Columns added since the first release, which a library created by an older version lacks.
        private static readonly (string Name, string AddSql)[] AddedColumns =
        {
            ("Mapper", "ALTER TABLE Beatmaps ADD COLUMN \"Mapper\" TEXT"),
            ("CS", "ALTER TABLE Beatmaps ADD COLUMN \"CS\" REAL"),
            ("AR", "ALTER TABLE Beatmaps ADD COLUMN \"AR\" REAL"),
            ("OD", "ALTER TABLE Beatmaps ADD COLUMN \"OD\" REAL"),
            ("HP", "ALTER TABLE Beatmaps ADD COLUMN \"HP\" REAL"),
        };

        // Creates the library, or brings one an older version created up to date: EnsureCreated
        // never touches an existing database, and the app has no migrations. Added columns start
        // empty (null) and are filled in from the maps by FillMissingDetailsAsync.
        public void EnsureSchema()
        {
            Database.EnsureCreated();
            var existing = Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Beatmaps')").ToList();
            foreach (var (name, addSql) in AddedColumns)
            {
                if (!existing.Contains(name))
                    Database.ExecuteSqlRaw(addSql);
            }
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string folder = DataFolder;
            System.IO.Directory.CreateDirectory(folder);
            string dbPath = System.IO.Path.Combine(folder, FileName(_client));
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }
}
