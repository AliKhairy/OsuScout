using System.Collections.Generic;

namespace OsuScoutNew.Services
{
    // An inclusive range. An infinite end means "no limit" and is left out of the query.
    public readonly record struct Bounds(double Min, double Max)
    {
        public static Bounds Any => new Bounds(double.NegativeInfinity, double.PositiveInfinity);
    }

    // What the map list is narrowed down to.
    public class MapFilter
    {
        // Matched against artist, title, difficulty name and mapper, ignoring case.
        public string SearchText { get; set; } = "";
        public List<string> RequiredTags { get; set; } = new List<string>();
        public List<string> ExcludedTags { get; set; } = new List<string>();

        public Bounds Stars { get; set; } = Bounds.Any;
        public Bounds Bpm { get; set; } = Bounds.Any;
        public Bounds LengthMinutes { get; set; } = Bounds.Any;
        public Bounds CS { get; set; } = Bounds.Any;
        public Bounds AR { get; set; } = Bounds.Any;
        public Bounds OD { get; set; } = Bounds.Any;
        public Bounds HP { get; set; } = Bounds.Any;
    }
}
