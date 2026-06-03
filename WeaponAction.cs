using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace WeaponPaints
{
    public partial class WeaponPaints
    {
        private void GivePlayerWeaponSkin(CCSPlayerController player, CBasePlayerWeapon weapon)
        {
            if (!Config.Additional.SkinEnabled)
                return;
            // Guard: validate weapon is still valid before any work
            if (weapon == null || !weapon.IsValid)
                return;
            if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out _))
                return;
            // Ensure player is on a valid team before accessing player.Team
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return;
            // Null safety check for weapon AttributeManager
            if (weapon.AttributeManager?.Item == null)
                return;
            // Schema accessors can be null after a CS2/CSSharp update — guard before any
            // RemoveAll()/Invoke that dereferences them, otherwise we throw an opaque NRE.
            if (weapon.AttributeManager.Item.AttributeList == null || weapon.AttributeManager.Item.NetworkedDynamicAttributes == null)
                return;

            bool isKnife = weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");

            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

            // Check FIRST if player has custom skin - if not, preserve Steam inventory skin
            if (!isKnife)
            {
                bool hasCustomSkin = HasChangedPaint(player, weaponDefIndex, out _);
                bool giveRandomSkin = Config.Additional.GiveRandomSkin;

                // If player has no custom skin and random skins are disabled, preserve their inventory skin
                if (!hasCustomSkin && !giveRandomSkin)
                    return;
            }

            // Capture knife value once to avoid race condition with background GetPlayerData task
            // which can TryRemove the key between HasChangedKnife check and direct indexer access
            bool playerHasChangedKnife = HasChangedKnife(player, out var playerKnifeValue);

            switch (isKnife)
            {
                case true when !playerHasChangedKnife:
                    return;

                case true:
                {
                    if (playerKnifeValue == null || !WeaponDefindexByName.TryGetValue(playerKnifeValue, out var newDefIndex))
                        return;

                    // Only call SubclassChange if the knife type actually needs to change.
                    // Both OnGiveNamedItemPost and OnEntityCreated fire for the same knife
                    // entity, causing GivePlayerWeaponSkin to be called twice. Each call to
                    // SubclassChange queues an async engine operation that wipes paint attributes
                    // when processed. By skipping SubclassChange when the defindex already
                    // matches, the second call (from OnEntityCreated's NextWorldUpdate) applies
                    // paint without a pending SubclassChange to wipe it.
                    if (weapon.AttributeManager.Item.ItemDefinitionIndex != (ushort)newDefIndex)
                    {
                        SubclassChange(weapon, (ushort)newDefIndex);
                        weapon.AttributeManager.Item.ItemDefinitionIndex = (ushort)newDefIndex;
                    }

                    weapon.AttributeManager.Item.EntityQuality = 3;

                    weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                    // Don't return; continue to apply knife skins if available
                    break;
                }
                default:
                    weapon.AttributeManager.Item.EntityQuality = 0;
                    break;
            }

            UpdatePlayerEconItemId(weapon.AttributeManager.Item);

            // Update weaponDefIndex in case it changed (for knives)
            weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            int fallbackPaintKit;

            weapon.AttributeManager.Item.AccountID = (uint)player.SteamID;

            bool isLegacyModel;

            if (Config.Additional.GiveRandomSkin && !HasChangedPaint(player, weaponDefIndex, out _))
            {
                // Random skins
                weapon.FallbackPaintKit = GetRandomPaint(weaponDefIndex);
                weapon.FallbackSeed = 0;
                weapon.FallbackWear = 0.01f;

                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", GetRandomPaint(weaponDefIndex));
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture seed", 0);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture wear", 0.01f);

                weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture prefab", GetRandomPaint(weaponDefIndex));
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture seed", 0);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture wear", 0.01f);

                fallbackPaintKit = weapon.FallbackPaintKit;

                if (fallbackPaintKit == 0)
                    return;

                weapon.AttributeManager.Item.Initialized = true;
                weapon.OriginalOwnerXuidLow = (uint)(player.SteamID & 0xFFFFFFFF);
                weapon.OriginalOwnerXuidHigh = (uint)(player.SteamID >> 32);
                try
                {
                    UpdateItemView.Invoke(weapon.AttributeManager.Item.Handle, nint.Zero);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("UpdateItemView (random skin) failed (def {DefinitionIndex}): {Message}", weaponDefIndex, ex.Message);
                }
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
                Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");

                isLegacyModel = !SkinsLegacyModelIndex.TryGetValue((weaponDefIndex, fallbackPaintKit), out var legacyApply) || legacyApply;
                UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
                return;
            }

            if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
            {
                return;
            }
            //Log($"Apply on {weapon.DesignerName}({weapon.AttributeManager.Item.ItemDefinitionIndex}) paint {gPlayerWeaponPaints[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]} seed {gPlayerWeaponSeed[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]} wear {gPlayerWeaponWear[steamId.SteamId64][weapon.AttributeManager.Item.ItemDefinitionIndex]}");

            weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
            weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();

            UpdatePlayerEconItemId(weapon.AttributeManager.Item);

            // CSSharp >=1.0.369 SetStringBytes no longer null-tolerant: Encoding.UTF8.GetBytes(null)
            // throws and aborts the whole apply (no paint/knife). Only set when we have a nametag.
            if (!string.IsNullOrEmpty(weaponInfo.Nametag))
                weapon.AttributeManager.Item.CustomName = weaponInfo.Nametag;
            weapon.FallbackPaintKit = weaponInfo.Paint;

            weapon.FallbackSeed = weaponInfo is { Paint: 38, Seed: 0 } ? _fadeSeed++ : weaponInfo.Seed;

            weapon.FallbackWear = weaponInfo.Wear;

            // After RemoveAll(), the attribute list has no paint-kit entry so the HUD
            // weapon-slot label can't resolve "Weapon | SkinName" and falls back to the
            // bare weapon name. Re-add the same attributes the engine expects on a real
            // econ item — also write to AttributeList so the client-side econ lookup hits
            // a populated list (Steam-inventory weapons have both).
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture prefab", weapon.FallbackPaintKit);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture seed", weapon.FallbackSeed);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "set item texture wear", weapon.FallbackWear);

            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture prefab", weapon.FallbackPaintKit);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture seed", weapon.FallbackSeed);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "set item texture wear", weapon.FallbackWear);
            if (isKnife) { }
            else
            {
                var logName = WeaponDefindex.TryGetValue(weaponDefIndex, out var mappedName) ? mappedName : weapon.DesignerName;
            }

            if (weaponInfo.StatTrak)
            {
                weapon.AttributeManager.Item.EntityQuality = 9;

                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "kill eater score type", 0);

                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater", ViewAsFloat((uint)weaponInfo.StatTrakCount));
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.AttributeList.Handle, "kill eater score type", 0);
            }

            fallbackPaintKit = weapon.FallbackPaintKit;

            if (fallbackPaintKit == 0)
                return;

            if (weaponInfo.KeyChain != null)
                SetKeychain(player, weapon);
            if (weaponInfo.Stickers.Count > 0)
                SetStickers(player, weapon);

            // Mark item as a real econ item and force CEconItemView::Update so the client
            // refreshes its cached attribute view. Without this, the texture renders from
            // FallbackPaintKit but the HUD weapon-slot label falls back to the base weapon
            // name ("M4A4" instead of "M4A4 | Mainframe") after a kill+regive cycle (!wp).
            weapon.AttributeManager.Item.Initialized = true;
            // After kill+regive, GiveNamedItem leaves OriginalOwnerXuid* at 0 so the client
            // treats the weapon as a non-econ pickup and skips the paint-kit name lookup.
            // Set both halves and network the change so the HUD label resolves "Glock-18 |
            // Fade" instead of bare "Glock-18".
            weapon.OriginalOwnerXuidLow = (uint)(player.SteamID & 0xFFFFFFFF);
            weapon.OriginalOwnerXuidHigh = (uint)(player.SteamID >> 32);
            try
            {
                UpdateItemView.Invoke(weapon.AttributeManager.Item.Handle, nint.Zero);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("UpdateItemView (weapon) failed (def {DefinitionIndex}): {Message}", weaponDefIndex, ex.Message);
            }
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackPaintKit");
            Utilities.SetStateChanged(weapon, "CEconEntity", "m_AttributeManager");

            isLegacyModel = !SkinsLegacyModelIndex.TryGetValue((weaponDefIndex, fallbackPaintKit), out var legacyApply2) || legacyApply2;
            UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
        }

        // silly method to update sticker when call RefreshWeapons()
        private void IncrementWearForWeaponWithStickers(CCSPlayerController player, CBasePlayerWeapon weapon)
        {
            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null || weaponInfo.Stickers.Count <= 0)
                return;

            float wearIncrement = 0.001f;
            float currentWear = weaponInfo.Wear;

            var playerWear = _temporaryPlayerWeaponWear.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<int, float>());

            float incrementedWear = playerWear.AddOrUpdate(weaponDefIndex, currentWear + wearIncrement, (_, oldWear) => Math.Min(oldWear + wearIncrement, 1.0f));

            weapon.FallbackWear = incrementedWear;
        }

        private void SetStickers(CCSPlayerController? player, CBasePlayerWeapon weapon)
        {
            if (player == null || !player.IsValid)
                return;

            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

            if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
                return;

            foreach (var sticker in weaponInfo.Stickers)
            {
                int stickerSlot = weaponInfo.Stickers.IndexOf(sticker);

                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} id", ViewAsFloat(sticker.Id));
                if (sticker.OffsetX != 0 || sticker.OffsetY != 0)
                    CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} schema", 0);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} offset x", sticker.OffsetX);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} offset y", sticker.OffsetY);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} wear", sticker.Wear);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} scale", sticker.Scale);
                CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, $"sticker slot {stickerSlot} rotation", sticker.Rotation);
            }

            if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var playerWear) && playerWear.TryGetValue(weaponDefIndex, out float storedWear))
            {
                weapon.FallbackWear = storedWear;
            }
        }

        private void SetKeychain(CCSPlayerController? player, CBasePlayerWeapon weapon)
        {
            if (player == null || !player.IsValid)
                return;

            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

            if (!HasChangedPaint(player, weaponDefIndex, out var value) || value?.KeyChain == null)
                return;

            var keyChain = value.KeyChain;

            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "keychain slot 0 id", ViewAsFloat(keyChain.Id));
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "keychain slot 0 offset x", keyChain.OffsetX);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "keychain slot 0 offset y", keyChain.OffsetY);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "keychain slot 0 offset z", keyChain.OffsetZ);
            CAttributeListSetOrAddAttributeValueByName.Invoke(weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle, "keychain slot 0 seed", ViewAsFloat(keyChain.Seed));
        }

        // Returns a "slotN" command that switches the player back to whatever they had drawn,
        // or null when no active weapon can be determined. Used after refresh paths that
        // forcibly re-equip the knife (slot3) so the player doesn't lose their drawn weapon.
        private static string? GetActiveWeaponSlotCommand(CCSPlayerController player)
        {
            var pawn = player.PlayerPawn.Value;
            var active = pawn?.WeaponServices?.ActiveWeapon.Value;
            if (active == null || !active.IsValid)
                return null;

            var name = active.DesignerName;
            if (string.IsNullOrEmpty(name))
                return null;

            // Slots: 1=primary, 2=secondary, 3=knife, 4=grenades, 5=c4. Taser shares the
            // pistol slot (slot2) — the engine maps "use weapon_taser" to slot2 internally.
            return name switch
            {
                "weapon_c4" => "slot5",
                "weapon_hegrenade" or "weapon_flashbang" or "weapon_smokegrenade" or "weapon_decoy" or "weapon_molotov" or "weapon_incgrenade" => "slot4",
                "weapon_taser" => "slot2",
                "weapon_deagle"
                or "weapon_elite"
                or "weapon_fiveseven"
                or "weapon_glock"
                or "weapon_hkp2000"
                or "weapon_p250"
                or "weapon_usp_silencer"
                or "weapon_cz75a"
                or "weapon_revolver"
                or "weapon_tec9" => "slot2",
                var n when n.StartsWith("weapon_knife", StringComparison.Ordinal) || n == "weapon_bayonet" => "slot3",
                _ => "slot1",
            };
        }

        private void RestoreActiveWeaponSlot(CCSPlayerController player, string? activeSlotCommand)
        {
            if (string.IsNullOrEmpty(activeSlotCommand))
                return;

            AddTimer(
                0.05f,
                () =>
                {
                    try
                    {
                        if (Utility.IsPlayerValid(player) && player.PawnIsAlive)
                            player.ExecuteClientCommand(activeSlotCommand);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("RestoreActiveWeaponSlot failed: {Message}", ex.Message);
                    }
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        // Forces CS2 to re-publish item attributes after we mutate them. Wrapped in try/catch
        // because the gamedata signature can drift on CS2 updates — if the function pointer is
        // null or the signature mismatches, we still want the surrounding glove logic to run.
        private void UpdateGloveItemView(CEconItemView item, int slot)
        {
            try
            {
                UpdateItemView.Invoke(item.Handle, nint.Zero);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "UpdateItemView failed (slot {Slot}, def {DefinitionIndex}): {Message}",
                    slot, item.ItemDefinitionIndex, ex.Message
                );
            }
        }

        // Detaches all wearable entities from the pawn. CS2 doesn't auto-detach the previous
        // glove wearable when ItemDefinitionIndex changes — without this, switching glove
        // types mid-life leaves a "ghost glove" rendered alongside the new one.
        private static void RemovePlayerWearables(CCSPlayerPawn pawn)
        {
            foreach (var wearableHandle in pawn.MyWearables)
            {
                if (!wearableHandle.IsValid || wearableHandle.Value == null || !wearableHandle.Value.IsValid)
                    continue;
                wearableHandle.Value.Remove();
            }
        }

        private static void GiveKnifeToPlayer(CCSPlayerController? player)
        {
            if (!Instance.Config.Additional.KnifeEnabled || player == null || !player.IsValid)
                return;

            if (PlayerHasKnife(player))
                return;

            // Don't substitute weapon_knife_t for T players — CS2 doesn't accept it as a
            // GiveNamedItem classname here, and downstream code (OnGiveNamedItemPost,
            // WeaponDefindexByName lookups, SubclassChange dual-event flow) is keyed on
            // "weapon_knife". CS2's spawn pipeline applies the team-correct model itself.
            player.GiveNamedItem(CsItem.Knife);
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }

        private static bool PlayerHasKnife(CCSPlayerController? player)
        {
            if (!Instance.Config.Additional.KnifeEnabled)
                return false;

            if (player == null || !player.IsValid || !player.PlayerPawn.IsValid)
            {
                return false;
            }

            if (player.PlayerPawn.Value == null || player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
                return false;

            var weapons = player.PlayerPawn.Value.WeaponServices?.MyWeapons;
            if (weapons == null)
                return false;
            foreach (var weapon in weapons)
            {
                if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid)
                    continue;
                if (weapon.Value.DesignerName.Contains("knife") || weapon.Value.DesignerName.Contains("bayonet"))
                {
                    return true;
                }
            }
            return false;
        }

        private void RefreshWeapons(CCSPlayerController? player)
        {
            RefreshWeapons(player, excludeKnife: false);
        }

        private void RefreshSingleWeapon(CCSPlayerController? player, int targetWeaponDefIndex)
        {
            if (!_gBCommandsAllowed)
            {
                return;
            }
            if (player == null || !player.IsValid || player.PlayerPawn.Value == null || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
            {
                return;
            }
            if (player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
            {
                return;
            }

            var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;
            if (weapons.Count == 0)
            {
                return;
            }
            if (player.Team is CsTeam.None or CsTeam.Spectator)
            {
                return;
            }

            string? weaponDesignerName = null;
            int clip1 = 0;
            int reservedAmmo = 0;
            CBasePlayerWeapon? targetWeapon = null;

            foreach (var weapon in weapons)
            {
                if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid || !weapon.Value.DesignerName.Contains("weapon_"))
                    continue;

                // Match by ItemDefinitionIndex
                if (weapon.Value.AttributeManager.Item.ItemDefinitionIndex != targetWeaponDefIndex)
                    continue;

                // If this is a knife, do not kill/regive here. Knife changes are applied in-place.
                if (weapon.Value.DesignerName.Contains("knife") || weapon.Value.DesignerName.Contains("bayonet"))
                {
                    return;
                }

                // Use the correct weapon class name from the defindex mapping instead of the weapon's DesignerName
                // This ensures we give back the correct weapon (e.g., USP-S instead of P2000)
                if (WeaponDefindex.TryGetValue(targetWeaponDefIndex, out var correctWeaponName))
                {
                    weaponDesignerName = correctWeaponName;
                }
                else
                {
                    weaponDesignerName = weapon.Value.DesignerName;
                }
                clip1 = weapon.Value.Clip1;
                reservedAmmo = weapon.Value.ReserveAmmo[0];
                targetWeapon = weapon.Value;
                break;
            }

            if (weaponDesignerName == null || targetWeapon == null)
            {
                return;
            }

            // Kill the weapon
            targetWeapon.AddEntityIOEvent("Kill", targetWeapon, null, "", 0.1f);

            AddTimer(
                0.25f,
                () =>
                {
                    if (!_gBCommandsAllowed)
                        return;
                    if (player == null || !player.IsValid || player.PlayerPawn.Value == null)
                        return;

                    try
                    {
                        var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(weaponDesignerName));
                        Server.NextFrame(() =>
                        {
                            try
                            {
                                if (newWeapon != null && newWeapon.IsValid)
                                {
                                    newWeapon.Clip1 = clip1;
                                    newWeapon.ReserveAmmo[0] = reservedAmmo;
                                    IncrementWearForWeaponWithStickers(player, newWeapon);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning("Error setting ammo on refreshed weapon: " + ex.Message);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Error refreshing single weapon: " + ex.Message);
                    }
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        private void RefreshWeapons(CCSPlayerController? player, bool excludeKnife)
        {
            if (!_gBCommandsAllowed)
                return;
            if (player == null || !player.IsValid || player.PlayerPawn.Value == null || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
                return;
            if (player.PlayerPawn.Value.WeaponServices == null || player.PlayerPawn.Value.ItemServices == null)
                return;

            var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;

            if (weapons.Count == 0)
                return;
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return;

            // Capture before the kill+regive cycle so we can restore the player's drawn weapon
            // after refresh — without this they end up holding the knife (slot3) every time.
            var activeSlotCommand = GetActiveWeaponSlotCommand(player);

            var hasKnife = false;

            Dictionary<string, List<(int, int)>> weaponsWithAmmo = [];

            foreach (var weapon in weapons)
            {
                if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid || !weapon.Value.DesignerName.Contains("weapon_"))
                    continue;

                CCSWeaponBaseGun gun = weapon.Value.As<CCSWeaponBaseGun>();

                if (weapon.Value.Entity == null)
                    continue;
                if (!weapon.Value.OwnerEntity.IsValid)
                    continue;
                if (gun.Entity == null)
                    continue;
                if (!gun.IsValid)
                    continue;

                try
                {
                    CCSWeaponBaseVData? weaponData = weapon.Value.As<CCSWeaponBase>().VData;

                    if (weaponData == null)
                        continue;

                    if (weaponData.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL)
                    {
                        if (!WeaponDefindex.TryGetValue(weapon.Value.AttributeManager.Item.ItemDefinitionIndex, out var weaponByDefindex))
                            continue;

                        int clip1 = weapon.Value.Clip1;
                        int reservedAmmo = weapon.Value.ReserveAmmo[0];

                        if (!weaponsWithAmmo.TryGetValue(weaponByDefindex, out var value))
                        {
                            value = [];
                            weaponsWithAmmo.Add(weaponByDefindex, value);
                        }

                        value.Add((clip1, reservedAmmo));

                        if (gun.VData == null)
                            return;

                        weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.1f);
                    }

                    if (weaponData.GearSlot == gear_slot_t.GEAR_SLOT_KNIFE)
                    {
                        // Skip knife refresh if excludeKnife is true
                        if (!excludeKnife)
                        {
                            weapon.Value?.AddEntityIOEvent("Kill", weapon.Value, null, "", 0.1f);
                            hasKnife = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex.Message);
                }
            }

            AddTimer(
                0.23f,
                () =>
                {
                    if (!_gBCommandsAllowed)
                        return;

                    if (!PlayerHasKnife(player) && hasKnife)
                    {
                        player.GiveNamedItem(CsItem.Knife);
                        player.ExecuteClientCommand("slot3");
                    }

                    foreach (var entry in weaponsWithAmmo)
                    {
                        foreach (var ammo in entry.Value)
                        {
                            var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(entry.Key));
                            Server.NextFrame(() =>
                            {
                                try
                                {
                                    newWeapon.Clip1 = ammo.Item1;
                                    newWeapon.ReserveAmmo[0] = ammo.Item2;

                                    IncrementWearForWeaponWithStickers(player, newWeapon);
                                }
                                catch (Exception ex)
                                {
                                    Logger.LogWarning("Error setting weapon properties: " + ex.Message);
                                }
                            });
                        }
                    }

                    RestoreActiveWeaponSlot(player, activeSlotCommand);
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        private void GivePlayerGloves(CCSPlayerController player)
        {
            if (!Utility.IsPlayerValid(player) || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
                return;

            CCSPlayerPawn? pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                return;

            // Ensure player is on a valid team before glove logic
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return;

            // Capture the active draw before the bodygroup toggle / glove apply so we can
            // restore the player's drawn weapon at the end of the inner timer instead of
            // letting the model refresh leave them on a different slot.
            var activeSlotCommand = GetActiveWeaponSlotCommand(player);

            try
            {
                // Do NOT capture pawn.EconGloves here — CEconItemView is a direct reference
                // into native CS2 memory. If the pawn is re-initialized or the model is reset
                // between now and when the timer fires, the pointer becomes stale and reading
                // its Handle throws AccessViolationException (a Corrupted State Exception that
                // cannot be caught by try/catch). Re-fetch it inside the callback instead.
                {
                    CEconItemView earlyItem = pawn.EconGloves;
                    if (earlyItem == null || earlyItem.Handle == IntPtr.Zero)
                        return;
                    earlyItem.NetworkedDynamicAttributes.Attributes.RemoveAll();
                    earlyItem.AttributeList.Attributes.RemoveAll();
                }

                // No "lastinv" trigger here — it created a transient weapon-switch on every
                // glove apply, accumulating entities on long-running workshop maps and slowing
                // Q-Q switching. UpdateItemView (called inside the inner timer) re-publishes
                // the item view authoritatively, so the workaround is no longer needed.
                Instance.AddTimer(
                    0.08f,
                    () =>
                    {
                        try
                        {
                            if (!player.IsValid)
                                return;

                            if (!player.PawnIsAlive)
                                return;

                            // Ensure player is on a valid team
                            if (player.Team is CsTeam.None or CsTeam.Spectator)
                                return;

                            if (!GPlayersGlove.TryGetValue(player.Slot, out var gloveInfo))
                                return;
                            if (!gloveInfo.TryGetValue(player.Team, out var gloveId))
                                return;
                            if (gloveId == 0)
                                return;
                            if (!HasChangedPaint(player, gloveId, out var weaponInfo) || weaponInfo == null)
                                return;

                            // Re-fetch the pawn and EconGloves fresh — do not use any pointer
                            // captured from the outer scope, as it may have been freed by now.
                            CCSPlayerPawn? timerPawn = player.PlayerPawn.Value;
                            if (timerPawn == null || !timerPawn.IsValid)
                                return;

                            CEconItemView item = timerPawn.EconGloves;
                            if (item == null || item.Handle == IntPtr.Zero)
                                return;

                            // Switching to a different glove type? Detach the previous wearable
                            // first so the engine doesn't render both side-by-side ("ghost gloves").
                            // Skip when ItemDefinitionIndex is 0 (no current gloves) or already
                            // matches (just a paint update on the same glove type).
                            if (item.ItemDefinitionIndex != 0 && item.ItemDefinitionIndex != gloveId)
                            {
                                RemovePlayerWearables(timerPawn);
                            }

                            item.ItemDefinitionIndex = gloveId;

                            UpdatePlayerEconItemId(item);

                            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                            CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "set item texture prefab", weaponInfo.Paint);
                            CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "set item texture seed", weaponInfo.Seed);
                            CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "set item texture wear", weaponInfo.Wear);

                            item.AttributeList.Attributes.RemoveAll();
                            CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "set item texture prefab", weaponInfo.Paint);
                            CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "set item texture seed", weaponInfo.Seed);
                            CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "set item texture wear", weaponInfo.Wear);

                            item.Initialized = true;

                            // Authoritative re-publish via the engine's CEconItemView::Update.
                            // No "lastinv" toggle — it caused entity accumulation on workshop
                            // maps. The bodygroup flip is kept as a lightweight model nudge
                            // (just sets a network var, no entity churn) for the rare glove+
                            // agent combinations where UpdateItemView alone doesn't redraw.
                            UpdateGloveItemView(item, player.Slot);
                            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");

                            SetBodygroup(pawn, "first_or_third_person", 0);
                            AddTimer(0.2f, () => SetBodygroup(pawn, "first_or_third_person", 1), TimerFlags.STOP_ON_MAPCHANGE);

                            RestoreActiveWeaponSlot(player, activeSlotCommand);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning("GivePlayerGloves bodygroup refresh failed: {Message}", ex.Message);
                        }
                    },
                    TimerFlags.STOP_ON_MAPCHANGE
                );
            }
            catch (Exception ex)
            {
                Logger.LogWarning("GivePlayerGloves failed: {Message}", ex.Message);
            }
        }

        // First-write-wins snapshot of the player's server-assigned gloves for the current
        // team. Safe to call repeatedly — the per-team key is only populated once. Must be
        // called before any custom glove override mutates pawn.EconGloves.
        private static void CacheCurrentNativeGloveSnapshot(CCSPlayerController player, CCSPlayerPawn pawn)
        {
            if (player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
                return;

            var snapshotsByTeam = GPlayersNativeGlove.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, NativeGloveSnapshot>());
            if (snapshotsByTeam.ContainsKey(player.Team))
                return;

            var item = pawn.EconGloves;
            if (item == null || item.Handle == IntPtr.Zero)
                return;

            snapshotsByTeam[player.Team] = new NativeGloveSnapshot
            {
                ItemDefinitionIndex = item.ItemDefinitionIndex,
                EntityQuality = item.EntityQuality,
                EntityLevel = item.EntityLevel,
                ItemID = item.ItemID,
                ItemIDHigh = item.ItemIDHigh,
                ItemIDLow = item.ItemIDLow,
                AccountID = item.AccountID,
                InventoryPosition = item.InventoryPosition,
                Initialized = item.Initialized,
                CustomName = item.CustomName ?? string.Empty,
                CustomNameOverride = item.CustomNameOverride ?? string.Empty,
            };
        }

        private void RestorePlayerDefaultGloves(CCSPlayerController player)
        {
            if (!Utility.IsPlayerValid(player) || player.PlayerPawn.Value == null)
                return;

            var pawn = player.PlayerPawn.Value;
            CEconItemView item = pawn.EconGloves;
            if (item == null || item.Handle == IntPtr.Zero)
                return;

            item.NetworkedDynamicAttributes.Attributes.RemoveAll();
            item.AttributeList.Attributes.RemoveAll();

            if (GPlayersNativeGlove.TryGetValue(player.Slot, out var snapshotsByTeam) && snapshotsByTeam.TryGetValue(player.Team, out var snapshot))
            {
                item.ItemDefinitionIndex = snapshot.ItemDefinitionIndex;
                item.EntityQuality = snapshot.EntityQuality;
                item.EntityLevel = snapshot.EntityLevel;
                item.ItemID = snapshot.ItemID;
                item.ItemIDHigh = snapshot.ItemIDHigh;
                item.ItemIDLow = snapshot.ItemIDLow;
                item.AccountID = snapshot.AccountID;
                item.InventoryPosition = snapshot.InventoryPosition;
                item.CustomName = snapshot.CustomName;
                item.CustomNameOverride = snapshot.CustomNameOverride;
                item.Initialized = snapshot.Initialized || snapshot.ItemDefinitionIndex != 0 || snapshot.ItemID != 0;
            }
            else
            {
                // No snapshot captured (player never had custom gloves applied this session,
                // or joined as spectator) — fall back to zeroing the slot so the engine drops
                // to whatever the map/agent default would assign next render.
                item.ItemDefinitionIndex = 0;
                item.ItemID = 0;
                item.ItemIDHigh = 0;
                item.ItemIDLow = 0;
                item.Initialized = false;
            }

            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
            TouchGloveState(pawn);
        }

        // Nudges the engine to re-render gloves without a full respawn: bumps
        // m_nEconGlovesChanged so CCSPlayerPawn re-publishes the slot, then flips the
        // first/third-person bodygroup (same trick GivePlayerGloves uses for paint redraws).
        private void TouchGloveState(CCSPlayerPawn pawn)
        {
            unchecked
            {
                pawn.EconGlovesChanged++;
            }
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_nEconGlovesChanged");
            SetBodygroup(pawn, "first_or_third_person", 0);
            AddTimer(0.2f, () =>
            {
                if (pawn.IsValid)
                    SetBodygroup(pawn, "first_or_third_person", 1);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        private static int GetRandomPaint(int defindex)
        {
            if (SkinsList.Count == 0)
                return 0;

            Random rnd = new Random();

            // Filter weapons by the provided defindex
            var filteredWeapons = SkinsList.Where(w => w["weapon_defindex"]?.ToString() == defindex.ToString()).ToList();

            if (filteredWeapons.Count == 0)
                return 0;

            var randomWeapon = filteredWeapons[rnd.Next(filteredWeapons.Count)];

            return int.TryParse(randomWeapon["paint"]?.ToString(), out var paintValue) ? paintValue : 0;
        }

        //xstage idea on css discord
        public static void SubclassChange(CBasePlayerWeapon weapon, ushort itemD)
        {
            weapon.AcceptInput("ChangeSubclass", value: itemD.ToString());
        }

        public static void SetBodygroup(CCSPlayerPawn pawn, string group, int value)
        {
            // IsValid check before AcceptInput — native call, AccessViolationException
            // cannot be caught by try/catch.
            if (pawn == null || !pawn.IsValid)
                return;
            pawn.AcceptInput("SetBodygroup", value: $"{group},{value}");
        }

        private void UpdateWeaponMeshGroupMask(CBaseEntity weapon, bool isLegacy = false)
        {
            // IsValid must be checked before CBodyComponent access AND before AcceptInput.
            // SceneNode != null only verifies the managed wrapper — it does not guarantee
            // the underlying native pointer is still live. AcceptInput is a native call
            // and AccessViolationException on a freed weapon cannot be caught by try/catch.
            if (weapon == null || !weapon.IsValid)
                return;
            if (weapon.CBodyComponent?.SceneNode == null)
                return;
            if (!weapon.IsValid)
                return;

            weapon.AcceptInput("SetBodygroup", value: $"body,{(isLegacy ? 1 : 0)}");
        }

        private void UpdatePlayerWeaponMeshGroupMask(CCSPlayerController player, CBasePlayerWeapon weapon, bool isLegacy)
        {
            UpdateWeaponMeshGroupMask(weapon, isLegacy);
        }

        private static void GivePlayerAgent(CCSPlayerController player)
        {
            if (!GPlayersAgent.TryGetValue(player.Slot, out var value))
                return;

            var model = player.TeamNum == 3 ? value.CT : value.T;
            if (string.IsNullOrEmpty(model) || model == "null")
                return;

            // Only load models that came from the agents catalog — untrusted paths could cause issues.
            if (!AgentsModelSet.Contains(model))
            {
                Instance.Logger.LogWarning("Rejected agent model not in catalog: {Model}", model);
                return;
            }

            if (player.PlayerPawn.Value == null)
                return;

            try
            {
                Server.NextFrame(() =>
                {
                    player.PlayerPawn.Value.SetModel($"agents/models/{model}.vmdl");
                });
            }
            catch (Exception ex)
            {
                Instance.Logger.LogWarning("GivePlayerAgent failed: {Message}", ex.Message);
            }
        }

        private static void GivePlayerMusicKit(CCSPlayerController player)
        {
            if (player.IsBot)
                return;
            if (!GPlayersMusic.TryGetValue(player.Slot, out var musicInfo) || !musicInfo.TryGetValue(player.Team, out var musicId) || musicId == 0)
                return;

            if (player.InventoryServices == null)
                return;

            player.MusicKitID = musicId;
            // player.MvpNoMusic = false;
            player.InventoryServices.MusicID = musicId;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitID");
            // Utilities.SetStateChanged(player, "CCSPlayerController", "m_bMvpNoMusic");
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
            // player.MusicKitMVPs = musicId;
            // Utilities.SetStateChanged(player, "CCSPlayerController", "m_iMusicKitMVPs");
        }

        private static void GivePlayerPin(CCSPlayerController player)
        {
            if (!GPlayersPin.TryGetValue(player.Slot, out var pinInfo) || !pinInfo.TryGetValue(player.Team, out var pinId) || pinId == 0)
                return;
            if (player.InventoryServices == null)
                return;

            CacheCurrentNativePin(player);
            player.InventoryServices.Rank[5] = (MedalRank_t)pinId;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }

        // Snapshot the player's server-assigned pin rank exactly once per (slot, team) so we
        // can restore it when the player picks "None" in the pin menu. GetOrAdd ensures the
        // first-seen value wins and is never overwritten by our own custom assignments.
        private static void CacheCurrentNativePin(CCSPlayerController player)
        {
            if (player.InventoryServices == null)
                return;
            var nativePinsByTeam = GPlayersNativePin.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, MedalRank_t>());
            nativePinsByTeam.GetOrAdd(player.Team, _ => player.InventoryServices.Rank[5]);
        }

        private static void RestorePlayerDefaultPin(CCSPlayerController player)
        {
            if (player.InventoryServices == null)
                return;
            if (!GPlayersNativePin.TryGetValue(player.Slot, out var nativePinsByTeam) || !nativePinsByTeam.TryGetValue(player.Team, out var nativePin))
                return;

            player.InventoryServices.Rank[5] = nativePin;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }

        private void GiveOnItemPickup(CCSPlayerController player)
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null)
                return;

            var myWeapons = pawn.WeaponServices?.MyWeapons;
            if (myWeapons == null)
                return;

            foreach (var handle in myWeapons)
            {
                var weapon = handle.Value;

                if (weapon == null || !weapon.IsValid)
                    continue;
                // Null safety check for weapon AttributeManager
                if (weapon.AttributeManager?.Item == null)
                    continue;

                if (myWeapons.Count == 1)
                {
                    var newWeapon = new CBasePlayerWeapon(player.GiveNamedItem(CsItem.USP));
                    weapon.AddEntityIOEvent("Kill", weapon, null, "", 0.01f);
                    player.GiveNamedItem(CsItem.Knife);
                    player.ExecuteClientCommand("slot3");
                    newWeapon.AddEntityIOEvent("Kill", newWeapon, null, "", 0.01f);
                }

                GivePlayerWeaponSkin(player, weapon);
            }
        }

        private void UpdatePlayerEconItemId(CEconItemView econItemView)
        {
            var itemId = (ulong)System.Threading.Interlocked.Increment(ref _nextItemId);

            econItemView.ItemID = itemId;
            econItemView.ItemIDLow = (uint)(itemId & 0xFFFFFFFF);
            econItemView.ItemIDHigh = (uint)(itemId >> 32);
        }

        private static CCSPlayerController? GetPlayerFromItemServices(CCSPlayer_ItemServices itemServices)
        {
            var pawn = itemServices?.Pawn?.Value;
            if (pawn == null || !pawn.IsValid || !pawn.Controller.IsValid || pawn.Controller.Value == null)
                return null;
            var player = new CCSPlayerController(pawn.Controller.Value.Handle);
            return !Utility.IsPlayerValid(player) ? null : player;
        }

        private static bool HasChangedKnife(CCSPlayerController player, out string? knifeValue)
        {
            knifeValue = null;

            // Check if player has knife info for their slot and team
            if (!GPlayersKnife.TryGetValue(player.Slot, out var knife) || !knife.TryGetValue(player.Team, out var value) || value == "weapon_knife")
                return false;
            knifeValue = value; // Assign the knife value to the out parameter
            return true;
        }

        private static bool HasChangedPaint(CCSPlayerController player, int weaponDefIndex, out WeaponInfo? weaponInfo)
        {
            weaponInfo = null;

            // Check if player has weapons info for their slot and team
            if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamInfo) || !teamInfo.TryGetValue(player.Team, out var teamWeapons))
            {
                return false;
            }

            // Check if the specified weapon has a paint/skin change
            if (!teamWeapons.TryGetValue(weaponDefIndex, out var value) || value.Paint <= 0)
                return false;

            weaponInfo = value; // Assign the out variable when it exists
            return true;
        }

        private static float ViewAsFloat(uint value)
        {
            return BitConverter.Int32BitsToSingle((int)value);
        }
    }
}
