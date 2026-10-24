using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

/*
 * PET EMOTES (!petemote) — WHAT THE IN-WORLD CHICKEN CAN PLAY FROM A PLUGIN. Ported from SkinHubgg/WeaponPaints
 * 47538bf + 90a9daa; untested on a server there and here.
 *
 * The pet's animation is the server's own AnimGraph, driven by the chicken AI's activity (EChickenActivity):
 * Idle 0, Squat 1, Walk 2, Run 3, Glide 4, Land 5, Panic 6, Trick 7, TurnInPlace 8, Feed 9, Sleep 10,
 * Shoulder 11, LowOnFood 12. Which clip plays inside a category is the graph parameter action_variation, which
 * the server always rolls itself and no schema field or input sets - so the plugin picks the CATEGORY and the
 * game picks the variant.
 *
 * How: CChicken.m_desiredActivity (schema, not networked) is written every tick until m_currentActivity shows
 * the emote, then left alone. The AI takes a desired activity at its next clip end (or at once while moving),
 * so an emote starts within a few seconds - up to ~25 s after a feed. The AI writes the field too, hence every
 * tick; after PetEmoteStartSeconds it is dropped. An emote the pet is already playing waits unwritten until the
 * pet does something else (writing it would only repeat the clip), and counts as started when it comes back.
 *
 * Left out: LowOnFood (its loop never completes, so the pet would stop following). Walk/Run/Glide/Land come
 * from moving; Turn needs a graph parameter only the AI sets.
 *
 * Stay mode: on casual/competitive/wingman the game's chicken manager puts owned chickens into "stay" 5 s after
 * round_freeze_end. A new entity is the only way out, one reason every owner spawn gets a new pet.
 */
public partial class WeaponPaints
{
    /// <summary>An emote: its id (chat argument and lang key), EChickenActivity number, and the variants shown.</summary>
    private sealed record PetEmote(string Id, int Activity, string Variants);

    // Menu order. Variants are the numbered clips the game rolls from in a match.
    private static readonly PetEmote[] PetEmotes =
    [
        new("trick", 7, "1-10"),
        new("squat", 1, "1, 3, 4"),
        new("sleep", 10, ""),
        new("feed", 9, ""),
        new("shoulder", 11, "1-3"),
        new("panic", 6, "1-2"),
        new("idle", 0, "1-3"),
    ];

    /// <summary>How long an emote may wait for the AI to take it before it is dropped.</summary>
    private const float PetEmoteStartSeconds = 30f;

    /// <summary>
    /// An emote being written into a pet until it starts. WaitsForChange: the pet was already in that activity
    /// when asked and has not left it since, so it is not written yet.
    /// </summary>
    private sealed record PendingPetEmote(uint PetHandle, PetEmote Emote, float QueuedAt, bool WaitsForChange);

    private void RegisterPetEmotes()
    {
        if (!Config.Additional.PetEmotesEnabled)
            return;

        // One dictionary check per tick while nothing is pending.
        RegisterListener<Listeners.OnTick>(PetEmoteTick);
    }

    private string PetEmoteLabel(PetEmote emote) => Localizer[$"wp_pet_emote_{emote.Id}"];

    private string PetEmoteOption(PetEmote emote) => emote.Variants.Length == 0 ? PetEmoteLabel(emote) : $"{PetEmoteLabel(emote)} ({emote.Variants})";

    /// <summary>Drops a slot's pending emote, and its cooldown unless <paramref name="keepCooldown"/>.</summary>
    private void ForgetPetEmotes(int slot, bool keepCooldown = false)
    {
        _petEmotes.TryRemove(slot, out _);
        if (!keepCooldown)
            _petEmoteCooldowns.TryRemove(slot, out _);
    }

    private void ClearPetEmotes()
    {
        _petEmotes.Clear();
        _petEmoteCooldowns.Clear();
    }

    private void PetEmoteTick()
    {
        if (_petEmotes.IsEmpty)
            return;

        foreach (var (slot, pending) in _petEmotes)
        {
            try
            {
                if (!_activePets.TryGetValue(slot, out var active) || active.Handle != pending.PetHandle || ResolvePet(active) is not { } chicken)
                {
                    _petEmotes.TryRemove(slot, out _);
                    continue;
                }

                var activity = pending.Emote.Activity;
                var waited = Server.CurrentTime - pending.QueuedAt;
                var current = PetCurrentActivity(chicken);

                if (pending.WaitsForChange)
                {
                    if (current == activity)
                    {
                        if (waited > PetEmoteStartSeconds)
                            _petEmotes.TryRemove(slot, out _);
                        continue;
                    }

                    _petEmotes.TryUpdate(slot, pending with { WaitsForChange = false }, pending);
                }
                else if (current == activity)
                {
                    // Started: the AI carries on by itself from here.
                    _petEmotes.TryRemove(slot, out _);
                    continue;
                }

                if (waited > PetEmoteStartSeconds)
                {
                    _petEmotes.TryRemove(slot, out _);
                    continue;
                }

                PetDesiredActivity(chicken) = activity;
            }
            catch (Exception ex)
            {
                _petEmotes.TryRemove(slot, out _);
                Logger.LogWarning("Pet emote failed for slot {Slot}: {Message}", slot, ex.Message);
            }
        }
    }

    #region !petemote

    private void SetupPetEmoteCommands()
    {
        if (!Config.Additional.PetEmotesEnabled)
            return;

        Config.Additional.CommandPetEmote.ForEach(c =>
        {
            AddGatedCommand(
                $"css_{c}",
                "Pet emote menu",
                (player, info) =>
                {
                    // Closed between round_end and round_start, like every other WeaponPaints menu.
                    if (!Utility.IsPlayerValid(player) || player == null || !_gBCommandsAllowed)
                        return;
                    OnCommandPetEmote(player, info);
                }
            );
        });
    }

    /// <summary>!petemote opens the menu, !petemote &lt;trick|squat|sleep|feed|shoulder|panic|idle&gt; plays one.</summary>
    private void OnCommandPetEmote(CCSPlayerController player, CommandInfo command)
    {
        if (!PlayerMayUsePets(player))
        {
            PrintPet(player, "wp_pet_no_permission");
            return;
        }

        var words = command.ArgString.Trim().Trim('"').ToLowerInvariant().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            OpenPetEmoteMenu(player);
            return;
        }

        if (PetEmotes.FirstOrDefault(emote => emote.Id == words[0]) is not { } chosen)
        {
            PrintPet(player, "wp_pet_emote_usage");
            return;
        }

        // "!petemote trick 3": the game rolls the variant, and the player is told so rather than left guessing.
        if (words.Length > 1)
            PrintPet(player, "wp_pet_emote_random");

        PlayPetEmote(player, chosen);
    }

    private void OpenPetEmoteMenu(CCSPlayerController player)
    {
        var menu = Utility.CreateMenu(Localizer["wp_pet_emote_menu_title"]);
        if (menu == null)
            return;

        foreach (var emote in PetEmotes)
        {
            var chosen = emote;
            menu.AddItem(
                PetEmoteOption(emote),
                (p, _) =>
                {
                    if (Utility.IsPlayerValid(p))
                        PlayPetEmote(p, chosen);
                }
            );
        }

        PrintPet(player, "wp_pet_emote_hint");
        menu.Display(player, 0);
    }

    /// <summary>Queues an emote on the player's pet, with the per-player cooldown.</summary>
    private void PlayPetEmote(CCSPlayerController player, PetEmote emote)
    {
        var slot = player.Slot;

        if (!_activePets.TryGetValue(slot, out var active) || ResolvePet(active) is not { } chicken)
        {
            PrintPet(player, "wp_pet_emote_no_pet");
            return;
        }

        if (_petEmoteCooldowns.TryGetValue(slot, out var readyAt) && DateTime.UtcNow < readyAt)
        {
            PrintPet(player, "wp_pet_emote_cooldown", Math.Ceiling((readyAt - DateTime.UtcNow).TotalSeconds).ToString(CultureInfo.InvariantCulture));
            return;
        }

        _petEmoteCooldowns[slot] = DateTime.UtcNow.AddSeconds(Math.Max(0, Config.Additional.PetEmoteCooldownSeconds));

        // Written at once, unless the pet is doing that very activity now (see PetEmoteTick).
        var alreadyIn = PetCurrentActivity(chicken) == emote.Activity;
        _petEmotes[slot] = new PendingPetEmote(active.Handle, emote, Server.CurrentTime, alreadyIn);
        if (!alreadyIn)
            PetDesiredActivity(chicken) = emote.Activity;

        PrintPet(player, "wp_pet_emote_queued", PetEmoteLabel(emote));
    }

    // Read and written as the plain int through the schema, not CChicken.CurrentActivity/DesiredActivity: their
    // enum type was renamed between CounterStrikeSharp versions (ChickenActivity -> EChickenActivity in 1.0.375),
    // which threw TypeLoadException every tick on a server running the other version. The fields are the game's.
    private static ref int PetCurrentActivity(CChicken chicken) => ref Schema.GetRef<int>(chicken.Handle, "CChicken", "m_currentActivity");

    private static ref int PetDesiredActivity(CChicken chicken) => ref Schema.GetRef<int>(chicken.Handle, "CChicken", "m_desiredActivity");

    #endregion
}
