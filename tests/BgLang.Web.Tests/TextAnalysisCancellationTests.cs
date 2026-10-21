using System.Diagnostics;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class TextAnalysisCancellationTests
{
    [TestMethod, TestCategory("FullPackage")]
    [DataRow("analyze-text")]
    [DataRow("find-forms-in-text")]
    public async Task Cancelled_text_work_finishes_before_the_response_callback_and_releases_the_session_lane(string operation)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete package is required.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        string dense = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dense-unknown-text.v1.txt"));
        Assert.AreEqual(4079, dense.Length);
        Assert.AreEqual(288, dense.Split(' ').Distinct(StringComparer.Ordinal).Count());
        await using var host = new SessionHost(manifest, hash);
        await host.StartAsync(CancellationToken.None);
        // Warm the worker independently of the cancelled request.
        await host.ExecuteResponseAsync("lookup", new("книга"), (_, _) => Task.CompletedTask, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        bool responded = false;
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => host.ExecuteResponseAsync(operation,
            new(dense, Query: operation == "find-forms-in-text" ? "чатбот" : null),
            (_, _) => { responded = true; return Task.CompletedTask; }, cancellation.Token));
        watch.Stop();
        Assert.IsFalse(responded, "A cancelled linguistic result cannot start a success response.");
        Assert.IsTrue(cancellation.IsCancellationRequested);
        ApiEnvelope? recovered = null;
        await host.ExecuteResponseAsync("analyze-word", new("книга"), (result, _) =>
        { recovered = result; return Task.CompletedTask; }, CancellationToken.None);
        Assert.IsTrue(((WordAnalysis)recovered!.Data).IsKnown);
        Console.WriteLine($"{operation}: cooperative work completed after {watch.Elapsed.TotalMilliseconds:F1} ms; successor completed in the same serialized lane.");
        await host.StopAsync(CancellationToken.None);
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Already_cancelled_text_and_query_requests_do_not_respond_or_poison_a_later_request()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete package is required.");
        await using var host = new SessionHost(manifest, hash);
        await host.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (string operation in new[] { "analyze-text", "find-forms-in-text" })
            await Assert.ThrowsAsync<OperationCanceledException>(() => host.ExecuteResponseAsync(operation,
                new("чатботовете", Query: operation == "find-forms-in-text" ? "чатбот" : null),
                (_, _) => throw new AssertFailedException("Cancelled work must not respond."), cancellation.Token));
        await host.ExecuteResponseAsync("lookup", new("книга"), (_, _) => Task.CompletedTask, CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }
}
