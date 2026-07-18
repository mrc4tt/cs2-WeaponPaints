using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace WeaponPaints
{
    public class Additional
    {
        [JsonPropertyName("KnifeEnabled")]
        public bool KnifeEnabled { get; set; } = true;

        [JsonPropertyName("GloveEnabled")]
        public bool GloveEnabled { get; set; } = true;

        [JsonPropertyName("MusicEnabled")]
        public bool MusicEnabled { get; set; } = true;

        [JsonPropertyName("AgentEnabled")]
        public bool AgentEnabled { get; set; } = true;

        [JsonPropertyName("SkinEnabled")]
        public bool SkinEnabled { get; set; } = true;

        [JsonPropertyName("PinsEnabled")]
        public bool PinsEnabled { get; set; } = true;

        [JsonPropertyName("CommandWpEnabled")]
        public bool CommandWpEnabled { get; set; } = true;

        [JsonPropertyName("CommandKillEnabled")]
        public bool CommandKillEnabled { get; set; } = true;

        [JsonPropertyName("CommandKnife")]
        public List<string> CommandKnife { get; set; } = ["knife"];

        [JsonPropertyName("CommandMusic")]
        public List<string> CommandMusic { get; set; } = ["music"];

        [JsonPropertyName("CommandPin")]
        public List<string> CommandPin { get; set; } = ["pin", "pins", "coin", "coins"];

        [JsonPropertyName("CommandGlove")]
        public List<string> CommandGlove { get; set; } = ["gloves"];

        [JsonPropertyName("CommandAgent")]
        public List<string> CommandAgent { get; set; } = ["agents"];

        [JsonPropertyName("CommandStattrak")]
        public List<string> CommandStattrak { get; set; } = ["stattrak", "st"];

        [JsonPropertyName("CommandSkin")]
        public List<string> CommandSkin { get; set; } = ["ws"];

        [JsonPropertyName("CommandSkinSelection")]
        public List<string> CommandSkinSelection { get; set; } = ["skins"];

        [JsonPropertyName("CommandSticker")]
        public List<string> CommandSticker { get; set; } = ["stickers", "sticker"];

        [JsonPropertyName("CommandRefresh")]
        public List<string> CommandRefresh { get; set; } = ["wp"];

        [JsonPropertyName("CommandKill")]
        public List<string> CommandKill { get; set; } = ["kill"];

        [JsonPropertyName("CommandFloat")]
        public List<string> CommandFloat { get; set; } = ["float", "wear"];

        [JsonPropertyName("CommandSeed")]
        public List<string> CommandSeed { get; set; } = ["seed", "pattern"];

        [JsonPropertyName("CommandGloveFloat")]
        public List<string> CommandGloveFloat { get; set; } = ["gfloat", "gwear"];

        [JsonPropertyName("CommandGloveSeed")]
        public List<string> CommandGloveSeed { get; set; } = ["gseed", "gpattern"];

        [JsonPropertyName("CommandMenu")]
        public List<string> CommandMenu { get; set; } = ["menu"];

        [JsonPropertyName("GiveRandomKnife")]
        public bool GiveRandomKnife { get; set; } = false;

        [JsonPropertyName("GiveRandomSkin")]
        public bool GiveRandomSkin { get; set; } = false;

        [JsonPropertyName("ShowSkinImage")]
        public bool ShowSkinImage { get; set; } = true;
    }

    public class WeaponPaintsConfig : BasePluginConfig
    {
        [JsonPropertyName("ConfigVersion")]
        public override int Version { get; set; } = 17;

        [JsonPropertyName("DatabaseHost")]
        public string DatabaseHost { get; set; } = "";

        [JsonPropertyName("DatabasePort")]
        public int DatabasePort { get; set; } = 3306;

        [JsonPropertyName("DatabaseUser")]
        public string DatabaseUser { get; set; } = "";

        [JsonPropertyName("DatabasePassword")]
        public string DatabasePassword { get; set; } = "";

        [JsonPropertyName("DatabaseName")]
        public string DatabaseName { get; set; } = "";

        // MySQL connection pool ceiling. Default raised to 32 to handle 64-player connect
        // storms — at 16, simultaneous GetPlayerData calls on a full server queue up and
        // back-pressure ThreadPool tasks. Lower on small servers (<24 slots) if memory matters.
        [JsonPropertyName("DatabaseMaxPoolSize")]
        public int DatabaseMaxPoolSize { get; set; } = 32;

        [JsonPropertyName("SkinsLanguage")]
        public string SkinsLanguage { get; set; } = "en";

        [JsonPropertyName("CmdRefreshCooldownSeconds")]
        public int CmdRefreshCooldownSeconds { get; set; } = 3;

        [JsonPropertyName("Website")]
        public string Website { get; set; } = "example.com/skins";

        [JsonPropertyName("SkinApiURL")]
        public string SkinApiURL { get; set; } = "https://cdn.jsdelivr.net/gh/ByMykel/CSGO-API@main/public/api";

        [JsonPropertyName("Additional")]
        public Additional Additional { get; set; } = new();

        // DEPRECATED — kept only so existing user configs don't error out. The plugin now uses
        // CS2MenuManager's PlayerMenu, which defers to each player's choice from MenuManagerCore's
        // settings menu (and MenuManagerCore's own server-default when the player hasn't picked one).
        // Configure the server default in MenuManagerCore's config, not here.
        [JsonPropertyName("MenuType")]
        public string MenuType { get; set; } = "chat";
    }
}
