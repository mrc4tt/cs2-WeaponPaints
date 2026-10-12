# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [4.1.0] - 2026-10-12

### Added

- **StatTrak that counts the right weapon.** Kills are credited to the weapon named in `player_death`, not
  the one held when the event lands. Dirty counters are flushed every `StatTrakFlushSeconds`, on disconnect
  and on unload.
- **Customize menu after a skin pick**: wear, seed, StatTrak, name tag, charm and stickers for any weapon,
  not only the one in hand. New `!nametag` and `!cp` (copy the spectated player's skin).
- **Sticker browser** on `!sticker`: tournaments, sticker kinds and effects, capsules, search by chat prompt,
  apply to all slots, remove all.
- **Online catalog**: skins, stickers and the other catalogs are merged from the ByMykel API
  (`CatalogApiEnabled`, `SkinApiURL`) off the game thread and cached in `data/cache/`, which wins over an
  older bundled file. `css_wp_reloadcatalog` re-runs the merge.
- `css_wp_refresh <all|steamid64|#userid|name>` - `!wp` for other players (`RefreshAdminPermission`; the
  server console is always allowed).
- `VipCommands` gate that can put any player command behind a permission.
- Paint memory (`PaintMemoryEnabled`): wear and seed are remembered per weapon and paint, so switching back
  to a paint restores them.
- Charm sticker and highlight attributes; music kit and pin are re-asserted when the game drifts them.
- Skin float range clamp at render time (`ClampWearToSkinRange`). Stored values are not changed.
- **Stickers and charms on the C4** (CS2 1.41.8.2). A `wp_player_skins` row with `weapon_defindex = 49`
  (always `weapon_paint_id = 0`) now applies its stickers and charm to the bomb through the existing
  "sticker without skin" path. The C4 is pinned to its one hd mesh (no `body` bodygroup write), kept out of
  `GiveRandomSkin`, and its fifth sticker slot borrows home 1 via a new `weapon_c4` row in `StickerAnchors`.
  `!sticker` on the held bomb, `!wp` and the web refresh re-apply it in place (`RefreshC4InPlace`) - the bomb is
  never killed and re-given. On `bomb_planted` the stickers are copied onto the `planted_c4` entity when the game
  did not carry any attributes across (untested in game). Ported from SkinHubgg/WeaponPaints `cc57c51`.
- **Chicken pets** (CS2 1.41.8.2): `!pet` menu and `!pet name|seed|color|stage|off` subcommands, a new
  `wp_player_pets` table (auto-created; no existing table is touched), and `PetsEnabled` / `PetPermission` /
  `CommandPet` config (`ConfigVersion` 20). The plugin spawns its own `chicken` carrying pet item 4681 with the
  pet id, stage, seed, name and owner, sets its colour with the `Skin` input on every spawn, re-asserts the
  leader, and restores it after the game's round-start chicken clean-up. Not run on a server yet. Ported from
  SkinHubgg/WeaponPaints `cc57c51` + `ea290cc`; the optional CDN `pets.json` dataset, the CDN thumbnail and the
  experimental team-intro pet were not ported.
- `gamedata/weaponpaints.json` keys may be spelled `CAttributeList::SetOrAddAttributeValueByName` or
  `CAttributeList_SetOrAddAttributeValueByName`, so files from generators that use the `_` form load.

### Changed

- **Every DB write goes through one ordered, coalescing queue per SteamID.** Rapid changes no longer race each
  other to the database, and writes still pending on unload are flushed (a hot reload hands them to the new
  instance).
- `gamedata/weaponpaints.json` updated for CS2 builds 14178, 14181, 14182 and 14190.
- Hot reload skips the CounterStrikeSharp core signature scans once they matched in the running process.
- The online catalog is not downloaded or merged again when the API files are unchanged (ETag / HTTP 304),
  and catalog files are streamed instead of read into one string.
- Config migration (`ConfigVersion` 22) no longer writes a `.bak` copy of `WeaponPaints.json` - the file holds
  DB credentials. Translations completed for all languages.

### Removed

- AstraSkins leftovers. `ConfigVersion` 23 deletes their keys (`TeamPreviewEnabled`, `AgentVoiceEnabled`,
  `ApplyCosmeticsOnBotTakeover`, `MusicKitMvpCounter`, `PrecacheAgentModels`, `MaxNameTagLength`,
  `BlockedNameTagWords`, `CommandSearch`, `CommandReset`, `CommandResetPlayer`, `AdminResetPermission`) from
  `WeaponPaints.json`.

### Fixed

- **Knives and skins silently did not change when CounterStrikeSharp's own `gamedata.json` was outdated.**
  `CEntityInstance_AcceptInput` and `CEntitySystem_AddEntityIOEvent` are now checked against the server binary
  at startup; a missing or non-matching entry is named once and the plugin does not load.
- Paint wear 0 is clamped to 0.001 (weapons, gloves, sticker fallback). Wear 0 could leave the paint
  unrendered on some weapons since CS2 1.41.8.x.
- Choosing "None" as music kit stored 0 and then never cleared the equipped kit.
- Config migration wrote "Migrated by WeaponPaints" comments into `WeaponPaints.json`; it no longer does and
  existing ones are removed.

## [4.0.1] - 2026-09-05

### Added

- **`!g` / `css_gen`**: apply a weapon from an inspect link or gen code. Hex inspect payloads, classic numeric
  codes (`<def> <paint> <seed> <wear> ...`, wear/seed order auto-detected) and the `!gens` 6-tuple parse
  offline; cs2inspects share codes are resolved through api.cs2inspects.com on a background task.
- `gamedata/weaponpaints.json` gains the signatures and offsets for the `CCSPlayerInventory::GetItemInLoadout`
  ("inventory simulator") path: `GetItemInLoadout`, `CEconItemView::CEconItemView`, `CEconItemView::operator=`,
  `SendInventoryUpdateEvent`, `CCSPlayer_ItemServices::SetWearables`, `CCSPlayerPawn::SetModelFromClass`,
  `SetModelFromLoadout`, plus the `m_pInventory` / `m_pSOCache` / `m_Owner` offsets and the `DropWeapon`
  vtable indices. Verified against CS2 build 14174 on both `libserver.so` and `server.dll`. **No code uses
  these yet** — CSSharp resolves signatures lazily, so unused entries are inert. See
  `docs/loadout-gamedata-notes.md` for the per-entry evidence.
- `docs/loadout-gamedata-notes.md` — how each of the above was identified, which signature is the most
  fragile, and the rosetta pitfall where a signature and its vtable metadata describe different functions.
- `CLAUDE.md` — a *Signatures and gamedata sourcing* section covering the rules learned while doing that
  verification.

### Changed

- Knife pickup events are debounced per player. A pickup fires once per regive, so spawning, buying, or any
  kill+regive cycle could deliver several within a few ticks, each running a full inventory walk. A burst
  now collapses into a single refresh 0.10s later, re-resolving the player by slot rather than capturing
  the controller across the delay.

### Fixed

- **Fresh installs failed to load with `Method CAttributeList_SetOrAddAttributeValueByName not found in
  gamedata.json`.** The sig-tracker rename of the key in `gamedata/weaponpaints.json` to
  `CAttributeList::SetOrAddAttributeValueByName` was never carried over to the code, so the static
  initializer threw before the config-time gamedata check could run. Servers still running the
  pre-rename JSON kept working, which masked the breakage. The code now resolves the `::`-named key,
  and the required keys are listed once in `Variables.cs` (`RequiredGamedataKeys`) so future renames
  have a single place to sync.
- **A missing or outdated `weaponpaints.json` now fails with a readable error instead of a
  `TypeInitializationException` cascade.** The signature lookups moved out of the static field
  initializers into `InitGamedataSignatures()`, called from `OnConfigParsed` after the installed
  `gamedata/weaponpaints.json` is validated. Missing file, unparsable JSON, or a missing key is now
  logged with the keys and the expected path, and the plugin unloads cleanly.
- **The plugin could not load on Windows.** The `UpdateItemView` signature no longer matched anything in
  `server.dll` on CS2 build 14174, and that entry is resolved at static-field initialisation
  (`Variables.cs`) rather than lazily, so the failure took the whole plugin down rather than just the one
  call. The function itself has not moved — only its prologue changed, `55 56 57` becoming `53 57` — so
  the old pattern's first ten bytes still matched and then diverged. Re-signed and confirmed unique. The
  Linux signature for the same function was re-checked and is still correct, so Linux was never affected.
- **Sticker changes could fail to render until a map change.** The wear value a weapon carries is part of
  how CS2 decides whether an already-generated weapon material can be reused. The old code oscillated wear
  by ±0.0005 around the real value, which only ever produced two values — editing a sticker flipped wear
  straight back onto a value the *previous* layout had already used, so the client reused the stale
  composite. Every distinct sticker layout is now handed its own wear value, and a value is never reused
  across two different layouts. With stickers applied the displayed float sits at least 0.001 off the real
  value (it was already 0.0005 off) but now stays put instead of oscillating, moving only when the layout
  actually changes.
- **Custom econ item IDs were reissued identically after every plugin reload and map change.** The client
  keys its generated weapon materials on the item ID, so a reload handed back IDs it had already cached
  with an older sticker layout. The low half of the ID is now seeded from wall-clock time on load and on
  map start, keeping the `16384` bucket in the high half that the HUD paint-kit name lookup needs.

## [4.0.0] - 2026-08-03

Baseline for this changelog. Earlier history is in the git log.
