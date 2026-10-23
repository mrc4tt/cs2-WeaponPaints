using Xunit;

namespace WeaponPaints.Tests;

public class KeychainParseTests
{
    [Fact]
    public void FiveFields()
    {
        var k = WeaponSynchronization.ParseKeychain("12;1.5;-2.25;0.5;77");
        Assert.Equal(12u, k.Id);
        Assert.Equal(1.5f, k.OffsetX);
        Assert.Equal(-2.25f, k.OffsetY);
        Assert.Equal(0.5f, k.OffsetZ);
        Assert.Equal(77u, k.Seed);
        Assert.Equal(0f, k.Rotation);
        Assert.Equal(0u, k.Sticker);
        Assert.Equal(0u, k.Highlight);
    }

    [Fact]
    public void SixFieldsReadsRotation()
    {
        var k = WeaponSynchronization.ParseKeychain("12;0;0;0;77;45.5");
        Assert.Equal(45.5f, k.Rotation);
    }

    [Fact]
    public void EightFieldsReadsStickerAndHighlight()
    {
        var k = WeaponSynchronization.ParseKeychain("12;0;0;0;77;0;5021;9");
        Assert.Equal(5021u, k.Sticker);
        Assert.Equal(9u, k.Highlight);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12;0;0;0")]
    [InlineData("abc;0;0;0;1")]
    [InlineData("12;0,5;0;0;1")]
    public void InvalidIsEmptyCharm(string? value)
    {
        var k = WeaponSynchronization.ParseKeychain(value);
        Assert.Equal(0u, k.Id);
        Assert.Equal(0u, k.Seed);
    }

    [Theory]
    [InlineData("12;1.5;-2.25;0.5;77;45.5")]
    [InlineData("12;0;0;0;77;0;5021;9")]
    public void FormatRoundTrips(string value)
    {
        Assert.Equal(value, WeaponSynchronization.FormatKeychain(WeaponSynchronization.ParseKeychain(value)));
    }

    [Fact]
    public void FormatEmptyCharm()
    {
        Assert.Equal("0;0;0;0;0", WeaponSynchronization.FormatKeychain(null));
        Assert.Equal("0;0;0;0;0", WeaponSynchronization.FormatKeychain(new KeyChainInfo()));
    }
}

public class StickerParseTests
{
    [Fact]
    public void SevenFields()
    {
        var s = WeaponSynchronization.ParseSticker("4321;0;0.1;-0.2;0.35;1;90");
        Assert.Equal(4321u, s.Id);
        Assert.Equal(0.35f, s.Wear);
        Assert.Equal(1f, s.Scale);
        Assert.Equal(90f, s.Rotation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4321;0;0;0;0;1")]          // 6 fields
    [InlineData("4321;0;0;0;0;1;90;7")]     // 8 fields
    [InlineData("4321;0;0;0;0,3;1;90")]     // decimal comma
    public void InvalidIsEmptySlot(string? value)
    {
        var s = WeaponSynchronization.ParseSticker(value);
        Assert.NotNull(s);
        Assert.Equal(0u, s.Id);
    }
}
