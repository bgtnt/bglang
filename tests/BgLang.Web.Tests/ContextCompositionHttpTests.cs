using System.Net;
using System.Text;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class ContextCompositionHttpTests
{
    private static readonly string[] AdditionalGroups = ["unknown-preverbal-attribute", "closed-register-title-attribute",
        "closed-adverbials", "nominal-possessive-clitic", "preposed-object-clitic", "special-preposed-dual-and-suppression",
        "closed-da-short-clitic-subpredicate", "closed-postverbal-clitic-subject", "closed-dative-speech-subpredicate"];
    private static readonly string[] Switches = ["includeAdditionalRoleHeuristics", "includeSubordinateCommaDiagnostics",
        "includeFragmentDiagnostics", "includeSpacingDiagnostics", "includeWordConstructions"];
    private static readonly string[] OffInputs = ["{\"text\":\"книга.\"}", "{\"text\":\"книга.\",\"context\":null}",
        "{\"text\":\"книга.\",\"context\":{}}",
        "{\"text\":\"книга.\",\"context\":{\"includeAdditionalRoleHeuristics\":false,\"includeSubordinateCommaDiagnostics\":false,\"includeFragmentDiagnostics\":false,\"includeSpacingDiagnostics\":false}}"];

    [TestMethod]
    public void Context_object_is_optional_strict_boolean_and_exclusive_to_analyze_text()
    {
        for (int index = 0; index < OffInputs.Length; index++)
        {
            WebInput input = JsonSerializer.Deserialize<WebInput>(OffInputs[index], HttpContract.Json)!;
            ApiOperations.Validate("analyze-text", input);
            Assert.AreEqual(default(TextContextOptions), input.Context ?? default);
            Assert.AreEqual(index >= 2, input.Context.HasValue);
        }
        foreach (string property in Switches)
        foreach (string value in new[] { "null", "\"true\"", "0", "[]", "{}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(
                "{\"text\":\"книга.\",\"context\":{\"" + property + "\":" + value + "}}", HttpContract.Json));
        foreach (string value in new[] { "true", "1", "[]", "\"context\"" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(
                "{\"text\":\"книга.\",\"context\":" + value + "}", HttpContract.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(
            "{\"text\":\"книга.\",\"context\":{\"notAnOption\":false}}", HttpContract.Json));
        JsonElement[] operations = Root.GetProperty("nonAnalyzeTextOperations").EnumerateArray().ToArray();
        Assert.AreEqual(8, operations.Length);
        foreach (JsonElement operation in operations)
        {
            string name = operation.GetString()!; string? query = name == "find-forms-in-text" ? "книга" : null;
            ApiOperations.Validate(name, new("книга", Query: query, Context: null));
            ApiRequestException error = Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(name, new("книга", Query: query, Context: default(TextContextOptions))));
            Assert.AreEqual(400, error.Status); Assert.AreEqual("option-not-applicable", error.Code);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Context_defaults_and_each_isolated_switch_forward_without_enabling_unknown_names(string version)
    {
        await WithPackage(async (app, session, client) =>
        {
            JsonElement? off = null;
            foreach (string json in OffInputs)
            {
                using JsonDocument text = await Analyze(client, version, json);
                JsonElement data = text.RootElement.GetProperty("data");
                AssertOptions(data, default);
                if (off is JsonElement prior) Assert.IsTrue(SharedDonorResponseTests.SameJson(prior, data));
                else off = data.Clone();
            }
            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            using var cancellation = new CancellationTokenSource();
            var contextByOption = new Dictionary<int, JsonElement[]>();
            for (int index = 0; index < Switches.Length; index++)
            foreach (bool names in new[] { false, true })
            {
                string json = "{\"text\":\"книга.\",\"includeUnknownContextProperNames\":" + (names ? "true" : "false") +
                    ",\"context\":{\"" + Switches[index] + "\":true}}";
                WebInput input = JsonSerializer.Deserialize<WebInput>(json, HttpContract.Json)!;
                using JsonDocument text = await Analyze(client, version, json);
                JsonElement data = text.RootElement.GetProperty("data");
                AssertOptions(data, input.Context!.Value);
                JsonElement[] current = new[] { "context", "syntax", "validation", "wordConstructions" }.Select(field => data.GetProperty(field).Clone()).ToArray();
                if (contextByOption.TryGetValue(index, out JsonElement[]? prior))
                    for (int part = 0; part < current.Length; part++) Assert.IsTrue(SharedDonorResponseTests.SameJson(prior[part], current[part]));
                else contextByOption.Add(index, current);
                Assert.AreEqual(names, data.GetProperty("properNames").GetProperty("unknownContextEvaluated").GetBoolean());
                Assert.AreEqual(names ? JsonValueKind.Array : JsonValueKind.Null,
                    data.GetProperty("properNames").GetProperty("unresolvedContextTokens").ValueKind);
                Assert.IsTrue(SharedDonorResponseTests.SameJson(off!.Value.GetProperty("readingInventory"), data.GetProperty("readingInventory")));
                int callbacks = 0;
                await host.ExecuteResponseAsync("analyze-text", input, (result, actualToken) =>
                {
                    callbacks++; Assert.AreEqual(cancellation.Token, actualToken);
                    using JsonDocument encoded = JsonDocument.Parse(Encode(Envelope(result.Data), HttpContract.RawFormat));
                    AssertOptions(encoded.RootElement.GetProperty("data"), input.Context.Value);
                    Assert.AreEqual(names, ((TextAnalysis)result.Data).ProperNames!.UnknownContextEvaluated);
                    return Task.CompletedTask;
                }, cancellation.Token);
                Assert.AreEqual(1, callbacks);
            }
            Assert.IsTrue(session.AnalyzeWord("книга").IsKnown);
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Invalid_context_and_empty_context_on_other_routes_are_rejected_before_the_occupied_language_lane(string version)
    {
        await WithPackage(async (app, _, client) =>
        {
            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            var entered = Signal(); var release = Signal();
            Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (_, _) =>
            { entered.TrySetResult(); await release.Task; }, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                string[] invalidContexts = ["{\"includeAdditionalRoleHeuristics\":null}",
                    "{\"includeSubordinateCommaDiagnostics\":\"true\"}", "{\"includeFragmentDiagnostics\":1}",
                    "{\"includeSpacingDiagnostics\":[]}", "{\"notAnOption\":false}"];
                foreach (string context in invalidContexts)
                    await Rejected("analyze-text", "{\"text\":\"книга.\",\"context\":" + context + "}", "invalid-json");
                foreach (JsonElement operation in Root.GetProperty("nonAnalyzeTextOperations").EnumerateArray())
                {
                    string name = operation.GetString()!;
                    string json = "{\"text\":\"книга\",\"context\":{}" +
                        (name == "find-forms-in-text" ? ",\"query\":\"книга\"}" : "}");
                    await Rejected(name, json, "option-not-applicable");
                }
            }
            finally { release.TrySetResult(); await active; }
            await host.ExecuteResponseAsync("lookup", new("книга"), (_, _) => Task.CompletedTask, CancellationToken.None);
            async Task Rejected(string operation, string json, string code)
            {
                using HttpResponseMessage response = await Post(client, "/api/v" + version + "/" + operation, json);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                Assert.AreEqual(code, error.RootElement.GetProperty("code").GetString());
                Assert.IsFalse(error.RootElement.TryGetProperty("data", out JsonElement ignored));
                Assert.IsFalse(active.IsCompleted, "Invalid options must finish before waiting for the occupied language lane.");
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Enabled_context_cancellation_drains_before_response_and_the_next_request()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest)); Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        string dense = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dense-unknown-text.v1.txt"));
        Assert.AreEqual(4079, dense.Length); Assert.AreEqual(288, dense.Split(' ').Distinct(StringComparer.Ordinal).Count());
        await using var host = new SessionHost(manifest, hash);
        await host.StartAsync(CancellationToken.None);
        await host.ExecuteResponseAsync("lookup", new("книга"), (_, _) => Task.CompletedTask, CancellationToken.None);
        TextContextOptions options = new() { IncludeAdditionalRoleHeuristics = true, IncludeSubordinateCommaDiagnostics = true,
            IncludeFragmentDiagnostics = true, IncludeSpacingDiagnostics = true };
        using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        bool responded = false;
        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() => host.ExecuteResponseAsync("analyze-text",
            new(dense, IncludeUnknownContextProperNames: true, Context: options),
            (_, _) => { responded = true; return Task.CompletedTask; }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(cancellation.IsCancellationRequested); Assert.IsFalse(responded);
        await host.ExecuteResponseAsync("lookup", new("книга"), (result, _) =>
        { Assert.IsTrue(((IReadOnlyList<WordReading>)result.Data).Count > 0); return Task.CompletedTask; }, CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Marked_compounds_keep_raw_http_tokens_and_utf16_spans(string version)
    {
        await WithPackage(async (_, _, client) =>
        {
            const string input = "😀 наи\u0306-силен по-си\u0300лен.";
            using JsonDocument response = await Analyze(client, version, JsonSerializer.Serialize(new { text = input }));
            JsonElement data = response.RootElement.GetProperty("data");
            Assert.AreEqual(input, data.GetProperty("text").GetString());
            CollectionAssert.AreEqual(new[] { "0:2:😀", "3:10:наи\u0306-силен", "14:9:по-си\u0300лен", "23:1:." },
                data.GetProperty("tokens").EnumerateArray().Select(token =>
                    $"{token.GetProperty("start").GetInt32()}:{token.GetProperty("length").GetInt32()}:{token.GetProperty("text").GetString()}").ToArray());
            JsonElement validation = data.GetProperty("validation");
            Assert.IsTrue(validation.GetProperty("available").GetBoolean());
            Assert.IsFalse(validation.GetProperty("diagnostics").EnumerateArray().Any(row =>
                row.GetProperty("code").GetString() == "invalid_word_phrase_compact_separator_order"));
            Assert.AreEqual(JsonValueKind.Null, validation.GetProperty("subordinateCommaDiagnostics").ValueKind);
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1", "по-ле\u0300сен", 9, 12)]
    [DataRow("1", "по-л\u0450сен", 8, 11)]
    [DataRow("1", "ПО-ЛЕ\u0300СЕН", 9, 12)]
    [DataRow("1", "ПО-Л\u0400СЕН", 8, 11)]
    [DataRow("2", "по-ле\u0300сен", 9, 12)]
    [DataRow("2", "по-л\u0450сен", 8, 11)]
    [DataRow("2", "ПО-ЛЕ\u0300СЕН", 9, 12)]
    [DataRow("2", "ПО-Л\u0400СЕН", 8, 11)]
    public async Task Grave_e_compounds_keep_raw_http_tokens_and_utf16_spans(string version, string form, int length, int periodStart)
    {
        await WithPackage(async (_, _, client) =>
        {
            string input = "😀 " + form + ".";
            using JsonDocument response = await Analyze(client, version, JsonSerializer.Serialize(new { text = input }));
            JsonElement data = response.RootElement.GetProperty("data");
            Assert.AreEqual(input, data.GetProperty("text").GetString());
            CollectionAssert.AreEqual(new[] { "0:2:😀", $"3:{length}:{form}", $"{periodStart}:1:." },
                data.GetProperty("tokens").EnumerateArray().Select(token =>
                    $"{token.GetProperty("start").GetInt32()}:{token.GetProperty("length").GetInt32()}:{token.GetProperty("text").GetString()}").ToArray());
            JsonElement validation = data.GetProperty("validation");
            Assert.IsTrue(validation.GetProperty("available").GetBoolean());
            Assert.IsFalse(validation.GetProperty("diagnostics").EnumerateArray().Any(row =>
                row.GetProperty("code").GetString() == "invalid_word_phrase_compact_separator_order"));
            Assert.AreEqual(JsonValueKind.Null, validation.GetProperty("subordinateCommaDiagnostics").ValueKind);
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Existing_comma_trigger_unavailability_is_visible_over_http(string version)
    {
        await WithPackage(async (_, _, client) =>
        {
            foreach (string input in new[] { "Ще дойда само когато мога.", "Само аз ще дойда когато мога." })
            {
                using JsonDocument disabled = await Analyze(client, version, JsonSerializer.Serialize(new { text = input }));
                using JsonDocument enabled = await Analyze(client, version, JsonSerializer.Serialize(new
                    { text = input, context = new { includeSubordinateCommaDiagnostics = true } }));
                JsonElement off = disabled.RootElement.GetProperty("data"), on = enabled.RootElement.GetProperty("data");
                Assert.AreEqual(input, on.GetProperty("text").GetString());
                Assert.AreEqual(JsonValueKind.Null, off.GetProperty("validation").GetProperty("subordinateCommaDiagnostics").ValueKind);
                JsonElement validation = on.GetProperty("validation");
                Assert.IsTrue(validation.GetProperty("available").GetBoolean());
                Assert.AreEqual(JsonValueKind.Null, validation.GetProperty("unavailableReason").ValueKind);
                const string reason = "subordinate_comma:trigger_authority_unavailable";
                Assert.IsFalse(off.GetProperty("validation").GetProperty("unavailableChecks").EnumerateArray().Any(row => row.GetString() == reason));
                Assert.IsTrue(validation.GetProperty("unavailableChecks").EnumerateArray().Any(row => row.GetString() == reason));
                JsonElement diagnostics = validation.GetProperty("subordinateCommaDiagnostics");
                Assert.AreEqual(JsonValueKind.Array, diagnostics.ValueKind);
                Assert.AreEqual(0, diagnostics.GetArrayLength());
                Assert.IsTrue(SharedDonorResponseTests.SameJson(off.GetProperty("diagnostics"), on.GetProperty("diagnostics")));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(off.GetProperty("readingInventory"), on.GetProperty("readingInventory")));
            }
        });
    }

    private static void AssertOptions(JsonElement data, TextContextOptions options)
    {
        Assert.AreEqual(JsonValueKind.Object, data.GetProperty("context").ValueKind);
        foreach ((string field, string version) in new[] { ("context", "bounded-context.v2"),
            ("syntax", "bounded-syntax-roles.v2"), ("validation", "bounded-validation.v2") })
        {
            Assert.AreEqual(version, data.GetProperty(field).GetProperty("version").GetString());
            Assert.IsTrue(data.GetProperty(field).GetProperty("available").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, data.GetProperty(field).GetProperty("unavailableReason").ValueKind);
        }
        JsonElement groups = data.GetProperty("syntax").GetProperty("additionalGroups");
        Assert.AreEqual(options.IncludeAdditionalRoleHeuristics ? JsonValueKind.Array : JsonValueKind.Null, groups.ValueKind);
        if (options.IncludeAdditionalRoleHeuristics)
        {
            Assert.AreEqual(9, groups.GetArrayLength(), "Account for every prepared group, including no-match/unavailable groups.");
            CollectionAssert.AreEquivalent(AdditionalGroups, groups.EnumerateArray().Select(group => group.GetProperty("groupId").GetString()).ToArray());
            CollectionAssert.AllItemsAreUnique(groups.EnumerateArray().Select(group => group.GetProperty("groupId").GetString()).ToArray());
            foreach (JsonElement group in groups.EnumerateArray())
                if (!group.GetProperty("available").GetBoolean())
                    Assert.IsFalse(string.IsNullOrWhiteSpace(group.GetProperty("unavailableReason").GetString()));
        }
        Assert.AreEqual(options.IncludeWordConstructions ? JsonValueKind.Object : JsonValueKind.Null,
            data.GetProperty("wordConstructions").ValueKind);
        JsonElement validation = data.GetProperty("validation");
        AssertMode(validation.GetProperty("subordinateCommaDiagnostics"), options.IncludeSubordinateCommaDiagnostics);
        AssertMode(validation.GetProperty("fragmentDiagnostics"), options.IncludeFragmentDiagnostics);
        AssertMode(validation.GetProperty("spacingDiagnostics"), options.IncludeSpacingDiagnostics);
        if (options.IncludeSpacingDiagnostics) Assert.AreEqual(0, validation.GetProperty("spacingDiagnostics").GetArrayLength(), "The literal single-word text has no spacing defect.");
        if (options.IncludeSubordinateCommaDiagnostics) Assert.AreEqual(0, validation.GetProperty("subordinateCommaDiagnostics").GetArrayLength(), "The literal text has no subordinate clause.");
        static void AssertMode(JsonElement value, bool enabled) =>
            Assert.AreEqual(enabled ? JsonValueKind.Array : JsonValueKind.Null, value.ValueKind);
    }

    private static async Task<JsonDocument> Analyze(HttpClient client, string version, string json)
    {
        using HttpResponseMessage response = await Post(client, "/api/v" + version + "/analyze-text", json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        if (version == "1") Assert.IsFalse(encoded.RootElement.TryGetProperty("evidenceFormat", out _));
        else Assert.AreEqual("shared-analyses-v1", encoded.RootElement.GetProperty("evidenceFormat").GetString());
        return Restore(encoded);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
    internal static async Task WithPackage(Func<WebApplication, BulgarianLanguageSession, HttpClient, Task> verify)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "", hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete release package is mandatory.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        BulgarianLanguageSession? session = null; int opens = 0;
        WebApplication app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest, "--BgLang:ManifestSha256", hash],
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
    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")]
    [DataRow("2")]
    public async Task Productive_graph_http_formats_preserve_nonempty_roles_and_original_source_references(string version)
    {
        await WithPackage(async (_, session, client) =>
        {
            var cases = new[]
            {
                (Text: "Свръхсупертракторите работят.", Plural: true, Mismatch: false, Repeated: false),
                (Text: "😀 Супертрактор работи.\nСупертрактор работи. ѝ и\u0300.", Plural: false, Mismatch: false, Repeated: true),
                (Text: "Супертрактор работят.", Plural: false, Mismatch: true, Repeated: false)
            };
            foreach (var example in cases)
            {
                // Warm the existing session before paired representation comparisons.
                // Every result asserted below is obtained through the actual HTTP endpoint.
                session.AnalyzeText(example.Text);
                using JsonDocument raw = await PostGraphEncoded(client, version, example.Text, HttpContract.RawFormat);
                AssertGraphRepresentation(raw.RootElement, version, HttpContract.RawFormat, example.Repeated);
                AssertProductiveGraphHttpData(raw.RootElement.GetProperty("data"), example.Text,
                    example.Plural, example.Mismatch, example.Repeated);

                // The explicit raw request above is the fourth matrix row.
                foreach (string? requested in new string?[]
                {
                    null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat
                })
                {
                    using JsonDocument encoded = await PostGraphEncoded(client, version, example.Text, requested);
                    string effective = requested ?? (version == "2"
                        ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat);
                    AssertGraphRepresentation(encoded.RootElement, version, effective, example.Repeated);
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                        "Reconstruction must preserve the whole HTTP envelope, original graph proofs, flags, tags and supports.");
                    AssertProductiveGraphHttpData(restored.RootElement.GetProperty("data"), example.Text,
                        example.Plural, example.Mismatch, example.Repeated);
                }
            }
        });
    }

    private static async Task<JsonDocument> PostGraphEncoded(HttpClient client, string version,
        string text, string? requested)
    {
        string json = requested is null
            ? JsonSerializer.Serialize(new { text }, HttpContract.Json)
            : JsonSerializer.Serialize(new { text, evidenceFormat = requested }, HttpContract.Json);
        using HttpResponseMessage response = await Post(client, "/api/v" + version + "/analyze-text", json);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private static void AssertGraphRepresentation(JsonElement envelope, string version,
        string effective, bool repeated)
    {
        Assert.AreEqual(version, envelope.GetProperty("apiVersion").GetString());
        JsonElement[] tokens = envelope.GetProperty("data").GetProperty("tokens").EnumerateArray().ToArray();
        if (effective == HttpContract.RawFormat)
        {
            Assert.IsFalse(envelope.TryGetProperty("evidenceFormat", out _));
            Assert.IsFalse(envelope.TryGetProperty("donorSets", out _));
            Assert.IsFalse(envelope.TryGetProperty("wordAnalyses", out _));
            Assert.IsTrue(tokens.All(token => token.TryGetProperty("analysis", out _) &&
                !token.TryGetProperty("analysisRef", out _)));
            return;
        }

        Assert.AreEqual(effective, envelope.GetProperty("evidenceFormat").GetString());
        Assert.AreEqual(JsonValueKind.Array, envelope.GetProperty("donorSets").ValueKind);
        Assert.IsTrue(envelope.GetProperty("donorSets").GetArrayLength() > 0,
            "These real graph witnesses retain conditional donor evidence as well as unconditional supports.");
        if (effective == SharedDonorResponse.Format)
        {
            Assert.IsFalse(envelope.TryGetProperty("wordAnalyses", out _));
            Assert.IsTrue(tokens.All(token => token.TryGetProperty("analysis", out _) &&
                !token.TryGetProperty("analysisRef", out _)));
            return;
        }

        JsonElement analyses = envelope.GetProperty("wordAnalyses");
        Assert.AreEqual(JsonValueKind.Array, analyses.ValueKind);
        Assert.IsTrue(tokens.All(token => !token.TryGetProperty("analysis", out _) &&
            token.TryGetProperty("analysisRef", out _)));
        foreach (JsonElement token in tokens)
        {
            JsonElement reference = token.GetProperty("analysisRef");
            if (token.GetProperty("kind").GetString() == "Word")
                Assert.IsTrue(reference.GetInt32() >= 0 && reference.GetInt32() < analyses.GetArrayLength());
            else Assert.AreEqual(JsonValueKind.Null, reference.ValueKind);
        }
        if (repeated)
        {
            Assert.AreEqual(tokens[1].GetProperty("analysisRef").GetInt32(),
                tokens[4].GetProperty("analysisRef").GetInt32());
            Assert.AreEqual(tokens[2].GetProperty("analysisRef").GetInt32(),
                tokens[5].GetProperty("analysisRef").GetInt32());
            Assert.AreNotEqual(tokens[7].GetProperty("analysisRef").GetInt32(),
                tokens[8].GetProperty("analysisRef").GetInt32(),
                "Raw ordinal surfaces ѝ and и\u0300 retain separate acquired analyses.");
            Assert.AreEqual(0, tokens[4].GetProperty("analysisRef").GetInt32());
            Assert.AreEqual(4, analyses.GetArrayLength());
        }
    }

    private static void AssertProductiveGraphHttpData(JsonElement data, string input,
        bool plural, bool mismatch, bool repeated)
    {
        Assert.AreEqual(input, data.GetProperty("text").GetString());
        JsonElement[] tokens = data.GetProperty("tokens").EnumerateArray().ToArray();
        for (int index = 0; index < tokens.Length; index++)
        {
            JsonElement token = tokens[index];
            int start = token.GetProperty("start").GetInt32();
            int length = token.GetProperty("length").GetInt32();
            Assert.IsTrue(start >= 0 && length >= 0 && start <= input.Length - length);
            Assert.AreEqual(input.Substring(start, length), token.GetProperty("text").GetString());
        }
        if (repeated)
        {
            CollectionAssert.AreEqual(new[]
            {
                "0:2:😀", "3:12:Супертрактор", "16:6:работи", "22:1:.",
                "24:12:Супертрактор", "37:6:работи", "43:1:.", "45:1:ѝ", "47:2:и\u0300", "49:1:."
            }, tokens.Select(token =>
                $"{token.GetProperty("start").GetInt32()}:{token.GetProperty("length").GetInt32()}:{token.GetProperty("text").GetString()}").ToArray());
            Assert.IsTrue(SharedDonorResponseTests.SameJson(tokens[1].GetProperty("analysis"),
                tokens[4].GetProperty("analysis")));
        }

        int subjectToken = repeated ? 4 : 0;
        int predicateToken = repeated ? 5 : 1;
        int expectedSubjectStart = repeated ? 24 : 0;
        string lemma = plural ? "свръхсупертрактор" : "супертрактор";
        string tag = plural ? "Съществително:7,Мъжки,мн.ч.,Членуван" : "Съществително:7,Мъжки";
        JsonElement subject = tokens[subjectToken];
        JsonElement analysis = subject.GetProperty("analysis");
        Assert.AreEqual(expectedSubjectStart, subject.GetProperty("start").GetInt32());
        Assert.IsFalse(analysis.GetProperty("isKnown").GetBoolean());
        Assert.AreEqual(0, analysis.GetProperty("readings").GetArrayLength());
        Assert.IsTrue(analysis.GetProperty("graphExactRootCandidatesComplete").GetBoolean());
        Assert.IsFalse(analysis.GetProperty("graphSearchIncomplete").GetBoolean());
        Assert.AreEqual(plural, analysis.GetProperty("graphCandidatesTruncated").GetBoolean());
        if (plural) Assert.AreEqual(32, analysis.GetProperty("graphCandidates").GetArrayLength());

        JsonElement inventory = data.GetProperty("readingInventory");
        JsonElement occurrence = inventory.GetProperty("occurrences").EnumerateArray()
            .Single(row => row.GetProperty("tokenIndex").GetInt32() == subjectToken);
        Assert.AreEqual(subject.GetProperty("text").GetString(), occurrence.GetProperty("text").GetString());
        Assert.AreEqual(expectedSubjectStart, occurrence.GetProperty("start").GetInt32());
        Assert.AreEqual(subject.GetProperty("length").GetInt32(), occurrence.GetProperty("length").GetInt32());
        if (repeated)
        {
            JsonElement firstOccurrence = inventory.GetProperty("occurrences").EnumerateArray()
                .Single(row => row.GetProperty("tokenIndex").GetInt32() == 1);
            Assert.AreEqual(firstOccurrence.GetProperty("readingSetIndex").GetInt32(),
                occurrence.GetProperty("readingSetIndex").GetInt32());
            Assert.AreNotEqual(firstOccurrence.GetProperty("start").GetInt32(), occurrence.GetProperty("start").GetInt32());
        }
        JsonElement set = inventory.GetProperty("sets")[occurrence.GetProperty("readingSetIndex").GetInt32()];
        JsonElement exactRows = set.GetProperty("supportReadings").ValueKind == JsonValueKind.Null
            ? set.GetProperty("canonicalReadings") : set.GetProperty("supportReadings");
        Assert.AreEqual(0, exactRows.GetArrayLength(), "Graph supports must not be inserted into the exact inventory.");
        Assert.AreEqual(0, set.GetProperty("displayedReadingCount").GetInt32());

        JsonElement context = data.GetProperty("context");
        JsonElement syntax = data.GetProperty("syntax");
        foreach ((JsonElement evidence, string version) in new[]
        {
            (context, "bounded-context.v2"), (syntax, "bounded-syntax-roles.v2"),
            (data.GetProperty("validation"), "bounded-validation.v2")
        })
        {
            Assert.AreEqual(version, evidence.GetProperty("version").GetString());
            Assert.IsTrue(evidence.GetProperty("available").GetBoolean());
        }

        JsonElement[] candidates = context.GetProperty("sentences").EnumerateArray()
            .SelectMany(sentence => sentence.GetProperty("candidates").EnumerateArray())
            .Where(candidate => candidate.GetProperty("authorityLane").GetString() == "productive_subject_predicate_graph.v1")
            .ToArray();
        Assert.IsTrue(candidates.Length > 0, "A real complete-package graph-only word must reach Context.");
        Assert.IsTrue(candidates.Any(candidate => candidate.GetProperty("roles").EnumerateArray().Any(role =>
            role.GetProperty("role").GetString() == "Subject" &&
            role.GetProperty("tokenIndex").GetInt32() == subjectToken &&
            role.TryGetProperty("graphReadings", out JsonElement graph) && graph.GetArrayLength() > 0)));
        Assert.IsTrue(candidates.Any(candidate => candidate.GetProperty("roles").EnumerateArray().Any(role =>
            role.GetProperty("role").GetString() == "Predicate" &&
            role.GetProperty("tokenIndex").GetInt32() == predicateToken &&
            role.GetProperty("readings").GetArrayLength() > 0)));

        int contextGraphReferences = 0;
        foreach (JsonElement sentence in context.GetProperty("sentences").EnumerateArray())
        foreach (JsonElement role in sentence.GetProperty("localRoles").EnumerateArray()
            .Concat(sentence.GetProperty("selectedRoles").EnumerateArray())
            .Concat(sentence.GetProperty("candidates").EnumerateArray()
                .SelectMany(candidate => candidate.GetProperty("roles").EnumerateArray())))
        {
            if (!role.TryGetProperty("graphReadings", out JsonElement graph)) continue;
            Assert.AreEqual(subjectToken, role.GetProperty("tokenIndex").GetInt32());
            Assert.AreEqual(expectedSubjectStart, role.GetProperty("start").GetInt32());
            Assert.AreEqual(subject.GetProperty("length").GetInt32(), role.GetProperty("length").GetInt32());
            int sentenceStart = sentence.GetProperty("start").GetInt32();
            int sentenceEnd = sentenceStart + sentence.GetProperty("length").GetInt32();
            Assert.IsTrue(expectedSubjectStart >= sentenceStart &&
                expectedSubjectStart + subject.GetProperty("length").GetInt32() <= sentenceEnd);
            Assert.AreEqual(0, role.GetProperty("readings").GetArrayLength());
            Assert.IsTrue(graph.GetArrayLength() > 0);
            foreach (JsonElement reference in graph.EnumerateArray()) { CheckGraphReference(reference); contextGraphReferences++; }
        }
        Assert.IsTrue(contextGraphReferences > 0);

        JsonElement[] productiveSyntax = syntax.GetProperty("sentences").EnumerateArray()
            .SelectMany(sentence => sentence.GetProperty("candidates").EnumerateArray())
            .Where(candidate => candidate.GetProperty("ruleId").GetString() is "ProductiveGraphSubject" or "ProductiveGraphPredicate")
            .ToArray();
        CollectionAssert.AreEquivalent(new[] { "Subject", "Predicate" },
            productiveSyntax.Select(candidate => candidate.GetProperty("role").GetString()!).Distinct().ToArray());
        foreach (JsonElement candidate in productiveSyntax)
        {
            int target = candidate.GetProperty("role").GetString() == "Subject" ? subjectToken : predicateToken;
            Assert.AreEqual(tokens[target].GetProperty("start").GetInt32(), candidate.GetProperty("start").GetInt32());
            Assert.AreEqual(tokens[target].GetProperty("length").GetInt32(), candidate.GetProperty("length").GetInt32());
        }
        int syntaxGraphReferences = 0;
        foreach (JsonElement candidate in productiveSyntax)
        foreach (JsonElement support in candidate.GetProperty("targets").EnumerateArray()
            .Concat(candidate.GetProperty("context").EnumerateArray()))
        {
            int owner = support.GetProperty("tokenIndex").GetInt32();
            if (support.TryGetProperty("graphReadings", out JsonElement graph))
            {
                Assert.AreEqual(subjectToken, owner);
                Assert.AreEqual("LicensedGraphMorphology", support.GetProperty("basis").GetString());
                Assert.AreEqual(0, support.GetProperty("readings").GetArrayLength());
                Assert.IsTrue(graph.GetArrayLength() > 0);
                foreach (JsonElement reference in graph.EnumerateArray()) { CheckGraphReference(reference); syntaxGraphReferences++; }
            }
            else
            {
                Assert.AreEqual(predicateToken, owner);
                Assert.IsTrue(support.GetProperty("readings").GetArrayLength() > 0);
                foreach (JsonElement reference in support.GetProperty("readings").EnumerateArray()) CheckExactReference(reference, owner);
            }
        }
        Assert.IsTrue(syntaxGraphReferences > 0);

        JsonElement[] diagnostics = data.GetProperty("diagnostics").EnumerateArray()
            .Where(row => row.GetProperty("code").GetString() == "productive_morphology_agreement_mismatch").ToArray();
        Assert.AreEqual(mismatch ? 1 : 0, diagnostics.Length);
        foreach (JsonElement diagnostic in diagnostics)
        {
            Assert.AreEqual("productive_subject_predicate_graph.v1", diagnostic.GetProperty("ruleId").GetString());
            Assert.AreEqual(expectedSubjectStart, diagnostic.GetProperty("start").GetInt32());
            Assert.AreEqual(subject.GetProperty("length").GetInt32(), diagnostic.GetProperty("length").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, diagnostic.GetProperty("projectedText").ValueKind);
            Assert.IsTrue(diagnostic.GetProperty("graphReadings").GetArrayLength() > 0);
            Assert.IsTrue(diagnostic.GetProperty("readings").GetArrayLength() > 0);
            foreach (JsonElement reference in diagnostic.GetProperty("graphReadings").EnumerateArray()) CheckGraphReference(reference);
            foreach (JsonElement reference in diagnostic.GetProperty("readings").EnumerateArray()) CheckExactReference(reference, predicateToken);
        }

        void CheckGraphReference(JsonElement reference)
        {
            CollectionAssert.AreEquivalent(new[] { "tokenIndex", "graphCandidateIndex", "fullTagIndex" },
                reference.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual(subjectToken, reference.GetProperty("tokenIndex").GetInt32());
            Assert.AreEqual(0, reference.GetProperty("graphCandidateIndex").GetInt32());
            Assert.AreEqual(0, reference.GetProperty("fullTagIndex").GetInt32());
            JsonElement path = tokens[reference.GetProperty("tokenIndex").GetInt32()]
                .GetProperty("analysis").GetProperty("graphCandidates")[reference.GetProperty("graphCandidateIndex").GetInt32()];
            Assert.AreEqual("трактор", path.GetProperty("base").GetString());
            Assert.AreEqual(lemma, path.GetProperty("derivedLemma").GetString());
            Assert.AreEqual(analysis.GetProperty("normalizedWord").GetString(), path.GetProperty("word").GetString());
            Assert.AreEqual(tag, path.GetProperty("fullTags")[reference.GetProperty("fullTagIndex").GetInt32()].GetString());
            Assert.IsFalse(path.TryGetProperty("isConditional", out JsonElement conditional) && conditional.GetBoolean());
            Assert.IsFalse(path.TryGetProperty("unmetExactSelfReadingRequirements", out JsonElement unmet) && unmet.GetArrayLength() > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(path.GetProperty("source").GetString()));
            Assert.IsTrue(path.GetProperty("edgeIds").GetArrayLength() > 0);
            Assert.IsTrue(path.GetProperty("ruleIds").GetArrayLength() > 0);
            Assert.IsTrue(path.GetProperty("evidenceCodes").GetArrayLength() > 0);
        }

        void CheckExactReference(JsonElement reference, int owner)
        {
            Assert.AreEqual(owner, reference.GetProperty("tokenIndex").GetInt32());
            JsonElement exactOccurrence = inventory.GetProperty("occurrences").EnumerateArray()
                .Single(row => row.GetProperty("tokenIndex").GetInt32() == owner);
            JsonElement exactSet = inventory.GetProperty("sets")[exactOccurrence.GetProperty("readingSetIndex").GetInt32()];
            JsonElement rows = exactSet.GetProperty("supportReadings").ValueKind == JsonValueKind.Null
                ? exactSet.GetProperty("canonicalReadings") : exactSet.GetProperty("supportReadings");
            int index = reference.GetProperty("completeReadingIndex").GetInt32();
            Assert.IsTrue(index >= 0 && index < rows.GetArrayLength());
            Assert.AreEqual(tokens[owner].GetProperty("text").GetString(), exactOccurrence.GetProperty("text").GetString());
        }
    }


    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Nominal_phrase_http_formats_preserve_nonempty_attribute_head_and_relation_specific_negatives(string version)
    {
        await NominalMatrix(version,
        [
            ("Суперхубав супертрактор работи.", 1, [0], null),
            ("Новият голям червен супертрактор работи.", 3, [0, 1, 2], null),
            ("Суперхубав суперкнига работи.", 1, [0], "productive_attribute_agreement_mismatch"),
            ("Суперхубав супертрактор работят.", 1, [0], "productive_morphology_agreement_mismatch")
        ]);
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Nominal_phrase_http_formats_preserve_plural_scope_and_original_unicode_aliases(string version)
    {
        await NominalMatrix(version,
        [
            ("Нови супертрактори работят.", 1, [0], null),
            ("😀 Суперхубав супертрактор работи.\nСуперхубав супертрактор работи. ѝ и\u0300.", 6, [5], null)
        ]);
    }

    private static async Task NominalMatrix(string version,
        (string Text, int Head, int[] Attributes, string? Diagnostic)[] cases)
    {
        await WithPackage(async (_, session, client) =>
        {
            foreach (var example in cases)
            {
                session.AnalyzeText(example.Text);
                using JsonDocument raw = await PostGraphEncoded(client, version, example.Text, HttpContract.RawFormat);
                AssertNominalHttp(raw.RootElement.GetProperty("data"), example.Text, example.Head, example.Attributes, example.Diagnostic);
                foreach (string? requested in new string?[] { null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    using JsonDocument encoded = await PostGraphEncoded(client, version, example.Text, requested);
                    string effective = requested ?? (version == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat);
                    AssertGraphRepresentation(encoded.RootElement, version, effective, repeated: false);
                    if (example.Text.StartsWith("😀", StringComparison.Ordinal) && effective == SharedDonorResponse.AnalysisFormat)
                    {
                        JsonElement tokens = encoded.RootElement.GetProperty("data").GetProperty("tokens");
                        foreach ((int first, int second) in new[] { (1, 5), (2, 6), (3, 7) })
                            Assert.AreEqual(tokens[first].GetProperty("analysisRef").GetInt32(), tokens[second].GetProperty("analysisRef").GetInt32());
                        Assert.AreNotEqual(tokens[9].GetProperty("analysisRef").GetInt32(), tokens[10].GetProperty("analysisRef").GetInt32());
                        Assert.AreEqual(5, encoded.RootElement.GetProperty("wordAnalyses").GetArrayLength());
                    }
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement), "The whole envelope and original proofs must survive every representation.");
                    AssertNominalHttp(restored.RootElement.GetProperty("data"), example.Text, example.Head, example.Attributes, example.Diagnostic);
                }
            }
        });
    }

    private static void AssertNominalHttp(JsonElement data, string text, int head, int[] attributes, string? diagnostic)
    {
        JsonElement tokens = data.GetProperty("tokens");
        foreach (JsonElement token in tokens.EnumerateArray())
            Assert.AreEqual(token.GetProperty("text").GetString(), text.Substring(token.GetProperty("start").GetInt32(), token.GetProperty("length").GetInt32()));
        foreach ((string name, string version) in new[] { ("context", "bounded-context.v2"), ("syntax", "bounded-syntax-roles.v2"), ("validation", "bounded-validation.v2") })
        {
            Assert.IsTrue(data.GetProperty(name).GetProperty("available").GetBoolean());
            Assert.AreEqual(version, data.GetProperty(name).GetProperty("version").GetString());
        }
        JsonElement candidate = data.GetProperty("context").GetProperty("sentences").EnumerateArray()
            .SelectMany(s => s.GetProperty("candidates").EnumerateArray()).Single(c =>
                c.GetProperty("authorityLane").GetString() == "productive_nominal_phrase_graph.v1" && c.GetProperty("predicateTokenIndex").GetInt32() == head + 1);
        Assert.AreEqual(diagnostic is null, candidate.GetProperty("selectable").GetBoolean());
        JsonElement[] roles = candidate.GetProperty("roles").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(attributes, roles.Where(r => r.GetProperty("role").GetString() == "Attribute").Select(r => r.GetProperty("tokenIndex").GetInt32()).ToArray());
        Assert.AreEqual(head, roles.Single(r => r.GetProperty("role").GetString() == "Subject").GetProperty("tokenIndex").GetInt32());
        JsonElement headAnalysis = tokens[head].GetProperty("analysis");
        Assert.IsFalse(headAnalysis.GetProperty("isKnown").GetBoolean()); Assert.AreEqual(0, headAnalysis.GetProperty("readings").GetArrayLength());
        Assert.IsTrue(headAnalysis.GetProperty("graphExactRootCandidatesComplete").GetBoolean()); Assert.IsFalse(headAnalysis.GetProperty("graphSearchIncomplete").GetBoolean());
        if (text.StartsWith("Нови ", StringComparison.Ordinal)) Assert.IsTrue(headAnalysis.GetProperty("graphCandidatesTruncated").GetBoolean());
        foreach (JsonElement role in roles)
        {
            int owner = role.GetProperty("tokenIndex").GetInt32();
            Assert.AreEqual(tokens[owner].GetProperty("start").GetInt32(), role.GetProperty("start").GetInt32());
            Assert.AreEqual(tokens[owner].GetProperty("length").GetInt32(), role.GetProperty("length").GetInt32());
            CheckSupport(role, owner);
        }
        foreach (int attribute in attributes)
        {
            JsonElement role = roles.Single(r => r.GetProperty("tokenIndex").GetInt32() == attribute);
            if (tokens[attribute].GetProperty("text").GetString() == "Суперхубав")
                Assert.IsTrue(role.GetProperty("graphReadings").GetArrayLength() > 0);
            else Assert.IsTrue(role.GetProperty("readings").GetArrayLength() > 0);
        }
        Assert.IsTrue(roles.Single(r => r.GetProperty("role").GetString() == "Subject").GetProperty("graphReadings").GetArrayLength() > 0);
        JsonElement[] syntax = data.GetProperty("syntax").GetProperty("sentences").EnumerateArray().SelectMany(s => s.GetProperty("candidates").EnumerateArray())
            .Where(c => c.GetProperty("ruleId").GetString() == "ProductiveNominalPhraseAttribute" &&
                attributes.Contains(c.GetProperty("targets")[0].GetProperty("tokenIndex").GetInt32())).ToArray();
        Assert.AreEqual(attributes.Length, syntax.Length);
        foreach (JsonElement attribute in syntax)
        {
            Assert.AreEqual(head, attribute.GetProperty("context")[0].GetProperty("tokenIndex").GetInt32());
            CheckSupport(attribute.GetProperty("targets")[0], attribute.GetProperty("targets")[0].GetProperty("tokenIndex").GetInt32());
            CheckSupport(attribute.GetProperty("context")[0], head);
        }
        JsonElement[] diagnostics = data.GetProperty("diagnostics").EnumerateArray().Where(d =>
            d.GetProperty("code").GetString() is "productive_attribute_agreement_mismatch" or "productive_morphology_agreement_mismatch").ToArray();
        Assert.AreEqual(diagnostic is null ? 0 : 1, diagnostics.Length);
        if (diagnostic is not null)
        {
            JsonElement actual = diagnostics.Single(); Assert.AreEqual(diagnostic, actual.GetProperty("code").GetString());
            Assert.AreEqual("productive_nominal_phrase_graph.v1", actual.GetProperty("ruleId").GetString());
            Assert.AreEqual(JsonValueKind.Null, actual.GetProperty("projectedText").ValueKind);
            foreach (JsonElement r in actual.GetProperty("graphReadings").EnumerateArray())
            {
                int owner = r.GetProperty("tokenIndex").GetInt32();
                Assert.IsTrue(diagnostic == "productive_attribute_agreement_mismatch" ? owner == head || attributes.Contains(owner) : owner == head);
                CheckGraph(r, owner);
            }
        }
        void CheckSupport(JsonElement support, int owner)
        {
            JsonElement exact = support.GetProperty("readings");
            bool graph = support.TryGetProperty("graphReadings", out JsonElement refs) && refs.ValueKind == JsonValueKind.Array && refs.GetArrayLength() > 0;
            Assert.IsTrue(exact.GetArrayLength() > 0 || graph);
            if (graph)
            {
                Assert.AreEqual(0, exact.GetArrayLength());
                foreach (JsonElement r in refs.EnumerateArray()) CheckGraph(r, owner);
            }
            else foreach (JsonElement r in exact.EnumerateArray())
            {
                Assert.AreEqual(owner, r.GetProperty("tokenIndex").GetInt32());
                JsonElement inventory = data.GetProperty("readingInventory");
                JsonElement occurrence = inventory.GetProperty("occurrences").EnumerateArray().Single(o => o.GetProperty("tokenIndex").GetInt32() == owner);
                JsonElement set = inventory.GetProperty("sets")[occurrence.GetProperty("readingSetIndex").GetInt32()];
                JsonElement rows = set.GetProperty("supportReadings").ValueKind == JsonValueKind.Null ? set.GetProperty("canonicalReadings") : set.GetProperty("supportReadings");
                int index = r.GetProperty("completeReadingIndex").GetInt32(); Assert.IsTrue(index >= 0 && index < rows.GetArrayLength());
            }
        }
        void CheckGraph(JsonElement reference, int owner)
        {
            Assert.AreEqual(owner, reference.GetProperty("tokenIndex").GetInt32());
            JsonElement analysis = tokens[owner].GetProperty("analysis");
            JsonElement path = analysis.GetProperty("graphCandidates")[reference.GetProperty("graphCandidateIndex").GetInt32()];
            Assert.IsFalse(path.TryGetProperty("isConditional", out JsonElement conditional) && conditional.GetBoolean());
            Assert.AreEqual("word-formation-source-v0", path.GetProperty("source").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(path.GetProperty("derivedLemma").GetString()));
            string tag = path.GetProperty("fullTags")[reference.GetProperty("fullTagIndex").GetInt32()].GetString()!;
            Assert.IsFalse(string.IsNullOrWhiteSpace(tag)); Assert.AreEqual(0, analysis.GetProperty("readings").GetArrayLength());
        }
    }

}
