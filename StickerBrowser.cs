using System.Collections.Concurrent;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Interface;
using Newtonsoft.Json.Linq;

namespace WeaponPaints;

// Grouping of the sticker catalog for the !sticker browser. Built off the game thread together with
// the catalog (Utility.LoadStickersFromFile / the online catalog merge) and swapped in whole, so a
// menu never sees a half-built index.
//
// Groups come from the online catalog's metadata when present ("event", "kind", "effect",
// "capsule" fields added by Catalog.cs). Without it — bundled files only — the tournament is parsed
// from the name ("Sticker | s1mple (Gold) | Paris 2023" -> "Paris 2023") and there are no capsules.
internal sealed class StickerBrowseIndex
{
    internal static readonly StickerBrowseIndex Empty = new();

    internal IReadOnlyList<string> Events { get; private init; } = [];
    internal IReadOnlyDictionary<string, List<JObject>> ByEvent { get; private init; } = new Dictionary<string, List<JObject>>();
    internal IReadOnlyList<string> Capsules { get; private init; } = [];
    internal IReadOnlyDictionary<string, List<JObject>> ByCapsule { get; private init; } = new Dictionary<string, List<JObject>>();
    internal IReadOnlyList<JObject> Ungrouped { get; private init; } = [];
    internal JObject[] Items { get; private init; } = [];
    internal string[] LowerNames { get; private init; } = [];

    internal static StickerBrowseIndex Build(IReadOnlyList<JObject> stickers)
    {
        var byEvent = new Dictionary<string, List<JObject>>();
        var eventOrder = new Dictionary<string, long>();
        var byCapsule = new Dictionary<string, List<JObject>>();
        var capsuleOrder = new Dictionary<string, long>();
        var ungrouped = new List<JObject>();
        var items = new List<JObject>(stickers.Count);
        var lowerNames = new List<string>(stickers.Count);

        foreach (var sticker in stickers)
        {
            var name = sticker["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            long.TryParse(sticker["id"]?.ToString(), out var id);

            items.Add(sticker);
            lowerNames.Add(name.ToLowerInvariant());

            // Online metadata is authoritative when present; only guess from the name without it.
            var eventName = sticker["meta"] != null ? sticker["event"]?.ToString() : ParseEvent(name);
            var capsule = sticker["capsule"]?.ToString();

            if (!string.IsNullOrEmpty(eventName))
                Add(byEvent, eventOrder, eventName, sticker, id);
            if (!string.IsNullOrEmpty(capsule))
                Add(byCapsule, capsuleOrder, capsule, sticker, id);
            if (string.IsNullOrEmpty(eventName) && string.IsNullOrEmpty(capsule))
                ungrouped.Add(sticker);
        }

        return new StickerBrowseIndex
        {
            // Newest first: a group's newest sticker id dates it.
            Events = eventOrder.OrderByDescending(e => e.Value).Select(e => e.Key).ToList(),
            ByEvent = byEvent,
            Capsules = capsuleOrder.OrderByDescending(c => c.Value).Select(c => c.Key).ToList(),
            ByCapsule = byCapsule,
            Ungrouped = ungrouped,
            Items = items.ToArray(),
            LowerNames = lowerNames.ToArray(),
        };
    }

    private static void Add(Dictionary<string, List<JObject>> groups, Dictionary<string, long> order, string key, JObject sticker, long id)
    {
        if (!groups.TryGetValue(key, out var list))
            groups[key] = list = new List<JObject>();
        list.Add(sticker);
        if (!order.TryGetValue(key, out var newest) || id > newest)
            order[key] = id;
    }

    // "Sticker | player (Gold) | Paris 2023" -> "Paris 2023". Needs three parts and a year-ish digit.
    private static string? ParseEvent(string name)
    {
        var parts = name.Split(" | ");
        if (parts.Length < 3)
            return null;
        var last = parts[^1].Trim();
        return last.Any(char.IsDigit) ? last : null;
    }
}

public partial class WeaponPaints
{
    private const int StickerSearchLimit = 200;

    // slot -> pending sticker/charm refresh (game thread only).
    private readonly Dictionary<int, CounterStrikeSharp.API.Modules.Timers.Timer> _stickerRefreshTimers = new();

    // Per-slot snapshot of the sticker list a player should see in their next sticker
    // sub-menu. Set when !sticker <query> is invoked and read once the slot sub-menu picks a
    // slot. Cleared on player disconnect via OnPlayerDisconnect.
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

        var weaponDefIndex = HeldWeaponDefIndex(player);
        if (weaponDefIndex < 0)
        {
            PrintLocalized(player, "wp_sticker_no_weapon");
            return;
        }

        // No skin requirement: stickers can be applied to a weapon with no custom paint.

        var query = command.ArgCount >= 2 ? command.ArgString.Trim().Trim('"') : "";
        if (string.IsNullOrEmpty(query))
        {
            _stickerCommandFilters.TryRemove(player.Slot, out _);
            OpenStickerSlotMenu(player, weaponDefIndex);
            return;
        }

        List<JObject> filtered;
        if (uint.TryParse(query, out var idQuery) && StickersById.TryGetValue(idQuery, out var hit))
            filtered = new List<JObject> { hit };
        else
            filtered = SearchStickers(query, int.MaxValue, out _);

        if (filtered.Count == 0)
        {
            PrintLocalized(player, "wp_sticker_no_match", query);
            return;
        }

        _stickerCommandFilters[player.Slot] = filtered;
        OpenStickerSlotMenu(player, weaponDefIndex);
    }

    private string StickerName(uint id)
        => StickersById.TryGetValue(id, out var sticker) ? sticker["name"]?.ToString() ?? id.ToString() : id.ToString();

    internal void OpenStickerSlotMenu(CCSPlayerController player, int weaponDefIndex, IMenu? back = null)
    {
        var menu = Utility.CreateMenu(Text("wp_sticker_slot_title"));
        menu.PrevMenu = back;

        var info = GetMenuWeaponInfo(player, weaponDefIndex);
        for (var slot = 0; slot < 5; slot++)
        {
            var capturedSlot = slot;
            var current = info != null && slot < info.Stickers.Count ? info.Stickers[slot].Id : 0;
            var label = current != 0
                ? Text("wp_sticker_slot_label_with", slot, StickerName(current))
                : Text("wp_sticker_slot_label", slot);

            menu.AddItem(label, (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    OpenStickerSourceMenu(p, weaponDefIndex, capturedSlot, menu);
            });
        }

        menu.AddItem(Text("wp_sticker_all_slots"), (p, _) =>
        {
            if (Utility.IsPlayerValid(p))
                OpenStickerSourceMenu(p, weaponDefIndex, -1, menu);
        });

        menu.AddItem(Text("wp_sticker_remove_all"), (p, _) =>
        {
            if (Utility.IsPlayerValid(p))
                ClearAllStickers(p, weaponDefIndex);
        });

        menu.Display(player, 0);
    }

    // slot -1 = put the picked sticker on all five slots.
    private void OpenStickerSourceMenu(CCSPlayerController player, int weaponDefIndex, int slot, IMenu back)
    {
        // `!sticker <query>`: go straight to the matches.
        if (_stickerCommandFilters.TryGetValue(player.Slot, out var filtered))
        {
            OpenStickerListMenu(player, weaponDefIndex, slot, SlotTitle(slot), filtered, back, includeClear: slot >= 0);
            return;
        }

        var index = StickerBrowse;
        var menu = Utility.CreateMenu(SlotTitle(slot));
        menu.PrevMenu = back;

        menu.AddItem(Text("wp_sticker_search"), (p, _) =>
        {
            if (!Utility.IsPlayerValid(p))
                return;
            GPlayersPendingSeedWearInput[p.Slot] = new PendingChatInput(PendingSeedWearKind.StickerSearch, weaponDefIndex, slot);
            PrintLocalized(p, "wp_sticker_search_prompt");
        });

        if (index.Events.Count > 0)
        {
            menu.AddItem(Text("wp_sticker_tournaments"), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    OpenStickerGroupMenu(p, weaponDefIndex, slot, Text("wp_sticker_tournaments"), index.Events, index.ByEvent, menu, splitKinds: true);
            });
        }

        if (index.Capsules.Count > 0)
        {
            menu.AddItem(Text("wp_sticker_capsules"), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    OpenStickerGroupMenu(p, weaponDefIndex, slot, Text("wp_sticker_capsules"), index.Capsules, index.ByCapsule, menu, splitKinds: false);
            });
        }

        if (index.Ungrouped.Count > 0)
        {
            menu.AddItem(Text("wp_sticker_other"), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    OpenStickerListMenu(p, weaponDefIndex, slot, Text("wp_sticker_other"), index.Ungrouped, menu, includeClear: false);
            });
        }

        // No flat "all stickers" list: ~11k menu items + closures built per open on the game thread.
        // Search and the groups cover it.

        if (slot >= 0)
        {
            menu.AddItem(Text("wp_sticker_clear_slot"), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    ApplyStickerToSlots(p, weaponDefIndex, [slot], null);
            });
        }

        menu.Display(player, 0);
    }

    private string SlotTitle(int slot)
        => slot < 0 ? Text("wp_sticker_browse_title", Text("wp_sticker_all_slots")) : Text("wp_sticker_menu_title", slot);

    private void OpenStickerGroupMenu(CCSPlayerController player, int weaponDefIndex, int slot, string title,
        IReadOnlyList<string> groups, IReadOnlyDictionary<string, List<JObject>> byGroup, IMenu back, bool splitKinds)
    {
        var menu = Utility.CreateMenu(title);
        menu.PrevMenu = back;

        foreach (var group in groups)
        {
            if (!byGroup.TryGetValue(group, out var stickers))
                continue;
            menu.AddItem($"{group} ({stickers.Count})", (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                if (splitKinds)
                    OpenStickerSplitMenu(p, weaponDefIndex, slot, group, stickers, menu, "kind");
                else
                    OpenStickerListMenu(p, weaponDefIndex, slot, group, stickers, menu, includeClear: false);
            });
        }

        menu.Display(player, 0);
    }

    // Splits a big group (a Major has ~700 stickers) by "kind" (players / teams / event), then by
    // "effect" (paper / holo / foil / gold ...). A level with a single value is skipped.
    private void OpenStickerSplitMenu(CCSPlayerController player, int weaponDefIndex, int slot, string title,
        List<JObject> stickers, IMenu back, string field)
    {
        var values = new List<string>();
        var groups = new Dictionary<string, List<JObject>>();
        foreach (var sticker in stickers)
        {
            var value = sticker[field]?.ToString() ?? "";
            if (!groups.TryGetValue(value, out var list))
            {
                groups[value] = list = new List<JObject>();
                values.Add(value);
            }
            list.Add(sticker);
        }

        if (groups.Count <= 1 || values.Contains(""))
        {
            if (field == "kind")
                OpenStickerSplitMenu(player, weaponDefIndex, slot, title, stickers, back, "effect");
            else
                OpenStickerListMenu(player, weaponDefIndex, slot, title, stickers, back, includeClear: false);
            return;
        }

        var menu = Utility.CreateMenu(title);
        menu.PrevMenu = back;
        foreach (var value in values)
        {
            var list = groups[value];
            var label = field == "kind" ? StickerKindLabel(value) : value;
            menu.AddItem($"{label} ({list.Count})", (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                if (field == "kind")
                    OpenStickerSplitMenu(p, weaponDefIndex, slot, $"{title} - {label}", list, menu, "effect");
                else
                    OpenStickerListMenu(p, weaponDefIndex, slot, $"{title} - {label}", list, menu, includeClear: false);
            });
        }
        menu.Display(player, 0);
    }

    private string StickerKindLabel(string kind) => kind switch
    {
        "Autograph" => Text("wp_sticker_kind_autograph"),
        "Team" => Text("wp_sticker_kind_team"),
        "Event" => Text("wp_sticker_kind_event"),
        _ => Text("wp_sticker_kind_other"),
    };

    private void OpenStickerListMenu(CCSPlayerController player, int weaponDefIndex, int slot, string title,
        IReadOnlyList<JObject> list, IMenu? back, bool includeClear)
    {
        var menu = Utility.CreateMenu(title);
        menu.PrevMenu = back;

        int[] slots = slot < 0 ? [0, 1, 2, 3, 4] : [slot];

        if (includeClear)
        {
            menu.AddItem(Text("wp_sticker_clear_slot"), (p, _) =>
            {
                if (Utility.IsPlayerValid(p))
                    ApplyStickerToSlots(p, weaponDefIndex, slots, null);
            });
        }

        foreach (var s in list)
        {
            var name = s["name"]?.ToString();
            if (string.IsNullOrEmpty(name))
                continue;
            if (!uint.TryParse(s["id"]?.ToString(), out var id))
                continue;

            menu.AddItem(name, (p, _) =>
            {
                if (!Utility.IsPlayerValid(p))
                    return;
                ApplyStickerToSlots(p, weaponDefIndex, slots, new StickerInfo { Id = id, Scale = 1 });
            });
        }

        menu.Display(player, 0);
    }

    // All query tokens must appear in the name. ~11k precomputed lower-case names: well under a
    // millisecond, fine on the game thread.
    private static List<JObject> SearchStickers(string query, int limit, out int total)
    {
        var index = StickerBrowse;
        var tokens = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<JObject>();
        total = 0;
        if (tokens.Length == 0)
            return results;

        var names = index.LowerNames;
        for (var i = 0; i < names.Length; i++)
        {
            var match = true;
            foreach (var token in tokens)
            {
                if (!names[i].Contains(token, StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }
            if (!match)
                continue;
            total++;
            if (results.Count < limit)
                results.Add(index.Items[i]);
        }
        return results;
    }

    private void OpenStickerSearchResults(CCSPlayerController player, int weaponDefIndex, int slot, string query)
    {
        var results = SearchStickers(query, StickerSearchLimit, out var total);
        if (results.Count == 0)
        {
            PrintLocalized(player, "wp_sticker_no_match", query);
            return;
        }
        if (total > results.Count)
            PrintLocalized(player, "wp_sticker_search_more", results.Count, total);

        OpenStickerListMenu(player, weaponDefIndex, slot, $"{SlotTitle(slot)} - \"{query}\"", results, null, includeClear: false);
    }

    // Writes `sticker` (null = clear) into each slot of the weapon on every team the change applies
    // to, refreshes once and persists the whole row once.
    private void ApplyStickerToSlots(CCSPlayerController player, int weaponDefIndex, int[] slots, StickerInfo? sticker)
    {
        // Sticker without skin: reuse the existing WeaponInfo if there is one (paint or not),
        // otherwise create a paint-0 entry so the sticker can attach to the default weapon.
        var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());
        var teams = TeamsFor(player);
        foreach (var team in teams)
        {
            var weaponInfo = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>()).GetOrAdd(weaponDefIndex, _ => new WeaponInfo());

            // Slot-indexed list: pad to 5 so any index can be written.
            while (weaponInfo.Stickers.Count < 5)
                weaponInfo.Stickers.Add(new StickerInfo());

            foreach (var slot in slots)
            {
                if (slot is >= 0 and <= 4)
                    weaponInfo.Stickers[slot] = sticker?.Clone() ?? new StickerInfo();
            }
        }

        ScheduleStickerRefresh(player, weaponDefIndex);
        QueueWeaponRowWrite(player, weaponDefIndex, teams);

        if (sticker != null)
        {
            if (slots.Length > 1)
                PrintLocalized(player, "wp_sticker_applied_all", StickerName(sticker.Id));
            else
                PrintLocalized(player, "wp_sticker_applied", StickerName(sticker.Id), slots[0]);
        }
        else if (slots.Length == 1)
        {
            PrintLocalized(player, "wp_sticker_cleared", slots[0]);
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
        ApplyStickerToSlots(player, weaponDefIndex, [0, 1, 2, 3, 4], null);
        PrintLocalized(player, "wp_sticker_cleared_all");
    }

    // Defer the refresh so the sticker menu has closed and the weapon is redrawn before the
    // kill+regive. Refreshing from inside the menu callback (menu still open) applied the
    // attributes but the weapon wasn't composited until a later !wp — typed in chat with the
    // menu already closed. The short delay makes the live apply behave like that !wp. Guns use the
    // full RefreshWeapons on purpose (a split single-weapon regive raced and dropped stickers).
    private void ScheduleStickerRefresh(CCSPlayerController player, int weaponDefIndex)
    {
        if (!_gBCommandsAllowed || (LifeState_t)player.LifeState != LifeState_t.LIFE_ALIVE)
            return;

        var slot = player.Slot;
        var steamId = player.SteamID;

        // One full regive per burst of clicks: restart the timer instead of stacking another.
        if (_stickerRefreshTimers.Remove(slot, out var previous))
            previous.Kill();

        _stickerRefreshTimers[slot] = AddTimer(0.25f, () =>
        {
            _stickerRefreshTimers.Remove(slot);
            var target = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(slot);
            if (target is not { IsValid: true } || target.SteamID != steamId || !Utility.IsPlayerValid(target))
                return;
            if ((LifeState_t)target.LifeState != LifeState_t.LIFE_ALIVE)
                return;

            var active = target.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            var isKnife = active != null && active.IsValid && active.AttributeManager.Item.ItemDefinitionIndex == weaponDefIndex && IsKnifeName(active.DesignerName);
            if (isKnife)
                RefreshKnife(target);
            else if (weaponDefIndex == C4DefIndex)
                RefreshC4InPlace(target); // the bomb is never killed+regiven
            else
                RefreshWeapons(target);
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
}
