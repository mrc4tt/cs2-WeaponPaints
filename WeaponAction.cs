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
            if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out _))
                return;
            // Ensure player is on a valid team before accessing player.Team
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return;
            // Null safety check for weapon AttributeManager
            if (weapon.AttributeManager?.Item == null)
                return;

            bool isKnife =
                weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");

            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

            // Check FIRST if player has custom skin - if not, preserve Steam inventory skin
            if (!isKnife)
            {
                bool hasCustomSkin = HasChangedPaint(player, weaponDefIndex, out _);
                bool giveRandomSkin = _config.Additional.GiveRandomSkin;
                
                // If player has no custom skin and random skins are disabled, preserve their inventory skin
                if (!hasCustomSkin && !giveRandomSkin)
                    return;
            }

            switch (isKnife)
            {
                case true when !HasChangedKnife(player, out var _):
                    return;

                case true:
                {
                    var newDefIndex = WeaponDefindex.FirstOrDefault(x =>
                        x.Value == GPlayersKnife[player.Slot][player.Team]
                    );
                    if (newDefIndex.Key == 0)
                        return;

                    // Always call SubclassChange - fixes bug where switching back to a
                    // previously used knife would result in default knife appearing
                    SubclassChange(weapon, (ushort)newDefIndex.Key);

                    weapon.AttributeManager.Item.ItemDefinitionIndex = (ushort)newDefIndex.Key;
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

            List<JObject> skinInfo;
            bool isLegacyModel;

            if (
                _config.Additional.GiveRandomSkin && !HasChangedPaint(player, weaponDefIndex, out _)
            )
            {
                // Random skins
                weapon.FallbackPaintKit = GetRandomPaint(weaponDefIndex);
                weapon.FallbackSeed = 0;
                weapon.FallbackWear = 0.01f;

                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    "set item texture prefab",
                    GetRandomPaint(weaponDefIndex)
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    "set item texture seed",
                    0
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    "set item texture wear",
                    0.01f
                );

                weapon.AttributeManager.Item.AttributeList.Attributes.RemoveAll();
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.AttributeList.Handle,
                    "set item texture prefab",
                    GetRandomPaint(weaponDefIndex)
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.AttributeList.Handle,
                    "set item texture seed",
                    0
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.AttributeList.Handle,
                    "set item texture wear",
                    0.01f
                );

                fallbackPaintKit = weapon.FallbackPaintKit;

                if (fallbackPaintKit == 0)
                    return;

                skinInfo = SkinsList
                    .Where(w =>
                        w["weapon_defindex"]?.ToObject<int>() == weaponDefIndex
                        && w["paint"]?.ToObject<int>() == fallbackPaintKit
                    )
                    .ToList();

                isLegacyModel = skinInfo.Count <= 0 || skinInfo[0].Value<bool>("legacy_model");
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

            weapon.AttributeManager.Item.CustomName = weaponInfo.Nametag;
            weapon.FallbackPaintKit = weaponInfo.Paint;

            weapon.FallbackSeed = weaponInfo is { Paint: 38, Seed: 0 }
                ? _fadeSeed++
                : weaponInfo.Seed;

            weapon.FallbackWear = weaponInfo.Wear;
            if (isKnife) { }
            else
            {
                var logName = WeaponDefindex.TryGetValue(weaponDefIndex, out var mappedName)
                    ? mappedName
                    : weapon.DesignerName;
            }

            if (weaponInfo.StatTrak)
            {
                weapon.AttributeManager.Item.EntityQuality = 9;

                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    "kill eater",
                    ViewAsFloat((uint)weaponInfo.StatTrakCount)
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    "kill eater score type",
                    0
                );

                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.AttributeList.Handle,
                    "kill eater",
                    ViewAsFloat((uint)weaponInfo.StatTrakCount)
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.AttributeList.Handle,
                    "kill eater score type",
                    0
                );
            }

            fallbackPaintKit = weapon.FallbackPaintKit;

            if (fallbackPaintKit == 0)
                return;

            if (weaponInfo.KeyChain != null)
                SetKeychain(player, weapon);
            if (weaponInfo.Stickers.Count > 0)
                SetStickers(player, weapon);

            skinInfo = SkinsList
                .Where(w =>
                    w["weapon_defindex"]?.ToObject<int>() == weaponDefIndex
                    && w["paint"]?.ToObject<int>() == fallbackPaintKit
                )
                .ToList();

            isLegacyModel = skinInfo.Count <= 0 || skinInfo[0].Value<bool>("legacy_model");
            UpdatePlayerWeaponMeshGroupMask(player, weapon, isLegacyModel);
        }

        // silly method to update sticker when call RefreshWeapons()
        private void IncrementWearForWeaponWithStickers(
            CCSPlayerController player,
            CBasePlayerWeapon weapon
        )
        {
            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
            if (
                !HasChangedPaint(player, weaponDefIndex, out var weaponInfo)
                || weaponInfo == null
                || weaponInfo.Stickers.Count <= 0
            )
                return;

            float wearIncrement = 0.001f;
            float currentWear = weaponInfo.Wear;

            var playerWear = _temporaryPlayerWeaponWear.GetOrAdd(
                player.Slot,
                _ => new ConcurrentDictionary<int, float>()
            );

            float incrementedWear = playerWear.AddOrUpdate(
                weaponDefIndex,
                currentWear + wearIncrement,
                (_, oldWear) => Math.Min(oldWear + wearIncrement, 1.0f)
            );

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

                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    $"sticker slot {stickerSlot} id",
                    ViewAsFloat(sticker.Id)
                );
                if (sticker.OffsetX != 0 || sticker.OffsetY != 0)
                    CAttributeListSetOrAddAttributeValueByName.Invoke(
                        weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                        $"sticker slot {stickerSlot} schema",
                        0
                    );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    $"sticker slot {stickerSlot} offset x",
                    sticker.OffsetX
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    $"sticker slot {stickerSlot} offset y",
                    sticker.OffsetY
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    $"sticker slot {stickerSlot} wear",
                    sticker.Wear
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    $"sticker slot {stickerSlot} scale",
                    sticker.Scale
                );
                CAttributeListSetOrAddAttributeValueByName.Invoke(
                    weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                    $"sticker slot {stickerSlot} rotation",
                    sticker.Rotation
                );
            }

            if (
                _temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var playerWear)
                && playerWear.TryGetValue(weaponDefIndex, out float storedWear)
            )
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

            CAttributeListSetOrAddAttributeValueByName.Invoke(
                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                "keychain slot 0 id",
                ViewAsFloat(keyChain.Id)
            );
            CAttributeListSetOrAddAttributeValueByName.Invoke(
                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                "keychain slot 0 offset x",
                keyChain.OffsetX
            );
            CAttributeListSetOrAddAttributeValueByName.Invoke(
                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                "keychain slot 0 offset y",
                keyChain.OffsetY
            );
            CAttributeListSetOrAddAttributeValueByName.Invoke(
                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                "keychain slot 0 offset z",
                keyChain.OffsetZ
            );
            CAttributeListSetOrAddAttributeValueByName.Invoke(
                weapon.AttributeManager.Item.NetworkedDynamicAttributes.Handle,
                "keychain slot 0 seed",
                ViewAsFloat(keyChain.Seed)
            );
        }

        private static void GiveKnifeToPlayer(CCSPlayerController? player)
        {
            if (!_config.Additional.KnifeEnabled || player == null || !player.IsValid)
                return;

            if (PlayerHasKnife(player))
                return;

            //string knifeToGive = (CsTeam)player.TeamNum == CsTeam.Terrorist ? "weapon_knife_t" : "weapon_knife";
            player.GiveNamedItem(CsItem.Knife);
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInventoryServices");
        }

        private static bool PlayerHasKnife(CCSPlayerController? player)
        {
            if (!_config.Additional.KnifeEnabled)
                return false;

            if (player == null || !player.IsValid || !player.PlayerPawn.IsValid)
            {
                return false;
            }

            if (
                player.PlayerPawn.Value == null
                || player.PlayerPawn.Value.WeaponServices == null
                || player.PlayerPawn.Value.ItemServices == null
            )
                return false;

            var weapons = player.PlayerPawn.Value.WeaponServices?.MyWeapons;
            if (weapons == null)
                return false;
            foreach (var weapon in weapons)
            {
                if (!weapon.IsValid || weapon.Value == null || !weapon.Value.IsValid)
                    continue;
                if (
                    weapon.Value.DesignerName.Contains("knife")
                    || weapon.Value.DesignerName.Contains("bayonet")
                )
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
            if (
                player == null
                || !player.IsValid
                || player.PlayerPawn.Value == null
                || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE
            )
            {
                return;
            }
            if (
                player.PlayerPawn.Value.WeaponServices == null
                || player.PlayerPawn.Value.ItemServices == null
            )
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
                if (
                    !weapon.IsValid
                    || weapon.Value == null
                    || !weapon.Value.IsValid
                    || !weapon.Value.DesignerName.Contains("weapon_")
                )
                    continue;

                // Match by ItemDefinitionIndex
                if (weapon.Value.AttributeManager.Item.ItemDefinitionIndex != targetWeaponDefIndex)
                    continue;

                // If this is a knife, do not kill/regive here. Knife changes are applied in-place.
                if (
                    weapon.Value.DesignerName.Contains("knife")
                    || weapon.Value.DesignerName.Contains("bayonet")
                )
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
                        var newWeapon = new CBasePlayerWeapon(
                            player.GiveNamedItem(weaponDesignerName)
                        );
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
                                Logger.LogWarning(
                                    "Error setting ammo on refreshed weapon: " + ex.Message
                                );
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
            if (
                player == null
                || !player.IsValid
                || player.PlayerPawn.Value == null
                || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE
            )
                return;
            if (
                player.PlayerPawn.Value.WeaponServices == null
                || player.PlayerPawn.Value.ItemServices == null
            )
                return;

            var weapons = player.PlayerPawn.Value.WeaponServices.MyWeapons;

            if (weapons.Count == 0)
                return;
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return;

            var hasKnife = false;

            Dictionary<string, List<(int, int)>> weaponsWithAmmo = [];

            foreach (var weapon in weapons)
            {
                if (
                    !weapon.IsValid
                    || weapon.Value == null
                    || !weapon.Value.IsValid
                    || !weapon.Value.DesignerName.Contains("weapon_")
                )
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

                    if (
                        weaponData.GearSlot
                        is gear_slot_t.GEAR_SLOT_RIFLE
                            or gear_slot_t.GEAR_SLOT_PISTOL
                    )
                    {
                        if (
                            !WeaponDefindex.TryGetValue(
                                weapon.Value.AttributeManager.Item.ItemDefinitionIndex,
                                out var weaponByDefindex
                            )
                        )
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
                        // Give a generic knife - OnEntityCreated event will automatically call GivePlayerWeaponSkin
                        // which handles SubclassChange to the correct knife type
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
                                    Logger.LogWarning(
                                        "Error setting weapon properties: " + ex.Message
                                    );
                                }
                            });
                        }
                    }
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        private void GivePlayerGloves(CCSPlayerController player)
        {
            if (
                !Utility.IsPlayerValid(player)
                || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE
            )
                return;

            CCSPlayerPawn? pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                return;

            // Ensure player is on a valid team before glove logic
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return;

            try
            {
                var model =
                    pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName
                    ?? string.Empty;
                if (!string.IsNullOrEmpty(model))
                {
                    pawn.SetModel("characters/models/tm_jumpsuit/tm_jumpsuit_varianta.vmdl");
                    pawn.SetModel(model);
                }

                CEconItemView item = pawn.EconGloves;
                if (item == null || item.Handle == IntPtr.Zero)
                    return;

                item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                item.AttributeList.Attributes.RemoveAll();

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
                        {
                            return;
                        }
                        if (!gloveInfo.TryGetValue(player.Team, out var gloveId))
                        {
                            return;
                        }
                        if (gloveId == 0)
                        {
                            return;
                        }
                        if (
                            !HasChangedPaint(player, gloveId, out var weaponInfo)
                            || weaponInfo == null
                        )
                        {
                            return;
                        }

                        item.ItemDefinitionIndex = gloveId;

                        UpdatePlayerEconItemId(item);

                        item.NetworkedDynamicAttributes.Attributes.RemoveAll();
                        CAttributeListSetOrAddAttributeValueByName.Invoke(
                            item.NetworkedDynamicAttributes.Handle,
                            "set item texture prefab",
                            weaponInfo.Paint
                        );
                        CAttributeListSetOrAddAttributeValueByName.Invoke(
                            item.NetworkedDynamicAttributes.Handle,
                            "set item texture seed",
                            weaponInfo.Seed
                        );
                        CAttributeListSetOrAddAttributeValueByName.Invoke(
                            item.NetworkedDynamicAttributes.Handle,
                            "set item texture wear",
                            weaponInfo.Wear
                        );

                        item.AttributeList.Attributes.RemoveAll();
                        CAttributeListSetOrAddAttributeValueByName.Invoke(
                            item.AttributeList.Handle,
                            "set item texture prefab",
                            weaponInfo.Paint
                        );
                        CAttributeListSetOrAddAttributeValueByName.Invoke(
                            item.AttributeList.Handle,
                            "set item texture seed",
                            weaponInfo.Seed
                        );
                        CAttributeListSetOrAddAttributeValueByName.Invoke(
                            item.AttributeList.Handle,
                            "set item texture wear",
                            weaponInfo.Wear
                        );

                        item.Initialized = true;

                        SetBodygroup(pawn, "default_gloves", 1);
                    }
                    catch (Exception)
                    {
                    }
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );
            }
            catch (Exception) { }
        }

        private static int GetRandomPaint(int defindex)
        {
            if (SkinsList.Count == 0)
                return 0;

            Random rnd = new Random();

            // Filter weapons by the provided defindex
            var filteredWeapons = SkinsList
                .Where(w => w["weapon_defindex"]?.ToString() == defindex.ToString())
                .ToList();

            if (filteredWeapons.Count == 0)
                return 0;

            var randomWeapon = filteredWeapons[rnd.Next(filteredWeapons.Count)];

            return int.TryParse(randomWeapon["paint"]?.ToString(), out var paintValue)
                ? paintValue
                : 0;
        }

        //xstage idea on css discord
        public static void SubclassChange(CBasePlayerWeapon weapon, ushort itemD)
        {
            weapon.AcceptInput("ChangeSubclass", value: itemD.ToString());
        }

        public static void SetBodygroup(CCSPlayerPawn pawn, string group, int value)
        {
            pawn.AcceptInput("SetBodygroup", value: $"{group},{value}");
        }

        private void UpdateWeaponMeshGroupMask(CBaseEntity weapon, bool isLegacy = false)
        {
            if (weapon.CBodyComponent?.SceneNode == null)
                return;
            //var skeleton = weapon.CBodyComponent.SceneNode.GetSkeletonInstance();
            // skeleton.ModelState.MeshGroupMask = isLegacy ? 2UL : 1UL;

            weapon.AcceptInput("SetBodygroup", value: $"body,{(isLegacy ? 1 : 0)}");
        }

        private void UpdatePlayerWeaponMeshGroupMask(
            CCSPlayerController player,
            CBasePlayerWeapon weapon,
            bool isLegacy
        )
        {
            UpdateWeaponMeshGroupMask(weapon, isLegacy);
        }

        private static void GivePlayerAgent(CCSPlayerController player)
        {
            if (!GPlayersAgent.TryGetValue(player.Slot, out var value))
                return;

            var model = player.TeamNum == 3 ? value.CT : value.T;
            if (string.IsNullOrEmpty(model))
                return;

            if (player.PlayerPawn.Value == null)
                return;

            try
            {
                Server.NextFrame(() =>
                {
                    player.PlayerPawn.Value.SetModel($"characters/models/{model}.vmdl");
                });
            }
            catch (Exception) { }
        }

        private static void GivePlayerMusicKit(CCSPlayerController player)
        {
            if (player.IsBot)
                return;
            if (
                !GPlayersMusic.TryGetValue(player.Slot, out var musicInfo)
                || !musicInfo.TryGetValue(player.Team, out var musicId)
                || musicId == 0
            )
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
            if (
                !GPlayersPin.TryGetValue(player.Slot, out var pinInfo)
                || !pinInfo.TryGetValue(player.Team, out var pinId)
            )
                return;
            if (player.InventoryServices == null)
                return;

            player.InventoryServices.Rank[5] =
                pinId > 0 ? (MedalRank_t)pinId : MedalRank_t.MEDAL_RANK_NONE;
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
            var itemId = _nextItemId++;

            econItemView.ItemID = itemId;
            econItemView.ItemIDLow = (uint)itemId & 0xFFFFFFFF;
            econItemView.ItemIDHigh = (uint)itemId >> 32;
        }

        private static CCSPlayerController? GetPlayerFromItemServices(
            CCSPlayer_ItemServices itemServices
        )
        {
            var pawn = itemServices.Pawn.Value;
            if (!pawn.IsValid || !pawn.Controller.IsValid || pawn.Controller.Value == null)
                return null;
            var player = new CCSPlayerController(pawn.Controller.Value.Handle);
            return !Utility.IsPlayerValid(player) ? null : player;
        }

        private static bool HasChangedKnife(CCSPlayerController player, out string? knifeValue)
        {
            knifeValue = null;

            // Check if player has knife info for their slot and team
            if (
                !GPlayersKnife.TryGetValue(player.Slot, out var knife)
                || !knife.TryGetValue(player.Team, out var value)
                || value == "weapon_knife"
            )
                return false;
            knifeValue = value; // Assign the knife value to the out parameter
            return true;
        }

        private static bool HasChangedPaint(
            CCSPlayerController player,
            int weaponDefIndex,
            out WeaponInfo? weaponInfo
        )
        {
            weaponInfo = null;

            // Check if player has weapons info for their slot and team
            if (
                !GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamInfo)
                || !teamInfo.TryGetValue(player.Team, out var teamWeapons)
            )
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
