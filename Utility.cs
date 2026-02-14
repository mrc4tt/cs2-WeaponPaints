using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Menu;
using Dapper;
using MenuManager;
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
					    CREATE TABLE IF NOT EXISTS `wp_player_pins` (
					        `steamid` varchar(64) NOT NULL,
					        `weapon_team` int(1) NOT NULL,
					        `id` int(11) NOT NULL,
					        UNIQUE (`steamid`, `weapon_team`) -- Unique constraint
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

        internal static void LoadSkinsFromFile(string filePath, ILogger logger)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var deserializedSkins = JsonConvert.DeserializeObject<List<JObject>>(json);
                WeaponPaints.SkinsList = deserializedSkins ?? [];
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
                var json = File.ReadAllText(filePath);
                var deserializedPins = JsonConvert.DeserializeObject<List<JObject>>(json);
                WeaponPaints.PinsList = deserializedPins ?? [];
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load pins from file: {ex.Message}");
            }
        }

        internal static void LoadGlovesFromFile(string filePath, ILogger logger)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var deserializedSkins = JsonConvert.DeserializeObject<List<JObject>>(json);
                WeaponPaints.GlovesList = deserializedSkins ?? [];
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load gloves from file: {ex.Message}");
            }
        }

        internal static void LoadAgentsFromFile(string filePath, ILogger logger)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var deserializedSkins = JsonConvert.DeserializeObject<List<JObject>>(json);
                WeaponPaints.AgentsList = deserializedSkins ?? [];
            }
            catch (Exception ex)
            {
                logger?.LogError($"Failed to load agents from file: {ex.Message}");
            }
        }

        internal static void LoadMusicFromFile(string filePath, ILogger logger)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var deserializedSkins = JsonConvert.DeserializeObject<List<JObject>>(json);
                WeaponPaints.MusicList = deserializedSkins ?? [];
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

        internal static IMenu? CreateMenu(string title)
        {
            var menuType = WeaponPaints.Instance.Config.MenuType.ToLower();

            var menu = menuType switch
            {
                _ when menuType.Equals("selectable", StringComparison.CurrentCultureIgnoreCase) =>
                    WeaponPaints.MenuApi?.NewMenu(title),

                _ when menuType.Equals("dynamic", StringComparison.CurrentCultureIgnoreCase) =>
                    WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.ButtonMenu),

                _ when menuType.Equals("center", StringComparison.CurrentCultureIgnoreCase) =>
                    WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.CenterMenu),

                _ when menuType.Equals("chat", StringComparison.CurrentCultureIgnoreCase) =>
                    WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.ChatMenu),

                _ when menuType.Equals("console", StringComparison.CurrentCultureIgnoreCase) =>
                    WeaponPaints.MenuApi?.NewMenuForcetype(title, MenuType.ConsoleMenu),

                _ => WeaponPaints.MenuApi?.NewMenu(title),
            };

            return menu;
        }
    }
}
