using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace WeaponPaints;

public partial class WeaponPaints : BasePlugin, IPluginConfig<WeaponPaintsConfig>
{
    internal static WeaponPaints Instance { get; private set; } = new();

    public WeaponPaintsConfig Config { get; set; } = new();
    internal static WeaponPaintsSqlConfig SqlConfig { get; set; } = new();
    public override string ModuleAuthor => "Nereziel & daffyy (Forked by Miksen)";
    public override string ModuleDescription => "Skin, gloves, agents and knife selector, standalone and web-based";
    public override string ModuleName => "WeaponPaints";
    public override string ModuleVersion => _moduleVersion;

    // Embedded by MSBuild from the repo-root VERSION file via <AssemblyMetadata>.
    private static readonly string _moduleVersion = typeof(WeaponPaints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ModuleVersion")?.Value ?? "unknown";

    public override void Load(bool hotReload)
    {
        Instance = this;

        if (hotReload)
        {
            OnMapStart(string.Empty);

            GPlayerWeaponsInfo.Clear();
            GPlayersKnife.Clear();
            GPlayersGlove.Clear();
            GPlayersNativeGlove.Clear();
            GPlayersAgent.Clear();
            GPlayersPin.Clear();
            GPlayersNativePin.Clear();
            GPlayersMusic.Clear();
            GPlayersPendingSeedWearInput.Clear();
            OriginalPawnModel.Clear();
            _temporaryPlayerWeaponWear.Clear();
            _stickerCommandFilters.Clear();
            _playerWeaponImage.Clear();
            CommandsCooldown.Clear();
            LastCommandTime.Clear();
            PlayersBySteamId.Clear();
            _weaponDataReady.Clear();
            _lastWeaponRefresh.Clear();

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
                            _weaponDataReady[playerInfo.Slot] = true;
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

        // Small catalogs that the menu builder (OnAllPluginsLoaded, which runs immediately after
        // Load) iterates at build time — parse these synchronously so the menus are populated.
        Utility.LoadGlovesFromFile(ModuleDirectory + $"/data/gloves_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadAgentsFromFile(ModuleDirectory + $"/data/agents_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadMusicFromFile(ModuleDirectory + $"/data/music_{Config.SkinsLanguage}.json", Logger);
        Utility.LoadPinsFromFile(ModuleDirectory + $"/data/collectibles_{Config.SkinsLanguage}.json", Logger);

        // Big catalogs (skins ~0.6MB, stickers ~2.1MB = ~93% of catalog bytes) are only read
        // on demand — gun-skin apply, the !skins menu (built lazily per-invoke), and !sticker —
        // never during menu build. Parsing them on the main thread freezes the current sim tick
        // (~200ms "UNEXPECTED LONG FRAME") on a live `css_plugins reload`. Parse off-thread; the
        // readers all tolerate the brief empty window (guns apply on next spawn once ready).
        Task.Run(() =>
        {
            Utility.LoadSkinsFromFile(ModuleDirectory + $"/data/skins_{Config.SkinsLanguage}.json", Logger);
            Utility.LoadStickersFromFile(ModuleDirectory + $"/data/stickers_{Config.SkinsLanguage}.json", Logger);
        });

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
            MaximumPoolSize = (uint)Math.Max(4, config.DatabaseMaxPoolSize),
            SslMode = MySqlSslMode.None,
            AllowPublicKeyRetrieval = true,
        };

        Database = new Database(builder.ConnectionString);
        WeaponSync = new WeaponSynchronization(Database, Config);

        _ = Utility.CheckDatabaseTables();
        _localizer = Localizer;

        Utility.Config = config;
    }

    // Unhook native function hooks before plugin unloads / hot-reloads. CSSharp tracks
    // RegisterListener / RegisterEventHandler subscriptions and removes them automatically on
    // unload, but VirtualFunctions.*.Hook(...) is registered against a process-wide static
    // function pointer — without an explicit Unhook, every hot-reload stacks another hook
    // and OnGiveNamedItemPost fires N times per weapon-give, leaking closures.
    public override void Unload(bool hotReload)
    {
        try
        {
            VirtualFunctions.GiveNamedItemFunc.Unhook(OnGiveNamedItemPost, HookMode.Post);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Unhook GiveNamedItemFunc failed: {Message}", ex.Message);
        }

        base.Unload(hotReload);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try
        {
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
            Logger.LogError("Error while loading required plugins");
            throw;
        }
    }
}
