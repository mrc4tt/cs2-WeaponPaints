using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WeaponPaints.Tests;

public class DbWriteQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static DbWriteQueue NewQueue(Task? startAfter = null) => new(NullLogger.Instance, startAfter);

    // A work item that records its label and then blocks until the gate opens.
    private static Func<Task> Blocking(ConcurrentQueue<string> log, string label, Task gate)
        => async () =>
        {
            log.Enqueue(label);
            await gate;
        };

    private static Func<Task> Record(ConcurrentQueue<string> log, string label)
        => () =>
        {
            log.Enqueue(label);
            return Task.CompletedTask;
        };

    [Fact]
    public async Task RunsOnePlayersWritesInOrder()
    {
        var q = NewQueue();
        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        var first = q.Enqueue("1", "a", Blocking(log, "a", gate.Task));
        var second = q.Enqueue("1", "b", Record(log, "b"));

        await Task.Delay(50);
        Assert.Equal(["a"], log);

        gate.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        Assert.Equal(["a", "b"], log);
    }

    [Fact]
    public async Task CoalescesWaitingItemWithSameKey()
    {
        var q = NewQueue();
        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        var head = q.Enqueue("1", "busy", Blocking(log, "busy", gate.Task));
        var stale = q.Enqueue("1", "paints", Record(log, "paints-old"));
        var fresh = q.Enqueue("1", "paints", Record(log, "paints-new"));

        Assert.Same(stale, fresh);

        gate.SetResult();
        await Task.WhenAll(head, fresh).WaitAsync(Timeout);
        Assert.Equal(["busy", "paints-new"], log);
    }

    [Fact]
    public async Task DoesNotCoalesceAcrossALaterItem()
    {
        // Merging into the earlier slot would let the new snapshot overtake "stattrak".
        var q = NewQueue();
        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        q.Enqueue("1", "busy", Blocking(log, "busy", gate.Task));
        q.Enqueue("1", "paints", Record(log, "paints-1"));
        q.Enqueue("1", "stattrak", Record(log, "stattrak"));
        q.Enqueue("1", "paints", Record(log, "paints-2"));

        gate.SetResult();
        await q.DrainAsync().WaitAsync(Timeout);
        Assert.Equal(["busy", "paints-1", "stattrak", "paints-2"], log);
    }

    [Fact]
    public async Task UniqueItemsAreNeverMerged()
    {
        var q = NewQueue();
        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        q.Enqueue("1", "busy", Blocking(log, "busy", gate.Task));
        q.EnqueueUnique("1", "read", Record(log, "read-1"));
        q.EnqueueUnique("1", "read", Record(log, "read-2"));

        gate.SetResult();
        await q.DrainAsync().WaitAsync(Timeout);
        Assert.Equal(["busy", "read-1", "read-2"], log);
    }

    [Fact]
    public async Task FailureIsSwallowedAndDoesNotBlockTheNextWrite()
    {
        var q = NewQueue();
        var log = new ConcurrentQueue<string>();

        var failing = q.Enqueue("1", "a", () => throw new InvalidOperationException("db down"));
        var next = q.Enqueue("1", "b", Record(log, "b"));

        await failing.WaitAsync(Timeout);
        Assert.False(failing.IsFaulted);
        await next.WaitAsync(Timeout);
        Assert.Equal(["b"], log);
    }

    [Fact]
    public async Task PlayersDoNotWaitForEachOther()
    {
        var q = NewQueue();
        var log = new ConcurrentQueue<string>();
        var gate = new TaskCompletionSource();

        var blocked = q.Enqueue("1", "a", Blocking(log, "p1", gate.Task));
        await q.Enqueue("2", "a", Record(log, "p2")).WaitAsync(Timeout);

        Assert.Contains("p2", log);
        Assert.False(blocked.IsCompleted);
        gate.SetResult();
        await blocked.WaitAsync(Timeout);
    }

    [Fact]
    public async Task EmptySteamIdIsIgnored()
    {
        var q = NewQueue();
        var ran = false;
        await q.Enqueue("", "a", () => { ran = true; return Task.CompletedTask; }).WaitAsync(Timeout);
        Assert.False(ran);
    }

    [Fact]
    public async Task FirstWriteWaitsForPreviousInstance()
    {
        // Hot reload: writes handed over from the old plugin instance must land first.
        var previous = new TaskCompletionSource();
        var q = NewQueue(previous.Task);
        var log = new ConcurrentQueue<string>();

        var write = q.Enqueue("1", "a", Record(log, "a"));
        await Task.Delay(50);
        Assert.Empty(log);

        previous.SetResult();
        await write.WaitAsync(Timeout);
        Assert.Equal(["a"], log);
    }

    [Fact]
    public async Task DrainWaitsForEverything()
    {
        var q = NewQueue();
        var gate = new TaskCompletionSource();
        q.Enqueue("1", "a", () => gate.Task);
        q.Enqueue("2", "a", () => gate.Task);

        var drain = q.DrainAsync();
        await Task.Delay(50);
        Assert.False(drain.IsCompleted);

        gate.SetResult();
        await drain.WaitAsync(Timeout);
    }
}
