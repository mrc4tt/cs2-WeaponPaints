using Microsoft.Extensions.Logging;

namespace WeaponPaints;

// Per-player ordered, coalescing database write queue.
//
// Every write for one SteamID runs strictly after the previous one, so two fast menu clicks can no
// longer land in the database in the wrong order, and a reload (GetPlayerData) queued behind a
// write sees that write. A write enqueued under a key that is still waiting to run replaces the
// waiting one (the newest snapshot wins, the stale one never touches the database). At most
// MaxConcurrent writes run at once across all players, so a 64-player connect storm can't drain
// the connection pool. Nothing here runs on the game thread.
internal sealed class DbWriteQueue
{
    private const int MaxConcurrent = 8;

    private readonly SemaphoreSlim _concurrency = new(MaxConcurrent, MaxConcurrent);
    private readonly object _lock = new();
    private readonly Dictionary<string, Task> _tails = new();
    private readonly Dictionary<(string SteamId, string Key), Pending> _pending = new();
    private readonly Task _startAfter;
    private readonly ILogger _logger;
    private long _uniqueKey;

    private sealed class Pending
    {
        public required (string SteamId, string Key) PendingKey;
        public Func<Task> Work = null!;
        public Task Completion = null!;
    }

    // A dead DB call in the previous instance must not stall every player of the new one forever.
    private static readonly TimeSpan StartAfterTimeout = TimeSpan.FromSeconds(10);

    // startAfter: writes the previous plugin instance still had in flight (hot reload). Every
    // player's first queued item waits for them, so a reload never reads rows older than them.
    internal DbWriteQueue(ILogger logger, Task? startAfter)
    {
        _logger = logger;
        _startAfter = startAfter == null || startAfter.IsCompleted
            ? Task.CompletedTask
            : Task.WhenAny(startAfter, Task.Delay(StartAfterTimeout));
    }

    // Queue `work` for `steamId`. Returns a task that completes once the (possibly coalesced)
    // work has run; it never faults — failures are logged here.
    //
    // Coalescing only happens while the waiting item is still the player's LAST queued item.
    // Merging into an earlier slot would let the newer write overtake writes queued after that
    // slot (e.g. a StatTrak flush), breaking the per-player order.
    internal Task Enqueue(string steamId, string key, Func<Task> work)
    {
        if (string.IsNullOrEmpty(steamId))
            return Task.CompletedTask;

        lock (_lock)
        {
            var pendingKey = (steamId, key);
            if (_pending.TryGetValue(pendingKey, out var waiting)
                && _tails.TryGetValue(steamId, out var currentTail) && currentTail == waiting.Completion)
            {
                waiting.Work = work;
                return waiting.Completion;
            }

            // A superseded waiting item (if any) still runs in its own slot with its own snapshot;
            // this newer one runs after it.
            var pending = new Pending { PendingKey = pendingKey, Work = work };
            _pending[pendingKey] = pending;

            var tail = _tails.TryGetValue(steamId, out var t) ? t : _startAfter;
            var next = tail.ContinueWith(_ => RunAsync(pending), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            pending.Completion = next;
            _tails[steamId] = next;

            next.ContinueWith(
                _ =>
                {
                    lock (_lock)
                    {
                        if (_tails.TryGetValue(steamId, out var current) && current == next)
                            _tails.Remove(steamId);
                    }
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            return next;
        }
    }

    // Queue work that must never be merged with another item (reads, StatTrak deltas).
    internal Task EnqueueUnique(string steamId, string label, Func<Task> work)
        => Enqueue(steamId, $"{label}#{Interlocked.Increment(ref _uniqueKey)}", work);

    private async Task RunAsync(Pending pending)
    {
        Func<Task> work;
        var pendingKey = pending.PendingKey;
        lock (_lock)
        {
            // Started: no longer open for coalescing.
            if (_pending.TryGetValue(pendingKey, out var current) && current == pending)
                _pending.Remove(pendingKey);
            work = pending.Work;
        }

        await _concurrency.WaitAsync().ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Database write '{Key}' failed: {Message}", pendingKey.Key, ex.Message);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    // Completes when everything queued so far has run.
    internal Task DrainAsync()
    {
        lock (_lock)
        {
            return _tails.Count == 0 ? Task.CompletedTask : Task.WhenAll(_tails.Values.ToArray());
        }
    }
}
