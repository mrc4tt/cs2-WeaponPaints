# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Critical rules (violating these crashes the server or breaks live loads)

These are the highest-severity invariants. Each is expanded in the section named in parentheses.

1. **Never do heavy work on the main thread inside `Load` / event handlers / listeners.** `Load(bool hotReload)` runs on the game's main simulation thread, so a live `css_plugins reload` freezes the current tick for the whole duration (a big synchronous JSON parse or DB call shows up in-console as `UNEXPECTED LONG FRAME DETECTED ... Server Simulation`). The large `data/` catalogs — `skins_*.json` (~0.6MB) and `stickers_*.json` (~2.1MB) — are parsed off-thread via `Task.Run` because nothing reads them during menu build; only the small catalogs the `OnAllPluginsLoaded` menu builder iterates (gloves/agents/music/pins) are parsed synchronously. Keep that split — do not move the big parses back onto the main thread, and don't add new synchronous file/DB/native work to `Load`. (see **Threading and lifetime rules**)
2. **Marshal back to the main thread before touching entities from any `Task.Run`/DB callback** — `Server.NextFrame` / `Server.NextWorldUpdate`. Reads/writes on `CCSPlayerController`, pawns, or weapons off-thread crash natively. (see **Threading and lifetime rules**)
3. **`OnPlayerSpawn` cosmetics are deferred with `AddTimer(0.15f, ...)` on purpose** — applying on spawn or on `NextFrame` crashes in native `SetModel`/`SetBodygroup` (pawn scene nodes not initialized). Don't "simplify". (see **Threading and lifetime rules**)
4. **Clear every `player.Slot`-keyed state in `OnPlayerDisconnect`.** New per-player dictionaries must be added to that block or they leak across the slot's next occupant. (see **Threading and lifetime rules**)
5. **`Stickers` is slot-indexed (`Stickers[i]` == sticker slot i).** Never `Add` without keeping the list 5-slot-aligned; empty slot = `StickerInfo` with `Id == 0`. (see **Stickers and keychains**)
6. **Bump `WeaponPaintsConfig.Version` on any `Config.cs` change** so the plugin's own `MigrateConfigFile` rewrites existing user configs. CSSharp's `ConfigManager.Load` does NOT migrate — it only generates a config when absent and otherwise deserializes without touching the file, so new fields would never reach an operator's on-disk `WeaponPaints.json`. `MigrateConfigFile` (in `WeaponPaints.cs`, called from `OnConfigParsed`) re-serializes when the stored `ConfigVersion` is behind, preserving operator values. (see **Runtime configuration layout** / **Conventions**)
7. **`gamedata/weaponpaints.json` ships to `addons/counterstrikesharp/gamedata/`, never inside the plugin folder**, and the runtime-provided DLLs (`CounterStrikeSharp.API.dll`, `CS2MenuManager.dll`, etc.) must be deleted from the release. Without the gamedata sig the plugin refuses to load. (see **Build and deploy**)
8. **Build `PlayerInfo` via `PlayerInfo.From(player)` only; use `.Print()` (not `PrintToChat`) for chat.** (see **Code layout**)

## What this project is

A CS2 server plugin (C#, .NET 10) built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) that lets players pick weapon paints, knives, gloves, agents, pins, MVP music, stickers, and keychains — persisted in MySQL and applied each spawn. Not a standalone program; it is loaded by the CSSharp runtime inside a CS2 dedicated server.

Targets `net10.0` and `CounterStrikeSharp.API` 1.0.369 (see `WeaponPaints.csproj`). There is no `MinimumApiVersion` attribute on the plugin class.

## Build and deploy

```bash
dotnet restore
dotnet build WeaponPaints.csproj -c Release -o ./WeaponPaints
```

There are no tests. CI (`.forgejo/workflows/release.yml`) replicates the build above and packages a release zip: plugin files land in `staging/addons/counterstrikesharp/plugins/WeaponPaints/`, and top-level `gamedata/weaponpaints.json` is copied to `addons/counterstrikesharp/gamedata/` (must not live inside the plugin folder). CI explicitly deletes `CounterStrikeSharp.API.dll`, `McMaster.NETCore.Plugins.dll`, `Microsoft.DotNet.PlatformAbstractions.dll`, `Microsoft.Extensions.DependencyModel.dll`, and `CS2MenuManager.dll` from the output before zipping — these are provided by the CSSharp runtime / MenuManagerCore and must not ship with the plugin.

Local dev loop: copy the build output into an existing CS2 server's `addons/counterstrikesharp/plugins/WeaponPaints/` directory and use `css_plugins reload WeaponPaints` in server console. The `Load(bool hotReload)` branch in `WeaponPaints.cs` handles reloading connected players.

Menus are powered by **schwarper's [CS2MenuManager](https://github.com/schwarper/CS2MenuManager)** (NuGet `CS2MenuManager` v1.0.42). The server must have `MenuManagerCore` installed alongside the plugin — `CS2MenuManager.dll` itself is provided by that core plugin and is excluded from the release zip. The legacy NfCore `menu:nfcore` capability and the vendored `3rd_party/MenuManagerApi.dll` are gone — do not reintroduce them.

## Runtime configuration layout

The plugin reads **two** JSON configs, split for security reasons:

- `addons/counterstrikesharp/configs/plugins/WeaponPaints/WeaponPaints.json` — behavior config (`WeaponPaintsConfig` in `Config.cs`, generated by CSSharp via `IPluginConfig<T>` only on first run when the file is absent). Bump `WeaponPaintsConfig.Version` (currently `16`) whenever you change this class. Migration is done by the plugin itself: `MigrateConfigFile` in `WeaponPaints.cs` re-serializes the deserialized config back to disk when the stored `ConfigVersion` is older than the class version, adding new fields at their defaults while preserving operator values (incl. DB creds). CSSharp does NOT do this — its `ConfigManager.Load` leaves existing files untouched.
- `addons/counterstrikesharp/configs/plugins/WeaponPaints/weaponpaintssql.json` — DB credentials only (`WeaponPaintsSqlConfig` in `WeaponPaintsSqlConfig.cs`). `OnConfigParsed` also accepts `WeaponPaintsSQL.json` or a legacy `WeaponPaints.json` in the same directory; it picks whichever has filled credentials. If none exists, it creates `weaponpaintssql.json` as a stub and calls `Unload(false)` — so first run always fails until the operator fills it in.

`WeaponPaintsConfig` also declares `DatabaseHost/Port/User/Password/Name` fields, but the runtime still reads DB credentials exclusively from `SqlConfig`. Those fields are currently unused — don't wire them up without a plan for migrating existing deployments off the split-file layout.

MySQL tables (`wp_player_skins`, `wp_player_knife`, `wp_player_gloves`, `wp_player_agents`, `wp_player_music`, `wp_player_pins`) are auto-created by `Utility.CheckDatabaseTables()` on load. Don't ship manual migration SQL; extend that method instead. `wp_player_skins` also carries the per-weapon sticker/keychain columns: `weapon_sticker_0`..`weapon_sticker_4` (each `id;schema;x;y;wear;scale;rotation`) and `weapon_keychain` (`id;x;y;z;seed`). A sticker/keychain row may exist with `weapon_paint_id = 0` (sticker without a skin) — readers must not filter those out.

## Code layout

The plugin is a single `partial class WeaponPaints : BasePlugin` split across files by concern. When adding functionality, match the existing file; don't introduce a new class unless the concern is genuinely new.

- `WeaponPaints.cs` — lifecycle: `Load`, `OnConfigParsed`, `OnAllPluginsLoaded`. Sets up DB, loads skin/glove/agent/music/pin JSON from `data/`, registers listeners and commands.
- `Variables.cs` — **all** shared static state lives here (see below for detail).
- `Events.cs` — CSSharp event handlers (`OnClientFullConnect`, `OnPlayerSpawn`, `OnPlayerDisconnect`, round/MVP hooks, `OnGiveNamedItemPost`, `OnEntityCreated`) and `RegisterListeners()`.
- `Commands.cs` — chat/console commands and `RegisterCommands()`; also contains the menu setup (`SetupKnifeMenu` etc.) built on schwarper's CS2MenuManager (`CS2MenuManager.API.{Class,Interface,Menu}`). `Utility.CreateMenu` returns an `IMenu`; menus are populated with `AddItem(...)` and shown with `Display(player, 0)`.
- `WeaponAction.cs` / `WeaponAction_RefreshKnife.cs` — actually applying skins, knife swaps, gloves, agents, pins, music, stickers, keychains to in-world entities. See **Stickers and keychains** below.
- `WeaponSynchronization.cs` — all DB reads/writes via Dapper; populates the `GPlayers*` dictionaries on connect and writes them back on disconnect. DB reads use `async`/`await` and log warnings via the injected `Logger` on failure — don't swallow exceptions silently.
- `Utility.cs` — DB table creation, skin/gloves/agents/music/pins JSON loaders, `CreateMenu` wrapper that returns a `PlayerMenu`. `PlayerMenu.Display(player, ...)` consults `MenuTypeManager.GetPlayerMenuType(player)` so each player sees the menu style they picked via MenuManagerCore's settings menu, falling back to MenuManagerCore's server-default. **`Config.MenuType` is deprecated and no longer read** — leave it in the class so old user configs still parse, but configure the default in MenuManagerCore. The `Load*FromFile` methods are also responsible for rebuilding the lookup indexes described below.
- `PlayerInfo.cs` — lightweight DTO carrying player identity across thread boundaries. **Always construct via `PlayerInfo.From(CCSPlayerController)`**; do not inline-build new `PlayerInfo { ... }` objects (the factory is the single source of truth for which fields come off the controller, and it's safe to call on the main thread before handing the struct to a background task).
- `PlayerExtensions.cs` — `.Print()` prepends the localized `wp_prefix` to chat messages; use it instead of `PrintToChat`.

`data/*.json` holds per-language skin/glove/agent/music/collectibles catalogs (key by `Config.SkinsLanguage`, default `en`). `lang/*.json` holds UI translations consumed through `IStringLocalizer`. `gamedata/weaponpaints.json` provides the native signature for `CAttributeList_SetOrAddAttributeValueByName` — without it, the plugin refuses to load.

## Shared state (`Variables.cs`)

Per-player cosmetic state (keyed by `player.Slot`):

- `GPlayersKnife`, `GPlayersGlove`, `GPlayersMusic`, `GPlayersPin` — per-slot × per-`CsTeam` `ConcurrentDictionary`s.
- `GPlayersAgent` — per-slot `(string? CT, string? T)` tuple.
- `GPlayerWeaponsInfo` — per-slot × per-team × per-weapon-defindex `WeaponInfo`. Gloves live here too, keyed by the glove def index (same table `wp_player_skins`), so a glove's `Paint/Seed/Wear` persist and apply through the normal weapon-paint path — `GivePlayerGloves` reads `weaponInfo.Seed/.Wear` when it writes the econ attributes. The `!gfloat`/`!gseed` commands (glove counterparts of the held-weapon `!float`/`!seed`) resolve the current glove from `GPlayersGlove` — gloves are never the held active weapon — then update this `WeaponInfo` and refresh via `GivePlayerGloves`. Setting glove float/seed on a *real Steam-inventory* glove the plugin didn't apply is not supported: the existing paint id can't be read back off `CEconItemView`, so it can't be re-applied/persisted without wiping the skin.
- `OriginalPawnModel` — per-slot cache of CS2's default faction/map pawn model, captured in `OnPlayerSpawn` **before** any custom agent overrides it. Used to restore immediately when the player picks "Agent | Default" without a kill+respawn.

Static catalogs loaded from `data/*.json`:

- Raw lists: `SkinsList`, `PinsList`, `GlovesList`, `AgentsList`, `MusicList`.
- **Lookup indexes** (O(1) reads, rebuilt inside the matching `Utility.Load*FromFile` method whenever the list is reloaded — *do not* mutate the lists directly without rebuilding):
  - `SkinsByWeaponName : Dictionary<string, List<JObject>>`
  - `SkinsLegacyModelIndex : Dictionary<(int defindex, int paint), bool>`
  - `GlovesByPaintName : Dictionary<string, JObject>`
  - `AgentsByNameAndTeam : Dictionary<(string name, int team), JObject>`
  - `AgentsByTeam : Dictionary<int, List<JObject>>`
  - `AgentsModelSet : HashSet<string>`
- `WeaponDefindex` (int → class name) and its reverse `WeaponDefindexByName` (class name → int); `WeaponList` (class name → display name). Use `WeaponDefindexByName.TryGetValue(...)` instead of `WeaponDefindex.FirstOrDefault(kv => kv.Value == name)`.

Other static infrastructure: `WeaponSync`, `Database`, `_localizer`, and the `CAttributeList_SetOrAddAttributeValueByName` memory function pointer loaded from `gamedata/weaponpaints.json`. (CS2MenuManager menus are constructed on demand inside `Utility.CreateMenu` — there is no shared `MenuApi` singleton.)

Performance cache: `PlayersBySteamId` is a `ConcurrentDictionary<ulong, CCSPlayerController>` to avoid O(n) scans inside `OnEntityCreated` — keep it in sync with `Players` in every connect/disconnect/hot-reload path.

## Stickers and keychains

Stored on `WeaponInfo.Stickers` (`List<StickerInfo>`, defined in `WeaponInfo.cs`) and `WeaponInfo.KeyChain` (`KeyChainInfo`). Applied by `SetStickers` / `SetKeychain` in `WeaponAction.cs`, which write engine attributes via `CAttributeListSetOrAddAttributeValueByName` onto the weapon's `NetworkedDynamicAttributes`.

Rules that are easy to break:

- **`Stickers` is slot-indexed: `Stickers[i]` IS sticker slot `i` (0-4).** `SetStickers` writes `"sticker slot {i} id/wear/scale/rotation"` by absolute index. The DB loader pads to a fixed 5-element list so an empty/invalid middle column can't shift later stickers into earlier slots, and `ApplySticker` pads-then-assigns `Stickers[slot]`. Never `Add` a sticker without keeping the list slot-aligned. An empty slot is a `StickerInfo` with `Id == 0`, which `SetStickers` writes as `id 0` to clear that slot.
- **Native anchor positions only — do NOT write `"sticker slot N offset x/y"` or `"schema"`.** Leaving offsets unset makes the engine place each sticker at the weapon model's built-in per-slot anchor (the "original" position). The old web-derived 2D→3D offsets were approximate and bunched stickers onto one spot; that path was removed. `OffsetX/Y`/`Schema` are still parsed and persisted for DB compatibility but are not applied.
- **Sticker/keychain without a skin works.** `HasChangedPaint` rejects paint-0 entries; the sticker/keychain paths use `TryGetWeaponInfo` (paint-independent) instead. When a weapon has stickers/keychain but no custom paint, `GivePlayerWeaponSkin` calls `ApplyStickersAndKeychainOnly`, which applies just those attributes without `RemoveAll()` so a Steam-inventory/default skin stays intact. The `!sticker` command and `ApplySticker` no longer require an existing skin.
- **Stickers only re-render when wear changes.** `IncrementWearForWeaponWithStickers` oscillates `FallbackWear` by a tiny jitter around the real value every refresh so the decals re-render without the float drifting forever. Don't replace it with a monotonic increment.

## Threading and lifetime rules — read before editing

CSSharp events and listeners fire on the game's main thread. Database work runs on the thread pool via `Task.Run`, so code in `WeaponSynchronization` and in the `_ = Task.Run(async () => ...)` blocks must marshal back before touching entities:

- Use `Server.NextFrame(() => ...)` or `Server.NextWorldUpdate(() => ...)` before calling anything on `CCSPlayerController`, pawns, or weapons from a background task.
- `OnPlayerSpawn` intentionally defers cosmetic application with `AddTimer(0.15f, ...)` — applying synchronously or on `NextFrame` crashes in native `SetModel`/`SetBodygroup` because pawn scene nodes aren't initialized yet. Don't "simplify" this.
- `OnPlayerSpawn` also captures `OriginalPawnModel[player.Slot]` from `pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName` *before* the agent override runs. Do this read while the pawn is still on its default model — later reads just return whatever custom agent has been applied.
- `OnClientFullConnect` kicks off `GetPlayerData` on a background task and then, on reconnect races where the player already spawned before the DB call returned, kills and regives the knife so `OnGiveNamedItemPost` + `OnEntityCreated` both fire (the dual-event mechanism is what makes the skin stick). The comments in `Events.cs` explain the exact race — respect them.
- `GivePlayerWeaponSkin` captures `HasChangedKnife` output into a local variable because the background `GetPlayerData` can `TryRemove` the slot between the check and a direct indexer read.
- Player state keyed by `player.Slot` must be cleared in `OnPlayerDisconnect` (it already does this for every `GPlayers*` dictionary, `OriginalPawnModel`, `_temporaryPlayerWeaponWear`, `CommandsCooldown`, and `PlayersBySteamId` — add to that block if you introduce a new one).
- Prefer `async`/`await` with `QueryAsync` over blocking `Query` inside DB helpers. The `GetPlayerData` helpers are now `async Task` and `await`ed in sequence; don't reintroduce sync Dapper calls.

## Conventions

- Configs: bump `WeaponPaintsConfig.Version` when changing `Config.cs` so the plugin's `MigrateConfigFile` (not CSSharp) rewrites existing user configs with the new fields.
- Formatting: C# files use 4-space indentation, braces on new lines, file-scoped namespace where already present (mixed — match the file).
- Logging: use the injected `Logger` (`Microsoft.Extensions.Logging`) for anything user-visible in server console, not `Console.WriteLine`. `Utility.Log` exists but is only used for dev prints. Don't swallow `catch (Exception) { }` — log at least a warning with the exception message.
- Build `PlayerInfo` via `PlayerInfo.From(player)` only; there is no `IpAddress` field anymore.
- `Patches/` was removed; the old platform-specific memory patches are gone in favor of the single `CAttributeList_SetOrAddAttributeValueByName` signature. Don't resurrect the pattern.
