using System.Security.Cryptography;
using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

// Builds folders laid out like a lazer data folder: client.realm and files/a/ab/<sha256>.
internal static class FakeLazer
{
    public static void MakeDataFolder(string folder)
    {
        File.WriteAllBytes(Path.Combine(folder, "client.realm"), Array.Empty<byte>());
        Directory.CreateDirectory(Path.Combine(folder, "files"));
    }

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    // Stores content the way lazer does and returns its absolute path.
    public static string AddFile(string dataFolder, byte[] content)
    {
        string path = Path.Combine(dataFolder, "files", LazerFilesSource.StoragePath(Hash(content)));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, content);
        return path;
    }
}
