namespace OsuScoutNew.Tests;

// A fresh folder per test, deleted afterwards.
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("scoutsu-tests-").FullName;

    // Writes a file at a path relative to the folder, creating its parent folders.
    public string Write(string relativePath, string content = "osu file format v14\n")
    {
        // GetFullPath turns the '/' in relativePath into the platform's separator.
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
