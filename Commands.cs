using System;
using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

public partial class WeaponPaints
{
    private void OnCommandRefresh(CCSPlayerController? player, CommandInfo command)
    {
        if (
            !Config.Additional.CommandWpEnabled
            || !Config.Additional.SkinEnabled
            || !_gBCommandsAllowed
        )
            return;
        if (!Utility.IsPlayerValid(player))
            return;

        if (player == null || !player.IsValid || player.UserId == null || player.IsBot)
            return;

        // Prevent double-call from chat/console trigger
        if (
            LastCommandTime.TryGetValue(player.Slot, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100
        )
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        // // Check cooldown
        // if (CommandsCooldown.TryGetValue(player.Slot, out var existingCooldown) && DateTime.UtcNow < existingCooldown)
        // {
        // 	if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
        // 	{
        // 		player.Print(Localizer["wp_command_cooldown"]);
        // 	}
        // 	return;
        // }

        PlayerInfo? playerInfo = new PlayerInfo
        {
            UserId = player.UserId,
            Slot = player.Slot,
            Index = (int)player.Index,
            SteamId = player.SteamID.ToString(),
            Name = player.PlayerName,
            IpAddress = player.IpAddress?.Split(":")[0],
        };

        // Set cooldown immediately
        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
            Config.CmdRefreshCooldownSeconds
        );

        if (WeaponSync != null)
        {
            // Ensure database data is loaded before refreshing weapons to avoid race conditions
            _ = Task.Run(async () =>
            {
                try
                {
                    await WeaponSync.GetPlayerData(playerInfo);
                    // After DB sync, refresh player cosmetics and weapons
                    Server.NextFrame(() =>
                    {
                        GivePlayerGloves(player);
                        // Refresh knife to apply new knife model and skin from database
                        RefreshKnife(player);
                        // Refresh other weapons (excluding knife since we just refreshed it)
                        RefreshWeapons(player, excludeKnife: true);
                        GivePlayerAgent(player);
                        GivePlayerMusicKit(player);
                        AddTimer(0.15f, () => GivePlayerPin(player));
                    });
                }
                catch (Exception)
                {
                    // Fallback: even if DB fetch fails, still attempt a refresh (without skipping knife)
                    Server.NextFrame(() =>
                    {
                        GivePlayerGloves(player);
                        RefreshWeapons(player);
                        GivePlayerAgent(player);
                        GivePlayerMusicKit(player);
                        AddTimer(0.15f, () => GivePlayerPin(player));
                    });
                }
            });
        }

        if (!string.IsNullOrEmpty(Localizer["wp_command_refresh_done"]))
        {
            player.Print(Localizer["wp_command_refresh_done"]);
        }
    }

    private void OnCommandWS(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled)
            return;
        if (!Utility.IsPlayerValid(player))
            return;

        if (!string.IsNullOrEmpty(Localizer["wp_info_website"]))
        {
            player!.Print(Localizer["wp_info_website", Config.Website]);
        }
        if (!string.IsNullOrEmpty(Localizer["wp_info_refresh"]))
        {
            player!.Print(Localizer["wp_info_refresh"]);
        }

        if (Config.Additional.GloveEnabled)
            if (!string.IsNullOrEmpty(Localizer["wp_info_glove"]))
            {
                player!.Print(Localizer["wp_info_glove"]);
            }

        if (Config.Additional.AgentEnabled)
            if (!string.IsNullOrEmpty(Localizer["wp_info_agent"]))
            {
                player!.Print(Localizer["wp_info_agent"]);
            }

        if (Config.Additional.MusicEnabled)
            if (!string.IsNullOrEmpty(Localizer["wp_info_music"]))
            {
                player!.Print(Localizer["wp_info_music"]);
            }

        if (Config.Additional.PinsEnabled)
            if (!string.IsNullOrEmpty(Localizer["wp_info_pin"]))
            {
                player!.Print(Localizer["wp_info_pin"]);
            }

        if (!Config.Additional.KnifeEnabled)
            return;
        if (!string.IsNullOrEmpty(Localizer["wp_info_knife"]))
        {
            player!.Print(Localizer["wp_info_knife"]);
        }
    }

    private void OnCommandFloat(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (
            LastCommandTime.TryGetValue(player.Slot, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100
        )
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        if (command.ArgCount < 2)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_usage"]))
            {
                player.Print(Localizer["wp_float_usage"]);
            }
            return;
        }

        if (
            !float.TryParse(
                command.GetArg(1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var floatValue
            )
        )
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_invalid"]))
            {
                player.Print(Localizer["wp_float_invalid"]);
            }
            return;
        }

        // Clamp float value between 0.00 and 1.00
        floatValue = Math.Clamp(floatValue, 0.00f, 1.00f);

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

        if (weapon == null || !weapon.IsValid)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_no_weapon"]))
            {
                player.Print(Localizer["wp_float_no_weapon"]);
            }
            return;
        }

        var weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

        // Check if it's a knife or default weapon
        if (!HasChangedPaint(player, weaponDefIndex, out var existingWeaponInfo))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_no_skin"]))
            {
                player.Print(Localizer["wp_float_no_skin"]);
            }
            return;
        }

        var playerSkins = GPlayerWeaponsInfo.GetOrAdd(
            player.Slot,
            new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>()
        );
        var teamsToCheck =
            player.TeamNum < 2
                ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                : [player.Team];

        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerSkins.GetOrAdd(
                team,
                _ => new ConcurrentDictionary<int, WeaponInfo>()
            );
            if (teamWeapons.TryGetValue(weaponDefIndex, out var weaponInfo))
            {
                weaponInfo.Wear = floatValue;
            }
        }

        // Clear any temporary wear override for this weapon, so the next apply uses the new value
        if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var tempWear))
        {
            tempWear.TryRemove(weaponDefIndex, out _);
        }

        var playerInfo = new PlayerInfo
        {
            UserId = player.UserId,
            Slot = player.Slot,
            Index = (int)player.Index,
            SteamId = player.SteamID.ToString(),
            Name = player.PlayerName,
            IpAddress = player.IpAddress?.Split(":")[0],
        };

        // Refresh only this specific weapon; for knives, apply skin directly without kill/regive
        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
        {
            bool isKnife =
                weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");
            if (isKnife)
            {
                RefreshKnife(player);
            }
            else
            {
                RefreshSingleWeapon(player, weaponDefIndex);
            }
        }

        if (WeaponSync != null)
        {
            _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));
        }

        if (!string.IsNullOrEmpty(Localizer["wp_float_set"]))
        {
            player.Print(
                Localizer[
                    "wp_float_set",
                    floatValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                ]
            );
        }
    }

    private void OnCommandSeed(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (
            LastCommandTime.TryGetValue(player.Slot, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100
        )
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        if (command.ArgCount < 2)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_usage"]))
            {
                player.Print(Localizer["wp_seed_usage"]);
            }
            return;
        }

        if (!int.TryParse(command.GetArg(1), out var seedValue))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_invalid"]))
            {
                player.Print(Localizer["wp_seed_invalid"]);
            }
            return;
        }

        // Clamp seed value between 0 and 1000
        seedValue = Math.Clamp(seedValue, 0, 1000);

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

        if (weapon == null || !weapon.IsValid)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_no_weapon"]))
            {
                player.Print(Localizer["wp_seed_no_weapon"]);
            }
            return;
        }

        var weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

        // Check if the player has a skin on this weapon
        if (!HasChangedPaint(player, weaponDefIndex, out var existingWeaponInfo))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_no_skin"]))
            {
                player.Print(Localizer["wp_seed_no_skin"]);
            }
            return;
        }

        var playerSkins = GPlayerWeaponsInfo.GetOrAdd(
            player.Slot,
            new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>()
        );
        var teamsToCheck =
            player.TeamNum < 2
                ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                : [player.Team];

        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerSkins.GetOrAdd(
                team,
                _ => new ConcurrentDictionary<int, WeaponInfo>()
            );
            if (teamWeapons.TryGetValue(weaponDefIndex, out var weaponInfo))
            {
                weaponInfo.Seed = seedValue;
            }
        }

        var playerInfo = new PlayerInfo
        {
            UserId = player.UserId,
            Slot = player.Slot,
            Index = (int)player.Index,
            SteamId = player.SteamID.ToString(),
            Name = player.PlayerName,
            IpAddress = player.IpAddress?.Split(":")[0],
        };

        // Refresh only this specific weapon; for knives, apply skin directly without kill/regive
        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
        {
            bool isKnife =
                weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");
            if (isKnife)
            {
                RefreshKnife(player);
            }
            else
            {
                RefreshSingleWeapon(player, weaponDefIndex);
            }
        }

        if (WeaponSync != null)
        {
            _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));
        }

        if (!string.IsNullOrEmpty(Localizer["wp_seed_set"]))
        {
            player.Print(Localizer["wp_seed_set", seedValue]);
        }
    }

    private void OnCommandDirectSkinSelection(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Check cooldown
        // if (CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) && DateTime.UtcNow < cooldownEndTime)
        // {
        // 	if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
        // 	{
        // 		player.Print(Localizer["wp_command_cooldown"]);
        // 	}
        // 	return;
        // }

        // If no argument, open the normal weapon selection menu
        if (command.ArgCount < 2)
        {
            CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
                Config.CmdRefreshCooldownSeconds
            );
            OpenWeaponSelectionMenu(player);
            return;
        }

        var weaponArg = command.GetArg(1).ToLower();

        // Try to find the weapon by name or class name
        string? selectedWeaponClassname = null;
        string? selectedWeaponName = null;

        foreach (var kvp in WeaponList)
        {
            var className = kvp.Key.ToLower();
            var displayName = kvp.Value.ToLower();

            // Match by partial class name (e.g., "ak47" matches "weapon_ak47")
            // or by partial display name (e.g., "ak" matches "AK-47")
            if (
                className.Contains(weaponArg)
                || className.Replace("weapon_", "").Contains(weaponArg)
                || displayName.Contains(weaponArg)
                || displayName.Replace("-", "").Replace(" ", "").Contains(weaponArg)
            )
            {
                selectedWeaponClassname = kvp.Key;
                selectedWeaponName = kvp.Value;
                break;
            }
        }

        if (selectedWeaponClassname == null)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_skin_weapon_not_found"]))
            {
                player.Print(Localizer["wp_skin_weapon_not_found", weaponArg]);
            }
            return;
        }

        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
            Config.CmdRefreshCooldownSeconds
        );
        OpenSkinSelectionMenuForWeapon(player, selectedWeaponClassname, selectedWeaponName!);
    }

    private void OpenWeaponSelectionMenu(CCSPlayerController player)
    {
        var classNamesByWeapon = WeaponList.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);

        var weaponSelectionMenu = Utility.CreateMenu(Localizer["wp_skin_menu_weapon_title"]);
        if (weaponSelectionMenu == null)
            return;

        var handleWeaponSelection = (CCSPlayerController? p, ChatMenuOption option) =>
        {
            if (!Utility.IsPlayerValid(p) || p == null)
                return;

            var selectedWeapon = option.Text;
            if (!classNamesByWeapon.TryGetValue(selectedWeapon, out var selectedWeaponClassname))
                return;

            OpenSkinSelectionMenuForWeapon(p, selectedWeaponClassname, selectedWeapon);
        };

        foreach (var weaponName in WeaponList.Select(kvp => kvp.Value))
        {
            weaponSelectionMenu.AddMenuOption(weaponName, handleWeaponSelection);
        }

        weaponSelectionMenu.Open(player);
    }

    private void OpenSkinSelectionMenuForWeapon(
        CCSPlayerController player,
        string weaponClassname,
        string weaponDisplayName
    )
    {
        var skinsForSelectedWeapon = SkinsList
            .Where(skin =>
                skin.TryGetValue("weapon_name", out var weaponName)
                && weaponName?.ToString() == weaponClassname
            )
            ?.ToList();

        if (skinsForSelectedWeapon == null || skinsForSelectedWeapon.Count == 0)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_skin_no_skins_found"]))
            {
                player.Print(Localizer["wp_skin_no_skins_found", weaponDisplayName]);
            }
            return;
        }

        var skinSubMenu = Utility.CreateMenu(
            Localizer["wp_skin_menu_skin_title", weaponDisplayName]
        );
        if (skinSubMenu == null)
            return;

        var handleSkinSelection = (CCSPlayerController p, ChatMenuOption opt) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;

            var firstSkin = SkinsList.FirstOrDefault(skin =>
            {
                if (skin.TryGetValue("weapon_name", out var weaponName))
                {
                    return weaponName?.ToString() == weaponClassname;
                }
                return false;
            });

            var selectedSkin = opt.Text;
            var selectedPaintId = selectedSkin[(selectedSkin.LastIndexOf('(') + 1)..].Trim(')');

            if (
                firstSkin == null
                || !firstSkin.TryGetValue("weapon_defindex", out var weaponDefIndexObj)
                || !int.TryParse(weaponDefIndexObj.ToString(), out var weaponDefIndex)
                || !int.TryParse(selectedPaintId, out var paintId)
            )
                return;
            {
                if (Config.Additional.ShowSkinImage)
                {
                    var foundSkin = SkinsList.FirstOrDefault(skin =>
                        ((int?)skin["weapon_defindex"] ?? 0) == weaponDefIndex
                        && ((int?)skin["paint"] ?? 0) == paintId
                        && skin["image"] != null
                    );
                    var image = foundSkin?["image"]?.ToString() ?? "";
                    _playerWeaponImage[p.Slot] = image;
                    AddTimer(
                        2.0f,
                        () => _playerWeaponImage.Remove(p.Slot),
                        TimerFlags.STOP_ON_MAPCHANGE
                    );
                }

                p.Print(Localizer["wp_skin_menu_select", selectedSkin]);
                var playerSkins = GPlayerWeaponsInfo.GetOrAdd(
                    p.Slot,
                    new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>()
                );

                var teamsToCheck =
                    p.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [p.Team];

                foreach (var team in teamsToCheck)
                {
                    var teamWeapons = playerSkins.GetOrAdd(
                        team,
                        _ => new ConcurrentDictionary<int, WeaponInfo>()
                    );
                    var value = teamWeapons.GetOrAdd(weaponDefIndex, _ => new WeaponInfo());
                    value.Paint = paintId;
                    value.Wear = 0.01f;
                    value.Seed = 0;
                }

                var playerInfo = new PlayerInfo
                {
                    UserId = p.UserId,
                    Slot = p.Slot,
                    Index = (int)p.Index,
                    SteamId = p.SteamID.ToString(),
                    Name = p.PlayerName,
                    IpAddress = p.IpAddress?.Split(":")[0],
                };

                if (
                    !_gBCommandsAllowed
                    || (LifeState_t)p.LifeState != LifeState_t.LIFE_ALIVE
                    || WeaponSync == null
                )
                    return;

                // Refresh only the specific weapon we changed the skin for (preserves knife)
                bool isKnife =
                    weaponClassname.Contains("knife") || weaponClassname.Contains("bayonet");
                if (isKnife)
                {
                    RefreshKnife(p);
                }
                else
                {
                    RefreshSingleWeapon(p, weaponDefIndex);
                }

                try
                {
                    _ = Task.Run(async () =>
                        await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo)
                    );
                }
                catch (Exception ex)
                {
                    Utility.Log($"Error syncing weapon paints: {ex.Message}");
                }
            }
        };

        foreach (var skin in skinsForSelectedWeapon)
        {
            if (
                !skin.TryGetValue("paint_name", out var paintNameObj)
                || !skin.TryGetValue("paint", out var paintObj)
            )
                continue;
            var paintName = paintNameObj?.ToString();
            var paint = paintObj?.ToString();

            if (!string.IsNullOrEmpty(paintName) && !string.IsNullOrEmpty(paint))
            {
                skinSubMenu.AddMenuOption($"{paintName} ({paint})", handleSkinSelection);
            }
        }

        skinSubMenu.Open(player);
    }

    private void RegisterCommands()
    {
        _config.Additional.CommandStattrak.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Stattrak toggle",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;

                    OnCommandStattrak(player, info);
                }
            );
        });

        _config.Additional.CommandSkin.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Skins info",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandWS(player, info);
                }
            );
        });

        _config.Additional.CommandRefresh.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Skins refresh",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandRefresh(player, info);
                }
            );
        });

        // Register float command
        _config.Additional.CommandFloat.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Set weapon float/wear value",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandFloat(player, info);
                }
            );
        });

        // Register seed command
        _config.Additional.CommandSeed.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Set weapon pattern seed",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandSeed(player, info);
                }
            );
        });

        if (Config.Additional.CommandKillEnabled)
        {
            _config.Additional.CommandKill.ForEach(c =>
            {
                AddCommand(
                    $"css_{c}",
                    "kill yourself",
                    (player, _) =>
                    {
                        if (
                            player == null
                            || !Utility.IsPlayerValid(player)
                            || player.PlayerPawn.Value == null
                            || !player.PlayerPawn.IsValid
                        )
                            return;

                        player.PlayerPawn.Value.CommitSuicide(true, false);
                    }
                );
            });
        }
    }

    private void OnCommandStattrak(CCSPlayerController? player, CommandInfo commandInfo)
    {
        if (player == null || !player.IsValid)
            return;

        if (!_gBCommandsAllowed)
            return;

        // Prevent double-call from chat/console trigger
        if (
            LastCommandTime.TryGetValue(player.Slot, out var lastTime)
            && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100
        )
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

        if (weapon == null || !weapon.IsValid)
            return;

        if (
            !HasChangedPaint(
                player,
                weapon.AttributeManager.Item.ItemDefinitionIndex,
                out var weaponInfo
            )
            || weaponInfo == null
        )
            return;

        weaponInfo.StatTrak = !weaponInfo.StatTrak;
        RefreshWeapons(player);

        if (!string.IsNullOrEmpty(Localizer["wp_stattrak_action"]))
        {
            player.Print(Localizer["wp_stattrak_action"]);
        }
    }

    private void SetupKnifeMenu()
    {
        if (!Config.Additional.KnifeEnabled || !_gBCommandsAllowed)
            return;

        var knivesOnly = WeaponList
            .Where(pair =>
                pair.Key.StartsWith("weapon_knife") || pair.Key.StartsWith("weapon_bayonet")
            )
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var giveItemMenu = Utility.CreateMenu(Localizer["wp_knife_menu_title"]);

        var handleGive = (CCSPlayerController player, ChatMenuOption option) =>
        {
            if (!Utility.IsPlayerValid(player))
                return;

            var playerKnives = GPlayersKnife.GetOrAdd(
                player.Slot,
                new ConcurrentDictionary<CsTeam, string>()
            );
            var teamsToCheck =
                player.TeamNum < 2
                    ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                    : [player.Team];

            var knifeName = option.Text;
            var knifeKey = knivesOnly.FirstOrDefault(x => x.Value == knifeName).Key;
            if (string.IsNullOrEmpty(knifeKey))
                return;
            if (!string.IsNullOrEmpty(Localizer["wp_knife_menu_select"]))
            {
                player.Print(Localizer["wp_knife_menu_select", knifeName]);
            }

            if (
                !string.IsNullOrEmpty(Localizer["wp_knife_menu_kill"])
                && Config.Additional.CommandKillEnabled
            )
            {
                player.Print(Localizer["wp_knife_menu_kill"]);
            }

            PlayerInfo playerInfo = new PlayerInfo
            {
                UserId = player.UserId,
                Slot = player.Slot,
                Index = (int)player.Index,
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                IpAddress = player.IpAddress?.Split(":")[0],
            };

            foreach (var team in teamsToCheck)
            {
                // Attempt to get the existing knives
                playerKnives[team] = knifeKey;
            }

            if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
                RefreshWeapons(player);

            if (WeaponSync != null)
                _ = Task.Run(async () =>
                    await WeaponSync.SyncKnifeToDatabase(playerInfo, knifeKey, teamsToCheck)
                );
        };
        foreach (var knifePair in knivesOnly)
        {
            giveItemMenu?.AddMenuOption(knifePair.Value, handleGive);
        }
        _config.Additional.CommandKnife.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Knife Menu",
                (player, _) =>
                {
                    if (giveItemMenu == null)
                        return;
                    if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed)
                        return;

                    if (player == null || player.UserId == null)
                        return;

                    if (
                        !CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime)
                        || DateTime.UtcNow
                            >= (
                                CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime)
                                    ? cooldownEndTime
                                    : DateTime.UtcNow
                            )
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
                            Config.CmdRefreshCooldownSeconds
                        );
                        giveItemMenu.PostSelectAction = PostSelectAction.Close;

                        giveItemMenu.Open(player);

                        return;
                    }
                    // if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                    // {
                    // 	player.Print(Localizer["wp_command_cooldown"]);
                    // }
                }
            );
        });
    }

    private void SetupSkinsMenu()
    {
        // Register the direct skin selection command with argument support
        _config.Additional.CommandSkinSelection.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Skins selection menu (optionally specify weapon: !skins ak47)",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;

                    if (player == null || player.UserId == null)
                        return;

                    OnCommandDirectSkinSelection(player, info);
                }
            );
        });
    }

    private void SetupGlovesMenu()
    {
        var glovesSelectionMenu = Utility.CreateMenu(Localizer["wp_glove_menu_title"]);
        if (glovesSelectionMenu == null)
            return;
        glovesSelectionMenu.PostSelectAction = PostSelectAction.Close;

        var handleGloveSelection = (CCSPlayerController? player, ChatMenuOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedPaintName = option.Text;

            var playerGloves = GPlayersGlove.GetOrAdd(
                player.Slot,
                new ConcurrentDictionary<CsTeam, ushort>()
            );
            var teamsToCheck =
                player.TeamNum < 2
                    ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                    : [player.Team];

            var selectedGlove = GlovesList.FirstOrDefault(g =>
                g.ContainsKey("paint_name") && g["paint_name"]?.ToString() == selectedPaintName
            );
            var image = selectedGlove?["image"]?.ToString() ?? "";
            if (
                selectedGlove == null
                || !selectedGlove.ContainsKey("weapon_defindex")
                || !selectedGlove.ContainsKey("paint")
                || !int.TryParse(
                    selectedGlove["weapon_defindex"]?.ToString(),
                    out var weaponDefindex
                )
                || !int.TryParse(selectedGlove["paint"]?.ToString(), out var paint)
            )
                return;
            if (Config.Additional.ShowSkinImage)
            {
                _playerWeaponImage[player.Slot] = image;
                AddTimer(
                    2.0f,
                    () => _playerWeaponImage.Remove(player.Slot),
                    CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE
                );
            }

            PlayerInfo playerInfo = new PlayerInfo
            {
                UserId = player.UserId,
                Slot = player.Slot,
                Index = (int)player.Index,
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                IpAddress = player.IpAddress?.Split(":")[0],
            };

            if (paint != 0)
            {
                // Ensure that player weapons info exists for the player
                if (!GPlayerWeaponsInfo.ContainsKey(player.Slot))
                {
                    GPlayerWeaponsInfo[player.Slot] =
                        new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>();
                }

                // Ensure teams are initialized and update glove info
                foreach (var team in teamsToCheck)
                {
                    if (!GPlayerWeaponsInfo[player.Slot].ContainsKey(team))
                    {
                        GPlayerWeaponsInfo[player.Slot][team] =
                            new ConcurrentDictionary<int, WeaponInfo>();
                    }

                    // Update the glove for the player in the specified team
                    playerGloves[team] = (ushort)weaponDefindex;

                    // Update weapon info with glove paint
                    if (
                        !GPlayerWeaponsInfo[player.Slot]
                            [team]
                            .TryGetValue(weaponDefindex, out var weaponInfo)
                    )
                    {
                        weaponInfo = new WeaponInfo();
                        GPlayerWeaponsInfo[player.Slot][team][weaponDefindex] = weaponInfo;
                    }
                    weaponInfo.Paint = paint;
                    weaponInfo.Wear = 0.00f;
                    weaponInfo.Seed = 0;
                }
            }
            else
            {
                GPlayersGlove.TryRemove(player.Slot, out _);
            }

            if (WeaponSync == null)
                return;

            // Async DB sync (doesn't block glove refresh)
            _ = Task.Run(async () =>
            {
                foreach (var team in teamsToCheck)
                {
                    await WeaponSync.SyncGloveToDatabase(
                        playerInfo,
                        (ushort)weaponDefindex,
                        teamsToCheck
                    );
                    await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo);
                }
            });

            AddTimer(
                0.1f,
                () =>
                {
                    GivePlayerGloves(player);
                }
            );
            AddTimer(
                0.25f,
                () =>
                {
                    GivePlayerGloves(player);
                }
            );
        };

        // Add weapon options to the weapon selection menu
        foreach (
            var paintName in GlovesList
                .Select(gloveObject => gloveObject["paint_name"]?.ToString() ?? "")
                .Where(paintName => paintName.Length > 0)
        )
        {
            glovesSelectionMenu.AddMenuOption(paintName, handleGloveSelection);
        }

        // Command to open the weapon selection menu for players
        _config.Additional.CommandGlove.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Gloves selection menu",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed)
                        return;

                    if (player == null || player.UserId == null)
                        return;

                    if (
                        !CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime)
                        || DateTime.UtcNow
                            >= (
                                CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime)
                                    ? cooldownEndTime
                                    : DateTime.UtcNow
                            )
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
                            Config.CmdRefreshCooldownSeconds
                        );
                        glovesSelectionMenu?.Open(player);
                        return;
                    }
                    // if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                    // {
                    // 	player.Print(Localizer["wp_command_cooldown"]);
                    // }
                }
            );
        });
    }

    private void SetupAgentsMenu()
    {
        var ctAgentMenu = Utility.CreateMenu(Localizer["wp_agent_menu_title"]);
        var tAgentMenu = Utility.CreateMenu(Localizer["wp_agent_menu_title"]);
        if (ctAgentMenu == null || tAgentMenu == null)
            return;
        ctAgentMenu.PostSelectAction = PostSelectAction.Close;
        tAgentMenu.PostSelectAction = PostSelectAction.Close;

        var handleAgentSelection = (CCSPlayerController? player, ChatMenuOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedAgentName = option.Text;
            var playerTeam = player.TeamNum.ToString();

            var selectedAgent = AgentsList.FirstOrDefault(a =>
                a.ContainsKey("agent_name")
                && a["agent_name"]?.ToString() == selectedAgentName
                && a["team"]?.ToString() == playerTeam
            );
            var image = selectedAgent?["image"]?.ToString() ?? "";
            if (
                selectedAgent == null
                || !selectedAgent.ContainsKey("model")
                || !selectedAgent.ContainsKey("team")
            )
                return;

            if (Config.Additional.ShowSkinImage)
            {
                _playerWeaponImage[player.Slot] = image;
                AddTimer(
                    2.0f,
                    () => _playerWeaponImage.Remove(player.Slot),
                    TimerFlags.STOP_ON_MAPCHANGE
                );
            }

            var model = selectedAgent["model"]?.ToString();
            var team = selectedAgent["team"]?.ToString();

            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(team))
                return;

            PlayerInfo playerInfo = new PlayerInfo
            {
                UserId = player.UserId,
                Slot = player.Slot,
                Index = (int)player.Index,
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                IpAddress = player.IpAddress?.Split(":")[0],
            };

            var currentAgents = GPlayersAgent.GetOrAdd(player.Slot, (null, null));
            var isDefault = model == "null";
            string? storedModel = isDefault ? null : model;

            if (team == "3")
            {
                GPlayersAgent[player.Slot] = (storedModel, currentAgents.T);
            }
            else if (team == "2")
            {
                GPlayersAgent[player.Slot] = (currentAgents.CT, storedModel);
            }

            if (!string.IsNullOrEmpty(Localizer["wp_agent_menu_select"]))
            {
                player.Print(Localizer["wp_agent_menu_select", selectedAgentName]);
            }

            if (!isDefault)
            {
                GivePlayerAgent(player);
                AddTimer(0.1f, () => GivePlayerAgent(player));
                AddTimer(0.25f, () => GivePlayerAgent(player));
            }

            if (WeaponSync != null)
            {
                _ = Task.Run(async () => await WeaponSync.SyncAgentToDatabase(playerInfo));
            }
        };

        foreach (var agent in AgentsList)
        {
            var agentName = agent["agent_name"]?.ToString() ?? "";
            var agentTeam = agent["team"]?.ToString();
            if (agentName.Length == 0)
                continue;

            if (agentTeam == "3")
                ctAgentMenu.AddMenuOption(agentName, handleAgentSelection);
            else if (agentTeam == "2")
                tAgentMenu.AddMenuOption(agentName, handleAgentSelection);
        }

        // Command to open the agent selection menu for players
        _config.Additional.CommandAgent.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Agents selection menu",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed)
                        return;

                    if (player == null || player.UserId == null)
                        return;

                    if (
                        !CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime)
                        || DateTime.UtcNow
                            >= (
                                CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime)
                                    ? cooldownEndTime
                                    : DateTime.UtcNow
                            )
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
                            Config.CmdRefreshCooldownSeconds
                        );

                        if (player.TeamNum == 3)
                            ctAgentMenu?.Open(player);
                        else if (player.TeamNum == 2)
                            tAgentMenu?.Open(player);
                        return;
                    }
                }
            );
        });
    }

    private void SetupMusicMenu()
    {
        var musicSelectionMenu = Utility.CreateMenu(Localizer["wp_music_menu_title"]);
        if (musicSelectionMenu == null)
            return;
        musicSelectionMenu.PostSelectAction = PostSelectAction.Close;

        var handleMusicSelection = (CCSPlayerController? player, ChatMenuOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedPaintName = option.Text;

            var playerMusic = GPlayersMusic.GetOrAdd(
                player.Slot,
                new ConcurrentDictionary<CsTeam, ushort>()
            );
            var teamsToCheck =
                player.TeamNum < 2
                    ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                    : [player.Team]; // Corrected array initializer

            var selectedMusic = MusicList.FirstOrDefault(g =>
                g.ContainsKey("name") && g["name"]?.ToString() == selectedPaintName
            );
            if (selectedMusic != null)
            {
                if (
                    !selectedMusic.ContainsKey("id")
                    || !selectedMusic.ContainsKey("name")
                    || !int.TryParse(selectedMusic["id"]?.ToString(), out var paint)
                )
                    return;
                var image = selectedMusic["image"]?.ToString() ?? "";
                if (Config.Additional.ShowSkinImage)
                {
                    _playerWeaponImage[player.Slot] = image;
                    AddTimer(
                        2.0f,
                        () => _playerWeaponImage.Remove(player.Slot),
                        TimerFlags.STOP_ON_MAPCHANGE
                    );
                }

                PlayerInfo playerInfo = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0],
                };

                if (paint != 0)
                {
                    foreach (var team in teamsToCheck)
                    {
                        playerMusic[team] = (ushort)paint;
                    }
                }
                else
                {
                    foreach (var team in teamsToCheck)
                    {
                        playerMusic[team] = 0;
                    }
                }

                GivePlayerMusicKit(player);

                if (!string.IsNullOrEmpty(Localizer["wp_music_menu_select"]))
                {
                    player.Print(Localizer["wp_music_menu_select", selectedPaintName]);
                }

                if (WeaponSync != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await WeaponSync.SyncMusicToDatabase(
                            playerInfo,
                            (ushort)paint,
                            teamsToCheck
                        );
                    });
                }
            }
            else
            {
                PlayerInfo playerInfo = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0],
                };

                foreach (var team in teamsToCheck)
                {
                    playerMusic[team] = 0;
                }

                GivePlayerMusicKit(player);

                if (!string.IsNullOrEmpty(Localizer["wp_music_menu_select"]))
                {
                    player.Print(Localizer["wp_music_menu_select", Localizer["None"]]);
                }

                if (WeaponSync != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await WeaponSync.SyncMusicToDatabase(playerInfo, 0, teamsToCheck);
                    });
                }
            }
        };

        musicSelectionMenu.AddMenuOption(Localizer["None"], handleMusicSelection);
        // Add weapon options to the weapon selection menu
        foreach (
            var paintName in MusicList
                .Select(musicObject => musicObject["name"]?.ToString() ?? "")
                .Where(paintName => paintName.Length > 0)
        )
        {
            musicSelectionMenu.AddMenuOption(paintName, handleMusicSelection);
        }

        // Command to open the weapon selection menu for players
        _config.Additional.CommandMusic.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Music selection menu",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed)
                        return;

                    if (player == null || player.UserId == null)
                        return;

                    if (
                        !CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime)
                        || DateTime.UtcNow
                            >= (
                                CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime)
                                    ? cooldownEndTime
                                    : DateTime.UtcNow
                            )
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
                            Config.CmdRefreshCooldownSeconds
                        );
                        musicSelectionMenu.Open(player);
                        return;
                    }
                    // if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                    // {
                    // 	player.Print(Localizer["wp_command_cooldown"]);
                    // }
                }
            );
        });
    }

    private void SetupPinsMenu()
    {
        var pinsSelectionMenu = Utility.CreateMenu(Localizer["wp_pins_menu_title"]);
        if (pinsSelectionMenu == null)
            return;
        pinsSelectionMenu.PostSelectAction = PostSelectAction.Close;

        var handlePinsSelection = (CCSPlayerController? player, ChatMenuOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedPaintName = option.Text;

            var playerPins = GPlayersPin.GetOrAdd(
                player.Slot,
                new ConcurrentDictionary<CsTeam, ushort>()
            );
            var teamsToCheck =
                player.TeamNum < 2
                    ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                    : [player.Team];

            var selectedPin = PinsList.FirstOrDefault(g =>
                g.ContainsKey("name") && g["name"]?.ToString() == selectedPaintName
            );
            if (selectedPin != null)
            {
                if (
                    !selectedPin.ContainsKey("id")
                    || !selectedPin.ContainsKey("name")
                    || !int.TryParse(selectedPin["id"]?.ToString(), out var paint)
                )
                    return;
                var image = selectedPin["image"]?.ToString() ?? "";
                if (Config.Additional.ShowSkinImage)
                {
                    _playerWeaponImage[player.Slot] = image;
                    AddTimer(
                        2.0f,
                        () => _playerWeaponImage.Remove(player.Slot),
                        TimerFlags.STOP_ON_MAPCHANGE
                    );
                }

                PlayerInfo playerInfo = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0],
                };

                if (paint != 0)
                {
                    foreach (var team in teamsToCheck)
                    {
                        playerPins[team] = (ushort)paint; // Set pin for each team
                    }
                }
                else
                {
                    foreach (var team in teamsToCheck)
                    {
                        playerPins[team] = 0; // Set pin for each team
                    }
                }

                if (!string.IsNullOrEmpty(Localizer["wp_pins_menu_select"]))
                {
                    player.Print(Localizer["wp_pins_menu_select", selectedPaintName]);
                }

                GivePlayerPin(player);

                if (WeaponSync != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await WeaponSync.SyncPinToDatabase(playerInfo, (ushort)paint, teamsToCheck);
                    });
                }
            }
            else
            {
                PlayerInfo playerInfo = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0],
                };

                foreach (var team in teamsToCheck)
                {
                    playerPins[team] = 0; // Set music for each team
                }

                if (!string.IsNullOrEmpty(Localizer["wp_pins_menu_select"]))
                {
                    player.Print(Localizer["wp_pins_menu_select", Localizer["None"]]);
                }

                GivePlayerPin(player);

                if (WeaponSync != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await WeaponSync.SyncPinToDatabase(playerInfo, 0, teamsToCheck);
                    });
                }
            }
        };

        pinsSelectionMenu.AddMenuOption(Localizer["None"], handlePinsSelection);
        // Add weapon options to the weapon selection menu
        foreach (
            var paintName in PinsList
                .Select(musicObject => musicObject["name"]?.ToString() ?? "")
                .Where(paintName => paintName.Length > 0)
        )
        {
            pinsSelectionMenu.AddMenuOption(paintName, handlePinsSelection);
        }

        // Command to open the weapon selection menu for players
        _config.Additional.CommandPin.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Pin selection menu",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed)
                        return;

                    if (player == null || player.UserId == null)
                        return;

                    if (
                        !CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime)
                        || DateTime.UtcNow
                            >= (
                                CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime)
                                    ? cooldownEndTime
                                    : DateTime.UtcNow
                            )
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(
                            Config.CmdRefreshCooldownSeconds
                        );
                        pinsSelectionMenu.Open(player);
                        return;
                    }

                    // if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                    // {
                    // 	player.Print(Localizer["wp_command_cooldown"]);
                    // }
                }
            );
        });
    }
}
