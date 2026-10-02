using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

public class GameClientsTests
{
    [Theory]
    [InlineData(true, false, OsuClient.Stable)]
    [InlineData(false, true, OsuClient.Lazer)]
    [InlineData(false, false, OsuClient.Stable)]
    public void FirstRunPicksTheOnlyClientInstalled(bool stable, bool lazer, OsuClient expected)
    {
        Assert.Equal(expected, GameClients.PickOnFirstRun(stable, lazer));
    }

    [Fact]
    public void FirstRunAsksWhenBothAreInstalled()
    {
        Assert.Null(GameClients.PickOnFirstRun(true, true));
    }

    [Fact]
    public void LazerSearchesByBeatmapId()
    {
        Assert.Equal("1867787", GameClients.SongSelectSearch(OsuClient.Lazer, "1867787", "Undertale", "Battle Against a True Hero", "Unfair Undyne"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(null)]
    public void LazerFallsBackToTextForMapsWithoutAnId(string beatmapId)
    {
        Assert.Equal("Artist Title Hard", GameClients.SongSelectSearch(OsuClient.Lazer, beatmapId, "Artist", "Title", "Hard"));
    }

    [Fact]
    public void StableKeepsSearchingByText()
    {
        Assert.Equal("Artist Title Hard", GameClients.SongSelectSearch(OsuClient.Stable, "1867787", "Artist", "Title", "Hard"));
    }

    [Fact]
    public void LazerIsRecognisedByOsuFrameworkNextToTheExe()
    {
        using var lazer = new TempFolder();
        using var stable = new TempFolder();
        lazer.Write("osu!.exe", "");
        lazer.Write("osu.Framework.dll", "");
        stable.Write("osu!.exe", "");
        stable.Write("osu!.db", "");

        Assert.True(GameClients.IsLazerInstall(lazer.Path));
        Assert.False(GameClients.IsLazerInstall(stable.Path));
        Assert.False(GameClients.IsLazerInstall(null));
    }
}
