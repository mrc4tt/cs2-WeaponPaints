using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace WeaponPaints;

// StatTrak kill counting and persistence.
//
// Kills are credited to the weapon that made them (player_death's "weapon"), not to whatever the
// attacker holds afterwards — a grenade/molotov kill used to land on the gun they switched back to.
// Counts live in WeaponInfo.StatTrakCount; the (slot, team, defindex) rows that changed are marked
// dirty and written every StatTrakFlushSeconds, on disconnect and on unload, so a crash or reload
// loses at most one flush interval instead of a whole session.
public partial class WeaponPaints
{
    // Game thread only.
    private readonly HashSet<(int Slot, CsTeam Team, int DefIndex)> _statTrakDirty = new();

    private void StartStatTrakFlushTimer()
    {
        if (Config.Additional.StatTrakFlushSeconds <= 0)
            return;
        AddTimer(Math.Max(10, Config.Additional.StatTrakFlushSeconds), () => FlushStatTrak(slotFilter: null), TimerFlags.REPEAT);
    }

    internal void MarkStatTrakDirty(CCSPlayerController player, int weaponDefIndex)
    {
        foreach (var team in TeamsFor(player))
            _statTrakDirty.Add((player.Slot, team, weaponDefIndex));
    }

    // Snapshot the dirty rows (optionally of one slot) and queue them. Game thread only.
    // `weaponsOverride` is the disconnecting player's already-detached weapon table.
    private void FlushStatTrak(int? slotFilter, PlayerInfo? infoOverride = null,
        IReadOnlyDictionary<CsTeam, System.Collections.Concurrent.ConcurrentDictionary<int, WeaponInfo>>? weaponsOverride = null)
    {
        if (_statTrakDirty.Count == 0)
            return;

        var bySlot = new Dictionary<int, List<(CsTeam Team, int DefIndex)>>();
        foreach (var key in _statTrakDirty)
        {
            if (slotFilter.HasValue && key.Slot != slotFilter.Value)
                continue;
            if (!bySlot.TryGetValue(key.Slot, out var list))
                bySlot[key.Slot] = list = new List<(CsTeam, int)>();
            list.Add((key.Team, key.DefIndex));
        }

        foreach (var (slot, keys) in bySlot)
        {
            PlayerInfo? playerInfo = infoOverride;
            if (playerInfo == null)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player is not { IsValid: true, IsBot: false })
                    continue;
                playerInfo = PlayerInfo.From(player);
            }

            IReadOnlyDictionary<CsTeam, System.Collections.Concurrent.ConcurrentDictionary<int, WeaponInfo>>? weapons = weaponsOverride;
            if (weapons == null)
            {
                if (!GPlayerWeaponsInfo.TryGetValue(slot, out var live))
                    continue;
                weapons = live;
            }

            var rows = new List<WeaponSynchronization.StatTrakRow>(keys.Count);
            foreach (var (team, defIndex) in keys)
            {
                if (weapons.TryGetValue(team, out var teamWeapons) && teamWeapons.TryGetValue(defIndex, out var info))
                    rows.Add(new WeaponSynchronization.StatTrakRow(team, defIndex, info.StatTrak, info.StatTrakCount));
            }

            // Only now: a slot skipped above keeps its dirty rows for the next flush / disconnect.
            foreach (var (team, defIndex) in keys)
                _statTrakDirty.Remove((slot, team, defIndex));

            if (rows.Count == 0)
                continue;

            var info2 = playerInfo;
            var sync = WeaponSync;
            if (sync != null && !string.IsNullOrEmpty(info2.SteamId))
                DbQueue.EnqueueUnique(info2.SteamId, "stattrak", () => sync.SyncStatTrakToDatabase(info2, rows));
        }
    }

    private void ForgetStatTrakDirty(int slot)
    {
        _statTrakDirty.RemoveWhere(k => k.Slot == slot);
    }

    private static string NormalizeDeathWeapon(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";
        if (name.StartsWith("weapon_", StringComparison.Ordinal))
            name = name[7..];
        return name switch
        {
            "m4a1_silencer_off" => "m4a1_silencer",
            "usp_silencer_off" => "usp_silencer",
            _ => name,
        };
    }

    private static bool IsKnifeName(string name)
        => name.Contains("knife", StringComparison.Ordinal) || name.Contains("bayonet", StringComparison.Ordinal);

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Attacker;
        CCSPlayerController? victim = @event.Userid;

        if (player is null || !player.IsValid || player.IsBot)
            return HookResult.Continue;
        if (victim == null || !victim.IsValid || victim == player)
            return HookResult.Continue;
        if (player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
            return HookResult.Continue;

        var weaponServices = player.PlayerPawn.Value?.WeaponServices;
        if (weaponServices == null)
            return HookResult.Continue;

        // Credit the weapon that killed. Grenades, the world, the bomb etc. resolve to nothing.
        var deathWeapon = NormalizeDeathWeapon(@event.Weapon);
        var knifeKill = IsKnifeName(deathWeapon);
        var weaponDefIndex = -1;
        if (!knifeKill)
        {
            if (!WeaponDefindexByName.TryGetValue("weapon_" + deathWeapon, out weaponDefIndex))
                return HookResult.Continue;
        }

        CBasePlayerWeapon? weapon = null;
        foreach (var handle in weaponServices.MyWeapons)
        {
            var candidate = handle.Value;
            if (candidate == null || !candidate.IsValid)
                continue;

            if (knifeKill)
            {
                // player_death says "knife"/"knife_t"/"bayonet" for every knife model; the entity's
                // definition index is the one that carries the custom knife.
                if (!IsKnifeName(candidate.DesignerName))
                    continue;
                weaponDefIndex = candidate.AttributeManager.Item.ItemDefinitionIndex;
            }
            else if (candidate.AttributeManager.Item.ItemDefinitionIndex != weaponDefIndex)
            {
                continue;
            }

            weapon = candidate;
            break;
        }

        if (weaponDefIndex < 0)
            return HookResult.Continue;
        if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null || !weaponInfo.StatTrak)
            return HookResult.Continue;

        if (weaponInfo.StatTrakCount < int.MaxValue)
            weaponInfo.StatTrakCount += 1;
        MarkStatTrakDirty(player, weaponDefIndex);

        if (weapon == null)
            return HookResult.Continue;

        var item = weapon.AttributeManager.Item;
        var count = ViewAsFloat((uint)weaponInfo.StatTrakCount);
        CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "kill eater", count);
        CAttributeListSetOrAddAttributeValueByName.Invoke(item.NetworkedDynamicAttributes.Handle, "kill eater score type", 0);
        CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "kill eater", count);
        CAttributeListSetOrAddAttributeValueByName.Invoke(item.AttributeList.Handle, "kill eater score type", 0);
        weapon.FallbackStatTrak = weaponInfo.StatTrakCount;
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_nFallbackStatTrak");

        return HookResult.Continue;
    }
}
