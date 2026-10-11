using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
// Only Vector3: all of System.Numerics would make CounterStrikeSharp's Vector ambiguous.
using Vector3 = System.Numerics.Vector3;

namespace WeaponPaints;

/*
 * CHICKEN PETS (CS2 1.41.8.2) — THE PLUGIN SPAWNS ITS OWN CHICKEN.
 *
 * Ported from SkinHubgg/WeaponPaints cc57c51 (feature) + ea290cc (colour fix). NONE OF THIS HAS BEEN SEEN
 * RUNNING ON A SERVER — upstream says the same of its own code; the notes below are theirs, read off the
 * 1.41.8.2 schema and the client.dll / server.dll 2000917-2000918 code.
 *
 * In game a pet is a `chicken` entity (CChicken, a prop_dynamic) carrying an econ item in m_AttributeManager —
 * item 4681 "pet" with the attributes "pet id", "pet seed" and "upgrade level" — plus m_leader (the pawn it
 * follows) and m_owner (the controller; the overhead label reads "{owner}'s {name}"). There is no pet field on
 * the pawn, controller or InventoryServices; the pet loadout slot is 57. Rather than faking loadout slot 57 (a
 * GetItemInLoadout hook — see docs/loadout-gamedata-notes.md), the plugin creates the chicken itself and fills
 * in the same item the game would:
 *
 *   - the COLOUR is a material group the server sets, never the seed. A chicken with no pending skin rolls a
 *     random group on every spawn, so the plugin sends the "Skin" input right after spawning — on every spawn,
 *     "0" (the default group) when the player has no colour. pet_variant is that style index;
 *   - the SEED and STAGE are read by the client from the networked item (only when m_bInitialized is set), once,
 *     when the chicken activates: the seed rolls body shape (pullet/hen) and colour jitter, the stage the shape
 *     preset. Seed 0 skips the jitter (never picked), stage > 3 reads garbage (clamped to 0..3);
 *   - the server reads "upgrade level" once, in Spawn, for the pet's scale, so the item is filled BEFORE
 *     DispatchSpawn and any change of look respawns the pet. Spawn always loads chicken.vmdl and clears m_leader,
 *     so the model and leader are set after it;
 *   - m_owner and m_szCustomNameOverride2/3 are written through the schema BY NAME (resolved against the running
 *     server) because the CounterStrikeSharp.API package this builds against predates them; a server without
 *     them logs one line and skips them;
 *   - at every round_start the game's chicken manager removes each chicken that has an owner but is not that
 *     owner's real pet — which includes ours — so every living owner's pet is put back a frame later.
 *
 * Lifecycle: spawned 0.15s after its owner spawns (the same deferral OnPlayerSpawn uses) and when their row
 * arrives from the database while they are alive; follows them; stays where it is when they die; comes back on
 * their next spawn if it was killed; removed on team change, disconnect, map end and plugin unload. Eggs never
 * leave the nest. A player the game itself gave a pet (a chicken whose m_owner is them) gets no second one.
 *
 * Deliberately NOT ported from SkinHub: the optional CDN "pets.json" dataset (this fork ships its catalogues in
 * data/ and has no dataset downloader — the five pet_definitions are built in below), the SkinHub CDN thumbnail
 * image, and the experimental, off-by-default team-intro pet (PetTeamIntro.cs upstream).
 */
public partial class WeaponPaints
{
    internal const int PetStageEgg = 0;
    internal const int PetStageChick = 1;
    internal const int PetStagePullet = 2;
    internal const int PetStageHen = 3;

    // items_game item 4681 "pet": item_quality unique, cannot trade, nameable, noteam.
    private const ushort PetItemDefIndex = 4681;
    private const int ItemQualityUnique = 4;

    // Longest pet name, in code points — the `pet_name varchar(32)` column. The in-game rename box stops at 20.
    private const int PetNameMaxCodePoints = 32;

    // m_szCustomName and m_szCustomNameOverride2/3 are char[161].
    private const int ItemNameBufferBytes = 161;

    private const float PetThinkSeconds = 1.0f;
    private const float PetSpawnDistance = 40f;
    private const float PetMaxFollowDistance = 1500f;

    // "pet food expiration date" is written a month ahead: a date in the future is the one state known to be
    // "alive and fed" (unix seconds, inferred from the attribute class).
    private const int PetFoodDays = 30;

    private const uint InvalidEntityHandle = 0xFFFFFFFF;

    // CEntityIdentity m_flags bit for an entity whose removal is queued (EF_MARKED_FOR_DELETE).
    private const uint EntityMarkedForDeleteFlag = 0x200;

    // The highest colour index accepted for a model with no known group count.
    private const int PetMaxUnknownGroup = 63;

    // The highest "pet seed" the plugin picks: the client's generator gives s and 2^32 - s the same look, and
    // 0x7FFFFFFF starts with a zero draw, so 1..0x7FFFFFFE holds every usable look once. Typed seeds above it
    // stay valid.
    private const uint PetMaxRandomSeed = 0x7FFFFFFE;

    /// <summary>A pet_definitions entry. Kind is "egg", "chick" or "adult".</summary>
    internal sealed record PetDefinition(int Id, string DisplayName, string Kind, string? Breed, string Model);

    // items_game pet_definitions (1.41.8.2). Changes only with a game update.
    private static readonly PetDefinition[] PetDefinitions =
    [
        new(1, "Chicken Egg", "egg", null, "models/chicken/egg_pristine.vmdl"),
        new(2, "Chick", "chick", null, "models/chicken/chick.vmdl"),
        new(3, "Catalana Chicken", "adult", "catalana", "models/chicken/chicken.vmdl"),
        new(4, "Silkie Chicken", "adult", "silkie", "models/chicken/chicken_silkie.vmdl"),
        new(5, "Polish Chicken", "adult", "polish", "models/chicken/chicken_polish.vmdl"),
    ];

    private const string ChickModel = "models/chicken/chick.vmdl";

    /// <summary>A pet model's colours: how many material groups it has, and the weights a new pet's colour is rolled with.</summary>
    private sealed record PetModelColours(int GroupCount, (int Group, int Weight)[] RollWeights);

    // Material groups per pet model (CS2 1.41.8.4). Index 0 is "default", index N is named "N", so a style is a
    // group index. Weights are the model's chicken_metadata `matgrps` "freq" values; a group without a weight is
    // never rolled but can still be picked with !pet color. The chick and egg have no groups.
    private static readonly Dictionary<string, PetModelColours> PetColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["models/chicken/chicken.vmdl"] = new(14, [(1, 4), (2, 1), (3, 3), (4, 1), (5, 1), (6, 2), (8, 3), (9, 2), (10, 1), (11, 1)]),
        ["models/chicken/chicken_silkie.vmdl"] = new(10, [(1, 4), (2, 3), (4, 2), (5, 2), (7, 3)]),
        ["models/chicken/chicken_polish.vmdl"] = new(13, [(1, 1), (2, 2), (3, 1), (4, 1), (5, 2), (9, 3)]),
        [ChickModel] = new(0, []),
        ["models/chicken/egg_pristine.vmdl"] = new(0, []),
    };

    /// <summary>
    /// What a spawned pet looks like. Equal looks mean the live chicken can be kept as it is. Group is the
    /// material group index sent with "Skin" (see PetGroupFor), not the stored style.
    /// </summary>
    private sealed record PetLook(int PetId, int Stage, int Group, uint Seed, string? Name, string Model);

    /// <summary>
    /// A chicken this plugin spawned, found again by its raw entity handle (index + serial), so a reused index is
    /// never mistaken for it. ItemId is only kept to log, once, whether the server rewrites the item.
    /// </summary>
    private sealed record ActivePet(uint Handle, ulong ItemId, ulong SteamId, PetLook Look);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> PetSchemaOffsets = new();
    private static int _petOwnerMissingLogged;
    private static int _petItemRewriteLogged;
    private static int _petMaterialGroupLogged;
    private static bool? _petLeaderNetworked;
    private int _petThinkTicks;

    internal static void QueuePetSpawn(int slot) => PetSpawnQueue[slot] = 0;

    private static PetDefinition? FindPet(int petId) => PetDefinitions.FirstOrDefault(pet => pet.Id == petId);

    /// <summary>Stages a pet can be picked at: the chick def only as a chick, the breeds as pullet or hen.</summary>
    private static int[] PetStagesFor(PetDefinition pet) =>
        pet.Kind switch
        {
            "chick" => [PetStageChick],
            "adult" => [PetStagePullet, PetStageHen],
            _ => [],
        };

    private static string PetStageKey(int stage) =>
        stage switch
        {
            PetStageEgg => "egg",
            PetStageChick => "chick",
            PetStagePullet => "pullet",
            _ => "hen",
        };

    private string PetStageLabel(int stage) => Localizer[$"wp_pet_stage_{PetStageKey(stage)}"];

    /// <summary>
    /// What a stored pet looks like when spawned, or null when it does not leave the nest (an egg). Odd rows a
    /// website wrote are normalised rather than refused: the chick def is always stage 1, a breed at stage 1 is
    /// drawn as a chick (with its breed's pet id), and the stage is clamped to 0..3.
    /// </summary>
    private static PetLook? ResolvePetLook(PetDefinition pet, PetInfo info)
    {
        var stage = Math.Clamp(info.Stage, PetStageEgg, PetStageHen);
        if (pet.Kind == "egg" || stage == PetStageEgg)
            return null;
        if (pet.Kind == "chick")
            stage = PetStageChick;

        var model = stage == PetStageChick ? ChickModel : pet.Model;

        return new PetLook(pet.Id, stage, PetGroupFor(model, info.Variant), info.Seed, SanitizePetName(info.Name), model);
    }

    /// <summary>
    /// The material group a pet is drawn with: its style when that is a group of the model it is drawn with,
    /// otherwise 0 (the model's "default" group) — no style, a chick, or an index past the end.
    /// </summary>
    private static int PetGroupFor(string model, int? style)
    {
        if (style is not { } group || group < 0)
            return 0;
        if (!PetColours.TryGetValue(model, out var colours))
            return group;
        return group < colours.GroupCount ? group : 0;
    }

    /// <summary>Whether !pet color takes this index for this pet: 0 always, past it only the pet's own groups.</summary>
    private static bool PetVariantAllowed(PetDefinition? pet, int variant) =>
        variant == 0
        || (variant > 0 && variant < (pet != null && PetColours.TryGetValue(pet.Model, out var colours) ? colours.GroupCount : PetMaxUnknownGroup + 1));

    /// <summary>A colour for a new pet, rolled with the model's weights, or null (default group) when it has none.</summary>
    private static int? RandomPetVariant(PetDefinition pet)
    {
        if (!PetColours.TryGetValue(pet.Model, out var colours) || colours.RollWeights.Length == 0)
            return null;

        var roll = Random.Shared.Next(colours.RollWeights.Sum(entry => entry.Weight));
        foreach (var (group, weight) in colours.RollWeights)
        {
            if (roll < weight)
                return group;
            roll -= weight;
        }

        return null;
    }

    internal static string? SanitizePetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // No control characters, and none of the characters the chat colour tags and centre-HTML use.
        var clean = new string(name.Where(c => !char.IsControl(c) && c is not ('{' or '}' or '<' or '>')).ToArray()).Trim();

        // Whole text elements only, so a cut never splits a surrogate pair or an emoji sequence, and never more
        // code points than the column holds.
        var kept = new StringBuilder();
        var codePoints = 0;
        var elements = StringInfo.GetTextElementEnumerator(clean);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var elementCodePoints = element.EnumerateRunes().Count();
            if (codePoints + elementCodePoints > PetNameMaxCodePoints)
                break;

            kept.Append(element);
            codePoints += elementCodePoints;
        }

        clean = kept.ToString().Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>A seed in 1..PetMaxRandomSeed. Never 0: seed 0 skips the colour jitter altogether.</summary>
    private static uint RandomPetSeed() => (uint)Random.Shared.NextInt64(1, (long)PetMaxRandomSeed + 1);

    private bool PlayerMayUsePets(CCSPlayerController player) =>
        string.IsNullOrWhiteSpace(Config.Additional.PetPermission) || AdminManager.PlayerHasPermissions(player, Config.Additional.PetPermission.Trim());

    #region Lifecycle

    private void RegisterPetListeners()
    {
        if (!Config.Additional.PetsEnabled)
            return;

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawnPet);
        RegisterEventHandler<EventRoundStart>(OnRoundStartPets);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamPet);
        RegisterListener<Listeners.OnMapEnd>(OnMapEndPets);
        RegisterListener<Listeners.OnServerPrecacheResources>(OnPrecachePets);

        AddTimer(PetThinkSeconds, PetThink, TimerFlags.REPEAT);
    }

    /// <summary>
    /// The pet models, precached for every map. A plugin loaded mid-map misses this until the next map, which may
    /// leave pets on the default chicken model until then. The egg never spawns.
    /// </summary>
    private void OnPrecachePets(ResourceManifest manifest)
    {
        foreach (var model in PetDefinitions.Where(pet => pet.Kind != "egg").Select(pet => pet.Model).Distinct(StringComparer.Ordinal))
            manifest.AddResource(model);
    }

    private HookResult OnPlayerSpawnPet(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        var slot = player.Slot;
        var steamId = player.SteamID;

        // Same 0.15s deferral as OnPlayerSpawn: the pawn is placed at its spawn point by then (not at the
        // pre-spawn graveyard origin), and a round restart has finished its clean-up. Re-resolve by slot.
        AddTimer(
            0.15f,
            () =>
            {
                var current = Utilities.GetPlayerFromSlot(slot);
                if (current == null || !current.IsValid || current.SteamID != steamId)
                    return;

                PetSpawnQueue.TryRemove(slot, out _);
                SpawnPlayerPet(current);
            },
            TimerFlags.STOP_ON_MAPCHANGE
        );

        return HookResult.Continue;
    }

    /// <summary>
    /// round_start: the game's chicken manager removes every chicken whose m_owner is a player unless it is that
    /// player's own game pet, which ours never is. One frame later, once that removal has gone through, every
    /// owner who is alive gets their pet back; a pet still standing is only brought to its owner.
    /// </summary>
    private HookResult OnRoundStartPets(EventRoundStart @event, GameEventInfo info)
    {
        Server.NextFrame(() =>
        {
            foreach (var slot in GPlayersPet.Keys)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (player == null || !player.IsValid || !player.PawnIsAlive)
                    continue;

                PetSpawnQueue.TryRemove(slot, out _);
                SpawnPlayerPet(player);
            }
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerTeamPet(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid)
            return HookResult.Continue;

        // Any team change sends the pet home; the owner's next spawn brings it back.
        RemovePlayerPet(player.Slot);
        return HookResult.Continue;
    }

    private void OnMapEndPets()
    {
        // The chickens go with the map; only the bookkeeping is left to clear.
        _activePets.Clear();
        PetSpawnQueue.Clear();
    }

    /// <summary>Removes every pet this plugin spawned. Called from Unload.</summary>
    private void RemoveAllPets()
    {
        try
        {
            foreach (var slot in _activePets.Keys)
                RemovePlayerPet(slot);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not remove the pets on unload: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Once a second: spawn queued pets for players who are alive, drop pets that died or were cleaned up (they
    /// come back on the owner's next spawn), re-assert the leader, bring back a pet that fell far behind, and give
    /// way to a pet the game spawned for the same player.
    /// </summary>
    private void PetThink()
    {
        foreach (var slot in PetSpawnQueue.Keys)
        {
            var queued = Utilities.GetPlayerFromSlot(slot);
            if (queued == null || !queued.IsValid)
            {
                PetSpawnQueue.TryRemove(slot, out _);
                continue;
            }

            // Dead or not spawned yet: their spawn handler picks it up.
            if (!queued.PawnIsAlive)
                continue;

            PetSpawnQueue.TryRemove(slot, out _);
            SpawnPlayerPet(queued);
        }

        if (_activePets.IsEmpty)
            return;

        // Looking for game-spawned pets walks every chicken, so it runs every fifth tick.
        var scanForGamePets = ++_petThinkTicks % 5 == 0;
        HashSet<uint>? gameOwners = null;

        foreach (var (slot, active) in _activePets)
        {
            var chicken = ResolvePet(active);
            if (chicken == null)
            {
                _activePets.TryRemove(slot, out _);
                continue;
            }

            var player = Utilities.GetPlayerFromSlot(slot);
            if (player == null || !player.IsValid || player.SteamID != active.SteamId)
            {
                RemovePlayerPet(slot);
                continue;
            }

            // A dead owner's pet stays where it is.
            var pawn = player.PlayerPawn.Value;
            if (!player.PawnIsAlive || pawn == null || !pawn.IsValid)
                continue;

            if (scanForGamePets && (gameOwners ??= GameSpawnedPetOwners()).Contains(pawn.Controller.Raw))
            {
                RemovePlayerPet(slot);
                continue;
            }

            try
            {
                AssertPetLeader(chicken, player);

                if (
                    chicken.AbsOrigin is { } petOrigin
                    && pawn.AbsOrigin is { } ownerOrigin
                    && Vector3.Distance(new Vector3(petOrigin.X, petOrigin.Y, petOrigin.Z), new Vector3(ownerOrigin.X, ownerOrigin.Y, ownerOrigin.Z)) > PetMaxFollowDistance
                    && PetSpawnPoint(pawn) is var (position, angles)
                )
                    chicken.Teleport(position, angles, Vector3.Zero);
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Pet upkeep failed for slot {Slot}: {Message}", slot, ex.Message);
            }
        }
    }

    /// <summary>
    /// Makes the world match the player's stored pet: spawns it, keeps the live one when nothing about it changed
    /// (bringing it to the player), replaces it when something did, and removes it when there is no pet to show.
    /// Game thread only.
    /// </summary>
    private void SpawnPlayerPet(CCSPlayerController player)
    {
        if (!Config.Additional.PetsEnabled || !Utility.IsPlayerValid(player))
            return;

        var slot = player.Slot;

        // "No pet to show" before the alive check: `!pet off` (or a revoked PetPermission) from a dead owner has
        // to take the chicken away now, and PetThink skips dead owners.
        if (
            !PlayerMayUsePets(player)
            || !GPlayersPet.TryGetValue(slot, out var petInfo)
            || FindPet(petInfo.PetId) is not { } definition
            || ResolvePetLook(definition, petInfo) is not { } look
        )
        {
            RemovePlayerPet(slot);
            return;
        }

        // A change of look while dead waits for the owner's next spawn, which replaces the pet.
        var pawn = player.PlayerPawn.Value;
        if (!player.PawnIsAlive || pawn == null || !pawn.IsValid || player.Team is CsTeam.None or CsTeam.Spectator)
            return;

        try
        {
            if (GameSpawnedPetOwners().Contains(pawn.Controller.Raw))
            {
                RemovePlayerPet(slot);
                return;
            }

            if (_activePets.TryGetValue(slot, out var active) && ResolvePet(active) is { } existing)
            {
                if (active.SteamId == player.SteamID && active.Look == look)
                {
                    if (PetSpawnPoint(pawn) is var (position, angles))
                        existing.Teleport(position, angles, Vector3.Zero);
                    AssertPetLeader(existing, player);
                    return;
                }

                KillPetEntity(existing);
            }

            _activePets.TryRemove(slot, out _);
            CreatePet(player, pawn, look);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not spawn the pet for {Player}: {Message}", player.PlayerName, ex.Message);
        }
    }

    private void CreatePet(CCSPlayerController player, CCSPlayerPawn pawn, PetLook look)
    {
        if (PetSpawnPoint(pawn) is not var (position, angles))
            return;

        var chicken = Utilities.CreateEntityByName<CChicken>("chicken");
        if (chicken == null || !chicken.IsValid)
        {
            Logger.LogWarning("Could not create a chicken entity for {Player}'s pet", player.PlayerName);
            return;
        }

        try
        {
            // The item first, all of it before DispatchSpawn: Spawn reads "upgrade level" once, for the scale.
            var item = chicken.AttributeManager.Item;
            FillPetItem(item, player.SteamID, look);

            var ownerHandle = pawn.Controller.Raw;
            WritePetOwner(chicken, ownerHandle, false);
            chicken.Leader.Raw = player.PlayerPawn.Raw;

            // No `chicken_model`: it does not choose the model (Spawn loads chicken.vmdl either way) and only
            // widens Spawn's random colour roll, which the "Skin" input below replaces.
            var keyValues = new CEntityKeyValues();
            keyValues.SetVector("origin", position.X, position.Y, position.Z);
            keyValues.SetAngle("angles", angles.X, angles.Y, angles.Z);
            chicken.DispatchSpawn(keyValues);

            // Spawn loaded chicken.vmdl and cleared the leader. The model goes first: the colour is an index into
            // the model's own groups. Then the colour, on every spawn — the style, or 0 (the default group).
            EnsurePetModel(chicken, look.Model);
            chicken.AcceptInput("Skin", value: look.Group.ToString(CultureInfo.InvariantCulture));
            LogPetMaterialGroupOnce(chicken, look);
            WritePetOwner(chicken, ownerHandle, true);
            AssertPetLeader(chicken, player);
            chicken.Teleport(position, angles, Vector3.Zero);

            _activePets[player.Slot] = new ActivePet(chicken.EntityHandle.Raw, item.ItemID, player.SteamID, look);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Could not spawn the pet for {Player}: {Message}", player.PlayerName, ex.Message);
            KillPetEntity(chicken);
        }
    }

    /// <summary>
    /// Diagnostic only, for the first server test: the colour token the "Skin" input left in the networked
    /// CSkeletonInstance::m_materialGroup, logged once, a frame later. 0 means the index was past the end of the
    /// model's groups, or the model was not set yet.
    /// </summary>
    private void LogPetMaterialGroupOnce(CChicken chicken, PetLook look)
    {
        if (Interlocked.Exchange(ref _petMaterialGroupLogged, 1) != 0)
            return;

        Server.NextFrame(() =>
        {
            try
            {
                if (!chicken.IsValid)
                    return;
                var skeleton = chicken.CBodyComponent?.SceneNode?.GetSkeletonInstance();
                if (skeleton == null)
                    return;

                Logger.LogInformation("Pet colour check: \"Skin\" {Group} on {Model} left m_materialGroup = 0x{Token:x8}", look.Group, look.Model, skeleton.MaterialGroup.Value);
            }
            catch (Exception ex)
            {
                Logger.LogInformation("Pet colour check skipped: {Message}", ex.Message);
            }
        });
    }

    private void RemovePlayerPet(int slot)
    {
        if (!_activePets.TryRemove(slot, out var active))
            return;
        KillPetEntity(ResolvePet(active));
    }

    private void KillPetEntity(CChicken? chicken)
    {
        if (chicken == null || !chicken.IsValid)
            return;

        try
        {
            // The same deferred Kill the rest of the plugin uses for weapons.
            chicken.AddEntityIOEvent("Kill", chicken, null, "", 0.0f);
        }
        catch (Exception)
        {
            try
            {
                chicken.Remove();
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not remove a pet: {Message}", ex.Message);
            }
        }
    }

    private CChicken? ResolvePet(ActivePet active)
    {
        // CHandle compares the whole handle, serial number included, so a reused index resolves to null.
        var chicken = new CHandle<CChicken>(active.Handle).Value;
        if (chicken == null || !chicken.IsValid || chicken.DesignerName != "chicken")
            return null;

        // Queued for removal (the game's round_start pass, a Kill input): the handle resolves until the removal
        // goes through, and a pet kept now would vanish a moment later. Treated as gone, so it is replaced.
        var identity = chicken.Entity;
        if (identity != null && (identity.Flags & EntityMarkedForDeleteFlag) != 0)
            return null;

        // Diagnostic only: says whether the game rewrites the pet item after spawn.
        var itemId = chicken.AttributeManager.Item.ItemID;
        if (itemId != active.ItemId && Interlocked.Exchange(ref _petItemRewriteLogged, 1) == 0)
            Logger.LogInformation(
                "A pet's item id changed after spawn ({Before} -> {After}, def {Def}) - the server rewrites the pet item on its own. The pet is still tracked by its entity handle.",
                active.ItemId,
                itemId,
                chicken.AttributeManager.Item.ItemDefinitionIndex
            );

        return chicken;
    }

    /// <summary>Controllers (as raw handles) that own a chicken this plugin did not spawn.</summary>
    private HashSet<uint> GameSpawnedPetOwners()
    {
        var owners = new HashSet<uint>();
        if (PetSchemaOffset("CChicken", "m_owner") <= 0)
            return owners;

        var ours = _activePets.Values.Select(active => active.Handle).ToHashSet();

        foreach (var chicken in Utilities.FindAllEntitiesByDesignerName<CChicken>("chicken"))
        {
            if (!chicken.IsValid || ours.Contains(chicken.EntityHandle.Raw))
                continue;

            var owner = Schema.GetRef<uint>(chicken.Handle, "CChicken", "m_owner");
            if (owner != 0 && owner != InvalidEntityHandle)
                owners.Add(owner);
        }

        return owners;
    }

    /// <summary>Behind the player, on the floor they stand on, facing the way they face. Null at the graveyard origin.</summary>
    private static (Vector3 Position, Vector3 Angles)? PetSpawnPoint(CCSPlayerPawn pawn)
    {
        if (pawn.AbsOrigin is not { } origin || origin.Z < -16000f)
            return null;

        var yaw = pawn.EyeAngles.Y;
        var radians = yaw * MathF.PI / 180f;

        return (new Vector3(origin.X - MathF.Cos(radians) * PetSpawnDistance, origin.Y - MathF.Sin(radians) * PetSpawnDistance, origin.Z + 2f), new Vector3(0f, yaw, 0f));
    }

    #endregion

    #region Writing the pet item

    /// <summary>
    /// The numeric pet attributes, through the same CAttributeList setter the stickers use. All are
    /// `stored_as_integer` in items_game, so they go in as the integer's bit pattern (ViewAsFloat).
    /// </summary>
    private static void WritePetAttributes(nint attributeList, PetLook look)
    {
        var fedUntil = (uint)DateTimeOffset.UtcNow.AddDays(PetFoodDays).ToUnixTimeSeconds();

        CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "pet id", ViewAsFloat((uint)look.PetId));
        CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "upgrade level", ViewAsFloat((uint)look.Stage));
        CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "pet seed", ViewAsFloat(look.Seed));
        CAttributeListSetOrAddAttributeValueByName.Invoke(attributeList, "pet food expiration date", ViewAsFloat(fedUntil));
    }

    /// <summary>Fills an item view as the game's pet item 4681 for this look, owned by this player.</summary>
    private void FillPetItem(CEconItemView item, ulong steamId, PetLook look)
    {
        item.ItemDefinitionIndex = PetItemDefIndex;
        item.EntityQuality = ItemQualityUnique;
        item.EntityLevel = 1; // min_ilevel = max_ilevel = 1 on item 4681
        UpdatePlayerEconItemId(item);
        item.AccountID = (uint)steamId;
        item.Initialized = true;

        // Dual-write, like every other attribute write in this plugin (see SetStickers).
        WritePetAttributes(item.NetworkedDynamicAttributes.Handle, look);
        WritePetAttributes(item.AttributeList.Handle, look);
        WritePetName(item, look.Name, look.Stage);
    }

    /// <summary>
    /// The name, where the game keeps it for this stage: m_szCustomName (chick), m_szCustomNameOverride2 (pullet),
    /// m_szCustomNameOverride3 (hen). Only m_szCustomName is networked, so the overhead label can only come from it —
    /// the current stage's name always goes there, and in the stage's own buffer too; the other buffer is emptied.
    /// </summary>
    private static void WritePetName(CEconItemView item, string? name, int stage)
    {
        var text = name ?? "";
        item.CustomName = text;

        WriteItemNameBuffer(item, "m_szCustomNameOverride2", stage == PetStagePullet ? text : "");
        WriteItemNameBuffer(item, "m_szCustomNameOverride3", stage == PetStageHen ? text : "");
    }

    /// <summary>
    /// Writes a char[161] CEconItemView name buffer the CSSharp package this builds against has no setter for:
    /// UTF-8 bytes, then a terminator. SanitizePetName caps a name at 32 code points (at most 128 bytes); the
    /// length is checked anyway. Skipped when this server has no such field.
    /// </summary>
    private static void WriteItemNameBuffer(CEconItemView item, string field, string text)
    {
        // Offset 0 means "not found" — never write there, it is the item's vtable.
        var offset = PetSchemaOffset("CEconItemView", field);
        if (offset <= 0)
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length >= ItemNameBufferBytes)
            return;

        var address = item.Handle + offset;
        Marshal.Copy(bytes, 0, address, bytes.Length);
        Marshal.WriteByte(address, bytes.Length, 0);
    }

    private void WritePetOwner(CChicken chicken, uint controllerHandle, bool networked)
    {
        if (PetSchemaOffset("CChicken", "m_owner") <= 0)
        {
            if (Interlocked.Exchange(ref _petOwnerMissingLogged, 1) == 0)
                Logger.LogWarning("This server has no CChicken::m_owner (it predates CS2 1.41.8.2) - pets spawn without an owner, so they carry no \"<player>'s <name>\" label");
            return;
        }

        Schema.GetRef<uint>(chicken.Handle, "CChicken", "m_owner") = controllerHandle;
        if (networked)
            Utilities.SetStateChanged(chicken, "CChicken", "m_owner");
    }

    private static void AssertPetLeader(CChicken chicken, CCSPlayerController player)
    {
        var pawnHandle = player.PlayerPawn.Raw;
        var leader = chicken.Leader;
        if (leader.Raw == pawnHandle)
            return;

        leader.Raw = pawnHandle;

        _petLeaderNetworked ??= Schema.IsSchemaFieldNetworked("CChicken", "m_leader");
        if (_petLeaderNetworked == true)
            Utilities.SetStateChanged(chicken, "CChicken", "m_leader");
    }

    private static void EnsurePetModel(CChicken chicken, string model)
    {
        var current = chicken.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName;
        if (!string.IsNullOrEmpty(current) && !string.Equals(current, model, StringComparison.OrdinalIgnoreCase))
            chicken.SetModel(model);
    }

    /// <summary>A schema field's offset on the running server, or 0 when it has no such field. Cached.</summary>
    private static int PetSchemaOffset(string className, string field) =>
        PetSchemaOffsets.GetOrAdd(
            $"{className}::{field}",
            _ =>
            {
                try
                {
                    return Schema.GetSchemaOffset(className, field);
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        );

    #endregion

    #region !pet

    private void SetupPetsMenu()
    {
        Config.Additional.CommandPet.ForEach(c =>
        {
            AddGatedCommand(
                $"css_{c}",
                "Pet menu",
                (player, info) =>
                {
                    // Closed between round_end and round_start, like every other WeaponPaints menu.
                    if (!Utility.IsPlayerValid(player) || player == null || !_gBCommandsAllowed)
                        return;
                    OnCommandPet(player, info);
                }
            );
        });
    }

    /// <summary>
    /// !pet opens the menu. The rest are chat subcommands, because a name and a seed are typed, not picked:
    ///   !pet name &lt;text&gt;                  - name the pet (empty clears it)
    ///   !pet seed [number]                  - a new random seed (body shape, colour jitter), or a specific one
    ///   !pet color &lt;n|random|default&gt;     - pick a colour (material group), roll one, or back to the default
    ///   !pet stage &lt;chick|pullet|hen&gt;     - change the stage
    ///   !pet off                            - no pet
    /// </summary>
    private void OnCommandPet(CCSPlayerController player, CommandInfo command)
    {
        if (!PlayerMayUsePets(player))
        {
            PrintPet(player, "wp_pet_no_permission");
            return;
        }

        // Prevent double-call from chat/console trigger
        if (LastCommandTime.TryGetValue(player.Slot, out var lastTime) && (DateTime.UtcNow - lastTime).TotalMilliseconds < 100)
            return;
        LastCommandTime[player.Slot] = DateTime.UtcNow;

        // One cooldown for the menu and every subcommand: each change respawns the chicken and writes the DB.
        if (CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) && DateTime.UtcNow < cooldownEndTime)
        {
            PrintPet(player, "wp_command_cooldown");
            return;
        }

        CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);

        var subcommand = command.ArgCount > 1 ? command.GetArg(1).Trim().ToLowerInvariant() : "";
        var argument = PetCommandArgument(command);

        switch (subcommand)
        {
            case "":
                OpenPetMenu(player);
                break;
            case "name":
                SetPetName(player, argument);
                break;
            case "seed":
                SetPetSeed(player, argument);
                break;
            case "color":
            case "colour":
            case "variant":
                SetPetVariant(player, argument);
                break;
            case "stage":
                SetPetStage(player, argument);
                break;
            case "off":
            case "none":
            case "remove":
                ChoosePet(player, null, PetStageEgg);
                break;
            default:
                PrintPet(player, "wp_pet_usage");
                break;
        }
    }

    /// <summary>Everything after the subcommand, unquoted — a name may contain spaces.</summary>
    private static string PetCommandArgument(CommandInfo command)
    {
        var args = command.ArgString.Trim();
        var space = args.IndexOfAny([' ', '\t']);
        var rest = space < 0 ? "" : args[(space + 1)..].Trim();

        return rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"' ? rest[1..^1] : rest;
    }

    private void OpenPetMenu(CCSPlayerController player)
    {
        var menu = Utility.CreateMenu(Localizer["wp_pet_menu_title"]);
        if (menu == null)
            return;

        menu.AddItem(Localizer["None"], (p, _) => ChoosePet(p, null, PetStageEgg));

        foreach (var pet in PetDefinitions.Where(pet => PetStagesFor(pet).Length > 0))
        {
            var chosen = pet;
            menu.AddItem(
                pet.DisplayName,
                (p, _) =>
                {
                    if (!Utility.IsPlayerValid(p))
                        return;

                    var stages = PetStagesFor(chosen);
                    if (stages.Length == 1)
                    {
                        ChoosePet(p, chosen, stages[0]);
                        return;
                    }

                    OpenPetStageMenu(p, chosen, stages);
                }
            );
        }

        if (GPlayersPet.ContainsKey(player.Slot))
            menu.AddItem(Localizer["wp_pet_menu_random_look"], (p, _) => RandomizePetLook(p));

        menu.Display(player, 0);
    }

    private void OpenPetStageMenu(CCSPlayerController player, PetDefinition pet, int[] stages)
    {
        var menu = Utility.CreateMenu(Localizer["wp_pet_menu_stage_title", pet.DisplayName]);
        if (menu == null)
            return;

        foreach (var stage in stages)
        {
            var chosen = stage;
            menu.AddItem(PetStageLabel(stage), (p, _) => ChoosePet(p, pet, chosen));
        }

        menu.Display(player, 0);
    }

    /// <summary>Picks a pet (or none, with a null pet), saves it and makes the world match.</summary>
    private void ChoosePet(CCSPlayerController player, PetDefinition? pet, int stage)
    {
        if (!Utility.IsPlayerValid(player))
            return;

        var slot = player.Slot;
        PetInfo? next = null;

        if (pet != null)
        {
            GPlayersPet.TryGetValue(slot, out var current);
            var samePet = current != null && current.PetId == pet.Id;

            next = new PetInfo
            {
                PetId = pet.Id,
                Stage = stage,
                // The same pet keeps its seed and colour, so changing the stage does not change the look. Another
                // pet gets both new; the colour is not carried over (it indexes one model's own groups).
                Seed = samePet ? current!.Seed : RandomPetSeed(),
                Variant = samePet ? current!.Variant : RandomPetVariant(pet),
                Name = current?.Name,
            };

            GPlayersPet[slot] = next;
            PrintPet(player, "wp_pet_menu_select", $"{pet.DisplayName} ({PetStageLabel(stage)})");
        }
        else
        {
            GPlayersPet.TryRemove(slot, out _);
            PrintPet(player, "wp_pet_menu_removed");
        }

        SavePet(player, next);
        ShowPetChange(player);
    }

    private void SetPetName(CCSPlayerController player, string argument)
    {
        if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        pet.Name = SanitizePetName(argument);

        if (pet.Name == null)
            PrintPet(player, "wp_pet_name_cleared");
        else
            PrintPet(player, "wp_pet_name_set", pet.Name);

        SavePet(player, pet);
        ShowPetChange(player);
    }

    private void SetPetSeed(CCSPlayerController player, string argument)
    {
        if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        uint seed;
        if (string.IsNullOrWhiteSpace(argument) || argument.Equals("random", StringComparison.OrdinalIgnoreCase))
            seed = RandomPetSeed();
        // 0 is refused: it is the one seed that skips the colour jitter.
        else if (!uint.TryParse(argument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seed) || seed == 0)
        {
            PrintPet(player, "wp_pet_invalid");
            return;
        }

        pet.Seed = seed;
        PrintPet(player, "wp_pet_seed_set", seed.ToString(CultureInfo.InvariantCulture));

        SavePet(player, pet);
        ShowPetChange(player);
    }

    /// <summary>The menu's "New random look": a new seed and, when the pet has colours to roll, a new colour.</summary>
    private void RandomizePetLook(CCSPlayerController player)
    {
        if (!Utility.IsPlayerValid(player))
            return;
        if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        pet.Seed = RandomPetSeed();
        PrintPet(player, "wp_pet_seed_set", pet.Seed.ToString(CultureInfo.InvariantCulture));

        if (FindPet(pet.PetId) is { } definition && RandomPetVariant(definition) is { } variant)
        {
            pet.Variant = variant;
            PrintPet(player, "wp_pet_variant_set", variant.ToString(CultureInfo.InvariantCulture));
        }

        SavePet(player, pet);
        ShowPetChange(player);
    }

    private void SetPetVariant(CCSPlayerController player, string argument)
    {
        if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        var value = argument.Trim().ToLowerInvariant();
        var definition = FindPet(pet.PetId);

        if (value is "" or "default" or "none")
        {
            pet.Variant = null;
            PrintPet(player, "wp_pet_variant_cleared");
        }
        else if (value == "random" && definition != null && RandomPetVariant(definition) is { } rolled)
        {
            pet.Variant = rolled;
            PrintPet(player, "wp_pet_variant_set", rolled.ToString(CultureInfo.InvariantCulture));
        }
        else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var variant) && PetVariantAllowed(definition, variant))
        {
            pet.Variant = variant;
            PrintPet(player, "wp_pet_variant_set", variant.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            PrintPet(player, "wp_pet_invalid");
            return;
        }

        SavePet(player, pet);
        ShowPetChange(player);
    }

    private void SetPetStage(CCSPlayerController player, string argument)
    {
        if (!GPlayersPet.TryGetValue(player.Slot, out var pet))
        {
            PrintPet(player, "wp_pet_none");
            return;
        }

        var stage = argument.Trim().ToLowerInvariant() switch
        {
            "chick" or "1" => PetStageChick,
            "pullet" or "2" => PetStagePullet,
            "hen" or "3" => PetStageHen,
            _ => -1,
        };

        if (FindPet(pet.PetId) is not { } definition || !PetStagesFor(definition).Contains(stage))
        {
            PrintPet(player, "wp_pet_invalid");
            return;
        }

        pet.Stage = stage;
        PrintPet(player, "wp_pet_stage_set", PetStageLabel(stage));

        SavePet(player, pet);
        ShowPetChange(player);
    }

    /// <summary>
    /// Shows a change the player just made. A menu opened before round_end can still be picked from after it; like
    /// the knife menu, such a pick is saved and shown on the owner's next spawn (a chicken spawned now would only be
    /// caught by the round restart clean-up). Taking a pet away creates nothing, so that happens at once.
    /// </summary>
    private void ShowPetChange(CCSPlayerController player)
    {
        if (_gBCommandsAllowed || !GPlayersPet.ContainsKey(player.Slot))
            SpawnPlayerPet(player);
    }

    private void SavePet(CCSPlayerController player, PetInfo? pet)
    {
        if (WeaponSync == null)
            return;

        var playerInfo = PlayerInfo.From(player);

        // A copy: the worker must not see this object change under it.
        var snapshot = pet == null
            ? null
            : new PetInfo
            {
                PetId = pet.PetId,
                Stage = pet.Stage,
                Variant = pet.Variant,
                Seed = pet.Seed,
                Name = pet.Name,
            };

        QueueDb(playerInfo, "pet", sync => sync.SyncPetToDatabase(playerInfo, snapshot));
    }

    private void PrintPet(CCSPlayerController player, string key, params object[] args)
    {
        // An empty translation turns a message off, the same as everywhere else in the plugin.
        if (string.IsNullOrEmpty(Localizer[key]))
            return;
        player.Print(args.Length == 0 ? Localizer[key] : Localizer[key, args]);
    }

    #endregion
}
