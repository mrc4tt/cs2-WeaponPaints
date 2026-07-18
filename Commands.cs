using System;
using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Menu;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

public partial class WeaponPaints
{
    private void OnCommandRefresh(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.CommandWpEnabled || !Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player))
            return;

        if (player == null || !player.IsValid || player.UserId == null || player.IsBot)
            return;

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
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

        PlayerInfo playerInfo = PlayerInfo.From(player);

        // Set cooldown immediately
        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

        if (WeaponSync != null)
        {
            // Ensure database data is loaded before refreshing weapons to avoid race conditions
            _ = Task.Run(async () =>
            {
                try
                {
                    await WeaponSync.GetPlayerData(playerInfo);
                    _weaponDataReady[playerInfo.Slot] = true;
                    // After DB sync, refresh player cosmetics and weapons
                    Server.NextFrame(() =>
                    {
                        if (player == null || !player.IsValid) return;

                        GivePlayerGloves(player);
                        // Single coordinated refresh (incl. knife). Splitting into RefreshKnife +
                        // RefreshWeapons(excludeKnife:true) ran two regive cycles that raced and
                        // dropped stickers (e.g. sticker slot 0 vanished after !wp while !sticker,
                        // which uses this same full call, kept it).
                        RefreshWeapons(player);
                        GivePlayerAgent(player);
                        GivePlayerMusicKit(player);
                        AddTimer(0.15f, () => GivePlayerPin(player), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
                    });
                }
                catch (Exception)
                {
                    // Fallback: even if DB fetch fails, still attempt a refresh (without skipping knife)
                    Server.NextFrame(() =>
                    {
                        if (player == null || !player.IsValid) return;

                        GivePlayerGloves(player);
                        RefreshWeapons(player);
                        GivePlayerAgent(player);
                        GivePlayerMusicKit(player);
                        AddTimer(0.15f, () => GivePlayerPin(player), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
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
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        if (command.ArgCount < 2)
        {
            // No arg form: stash pending-Wear state, prompt player, and intercept their
            // next say message via OnPlayerChatSeedWearInput.
            BeginSeedWearChatPrompt(player, PendingSeedWearKind.Wear);
            return;
        }

        if (!float.TryParse(command.GetArg(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatValue))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_invalid"]))
            {
                player.Print(Localizer["wp_float_invalid"]);
            }
            return;
        }

        ApplyHeldWeaponWear(player, floatValue);
    }

    private void OnCommandSeed(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        if (command.ArgCount < 2)
        {
            // No arg form: stash pending-Seed state, prompt player, and intercept their
            // next say message via OnPlayerChatSeedWearInput.
            BeginSeedWearChatPrompt(player, PendingSeedWearKind.Seed);
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

        ApplyHeldWeaponSeed(player, seedValue);
    }

    // Shared apply path for !float/wear and the chat-input no-arg flow. Caller is
    // responsible for the input parse + clamp; this assumes a sane value.
    private void ApplyHeldWeaponWear(CCSPlayerController player, float floatValue)
    {
        floatValue = Math.Clamp(floatValue, 0.00f, 1.00f);

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_no_weapon"]))
                player.Print(Localizer["wp_float_no_weapon"]);
            return;
        }

        var weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

        if (!HasChangedPaint(player, weaponDefIndex, out _))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_no_skin"]))
                player.Print(Localizer["wp_float_no_skin"]);
            return;
        }

        var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerSkins.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
            if (teamWeapons.TryGetValue(weaponDefIndex, out var weaponInfo))
                weaponInfo.Wear = floatValue;
        }

        // Clear any temporary wear override so the next apply uses the new value
        if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var tempWear))
            tempWear.TryRemove(weaponDefIndex, out _);

        var playerInfo = PlayerInfo.From(player);

        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
        {
            bool isKnife = weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");
            if (isKnife)
                RefreshKnife(player);
            else
                RefreshSingleWeapon(player, weaponDefIndex);
        }

        if (WeaponSync != null)
            _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));

        if (!string.IsNullOrEmpty(Localizer["wp_float_set"]))
            player.Print(Localizer["wp_float_set", floatValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private void ApplyHeldWeaponSeed(CCSPlayerController player, int seedValue)
    {
        seedValue = Math.Clamp(seedValue, 0, 1000);

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_no_weapon"]))
                player.Print(Localizer["wp_seed_no_weapon"]);
            return;
        }

        var weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

        if (!HasChangedPaint(player, weaponDefIndex, out _))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_no_skin"]))
                player.Print(Localizer["wp_seed_no_skin"]);
            return;
        }

        var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerSkins.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
            if (teamWeapons.TryGetValue(weaponDefIndex, out var weaponInfo))
                weaponInfo.Seed = seedValue;
        }

        var playerInfo = PlayerInfo.From(player);

        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
        {
            bool isKnife = weapon.DesignerName.Contains("knife") || weapon.DesignerName.Contains("bayonet");
            if (isKnife)
                RefreshKnife(player);
            else
                RefreshSingleWeapon(player, weaponDefIndex);
        }

        if (WeaponSync != null)
            _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));

        if (!string.IsNullOrEmpty(Localizer["wp_seed_set"]))
            player.Print(Localizer["wp_seed_set", seedValue]);
    }

    private void OnCommandGloveFloat(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.GloveEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        if (command.ArgCount < 2)
        {
            BeginGloveSeedWearChatPrompt(player, PendingSeedWearKind.GloveWear);
            return;
        }

        if (!float.TryParse(command.GetArg(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatValue))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_float_invalid"]))
                player.Print(Localizer["wp_float_invalid"]);
            return;
        }

        ApplyGloveWear(player, floatValue);
    }

    private void OnCommandGloveSeed(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.GloveEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        if (command.ArgCount < 2)
        {
            BeginGloveSeedWearChatPrompt(player, PendingSeedWearKind.GloveSeed);
            return;
        }

        if (!int.TryParse(command.GetArg(1), out var seedValue))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_seed_invalid"]))
                player.Print(Localizer["wp_seed_invalid"]);
            return;
        }

        ApplyGloveSeed(player, seedValue);
    }

    // Resolve the glove def index the player currently wears. Prefers the active team's
    // glove; falls back to any non-zero team entry (e.g. player still in warmup/spectator
    // when they picked). Returns false when the player has no custom glove applied.
    private bool TryGetCurrentGloveDefIndex(CCSPlayerController player, out int gloveDefIndex)
    {
        gloveDefIndex = 0;
        if (!GPlayersGlove.TryGetValue(player.Slot, out var gloves))
            return false;

        if (player.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist
            && gloves.TryGetValue(player.Team, out var teamGlove) && teamGlove != 0)
        {
            gloveDefIndex = teamGlove;
            return true;
        }

        foreach (var kv in gloves)
        {
            if (kv.Value != 0)
            {
                gloveDefIndex = kv.Value;
                return true;
            }
        }
        return false;
    }

    // Shared apply path for glove !glovefloat and its chat-input no-arg flow. Gloves are
    // never the "held" active weapon, so unlike ApplyHeldWeaponWear the target def index
    // comes from GPlayersGlove, and the refresh goes through GivePlayerGloves.
    private void ApplyGloveWear(CCSPlayerController player, float floatValue)
    {
        floatValue = Math.Clamp(floatValue, 0.00f, 1.00f);

        if (!TryGetCurrentGloveDefIndex(player, out var gloveDefIndex))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_glove_no_glove"]))
                player.Print(Localizer["wp_glove_no_glove"]);
            return;
        }

        if (!HasChangedPaint(player, gloveDefIndex, out _))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_glove_no_skin"]))
                player.Print(Localizer["wp_glove_no_skin"]);
            return;
        }

        var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerSkins.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
            if (teamWeapons.TryGetValue(gloveDefIndex, out var weaponInfo))
                weaponInfo.Wear = floatValue;
        }

        var playerInfo = PlayerInfo.From(player);

        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
            GivePlayerGloves(player);

        if (WeaponSync != null)
            _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));

        if (!string.IsNullOrEmpty(Localizer["wp_float_set"]))
            player.Print(Localizer["wp_float_set", floatValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)]);
    }

    private void ApplyGloveSeed(CCSPlayerController player, int seedValue)
    {
        seedValue = Math.Clamp(seedValue, 0, 1000);

        if (!TryGetCurrentGloveDefIndex(player, out var gloveDefIndex))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_glove_no_glove"]))
                player.Print(Localizer["wp_glove_no_glove"]);
            return;
        }

        if (!HasChangedPaint(player, gloveDefIndex, out _))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_glove_no_skin"]))
                player.Print(Localizer["wp_glove_no_skin"]);
            return;
        }

        var playerSkins = GPlayerWeaponsInfo.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerSkins.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
            if (teamWeapons.TryGetValue(gloveDefIndex, out var weaponInfo))
                weaponInfo.Seed = seedValue;
        }

        var playerInfo = PlayerInfo.From(player);

        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
            GivePlayerGloves(player);

        if (WeaponSync != null)
            _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));

        if (!string.IsNullOrEmpty(Localizer["wp_seed_set"]))
            player.Print(Localizer["wp_seed_set", seedValue]);
    }

    private void BeginGloveSeedWearChatPrompt(CCSPlayerController player, PendingSeedWearKind kind)
    {
        if (!TryGetCurrentGloveDefIndex(player, out var gloveDefIndex))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_glove_no_glove"]))
                player.Print(Localizer["wp_glove_no_glove"]);
            return;
        }

        if (!HasChangedPaint(player, gloveDefIndex, out _))
        {
            if (!string.IsNullOrEmpty(Localizer["wp_glove_no_skin"]))
                player.Print(Localizer["wp_glove_no_skin"]);
            return;
        }

        GPlayersPendingSeedWearInput[player.Slot] = kind;

        var promptKey = kind == PendingSeedWearKind.GloveSeed ? "wp_seed_prompt" : "wp_float_prompt";
        if (!string.IsNullOrEmpty(Localizer[promptKey]))
            player.Print(Localizer[promptKey]);
    }

    private void BeginSeedWearChatPrompt(CCSPlayerController player, PendingSeedWearKind kind)
    {
        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
        {
            var noWeaponKey = kind == PendingSeedWearKind.Seed ? "wp_seed_no_weapon" : "wp_float_no_weapon";
            if (!string.IsNullOrEmpty(Localizer[noWeaponKey]))
                player.Print(Localizer[noWeaponKey]);
            return;
        }

        if (!HasChangedPaint(player, weapon.AttributeManager.Item.ItemDefinitionIndex, out _))
        {
            var noSkinKey = kind == PendingSeedWearKind.Seed ? "wp_seed_no_skin" : "wp_float_no_skin";
            if (!string.IsNullOrEmpty(Localizer[noSkinKey]))
                player.Print(Localizer[noSkinKey]);
            return;
        }

        GPlayersPendingSeedWearInput[player.Slot] = kind;

        var promptKey = kind == PendingSeedWearKind.Seed ? "wp_seed_prompt" : "wp_float_prompt";
        if (!string.IsNullOrEmpty(Localizer[promptKey]))
            player.Print(Localizer[promptKey]);
    }

    // HookMode.Pre listener on say/say_team. Returns Continue normally so chat flows;
    // returns Handled when we consumed the message (pending input present and parsed)
    // so the value isn't echoed in public chat.
    private HookResult OnPlayerChatSeedWearInput(CCSPlayerController? player, CommandInfo info)
    {
        if (!Utility.IsPlayerValid(player) || player is null)
            return HookResult.Continue;
        if (!GPlayersPendingSeedWearInput.TryGetValue(player.Slot, out var kind))
            return HookResult.Continue;

        var input = info.GetArg(1)?.Trim().Trim('"');
        if (string.IsNullOrEmpty(input))
            return HookResult.Continue;

        // Let the player escape the prompt via another command; don't consume slashes.
        if (input.StartsWith('!') || input.StartsWith('/'))
        {
            GPlayersPendingSeedWearInput.TryRemove(player.Slot, out _);
            return HookResult.Continue;
        }

        if (kind is PendingSeedWearKind.Seed or PendingSeedWearKind.GloveSeed)
        {
            if (!int.TryParse(input, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seedValue))
            {
                if (!string.IsNullOrEmpty(Localizer["wp_seed_invalid"]))
                    player.Print(Localizer["wp_seed_invalid"]);
                return HookResult.Handled;
            }
            if (kind == PendingSeedWearKind.GloveSeed)
                ApplyGloveSeed(player, seedValue);
            else
                ApplyHeldWeaponSeed(player, seedValue);
        }
        else
        {
            if (!float.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatValue)
                && !float.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out floatValue))
            {
                if (!string.IsNullOrEmpty(Localizer["wp_float_invalid"]))
                    player.Print(Localizer["wp_float_invalid"]);
                return HookResult.Handled;
            }
            if (kind == PendingSeedWearKind.GloveWear)
                ApplyGloveWear(player, floatValue);
            else
                ApplyHeldWeaponWear(player, floatValue);
        }

        GPlayersPendingSeedWearInput.TryRemove(player.Slot, out _);
        return HookResult.Handled;
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
            CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
            OpenWeaponSelectionMenu(player);
            return;
        }

        var weaponArg = command.GetArg(1).ToLower();

        // Try to find the weapon by name or class name
        string? selectedWeaponClassname = null;
        string? selectedWeaponName = null;

        // Pass 1: exact match first so "bayonet" picks weapon_bayonet, not weapon_knife_m9_bayonet.
        foreach (var kvp in WeaponList)
        {
            var classNameStripped = kvp.Key.ToLower().Replace("weapon_", "");
            var displayName = kvp.Value.ToLower();
            var displayNameCompact = displayName.Replace("-", "").Replace(" ", "");

            if (classNameStripped == weaponArg || displayName == weaponArg || displayNameCompact == weaponArg)
            {
                selectedWeaponClassname = kvp.Key;
                selectedWeaponName = kvp.Value;
                break;
            }
        }

        // Pass 2: fall back to substring match (e.g., "ak" → "AK-47", "m9" → "M9 Bayonet").
        if (selectedWeaponClassname == null)
        {
            foreach (var kvp in WeaponList)
            {
                var className = kvp.Key.ToLower();
                var displayName = kvp.Value.ToLower();

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
        }

        if (selectedWeaponClassname == null)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_skin_weapon_not_found"]))
            {
                player.Print(Localizer["wp_skin_weapon_not_found", weaponArg]);
            }
            return;
        }

        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
        OpenSkinSelectionMenuForWeapon(player, selectedWeaponClassname, selectedWeaponName!);
    }

    // Weapons that belong to a category. Knives are matched by classname prefix so all
    // knife/bayonet variants land under the "Knives" category regardless of WeaponCategory.
    private static List<KeyValuePair<string, string>> GetWeaponsInCategory(string category)
    {
        return WeaponList.Where(kvp =>
        {
            if (category == "Knives")
                return kvp.Key.StartsWith("weapon_knife") || kvp.Key.StartsWith("weapon_bayonet");
            return WeaponCategory.TryGetValue(kvp.Key, out var c) && c == category;
        }).ToList();
    }

    // Top level of the organized skin menu: pick a category (Rifles/Pistols/...).
    private void OpenWeaponSelectionMenu(CCSPlayerController player)
    {
        var categoryMenu = Utility.CreateMenu(Localizer["wp_skin_menu_category_title"]);
        if (categoryMenu == null)
            return;

        foreach (var category in WeaponCategoryOrder)
        {
            var weaponsInCategory = GetWeaponsInCategory(category);
            if (weaponsInCategory.Count == 0)
                continue;

            var cat = category;
            categoryMenu.AddItem(Localizer["wp_cat_" + cat.ToLower()], (p, option) =>
            {
                if (!Utility.IsPlayerValid(p) || p == null)
                    return;

                OpenWeaponCategoryMenu(p, cat, weaponsInCategory);
            });
        }

        categoryMenu.Display(player, 0);
    }

    // Second level: weapons within a chosen category → skin submenu.
    private void OpenWeaponCategoryMenu(CCSPlayerController player, string category, List<KeyValuePair<string, string>> weapons)
    {
        var weaponMenu = Utility.CreateMenu(Localizer["wp_cat_" + category.ToLower()]);
        if (weaponMenu == null)
            return;

        foreach (var kvp in weapons)
        {
            var weaponClassname = kvp.Key;
            var weaponName = kvp.Value;
            weaponMenu.AddItem(weaponName, (p, option) =>
            {
                if (!Utility.IsPlayerValid(p) || p == null)
                    return;

                OpenSkinSelectionMenuForWeapon(p, weaponClassname, weaponName);
            });
        }

        weaponMenu.Display(player, 0);
    }

    private void OpenSkinSelectionMenuForWeapon(CCSPlayerController player, string weaponClassname, string weaponDisplayName)
    {
        var skinsForSelectedWeapon = SkinsList.Where(skin => skin.TryGetValue("weapon_name", out var weaponName) && weaponName?.ToString() == weaponClassname)?.ToList();

        if (skinsForSelectedWeapon == null || skinsForSelectedWeapon.Count == 0)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_skin_no_skins_found"]))
            {
                player.Print(Localizer["wp_skin_no_skins_found", weaponDisplayName]);
            }
            return;
        }

        var skinSubMenu = Utility.CreateMenu(Localizer["wp_skin_menu_skin_title", weaponDisplayName]);
        if (skinSubMenu == null)
            return;

        var handleSkinSelection = (CCSPlayerController p, ItemOption opt) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;

            SkinsByWeaponName.TryGetValue(weaponClassname, out var weaponSkins);
            var firstSkin = weaponSkins?.FirstOrDefault();

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
                    JObject? foundSkin = null;
                    if (weaponSkins != null)
                    {
                        foreach (var s in weaponSkins)
                        {
                            if (((int?)s["paint"] ?? 0) == paintId && s["image"] != null)
                            {
                                foundSkin = s;
                                break;
                            }
                        }
                    }
                    var image = foundSkin?["image"]?.ToString() ?? "";
                    _playerWeaponImage[p.Slot] = image;
                    AddTimer(2.0f, () => _playerWeaponImage.Remove(p.Slot), TimerFlags.STOP_ON_MAPCHANGE);
                }

                p.Print(Localizer["wp_skin_menu_select", selectedSkin]);
                var playerSkins = GPlayerWeaponsInfo.GetOrAdd(p.Slot, new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());

                var teamsToCheck = p.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [p.Team];

                foreach (var team in teamsToCheck)
                {
                    var teamWeapons = playerSkins.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
                    var value = teamWeapons.GetOrAdd(weaponDefIndex, _ => new WeaponInfo());
                    value.Paint = paintId;
                    value.Wear = 0.01f;
                    value.Seed = 0;
                }

                var playerInfo = PlayerInfo.From(p);

                if (!_gBCommandsAllowed || (LifeState_t)p.LifeState != LifeState_t.LIFE_ALIVE || WeaponSync == null)
                    return;

                // Refresh only the specific weapon we changed the skin for (preserves knife)
                bool isKnife = weaponClassname.Contains("knife") || weaponClassname.Contains("bayonet");
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
                    _ = Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo));
                }
                catch (Exception ex)
                {
                    Utility.Log($"Error syncing weapon paints: {ex.Message}");
                }
            }
        };

        foreach (var skin in skinsForSelectedWeapon)
        {
            if (!skin.TryGetValue("paint_name", out var paintNameObj) || !skin.TryGetValue("paint", out var paintObj))
                continue;
            var paintName = paintNameObj?.ToString();
            var paint = paintObj?.ToString();

            if (!string.IsNullOrEmpty(paintName) && !string.IsNullOrEmpty(paint))
            {
                skinSubMenu.AddItem($"{paintName} ({paint})", handleSkinSelection);
            }
        }

        skinSubMenu.Display(player, 0);
    }

    private void OnCommandMenu(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !Utility.IsPlayerValid(player))
            return;

        // CS2MenuManager builds the menu-type chooser; selecting an item persists via SetPlayerMenuType.
        var chooser = MenuTypeManager.MenuTypeMenuByType(typeof(PlayerMenu), player, this, null!);
        chooser.Display(player, 0);
    }

    private void RegisterCommands()
    {
        Config.Additional.CommandStattrak.ForEach(c =>
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

        Config.Additional.CommandMenu.ForEach(c =>
        {
            AddCommand($"css_{c}", "Pick your preferred menu style", (player, info) => OnCommandMenu(player, info));
        });

        Config.Additional.CommandSkin.ForEach(c =>
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

        Config.Additional.CommandRefresh.ForEach(c =>
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
        Config.Additional.CommandFloat.ForEach(c =>
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
        Config.Additional.CommandSeed.ForEach(c =>
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

        // Register glove float command
        Config.Additional.CommandGloveFloat.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Set glove float/wear value",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandGloveFloat(player, info);
                }
            );
        });

        // Register glove seed command
        Config.Additional.CommandGloveSeed.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Set glove pattern seed",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandGloveSeed(player, info);
                }
            );
        });

        // Register sticker command
        Config.Additional.CommandSticker.ForEach(c =>
        {
            AddCommand(
                $"css_{c}",
                "Apply sticker to held weapon",
                (player, info) =>
                {
                    if (!Utility.IsPlayerValid(player))
                        return;
                    OnCommandSticker(player, info);
                }
            );
        });

        // Chat-input interceptor for !seed / !float no-arg flow. Pre-hook so we can swallow
        // the value message (HookResult.Handled) without it appearing in public chat.
        AddCommandListener("say", OnPlayerChatSeedWearInput, HookMode.Pre);
        AddCommandListener("say_team", OnPlayerChatSeedWearInput, HookMode.Pre);

        if (Config.Additional.CommandKillEnabled)
        {
            Config.Additional.CommandKill.ForEach(c =>
            {
                AddCommand(
                    $"css_{c}",
                    "kill yourself",
                    (player, _) =>
                    {
                        if (player == null || !Utility.IsPlayerValid(player) || player.PlayerPawn.Value == null || !player.PlayerPawn.IsValid)
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
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

        if (weapon == null || !weapon.IsValid)
            return;

        if (!HasChangedPaint(player, weapon.AttributeManager.Item.ItemDefinitionIndex, out var weaponInfo) || weaponInfo == null)
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

        var knivesOnly = WeaponList.Where(pair => pair.Key.StartsWith("weapon_knife") || pair.Key.StartsWith("weapon_bayonet")).ToDictionary(pair => pair.Key, pair => pair.Value);

        var giveItemMenu = Utility.CreateMenu(Localizer["wp_knife_menu_title"]);

        var handleGive = (CCSPlayerController player, ItemOption option) =>
        {
            if (!Utility.IsPlayerValid(player))
                return;

            var playerKnives = GPlayersKnife.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, string>());
            var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

            var knifeName = option.Text;
            var knifeKey = knivesOnly.FirstOrDefault(x => x.Value == knifeName).Key;
            if (string.IsNullOrEmpty(knifeKey))
                return;
            if (!string.IsNullOrEmpty(Localizer["wp_knife_menu_select"]))
            {
                player.Print(Localizer["wp_knife_menu_select", knifeName]);
            }

            if (!string.IsNullOrEmpty(Localizer["wp_knife_menu_kill"]) && Config.Additional.CommandKillEnabled)
            {
                player.Print(Localizer["wp_knife_menu_kill"]);
            }

            PlayerInfo playerInfo = PlayerInfo.From(player);

            foreach (var team in teamsToCheck)
            {
                // Attempt to get the existing knives
                playerKnives[team] = knifeKey;
            }

            if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
                RefreshWeapons(player);

            if (WeaponSync != null)
                _ = Task.Run(async () => await WeaponSync.SyncKnifeToDatabase(playerInfo, knifeKey, teamsToCheck));
        };
        foreach (var knifePair in knivesOnly)
        {
            giveItemMenu?.AddItem(knifePair.Value, handleGive);
        }
        Config.Additional.CommandKnife.ForEach(c =>
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
                        || DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow)
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

                        giveItemMenu.Display(player, 0);

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
        Config.Additional.CommandSkinSelection.ForEach(c =>
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

        var handleGloveSelection = (CCSPlayerController player, ItemOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedPaintName = option.Text;

            var playerGloves = GPlayersGlove.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ushort>());
            var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

            GlovesByPaintName.TryGetValue(selectedPaintName, out var selectedGlove);
            var image = selectedGlove?["image"]?.ToString() ?? "";
            if (selectedGlove == null || !int.TryParse(selectedGlove["weapon_defindex"]?.ToString(), out var weaponDefindex) || !int.TryParse(selectedGlove["paint"]?.ToString(), out var paint))
                return;
            if (Config.Additional.ShowSkinImage)
            {
                _playerWeaponImage[player.Slot] = image;
                AddTimer(2.0f, () => _playerWeaponImage.Remove(player.Slot), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
            }

            PlayerInfo playerInfo = PlayerInfo.From(player);

            if (paint != 0)
            {
                // Snapshot the player's default gloves *before* the first custom override,
                // so a later "None" pick can revert without respawn.
                var pawn = player.PlayerPawn.Value;
                if (pawn != null && pawn.IsValid)
                {
                    CacheCurrentNativeGloveSnapshot(player, pawn);
                }

                var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
                foreach (var team in teamsToCheck)
                {
                    playerGloves[team] = (ushort)weaponDefindex;
                    var weaponInfo = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>()).GetOrAdd(weaponDefindex, _ => new WeaponInfo());
                    weaponInfo.Paint = paint;
                    weaponInfo.Wear = 0.00f;
                    weaponInfo.Seed = 0;
                }
            }
            else
            {
                foreach (var team in teamsToCheck)
                {
                    playerGloves.TryRemove(team, out _);
                }
                if (playerGloves.IsEmpty)
                {
                    GPlayersGlove.TryRemove(player.Slot, out _);
                }
            }

            if (WeaponSync == null)
                return;

            // Single DB round-trip for both syncs — previous loop ran them once per team.
            _ = Task.Run(async () =>
            {
                await WeaponSync.SyncGloveToDatabase(playerInfo, (ushort)weaponDefindex, teamsToCheck);
                await WeaponSync.SyncWeaponPaintsToDatabase(playerInfo);
            });

            if (paint == 0)
            {
                AddTimer(0.05f, () => RestorePlayerDefaultGloves(player), TimerFlags.STOP_ON_MAPCHANGE);
            }
            else
            {
                AddTimer(0.1f, () => GivePlayerGloves(player), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
            }

			//force gloves model refresh to prevent model overlap
			player.ExecuteClientCommand("lastinv");
			AddTimer(0.15f, () =>
			{
				if (player.IsValid && player.PawnIsAlive)
					player.ExecuteClientCommand("lastinv");
			}, TimerFlags.STOP_ON_MAPCHANGE);
        };

        // Add weapon options to the weapon selection menu
        foreach (var paintName in GlovesList.Select(gloveObject => gloveObject["paint_name"]?.ToString() ?? "").Where(paintName => paintName.Length > 0))
        {
            glovesSelectionMenu.AddItem(paintName, handleGloveSelection);
        }

        // Command to open the weapon selection menu for players
        Config.Additional.CommandGlove.ForEach(c =>
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
                        || DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow)
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                        glovesSelectionMenu?.Display(player, 0);
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

        var handleAgentSelection = (CCSPlayerController player, ItemOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedAgentName = option.Text;
            var playerTeam = player.TeamNum.ToString();

            AgentsByNameAndTeam.TryGetValue((selectedAgentName, player.TeamNum), out var selectedAgent);
            var image = selectedAgent?["image"]?.ToString() ?? "";
            if (selectedAgent == null || !selectedAgent.ContainsKey("model") || !selectedAgent.ContainsKey("team"))
                return;

            if (Config.Additional.ShowSkinImage)
            {
                _playerWeaponImage[player.Slot] = image;
                AddTimer(2.0f, () => _playerWeaponImage.Remove(player.Slot), TimerFlags.STOP_ON_MAPCHANGE);
            }

            var model = selectedAgent["model"]?.ToString();
            var team = selectedAgent["team"]?.ToString();

            if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(team))
                return;

            PlayerInfo playerInfo = PlayerInfo.From(player);

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
                AddTimer(0.1f, () => GivePlayerAgent(player), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
                AddTimer(0.25f, () => GivePlayerAgent(player), CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
            }
            else if (player.PawnIsAlive && player.PlayerPawn.Value != null && OriginalPawnModel.TryGetValue(player.Slot, out var defaultModel))
            {
                // Player selected "Agent | Default" while alive — restore the map/team's
                // default model captured on this player's last spawn. If the cache is empty
                // (plugin hot-reloaded mid-round, or model path wasn't readable), we fall
                // through silently and the change applies on the next natural respawn.
                var pawn = player.PlayerPawn.Value;
                Server.NextFrame(() =>
                {
                    try
                    {
                        if (pawn.IsValid)
                            pawn.SetModel(defaultModel);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Default agent restore failed: {Message}", ex.Message);
                    }
                });
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
                ctAgentMenu.AddItem(agentName, handleAgentSelection);
            else if (agentTeam == "2")
                tAgentMenu.AddItem(agentName, handleAgentSelection);
        }

        // Command to open the agent selection menu for players
        Config.Additional.CommandAgent.ForEach(c =>
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
                        || DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow)
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

                        if (player.TeamNum == 3)
                            ctAgentMenu?.Display(player, 0);
                        else if (player.TeamNum == 2)
                            tAgentMenu?.Display(player, 0);
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

        var handleMusicSelection = (CCSPlayerController player, ItemOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedPaintName = option.Text;

            var playerMusic = GPlayersMusic.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ushort>());
            var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team]; // Corrected array initializer

            var selectedMusic = MusicList.FirstOrDefault(g => g.ContainsKey("name") && g["name"]?.ToString() == selectedPaintName);
            if (selectedMusic != null)
            {
                if (!selectedMusic.ContainsKey("id") || !selectedMusic.ContainsKey("name") || !int.TryParse(selectedMusic["id"]?.ToString(), out var paint))
                    return;
                var image = selectedMusic["image"]?.ToString() ?? "";
                if (Config.Additional.ShowSkinImage)
                {
                    _playerWeaponImage[player.Slot] = image;
                    AddTimer(2.0f, () => _playerWeaponImage.Remove(player.Slot), TimerFlags.STOP_ON_MAPCHANGE);
                }

                PlayerInfo playerInfo = PlayerInfo.From(player);

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
                        await WeaponSync.SyncMusicToDatabase(playerInfo, (ushort)paint, teamsToCheck);
                    });
                }
            }
            else
            {
                PlayerInfo playerInfo = PlayerInfo.From(player);

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

        musicSelectionMenu.AddItem(Localizer["None"], handleMusicSelection);
        // Add weapon options to the weapon selection menu
        foreach (var paintName in MusicList.Select(musicObject => musicObject["name"]?.ToString() ?? "").Where(paintName => paintName.Length > 0))
        {
            musicSelectionMenu.AddItem(paintName, handleMusicSelection);
        }

        // Command to open the weapon selection menu for players
        Config.Additional.CommandMusic.ForEach(c =>
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
                        || DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow)
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                        musicSelectionMenu.Display(player, 0);
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

        var handlePinsSelection = (CCSPlayerController player, ItemOption option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null)
                return;

            var selectedPaintName = option.Text;

            var playerPins = GPlayersPin.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ushort>());
            var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : [player.Team];

            var selectedPin = PinsList.FirstOrDefault(g => g.ContainsKey("name") && g["name"]?.ToString() == selectedPaintName);
            if (selectedPin != null)
            {
                if (!selectedPin.ContainsKey("id") || !selectedPin.ContainsKey("name") || !int.TryParse(selectedPin["id"]?.ToString(), out var paint))
                    return;
                var image = selectedPin["image"]?.ToString() ?? "";
                if (Config.Additional.ShowSkinImage)
                {
                    _playerWeaponImage[player.Slot] = image;
                    AddTimer(2.0f, () => _playerWeaponImage.Remove(player.Slot), TimerFlags.STOP_ON_MAPCHANGE);
                }

                PlayerInfo playerInfo = PlayerInfo.From(player);

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
                        playerPins.TryRemove(team, out _);
                    }
                    if (playerPins.IsEmpty)
                    {
                        GPlayersPin.TryRemove(player.Slot, out _);
                    }
                }

                if (!string.IsNullOrEmpty(Localizer["wp_pins_menu_select"]))
                {
                    player.Print(Localizer["wp_pins_menu_select", selectedPaintName]);
                }

                if (paint != 0)
                    GivePlayerPin(player);
                else
                    RestorePlayerDefaultPin(player);

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
                PlayerInfo playerInfo = PlayerInfo.From(player);

                foreach (var team in teamsToCheck)
                {
                    playerPins.TryRemove(team, out _);
                }

                if (playerPins.IsEmpty)
                {
                    GPlayersPin.TryRemove(player.Slot, out _);
                }

                if (!string.IsNullOrEmpty(Localizer["wp_pins_menu_select"]))
                {
                    player.Print(Localizer["wp_pins_menu_select", Localizer["None"]]);
                }

                RestorePlayerDefaultPin(player);

                if (WeaponSync != null)
                {
                    _ = Task.Run(async () =>
                    {
                        await WeaponSync.SyncPinToDatabase(playerInfo, 0, teamsToCheck);
                    });
                }
            }
        };

        pinsSelectionMenu.AddItem(Localizer["None"], handlePinsSelection);
        // Add weapon options to the weapon selection menu
        foreach (var paintName in PinsList.Select(musicObject => musicObject["name"]?.ToString() ?? "").Where(paintName => paintName.Length > 0))
        {
            pinsSelectionMenu.AddItem(paintName, handlePinsSelection);
        }

        // Command to open the weapon selection menu for players
        Config.Additional.CommandPin.ForEach(c =>
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
                        || DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow)
                    )
                    {
                        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                        pinsSelectionMenu.Display(player, 0);
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

    // Per-slot snapshot of the sticker list a player should see in their next sticker
    // sub-menu. Set when !sticker is invoked (filtered or full) and read once the slot
    // sub-menu picks a slot. Cleared on player disconnect via OnPlayerDisconnect.
    private static readonly ConcurrentDictionary<int, List<JObject>> _stickerCommandFilters = new();

    private void OnCommandSticker(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_sticker_no_weapon"]))
                player.Print(Localizer["wp_sticker_no_weapon"]);
            return;
        }

        var weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

        // No skin requirement: stickers can be applied to a weapon with no custom paint.

        var query = "";
        if (command.ArgCount >= 2)
        {
            var parts = new string[command.ArgCount - 1];
            for (var i = 0; i < parts.Length; i++)
                parts[i] = command.GetArg(i + 1);
            query = string.Join(' ', parts).Trim();
        }

        List<JObject> filtered;
        if (string.IsNullOrEmpty(query))
        {
            filtered = StickersList;
        }
        else if (uint.TryParse(query, out var idQuery) && StickersById.TryGetValue(idQuery, out var hit))
        {
            filtered = new List<JObject> { hit };
        }
        else
        {
            var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            filtered = StickersList
                .Where(s =>
                {
                    var name = s["name"]?.ToString()?.ToLowerInvariant() ?? "";
                    return tokens.All(t => name.Contains(t));
                })
                .ToList();
        }

        if (filtered.Count == 0)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_sticker_no_match"]))
                player.Print(Localizer["wp_sticker_no_match", query]);
            return;
        }

        _stickerCommandFilters[player.Slot] = filtered;
        OpenStickerSlotMenu(player, weaponDefIndex);
    }

    private void OpenStickerSlotMenu(CCSPlayerController player, int weaponDefIndex)
    {
        var menu = Utility.CreateMenu(Localizer["wp_sticker_slot_title"]);
        if (menu == null)
            return;

        for (var slot = 0; slot < 5; slot++)
        {
            var capturedSlot = slot;
            var label = Localizer["wp_sticker_slot_label", slot].ToString();
            if (string.IsNullOrEmpty(label))
                label = $"Slot {slot}";

            menu.AddItem(
                label,
                (p, _) =>
                {
                    if (!Utility.IsPlayerValid(p))
                        return;
                    OpenStickerListMenu(p, weaponDefIndex, capturedSlot);
                }
            );
        }

        var removeAllLabel = Localizer["wp_sticker_remove_all"].ToString();
        if (string.IsNullOrEmpty(removeAllLabel))
            removeAllLabel = "Remove all stickers";

        menu.AddItem(
            removeAllLabel,
            (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                ClearAllStickers(p, weaponDefIndex);
            }
        );

        menu.Display(player, 0);
    }

    private void OpenStickerListMenu(CCSPlayerController player, int weaponDefIndex, int slot)
    {
        if (!_stickerCommandFilters.TryGetValue(player.Slot, out var list))
            list = StickersList;

        var title = Localizer["wp_sticker_menu_title", slot].ToString();
        if (string.IsNullOrEmpty(title))
            title = $"Stickers - Slot {slot}";

        var menu = Utility.CreateMenu(title);
        if (menu == null)
            return;

        var clearLabel = Localizer["wp_sticker_clear_slot"].ToString();
        if (string.IsNullOrEmpty(clearLabel))
            clearLabel = "Clear this slot";

        menu.AddItem(
            clearLabel,
            (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                ApplySticker(p, weaponDefIndex, slot, null);
            }
        );

        foreach (var s in list)
        {
            var name = s["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            if (!uint.TryParse(s["id"]?.ToString(), out var id))
                continue;

            var capturedId = id;
            menu.AddItem(
                name,
                (p, _) =>
                {
                    if (!Utility.IsPlayerValid(p))
                        return;
                    ApplySticker(
                        p,
                        weaponDefIndex,
                        slot,
                        new StickerInfo
                        {
                            Id = capturedId,
                            Schema = 0,
                            OffsetX = 0,
                            OffsetY = 0,
                            Wear = 0,
                            Scale = 1,
                            Rotation = 0,
                        }
                    );
                }
            );
        }

        menu.Display(player, 0);
    }

    private void ApplySticker(CCSPlayerController player, int weaponDefIndex, int slot, StickerInfo? sticker)
    {
        if (slot < 0 || slot > 4)
            return;

        // Sticker without skin: reuse the existing WeaponInfo if there is one (paint or not),
        // otherwise create a paint-0 entry so the sticker can attach to the default weapon.
        if (!TryGetWeaponInfo(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
            weaponInfo = new WeaponInfo();

        // Pad list to 5 slots so we can write to any index.
        while (weaponInfo.Stickers.Count < 5)
        {
            weaponInfo.Stickers.Add(new StickerInfo());
        }

        weaponInfo.Stickers[slot] = sticker ?? new StickerInfo();

        var playerInfo = PlayerInfo.From(player);
        var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : new[] { player.Team };

        var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        foreach (var team in teamsToCheck)
        {
            var teamWeapons = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
            teamWeapons[weaponDefIndex] = weaponInfo;
        }

        // Defer the refresh so the sticker menu has closed and the weapon is redrawn before the
        // kill+regive. Refreshing from inside the menu callback (menu still open) applied the
        // attributes but the weapon wasn't composited until a later !wp — typed in chat with the
        // menu already closed. The short delay makes the live apply behave like that !wp.
        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
        {
            AddTimer(0.25f, () =>
            {
                if (!Utility.IsPlayerValid(player) || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
                    return;
                var active = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
                var isKnife = active != null && active.IsValid && active.AttributeManager.Item.ItemDefinitionIndex == weaponDefIndex && (active.DesignerName.Contains("knife") || active.DesignerName.Contains("bayonet"));
                if (isKnife)
                    RefreshKnife(player);
                else
                    RefreshWeapons(player);
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }

        if (WeaponSync != null)
        {
            _ = Task.Run(async () => await WeaponSync.SyncStickerToDatabase(playerInfo, weaponDefIndex, slot, sticker, teamsToCheck));
        }

        if (sticker != null)
        {
            if (!string.IsNullOrEmpty(Localizer["wp_sticker_applied"]))
            {
                var displayName = StickersById.TryGetValue(sticker.Id, out var so) ? so["name"]?.ToString() ?? sticker.Id.ToString() : sticker.Id.ToString();
                player.Print(Localizer["wp_sticker_applied", displayName, slot]);
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(Localizer["wp_sticker_cleared"]))
                player.Print(Localizer["wp_sticker_cleared", slot]);
        }
    }

    private void ClearAllStickers(CCSPlayerController player, int weaponDefIndex)
    {
        if (!TryGetWeaponInfo(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
            return;

        // Keep 5 empty (Id 0) slots — NOT an empty list. On the regive, SetStickers sees an
        // all-empty list and writes "sticker slot N id 0" to every slot, which invalidates the
        // client's cached composite so the old decals are actually removed. An empty list would
        // write nothing and the stale decals would linger.
        weaponInfo.Stickers.Clear();
        for (var i = 0; i < 5; i++)
            weaponInfo.Stickers.Add(new StickerInfo());

        var playerInfo = PlayerInfo.From(player);
        var teamsToCheck = player.TeamNum < 2 ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } : new[] { player.Team };

        // Defer so the menu has closed and the weapon is redrawn before the regive (see ApplySticker).
        if (_gBCommandsAllowed && (LifeState_t)player.LifeState == LifeState_t.LIFE_ALIVE)
        {
            AddTimer(0.25f, () =>
            {
                if (!Utility.IsPlayerValid(player) || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
                    return;
                var active = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
                var isKnife = active != null && active.IsValid && active.AttributeManager.Item.ItemDefinitionIndex == weaponDefIndex && (active.DesignerName.Contains("knife") || active.DesignerName.Contains("bayonet"));
                if (isKnife)
                    RefreshKnife(player);
                else
                    RefreshWeapons(player);
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
        }

        if (WeaponSync != null)
        {
            _ = Task.Run(async () =>
            {
                for (var slot = 0; slot < 5; slot++)
                    await WeaponSync.SyncStickerToDatabase(playerInfo, weaponDefIndex, slot, null, teamsToCheck);
            });
        }

        if (!string.IsNullOrEmpty(Localizer["wp_sticker_cleared_all"]))
            player.Print(Localizer["wp_sticker_cleared_all"]);
    }
}
