using Newtonsoft.Json.Linq;
using Xunit;

namespace WeaponPaints.Tests;

public class AgentDefIndexTests
{
    [Fact]
    public void PrefersCatalogDefIndex()
    {
        var agent = new JObject { ["def_index"] = "4613", ["image"] = "https://example/agent-9999.png" };
        Assert.Equal((ushort)4613, Utility.AgentDefIndex(agent));
    }

    [Fact]
    public void FallsBackToBundledImageName()
    {
        var agent = new JObject { ["image"] = "https://host/img/skins/agent-5504.png" };
        Assert.Equal((ushort)5504, Utility.AgentDefIndex(agent));
    }

    [Fact]
    public void ZeroWhenUnknown()
    {
        Assert.Equal((ushort)0, Utility.AgentDefIndex(new JObject { ["image"] = "", ["def_index"] = "" }));
        Assert.Equal((ushort)0, Utility.AgentDefIndex(new JObject { ["image"] = "https://steamcdn/economy/image/abc" }));
    }

    [Fact]
    public void BuildAgentsIndexesBundledCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "agents_en.json");
        var list = JArray.Parse(File.ReadAllText(path)).Cast<JObject>().ToList();
        var catalog = Utility.BuildAgents(list);
        Assert.Equal(list.Count(a => a["model"]?.ToString() != "null"), catalog.DefIndexByModel.Count);
        Assert.Equal((ushort)4613, catalog.DefIndexByModel["tm_professional/tm_professional_varf5"]);
    }
}
