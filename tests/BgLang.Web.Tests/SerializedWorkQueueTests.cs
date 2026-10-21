using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class SerializedWorkQueueTests
{
    [TestMethod]
    public async Task Disconnected_waiter_does_not_release_running_work_or_admit_parallel_execution()
    {
        await using var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(10));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        int secondRan = 0;
        Task<object> first = queue.RunAsync(() =>
        {
            entered.SetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)), "Test worker was not released.");
            return 1;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);
            Task<object> second = queue.RunAsync(() => { Interlocked.Increment(ref secondRan); return 2; }, CancellationToken.None);
            ApiRequestException full = await Assert.ThrowsAsync<ApiRequestException>(async () =>
                await queue.RunAsync(() => 3, CancellationToken.None));
            Assert.AreEqual(503, full.Status);
            Assert.AreEqual("queue-full", full.Code);
            Assert.AreEqual(0, Volatile.Read(ref secondRan));
            Assert.IsFalse(second.IsCompleted);
            release.Set();
            Assert.AreEqual(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, secondRan);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Queue_wait_expires_while_worker_is_busy_and_expired_item_never_executes()
    {
        await using var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(1));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int expiredRan = 0;
        Task<object> active = queue.RunAsync(() =>
        {
            entered.SetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            return 1;
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ApiRequestException expired = await Assert.ThrowsAsync<ApiRequestException>(async () =>
                await queue.RunAsync(() => { expiredRan++; return 2; }, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(503, expired.Status);
            Assert.AreEqual("queue-expired", expired.Code);
            Assert.IsFalse(active.IsCompleted, "Expiry must be independent of completion of active work.");
            release.Set();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
            queue.Close();
            await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, expiredRan);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task Shutdown_rejects_pending_work_and_waits_for_actual_execution()
    {
        var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(10));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int pendingRan = 0;
        Task<object> active = queue.RunAsync(() =>
        {
            entered.SetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)));
            return 1;
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<object> pending = queue.RunAsync(() => { pendingRan++; return 2; }, CancellationToken.None);
            Task disposing = queue.DisposeAsync().AsTask();
            Assert.IsFalse(disposing.IsCompleted, "Shutdown must not release resources used by active work.");
            ApiRequestException stopped = await Assert.ThrowsAsync<ApiRequestException>(async () =>
                await queue.RunAsync(() => 3, CancellationToken.None));
            Assert.AreEqual(503, stopped.Status);
            release.Set();
            Assert.AreEqual(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ApiRequestException>(async () => await pending);
            Assert.AreEqual(0, pendingRan);
        }
        finally { release.Set(); await queue.DisposeAsync(); }
    }
}
