using OsuScout;
using OsuScoutNew.Services;

namespace OsuScoutNew.Tests;

// The map list's sort, as the column headers set it.
public class MapSortTests
{
    private static BeatmapRecord Map(string title, double stars, double bpm, double? cs = 4) =>
        new BeatmapRecord { Title = title, StarRating = stars, BPM = bpm, CS = cs };

    private static List<string> Sorted(IEnumerable<BeatmapRecord> maps, params (string, bool)[] columns)
    {
        var list = maps.ToList();
        var sort = new MapSort(columns);
        list.Sort((a, b) => sort.Compare(a, b));
        return list.Select(m => m.Title).ToList();
    }

    [Fact]
    public void ALaterColumnOnlyBreaksTiesOfAnEarlierOne()
    {
        var maps = new[] { Map("a", 6, 180), Map("b", 5, 200), Map("c", 6, 220), Map("d", 5, 150) };

        // Stars ascending (a header click), then BPM descending (Shift-click twice).
        var order = Sorted(maps, ("StarRating", false), ("BPM", true));

        Assert.Equal(new[] { "b", "d", "c", "a" }, order);
    }

    [Fact]
    public void MapsWithoutCsYetComeFirst()
    {
        var maps = new[] { Map("a", 6, 180, 4.2), Map("b", 5, 200, null), Map("c", 6, 220, 3.5) };

        Assert.Equal(new[] { "b", "c", "a" }, Sorted(maps, ("CS", false)));
    }

    [Fact]
    public void AColumnItCantSortByIsIgnored()
    {
        Assert.True(new MapSort(new[] { ("NotAColumn", false) }).IsEmpty);
        Assert.False(new MapSort(new[] { ("NotAColumn", false), ("Title", false) }).IsEmpty);
    }
}
