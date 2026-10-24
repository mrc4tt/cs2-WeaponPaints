using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponPaints
{
    public partial class WeaponPaints
    {
        private bool _mvpPlayed;

        // slot -> spawn counter, so a deferred spawn apply can tell it has been superseded.
        private readonly Dictionary<int, int> _spawnGeneration = new();

        [GameEventHandler]
        public HookResult OnClientFullConnect(EventPlayerConnectFull @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;

            if (player is null || !player.IsValid || player.IsBot || WeaponSync == null || Database == null)
                return HookResult.Continue;

            var playerInfo = PlayerInfo.From(player);

            try
            {
                _ = Task.Run(async () =>
                {
                    await QueueGetPlayerData(playerInfo);

                    // Mark data ready so OnPlayerSpawn's refresh runs (and does so with data, not
                    // empty). If the player hasn't spawned yet, that timer does the first refresh.
                    _weaponDataReady[playerInfo.Slot] = true;

                    // After DB data is loaded, refresh ALL weapons on the main thread — not just the
                    // knife. Weapons are given at spawn BEFORE this background GetPlayerData finishes,
                    // so the give-event apply (OnGiveNamedItemPost/OnEntityCreated) runs with no data
                    // and pistols/rifles get no skin or stickers. The old code only re-applied the
                    // knife here, which is why guns showed nothing until the player typed !wp.
                    // RefreshWeapons regives every weapon (incl. knife) -> dual-event -> full apply:
                    // the same thing !wp does, automatically once the data has landed. If the player
                    // hasn't spawned yet, OnPlayerSpawn does the same refresh once they do.
                    Server.NextWorldUpdate(() =>
                    {
                        if (player != null && player.IsValid)
                            ScheduleMusicPinReassert(player);

                        if (player == null || !player.IsValid || !player.PawnIsAlive)
                            return;
                        if (player.PlayerPawn.Value?.WeaponServices == null)
                            return;

                        // Reconnect race: player already spawned before data landed, so their guns
                        // are unskinned. Apply in place (no flicker) + swap the knife model.
                        ApplyGunSkinsInPlace(player);
                        if (HasChangedKnife(player, out _))
                            RefreshKnife(player, switchToKnife: false);
                    });
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"[OnClientFullConnect] Error loading player data: {ex.Message}");
            }

            Players.Add(player);
            PlayersBySteamId[player.SteamID] = player;

            return HookResult.Continue;
        }

        [GameEventHandler]
        public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;

            if (player is null || !player.IsValid || player.IsBot)
                return HookResult.Continue;

            var playerInfo = PlayerInfo.From(player);

            // Detach this player's weapon state here on the game thread and flush their changed
            // StatTrak rows from that snapshot. Reading player.Slot / GPlayerWeaponsInfo from a
            // background task would race a new occupant of the slot (sync their data, then wipe it).
            GPlayerWeaponsInfo.TryGetValue(playerInfo.Slot, out var weaponsSnapshot);
            if (Config.Additional.SkinEnabled)
                GPlayerWeaponsInfo.TryRemove(playerInfo.Slot, out _);

            try
            {
                if (weaponsSnapshot != null)
                    FlushStatTrak(playerInfo.Slot, playerInfo, weaponsSnapshot);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[OnPlayerDisconnect] Error syncing StatTrak: {ex.Message}");
            }
            ForgetStatTrakDirty(playerInfo.Slot);

            if (Config.Additional.KnifeEnabled)
            {
                GPlayersKnife.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.GloveEnabled)
            {
                GPlayersGlove.TryRemove(player.Slot, out _);
                GPlayersNativeGlove.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.AgentEnabled)
            {
                GPlayersAgent.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.MusicEnabled)
            {
                GPlayersMusic.TryRemove(player.Slot, out _);
                GPlayersNativeMusic.TryRemove(player.Slot, out _);
                GPlayersMusicOff.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.PinsEnabled)
            {
                GPlayersPin.TryRemove(player.Slot, out _);
                GPlayersNativePin.TryRemove(player.Slot, out _);
            }

            // Always, not only when PetsEnabled: a pet spawned before a config change must still go.
            RemovePlayerPet(player.Slot);
            GPlayersPet.TryRemove(player.Slot, out _);
            PetSpawnQueue.TryRemove(player.Slot, out _);

            _temporaryPlayerWeaponWear.TryRemove(player.Slot, out _);
            _stickerWearAssignments.TryRemove(player.Slot, out _);
            _stickerWearOwners.TryRemove(player.Slot, out _);
            if (_pickupRefreshTimers.Remove(player.Slot, out var pickupTimer))
                pickupTimer.Kill();
            _stickerCommandFilters.TryRemove(player.Slot, out _);
            GPlayersPendingSeedWearInput.TryRemove(player.Slot, out _);
            GPlayersPaintMemory.TryRemove(player.Slot, out _);
            if (_weaponRefreshPending.Remove(player.Slot, out var weaponRefresh))
                weaponRefresh.Timer?.Kill();
            if (_stickerRefreshTimers.Remove(player.Slot, out var stickerRefresh))
                stickerRefresh.Kill();
            _musicPinReasserts.Remove(player.Slot);
            _spawnGeneration.Remove(player.Slot);
            CommandsCooldown.Remove(player.Slot);
            LastCommandTime.Remove(player.Slot);
            _playerWeaponImage.Remove(player.Slot);
            PlayersBySteamId.TryRemove(player.SteamID, out _);
            OriginalPawnModel.TryRemove(player.Slot, out _);
            _weaponDataReady.TryRemove(player.Slot, out _);
            _lastWeaponRefresh.TryRemove(player.Slot, out _);
            _refreshInFlight.TryRemove(player.Slot, out _);
            Players.Remove(player);

            return HookResult.Continue;
        }

        private void OnMapStart(string mapName)
        {
            // The timers carry STOP_ON_MAPCHANGE, but the dictionary still has to be emptied.
            ClearPickupRefreshTimers();
            ClearWeaponRefreshTimers();

            if (Config.Additional is { KnifeEnabled: false, SkinEnabled: false, GloveEnabled: false })
                return;

            // Initialize WeaponSync if not already done (e.g., if OnConfigParsed ran before map start)
            if (Database != null && WeaponSync == null)
                WeaponSync = new WeaponSynchronization(Database, Config);

            _fadeSeed = 0;
            _c4StickerSource = null;
            _c4StickerAccountId = 0;
            // Re-seed rather than reset to a constant, so the client cannot match these item IDs
            // against materials it generated for the previous map's sticker layouts.
            _nextItemId = NewItemIdSeed();
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;

            if (player is null || !player.IsValid || player.IsBot || Config.Additional is { KnifeEnabled: false, GloveEnabled: false })
                return HookResult.Continue;

            // Defer ALL cosmetic application — OnPlayerSpawn fires during team assignment
            // when the pawn's native scene node, body component, and model infrastructure
            // are not yet fully initialized. Applying cosmetics synchronously or even on
            // NextFrame crashes in native SetModel/SetBodygroup calls.
            //
            // The timer re-resolves the player by slot and checks SteamID + spawn generation: a
            // disconnect + reconnect into the slot, or a second spawn inside the 0.15s window (that
            // spawn runs its own timer), must not apply to the wrong pawn or apply twice.
            var spawnSlot = player.Slot;
            var spawnSteamId = player.SteamID;
            var generation = _spawnGeneration.TryGetValue(spawnSlot, out var previousGeneration) ? previousGeneration + 1 : 1;
            _spawnGeneration[spawnSlot] = generation;
            _musicPinReasserts.Remove(spawnSlot);

            AddTimer(
                0.15f,
                () =>
                {
                    if (!_spawnGeneration.TryGetValue(spawnSlot, out var currentGeneration) || currentGeneration != generation)
                        return;
                    player = Utilities.GetPlayerFromSlot(spawnSlot);
                    if (player == null || !player.IsValid || player.SteamID != spawnSteamId || !player.PawnIsAlive)
                        return;
                    if (player.Team is CsTeam.None or CsTeam.Spectator)
                        return;

                    CCSPlayerPawn? pawn = player.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid)
                        return;

                    // Capture CS2's default pawn model for this map/team BEFORE any custom agent
                    // overrides it — lets us restore "Default" mid-round without kill+respawn.
                    try
                    {
                        var modelPath = pawn.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState.ModelName.ToString();
                        if (!string.IsNullOrEmpty(modelPath) && modelPath.Contains('/') && modelPath.EndsWith(".vmdl"))
                        {
                            OriginalPawnModel[player.Slot] = modelPath;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("Cache pawn model failed: {Message}", ex.Message);
                    }

                    GivePlayerMusicKit(player);
                    GivePlayerAgent(player);
                    GivePlayerGloves(player);
                    GivePlayerPin(player);

                    // Apply weapon skins + stickers on every spawn. The give-event apply
                    // (OnGiveNamedItemPost/OnEntityCreated) does not reliably fire/land on every
                    // server's spawn-loadout flow, leaving guns unskinned until a manual !wp.
                    // Apply gun skins in place (no kill+regive -> no flicker) over the weapons the
                    // spawn give-loop already handed out, then swap the knife model (needs a regive).
                    // Only after GetPlayerData has landed, else there's nothing to apply yet; the
                    // connect-path does the first apply once data is ready.
                    if (_weaponDataReady.TryGetValue(player.Slot, out var ready) && ready)
                    {
                        ApplyGunSkinsInPlace(player);
                        // Only swap the knife when the player actually picked a custom one — otherwise
                        // regiving the default knife just flickers it for nothing. Don't force slot3
                        // on spawn: keep the player on whatever weapon they spawned holding.
                        if (HasChangedKnife(player, out _))
                            RefreshKnife(player, switchToKnife: false);
                    }
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );

            return HookResult.Continue;
        }

        // The client's inventory load (GC) can land after our music kit / pin were applied and put
        // the Steam ones back. Re-assert a few times after connect, then let the drift check below
        // catch anything later. Each pass is two field writes, only when the value differs.
        private void ScheduleMusicPinReassert(CCSPlayerController player)
        {
            if (!Config.Additional.MusicPinDriftCheck || (!Config.Additional.MusicEnabled && !Config.Additional.PinsEnabled))
                return;

            var slot = player.Slot;
            var steamId = player.SteamID;
            _musicPinReasserts.Remove(slot);
            foreach (var delay in new[] { 1f, 3f, 7f })
            {
                AddTimer(delay, () =>
                {
                    var target = Utilities.GetPlayerFromSlot(slot);
                    if (target is { IsValid: true, IsBot: false } && target.SteamID == steamId)
                        ReassertMusicAndPin(target);
                }, TimerFlags.STOP_ON_MAPCHANGE);
            }
        }

        private void StartMusicPinDriftTimer()
        {
            if (!Config.Additional.MusicPinDriftCheck || (!Config.Additional.MusicEnabled && !Config.Additional.PinsEnabled))
                return;

            AddTimer(5f, () =>
            {
                foreach (var player in Utilities.GetPlayers())
                {
                    if (player is { IsValid: true, IsBot: false, IsHLTV: false, Connected: PlayerConnectedState.Connected })
                        ReassertMusicAndPin(player);
                }
            }, TimerFlags.REPEAT);
        }

        // slot -> consecutive re-asserts that changed something. If the engine keeps reverting the
        // value, stop after MaxMusicPinReasserts instead of rewriting it every 5s forever; a spawn
        // or a new connect starts over.
        private readonly Dictionary<int, int> _musicPinReasserts = new();
        private const int MaxMusicPinReasserts = 5;

        // Re-applies the chosen kit / pin only when the controller no longer shows it.
        private void ReassertMusicAndPin(CCSPlayerController player)
        {
            if (player.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist) || player.InventoryServices == null)
                return;

            _musicPinReasserts.TryGetValue(player.Slot, out var count);
            if (count >= MaxMusicPinReasserts)
                return;

            var changed = false;
            if (Config.Additional.MusicEnabled
                && GPlayersMusic.TryGetValue(player.Slot, out var music) && music.TryGetValue(player.Team, out var musicId)
                && player.MusicKitID != ResolveMusicKitId(player.Slot, musicId))
            {
                GivePlayerMusicKit(player);
                changed = true;
            }

            if (Config.Additional.PinsEnabled
                && GPlayersPin.TryGetValue(player.Slot, out var pins) && pins.TryGetValue(player.Team, out var pinId) && pinId != 0
                && (int)player.InventoryServices.Rank[5] != pinId)
            {
                GivePlayerPin(player);
                changed = true;
            }

            if (changed)
                _musicPinReasserts[player.Slot] = count + 1;
        }

        private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
        {
            _gBCommandsAllowed = false;
            return HookResult.Continue;
        }

        private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
        {
            _gBCommandsAllowed = true;
            _mvpPlayed = false;
            return HookResult.Continue;
        }

        private HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
        {
            // Prestart, not round_start: the new bomb is handed out while the round restarts, and a
            // reset on round_start could land after it and wipe the row that just decorated it.
            _c4StickerSource = null;
            _c4StickerAccountId = 0;
            return HookResult.Continue;
        }

        // Planting removes weapon_c4 and creates planted_c4 (CPlantedC4), a different entity with its
        // own m_AttributeManager. Whether the game carries the bomb's item across is not known, so: if
        // the planted bomb's item already has networked attributes the game copied something and it is
        // left alone; otherwise the stickers of the row that decorated this round's bomb are written
        // onto it, in the tick it was planted (bomb_planted fires from the plant itself). Stickers only —
        // whether planted_c4 draws a charm is unknown. UNTESTED IN GAME (ported from SkinHubgg
        // WeaponPaints cc57c51, where it is untested too). Hangs off the game event rather than an
        // OnEntitySpawned "planted_c4" check so it does not depend on CSSharp's entity listeners.
        private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
        {
            if (!Config.Additional.SkinEnabled)
                return HookResult.Continue;

            var source = _c4StickerSource;
            if (source == null || !source.Stickers.Any(s => s.Id != 0))
                return HookResult.Continue;

            try
            {
                foreach (var planted in Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4"))
                {
                    if (!planted.IsValid)
                        continue;

                    var item = planted.AttributeManager?.Item;
                    if (item == null || item.AttributeList == null || item.NetworkedDynamicAttributes == null)
                        continue;
                    if (item.NetworkedDynamicAttributes.Attributes.Count > 0)
                        continue;

                    if (item.ItemDefinitionIndex == 0)
                        item.ItemDefinitionIndex = C4DefIndex;
                    UpdatePlayerEconItemId(item);
                    item.AccountID = _c4StickerAccountId;
                    item.Initialized = true;

                    // The C4 has one (hd) mesh.
                    WriteStickerAttributes(item, C4DefIndex, source, false);

                    try
                    {
                        UpdateItemView.Invoke(item.Handle, nint.Zero);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("UpdateItemView (planted C4) failed: {Message}", ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not copy the C4 stickers onto the planted bomb: {Message}", ex.Message);
            }

            return HookResult.Continue;
        }

        private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
        {
            if (_mvpPlayed)
                return HookResult.Continue;

            var player = @event.Userid;

            if (player == null || !player.IsValid)
                return HookResult.Continue;

            var musicId = @event.Musickitid;
            var noMusic = @event.Nomusic;

            // Explicit 0 ("None") counts too so the event carries the restored kit instead of a stale one.
            ushort pickedMusicId = 0;
            var customKit = !player.IsBot
                && player.Team is not (CsTeam.None or CsTeam.Spectator)
                && GPlayersMusic.TryGetValue(player.Slot, out var musicInfo)
                && musicInfo.TryGetValue(player.Team, out pickedMusicId);
            if (customKit)
            {
                musicId = ResolveMusicKitId(player.Slot, pickedMusicId);
                noMusic = 0;
            }

            var recipients = Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false }).ToList();
            var anyMuted = recipients.Any(p => GPlayersMusicOff.TryGetValue(p.Slot, out var off) && off);

            if (!customKit && !anyMuted)
                return HookResult.Continue;

            info.DontBroadcast = true;
            _mvpPlayed = true;

            // Sent per client so !musicoff players get the MVP banner without the anthem.
            foreach (var recipient in recipients)
            {
                var muted = GPlayersMusicOff.TryGetValue(recipient.Slot, out var off) && off;
                var newEvent = new EventRoundMvp(true)
                {
                    Userid = player,
                    Reason = @event.Reason,
                    Value = @event.Value,
                    Musickitid = musicId,
                    Nomusic = muted ? 1 : noMusic,
                };
                newEvent.FireEventToClient(recipient);
                newEvent.Free();
            }

            return HookResult.Continue;
        }

        private HookResult OnGiveNamedItemPost(DynamicHook hook)
        {
            try
            {
                var itemServices = hook.GetParam<CCSPlayer_ItemServices>(0);
                var weapon = hook.GetReturn<CBasePlayerWeapon>();

                // Guard: weapon can be null or invalid if entity was destroyed between frames
                if (weapon == null || !weapon.IsValid)
                    return HookResult.Continue;

                if (!weapon.DesignerName.Contains("weapon"))
                    return HookResult.Continue;

                var player = GetPlayerFromItemServices(itemServices);
                if (player != null)
                {
                    GivePlayerWeaponSkin(player, weapon);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[OnGiveNamedItemPost] Exception: {Message}", ex.Message);
            }

            return HookResult.Continue;
        }

        private void OnEntityCreated(CEntityInstance entity)
        {
            string designerName;
            try
            {
                designerName = entity.DesignerName;
            }
            catch (Exception)
            {
                // Entity system may not be initialized yet during early server startup
                return;
            }

            if (!string.IsNullOrEmpty(designerName) && designerName.Contains("weapon"))
            {
                // Capture entity index (a plain int) instead of the raw Handle pointer.
                // Handle is an IntPtr into native CS2 memory — by the time the callback
                // fires, that memory may already be freed. Reading a stale pointer throws
                // AccessViolationException, which is a Corrupted State Exception in .NET
                // and CANNOT be caught by a normal try/catch, so it crashes the server.
                //
                // Using AddTimer instead of NextWorldUpdate to add a small delay — this
                // gives the entity system time to fully clean up destroyed entities before
                // we try to re-resolve the index. NextWorldUpdate (next tick) is often too
                // fast and the entity pointer table can still hold dangling pointers.
                var entityIndex = entity.Index;

                Server.NextWorldUpdate(() =>
                {
                    CBasePlayerWeapon? weapon = Utilities.GetEntityFromIndex<CBasePlayerWeapon>((int)entityIndex);

                    if (weapon == null || !weapon.IsValid)
                        return;

                    try
                    {
                        // Null safety check for weapon AttributeManager
                        if (weapon.AttributeManager?.Item == null)
                            return;

                        SteamID? steamid = null;

                        if (weapon.OriginalOwnerXuidLow > 0)
                            steamid = new SteamID(weapon.OriginalOwnerXuidLow);

                        CCSPlayerController? player = null;

                        if (steamid != null && steamid.IsValid())
                        {
                            // Use cached lookup instead of linear scan through Players list
                            if (!PlayersBySteamId.TryGetValue(steamid.SteamId64, out player))
                                player = Utilities.GetPlayerFromSteamId(weapon.OriginalOwnerXuidLow);
                        }
                        else
                        {
                            // Separated try/catch — OwnerEntity access can throw if entity is invalid
                            try
                            {
                                if (!weapon.OwnerEntity.IsValid || weapon.OwnerEntity.Index == 0)
                                    return;

                                player = Utilities.GetPlayerFromIndex((int)weapon.OwnerEntity.Index);

                                if (player == null)
                                {
                                    CCSWeaponBaseGun gun = weapon.As<CCSWeaponBaseGun>();
                                    if (gun.IsValid && gun.OwnerEntity.IsValid && gun.OwnerEntity.Value != null)
                                    {
                                        player = Utilities.GetPlayerFromIndex((int)gun.OwnerEntity.Value.Index);
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                return; // OwnerEntity became invalid
                            }
                        }

                        if (string.IsNullOrEmpty(player?.PlayerName))
                            return;
                        if (!Utility.IsPlayerValid(player))
                            return;

                        // Re-validate weapon — it may have been destroyed during player lookups
                        if (!weapon.IsValid)
                            return;

                        GivePlayerWeaponSkin(player, weapon);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "OnEntityCreated apply failed: {Message}", ex.Message);
                    }
                });
            }
        }

        // OnTick removed — was sending PrintToCenterHtml to every player every tick (64-128/s)
        // This was the single most expensive operation across all plugins

        [GameEventHandler]
        public HookResult OnItemPickup(EventItemPickup @event, GameEventInfo _)
        {
            // if (!IsWindows) return HookResult.Continue;
            var player = @event.Userid;
            if (player == null || !player.IsValid || player.IsBot)
                return HookResult.Continue;
            if (!@event.Item.Contains("knife"))
                return HookResult.Continue;

            var weaponDefIndex = (int)@event.Defindex;

            if (!HasChangedKnife(player, out var _) || !HasChangedPaint(player, weaponDefIndex, out var _))
                return HookResult.Continue;

            if (player is { Connected: PlayerConnectedState.Connected, PawnIsAlive: true, PlayerPawn.IsValid: true })
            {
                SchedulePickupRefresh(player);
            }

            return HookResult.Continue;
        }

        // A knife pickup fires once per regive, so spawning, buying, or any kill+regive cycle can
        // deliver several in the same handful of ticks. GiveOnItemPickup walks the player's whole
        // inventory and may run a give+kill dance, so restart a short timer instead of running it
        // per event — the burst collapses into one refresh.
        private void SchedulePickupRefresh(CCSPlayerController player)
        {
            int slot = player.Slot;

            if (_pickupRefreshTimers.Remove(slot, out var pending))
                pending.Kill();

            _pickupRefreshTimers[slot] = AddTimer(
                0.10f,
                () =>
                {
                    _pickupRefreshTimers.Remove(slot);

                    // Re-resolve by slot: the player may have disconnected during the delay, and
                    // the slot may already belong to someone else.
                    var current = Utilities.GetPlayerFromSlot(slot);
                    if (current is { IsValid: true, IsBot: false, Connected: PlayerConnectedState.Connected, PawnIsAlive: true, PlayerPawn.IsValid: true })
                        GiveOnItemPickup(current);
                },
                TimerFlags.STOP_ON_MAPCHANGE
            );
        }

        private void ClearPickupRefreshTimers()
        {
            foreach (var timer in _pickupRefreshTimers.Values)
                timer.Kill();

            _pickupRefreshTimers.Clear();
        }

        private void RegisterListeners()
        {
            RegisterListener<Listeners.OnMapStart>(OnMapStart);

            RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
            RegisterEventHandler<EventRoundStart>(OnRoundStart);
            RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
            RegisterEventHandler<EventRoundMvp>(OnRoundMvp);
            RegisterListener<Listeners.OnEntitySpawned>(OnEntityCreated);
            RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
            RegisterEventHandler<EventRoundPrestart>(OnRoundPrestart);
            RegisterEventHandler<EventBombPlanted>(OnBombPlanted);

            RegisterPetListeners();

            // ShowSkinImage OnTick removed — per-tick HTML sends are too expensive
            // If skin image display is needed in the future, use a 0.5s repeating timer instead

            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
        }
    }
}
