using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace WeaponPaints
{
    public partial class WeaponPaints
    {
        private bool _mvpPlayed;

        [GameEventHandler]
        public HookResult OnClientFullConnect(EventPlayerConnectFull @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;

            if (
                player is null
                || !player.IsValid
                || player.IsBot
                || WeaponSync == null
                || Database == null
            )
                return HookResult.Continue;

            var playerInfo = new PlayerInfo
            {
                UserId = player.UserId,
                Slot = player.Slot,
                Index = (int)player.Index,
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                IpAddress = player.IpAddress?.Split(":")[0],
            };

            try
            {
                _ = Task.Run(async () =>
                {
                    await WeaponSync.GetPlayerData(playerInfo);

                    // After DB data is loaded, schedule a knife refresh on the main thread.
                    // On quick reconnect, the player may have already spawned and received a
                    // vanilla knife before GetPlayerData completed (race condition).
                    // We kill the existing knife and regive it so that both OnGiveNamedItemPost
                    // and OnEntityCreated fire naturally — the dual-event mechanism ensures
                    // SubclassChange happens on the first call and paint sticks on the second.
                    Server.NextWorldUpdate(() =>
                    {
                        if (player == null || !player.IsValid || !player.PawnIsAlive)
                            return;
                        if (player.PlayerPawn.Value?.WeaponServices == null)
                            return;
                        if (!Config.Additional.KnifeEnabled)
                            return;
                        if (!HasChangedKnife(player, out var knifeValue))
                            return;

                        // Check if the knife already has the correct type applied (normal connect)
                        // Only kill+regive if it's still a default knife (reconnect race condition)
                        var desiredDefIndex = WeaponDefindex.FirstOrDefault(x =>
                            x.Value == knifeValue
                        );
                        if (desiredDefIndex.Key == 0)
                            return;

                        bool needsRefresh = false;

                        // Find and kill the player's current knife
                        foreach (var weaponHandle in player.PlayerPawn.Value.WeaponServices.MyWeapons)
                        {
                            if (!weaponHandle.IsValid || weaponHandle.Value == null || !weaponHandle.Value.IsValid)
                                continue;

                            var designerName = weaponHandle.Value.DesignerName;
                            if (designerName.Contains("knife") || designerName.Contains("bayonet"))
                            {
                                // If defindex already matches desired knife, skin was applied normally
                                if (weaponHandle.Value.AttributeManager?.Item != null &&
                                    weaponHandle.Value.AttributeManager.Item.ItemDefinitionIndex == (ushort)desiredDefIndex.Key)
                                    break;

                                needsRefresh = true;
                                weaponHandle.Value.AddEntityIOEvent("Kill", weaponHandle.Value, null, "", 0.01f);
                                break;
                            }
                        }

                        // Give a new knife — OnGiveNamedItemPost + OnEntityCreated will handle skin
                        if (needsRefresh)
                        {
                            AddTimer(0.1f, () =>
                            {
                                if (player == null || !player.IsValid || !player.PawnIsAlive)
                                    return;
                                player.GiveNamedItem(CsItem.Knife);
                            });
                        }
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

            var playerInfo = new PlayerInfo
            {
                UserId = player.UserId,
                Slot = player.Slot,
                Index = (int)player.Index,
                SteamId = player.SteamID.ToString(),
                Name = player.PlayerName,
                IpAddress = player.IpAddress?.Split(":")[0],
            };

            try
            {
                Task.Run(async () =>
                {
                    if (WeaponSync != null)
                        await WeaponSync.SyncStatTrakToDatabase(playerInfo);

                    if (Config.Additional.SkinEnabled)
                    {
                        GPlayerWeaponsInfo.TryRemove(player.Slot, out _);
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError($"[OnPlayerDisconnect] Error syncing StatTrak: {ex.Message}");
            }

            if (Config.Additional.KnifeEnabled)
            {
                GPlayersKnife.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.GloveEnabled)
            {
                GPlayersGlove.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.AgentEnabled)
            {
                GPlayersAgent.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.MusicEnabled)
            {
                GPlayersMusic.TryRemove(player.Slot, out _);
            }
            if (Config.Additional.PinsEnabled)
            {
                GPlayersPin.TryRemove(player.Slot, out _);
            }

            _temporaryPlayerWeaponWear.TryRemove(player.Slot, out _);
            CommandsCooldown.Remove(player.Slot);
            PlayersBySteamId.TryRemove(player.SteamID, out _);
            Players.Remove(player);

            return HookResult.Continue;
        }

        private void OnMapStart(string mapName)
        {
            if (
                Config.Additional is
                { KnifeEnabled: false, SkinEnabled: false, GloveEnabled: false }
            )
                return;

            // Initialize WeaponSync if not already done (e.g., if OnConfigParsed ran before map start)
            if (Database != null && WeaponSync == null)
                WeaponSync = new WeaponSynchronization(Database, Config);

            _fadeSeed = 0;
            _nextItemId = MinimumCustomItemId;
        }

        private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Userid;

            if (
                player is null
                || !player.IsValid
                || player.IsBot
                || Config.Additional is { KnifeEnabled: false, GloveEnabled: false }
            )
                return HookResult.Continue;

            // Defer ALL cosmetic application — OnPlayerSpawn fires during team assignment
            // when the pawn's native scene node, body component, and model infrastructure
            // are not yet fully initialized. Applying cosmetics synchronously or even on
            // NextFrame crashes in native SetModel/SetBodygroup calls.
            AddTimer(0.15f, () =>
            {
                if (player == null || !player.IsValid || !player.PawnIsAlive)
                    return;
                if (player.Team is CsTeam.None or CsTeam.Spectator)
                    return;

                CCSPlayerPawn? pawn = player.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid)
                    return;

                GivePlayerMusicKit(player);
                GivePlayerAgent(player);
                GivePlayerGloves(player);
                GivePlayerPin(player);
            }, TimerFlags.STOP_ON_MAPCHANGE);

            return HookResult.Continue;
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

        private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
        {
            if (_mvpPlayed)
                return HookResult.Continue;

            var player = @event.Userid;

            if (player == null || !player.IsValid || player.IsBot)
                return HookResult.Continue;

            // Ensure player is on a valid team
            if (player.Team is CsTeam.None or CsTeam.Spectator)
                return HookResult.Continue;

            if (
                !(
                    GPlayersMusic.TryGetValue(player.Slot, out var musicInfo)
                    && musicInfo.TryGetValue(player.Team, out var musicId)
                    && musicId != 0
                )
            )
                return HookResult.Continue;

            @event.Musickitid = musicId;
            @event.Nomusic = 0;
            info.DontBroadcast = true;

            var newEvent = new EventRoundMvp(true)
            {
                Userid = player,
                Musickitid = musicId,
                Nomusic = 0,
            };

            _mvpPlayed = true;

            newEvent.FireEvent(false);
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
                Logger.LogError($"[OnGiveNamedItemPost] Exception: {ex.Message}");
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

            if (designerName.Contains("weapon"))
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
                                    if (gun.OwnerEntity.IsValid && gun.OwnerEntity.Value != null)
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
                    catch (Exception) { }
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

            if (
                !HasChangedKnife(player, out var _)
                || !HasChangedPaint(player, weaponDefIndex, out var _)
            )
                return HookResult.Continue;

            if (
                player is
                {
                    Connected: PlayerConnectedState.PlayerConnected,
                    PawnIsAlive: true,
                    PlayerPawn.IsValid: true
                }
            )
            {
                GiveOnItemPickup(player);
            }

            return HookResult.Continue;
        }

        private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
        {
            CCSPlayerController? player = @event.Attacker;
            CCSPlayerController? victim = @event.Userid;

            if (player is null || !player.IsValid)
                return HookResult.Continue;

            if (victim == null || !victim.IsValid || victim == player)
                return HookResult.Continue;

            CBasePlayerWeapon? weapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;

            if (weapon == null)
                return HookResult.Continue;

            int weaponDefIndex = weapon.AttributeManager.Item.ItemDefinitionIndex;

            if (!HasChangedPaint(player, weaponDefIndex, out var weaponInfo) || weaponInfo == null)
                return HookResult.Continue;

            if (!weaponInfo.StatTrak)
                return HookResult.Continue;

            weaponInfo.StatTrakCount += 1;

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

            return HookResult.Continue;
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

            // ShowSkinImage OnTick removed — per-tick HTML sends are too expensive
            // If skin image display is needed in the future, use a 0.5s repeating timer instead

            VirtualFunctions.GiveNamedItemFunc.Hook(OnGiveNamedItemPost, HookMode.Post);
        }
    }
}
