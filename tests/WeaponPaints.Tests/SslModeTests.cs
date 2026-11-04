using MySqlConnector;
using Xunit;

namespace WeaponPaints.Tests;

public class SslModeTests
{
    [Theory]
    [InlineData("None", MySqlSslMode.None)]
    [InlineData("Preferred", MySqlSslMode.Preferred)]
    [InlineData("required", MySqlSslMode.Required)]
    [InlineData(" VerifyCA ", MySqlSslMode.VerifyCA)]
    [InlineData("VERIFYFULL", MySqlSslMode.VerifyFull)]
    public void ParsesNamesCaseInsensitive(string value, MySqlSslMode expected)
    {
        Assert.Equal(expected, Utility.ParseSslMode(value, out var valid));
        Assert.True(valid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIsPreferredAndValid(string? value)
    {
        Assert.Equal(MySqlSslMode.Preferred, Utility.ParseSslMode(value, out var valid));
        Assert.True(valid);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("2")]
    [InlineData("99")]
    [InlineData("Preferred,None")]
    public void UnknownFallsBackToPreferred(string value)
    {
        Assert.Equal(MySqlSslMode.Preferred, Utility.ParseSslMode(value, out var valid));
        Assert.False(valid);
    }

    [Fact]
    public void ConfigDefaultIsPreferred()
    {
        Assert.Equal(MySqlSslMode.Preferred, Utility.ParseSslMode(new WeaponPaintsConfig().DatabaseSslMode, out var valid));
        Assert.True(valid);
    }
}
