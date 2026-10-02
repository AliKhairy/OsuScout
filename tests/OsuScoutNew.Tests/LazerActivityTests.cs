using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

// lazer's window title, as osu.Game OsuGame.updateWindowTitle writes it.
public class LazerActivityTests
{
    [Theory]
    [InlineData("osu! - Camellia - Xeroa [PREON]")]
    [InlineData("osu! - DJ TOTTO - Crystalia [SB's Extreme]")]
    [InlineData("osu! - Various Artists - Stream Practice Set [Sidetracked Day - 188 bpm]")]
    public void PlayingAMapPutsItInTheTitle(string title)
    {
        Assert.True(LazerActivity.IsPlaying(title));
    }

    [Theory]
    [InlineData("osu!")]
    [InlineData("")]
    [InlineData(null)]
    // The editor shows the map's file instead.
    [InlineData("osu! - Camellia - Xeroa (Ryuusei Aika) [PREON].osu")]
    [InlineData("osu! - new beatmap")]
    // Some other program's window.
    [InlineData("Notepad - notes.txt")]
    public void AnythingElseIsNotPlaying(string title)
    {
        Assert.False(LazerActivity.IsPlaying(title));
    }
}
