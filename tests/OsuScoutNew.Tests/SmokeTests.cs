namespace OsuScoutNew.Tests;

// Proves the test project builds, runs and can see the repo's fixture maps.
public class SmokeTests
{
    [Theory]
    [InlineData("stream.osu")]
    [InlineData("jump.osu")]
    [InlineData("tech.osu")]
    public void ParityFixtureIsAnOsuFile(string name)
    {
        string path = Path.Combine(RepoPaths.ParityFixtures, name);

        Assert.True(File.Exists(path), $"Missing fixture {path}");
        Assert.StartsWith("osu file format v", File.ReadLines(path).First().TrimStart('﻿').Trim());
    }
}
