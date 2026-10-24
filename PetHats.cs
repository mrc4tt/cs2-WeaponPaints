using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

/*
 * PET HATS (!pethat) — THE PHOTO BOOTH HATS ON THE IN-WORLD PET. Ported from SkinHubgg/WeaponPaints 47538bf;
 * untested on a server there and here.
 *
 * The game has no hat on a pet in a match: the ten "silly hats" exist only in the pet photo booth, where the
 * client attaches the hat model to the pet at a head attachment with a per-stage scale. The plugin does the
 * server-side equivalent, so every client draws it:
 *
 *   - a non-solid prop_dynamic with the hat's model (models/photobooth/silly_hats/*.vmdl, precached with the
 *     pet models), named PetHatEntityName;
 *   - "SetParent" to the chicken, then "SetParentAttachment" to head_attach (eyewear_attach for the two
 *     glasses, the booth's rule), which puts the hat on the attachment with no offset;
 *   - its own scale set to the booth's headwearScale for the stage (chick 1.0, pullet 0.43, hen 0.55); the
 *     pet's own scale sits above it in the hierarchy.
 *
 * The id is wp_player_pets.pet_hat (the SkinHub website writes the same column), exactly as the booth's
 * petPhotobooth.json headwear names it. NULL is no hat; an unknown id draws nothing and is written back as is.
 *
 * Lifecycle: the hat belongs to one chicken. Created with the pet (CreatePet), re-checked every second by
 * PetThink (put back if missing, replaced if the pet, hat or stage changed), killed with the pet
 * (RemovePlayerPet, OnEntityDeletedPets, SweepStrayPets). A hat that fails to stay on three times on the same
 * pet is given up on for that pet with one warning; a new pick starts the count over.
 */
public partial class WeaponPaints
{
    /// <summary>A photo booth hat: its pet_hat id, model and the chicken attachment it sits on.</summary>
    internal sealed record PetHat(string Id, string Model, string Attachment);

    // petPhotobooth.json headwear.items (CS2 1.41.8.5), in the booth's order - the menu order and the numbers
    // !pethat takes. Changes only with a game update.
    internal static readonly PetHat[] PetHats =
    [
        new("helmet", "models/photobooth/silly_hats/helmet.vmdl", "head_attach"),
        new("armor", "models/photobooth/silly_hats/armor_helmet.vmdl", "head_attach"),
        new("alien", "models/photobooth/silly_hats/alien.vmdl", "head_attach"),
        new("banana", "models/photobooth/silly_hats/banana.vmdl", "head_attach"),
        new("glasses", "models/photobooth/silly_hats/glasses.vmdl", "eyewear_attach"),
        new("nose_glasses", "models/photobooth/silly_hats/nose_glasses.vmdl", "eyewear_attach"),
        new("party", "models/photobooth/silly_hats/party.vmdl", "head_attach"),
        new("sprout", "models/photobooth/silly_hats/sprout.vmdl", "head_attach"),
        new("top_hat", "models/photobooth/silly_hats/top_hat.vmdl", "head_attach"),
        new("wizard_hat", "models/photobooth/silly_hats/wizard_hat.vmdl", "head_attach"),
    ];

    /// <summary>targetname of every hat prop this plugin spawns (see PetEntityName).</summary>
    internal const string PetHatEntityName = "weaponpaints_pet_hat";

    private const int PetHatMaxAttempts = 3;

    /// <summary>The booth's growth[].headwearScale: chick 1.0, pullet 0.43, hen 0.55.</summary>
    private static float PetHatScale(int stage) =>
        stage switch
        {
            PetStageChick => 1.0f,
            PetStagePullet => 0.43f,
            _ => 0.55f,
        };

    /// <summary>A hat this plugin spawned: its prop, the chicken it sits on, which hat and for which stage.</summary>
    private sealed record ActivePetHat(uint Handle, uint PetHandle, string HatId, int Stage);

    private static int _petHatCheckLogged;

    /// <summary>The hat for a stored pet_hat id, or null for none and for an id this plugin does not know.</summary>
    internal static PetHat? FindPetHat(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : PetHats.FirstOrDefault(hat => string.Equals(hat.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    private string PetHatLabel(PetHat hat) => Localizer[$"wp_pet_hat_{hat.Id}"];

    #region The hat entity

    /// <summary>
    /// Makes the slot's hat match its stored pet_hat on this chicken: keeps a live hat that is already right,
    /// otherwise removes it and, when there should be one, creates it. Game thread only.
    /// </summary>
    private void SyncPetHat(int slot, CChicken chicken, PetLook look)
    {
        var wanted = Config.Additional.PetHatsEnabled && GPlayersPet.TryGetValue(slot, out var info) ? FindPetHat(info.Hat) : null;
        var petHandle = chicken.EntityHandle.Raw;

        if (_petHats.TryGetValue(slot, out var current))
        {
            if (wanted != null && current.PetHandle == petHandle && current.HatId == wanted.Id && current.Stage == look.Stage && ResolvePetHat(current) != null)
                return;

            RemovePetHat(slot);
        }

        if (wanted == null)
        {
            _petHatAttempts.TryRemove(slot, out _);
            return;
        }

        // A hat that keeps failing or disappearing (a model this server cannot load) is not recreated every
        // second: a few tries per pet and hat, then one warning.
        var attempts = _petHatAttempts.TryGetValue(slot, out var last) && last.PetHandle == petHandle && last.HatId == wanted.Id ? last.Attempts : 0;
        if (attempts >= PetHatMaxAttempts)
        {
            if (attempts == PetHatMaxAttempts)
            {
                _petHatAttempts[slot] = (petHandle, wanted.Id, attempts + 1);
                Logger.LogWarning("The {Hat} pet hat did not stay on the pet in slot {Slot} after {Attempts} tries - not trying again on this pet", wanted.Id, slot, attempts);
            }

            return;
        }

        _petHatAttempts[slot] = (petHandle, wanted.Id, attempts + 1);
        CreatePetHat(slot, chicken, wanted, look.Stage);
    }

    private void CreatePetHat(int slot, CChicken chicken, PetHat hat, int stage)
    {
        var prop = Utilities.CreateEntityByName<CDynamicProp>("prop_dynamic");
        if (prop == null || !prop.IsValid)
        {
            Logger.LogWarning("Could not create a prop_dynamic for the {Hat} pet hat", hat.Id);
            return;
        }

        _spawnedPetEntities[prop.EntityHandle.Raw] = 0;

        try
        {
            var keyValues = new CEntityKeyValues();
            keyValues.SetString("targetname", PetHatEntityName);
            keyValues.SetString("model", hat.Model);
            // Not solid: bullets, grenades and players pass through it.
            keyValues.SetInt("solid", 0);
            if (chicken.AbsOrigin is { } origin)
                keyValues.SetVector("origin", origin.X, origin.Y, origin.Z);
            prop.CreateNonSolid = true;
            prop.DispatchSpawn(keyValues);

            // The model keyvalue should have set it; SetModel when it did not.
            var model = prop.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
            if (!string.Equals(model, hat.Model, StringComparison.OrdinalIgnoreCase))
                prop.SetModel(hat.Model);

            // Parent first, then the attachment: SetParentAttachment puts the hat on the attachment itself.
            prop.AcceptInput("SetParent", chicken, prop, "!activator");
            prop.AcceptInput("SetParentAttachment", chicken, prop, hat.Attachment);

            if (prop.CBodyComponent?.SceneNode is { } node)
            {
                node.Scale = PetHatScale(stage);
                Utilities.SetStateChanged(prop, "CBaseEntity", "m_CBodyComponent");
            }

            var active = new ActivePetHat(prop.EntityHandle.Raw, chicken.EntityHandle.Raw, hat.Id, stage);
            _petHats[slot] = active;
            LogPetHatCheckOnce(active, hat);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not put the {Hat} hat on a pet: {Message}", hat.Id, ex.Message);
            KillPetEntity(prop);
        }
    }

    /// <summary>
    /// Diagnostic for the first server test, once per load: a frame after the first hat, whether it is still
    /// there, its parent and attachment, and the scales. Parent 0 / attachment -1 = the inputs did not take.
    /// </summary>
    private void LogPetHatCheckOnce(ActivePetHat active, PetHat hat)
    {
        if (Interlocked.Exchange(ref _petHatCheckLogged, 1) != 0)
            return;

        Server.NextFrame(() =>
        {
            try
            {
                var prop = ResolvePetHat(active);
                var chicken = new CHandle<CChicken>(active.PetHandle).Value;
                if (prop == null)
                {
                    Logger.LogInformation("Pet hat check: the {Hat} hat was gone a frame after it was created", hat.Id);
                    return;
                }

                var node = prop.CBodyComponent?.SceneNode;
                Logger.LogInformation(
                    "Pet hat check: {Hat} parent 0x{Parent:x8} (pet 0x{Pet:x8}), {Attachment} = attachment index {Index}, scale {Scale} x pet {PetScale} = {AbsScale}",
                    hat.Id,
                    node?.PParent?.Owner?.EntityHandle.Raw ?? 0,
                    active.PetHandle,
                    hat.Attachment,
                    node?.ParentAttachmentOrBone,
                    node?.Scale,
                    chicken?.CBodyComponent?.SceneNode?.AbsScale,
                    node?.AbsScale
                );
            }
            catch (Exception ex)
            {
                Logger.LogInformation("Pet hat check skipped: {Message}", ex.Message);
            }
        });
    }

    private CDynamicProp? ResolvePetHat(ActivePetHat hat)
    {
        var prop = new CHandle<CDynamicProp>(hat.Handle).Value;
        if (prop == null || !prop.IsValid || !prop.DesignerName.StartsWith("prop_dynamic", StringComparison.Ordinal))
            return null;

        return IsMarkedForDelete(prop) ? null : prop;
    }

    /// <summary>Stops tracking the slot's hat and kills it.</summary>
    private void RemovePetHat(int slot)
    {
        if (_petHats.TryRemove(slot, out var hat))
            KillPetEntity(ResolvePetHat(hat));
    }

    #endregion

    #region !pethat

    private void SetupPetHatCommands()
    {
        if (!Config.Additional.PetHatsEnabled)
            return;

        Config.Additional.CommandPetHat.ForEach(c =>
        {
            AddGatedCommand(
                $"css_{c}",
                "Pet hat menu",
                (player, info) =>
                {
                    // Closed between round_end and round_start, like every other WeaponPaints menu.
                    if (!Utility.IsPlayerValid(player) || player == null || !_gBCommandsAllowed)
                        return;
                    OnCommandPetHat(player, info);
                }
            );
        });
    }

    /// <summary>
    /// !pethat opens the menu; !pethat &lt;name|1-10|none&gt; picks straight away. Names are the pet_hat ids
    /// (spaces and dashes read as underscores), numbers the menu order.
    /// </summary>
    private void OnCommandPetHat(CCSPlayerController player, CommandInfo command)
    {
        if (!PlayerMayUsePets(player))
        {
            PrintPet(player, "wp_pet_no_permission");
            return;
        }

        if (!GPlayersPet.ContainsKey(player.Slot))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        // The !pet cooldown: a pick writes the database and replaces a prop.
        if (CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) && DateTime.UtcNow < cooldownEndTime)
        {
            PrintPet(player, "wp_command_cooldown");
            return;
        }

        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

        var argument = command.ArgString.Trim().Trim('"').Trim();
        if (argument.Length == 0)
        {
            OpenPetHatMenu(player);
            return;
        }

        if (!TryParsePetHat(argument, out var hat))
        {
            PrintPet(player, "wp_pet_hat_usage");
            return;
        }

        SetPetHat(player, hat);
    }

    internal static bool TryParsePetHat(string text, out PetHat? hat)
    {
        hat = null;
        var key = text.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

        if (key is "none" or "off" or "remove" or "0")
            return true;

        if (int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            if (number < 1 || number > PetHats.Length)
                return false;
            hat = PetHats[number - 1];
            return true;
        }

        hat = FindPetHat(key);
        return hat != null;
    }

    private void OpenPetHatMenu(CCSPlayerController player)
    {
        var menu = Utility.CreateMenu(Localizer["wp_pet_hat_menu_title"]);
        if (menu == null)
            return;

        menu.AddItem(Localizer["None"], (p, _) => SetPetHat(p, null));

        foreach (var hat in PetHats)
        {
            var chosen = hat;
            menu.AddItem(PetHatLabel(hat), (p, _) => SetPetHat(p, chosen));
        }

        menu.Display(player, 0);
    }

    /// <summary>Picks a hat (or none), saves it and puts it on the pet that is out now, if there is one.</summary>
    private void SetPetHat(CCSPlayerController player, PetHat? hat)
    {
        if (!Utility.IsPlayerValid(player))
            return;

        if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        pet.Hat = hat?.Id;

        // A pick gets fresh tries, also for the same hat again after the plugin gave up on it.
        _petHatAttempts.TryRemove(player.Slot, out _);

        if (hat == null)
            PrintPet(player, "wp_pet_hat_removed");
        else
            PrintPet(player, "wp_pet_hat_set", PetHatLabel(hat));

        if (!PetHatColumnReady)
            Logger.LogWarning("wp_player_pets has no pet_hat column (yet) - {Player}'s hat is shown but not saved", player.PlayerName);

        SavePet(player, pet);

        // Only the hat changes, so the chicken stays; dead owner or not, the pet that is out gets it now.
        if (_activePets.TryGetValue(player.Slot, out var active) && ResolvePet(active) is { } chicken)
            SyncPetHat(player.Slot, chicken, active.Look);
    }

    #endregion
}
