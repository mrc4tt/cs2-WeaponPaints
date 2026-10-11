using System.Net;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

// Online item catalog (ByMykel/CSGO-API, Config.SkinApiURL).
//
// The bundled data/*.json files stay the source of truth and the offline fallback. In the
// background the API's files for Config.SkinsLanguage are downloaded and merged in ADDITIVELY:
// entries the bundled files don't have yet are appended (new skins, stickers, charms, agents, music
// kits, pins), and every skin gets its float range, every sticker its tournament / kind / effect /
// capsule for the sticker browser. Nothing is removed or renamed. The merged result is written to
// data/cache/, which Load prefers over a bundled file that is older than it, so new items survive a
// restart even with the API down.
//
// Threading: download + parse + merge + index build + cache write all run on the thread pool; the
// game thread only swaps references (Install*). Gloves / agents / music / pins menus are built once
// at load, so their new entries show up in the menus from the next plugin load (their lookups are
// swapped immediately).
public partial class WeaponPaints
{
    private static readonly HttpClient CatalogHttp = CreateCatalogHttp();

    private static HttpClient CreateCatalogHttp()
    {
        var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WeaponPaints");
        return http;
    }

    private int _catalogRefreshBusy;

    // Completes once the bundled skins/stickers/charms are parsed (they load off-thread in Load).
    private Task _localCatalogTask = Task.CompletedTask;

    // The catalog file to read for `name`: the merged cache when it is at least as new as the
    // bundled file (a plugin update ships newer bundled files, which then win until the next merge).
    internal string CatalogPath(string name)
    {
        var bundled = Path.Combine(ModuleDirectory, "data", $"{name}_{Config.SkinsLanguage}.json");
        var cached = Path.Combine(ModuleDirectory, "data", "cache", $"{name}_{Config.SkinsLanguage}.json");
        try
        {
            if (File.Exists(cached) && (!File.Exists(bundled) || File.GetLastWriteTimeUtc(cached) >= File.GetLastWriteTimeUtc(bundled)))
                return cached;
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not check the catalog cache for {Name}: {Message}", name, ex.Message);
        }
        return bundled;
    }

    private void RegisterCatalogCommands()
    {
        AddCommand("css_wp_reloadcatalog", "Re-download / reload the item catalog", (player, command) =>
        {
            if (player != null && !AdminManager.PlayerHasPermissions(player, Config.CatalogReloadPermission))
            {
                command.ReplyToCommand("[WeaponPaints] No permission.");
                return;
            }
            StartCatalogRefresh(command);
        });

        if (Config.CatalogApiEnabled && Config.CatalogApiRefreshHours > 0)
            AddTimer(Config.CatalogApiRefreshHours * 3600f, () => StartCatalogRefresh(null), TimerFlags.REPEAT);
    }

    // Game thread. `command` (optional) gets the result reply.
    private void StartCatalogRefresh(CommandInfo? command)
    {
        var apiUrl = Config.SkinApiURL ?? "";
        var online = Config.CatalogApiEnabled && !string.IsNullOrWhiteSpace(apiUrl);
        var baseUrl = apiUrl.TrimEnd('/');
        var language = string.IsNullOrWhiteSpace(Config.SkinsLanguage) ? "en" : Config.SkinsLanguage;
        var timeout = TimeSpan.FromSeconds(Math.Max(10, Config.CatalogApiTimeoutSeconds));
        var cacheDir = Path.Combine(ModuleDirectory, "data", "cache");
        var localTask = _localCatalogTask;
        var knownWeapons = new HashSet<int>(WeaponDefindex.Keys);
        var replyTo = command?.CallingPlayer is { IsValid: true } p ? p.Slot : -1;
        var replySteam = command?.CallingPlayer?.SteamID ?? 0;
        var fromConsole = command != null && command.CallingPlayer == null;

        void Reply(string message)
        {
            Server.NextFrame(() =>
            {
                if (fromConsole)
                    Server.PrintToConsole("[WeaponPaints] " + message);
                else if (replyTo >= 0 && Utilities.GetPlayerFromSlot(replyTo) is { IsValid: true } target && target.SteamID == replySteam)
                    target.PrintToConsole("[WeaponPaints] " + message);
            });
        }

        if (Interlocked.CompareExchange(ref _catalogRefreshBusy, 1, 0) != 0)
        {
            command?.ReplyToCommand("[WeaponPaints] A catalog reload is already running.");
            return;
        }

        if (command != null)
            command.ReplyToCommand(online ? "[WeaponPaints] Downloading the item catalog..." : "[WeaponPaints] Reloading the bundled item catalog...");

        // GC pauses stop the game thread too; logged with the result to see what the parse costs it.
        var gcPauseStart = GC.GetTotalPauseDuration();
        var gen2Start = GC.CollectionCount(2);

        _ = Task.Run(async () =>
        {
            try
            {
                await localTask.ConfigureAwait(false);

                if (!online)
                {
                    var skins = Utility.BuildSkins(JsonConvert.DeserializeObject<List<JObject>>(File.ReadAllText(CatalogPath("skins"))) ?? []);
                    var stickers = Utility.BuildStickers(JsonConvert.DeserializeObject<List<JObject>>(File.ReadAllText(CatalogPath("stickers"))) ?? []);
                    var charms = Utility.BuildKeychains(JsonConvert.DeserializeObject<List<JObject>>(File.ReadAllText(CatalogPath("keychains"))) ?? []);
                    Server.NextFrame(() =>
                    {
                        Utility.InstallSkins(skins);
                        Utility.InstallStickers(stickers);
                        Utility.InstallKeychains(charms);
                    });
                    Reply($"Catalog reloaded from files: {skins.List.Count} skins, {stickers.List.Count} stickers, {charms.List.Count} charms.");
                    return;
                }

                // Snapshot the installed lists (reference reads; installed lists are never mutated).
                var current = new CatalogMerge.Lists(SkinsList, GlovesList, StickersList, KeychainsList, AgentsList, MusicList, PinsList);
                var merged = await CatalogMerge.FetchAndMergeAsync(CatalogHttp, baseUrl, language, timeout, current, knownWeapons).ConfigureAwait(false);

                var skinsCatalog = Utility.BuildSkins(merged.Lists.Skins);
                var stickersCatalog = Utility.BuildStickers(merged.Lists.Stickers);
                var charmsCatalog = Utility.BuildKeychains(merged.Lists.Keychains);
                var glovesCatalog = Utility.BuildGloves(merged.Lists.Gloves);
                var agentsCatalog = Utility.BuildAgents(merged.Lists.Agents);
                var floatRange = merged.FloatRange;

                Server.NextFrame(() =>
                {
                    Utility.InstallSkins(skinsCatalog);
                    SkinFloatRange = floatRange;
                    Utility.InstallStickers(stickersCatalog);
                    Utility.InstallKeychains(charmsCatalog);
                    Utility.InstallGloves(glovesCatalog);
                    Utility.InstallAgents(agentsCatalog);
                    MusicList = merged.Lists.Music;
                    PinsList = merged.Lists.Pins;
                });

                try
                {
                    CatalogMerge.WriteCache(cacheDir, language, merged.Lists);
                }
                catch (Exception ex)
                {
                    // Installed in memory already; only the restart cache is missing.
                    Logger.LogWarning("Could not write the catalog cache to {Dir}: {Message}", cacheDir, ex.Message);
                }

                var summary = $"Item catalog merged from the online API: +{merged.Added.Skins} skins, +{merged.Added.Gloves} gloves, +{merged.Added.Stickers} stickers, +{merged.Added.Keychains} charms, +{merged.Added.Agents} agents, +{merged.Added.Music} music kits, +{merged.Added.Pins} pins. GC pauses meanwhile: {(GC.GetTotalPauseDuration() - gcPauseStart).TotalMilliseconds:F0} ms ({GC.CollectionCount(2) - gen2Start} gen2).";
                Logger.LogInformation(summary);
                Reply(summary);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Online item catalog failed, keeping the bundled data: {Message}", ex.Message);
                Reply("Catalog reload failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _catalogRefreshBusy, 0);
            }
        });
    }
}

internal static class CatalogMerge
{
    internal sealed record Lists(
        List<JObject> Skins,
        List<JObject> Gloves,
        List<JObject> Stickers,
        List<JObject> Keychains,
        List<JObject> Agents,
        List<JObject> Music,
        List<JObject> Pins);

    internal sealed record Counts(int Skins, int Gloves, int Stickers, int Keychains, int Agents, int Music, int Pins);

    internal sealed record Result(Lists Lists, Counts Added, Dictionary<(int, int), (float Min, float Max)> FloatRange);

    // Collectible types the bundled pins file leaves out (they aren't pins/coins you equip).
    private static readonly HashSet<string> SkippedCollectibleTypes = new(StringComparer.OrdinalIgnoreCase) { "Stars for Operation" };

    internal static async Task<Result> FetchAndMergeAsync(HttpClient http, string baseUrl, string language, TimeSpan timeout,
        Lists current, HashSet<int> knownWeapons)
    {
        using var deadline = new CancellationTokenSource(timeout);
        var token = deadline.Token;

        var skinsTask = FetchAsync(http, $"{baseUrl}/{language}/skins.json", token);
        var stickersTask = FetchAsync(http, $"{baseUrl}/{language}/stickers.json", token);
        var keychainsTask = FetchAsync(http, $"{baseUrl}/{language}/keychains.json", token);
        var agentsTask = FetchAsync(http, $"{baseUrl}/{language}/agents.json", token);
        var musicTask = FetchAsync(http, $"{baseUrl}/{language}/music_kits.json", token);
        var pinsTask = FetchAsync(http, $"{baseUrl}/{language}/collectibles.json", token);
        Task<JsonDocument>[] all = [skinsTask, stickersTask, keychainsTask, agentsTask, musicTask, pinsTask];

        try
        {
            await Task.WhenAll(all).ConfigureAwait(false);

            var floatRange = new Dictionary<(int, int), (float, float)>();
            var (skins, gloves, addedSkins, addedGloves) = MergeSkins(skinsTask.Result, current.Skins, current.Gloves, knownWeapons, floatRange);
            var (stickers, addedStickers) = MergeStickers(stickersTask.Result, current.Stickers);
            var (keychains, addedKeychains) = MergeById(keychainsTask.Result, current.Keychains, _ => true);
            var (agents, addedAgents) = MergeAgents(agentsTask.Result, current.Agents);
            var (music, addedMusic) = MergeById(musicTask.Result, current.Music,
                e => !(ReadString(e, "name")?.StartsWith("StatTrak", StringComparison.OrdinalIgnoreCase) ?? false));
            var (pins, addedPins) = MergeById(pinsTask.Result, current.Pins,
                e => ReadString(e, "type") is { Length: > 0 } type && !SkippedCollectibleTypes.Contains(type));

            return new Result(
                new Lists(skins, gloves, stickers, keychains, agents, music, pins),
                new Counts(addedSkins, addedGloves, addedStickers, addedKeychains, addedAgents, addedMusic, addedPins),
                floatRange);
        }
        finally
        {
            foreach (var task in all)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result.Dispose();
            }
        }
    }

    private static async Task<JsonDocument> FetchAsync(HttpClient http, string url, CancellationToken token)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new InvalidDataException($"{url} is not a JSON array");
        }
        return document;
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadNestedString(JsonElement element, string property, string nested)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object ? ReadString(value, nested) : null;

    // def_index is a string in the API today; accept a number too so a format change can't
    // silently merge zero items.
    private static string? ReadId(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int ReadIntLoose(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(value.GetString(), out var n) => n,
            _ => 0,
        };
    }

    private static (List<JObject> Skins, List<JObject> Gloves, int AddedSkins, int AddedGloves) MergeSkins(
        JsonDocument doc, List<JObject> currentSkins, List<JObject> currentGloves, HashSet<int> knownWeapons,
        Dictionary<(int, int), (float, float)> floatRange)
    {
        var skinKeys = new HashSet<(int, int)>();
        foreach (var skin in currentSkins)
            skinKeys.Add(((int?)skin["weapon_defindex"] ?? 0, (int?)skin["paint"] ?? 0));
        var gloveKeys = new HashSet<(int, int)>();
        foreach (var glove in currentGloves)
            gloveKeys.Add(((int?)glove["weapon_defindex"] ?? 0, (int?)glove["paint"] ?? 0));

        var newSkins = new List<JObject>();
        var newGloves = new List<JObject>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("weapon", out var weapon) || weapon.ValueKind != JsonValueKind.Object)
                continue;
            var defIndex = ReadIntLoose(weapon, "weapon_id");
            var paint = ReadIntLoose(item, "paint_index");
            if (defIndex <= 0 || paint <= 0)
                continue;

            if (item.TryGetProperty("min_float", out var min) && min.ValueKind == JsonValueKind.Number
                && item.TryGetProperty("max_float", out var max) && max.ValueKind == JsonValueKind.Number)
                floatRange[(defIndex, paint)] = (min.GetSingle(), max.GetSingle());

            var name = ReadString(item, "name") ?? "";
            var image = ReadString(item, "image") ?? "";
            if (name.Length == 0)
                continue;

            if (ReadNestedString(item, "category", "name") == "Gloves")
            {
                if (gloveKeys.Add((defIndex, paint)))
                {
                    newGloves.Add(new JObject
                    {
                        ["weapon_defindex"] = defIndex,
                        ["paint"] = paint.ToString(),
                        ["image"] = image,
                        ["paint_name"] = name,
                    });
                }
                continue;
            }

            // A weapon the plugin can't give/apply (not in WeaponDefindex) would only be menu noise.
            var className = ReadString(weapon, "id");
            if (!knownWeapons.Contains(defIndex) || string.IsNullOrEmpty(className) || !skinKeys.Add((defIndex, paint)))
                continue;

            newSkins.Add(new JObject
            {
                ["weapon_defindex"] = defIndex,
                ["weapon_name"] = className,
                ["paint"] = paint.ToString(),
                ["image"] = image,
                ["paint_name"] = name,
                ["legacy_model"] = item.TryGetProperty("legacy_model", out var legacy) && legacy.ValueKind == JsonValueKind.True,
            });
        }

        // Copies, so the installed objects are never mutated while the game thread reads them.
        var skins = new List<JObject>(currentSkins.Count + newSkins.Count);
        foreach (var skin in currentSkins)
        {
            var copy = (JObject)skin.DeepClone();
            var key = ((int?)skin["weapon_defindex"] ?? 0, (int?)skin["paint"] ?? 0);
            if (floatRange.TryGetValue(key, out var range))
            {
                copy["min_float"] = range.Item1;
                copy["max_float"] = range.Item2;
            }
            skins.Add(copy);
        }
        foreach (var skin in newSkins)
        {
            var key = ((int?)skin["weapon_defindex"] ?? 0, (int?)skin["paint"] ?? 0);
            if (floatRange.TryGetValue(key, out var range))
            {
                skin["min_float"] = range.Item1;
                skin["max_float"] = range.Item2;
            }
            skins.Add(skin);
        }

        var gloves = new List<JObject>(currentGloves.Count + newGloves.Count);
        gloves.AddRange(currentGloves);
        gloves.AddRange(newGloves);

        return (skins, gloves, newSkins.Count, newGloves.Count);
    }

    private static (List<JObject> List, int Added) MergeStickers(JsonDocument doc, List<JObject> current)
    {
        var meta = new Dictionary<string, (string? Event, string? Kind, string? Effect, string? Capsule)>();
        var fresh = new List<(string Id, string Name, string Image)>();
        var known = new HashSet<string>(current.Select(s => s["id"]?.ToString() ?? ""));

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = ReadId(item, "def_index");
            var name = ReadString(item, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                continue;

            string? capsule = null;
            if (item.TryGetProperty("crates", out var crates) && crates.ValueKind == JsonValueKind.Array)
            {
                foreach (var crate in crates.EnumerateArray())
                {
                    capsule = ReadString(crate, "name");
                    if (!string.IsNullOrEmpty(capsule))
                        break;
                }
            }

            meta[id] = (ReadNestedString(item, "tournament", "name"), ReadString(item, "type"), ReadString(item, "effect"), capsule);
            if (known.Add(id))
                fresh.Add((id, name, ReadString(item, "image") ?? ""));
        }

        var list = new List<JObject>(current.Count + fresh.Count);
        foreach (var sticker in current)
            list.Add(WithStickerMeta((JObject)sticker.DeepClone(), meta));
        foreach (var (id, name, image) in fresh)
            list.Add(WithStickerMeta(new JObject { ["id"] = id, ["name"] = name, ["image"] = image }, meta));

        return (list, fresh.Count);
    }

    private static JObject WithStickerMeta(JObject sticker, Dictionary<string, (string? Event, string? Kind, string? Effect, string? Capsule)> meta)
    {
        if (!meta.TryGetValue(sticker["id"]?.ToString() ?? "", out var m))
            return sticker;

        // "meta" tells StickerBrowseIndex not to guess the tournament from the name.
        sticker["meta"] = 1;
        if (!string.IsNullOrEmpty(m.Event))
            sticker["event"] = m.Event;
        if (!string.IsNullOrEmpty(m.Kind))
            sticker["kind"] = m.Kind;
        if (!string.IsNullOrEmpty(m.Effect))
            sticker["effect"] = m.Effect == "Other" ? "Paper" : m.Effect;
        if (!string.IsNullOrEmpty(m.Capsule))
            sticker["capsule"] = m.Capsule;
        return sticker;
    }

    // Music / pins / charms: {id, name, image}, keyed by def_index.
    private static (List<JObject> List, int Added) MergeById(JsonDocument doc, List<JObject> current, Func<JsonElement, bool> include)
    {
        var known = new HashSet<string>(current.Select(e => e["id"]?.ToString() ?? ""));
        var list = new List<JObject>(current);
        var added = 0;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var id = ReadId(item, "def_index");
            var name = ReadString(item, "name");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !include(item) || !known.Add(id))
                continue;
            list.Add(new JObject { ["id"] = id, ["name"] = name, ["image"] = ReadString(item, "image") ?? "" });
            added++;
        }

        return (list, added);
    }

    private static (List<JObject> List, int Added) MergeAgents(JsonDocument doc, List<JObject> current)
    {
        var known = new HashSet<string>(current.Select(a => a["model"]?.ToString() ?? ""));
        var list = new List<JObject>(current);
        var added = 0;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // "agents/models/tm_professional/tm_professional_varf5.vmdl" -> "tm_professional/tm_professional_varf5"
            var modelPath = ReadString(item, "model_player");
            var name = ReadString(item, "name");
            if (string.IsNullOrEmpty(modelPath) || string.IsNullOrEmpty(name))
                continue;
            const string prefix = "agents/models/";
            var model = modelPath.StartsWith(prefix, StringComparison.Ordinal) ? modelPath[prefix.Length..] : modelPath;
            if (model.EndsWith(".vmdl", StringComparison.Ordinal))
                model = model[..^5];

            var team = ReadNestedString(item, "team", "id") switch
            {
                "terrorists" => 2,
                "counter-terrorists" => 3,
                _ => 0,
            };
            if (team == 0 || !known.Add(model))
                continue;

            list.Add(new JObject { ["team"] = team, ["image"] = ReadString(item, "image") ?? "", ["model"] = model, ["agent_name"] = name });
            added++;
        }

        return (list, added);
    }

    // Atomic per file (temp + move): a crash mid-write never leaves a truncated catalog behind.
    internal static void WriteCache(string cacheDir, string language, Lists lists)
    {
        Directory.CreateDirectory(cacheDir);
        Write("skins", lists.Skins);
        Write("gloves", lists.Gloves);
        Write("stickers", lists.Stickers);
        Write("keychains", lists.Keychains);
        Write("agents", lists.Agents);
        Write("music", lists.Music);
        Write("collectibles", lists.Pins);

        void Write(string name, List<JObject> list)
        {
            var path = Path.Combine(cacheDir, $"{name}_{language}.json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(list, Formatting.None));
            File.Move(temp, path, overwrite: true);
        }
    }
}
