using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace WeaponPaints.Tests;

public class ConfigMigrationTests
{
    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void AddsMissingKeysWithDefaults()
    {
        var file = Obj("""{"A":1}""");
        WeaponPaints.AddMissingConfigKeys(file, Obj("""{"A":5,"B":"x"}"""));
        Assert.Equal(1, (int)file["A"]!);
        Assert.Equal("x", (string)file["B"]!);
    }

    [Fact]
    public void RecursesIntoObjectsWithoutReplacingValues()
    {
        var file = Obj("""{"Additional":{"KnifeEnabled":false}}""");
        WeaponPaints.AddMissingConfigKeys(file, Obj("""{"Additional":{"KnifeEnabled":true,"GloveEnabled":true}}"""));
        Assert.False((bool)file["Additional"]!["KnifeEnabled"]!);
        Assert.True((bool)file["Additional"]!["GloveEnabled"]!);
    }

    [Fact]
    public void NeverTouchesArraysOrUnknownKeys()
    {
        var file = Obj("""{"List":[1],"Custom":"keep"}""");
        WeaponPaints.AddMissingConfigKeys(file, Obj("""{"List":[1,2,3]}"""));
        Assert.Single(file["List"]!.AsArray());
        Assert.Equal("keep", (string)file["Custom"]!);
    }

    [Fact]
    public void TypeMismatchKeepsOperatorValue()
    {
        var file = Obj("""{"Additional":"oops"}""");
        WeaponPaints.AddMissingConfigKeys(file, Obj("""{"Additional":{"KnifeEnabled":true}}"""));
        Assert.Equal("oops", (string)file["Additional"]!);
    }

    [Fact]
    public void RealDefaultsFillAnEmptyConfig()
    {
        var defaults = (JsonObject)JsonSerializer.SerializeToNode(new WeaponPaintsConfig())!;
        var file = new JsonObject();
        WeaponPaints.AddMissingConfigKeys(file, defaults);
        Assert.Equal(defaults.ToJsonString(), file.ToJsonString());

        // Defaults are cloned, not shared: mutating the result must not touch the source tree.
        file["ConfigVersion"] = -1;
        Assert.NotEqual(-1, (int)defaults["ConfigVersion"]!);
    }

    [Fact]
    public void ObsoleteKeysNeverIncludeLiveSettings()
    {
        // The migration deletes ObsoleteKeys from operators' files. Database* are the preferred
        // credential source in OnConfigParsed: obsoleting them would wipe servers' DB logins.
        var defaults = JsonSerializer.SerializeToNode(new WeaponPaintsConfig())!.AsObject();
        var additional = defaults["Additional"]!.AsObject();
        foreach (var key in WeaponPaintsConfig.ObsoleteKeys)
        {
            Assert.False(defaults.ContainsKey(key), $"{key} is obsolete but still a config property");
            Assert.False(additional.ContainsKey(key), $"{key} is obsolete but still an Additional property");
        }

        foreach (var live in new[] { "DatabaseHost", "DatabasePort", "DatabaseUser", "DatabasePassword", "DatabaseName", "DatabaseMaxPoolSize" })
            Assert.DoesNotContain(live, WeaponPaintsConfig.ObsoleteKeys);
    }
}
