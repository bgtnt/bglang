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
public sealed class TextCompositionOptInHttpTests
{
    [TestMethod]
    public void Nullable_option_parsing_and_applicability_keep_omitted_null_and_false_distinct()
    {
        foreach (JsonElement item in Root.GetProperty("optInInputs").EnumerateArray())
        {
            WebInput input = JsonSerializer.Deserialize<WebInput>(item.GetProperty("json").GetString()!, HttpContract.Json)!;
            ApiOperations.Validate("analyze-text", input);
            Assert.AreEqual(item.GetProperty("enabled").GetBoolean(), input.IncludeUnknownContextProperNames ?? false);
            if (item.GetProperty("id").GetString() is "omitted" or "null") Assert.IsNull(input.IncludeUnknownContextProperNames);
        }
        foreach (JsonElement operation in Root.GetProperty("nonAnalyzeTextOperations").EnumerateArray())
        {
            string name = operation.GetString()!;
            string? query = name == "find-forms-in-text" ? "книга" : null;
            ApiOperations.Validate(name, new("книга", Query: query, IncludeUnknownContextProperNames: null));
            foreach (bool flag in new[] { false, true })
            {
                ApiRequestException error = Assert.Throws<ApiRequestException>(() =>
                    ApiOperations.Validate(name, new("книга", Query: query, IncludeUnknownContextProperNames: flag)));
                Assert.AreEqual(400, error.Status); Assert.AreEqual("option-not-applicable", error.Code);
            }
        }
        foreach (JsonElement invalid in Root.GetProperty("invalidInputs").EnumerateArray())
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(invalid.GetProperty("json").GetString()!, HttpContract.Json));
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Omitted_null_false_and_true_use_existing_routes_and_default_formats(string version)
    {
        await WithPackage(async (app, session, client) =>
        {
            foreach (JsonElement item in Root.GetProperty("optInInputs").EnumerateArray())
            {
                bool enabled = item.GetProperty("enabled").GetBoolean();
                using HttpResponseMessage response = await Post(client, "/api/v" + version + "/analyze-text", item.GetProperty("json").GetString()!);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                if (version == "1") Assert.IsFalse(encoded.RootElement.TryGetProperty("evidenceFormat", out _));
                else Assert.AreEqual("shared-analyses-v1", encoded.RootElement.GetProperty("evidenceFormat").GetString());
                using JsonDocument restored = Restore(encoded);
                JsonElement text = restored.RootElement.GetProperty("data");
                CollectionAssert.AreEqual(TextFields, text.EnumerateObject().Select(property => property.Name).ToArray());
                Assert.AreEqual(1, text.GetProperty("profile").GetProperty("knownWordOccurrences").GetInt32());
                Assert.IsTrue(text.GetProperty("readingInventory").GetProperty("sets")[0].GetProperty("canonicalReadings").GetArrayLength() > 0);
                Assert.AreEqual(enabled, text.GetProperty("properNames").GetProperty("unknownContextEvaluated").GetBoolean());
                JsonElement unresolved = text.GetProperty("properNames").GetProperty("unresolvedContextTokens");
                Assert.AreEqual(enabled ? JsonValueKind.Array : JsonValueKind.Null, unresolved.ValueKind);
                if (enabled) Assert.AreEqual(0, unresolved.GetArrayLength(), "Exact non-PROPN книга suppresses unknown-name guessing.");
            }
            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            using var token = new CancellationTokenSource();
            foreach (bool? flag in new bool?[] { null, false, true })
            {
                int responses = 0;
                await host.ExecuteResponseAsync("analyze-text", new("книга", IncludeUnknownContextProperNames: flag), (result, actualToken) =>
                {
                    responses++; Assert.AreEqual(token.Token, actualToken);
                    Assert.AreEqual(flag ?? false, ((TextAnalysis)result.Data).ProperNames!.UnknownContextEvaluated);
                    return Task.CompletedTask;
                }, token.Token);
                Assert.AreEqual(1, responses);
            }
            Assert.IsNull(session.AnalyzeText("книга", default).ProperNames!.UnresolvedContextTokens,
                "Existing two-argument default expression binds CancellationToken without ambiguity.");
            using HttpResponseMessage find = await Post(client, "/api/v" + version + "/find-forms-in-text",
                "{\"text\":\"книга\",\"query\":\"книга\",\"includeUnknownContextProperNames\":null}");
            Assert.AreEqual(HttpStatusCode.OK, find.StatusCode);
            using JsonDocument findEncoded = JsonDocument.Parse(await find.Content.ReadAsByteArrayAsync());
            using JsonDocument findRestored = Restore(findEncoded);
            JsonElement nested = findRestored.RootElement.GetProperty("data").GetProperty("textAnalysis");
            CollectionAssert.AreEqual(TextFields, nested.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual(JsonValueKind.Null, nested.GetProperty("properNames").GetProperty("unresolvedContextTokens").ValueKind);
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Invalid_options_and_json_are_rejected_while_language_lease_is_still_occupied(string version)
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
                foreach (JsonElement invalid in Root.GetProperty("invalidInputs").EnumerateArray())
                {
                    await Rejected("analyze-text", invalid.GetProperty("json").GetString()!, "invalid-json");
                    Assert.IsFalse(active.IsCompleted, "Validation must finish before waiting for the language lease.");
                }
                foreach (JsonElement item in Root.GetProperty("nonAnalyzeTextOperations").EnumerateArray())
                foreach (bool flag in new[] { false, true })
                {
                    string operation = item.GetString()!;
                    WebInput input = new("книга", Query: operation == "find-forms-in-text" ? "книга" : null,
                        IncludeUnknownContextProperNames: flag);
                    await Rejected(operation, JsonSerializer.Serialize(input, HttpContract.Json), "option-not-applicable");
                    Assert.IsFalse(active.IsCompleted);
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
                Assert.IsFalse(error.RootElement.TryGetProperty("data", out JsonElement ignoredData));
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow(false)][DataRow(true)]
    public async Task Opt_in_text_cancellation_drains_before_response_and_next_session_request(bool enabled)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest)); Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        string dense = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "dense-unknown-text.v1.txt"));
        Assert.AreEqual(4079, dense.Length); Assert.AreEqual(288, dense.Split(' ').Distinct(StringComparer.Ordinal).Count());
        await using var host = new SessionHost(manifest, hash);
        await host.StartAsync(CancellationToken.None);
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => host.ExecuteResponseAsync("analyze-text",
                new("книга", IncludeUnknownContextProperNames: enabled),
                (_, _) => throw new AssertFailedException("Precancelled work must not respond."), before.Token));
        }
        await host.ExecuteResponseAsync("lookup", new("книга"), (_, _) => Task.CompletedTask, CancellationToken.None);
        using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        bool responded = false;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => host.ExecuteResponseAsync("analyze-text",
            new(dense, IncludeUnknownContextProperNames: enabled), (_, _) => { responded = true; return Task.CompletedTask; }, cancellation.Token));
        Assert.IsTrue(cancellation.IsCancellationRequested); Assert.IsFalse(responded);
        await host.ExecuteResponseAsync("lookup", new("книга"), (result, _) =>
        { Assert.IsTrue(((IReadOnlyList<WordReading>)result.Data).Count > 0); return Task.CompletedTask; }, CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
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
