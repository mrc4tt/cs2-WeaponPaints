# Loadout-hook gamedata — verification notes

Evidence trail for the `GetItemInLoadout` / inventory-simulator entries in `gamedata/weaponpaints.json`.
The gamedata file itself must stay pure CSSharp schema, so the reasoning lives here.

**Verified against CS2 build 14174** (`libserver.so` + `server.dll`) on 2026-08-04.
Linux signatures came from `rosetta-cs2.json` (source_build 24537688); Windows signatures came from
[MarkBilegt/WeaponPaints](https://github.com/MarkBilegt/WeaponPaints). Every signature was re-confirmed
**unique** on both binaries, every function was **decompiled and identified on both**, and all four
offsets were derived **independently on both**.

**Nothing here has been tested at runtime.** None of it has been loaded through CSSharp's `GameData`
or called. The entries are shipped ahead of the code that will use them.

## Signatures

| Entry | Linux | Windows | Identification |
|---|---|---|---|
| `CCSPlayerInventory::GetItemInLoadout` | `0x14af1b0` | `0x1809d7c90` | Decompiled both: bounds `team<=3` / `slot<=0x38`, stride `57*team`, loadout array at `inventory+144` with 16-byte entries, spray slot 56 special-cased via `item_sub_position2`/`spray0`. |
| `CEconItemView::CEconItemView` | `0x1b5b540` | `0x180f1d120` | Linux: installs vtable `off_2521668` plus sub-object vtables at +112/+232, clears the init byte at +104, `memset(+352, 0, 0xA0)`, reaches +513. Windows: proven by retained MSVC symbols — loads `??_7CEconItemView@@6B@` into `[rcx]` and `??_7NetworkVar_m_AttributeList@CEconItemView@@6B@` into `[rdi+0x70]`, clears the init byte at `+0x68` (104). |
| `CEconItemView::operator=` | `0x1b5add0` | `0x180f1e150` | Linux: copies fields 56/60/64/72/80/84/88/92/96/104, the sub-objects at +112/+232 and the 161-byte blobs at +352/+513, firing the state-changed callback (vtable+248) per changed field, returns `dst`. Field set matches the constructor. Windows: proven by use — `SetWearables` calls this exact address to copy the slot-41 glove view. |
| `CCSPlayerInventory::SendInventoryUpdateEvent` | `0x1ae0fd0` | `0x180f14120` | Both decompiled and equivalent: read the inventory's own SOID (id at +16, type at +24), pass the id on only when type == 1, tail-call a single dispatch function. |
| `CCSPlayer_ItemServices::SetWearables` | `0x1552020` | `0x180a69370` | Both walk the owner's loadout for slot 38 then slot 41, compare five item ids against pawn fields (dword-index 1398..1402 linux / 1218 windows), then copy the slot-41 glove item view in via `CEconItemView::operator=`. Slot 41 is the fork's `GloveLoadoutSlot`. |
| `CCSPlayerPawn::SetModelFromClass` | `0xaea650` | `0x1801ebc60` | Both pick the hardcoded faction default — `agents/models/tm_phoenix/tm_phoenix.vmdl` or `agents/models/ctm_sas/ctm_sas.vmdl` — off the team byte (pawn+1572 linux, pawn+836 windows; `==2` means T). |
| `CCSPlayerPawn::SetModelFromLoadout` | `0xaf0190` | `0x1801c7880` | Both fetch loadout slot 38 (agent), check the item view's init flag at +104, resolve the definition through `CEconItemDefinition`/`CCStrike15ItemDefinition` RTTI, read the model path at `definition+936`, fall back to `ctm_sas.vmdl`. Windows additionally shows the female-model test against `_fem` / `fbihrt_epic` / `swat_epic`. |

`CAttributeList_SetOrAddAttributeValueByName` (already shipped, key keeps its underscore form — renaming
it breaks the existing `GameData.GetSignature` call) resolves to `0x1b5e3a0` / `0x180f2add0` on this build.
Its shipped pattern was regenerated from scratch in IDA and came back byte-for-byte identical, so that
entry is sound.

## `UpdateItemView` (already shipped, and it had drifted)

`CEconItemView::Update` — linux `0x1b5e570`, windows `0x180f26fa0`. Takes `(this, pItem)`, falls back to
`*(this+96)` when the second argument is null (which is why the plugin can call it with `nint.Zero` —
field 96 is the `CEconItem*` that `operator=` copies), then walks the item's attribute array at
`*(pItem+32)` with a 16-byte stride and rebuilds the networked attribute block at `this+240`. The linux
body references the `kill eater` attribute, confirming it is the econ view rebuild.

Located by the call pair in `SetWearables`, which on both platforms does
`operator=(view, src)` immediately followed by `Update(view, 0)`.

**The windows signature was stale and matched nothing on build 14174.** Unlike the loadout entries above,
this one is resolved at static-field initialisation in `Variables.cs`, not lazily — so it took the whole
plugin down on windows rather than failing at the call site. The function had not moved; only its prologue
changed (`55 56 57` became `53 57`), which is why the old pattern's first ten bytes still matched. The
linux signature was re-checked at the same time and is still unique and correct.

This entry is **fork-local** — upstream `Nereziel/cs2-WeaponPaints` and `daffyyyy/cs2-WeaponPaints` ship
only `CAttributeList_SetOrAddAttributeValueByName`, so there is no upstream to sync it from. Re-verify it
on both binaries every CS2 update.

### Weakest link

`SendInventoryUpdateEvent` is the shortest signature in the file — 8 bytes on Linux, 7 on Windows — and it
starts mid-prologue. Unique on build 14174, but it is the first one to re-check after any CS2 update.
Its body is a thin forwarder, so the *name* is taken on rosetta's and the fork's word; the identification
as the same cross-platform function is solid, the label is not independently proven.

## Offsets

| Entry | Linux | Windows | Identification |
|---|---|---|---|
| `CCSPlayerController_InventoryServices::m_pInventory` | 112 | 112 | Linux: thunk `sub_1593B30` = `GetItemInLoadout(this+112, team, slot)`; cross-checked by `sub_15994A0` calling `(this+112, team 0, slot 54 = music kit)` and storing into `*(this+64)`, which the schema names `m_unMusicID`; used again by `SetWearables`. Windows: thunks `sub_180AA92F0` and `sub_180AC58F0` both `add rcx, 70h; jmp <CPlayerInventory method>`, the first jumping to `GetItemInLoadoutFilteredByProhibition`. |
| `CCSPlayerInventory::m_pSOCache` | 104 | 104 | Read side: `CPlayerInventory::GetMaxItemCount` (`sub_14AF790` / `sub_1809D8280`) loads `*(this+104)`, calls `FindTypeCache(cache, 7)`, returns `1000 + extra_backpack_slots` clamped to 2000. Write side: `CPlayerInventory::SOCacheSubscribed` (`sub_1B390F0` / `sub_180F13AD0`) stores the incoming cache pointer to `*(this+104)`; the Windows build leaks the source path `econ_item_inventory.cpp` and the symbol `OnLoadoutChanged`. |
| `CGCClientSharedObjectCache::m_Owner` | 40 | 40 | Linux: `sub_1B50490` is literally `return *(uint64*)(this+40)`, reached as the virtual at vtable+16; `sub_2289970` inlines the same fast path as `this[5]`/`this[6]`. Windows: RTTI type descriptor → CompleteObjectLocator `0x1819ffc40` → vftable `0x1818a7520`; `vtable[1]` = `movups xmm0, [rcx+28h]; mov rax, rdx; movups [rdx], xmm0; retn`. |
| `CCSPlayer_WeaponServices::DropWeapon` | 29 | 28 | Vtable indices, verified by RTTI walk on both. Linux: mangled name `24CCSPlayer_WeaponServices` at `0x824080` → typeinfo `0x2490980` → vtable `0x2491218` (slot 0 at `0x2491228`); slot 29 = `0x1598770`. Windows: descriptor `0x181d71e48` → three COLs, the offset-0 one (`0x1819b5758`) → vftable `0x181780488`; slot 28 = `0x180aa6170`. Both are `(this, weapon, a3, Vector* a4)`, substitute a default vector when `a4` is null, call the weapon's vtable+2576 (linux) / +2512 (windows), then resolve `CBasePlayerWeapon`/`CCSWeaponBase` RTTI. |

`m_pInventory` is the offset of an **embedded** `CCSPlayerInventory` sub-object, not a pointer field — take
the address (`InventoryServices + 112`), do not dereference it. `m_Owner` is a `SOID_t`: the 64-bit SteamID
sits at +40 and its type tag at +48, so read 8 bytes at +40.

`DropWeapon`'s index legitimately differs across platforms. That is not a typo.

## Trap: rosetta's DropWeapon entry

`rosetta-cs2.json` reports `vtable: 29` for `CCSPlayer_WeaponServices::DropWeapon` with a verified 4-arg
prototype, but its **byte signature for that same name points at a different, non-virtual function**:
`DropWeapon(this, weapon, bool bSilent)` at `0x159d420`, the player-pressed-G handler that prints
`#SFUI_Notice_YouDroppedWeapon` / `#SFUI_Notice_CannotDropWeapon` / `#SFUI_Notice_CannotDropWeaponDuringWarmup`.
It appears nowhere in the vtable.

Rosetta joins signature and vtable/prototype metadata **by name**, and that join is not always right. Use
the index, not that signature. See the *Signatures and gamedata sourcing* section in `CLAUDE.md`.
