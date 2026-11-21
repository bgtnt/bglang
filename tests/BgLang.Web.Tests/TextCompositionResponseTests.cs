using System.Collections;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class TextCompositionResponseTests
{
    [TestMethod]
    [DataRow("raw-v1")][DataRow("shared-donors-v1")][DataRow("shared-analyses-v1")]
    public void Every_format_reconstructs_all_eleven_fields_duplicate_rows_and_reference35(string format)
    {
        foreach (bool enabled in new[] { false, true })
        foreach (bool nested in new[] { false, true })
        {
            TextAnalysis text = Create(enabled);
            object data = nested ? TextMatchProjection.Find(text.Tokens[1].Analysis!, text) : text;
            ApiEnvelope raw = Envelope(data);
            using JsonDocument encoded = JsonDocument.Parse(Encode(raw, format));
            using JsonDocument restored = Restore(encoded);
            using JsonDocument expected = JsonDocument.Parse(JsonSerializer.Serialize(raw, HttpContract.Json));
            Assert.IsTrue(SharedDonorResponseTests.SameJson(expected.RootElement, restored.RootElement),
                "Whole envelope comparison must not filter any old or new Text field.");
            JsonElement value = restored.RootElement.GetProperty("data");
            AssertWholeText(nested ? value.GetProperty("textAnalysis") : value, enabled);
            if (format == SharedDonorResponse.AnalysisFormat)
            {
                Assert.AreEqual(1, encoded.RootElement.GetProperty("wordAnalyses").GetArrayLength());
                JsonElement projected = encoded.RootElement.GetProperty("data");
                if (nested) projected = projected.GetProperty("textAnalysis");
                CollectionAssert.AreEqual(new[] { 0, 0 }, projected.GetProperty("tokens").EnumerateArray()
                    .Where(token => token.GetProperty("kind").GetString() == "Word")
                    .Select(token => token.GetProperty("analysisRef").GetInt32()).ToArray());
                Assert.AreEqual(32, encoded.RootElement.GetProperty("wordAnalyses")[0].GetProperty("readings").GetArrayLength());
            }
        }
    }

    [TestMethod]
    [DataRow("1", null)][DataRow("2", null)]
    [DataRow("1", "raw-v1")][DataRow("2", "raw-v1")]
    [DataRow("1", "shared-donors-v1")][DataRow("2", "shared-donors-v1")]
    [DataRow("1", "shared-analyses-v1")][DataRow("2", "shared-analyses-v1")]
    public async Task Whole_response_budget_counts_new_fields_before_headers_and_keeps_defaults(string version, string? format)
    {
        ApiEnvelope raw = Envelope(Create(true), version);
        byte[] expected = Encode(raw, format);
        using var rejectedBody = new MemoryStream();
        var rejected = new DefaultHttpContext(); rejected.Response.Body = rejectedBody;
        ApiRequestException error = await Assert.ThrowsAsync<ApiRequestException>(() =>
            new ResponseWriter(expected.Length - 1).WriteAsync(rejected, raw, format, CancellationToken.None));
        Assert.AreEqual(413, error.Status); Assert.AreEqual("response-too-large", error.Code);
        Assert.AreEqual(0L, rejectedBody.Length); Assert.IsFalse(rejected.Response.HasStarted);
        Assert.IsNull(rejected.Response.ContentType); Assert.IsNull(rejected.Response.ContentLength);
        foreach (long limit in new[] { (long)expected.Length, expected.Length + 1L })
        {
            using var body = new MemoryStream(); var context = new DefaultHttpContext(); context.Response.Body = body;
            await new ResponseWriter(limit).WriteAsync(context, raw, format, CancellationToken.None);
            CollectionAssert.AreEqual(expected, body.ToArray());
            Assert.AreEqual((long)expected.Length, context.Response.ContentLength);
            Assert.IsTrue(body.CanWrite);
            using JsonDocument encoded = JsonDocument.Parse(body.ToArray());
            using JsonDocument restored = Restore(encoded);
            AssertWholeText(restored.RootElement.GetProperty("data"), true);
        }
    }

    [TestMethod]
    public async Task Cancelled_new_text_and_new_lane_preflight_never_start_success_body()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        using var body = new MemoryStream(); var context = new DefaultHttpContext(); context.Response.Body = body;
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ResponseWriter(1_000_000)
            .WriteAsync(context, Envelope(Create(true)), SharedDonorResponse.AnalysisFormat, before.Token));
        Assert.AreEqual(0L, body.Length); Assert.IsNull(context.Response.ContentLength);
        using var during = new CancellationTokenSource();
        TextAnalysis text = Create(true);
        text = text with { Spelling = text.Spelling! with { Words = new CancelList<SpellingWordEvidence>(text.Spelling.Words, during) } };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ResponseWriter(1_000_000)
            .WriteAsync(context, Envelope(text), HttpContract.RawFormat, during.Token));
        Assert.IsTrue(during.IsCancellationRequested);
        Assert.AreEqual(0L, body.Length); Assert.IsFalse(context.Response.HasStarted);
        Assert.IsNull(context.Response.ContentLength);
    }

    [TestMethod]
    public async Task Partial_new_text_transfer_drains_before_abort_and_successor_keeps_all_fields()
    {
        await using var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var started = new StartedResponse();
        using var body = new GatedBody(() => started.Started = true);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(started); context.Response.Body = body;
        var lifetime = new AbortCapture(); context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        ApiEnvelope raw = Envelope(Create(true));
        int expectedLength = Encode(raw, HttpContract.RawFormat).Length;
        Assert.IsTrue(expectedLength > 16 * 1024, "Independent36-row fixture must reach a second serializer flush.");
        Task<object> active = queue.RunResponseAsync(async () =>
        {
            await new ResponseWriter(expectedLength).WriteAsync(context, raw, HttpContract.RawFormat, cancellation.Token);
            return raw;
        }, cancellation.Token);
        Task request = HandleAsync();
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(context.Response.HasStarted);
            byte[] prefix = body.ToArray(); Assert.IsTrue(prefix.Length > 0 && prefix.Length < expectedLength);
            using var nextBody = new MemoryStream(); var nextContext = new DefaultHttpContext(); nextContext.Response.Body = nextBody;
            int nextRan = 0;
            Task<object> next = queue.RunResponseAsync(async () =>
            {
                nextRan++;
                await new ResponseWriter(expectedLength).WriteAsync(nextContext, raw, HttpContract.RawFormat, CancellationToken.None);
                return 2;
            }, CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TimeoutException>(async () => await request.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(active.IsCompleted); Assert.IsFalse(next.IsCompleted); Assert.AreEqual(0, nextRan);
            Assert.IsFalse(lifetime.Aborted);
            body.Release.TrySetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(lifetime.Aborted);
            CollectionAssert.AreEqual(prefix, body.ToArray(), "No error tail or fabricated empty inventory after partial success.");
            Assert.AreEqual(2, await next.WaitAsync(TimeSpan.FromSeconds(5))); Assert.AreEqual(1, nextRan);
            using JsonDocument recovered = JsonDocument.Parse(nextBody.ToArray());
            AssertWholeText(recovered.RootElement.GetProperty("data"), true);
        }
        finally { body.Release.TrySetResult(); await request; }
        async Task HandleAsync()
        {
            try { await active; }
            catch (OperationCanceledException) { await WebHostFactory.ErrorAsync(context, 504, "response-timeout", "timeout"); }
        }
    }

    private sealed class CancelList<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] { get { cancellation.Cancel(); return values[index]; } }
        public IEnumerator<T> GetEnumerator() { cancellation.Cancel(); return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class GatedBody(Action started) : MemoryStream
    {
        private int writes;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref writes) == 1) { await base.WriteAsync(bytes, cancellationToken); started(); return; }
            Entered.TrySetResult(); await Release.Task; cancellationToken.ThrowIfCancellationRequested();
            await base.WriteAsync(bytes, cancellationToken);
        }
    }
    private sealed class StartedResponse : HttpResponseFeature
    {
        internal bool Started { get; set; }
        public override bool HasStarted => Started;
    }
    private sealed class AbortCapture : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }
        internal bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }
}
