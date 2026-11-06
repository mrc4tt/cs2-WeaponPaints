using Xunit;

namespace WeaponPaints.Tests;

public class StaleGamedataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-gamedata-" + Guid.NewGuid().ToString("N"));
    private readonly string _module;
    private readonly string _stale;
    private readonly string _gamedata;

    public StaleGamedataTests()
    {
        var css = Path.Combine(_root, "addons", "counterstrikesharp");
        _module = Path.Combine(css, "plugins", "WeaponPaints");
        _stale = Path.Combine(_module, "gamedata", "weaponpaints.json");
        _gamedata = Path.Combine(css, "gamedata", "weaponpaints.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_stale)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_gamedata)!);
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void DeletesTheCopyOnceTheRealFileExists()
    {
        File.WriteAllText(_stale, "{}");
        File.WriteAllText(_gamedata, "{}");
        Assert.Equal(Path.GetFullPath(_stale), Utility.RemoveStalePluginFolderGamedata(_module, _gamedata));
        Assert.False(File.Exists(_stale));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_stale)));
        Assert.True(File.Exists(_gamedata));
    }

    [Fact]
    public void KeepsTheCopyWhenTheRealFileIsMissing()
    {
        File.WriteAllText(_stale, "{}");
        Assert.Null(Utility.RemoveStalePluginFolderGamedata(_module, _gamedata));
        Assert.True(File.Exists(_stale));
    }

    [Fact]
    public void KeepsAFolderWithOtherFiles()
    {
        File.WriteAllText(_stale, "{}");
        File.WriteAllText(Path.Combine(_module, "gamedata", "other.json"), "{}");
        File.WriteAllText(_gamedata, "{}");
        Assert.NotNull(Utility.RemoveStalePluginFolderGamedata(_module, _gamedata));
        Assert.True(Directory.Exists(Path.GetDirectoryName(_stale)));
    }
}
