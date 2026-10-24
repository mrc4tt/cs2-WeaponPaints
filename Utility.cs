using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CS2MenuManager.API.Interface;
using CS2MenuManager.API.Menu;
using Dapper;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WeaponPaints
{
    internal static class Utility
    {
        internal static WeaponPaintsConfig? Config { get; set; }

        internal static async Task CheckDatabaseTables()
        {
            if (WeaponPaints.Database is null)
                return;

            try
            {
                await using var connection = await WeaponPaints.Database.GetConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();

                try
                {
                    string[] createTableQueries =
                    [
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_skins` (
					        `steamid` varchar(18) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `weapon_defindex` int(6) NOT NULL,
					        `weapon_paint_id` int(6) NOT NULL,
					        `weapon_wear` float NOT NULL DEFAULT 0.000001,
					        `weapon_seed` int(16) NOT NULL DEFAULT 0,
					        `weapon_nametag` VARCHAR(128) DEFAULT NULL,
					        `weapon_stattrak` tinyint(1) NOT NULL DEFAULT 0,
					        `weapon_stattrak_count` int(10) NOT NULL DEFAULT 0,
					        `weapon_sticker_0` VARCHAR(128) NOT NULL DEFAULT '0;0;0;0;0;0;0' COMMENT 'id;schema;x;y;wear;scale;rotation',
					        `weapon_sticker_1` VARCHAR(128) NOT NULL DEFAULT '0;0;0;0;0;0;0' COMMENT 'id;schema;x;y;wear;scale;rotation',
					        `weapon_sticker_2` VARCHAR(128) NOT NULL DEFAULT '0;0;0;0;0;0;0' COMMENT 'id;schema;x;y;wear;scale;rotation',
					        `weapon_sticker_3` VARCHAR(128) NOT NULL DEFAULT '0;0;0;0;0;0;0' COMMENT 'id;schema;x;y;wear;scale;rotation',
					        `weapon_sticker_4` VARCHAR(128) NOT NULL DEFAULT '0;0;0;0;0;0;0' COMMENT 'id;schema;x;y;wear;scale;rotation',
					        `weapon_keychain` VARCHAR(128) NOT NULL DEFAULT '0;0;0;0;0' COMMENT 'id;x;y;z;seed',
					        UNIQUE (`steamid`, `weapon_team`, `weapon_defindex`) -- Add unique constraint here
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_knife` (
					        `steamid` varchar(18) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `knife` varchar(64) NOT NULL,
					        UNIQUE (`steamid`, `weapon_team`) -- Unique constraint
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_gloves` (
					        `steamid` varchar(18) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `weapon_defindex` int(11) NOT NULL,
					        UNIQUE (`steamid`, `weapon_team`) -- Unique constraint
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_agents` (
					        `steamid` varchar(18) NOT NULL,
					        `agent_ct` varchar(64) DEFAULT NULL,
					        `agent_t` varchar(64) DEFAULT NULL,
					        UNIQUE (`steamid`) -- Unique constraint
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_music` (
					        `steamid` varchar(64) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `music_id` int(11) NOT NULL,
					        UNIQUE (`steamid`, `weapon_team`) -- Unique constraint
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_music_settings` (
					        `steamid` varchar(64) NOT NULL PRIMARY KEY,
					        `music_off` tinyint(1) NOT NULL DEFAULT 0
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_pins` (
					        `steamid` varchar(64) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `id` int(11) NOT NULL,
					        UNIQUE (`steamid`, `weapon_team`) -- Unique constraint
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_refresh` (
					        `steamid` VARCHAR(32) NOT NULL PRIMARY KEY,
					        `stamp` TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        // One pet per player, like agents: the pet item is "noteam" in items_game. Column
                        // names and types match SkinHubgg/WeaponPaints' wp_player_pets (and the SkinHub site's
                        // formatPetRow/parsePetRow), so a site written for that schema works unchanged here.
                        // A new table only - no existing table is touched.
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_pets` (
					        `steamid` varchar(18) NOT NULL PRIMARY KEY,
					        `pet_id` int NOT NULL COMMENT 'pet_definitions id: 1 egg, 2 chick, 3 catalana, 4 silkie, 5 polish',
					        `pet_stage` tinyint NOT NULL DEFAULT 3 COMMENT 'upgrade level: 0 egg, 1 chick, 2 pullet, 3 hen',
					        `pet_variant` int NULL DEFAULT NULL COMMENT 'item style = material group index; NULL = no style, the default group',
					        `pet_seed` int unsigned NOT NULL DEFAULT 0 COMMENT 'pet seed attribute',
					        `pet_name` varchar(32) NULL DEFAULT NULL COMMENT 'name tag for the current stage'
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                        // Remembered wear/seed per paint (PaintMemoryEnabled): picking a skin again restores
                        // its old float and seed. A new table only - wp_player_skins is not touched.
                        @"
					    CREATE TABLE IF NOT EXISTS `wp_player_paint_memory` (
					        `steamid` varchar(18) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `weapon_defindex` int(6) NOT NULL,
					        `weapon_paint_id` int(6) NOT NULL,
					        `weapon_wear` float NOT NULL DEFAULT 0.01,
					        `weapon_seed` int(16) NOT NULL DEFAULT 0,
					        PRIMARY KEY (`steamid`, `weapon_team`, `weapon_defindex`, `weapon_paint_id`)
					    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;",
                    ];

                    foreach (var query in createTableQueries)
                    {
                        await connection.ExecuteAsync(query, transaction: transaction);
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception)
                {
                    await transaction.RollbackAsync();
                    throw new Exception("[WeaponPaints] Unable to create tables!");
                }
            }
            catch (Exception ex)
            {
                throw new Exception("[WeaponPaints] Unknown MySQL exception! " + ex.Message);
            }
        }

        internal static bool IsPlayerValid(CCSPlayerController? player)
        {
            if (player is null || WeaponPaints.WeaponSync is null)
                return false;

            return player is { IsValid: true, IsBot: false, IsHLTV: false, UserId: not null };
        }

        // Catalog loading is split into Build* (pure: parse + indexes, safe on any thread) and
        // Install* (swap the static references). Readers only ever see a complete list + its
        // indexes; nothing mutates an installed list. Load*FromFile = read file, build, install.

        // Streams the file into the JObject tree instead of first reading it into one big string
        // (stickers is ~2MB = a 4MB string on the large object heap per load).
        internal static List<JObject> ReadCatalogFile(string filePath)
        {
            using var reader = new JsonTextReader(new StreamReader(filePath));
            return JsonSerializer.CreateDefault().Deserialize<List<JObject>>(reader) ?? [];
        }

        internal sealed record SkinsCatalog(
            List<JObject> List,
            Dictionary<string, List<JObject>> ByWeapon,
            Dictionary<(int, int), bool> Legacy,
            Dictionary<(int, int), (float Min, float Max)> FloatRange);

        internal static SkinsCatalog BuildSkins(List<JObject> list)
        {
            var byWeapon = new Dictionary<string, List<JObject>>();
            var legacyByDefPaint = new Dictionary<(int, int), bool>();
            var floatRange = new Dictionary<(int, int), (float, float)>();

            foreach (var skin in list)
            {
                var weaponName = skin["weapon_name"]?.ToString();
                if (!string.IsNullOrEmpty(weaponName))
                {
                    if (!byWeapon.TryGetValue(weaponName, out var weaponSkins))
                        byWeapon[weaponName] = weaponSkins = new List<JObject>();
                    weaponSkins.Add(skin);
                }

                if (skin["weapon_defindex"] == null || skin["paint"] == null)
                    continue;
                var defindex = (int?)skin["weapon_defindex"] ?? 0;
                var paint = (int?)skin["paint"] ?? 0;
                legacyByDefPaint[(defindex, paint)] = skin.Value<bool>("legacy_model");

                // Present only in catalogs merged from the online API (data/cache).
                if (skin["min_float"] != null && skin["max_float"] != null)
                    floatRange[(defindex, paint)] = ((float?)skin["min_float"] ?? 0f, (float?)skin["max_float"] ?? 1f);
            }

            return new SkinsCatalog(list, byWeapon, legacyByDefPaint, floatRange);
        }

        internal static void InstallSkins(SkinsCatalog catalog)
        {
            WeaponPaints.SkinsByWeaponName = catalog.ByWeapon;
            WeaponPaints.SkinsLegacyModelIndex = catalog.Legacy;
            // The online merge keeps its own (fuller) range table; a bundled file has none.
            if (catalog.FloatRange.Count > 0 || WeaponPaints.SkinFloatRange.Count == 0)
                WeaponPaints.SkinFloatRange = catalog.FloatRange;
            WeaponPaints.SkinsList = catalog.List;
        }

        internal static void LoadSkinsFromFile(string filePath, ILogger logger)
        {
            try
            {
                InstallSkins(BuildSkins(ReadCatalogFile(filePath)));
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load skins from file: {ex.Message}");
            }
        }

        internal static void LoadPinsFromFile(string filePath, ILogger logger)
        {
            try
            {
                WeaponPaints.PinsList = ReadCatalogFile(filePath);
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load pins from file: {ex.Message}");
            }
        }

        internal sealed record GlovesCatalog(List<JObject> List, Dictionary<string, JObject> ByPaintName);

        internal static GlovesCatalog BuildGloves(List<JObject> list)
        {
            var byPaint = new Dictionary<string, JObject>();
            foreach (var glove in list)
            {
                var paintName = glove["paint_name"]?.ToString();
                if (!string.IsNullOrEmpty(paintName))
                    byPaint[paintName] = glove;
            }
            return new GlovesCatalog(list, byPaint);
        }

        internal static void InstallGloves(GlovesCatalog catalog)
        {
            WeaponPaints.GlovesByPaintName = catalog.ByPaintName;
            WeaponPaints.GlovesList = catalog.List;
        }

        internal static void LoadGlovesFromFile(string filePath, ILogger logger)
        {
            try
            {
                InstallGloves(BuildGloves(ReadCatalogFile(filePath)));
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load gloves from file: {ex.Message}");
            }
        }

        internal sealed record AgentsCatalog(
            List<JObject> List,
            Dictionary<(string, int), JObject> ByNameTeam,
            Dictionary<int, List<JObject>> ByTeam,
            HashSet<string> ModelSet,
            Dictionary<string, string> ByBasename,
            Dictionary<string, ushort> DefIndexByModel);

        private static readonly System.Text.RegularExpressions.Regex AgentImageDefIndex =
            new(@"agent-(\d+)\.png", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        // Item definition index of a catalog agent: the "def_index" the online catalog carries, else
        // the bundled files' preview image name (".../agent-<defindex>.png"). 0 when neither has it.
        internal static ushort AgentDefIndex(JObject agent)
        {
            if (ushort.TryParse(agent["def_index"]?.ToString(), out var defIndex) && defIndex != 0)
                return defIndex;

            var match = AgentImageDefIndex.Match(agent["image"]?.ToString() ?? "");
            return match.Success && ushort.TryParse(match.Groups[1].Value, out defIndex) ? defIndex : (ushort)0;
        }

        internal static AgentsCatalog BuildAgents(List<JObject> list)
        {
            var byNameTeam = new Dictionary<(string, int), JObject>();
            var byTeam = new Dictionary<int, List<JObject>>();
            var modelSet = new HashSet<string>();
            var byBasename = new Dictionary<string, string>();
            var defIndexByModel = new Dictionary<string, ushort>();

            foreach (var agent in list)
            {
                var name = agent["agent_name"]?.ToString();
                var team = (int?)agent["team"] ?? 0;
                if (!string.IsNullOrEmpty(name))
                    byNameTeam[(name, team)] = agent;

                if (!byTeam.TryGetValue(team, out var teamAgents))
                    byTeam[team] = teamAgents = new List<JObject>();
                teamAgents.Add(agent);

                var model = agent["model"]?.ToString();
                if (!string.IsNullOrEmpty(model))
                {
                    modelSet.Add(model);
                    var slash = model.LastIndexOf('/');
                    var basename = slash >= 0 ? model.Substring(slash + 1) : model;
                    byBasename[basename] = model;

                    var defIndex = AgentDefIndex(agent);
                    if (defIndex != 0)
                        defIndexByModel[model] = defIndex;
                }
            }

            return new AgentsCatalog(list, byNameTeam, byTeam, modelSet, byBasename, defIndexByModel);
        }

        internal static void InstallAgents(AgentsCatalog catalog)
        {
            WeaponPaints.AgentsByNameAndTeam = catalog.ByNameTeam;
            WeaponPaints.AgentsByTeam = catalog.ByTeam;
            WeaponPaints.AgentsModelSet = catalog.ModelSet;
            WeaponPaints.AgentsModelByBasename = catalog.ByBasename;
            WeaponPaints.AgentDefIndexByModel = catalog.DefIndexByModel;
            WeaponPaints.AgentsList = catalog.List;
        }

        internal static void LoadAgentsFromFile(string filePath, ILogger logger)
        {
            try
            {
                InstallAgents(BuildAgents(ReadCatalogFile(filePath)));
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load agents from file: {ex.Message}");
            }
        }

        // Resolves a stored agent model to its canonical catalog model.
        // Returns the model unchanged if already valid, the healed model if the basename
        // matches a catalog entry (legacy/web rows that lost their path prefix), or null if unknown.
        internal static string? NormalizeAgentModel(string? model)
        {
            if (string.IsNullOrEmpty(model) || model == "null")
                return null;
            if (WeaponPaints.AgentsModelSet.Contains(model))
                return model;

            var slash = model.LastIndexOf('/');
            var basename = slash >= 0 ? model.Substring(slash + 1) : model;
            if (WeaponPaints.AgentsModelByBasename.TryGetValue(basename, out var full))
                return full;

            return null;
        }

        internal sealed record StickersCatalog(List<JObject> List, Dictionary<uint, JObject> ById, StickerBrowseIndex Browse);

        internal static StickersCatalog BuildStickers(List<JObject> list)
        {
            var byId = new Dictionary<uint, JObject>();
            foreach (var sticker in list)
            {
                var idTok = sticker["id"]?.ToString();
                if (!string.IsNullOrEmpty(idTok) && uint.TryParse(idTok, out var id))
                    byId[id] = sticker;
            }
            return new StickersCatalog(list, byId, StickerBrowseIndex.Build(list));
        }

        internal static void InstallStickers(StickersCatalog catalog)
        {
            WeaponPaints.StickersById = catalog.ById;
            WeaponPaints.StickerBrowse = catalog.Browse;
            WeaponPaints.StickersList = catalog.List;
        }

        internal static void LoadStickersFromFile(string filePath, ILogger logger)
        {
            try
            {
                InstallStickers(BuildStickers(ReadCatalogFile(filePath)));
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load stickers from file: {ex.Message}");
            }
        }

        internal sealed record KeychainsCatalog(List<JObject> List, Dictionary<uint, JObject> ById);

        internal static KeychainsCatalog BuildKeychains(List<JObject> list)
        {
            var byId = new Dictionary<uint, JObject>();
            foreach (var charm in list)
            {
                if (uint.TryParse(charm["id"]?.ToString(), out var id))
                    byId[id] = charm;
            }
            return new KeychainsCatalog(list, byId);
        }

        internal static void InstallKeychains(KeychainsCatalog catalog)
        {
            WeaponPaints.KeychainsById = catalog.ById;
            WeaponPaints.KeychainsList = catalog.List;
        }

        internal static void LoadKeychainsFromFile(string filePath, ILogger logger)
        {
            try
            {
                InstallKeychains(BuildKeychains(ReadCatalogFile(filePath)));
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load charms from file: {ex.Message}");
            }
        }

        internal static void LoadMusicFromFile(string filePath, ILogger logger)
        {
            try
            {
                WeaponPaints.MusicList = ReadCatalogFile(filePath);
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load music from file: {ex.Message}");
            }
        }

        internal static void Log(string message)
        {
            Console.BackgroundColor = ConsoleColor.DarkGray;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("[WeaponPaints] " + message);
            Console.ResetColor();
        }

        // PlayerMenu defers menu-type selection to MenuTypeManager.GetPlayerMenuType(player),
        // so each player sees the style they've picked via MenuManagerCore's settings menu.
        // Falls back to MenuManagerCore's server-default when the player hasn't chosen one.
        internal static IMenu CreateMenu(string title)
            => new PlayerMenu(title, WeaponPaints.Instance);
    }
}
