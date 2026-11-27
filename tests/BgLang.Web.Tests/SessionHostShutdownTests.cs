using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class SessionHostShutdownTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Cancelled_stop_rejects_work_but_retains_the_session_until_the_active_response_finishes(bool cancelBeforeStop)
    {
        (string manifest, string hash) = Package();
        BulgarianLanguageSession? session = null;
        await using var host = new SessionHost(manifest, hash, () => session = BulgarianLanguageSession.Open(manifest));
        await host.StartAsync(CancellationToken.None);
        Assert.IsTrue(host.Ready);
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        int pendingRan = 0;
        Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (result, _) =>
        {
            Assert.IsTrue(((IReadOnlyList<WordReading>)result.Data).Count > 0);
            entered.SetResult();
            await release.Task;
            Assert.AreEqual(hash.ToLowerInvariant(), session!.Provenance.ManifestSha256.ToLowerInvariant(),
                "The active response must retain a usable session through its final callback instruction.");
        }, CancellationToken.None);
        Task? pending = null;
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            pending = host.ExecuteResponseAsync("lookup", new("книга"), (_, _) =>
            { Interlocked.Increment(ref pendingRan); return Task.CompletedTask; }, CancellationToken.None);
            if (cancelBeforeStop) cancellation.Cancel();
            Task stopping = host.StopAsync(cancellation.Token);
            AssertNotReady(host);
            if (!cancelBeforeStop)
            {
                Assert.IsFalse(stopping.IsCompleted, "A live stop token must keep waiting for the held response.");
                cancellation.Cancel();
            }
            await stopping.WaitAsync(TestTimeout);
            await host.StopAsync(cancellation.Token).WaitAsync(TestTimeout);
            Assert.IsFalse(active.IsCompleted, "Ending StopAsync must not abandon the active response.");
            Assert.AreEqual(hash.ToLowerInvariant(), session!.Provenance.ManifestSha256.ToLowerInvariant());
            Task disposing = host.DisposeAsync().AsTask();
            Assert.IsFalse(disposing.IsCompleted, "Disposal must keep waiting while the callback owns the session.");
            Assert.AreEqual(0, Volatile.Read(ref pendingRan));
            release.SetResult();
            await active.WaitAsync(TestTimeout);
            ApiRequestException rejected = await Assert.ThrowsAsync<ApiRequestException>(() => pending!.WaitAsync(TestTimeout));
            Assert.AreEqual(503, rejected.Status);
            Assert.AreEqual("queue-expired", rejected.Code);
            await disposing.WaitAsync(TestTimeout);
            Assert.AreEqual(0, pendingRan, "Queued work must never start after shutdown closes admission.");
            Assert.Throws<ObjectDisposedException>(() => _ = session!.Provenance);
            await host.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        }
        finally
        {
            release.TrySetResult();
            await active.WaitAsync(TestTimeout);
            if (pending is not null)
                try { await pending.WaitAsync(TestTimeout); } catch (ApiRequestException) { }
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Uncancelled_stop_waits_for_the_active_response_and_disposal_remains_explicit()
    {
        (string manifest, string hash) = Package();
        BulgarianLanguageSession? session = null;
        await using var host = new SessionHost(manifest, hash, () => session = BulgarianLanguageSession.Open(manifest));
        await host.StartAsync(CancellationToken.None);
        var entered = Signal(); var release = Signal();
        Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (_, _) =>
        { entered.SetResult(); await release.Task; }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            Task stopping = host.StopAsync(CancellationToken.None);
            AssertNotReady(host);
            Assert.IsFalse(stopping.IsCompleted, "Normal shutdown must drain the actual response callback.");
            release.SetResult();
            await active.WaitAsync(TestTimeout);
            await stopping.WaitAsync(TestTimeout);
            Assert.AreEqual(hash.ToLowerInvariant(), session!.Provenance.ManifestSha256.ToLowerInvariant(),
                "StopAsync closes execution; DisposeAsync owns release of the session.");
            await host.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            await host.DisposeAsync().AsTask().WaitAsync(TestTimeout);
            Assert.Throws<ObjectDisposedException>(() => _ = session!.Provenance);
        }
        finally { release.TrySetResult(); await active.WaitAsync(TestTimeout); }
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Response_failure_after_cancelled_stop_still_reaches_its_original_caller()
    {
        (string manifest, string hash) = Package();
        await using var host = new SessionHost(manifest, hash);
        await host.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal();
        var failure = new InvalidOperationException("Held response failed after the host deadline.");
        Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (_, _) =>
        { entered.SetResult(); await release.Task; throw failure; }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TestTimeout);
            Task stopping = host.StopAsync(cancellation.Token);
            Assert.IsFalse(stopping.IsCompleted);
            cancellation.Cancel();
            await stopping.WaitAsync(TestTimeout);
            AssertNotReady(host);
            Assert.IsFalse(active.IsCompleted);
            release.SetResult();
            InvalidOperationException observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => active.WaitAsync(TestTimeout));
            Assert.AreSame(failure, observed, "Shutdown cancellation must not replace the response failure.");
            await host.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        }
        finally
        {
            release.TrySetResult();
            try { await active.WaitAsync(TestTimeout); } catch (InvalidOperationException error) when (ReferenceEquals(error, failure)) { }
        }
    }

    [TestMethod]
    public async Task Stop_before_start_and_repeated_disposal_do_not_open_a_session()
    {
        await using var host = new SessionHost("unused-manifest.json", new string('0', 64),
            () => throw new AssertFailedException("Stopping an unstarted host must not open the package."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await host.StopAsync(cancellation.Token).WaitAsync(TestTimeout);
        await host.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        AssertNotReady(host);
        await host.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await host.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    private static void AssertNotReady(SessionHost host)
    {
        Assert.IsFalse(host.Ready);
        foreach (Action request in new Action[]
        {
            () => { _ = host.ExecuteAsync("lookup", new("книга"), CancellationToken.None); },
            () => { _ = host.ExecuteResponseAsync("lookup", new("книга"), (_, _) => Task.CompletedTask, CancellationToken.None); },
            () => { _ = host.AnalyzeSyntaxAsync(1, new("книга"), CancellationToken.None); }
        })
        {
            ApiRequestException error = Assert.Throws<ApiRequestException>(request);
            Assert.AreEqual(503, error.Status);
            Assert.AreEqual("not-ready", error.Code);
        }
    }

    private static (string Manifest, string Hash) Package()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete package is required.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        return (manifest, hash);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
