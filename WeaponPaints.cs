using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

[MinimumApiVersion(338)]
public partial class WeaponPaints : BasePlugin, IPluginConfig<WeaponPaintsConfig>
{
    internal static WeaponPaints Instance { get; private set; } = new();

    public WeaponPaintsConfig Config { get; set; } = new();
    internal static WeaponPaintsSqlConfig SqlConfig { get; set; } = new();
    private static WeaponPaintsConfig _config { get; set; } = new();
    public override string ModuleAuthor => "Nereziel & daffyy";
    public override string ModuleDescription =>
        "Skin, gloves, agents and knife selector, standalone and web-based";
    public override string ModuleName => "WeaponPaints";
    public override string ModuleVersion => "3.3a";

    public override void Load(bool hotReload)
    {
        // Hardcoded hotfix needs to be changed later (Not needed 17.09.2025)
        //if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        //	Patch.PerformPatch("0F 85 ? ? ? ? 31 C0 B9 ? ? ? ? BA ? ? ? ? 66 0F EF C0 31 F6 31 FF 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 48 C7 45 ? ? ? ? ? 0F 29 45 ? 48 C7 45 ? ? ? ? ? C7 45 ? ? ? ? ? 66 89 45 ? E8 ? ? ? ? 41 89 C5 85 C0 0F 8E", "90 90 90 90 90 90");
        //else
        //	Patch.PerformPatch("74 ? 48 8D 0D ? ? ? ? FF 15 ? ? ? ? EB ? BA", "EB");

        Instance = this;

        if (hotReload)
        {
            OnMapStart(string.Empty);

            GPlayerWeaponsInfo.Clear();
            GPlayersKnife.Clear();
            GPlayersGlove.Clear();
            GPlayersAgent.Clear();
            GPlayersPin.Clear();
            GPlayersMusic.Clear();
            PlayersBySteamId.Clear();

            foreach (
                var player in Enumerable
                    .OfType<CCSPlayerController>(
                        Utilities.GetPlayers().TakeWhile(_ => WeaponSync != null)
                    )
                    .Where(player =>
                        player.IsValid
                        && !string.IsNullOrEmpty(player.IpAddress)
                        && player
                            is { IsBot: false, Connected: PlayerConnectedState.PlayerConnected }
                    )
            )
            {
                var playerInfo = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player?.SteamID.ToString(),
                    Name = player?.PlayerName,
                    IpAddress = player?.IpAddress?.Split(":")[0],
                };

                _ = Task.Run(async () =>
                {
                    if (WeaponSync != null)
                        await WeaponSync.GetPlayerData(playerInfo);
                });

                // Rebuild SteamID cache
                if (player != null)
                    PlayersBySteamId[player.SteamID] = player;
            }
        }

        Utility.LoadSkinsFromFile(
            ModuleDirectory + $"/data/skins_{_config.SkinsLanguage}.json",
            Logger
        );
        Utility.LoadGlovesFromFile(
            ModuleDirectory + $"/data/gloves_{_config.SkinsLanguage}.json",
            Logger
        );
        Utility.LoadAgentsFromFile(
            ModuleDirectory + $"/data/agents_{_config.SkinsLanguage}.json",
            Logger
        );
        Utility.LoadMusicFromFile(
            ModuleDirectory + $"/data/music_{_config.SkinsLanguage}.json",
            Logger
        );
        Utility.LoadPinsFromFile(
            ModuleDirectory + $"/data/collectibles_{_config.SkinsLanguage}.json",
            Logger
        );

        RegisterListeners();
    }

    public void OnConfigParsed(WeaponPaintsConfig config)
    {
        Config = config;
        _config = config;

        // Load SQL config from separate file (configs/plugins/WeaponPaints/WeaponPaintsSQL.json)
        // ModuleDirectory = .../addons/counterstrikesharp/plugins/WeaponPaints
        // We need: .../addons/counterstrikesharp/configs/plugins/WeaponPaints
        var cssharpDir = Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory))!; // .../addons/counterstrikesharp
        var sqlConfigDir = Path.Combine(cssharpDir, "configs", "plugins", "WeaponPaints");

        // Check for alternative lowercase filename first (e.g. FSHOST uses weaponpaintssql.json)
        var sqlConfigPathLower = Path.Combine(sqlConfigDir, "weaponpaintssql.json");
        var sqlConfigPathDefault = Path.Combine(sqlConfigDir, "WeaponPaintsSQL.json");

        // Use whichever exists — prefer lowercase variant to avoid creating duplicates
        string sqlConfigPath;
        if (File.Exists(sqlConfigPathLower))
            sqlConfigPath = sqlConfigPathLower;
        else
            sqlConfigPath = sqlConfigPathDefault;

        if (!File.Exists(sqlConfigPath))
        {
            // Create default SQL config file
            SqlConfig = new WeaponPaintsSqlConfig();
            var defaultJson = JsonSerializer.Serialize(
                SqlConfig,
                new JsonSerializerOptions { WriteIndented = true }
            );
            Directory.CreateDirectory(Path.GetDirectoryName(sqlConfigPath)!);
            File.WriteAllText(sqlConfigPath, defaultJson);
            Logger.LogError(
                $"SQL config file created at \"{sqlConfigPath}\". Please configure your database credentials!"
            );
            Unload(false);
            return;
        }

        try
        {
            var sqlConfigJson = File.ReadAllText(sqlConfigPath);
            SqlConfig =
                JsonSerializer.Deserialize<WeaponPaintsSqlConfig>(sqlConfigJson)
                ?? new WeaponPaintsSqlConfig();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error reading SQL config: {ex.Message}");
            Unload(false);
            return;
        }

        if (
            SqlConfig.DatabaseHost.Length < 1
            || SqlConfig.DatabaseName.Length < 1
            || SqlConfig.DatabaseUser.Length < 1
        )
        {
            Logger.LogError($"You need to setup Database credentials in \"{sqlConfigPath}\"!");
            Unload(false);
            return;
        }

        if (
            !File.Exists(
                Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory))
                    + "/gamedata/weaponpaints.json"
            )
        )
        {
            Logger.LogError("You need to upload \"weaponpaints.json\" to \"gamedata directory\"!");
            Unload(false);
            return;
        }

        var builder = new MySqlConnectionStringBuilder
        {
            Server = SqlConfig.DatabaseHost,
            UserID = SqlConfig.DatabaseUser,
            Password = SqlConfig.DatabasePassword,
            Database = SqlConfig.DatabaseName,
            Port = (uint)SqlConfig.DatabasePort,
            Pooling = true,
            MaximumPoolSize = 16, // Was 640 — a CS2 server has max 64 players, 16 connections is plenty
        };

        Database = new Database(builder.ConnectionString);
        WeaponSync = new WeaponSynchronization(Database, Config);

        _ = Utility.CheckDatabaseTables();
        _localizer = Localizer;

        Utility.Config = config;
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
            MenuApi = MenuCapability.Get();

            if (Config.Additional.KnifeEnabled)
                SetupKnifeMenu();
            if (Config.Additional.SkinEnabled)
                SetupSkinsMenu();
            if (Config.Additional.GloveEnabled)
                SetupGlovesMenu();
            if (Config.Additional.AgentEnabled)
                SetupAgentsMenu();
            if (Config.Additional.MusicEnabled)
                SetupMusicMenu();
            if (Config.Additional.PinsEnabled)
                SetupPinsMenu();

            RegisterCommands();
        }
        catch (Exception)
        {
            MenuApi = null;
            Logger.LogError("Error while loading required plugins");
            throw;
        }
    }
}
