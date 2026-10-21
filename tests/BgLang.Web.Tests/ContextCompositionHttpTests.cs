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

    private static void AssertOptions(JsonElement data, TextContextOptions options)
    {
        Assert.AreEqual(JsonValueKind.Object, data.GetProperty("context").ValueKind);
        foreach ((string field, string version) in new[] { ("context", "bounded-context.v1"),
            ("syntax", "bounded-syntax-roles.v1"), ("validation", "bounded-validation.v1") })
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
    private static async Task WithPackage(Func<WebApplication, BulgarianLanguageSession, HttpClient, Task> verify)
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
}
