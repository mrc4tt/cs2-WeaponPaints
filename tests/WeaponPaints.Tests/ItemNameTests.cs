using System.Text;
using Xunit;

namespace WeaponPaints.Tests;

public class ItemNameTests
{
    [Fact]
    public void EmptyAndNullBecomeEmpty()
    {
        Assert.Equal("", WeaponPaints.FitItemName(null));
        Assert.Equal("", WeaponPaints.FitItemName(""));
    }

    [Fact]
    public void ShortNameUnchanged()
    {
        Assert.Equal("My AK", WeaponPaints.FitItemName("My AK"));
        var exact = new string('a', 160);
        Assert.Same(exact, WeaponPaints.FitItemName(exact));
    }

    [Fact]
    public void AsciiCutAt160Bytes()
    {
        Assert.Equal(new string('a', 160), WeaponPaints.FitItemName(new string('a', 161)));
    }

    [Fact]
    public void MultiByteNeverSplitsCodePoint()
    {
        // Cyrillic = 2 bytes each: 128 chars = 256 bytes -> 80 chars.
        var cyr = WeaponPaints.FitItemName(new string('Ж', 128));
        Assert.Equal(new string('Ж', 80), cyr);

        // Emoji = 4 bytes (surrogate pair): 1 + 4n must stop before 160.
        var mixed = "a" + string.Concat(Enumerable.Repeat("😀", 50));
        var fitted = WeaponPaints.FitItemName(mixed);
        Assert.True(Encoding.UTF8.GetByteCount(fitted) <= 160);
        Assert.Equal("a" + string.Concat(Enumerable.Repeat("😀", 39)), fitted);
        Assert.False(char.IsHighSurrogate(fitted[^1]));
    }
}
