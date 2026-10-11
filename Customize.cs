using System.Collections.Concurrent;
using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Interface;

namespace WeaponPaints;

// Per-weapon customization: wear / seed / StatTrak / name tag / charm on any weapon (not only the
// held one), the customize menu opened after picking a skin, !nametag, !cp and the VIP command gate.
public partial class WeaponPaints
{
    private static readonly (string Key, float Value)[] WearPresets =
    [
        ("wp_wear_fn", 0.01f),
        ("wp_wear_mw", 0.10f),
        ("wp_wear_ft", 0.20f),
        ("wp_wear_ww", 0.40f),
        ("wp_wear_bs", 0.60f),
    ];

    // slot -> pending debounced weapon refresh (game thread only).
    private readonly Dictionary<int, PendingWeaponRefresh> _weaponRefreshPending = new();

    private sealed class PendingWeaponRefresh
    {
        public required ulong SteamId;
        public required HashSet<int> DefIndexes;
        public CounterStrikeSharp.API.Modules.Timers.Timer? Timer;
    }

    internal static bool IsKnifeDefIndex(int weaponDefIndex)
        => WeaponDefindex.TryGetValue(weaponDefIndex, out var className) && IsKnifeName(className);

    // Prints a localized line unless the operator blanked the key.
    private void PrintLocalized(CCSPlayerController player, string key, params object[] args)
    {
        var text = Localizer[key, args].ToString();
        if (!string.IsNullOrEmpty(text) && text != key)
            player.Print(text);
    }

    private string Text(string key, params object[] args)
    {
        var text = Localizer[key, args].ToString();
        return string.IsNullOrEmpty(text) ? key : text;
    }

    // The WeaponInfo the menu shows: the player's current team, T while spectating (writes go to
    // both teams then, see TeamsFor).
    private static WeaponInfo? GetMenuWeaponInfo(CCSPlayerController player, int weaponDefIndex)
    {
        var team = player.TeamNum < 2 ? CsTeam.Terrorist : player.Team;
        if (!GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teams) || !teams.TryGetValue(team, out var weapons))
            return null;
        return weapons.TryGetValue(weaponDefIndex, out var info) ? info : null;
    }

    // Mutates the weapon's WeaponInfo on every team the change applies to. Returns false when the
    // weapon has no entry (or no paint while requirePaint).
    private static bool UpdateWeaponInfos(CCSPlayerController player, int weaponDefIndex, bool requirePaint, Action<WeaponInfo> mutate)
    {
        var any = false;
        var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        foreach (var team in TeamsFor(player))
        {
            var teamWeapons = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
            if (!teamWeapons.TryGetValue(weaponDefIndex, out var info) || (requirePaint && info.Paint <= 0))
                continue;
            mutate(info);
            any = true;
        }
        return any;
    }

    // Input-time clamp (menu, !float): same bounds as RenderWear, so what is stored is what is drawn.
    internal float ClampWearForPaint(int weaponDefIndex, int paint, float wear)
        => RenderWear(weaponDefIndex, paint, wear);

    private string WearName(float wear)
    {
        var key = wear switch
        {
            < 0.07f => "wp_wear_fn",
            < 0.15f => "wp_wear_mw",
            < 0.38f => "wp_wear_ft",
            < 0.45f => "wp_wear_ww",
            _ => "wp_wear_bs",
        };
        return Text(key);
    }

    private static string FormatWear(float wear) => wear.ToString("0.0###", CultureInfo.InvariantCulture);

    // Collapse a burst of changes (menu clicks) into one kill+regive per weapon. Also lets the menu
    // close before the regive, which is what makes stickers/charms composite on the first try
    // (see ApplySticker). Re-resolves the player by slot + SteamID when it fires.
    private void ScheduleWeaponRefresh(CCSPlayerController player, int weaponDefIndex)
    {
        if (!_gBCommandsAllowed || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
            return;

        var slot = player.Slot;
        var steamId = player.SteamID;
        if (!_weaponRefreshPending.TryGetValue(slot, out var pending) || pending.SteamId != steamId)
        {
            pending?.Timer?.Kill();
            pending = new PendingWeaponRefresh { SteamId = steamId, DefIndexes = new HashSet<int>() };
            _weaponRefreshPending[slot] = pending;
        }

        pending.DefIndexes.Add(weaponDefIndex);
        pending.Timer?.Kill();
        pending.Timer = AddTimer(
            0.25f,
            () =>
            {
                if (!_weaponRefreshPending.TryGetValue(slot, out var current) || current != pending)
                    return;
                _weaponRefreshPending.Remove(slot);

                var target = Utilities.GetPlayerFromSlot(slot);
                if (target is not { IsValid: true } || target.SteamID != steamId || !_gBCommandsAllowed)
                    return;
                if ((LifeState_t)target.LifeState != LifeState_t.LIFE_ALIVE)
                    return;

                RunWeaponRefresh(target, pending.DefIndexes);
            },
            TimerFlags.STOP_ON_MAPCHANGE
        );
    }

    private void RunWeaponRefresh(CCSPlayerController player, HashSet<int> defIndexes)
    {
        var active = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        var activeDef = active is { IsValid: true } ? active.AttributeManager.Item.ItemDefinitionIndex : -1;

        var guns = new List<int>();
        var knife = false;
        foreach (var defIndex in defIndexes)
        {
            if (defIndex == C4DefIndex)
                RefreshC4InPlace(player); // the bomb is never killed+regiven
            else if (IsKnifeDefIndex(defIndex))
                knife = true;
            else
                guns.Add(defIndex);
        }

        // Only regive the knife when the changed knife is the one they carry.
        if (knife && HasChangedKnife(player, out var knifeClass) && knifeClass != null
            && WeaponDefindexByName.TryGetValue(knifeClass, out var knifeDef) && defIndexes.Contains(knifeDef))
            RefreshKnife(player, switchToKnife: activeDef == knifeDef);

        if (guns.Count == 1)
            RefreshSingleWeapon(player, guns[0]);
        else if (guns.Count > 1)
            RefreshWeapons(player, excludeKnife: true);
    }

    private void ClearWeaponRefreshTimers()
    {
        foreach (var pending in _weaponRefreshPending.Values)
            pending.Timer?.Kill();
        _weaponRefreshPending.Clear();
        foreach (var timer in _stickerRefreshTimers.Values)
            timer.Kill();
        _stickerRefreshTimers.Clear();
    }

    private int HeldWeaponDefIndex(CCSPlayerController player)
    {
        var weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        return weapon is { IsValid: true } ? weapon.AttributeManager.Item.ItemDefinitionIndex : -1;
    }

    // ---- wear / seed ----------------------------------------------------------------------------

    internal void ApplyWeaponWear(CCSPlayerController player, int weaponDefIndex, float floatValue)
    {
        var info = GetMenuWeaponInfo(player, weaponDefIndex);
        if (info is not { Paint: > 0 })
        {
            PrintLocalized(player, "wp_float_no_skin");
            return;
        }

        floatValue = ClampWearForPaint(weaponDefIndex, info.Paint, floatValue);
        UpdateWeaponInfos(player, weaponDefIndex, true, i => i.Wear = floatValue);

        // Clear any temporary wear override so the next apply uses the new value
        if (_temporaryPlayerWeaponWear.TryGetValue(player.Slot, out var tempWear))
            tempWear.TryRemove(weaponDefIndex, out _);

        RememberPaint(player, weaponDefIndex);
        ScheduleWeaponRefresh(player, weaponDefIndex);
        QueueWeaponRowWrite(player, weaponDefIndex);

        PrintLocalized(player, "wp_float_set", FormatWear(floatValue));
    }

    internal void ApplyWeaponSeed(CCSPlayerController player, int weaponDefIndex, int seedValue)
    {
        seedValue = Math.Clamp(seedValue, 0, 1000);
        if (!UpdateWeaponInfos(player, weaponDefIndex, true, i => i.Seed = seedValue))
        {
            PrintLocalized(player, "wp_seed_no_skin");
            return;
        }

        RememberPaint(player, weaponDefIndex);
        ScheduleWeaponRefresh(player, weaponDefIndex);
        QueueWeaponRowWrite(player, weaponDefIndex);

        PrintLocalized(player, "wp_seed_set", seedValue);
    }

    internal void ToggleWeaponStatTrak(CCSPlayerController player, int weaponDefIndex)
    {
        var info = GetMenuWeaponInfo(player, weaponDefIndex);
        if (info is not { Paint: > 0 })
            return;

        var enabled = !info.StatTrak;
        UpdateWeaponInfos(player, weaponDefIndex, true, i => i.StatTrak = enabled);
        ScheduleWeaponRefresh(player, weaponDefIndex);
        QueueWeaponRowWrite(player, weaponDefIndex);

        PrintLocalized(player, "wp_stattrak_action");
    }

    // ---- name tag -------------------------------------------------------------------------------

    internal string SanitizeNametag(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var chars = raw.Trim().Trim('"').Where(c => !char.IsControl(c)).ToArray();
        var tag = new string(chars).Trim();

        var max = Math.Clamp(Config.Additional.NametagMaxLength, 1, 128);
        if (tag.Length > max)
        {
            var cut = max;
            if (char.IsHighSurrogate(tag[cut - 1]))
                cut--;
            tag = tag[..cut].TrimEnd();
        }
        return tag;
    }

    internal void ApplyWeaponNametag(CCSPlayerController player, int weaponDefIndex, string? rawTag)
    {
        var tag = SanitizeNametag(rawTag);
        if (!UpdateWeaponInfos(player, weaponDefIndex, true, i => i.Nametag = tag))
        {
            PrintLocalized(player, "wp_nametag_no_skin");
            return;
        }

        ScheduleWeaponRefresh(player, weaponDefIndex);
        QueueWeaponRowWrite(player, weaponDefIndex);

        if (tag.Length == 0)
            PrintLocalized(player, "wp_nametag_cleared");
        else
            PrintLocalized(player, "wp_nametag_set", tag);
    }

    private void BeginNametagPrompt(CCSPlayerController player, int weaponDefIndex)
    {
        GPlayersPendingSeedWearInput[player.Slot] = new PendingChatInput(PendingSeedWearKind.Nametag, weaponDefIndex);
        PrintLocalized(player, "wp_nametag_prompt", Config.Additional.NametagMaxLength);
    }

    private void OnCommandNametag(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !_gBCommandsAllowed)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        var weaponDefIndex = HeldWeaponDefIndex(player);
        if (weaponDefIndex < 0)
        {
            PrintLocalized(player, "wp_nametag_no_weapon");
            return;
        }

        if (!HasChangedPaint(player, weaponDefIndex, out _))
        {
            PrintLocalized(player, "wp_nametag_no_skin");
            return;
        }

        if (command.ArgCount < 2)
        {
            BeginNametagPrompt(player, weaponDefIndex);
            return;
        }

        var raw = command.ArgString.Trim();
        ApplyWeaponNametag(player, weaponDefIndex, raw == "-" ? null : raw);
    }

    // ---- charm ----------------------------------------------------------------------------------

    internal void ApplyWeaponCharm(CCSPlayerController player, int weaponDefIndex, uint charmId)
    {
        // Charm without a skin works like a sticker without a skin: a paint-0 entry carries it.
        var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        foreach (var team in TeamsFor(player))
        {
            var info = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>()).GetOrAdd(weaponDefIndex, _ => new WeaponInfo());
            info.KeyChain = charmId == 0 ? new KeyChainInfo() : new KeyChainInfo { Id = charmId };
        }

        ScheduleStickerRefresh(player, weaponDefIndex);
        QueueWeaponRowWrite(player, weaponDefIndex);

        if (charmId == 0)
            PrintLocalized(player, "wp_charm_removed");
        else
            PrintLocalized(player, "wp_charm_set", KeychainsById.TryGetValue(charmId, out var charm) ? charm["name"]?.ToString() ?? charmId.ToString() : charmId.ToString());
    }

    private void OpenCharmMenu(CCSPlayerController player, int weaponDefIndex, IMenu? back)
    {
        var menu = Utility.CreateMenu(Text("wp_charm_menu_title"));
        menu.PrevMenu = back;

        menu.AddItem(Text("None"), (p, _) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;
            ApplyWeaponCharm(p, weaponDefIndex, 0);
        });

        foreach (var charm in KeychainsList)
        {
            var name = charm["name"]?.ToString();
            if (string.IsNullOrEmpty(name) || !uint.TryParse(charm["id"]?.ToString(), out var id))
                continue;
            menu.AddItem(name, (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                ApplyWeaponCharm(p, weaponDefIndex, id);
            });
        }

        menu.Display(player, 0);
    }

    // ---- customize menu -------------------------------------------------------------------------

    private string PaintDisplayName(int weaponDefIndex, int paint)
    {
        if (WeaponDefindex.TryGetValue(weaponDefIndex, out var className) && SkinsByWeaponName.TryGetValue(className, out var skins))
        {
            foreach (var skin in skins)
            {
                if (((int?)skin["paint"] ?? -1) == paint)
                    return skin["paint_name"]?.ToString() ?? className;
            }
        }
        return WeaponDefindex.TryGetValue(weaponDefIndex, out var name) && WeaponList.TryGetValue(name, out var display) ? display : weaponDefIndex.ToString();
    }

    // `back` is what the menu's Back button returns to (the skin list it was opened from).
    internal void OpenWeaponCustomizeMenu(CCSPlayerController player, int weaponDefIndex, IMenu? back)
    {
        var info = GetMenuWeaponInfo(player, weaponDefIndex);
        var paint = info?.Paint ?? 0;

        var menu = Utility.CreateMenu(Text("wp_customize_title", PaintDisplayName(weaponDefIndex, paint)));
        menu.PrevMenu = back;

        if (info != null && paint > 0)
        {
            var shownWear = RenderWear(weaponDefIndex, paint, info.Wear);
            menu.AddItem(Text("wp_customize_wear", FormatWear(shownWear), WearName(shownWear)), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    OpenWearMenu(p, weaponDefIndex, menu, back);
            });
            menu.AddItem(Text("wp_customize_seed", info.Seed), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    OpenSeedMenu(p, weaponDefIndex, menu, back);
            });
            menu.AddItem(Text(info.StatTrak ? "wp_customize_stattrak_on" : "wp_customize_stattrak_off"), (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                ToggleWeaponStatTrak(p, weaponDefIndex);
                OpenWeaponCustomizeMenu(p, weaponDefIndex, back);
            });
            menu.AddItem(Text("wp_customize_nametag", string.IsNullOrEmpty(info.Nametag) ? Text("None") : info.Nametag), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    BeginNametagPrompt(p, weaponDefIndex);
            });
            if (!string.IsNullOrEmpty(info.Nametag))
            {
                menu.AddItem(Text("wp_customize_nametag_remove"), (p, _) =>
                {
                    if (!Utility.IsPlayerValid(p))
                        return;
                    ApplyWeaponNametag(p, weaponDefIndex, null);
                    OpenWeaponCustomizeMenu(p, weaponDefIndex, back);
                });
            }
        }

        // Knives take no stickers or charms.
        if (!IsKnifeDefIndex(weaponDefIndex))
        {
            menu.AddItem(Text("wp_customize_stickers"), (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                // A leftover `!sticker <query>` filter belongs to that command, not to this menu.
                _stickerCommandFilters.TryRemove(p.Slot, out List<Newtonsoft.Json.Linq.JObject>? _);
                OpenStickerSlotMenu(p, weaponDefIndex, menu);
            });

            if (KeychainsList.Count > 0)
            {
                var charmId = info?.KeyChain?.Id ?? 0;
                var charmName = charmId != 0 && KeychainsById.TryGetValue(charmId, out var charm) ? charm["name"]?.ToString() ?? charmId.ToString() : Text("None");
                menu.AddItem(Text("wp_customize_charm", charmName), (p, _) =>
                {
                    if (Utility.IsPlayerValid(p))
                        OpenCharmMenu(p, weaponDefIndex, menu);
                });
            }
        }

        menu.Display(player, 0);
    }

    private void OpenWearMenu(CCSPlayerController player, int weaponDefIndex, IMenu customizeMenu, IMenu? back)
    {
        var info = GetMenuWeaponInfo(player, weaponDefIndex);
        if (info is not { Paint: > 0 })
            return;

        var menu = Utility.CreateMenu(Text("wp_wear_menu_title"));
        menu.PrevMenu = customizeMenu;

        foreach (var (key, preset) in WearPresets)
        {
            var value = ClampWearForPaint(weaponDefIndex, info.Paint, preset);
            menu.AddItem($"{Text(key)} ({FormatWear(value)})", (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                ApplyWeaponWear(p, weaponDefIndex, value);
                OpenWeaponCustomizeMenu(p, weaponDefIndex, back);
            });
        }

        menu.AddItem(Text("wp_customize_custom"), (p, _) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;
            GPlayersPendingSeedWearInput[p.Slot] = new PendingChatInput(PendingSeedWearKind.Wear, weaponDefIndex);
            PrintLocalized(p, "wp_float_prompt");
        });

        menu.Display(player, 0);
    }

    private void OpenSeedMenu(CCSPlayerController player, int weaponDefIndex, IMenu customizeMenu, IMenu? back)
    {
        var menu = Utility.CreateMenu(Text("wp_seed_menu_title"));
        menu.PrevMenu = customizeMenu;

        menu.AddItem(Text("wp_customize_random_seed"), (p, _) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;
            ApplyWeaponSeed(p, weaponDefIndex, Random.Shared.Next(0, 1001));
            OpenWeaponCustomizeMenu(p, weaponDefIndex, back);
        });
        menu.AddItem(Text("wp_customize_custom"), (p, _) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;
            GPlayersPendingSeedWearInput[p.Slot] = new PendingChatInput(PendingSeedWearKind.Seed, weaponDefIndex);
            PrintLocalized(p, "wp_seed_prompt");
        });

        menu.Display(player, 0);
    }

    // ---- !cp ------------------------------------------------------------------------------------

    private void OnCommandCopySkin(CCSPlayerController? player, CommandInfo command)
    {
        if (!Config.Additional.SkinEnabled || !Config.Additional.CopySkinEnabled)
            return;
        if (!Utility.IsPlayerValid(player) || player == null)
            return;

        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        // Spectating (dead or spectator team): the observer pawn's target is the watched pawn.
        CCSPlayerPawn? targetPawn = null;
        try
        {
            var target = player.ObserverPawn.Value?.ObserverServices?.ObserverTarget.Value;
            if (target is { IsValid: true } && target.DesignerName == "player")
                targetPawn = target.As<CCSPlayerPawn>();
        }
        catch (Exception)
        {
            targetPawn = null;
        }

        var targetController = targetPawn?.OriginalController.Value;
        if (targetPawn is not { IsValid: true } || targetController is not { IsValid: true } || targetController.Slot == player.Slot)
        {
            PrintLocalized(player, "wp_copy_no_target");
            return;
        }

        var weapon = targetPawn.WeaponServices?.ActiveWeapon.Value;
        if (weapon is not { IsValid: true })
        {
            PrintLocalized(player, "wp_copy_nothing");
            return;
        }

        var weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;
        if (!WeaponDefindex.TryGetValue(weaponDefIndex, out var className))
        {
            PrintLocalized(player, "wp_copy_nothing");
            return;
        }

        // Prefer the target's plugin loadout (has stickers/charm); otherwise read what the
        // weapon entity shows (a Steam-inventory skin: paint/seed/wear/name tag only).
        WeaponInfo source;
        if (!targetController.IsBot && TryGetWeaponInfo(targetController, weaponDefIndex, out var targetInfo) && targetInfo is { Paint: > 0 })
        {
            source = targetInfo.Clone();
        }
        else
        {
            if (weapon.FallbackPaintKit <= 0)
            {
                PrintLocalized(player, "wp_copy_nothing");
                return;
            }
            source = new WeaponInfo
            {
                Paint = weapon.FallbackPaintKit,
                Seed = weapon.FallbackSeed,
                Wear = weapon.FallbackWear,
                Nametag = weapon.AttributeManager.Item.CustomName ?? "",
            };
        }

        // Kills are the target's, not the copier's.
        source.StatTrakCount = 0;

        var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        var teams = TeamsFor(player);
        foreach (var team in teams)
            playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>())[weaponDefIndex] = source.Clone();

        // A knife skin is only visible on that knife model, so select the model too.
        if (IsKnifeDefIndex(weaponDefIndex) && Config.Additional.KnifeEnabled)
        {
            var knives = GPlayersKnife.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, string>());
            foreach (var team in teams)
                knives[team] = className;
            var playerInfo = PlayerInfo.From(player);
            QueueDb(playerInfo, "knife", sync => sync.SyncKnifeToDatabase(playerInfo, className, teams));
        }

        RememberPaint(player, weaponDefIndex, teams);
        QueueWeaponRowWrite(player, weaponDefIndex, teams);
        ScheduleWeaponRefresh(player, weaponDefIndex);

        PrintLocalized(player, "wp_copy_done", PaintDisplayName(weaponDefIndex, source.Paint), targetController.PlayerName);
    }

    // ---- VIP gate -------------------------------------------------------------------------------

    private bool HasVipAccess(CCSPlayerController player, string commandName)
    {
        var gated = Config.Additional.VipCommands;
        if (gated.Count == 0 || string.IsNullOrEmpty(Config.Additional.VipPermission))
            return true;

        var bare = commandName.StartsWith("css_", StringComparison.OrdinalIgnoreCase) ? commandName[4..] : commandName;
        var isGated = false;
        foreach (var entry in gated)
        {
            var name = entry.Trim().TrimStart('!', '/');
            if (name.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
                name = name[4..];
            if (string.Equals(name, bare, StringComparison.OrdinalIgnoreCase))
            {
                isGated = true;
                break;
            }
        }

        return !isGated || AdminManager.PlayerHasPermissions(player, Config.Additional.VipPermission);
    }

    // AddCommand with the VipCommands gate in front. Console (null player) is never gated.
    private void AddGatedCommand(string name, string description, CommandInfo.CommandCallback handler)
    {
        AddCommand(name, description, (player, info) =>
        {
            if (player != null && player.IsValid && !HasVipAccess(player, name))
            {
                PrintLocalized(player, "wp_vip_only");
                return;
            }
            handler(player, info);
        });
    }
}
