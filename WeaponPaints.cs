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
    public override string ModuleAuthor => "Nereziel & daffyy";
    public override string ModuleDescription => "Skin, gloves, agents and knife selector, standalone and web-based";
    public override string ModuleName => "WeaponPaints";
    public override string ModuleVersion => "3.4a";

    public override void Load(bool hotReload)
    {
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

            // Defer player enumeration — entity system may not be initialized yet during Load()
            Server.NextWorldUpdate(() =>
            {
                try
                {
                    foreach (
                        var player in Enumerable
                            .OfType<CCSPlayerController>(Utilities.GetPlayers().TakeWhile(_ => WeaponSync != null))
                            .Where(player => player.IsValid && player is { IsBot: false, Connected: PlayerConnectedState.Connected })
                    )
                    {
                        var playerInfo = PlayerInfo.From(player);

                        _ = Task.Run(async () =>
                        {
                            if (WeaponSync != null)
                                await WeaponSync.GetPlayerData(playerInfo);
                        });

                        PlayersBySteamId[player.SteamID] = player;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[HotReload] Error enumerating players: {ex.Message}");
                }
            });
        }

        Utility.LoadSkinsFromFile(ModuleDirectory + $"/data/skins_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadGlovesFromFile(ModuleDirectory + $"/data/gloves_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadAgentsFromFile(ModuleDirectory + $"/data/agents_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadMusicFromFile(ModuleDirectory + $"/data/music_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadPinsFromFile(ModuleDirectory + $"/data/collectibles_{Config.SkinsLanguage}.json", Logger);

        RegisterListeners();
    }

    public void OnConfigParsed(WeaponPaintsConfig config)
    {
        Config = config;

        // Preferred source of truth: DB credentials on the main WeaponPaints.json
        // (Config.Database*). Fall back to the legacy split-file layout
        // (weaponpaintssql.json / WeaponPaintsSQL.json) only if the main config
        // has empty DB fields, so older deployments keep working.
        if (config.DatabaseHost.Length > 0 && config.DatabaseName.Length > 0 && config.DatabaseUser.Length > 0)
        {
            SqlConfig = new WeaponPaintsSqlConfig
            {
                DatabaseHost = config.DatabaseHost,
                DatabasePort = config.DatabasePort,
                DatabaseUser = config.DatabaseUser,
                DatabasePassword = config.DatabasePassword,
                DatabaseName = config.DatabaseName,
            };
            Logger.LogInformation("Using SQL config: WeaponPaints.json");
        }
        else
        {
            var cssharpDir = Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory))!;
            var sqlConfigDir = Path.Combine(cssharpDir, "configs", "plugins", "WeaponPaints");

            var candidateNames = new[] { "weaponpaintssql.json", "WeaponPaintsSQL.json" };
            var existingCandidates = new List<(string Path, WeaponPaintsSqlConfig? Cfg, bool Filled)>();

            foreach (var name in candidateNames)
            {
                var path = Path.Combine(sqlConfigDir, name);
                if (!File.Exists(path))
                    continue;

                WeaponPaintsSqlConfig? parsed = null;
                try
                {
                    parsed = JsonSerializer.Deserialize<WeaponPaintsSqlConfig>(File.ReadAllText(path));
                }
                catch { }

                var filled = parsed is not null && parsed.DatabaseHost.Length > 0 && parsed.DatabaseName.Length > 0 && parsed.DatabaseUser.Length > 0;

                existingCandidates.Add((path, parsed, filled));
            }

            var chosen = existingCandidates.FirstOrDefault(c => c.Filled);

            if (chosen.Path is not null)
            {
                SqlConfig = chosen.Cfg!;
                Logger.LogInformation($"Using SQL config: {Path.GetFileName(chosen.Path)}");
            }
            else
            {
                Logger.LogError("You need to setup Database credentials (DatabaseHost/Port/User/Password/Name) in WeaponPaints.json!");
                Unload(false);
                return;
            }
        }

        if (!File.Exists(Path.GetDirectoryName(Path.GetDirectoryName(ModuleDirectory)) + "/gamedata/weaponpaints.json"))
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
            MaximumPoolSize = 16,
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
