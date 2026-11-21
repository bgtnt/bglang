using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class DefaultResponseTests
{
    [TestMethod]
    [DataRow("word")]
    [DataRow("text")]
    [DataRow("query")]
    [DataRow("paths")]
    public void Analysis_sharing_reconstructs_every_field_and_uses_reference_identity_in_first_token_order(string shape)
    {
        TextAnalysis text = Fixture();
        WordAnalysis first = text.Tokens[1].Analysis!;
        object data = shape switch
        {
            "word" => first, "text" => text, "query" => TextMatchProjection.Find(first, text),
            _ => (IReadOnlyList<WordDerivation>)first.GraphCandidates
        };
        ApiEnvelope raw = Envelope(data);
        var shared = new SharedDonorResponse(raw, shareAnalyses: true);
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(shared.Envelope, shared.Json);
        using JsonDocument json = JsonDocument.Parse(encoded);
        using JsonDocument restored = SharedDonorResponseTests.Reconstruct(json);
        using JsonDocument expected = JsonDocument.Parse(JsonSerializer.Serialize(raw, HttpContract.Json));
        Assert.IsTrue(SharedDonorResponseTests.SameJson(expected.RootElement, restored.RootElement));
        Assert.AreEqual(SharedDonorResponse.AnalysisFormat, json.RootElement.GetProperty("evidenceFormat").GetString());
        if (shape is "text" or "query")
        {
            Assert.AreEqual(5, json.RootElement.GetProperty("wordAnalyses").GetArrayLength(), "Equal but distinct objects, changed flags and changed proofs cannot merge.");
            JsonElement analysis = shape == "text" ? json.RootElement.GetProperty("data") : json.RootElement.GetProperty("data").GetProperty("textAnalysis");
            JsonElement[] tokens = analysis.GetProperty("tokens").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { 0, 0, 1, 2, 3, 4 }, tokens.Where(token => token.GetProperty("kind").GetString() == "Word")
                .Select(token => token.GetProperty("analysisRef").GetInt32()).ToArray());
            Assert.AreEqual(JsonValueKind.Null, tokens[0].GetProperty("analysisRef").ValueKind);
            Assert.IsTrue(tokens.All(token => !token.TryGetProperty("analysis", out _)));
            foreach (TokenAnalysis token in text.Tokens) Assert.AreEqual(token.Text, text.Text.Substring(token.Start, token.Length));
            Assert.AreEqual(3, text.Tokens[1].Start, "Emoji is two UTF-16 units, followed by a space.");
            if (shape == "query")
            {
                JsonElement query = json.RootElement.GetProperty("data").GetProperty("queryAnalysis");
                Assert.IsTrue(query.TryGetProperty("graphCandidates", out _), "QueryAnalysis stays inline even when it is the same object as a token analysis.");
                Assert.IsFalse(query.TryGetProperty("analysisRef", out _));
            }
        }
        else Assert.IsFalse(json.RootElement.TryGetProperty("wordAnalyses", out _));
        int poolSize = shared.Envelope.DonorSets!.Count;
        CollectionAssert.AreEqual(encoded, JsonSerializer.SerializeToUtf8Bytes(shared.Envelope, shared.Json), "Count and output passes must use a frozen table and identical options.");
        Assert.AreEqual(poolSize, shared.Envelope.DonorSets.Count);
        var independent = new SharedDonorResponse(Envelope(new TextAnalysis("новак", [new("новак", TokenKind.Word, 0, 5, first)])), true);
        Assert.AreEqual(1, independent.Envelope.WordAnalyses!.Count, "Analysis tables belong to one response.");
    }

    [TestMethod]
    public void Safe_encoder_writes_cyrillic_utf8_and_roundtrips_html_sensitive_controls_emoji_and_stress()
    {
        const string input = "Кирилица </script> & \" ' \u0000\r\n\t📚 пила\u0301 \u2028\u2029";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new WebInput(input), HttpContract.Json);
        string encoded = Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(encoded, "Кирилица");
        Assert.IsFalse(encoded.Contains("\\u041A", StringComparison.OrdinalIgnoreCase));
        foreach (string sensitive in new[] { "<", ">", "&", "'", "\u0000", "\r", "\n", "\t", "\u2028", "\u2029" })
            Assert.IsFalse(encoded.Contains(sensitive, StringComparison.Ordinal), sensitive);
        using JsonDocument json = JsonDocument.Parse(bytes);
        Assert.AreEqual(input, json.RootElement.GetProperty("text").GetString());
        Assert.IsTrue(bytes.Length > encoded.Length, "UTF-8 bytes and UTF-16 units are independent quantities.");
    }

    [TestMethod]
    [DataRow("1", null)]
    [DataRow("1", "raw-v1")]
    [DataRow("1", "shared-donors-v1")]
    [DataRow("1", "shared-analyses-v1")]
    [DataRow("2", null)]
    [DataRow("2", "raw-v1")]
    [DataRow("2", "shared-donors-v1")]
    [DataRow("2", "shared-analyses-v1")]
    public async Task Whole_body_utf8_boundary_is_exact_for_both_versions_and_every_format(string version, string? format)
    {
        ApiEnvelope raw = Envelope(Fixture()) with { ApiVersion = version };
        string effective = format ?? (version == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat);
        byte[] expected;
        if (effective == HttpContract.RawFormat) expected = JsonSerializer.SerializeToUtf8Bytes(raw, HttpContract.Json);
        else
        {
            var shared = new SharedDonorResponse(raw, effective == SharedDonorResponse.AnalysisFormat);
            expected = JsonSerializer.SerializeToUtf8Bytes(shared.Envelope, shared.Json);
        }
        Assert.IsTrue(expected.Length > Encoding.UTF8.GetString(expected).Length);
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
            var writer = new ResponseWriter(limit);
            Assert.AreEqual(limit, writer.MaximumBytes);
            await writer.WriteAsync(context, raw, format, CancellationToken.None);
            CollectionAssert.AreEqual(expected, body.ToArray());
            Assert.AreEqual((long)expected.Length, context.Response.ContentLength);
            Assert.IsTrue(body.CanWrite, "The guard must not dispose ASP.NET's response stream.");
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponseWriter(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResponseWriter(-1));
        foreach (string operation in new[] { "lookup", "analyze-word", "analyze-text", "generate-forms", "derive-words", "decompose-word", "find-forms-in-text" })
            ApiOperations.Validate(operation, new("дума", Query: operation == "find-forms-in-text" ? "дума" : null, EvidenceFormat: effective));
    }

    [TestMethod]
    public async Task Cancellation_before_projection_and_during_preflight_never_starts_the_body()
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new MemoryStream(); var context = new DefaultHttpContext(); context.Response.Body = body;
        cancellation.Cancel();
        var probe = new CancelOnRead(cancellation);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ResponseWriter(10000).WriteAsync(context,
            Envelope(probe), HttpContract.RawFormat, cancellation.Token));
        Assert.AreEqual(0, probe.Reads);
        using var preflight = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ResponseWriter(10000).WriteAsync(context,
            Envelope(new CancelOnRead(preflight)), HttpContract.RawFormat, preflight.Token));
        Assert.AreEqual(0L, body.Length); Assert.IsNull(context.Response.ContentLength);
        using var projection = new CancellationTokenSource();
        WordAnalysis word = Word() with { GraphCandidates = [Path(new CancelOnEnumeration(projection))] };
        Assert.Throws<OperationCanceledException>(() => new SharedDonorResponse(Envelope(word), true, projection.Token));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Cancellation_after_start_drains_response_and_preserves_the_next_slot(bool cancelInsideCallback)
    {
        await using var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var entered = Signal(); var release = Signal(); int nextRan = 0;
        Task<object> active = queue.RunResponseAsync(async () =>
        {
            if (cancelInsideCallback) cancellation.Cancel(); // Start won, possibly before Started.WaitAsync's continuation.
            entered.SetResult(); await release.Task;
            cancellation.Token.ThrowIfCancellationRequested(); return 1;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!cancelInsideCallback) cancellation.Cancel();
            Task<object> next = queue.RunResponseAsync(() => { nextRan++; return Task.FromResult<object>(2); }, CancellationToken.None);
            await Assert.ThrowsAsync<TimeoutException>(async () => await active.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.AreEqual(0, nextRan); Assert.IsFalse(next.IsCompleted);
            release.SetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await active);
            Assert.AreEqual(2, await next.WaitAsync(TimeSpan.FromSeconds(5))); Assert.AreEqual(1, nextRan);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task Slow_transfer_cancellation_and_shutdown_keep_the_worker_until_actual_completion()
    {
        var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        using var body = new GatedBody(); var context = new DefaultHttpContext(); context.Response.Body = body;
        Task<object> active = queue.RunResponseAsync(async () =>
        {
            await new ResponseWriter(100000).WriteAsync(context, Envelope(Word()), null, cancellation.Token); return 1;
        }, cancellation.Token);
        int pendingRan = 0;
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<object> pending = queue.RunResponseAsync(() => { pendingRan++; return Task.FromResult<object>(2); }, CancellationToken.None);
            cancellation.Cancel();
            Task disposing = queue.DisposeAsync().AsTask();
            Assert.IsFalse(active.IsCompleted); Assert.IsFalse(disposing.IsCompleted); Assert.AreEqual(0, pendingRan);
            ApiRequestException closed = await Assert.ThrowsAsync<ApiRequestException>(() =>
                queue.RunResponseAsync(() => Task.FromResult<object>(3), CancellationToken.None));
            Assert.AreEqual(503, closed.Status);
            body.Release.SetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await active);
            await Assert.ThrowsAsync<ApiRequestException>(async () => await pending);
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, pendingRan); Assert.AreEqual(0L, body.Length); Assert.IsTrue(body.CanWrite);
        }
        finally { body.Release.TrySetResult(); await queue.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("expire")]
    [DataRow("close")]
    public async Task Queued_response_cancel_expiry_and_close_never_execute_later(string winner)
    {
        var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(); var entered = Signal(); var release = Signal(); int pendingRan = 0;
        Task<object> active = queue.RunResponseAsync(async () => { entered.SetResult(); await release.Task; return 1; }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<object> pending = queue.RunResponseAsync(() => { pendingRan++; return Task.FromResult<object>(2); }, cancellation.Token);
            if (winner == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
            }
            else if (winner == "expire")
            {
                ApiRequestException error = await Assert.ThrowsAsync<ApiRequestException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual("queue-expired", error.Code);
            }
            else queue.Close();
            Assert.IsFalse(active.IsCompleted); release.SetResult();
            Assert.AreEqual(1, await active.WaitAsync(TimeSpan.FromSeconds(5)));
            if (winner == "close") await Assert.ThrowsAsync<ApiRequestException>(async () => await pending);
            queue.Close(); await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.AreEqual(0, pendingRan);
        }
        finally { release.TrySetResult(); await queue.DisposeAsync(); }
    }

    [TestMethod]
    public async Task Error_after_headers_aborts_without_appending_a_json_tail()
    {
        var context = new DefaultHttpContext(); using var body = new MemoryStream();
        body.Write("partial-success"u8); var lifetime = new AbortCapture();
        context.Features.Set<IHttpResponseFeature>(new StartedResponse()); context.Response.Body = body;
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        await WebHostFactory.ErrorAsync(context, 504, "response-timeout", "timeout");
        Assert.IsTrue(lifetime.Aborted);
        CollectionAssert.AreEqual("partial-success"u8.ToArray(), body.ToArray());
    }

    [TestMethod]
    public async Task Partial_transfer_cancellation_drains_before_abort_and_next_response_is_valid()
    {
        await using var queue = new SerializedWorkQueue(1, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var started = new StartedResponse(started: false);
        using var body = new GatedBody(allowFirstWrite: true, markStarted: () => started.Started = true);
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(started); context.Response.Body = body;
        var lifetime = new AbortCapture(); context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        string[] donors = Enumerable.Range(0, 4000).Select(index => "донор" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        ApiEnvelope result = Envelope(Word() with { GraphCandidates = [Path(donors)] });
        int encodedBytes = JsonSerializer.SerializeToUtf8Bytes(result, HttpContract.Json).Length;
        Assert.IsTrue(encodedBytes > 16 * 1024 && encodedBytes < 100000, "A small fixture must exercise multiple async flushes under the unchanged encoder.");
        Task<object>? active = null;
        Task request = HandleAsync();
        try
        {
            await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(context.Response.HasStarted); Assert.IsTrue(body.Length > 0);
            byte[] prefix = body.ToArray();
            Assert.IsTrue(prefix.Length < encodedBytes, "The second write is blocked after an actual partial body.");
            using var nextBody = new MemoryStream(); var nextContext = new DefaultHttpContext(); nextContext.Response.Body = nextBody;
            int nextRan = 0;
            Task<object> next = queue.RunResponseAsync(async () =>
            {
                nextRan++;
                await new ResponseWriter(100000).WriteAsync(nextContext, Envelope(Word()), HttpContract.RawFormat, CancellationToken.None);
                return 2;
            }, CancellationToken.None);
            cancellation.Cancel();
            await Assert.ThrowsAsync<TimeoutException>(async () => await request.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.IsFalse(active!.IsCompleted); Assert.IsFalse(next.IsCompleted); Assert.AreEqual(0, nextRan);
            Assert.IsFalse(lifetime.Aborted, "The handler must not abandon the response context before the active stream drains.");
            body.Release.SetResult();
            await request.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(lifetime.Aborted);
            CollectionAssert.AreEqual(prefix, body.ToArray(), "Abort cannot append an error tail to a partial success.");
            Assert.AreEqual(2, await next.WaitAsync(TimeSpan.FromSeconds(5))); Assert.AreEqual(1, nextRan);
            using JsonDocument recovered = JsonDocument.Parse(nextBody.ToArray());
            Assert.AreEqual("новак", recovered.RootElement.GetProperty("data").GetProperty("word").GetString());
            Assert.IsFalse(recovered.RootElement.TryGetProperty("code", out _));
        }
        finally { body.Release.TrySetResult(); await request; }

        async Task HandleAsync()
        {
            try
            {
                active = queue.RunResponseAsync(async () =>
                {
                    await new ResponseWriter(100000).WriteAsync(context, result, HttpContract.RawFormat, cancellation.Token);
                    return result;
                }, cancellation.Token);
                await active;
            }
            catch (OperationCanceledException)
            {
                // Success work is drained; the small protocol error is outside its cancelled deadline.
                await WebHostFactory.ErrorAsync(context, 504, "response-timeout", "timeout");
            }
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Native_repeated4091_default_v2_shares_analysis_and_raw_versions_reject_then_recover()
    {
        await WithPackage(async (app, session, client) =>
        {
            using JsonDocument metadata = JsonDocument.Parse(await client.GetStringAsync("/api/v2/"));
            Assert.AreEqual(HttpContract.DefaultResponseBytes, metadata.RootElement.GetProperty("limits").GetProperty("responseBodyBytes").GetInt64());
            Assert.AreEqual(SharedDonorResponse.AnalysisFormat, metadata.RootElement.GetProperty("defaultEvidenceFormat").GetString());
            const string queryText = "📚 Книга, книгите.\r\nПила.";
            TextMatchResult match = TextMatchProjection.Find(session.AnalyzeWord("книга", 32), session.AnalyzeText(queryText));
            using (HttpResponseMessage query = await Post(client, "/api/v2/find-forms-in-text", new(queryText, Query: "книга")))
            {
                Assert.AreEqual(HttpStatusCode.OK, query.StatusCode);
                using var queryStream = new CountedRead(await query.Content.ReadAsStreamAsync(), HttpContract.DefaultResponseBytes);
                using JsonDocument queryJson = await JsonDocument.ParseAsync(queryStream);
                using JsonDocument restoredQuery = SharedDonorResponseTests.Reconstruct(queryJson);
                using JsonDocument expectedQuery = JsonDocument.Parse(JsonSerializer.Serialize(match, HttpContract.Json));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expectedQuery.RootElement, restoredQuery.RootElement.GetProperty("data")));
                Assert.IsTrue(queryJson.RootElement.GetProperty("data").GetProperty("queryAnalysis").TryGetProperty("readings", out _));
            }
            const string surface = "флорбините";
            string text = string.Join(" ", Enumerable.Repeat(surface, 372)); Assert.AreEqual(4091, text.Length);
            TextAnalysis direct = session.AnalyzeText(text);
            using HttpResponseMessage response = await Post(client, "/api/v2/analyze-text", new(text));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var counted = new CountedRead(await response.Content.ReadAsStreamAsync(), HttpContract.DefaultResponseBytes);
            using JsonDocument shared = await JsonDocument.ParseAsync(counted);
            Assert.IsTrue(counted.Bytes > 0); Assert.AreEqual(counted.Bytes, response.Content.Headers.ContentLength);
            Assert.AreEqual(64, counted.Digest().Length); // Streamed digest; no raw giant JSON is constructed.
            Assert.AreEqual("2", shared.RootElement.GetProperty("apiVersion").GetString());
            Assert.AreEqual(1, shared.RootElement.GetProperty("wordAnalyses").GetArrayLength());
            JsonElement[] tokens = shared.RootElement.GetProperty("data").GetProperty("tokens").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(direct.Tokens.Select(token => token.Text).ToArray(), tokens.Select(token => token.GetProperty("text").GetString()!).ToArray());
            // Expand one table entry, then compare each occurrence independently; never inflate 745 MB.
            for (int index = 0; index < tokens.Length; index++)
            {
                JsonElement token = tokens[index]; TokenAnalysis original = direct.Tokens[index];
                int start = token.GetProperty("start").GetInt32(), length = token.GetProperty("length").GetInt32();
                Assert.AreEqual(original.Kind.ToString(), token.GetProperty("kind").GetString());
                Assert.AreEqual(original.Start, start); Assert.AreEqual(original.Length, length);
                Assert.AreEqual(original.Text, text.Substring(start, length));
                if (original.Analysis is null) Assert.AreEqual(JsonValueKind.Null, token.GetProperty("analysisRef").ValueKind);
                else Assert.AreEqual(0, token.GetProperty("analysisRef").GetInt32());
            }
            // HTTP is the second analysis; this third warm call is a phase-labelled public control,
            // not object identity across requests. A mismatch may expose cache/work-phase behavior.
            TextAnalysis warmText = session.AnalyzeText(text);
            WordAnalysis warm = warmText.Tokens.First(token => token.Analysis is not null).Analysis!;
            using JsonDocument restoredWord = SharedDonorResponseTests.Reconstruct(shared, shared.RootElement.GetProperty("wordAnalyses")[0]);
            using JsonDocument expectedWord = JsonDocument.Parse(JsonSerializer.Serialize(warm, HttpContract.Json));
            Assert.IsTrue(SharedDonorResponseTests.SameJson(expectedWord.RootElement, restoredWord.RootElement.GetProperty("data")), "HTTP warm2 versus public warm3: compare complete donors/counts/proof/flags without multiplying by372; report phase differences separately from synthetic same-result losslessness.");
            foreach (string property in new[] { "sentences", "phonetics", "diagnostics" })
            {
                object publicValue = property switch { "sentences" => warmText.Sentences, "phonetics" => warmText.Phonetics, _ => warmText.Diagnostics };
                using JsonDocument expectedTextField = JsonDocument.Parse(JsonSerializer.Serialize(publicValue, HttpContract.Json));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expectedTextField.RootElement, shared.RootElement.GetProperty("data").GetProperty(property)), property);
            }
            foreach ((string path, string? format) in new[] { ("/api/v1/analyze-text", (string?)null), ("/api/v2/analyze-text", HttpContract.RawFormat) })
            {
                using HttpResponseMessage raw = await Post(client, path, new(text, EvidenceFormat: format));
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, raw.StatusCode);
                await AssertTooLarge(raw);
            }
            foreach ((string version, string? format) in new[] { ("1", (string?)null), ("2", HttpContract.RawFormat), ("2", (string?)null) })
            {
                using HttpResponseMessage small = await Post(client, $"/api/v{version}/lookup", new("книга", EvidenceFormat: format));
                Assert.AreEqual(HttpStatusCode.OK, small.StatusCode);
                using var stream = new CountedRead(await small.Content.ReadAsStreamAsync(), HttpContract.DefaultResponseBytes);
                using JsonDocument json = await JsonDocument.ParseAsync(stream);
                Assert.AreEqual(version, json.RootElement.GetProperty("apiVersion").GetString());
                using JsonDocument expected = JsonDocument.Parse(JsonSerializer.Serialize(session.Lookup("книга", LookupMode.Exact, 32), HttpContract.Json));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expected.RootElement, json.RootElement.GetProperty("data")));
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Native_tiny_cap_returns_a_complete_error_larger_than_success_budget()
    {
        await WithPackage(async (app, session, client) =>
        {
            using HttpResponseMessage rejected = await Post(client, "/api/v2/analyze-word", new("книга"));
            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
            Assert.IsTrue(rejected.Content.Headers.ContentLength > 1, "The fixed small error is independent of the language success budget.");
            await AssertTooLarge(rejected);
            using HttpResponseMessage live = await client.GetAsync("/health/live"); Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
        }, maximumBytes: 1);
    }

    private static TextAnalysis Fixture()
    {
        WordAnalysis first = Word() with
        {
            GraphCandidates = [Path(null) with { Base = "пресен", IsConditional = false }, Path(["пресен", "тесен"])],
            KnownBaseCandidates = [new("новак", "пресен", "base-tag", "form-tag", "program", 0, [], "source")],
            BaseTypeCandidates = [new("новак", "новак", "base-tag", "form-tag", "program", 2, ["пресен", "тесен"], "source")
            { ShorterEndingCandidates = [new("новак", "новак", "base-tag", "short-tag", "program", 1, ["пресен", "тесен"], "source")] }],
            GraphSearchIncomplete = true, CandidateEvidenceLimited = true, BaseTypeSearchLimited = true
        };
        WordAnalysis clone = first with { }, flags = first with { GraphSearchIncomplete = false, CandidateSearchLimited = true };
        WordAnalysis proof = first with { GraphCandidates = [Path(["тесен", "пресен"])] };
        WordAnalysis homonyms = new("пила\u0301", "пила", [new("пила\u0301", "пила", "Съществително:41,Женски", 1, null, null, null, "source"),
            new("пила\u0301", "пия", "Глагол:174,Минало", 1, null, null, null, "other-source")], null);
        const string text = "📚 новак, новак\r\nновак новак новак пила\u0301.";
        return new(text, [new("📚", TokenKind.Symbol, 0, 2, null), new("новак", TokenKind.Word, 3, 5, first),
            new("новак", TokenKind.Word, 10, 5, first), new("\r\n", TokenKind.Whitespace, 15, 2, null),
            new("новак", TokenKind.Word, 17, 5, clone), new("новак", TokenKind.Word, 23, 5, flags),
            new("новак", TokenKind.Word, 29, 5, proof), new("пила\u0301", TokenKind.Word, 35, 5, homonyms)])
        {
            Sentences = [new(0, 17), new(17, text.Length - 17)],
            Phonetics = new(["но", "вак", "пи", "ла\u0301"], [38], [18])
            {
                ExplicitRhymeTailCandidates = ["ла"], InferredSingleVowelRhymeTailCandidates = ["ак"],
                ReviewedExactStressPositions = [38], ReviewedExactRhymeTailCandidates = ["ила"],
                LexicalStressPositions = [36], LexicalRhymeTailCandidates = ["пила"]
            },
            Diagnostics = [new("hypothesis", 3, 5, "rule") { ProjectedText = "новак" }]
        };
    }
    private static WordAnalysis Word() => new("новак", "новак", [], null);
    private static WordDerivation Path(IReadOnlyList<string>? donors) => new("новак", "новак", "from", "to", [], ["edge"], ["rule"], ["application"], .85, ["proof"], "source")
    {
        DerivedLemma = "новак", FullTags = ["base-tag", "form-tag"], IsConditional = donors is not null,
        ConditionalRootProgramId = donors is null ? null : "program", ConditionalRootBaseFullTag = donors is null ? null : "base-tag",
        ConditionalRootSupportCount = donors?.Count, ConditionalSupportingLemmas = donors,
        UnmetExactSelfReadingRequirements = donors is null ? null : [new("новак", "base-tag", "base-edge")]
    };
    private static ApiEnvelope Envelope(object data) => new("2", new("host", "api", "contracts", "dotnet"),
        new("package", "schema", new string('a', 64), "lexicon", "lexicon-source", "graph", "graph-source"),
        new(null, 32, 32, false, "per-word-analysis"), new(false, true, false), data);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, WebInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input, options: HttpContract.Json) };
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
    private static async Task AssertTooLarge(HttpResponseMessage response)
    {
        using var stream = new CountedRead(await response.Content.ReadAsStreamAsync(), HttpContract.BodyBytes);
        using JsonDocument error = await JsonDocument.ParseAsync(stream);
        Assert.AreEqual(stream.Bytes, response.Content.Headers.ContentLength);
        Assert.AreEqual("response-too-large", error.RootElement.GetProperty("code").GetString());
        JsonElement coverage = error.RootElement.GetProperty("coverage");
        Assert.IsTrue(coverage.GetProperty("hostTruncated").GetBoolean()); Assert.IsTrue(coverage.GetProperty("possibleTruncation").GetBoolean());
        Assert.IsFalse(coverage.GetProperty("complete").GetBoolean());
    }
    private static async Task WithPackage(Func<WebApplication, BulgarianLanguageSession, HttpClient, Task> verify,
        long maximumBytes = HttpContract.DefaultResponseBytes)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "", hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete release package is required; no package-free acceptance.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit), "Set the independently selected manifest SHA-256.");
        BulgarianLanguageSession? session = null; int opens = 0;
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash, "--BgLang:ResponseByteLimit", maximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            () => { opens++; return session = BulgarianLanguageSession.Open(manifest); });
        try
        {
            await app.StartAsync(); Assert.AreEqual(hash.ToLowerInvariant(), session!.Provenance.ManifestSha256.ToLowerInvariant());
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            await verify(app, session, client); Assert.AreEqual(1, opens);
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
        Assert.Throws<ObjectDisposedException>(() => _ = session!.Provenance);
    }
    private sealed class CancelOnRead(CancellationTokenSource cancellation)
    {
        internal int Reads { get; private set; }
        public string Value { get { Reads++; cancellation.Cancel(); return "Кирилица"; } }
    }
    private sealed class CancelOnEnumeration(CancellationTokenSource cancellation) : IReadOnlyList<string>
    {
        public int Count => 2; public string this[int index] => index == 0 ? "пресен" : "тесен";
        public IEnumerator<string> GetEnumerator() { cancellation.Cancel(); yield return "пресен"; yield return "тесен"; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class GatedBody(bool allowFirstWrite = false, Action? markStarted = null) : MemoryStream
    {
        private int _writes;
        internal TaskCompletionSource Entered { get; } = Signal(); internal TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            if (allowFirstWrite && Interlocked.Increment(ref _writes) == 1)
            {
                await base.WriteAsync(bytes, cancellationToken); markStarted?.Invoke(); return;
            }
            Entered.TrySetResult(); await Release.Task; cancellationToken.ThrowIfCancellationRequested();
            await base.WriteAsync(bytes, cancellationToken);
        }
    }
    private sealed class StartedResponse(bool started = true) : HttpResponseFeature
    {
        internal bool Started { get; set; } = started;
        public override bool HasStarted => Started;
    }
    private sealed class AbortCapture : IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; } internal bool Aborted { get; private set; }
        public void Abort() => Aborted = true;
    }
    private sealed class CountedRead(Stream source, long maximum) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Bytes { get; private set; }
        internal string Digest() => Convert.ToHexString(_hash.GetHashAndReset());
        private void Count(ReadOnlySpan<byte> bytes) { Bytes += bytes.Length; Assert.IsTrue(Bytes <= maximum, "Body exceeded its declared test profile."); _hash.AppendData(bytes); }
        public override int Read(byte[] buffer, int offset, int count) { int read = source.Read(buffer, offset, count); Count(buffer.AsSpan(offset, read)); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { int read = await source.ReadAsync(buffer, cancellationToken); Count(buffer.Span[..read]); return read; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) { _hash.Dispose(); source.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
