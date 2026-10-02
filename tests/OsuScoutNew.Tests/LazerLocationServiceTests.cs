using OsuScoutNew.Core;

namespace OsuScoutNew.Tests;

public class LazerLocationServiceTests
{
    [Fact]
    public void FindsTheDefaultFolder()
    {
        using var defaultFolder = new TempFolder();
        FakeLazer.MakeDataFolder(defaultFolder.Path);

        Assert.Equal(defaultFolder.Path, LazerLocationService.FindDataFolder(null, defaultFolder.Path));
    }

    [Fact]
    public void FollowsStorageIniToAMovedFolder()
    {
        using var defaultFolder = new TempFolder();
        using var moved = new TempFolder();
        FakeLazer.MakeDataFolder(moved.Path);
        // A moved install leaves a stale files/ behind in the default folder, but no client.realm.
        Directory.CreateDirectory(Path.Combine(defaultFolder.Path, "files"));
        defaultFolder.Write("storage.ini", $"FullPath = {moved.Path}\n");

        Assert.Equal(moved.Path, LazerLocationService.FindDataFolder(null, defaultFolder.Path));
    }

    [Fact]
    public void FallsBackToTheDefaultWhenStorageIniPointsNowhere()
    {
        using var defaultFolder = new TempFolder();
        FakeLazer.MakeDataFolder(defaultFolder.Path);
        defaultFolder.Write("storage.ini", "FullPath = " + Path.Combine(defaultFolder.Path, "gone") + "\n");

        Assert.Equal(defaultFolder.Path, LazerLocationService.FindDataFolder(null, defaultFolder.Path));
    }

    [Fact]
    public void IgnoresAnEmptyFullPath()
    {
        using var defaultFolder = new TempFolder();
        FakeLazer.MakeDataFolder(defaultFolder.Path);
        defaultFolder.Write("storage.ini", "FullPath =\n");

        Assert.Null(LazerLocationService.ReadMovedDataFolder(defaultFolder.Path));
        Assert.Equal(defaultFolder.Path, LazerLocationService.FindDataFolder(null, defaultFolder.Path));
    }

    [Fact]
    public void AFolderTheUserPickedWins()
    {
        using var defaultFolder = new TempFolder();
        using var picked = new TempFolder();
        FakeLazer.MakeDataFolder(defaultFolder.Path);
        FakeLazer.MakeDataFolder(picked.Path);

        Assert.Equal(picked.Path, LazerLocationService.FindDataFolder(picked.Path, defaultFolder.Path));
    }

    [Fact]
    public void AnInvalidPickedFolderIsSkipped()
    {
        using var defaultFolder = new TempFolder();
        using var picked = new TempFolder();
        FakeLazer.MakeDataFolder(defaultFolder.Path);

        Assert.Equal(defaultFolder.Path, LazerLocationService.FindDataFolder(picked.Path, defaultFolder.Path));
    }

    [Fact]
    public void AFolderWithoutFilesIsNotLazer()
    {
        using var folder = new TempFolder();
        folder.Write("client.realm", "");

        Assert.False(LazerLocationService.IsDataFolder(folder.Path));
        Assert.Null(LazerLocationService.FindDataFolder(null, folder.Path));
    }

    [Fact]
    public void AFolderWithoutClientRealmIsNotLazer()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "files"));

        Assert.False(LazerLocationService.IsDataFolder(folder.Path));
        Assert.Null(LazerLocationService.FindDataFolder(null, folder.Path));
    }

    [Fact]
    public void NoLazerAnywhereGivesNull()
    {
        using var empty = new TempFolder();

        Assert.Null(LazerLocationService.FindDataFolder(null, Path.Combine(empty.Path, "osu")));
    }
}
