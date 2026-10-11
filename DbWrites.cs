using System.Collections.Concurrent;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace WeaponPaints;

// Game-thread side of the database write path: snapshot what changed, hand it to the per-player
// DbWriteQueue. Every helper here must be called on the game thread (it reads GPlayer* state and
// the controller); the queued work only ever sees the snapshot.
public partial class WeaponPaints
{
    // Writes still in flight when the previous plugin instance unloaded (hot reload). Handed over
    // through the AppDomain because statics die with the old plugin's load context.
    private const string PendingWritesKey = "WeaponPaints.PendingWrites";

    private DbWriteQueue? _dbQueue;

    internal DbWriteQueue DbQueue => _dbQueue ??= CreateDbQueue();

    private DbWriteQueue CreateDbQueue()
    {
        var previous = AppDomain.CurrentDomain.GetData(PendingWritesKey) as Task;
        AppDomain.CurrentDomain.SetData(PendingWritesKey, null);
        return new DbWriteQueue(Logger, previous);
    }

    internal static CsTeam[] TeamsFor(CCSPlayerController player)
        => player.TeamNum < 2 ? [CsTeam.Terrorist, CsTeam.CounterTerrorist] : [player.Team];

    // Queue a write for this player. `key` coalesces: a newer write with the same key replaces one
    // that hasn't started yet, so pass the same key only for writes that fully supersede each other.
    internal Task QueueDb(PlayerInfo playerInfo, string key, Func<WeaponSynchronization, Task> work)
    {
        var sync = WeaponSync;
        if (sync == null || string.IsNullOrEmpty(playerInfo.SteamId))
            return Task.CompletedTask;
        return DbQueue.Enqueue(playerInfo.SteamId, key, () => work(sync));
    }

    // Reload a player's rows behind any write still queued for them, so the reload can't read a row
    // the player changed a moment ago and then overwrite the newer in-memory value with it.
    internal Task QueueGetPlayerData(PlayerInfo playerInfo)
    {
        var sync = WeaponSync;
        if (sync == null)
            return Task.CompletedTask;
        if (string.IsNullOrEmpty(playerInfo.SteamId))
            return sync.GetPlayerData(playerInfo);
        return DbQueue.EnqueueUnique(playerInfo.SteamId, "load", () => sync.GetPlayerData(playerInfo));
    }

    private static List<WeaponSynchronization.WeaponRow> SnapshotWeaponRows(int slot, int weaponDefIndex, CsTeam[] teams)
    {
        var rows = new List<WeaponSynchronization.WeaponRow>(teams.Length);
        if (!GPlayerWeaponsInfo.TryGetValue(slot, out var teamWeapons))
            return rows;

        foreach (var team in teams)
        {
            if (teamWeapons.TryGetValue(team, out var weapons) && weapons.TryGetValue(weaponDefIndex, out var info))
                rows.Add(new WeaponSynchronization.WeaponRow(team, weaponDefIndex, info.Clone()));
        }
        return rows;
    }

    // Full-row write (paint, wear, seed, nametag, StatTrak, stickers, charm) of one weapon.
    internal void QueueWeaponRowWrite(CCSPlayerController player, int weaponDefIndex, CsTeam[]? teams = null)
    {
        var playerInfo = PlayerInfo.From(player);
        var rows = SnapshotWeaponRows(player.Slot, weaponDefIndex, teams ?? TeamsFor(player));
        if (rows.Count == 0)
            return;
        QueueDb(playerInfo, $"row:{weaponDefIndex}", sync => sync.SyncWeaponRowsToDatabase(playerInfo, rows));
    }

    // Paint/wear/seed of every weapon the player has (the old SyncWeaponPaintsToDatabase scope).
    internal void QueuePaintsWrite(CCSPlayerController player)
    {
        var playerInfo = PlayerInfo.From(player);
        var rows = new List<WeaponSynchronization.PaintRow>();
        if (GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamWeapons))
        {
            foreach (var (team, weapons) in teamWeapons)
            {
                foreach (var (defIndex, info) in weapons)
                    rows.Add(new WeaponSynchronization.PaintRow(team, defIndex, info.Paint, info.Wear, info.Seed));
            }
        }

        if (rows.Count == 0)
            return;
        QueueDb(playerInfo, "paints", sync => sync.SyncWeaponPaintsToDatabase(playerInfo, rows));
    }

    // Record the weapon's current (paint, wear, seed) as the remembered values for that paint.
    internal void RememberPaint(CCSPlayerController player, int weaponDefIndex, CsTeam[]? teams = null)
    {
        if (!Config.Additional.PaintMemoryEnabled || !GPlayerWeaponsInfo.TryGetValue(player.Slot, out var teamWeapons))
            return;

        var memory = GPlayersPaintMemory.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<(CsTeam, int, int), (float, int)>());
        var rows = new List<WeaponSynchronization.PaintMemoryRow>(2);
        foreach (var team in teams ?? TeamsFor(player))
        {
            if (!teamWeapons.TryGetValue(team, out var weapons) || !weapons.TryGetValue(weaponDefIndex, out var info) || info.Paint == 0)
                continue;
            memory[(team, weaponDefIndex, info.Paint)] = (info.Wear, info.Seed);
            rows.Add(new WeaponSynchronization.PaintMemoryRow(team, weaponDefIndex, info.Paint, info.Wear, info.Seed));
        }

        if (rows.Count == 0)
            return;
        var playerInfo = PlayerInfo.From(player);
        var paint = rows[0].Paint;
        QueueDb(playerInfo, $"mem:{weaponDefIndex}:{paint}", sync => sync.SyncPaintMemoryToDatabase(playerInfo, rows));
    }

    // Remembered (wear, seed) for this paint, if the player had it before.
    internal static bool TryRecallPaint(int slot, CsTeam team, int weaponDefIndex, int paint, out (float Wear, int Seed) values)
    {
        values = default;
        return GPlayersPaintMemory.TryGetValue(slot, out var memory) && memory.TryGetValue((team, weaponDefIndex, paint), out values);
    }

    // Unload: flush StatTrak, then hand whatever is still queued to the next instance (hot reload)
    // or wait for it briefly (unload / server shutdown — nothing will pick it up afterwards).
    private void FlushWritesOnUnload(bool hotReload)
    {
        if (_dbQueue == null && _statTrakDirty.Count == 0)
            return;

        try
        {
            FlushStatTrak(slotFilter: null);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("StatTrak flush on unload failed: {Message}", ex.Message);
        }

        var drain = DbQueue.DrainAsync();
        if (drain.IsCompleted)
            return;

        if (hotReload)
        {
            AppDomain.CurrentDomain.SetData(PendingWritesKey, drain);
            return;
        }

        try
        {
            if (!drain.Wait(TimeSpan.FromSeconds(2)))
                Logger.LogWarning("Some database writes were still running when WeaponPaints unloaded");
        }
        catch (Exception ex)
        {
            Logger.LogWarning("Waiting for database writes on unload failed: {Message}", ex.Message);
        }
    }
}
