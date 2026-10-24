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

        [JsonPropertyName("PetsEnabled")]
        public bool PetsEnabled { get; set; } = true;

        // CounterStrikeSharp permission needed for !pet AND for a pet to be spawned at all,
        // e.g. "@css/vip". Empty = everyone.
        [JsonPropertyName("PetPermission")]
        public string PetPermission { get; set; } = "";

        // !pethat: one of the ten photo booth hats on the pet's head (wp_player_pets.pet_hat). See PetHats.cs.
        [JsonPropertyName("PetHatsEnabled")]
        public bool PetHatsEnabled { get; set; } = true;

        // !petemote: asks the pet for a move the game's chicken plays in a match (trick, squat, sleep,
        // feed, shoulder, panic, idle). The game picks the variant. See PetEmotes.cs.
        [JsonPropertyName("PetEmotesEnabled")]
        public bool PetEmotesEnabled { get; set; } = true;

        // Seconds a player waits between two !petemote requests.
        [JsonPropertyName("PetEmoteCooldownSeconds")]
        public int PetEmoteCooldownSeconds { get; set; } = 10;

        [JsonPropertyName("CommandWpEnabled")]
        public bool CommandWpEnabled { get; set; } = true;

        [JsonPropertyName("CommandKillEnabled")]
        public bool CommandKillEnabled { get; set; } = true;

        [JsonPropertyName("CommandKnife")]
        public List<string> CommandKnife { get; set; } = ["knife"];

        [JsonPropertyName("CommandMusic")]
        public List<string> CommandMusic { get; set; } = ["music"];

        [JsonPropertyName("CommandMusicOff")]
        public List<string> CommandMusicOff { get; set; } = ["musicoff"];

        [JsonPropertyName("CommandPin")]
        public List<string> CommandPin { get; set; } = ["pin", "pins", "coin", "coins"];

        [JsonPropertyName("CommandGlove")]
        public List<string> CommandGlove { get; set; } = ["gloves"];

        [JsonPropertyName("CommandPet")]
        public List<string> CommandPet { get; set; } = ["pet"];

        [JsonPropertyName("CommandPetHat")]
        public List<string> CommandPetHat { get; set; } = ["pethat"];

        [JsonPropertyName("CommandPetEmote")]
        public List<string> CommandPetEmote { get; set; } = ["petemote"];

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

        // Admin console/chat command: css_wp_refresh <all|steamid64|#userid|name> reloads players'
        // skins from the database and re-applies them (what !wp does, for someone else).
        [JsonPropertyName("CommandRefreshAdmin")]
        public List<string> CommandRefreshAdmin { get; set; } = ["wp_refresh"];

        [JsonPropertyName("RefreshAdminPermission")]
        public string RefreshAdminPermission { get; set; } = "@css/admin";

        [JsonPropertyName("CommandMenu")]
        public List<string> CommandMenu { get; set; } = ["menu"];

        [JsonPropertyName("CommandGenEnabled")]
        public bool CommandGenEnabled { get; set; } = true;

        [JsonPropertyName("CommandGen")]
        public List<string> CommandGen { get; set; } = ["g", "gen"];

        // Admin-permission gate for !g, e.g. "@css/vip". Empty = everyone may use it.
        [JsonPropertyName("CommandGenPermission")]
        public string CommandGenPermission { get; set; } = "";

        [JsonPropertyName("GiveRandomKnife")]
        public bool GiveRandomKnife { get; set; } = false;

        [JsonPropertyName("GiveRandomSkin")]
        public bool GiveRandomSkin { get; set; } = false;

        [JsonPropertyName("ShowSkinImage")]
        public bool ShowSkinImage { get; set; } = true;

        // !nametag <text> on the held weapon ("-" removes it, no argument = type it in chat).
        [JsonPropertyName("CommandNametag")]
        public List<string> CommandNametag { get; set; } = ["nametag", "nt"];

        // CS2 caps real name tags at 20 characters; longer ones are cut to this length.
        [JsonPropertyName("NametagMaxLength")]
        public int NametagMaxLength { get; set; } = 20;

        // After picking a skin in !skins, open a menu for wear, seed, StatTrak, name tag,
        // stickers and charm of that weapon (the weapon does not have to be held).
        [JsonPropertyName("CustomizeMenuEnabled")]
        public bool CustomizeMenuEnabled { get; set; } = true;

        // !cp copies the skin of the weapon held by the player you are spectating.
        [JsonPropertyName("CopySkinEnabled")]
        public bool CopySkinEnabled { get; set; } = true;

        [JsonPropertyName("CommandCopySkin")]
        public List<string> CommandCopySkin { get; set; } = ["cp", "copyskin"];

        // Commands (names without "css_" / "!", e.g. "knife", "gloves") that require VipPermission.
        // Empty list = no command is gated.
        [JsonPropertyName("VipCommands")]
        public List<string> VipCommands { get; set; } = [];

        [JsonPropertyName("VipPermission")]
        public string VipPermission { get; set; } = "@css/vip";

        // StatTrak kill counts are written to the database this often (and on disconnect / unload),
        // so a crash or reload loses at most this many seconds of kills. 0 = only on disconnect/unload.
        [JsonPropertyName("StatTrakFlushSeconds")]
        public int StatTrakFlushSeconds { get; set; } = 60;

        // Keep wear inside each skin's real float range (e.g. Asiimov 0.18-1.00). Needs the float
        // ranges from the online catalog (CatalogApiEnabled); without them only 0.001-1 applies.
        [JsonPropertyName("ClampWearToSkinRange")]
        public bool ClampWearToSkinRange { get; set; } = true;

        // Re-assert the chosen music kit / pin a few seconds after connecting and then check every
        // few seconds whether the game's inventory load overwrote them (cheap field compare).
        [JsonPropertyName("MusicPinDriftCheck")]
        public bool MusicPinDriftCheck { get; set; } = true;

        // Remember wear/seed per paint, so switching back to a skin restores its old float and seed
        // instead of resetting them. Stored in wp_player_paint_memory.
        [JsonPropertyName("PaintMemoryEnabled")]
        public bool PaintMemoryEnabled { get; set; } = true;
    }

    public class WeaponPaintsConfig : BasePluginConfig
    {
        [JsonPropertyName("ConfigVersion")]
        public override int Version { get; set; } = 24;

        // Keys of removed features (the AstraSkins port). MigrateConfigFile deletes them from
        // Additional or the root of an existing WeaponPaints.json.
        internal static readonly string[] ObsoleteKeys =
        [
            "TeamPreviewEnabled", "AgentVoiceEnabled", "ApplyCosmeticsOnBotTakeover", "MusicKitMvpCounter",
            "PrecacheAgentModels", "MaxNameTagLength", "BlockedNameTagWords", "CommandSearch",
            "CommandReset", "CommandResetPlayer", "AdminResetPermission",
        ];

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

        // Live web refresh: the website upserts a row into wp_player_refresh whenever a player
        // changes their loadout; the plugin polls that table and re-applies the player's skins
        // without them typing !wp.
        [JsonPropertyName("WebRefreshEnabled")]
        public bool WebRefreshEnabled { get; set; } = true;

        [JsonPropertyName("WebRefreshIntervalSeconds")]
        public int WebRefreshIntervalSeconds { get; set; } = 4;

        [JsonPropertyName("SkinApiURL")]
        public string SkinApiURL { get; set; } = "https://cdn.jsdelivr.net/gh/ByMykel/CSGO-API@main/public/api";

        // Fetch the item catalog from SkinApiURL in the background and add any skins, stickers,
        // charms, agents, music kits and pins the bundled data/ files don't have yet (plus float
        // ranges and sticker groups). The bundled files stay the fallback; nothing is removed.
        [JsonPropertyName("CatalogApiEnabled")]
        public bool CatalogApiEnabled { get; set; } = true;

        [JsonPropertyName("CatalogApiTimeoutSeconds")]
        public int CatalogApiTimeoutSeconds { get; set; } = 60;

        // Re-fetch the catalog every N hours while the server runs. 0 = only on plugin load.
        [JsonPropertyName("CatalogApiRefreshHours")]
        public int CatalogApiRefreshHours { get; set; } = 0;

        // Permission for css_wp_reloadcatalog (server console always allowed).
        [JsonPropertyName("CatalogReloadPermission")]
        public string CatalogReloadPermission { get; set; } = "@css/root";

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
