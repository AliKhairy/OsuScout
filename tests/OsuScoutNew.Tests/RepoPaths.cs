namespace OsuScoutNew.Tests;

// Locates files checked into the repo, so tests can use real maps (e.g. the
// parity fixtures) without copying them into the test project.
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string ParityFixtures => Path.Combine(Root, "parity", "fixtures");

    public static string ModelAssets => Path.Combine(Root, "OsuScoutNew", "Assets");

    // Walk up from the test binary (tests/OsuScoutNew.Tests/bin/<cfg>/<tfm>) until
    // the folder holding the solution file.
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "OsuScoutNew.sln")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException($"No OsuScoutNew.sln above {AppContext.BaseDirectory}");
    }
}
