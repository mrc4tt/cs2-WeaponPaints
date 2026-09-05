# Changelog

All notable changes to this project are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

### Changed

- Knife pickup events are debounced per player. A pickup fires once per regive, so spawning, buying, or any
  kill+regive cycle could deliver several within a few ticks, each running a full inventory walk. A burst
  now collapses into a single refresh 0.10s later, re-resolving the player by slot rather than capturing
  the controller across the delay.

### Added

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

## [4.0.0]

Baseline for this changelog. Earlier history is in the git log.
