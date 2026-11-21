using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using BgLang.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class GraphStressSuccessorHttpTests
{
    [TestMethod, DataRow("1"), DataRow("2")]
    public async Task Explicit_graph_stress_successor_keeps_unknown_owner_evidence_in_all_formats(string version)
    {
        string manifest = GraphStressAcceptance.Manifest; GraphStressAcceptance.AssertBinding(manifest);
        string hash = GraphStressAcceptance.ManifestSha;
        var clock = new ManualClock();
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => BulgarianLanguageSession.Open(manifest), clock);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            var rawByOperation = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
            foreach ((string operation, string text) in new[] { ("analyze-word", "колажист"), ("analyze-text", "😀 колажист!"), ("analyze-text", "😀 колажи\u0301ст!") })
            {
                string requestKey = operation + ":" + text;
                clock.Now = clock.Now.AddMinutes(1);
                using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/" + operation,
                    new WebInput(text, EvidenceFormat: format, Context: operation == "analyze-text"
                        ? new TextContextOptions { IncludeNominalConstructions = true } : null), HttpContract.Json);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                using JsonDocument restored = Restore(encoded);
                var root = restored.RootElement;
                if (format == HttpContract.RawFormat)
                {
                    Assert.IsFalse(encoded.RootElement.TryGetProperty("evidenceFormat", out _));
                    Assert.IsFalse(encoded.RootElement.TryGetProperty("donorSets", out _));
                    Assert.IsFalse(encoded.RootElement.TryGetProperty("wordAnalyses", out _));
                    rawByOperation.Add(requestKey, root.Clone());
                }
                else
                {
                    Assert.AreEqual(format, encoded.RootElement.GetProperty("evidenceFormat").GetString());
                    Assert.AreEqual(JsonValueKind.Array, encoded.RootElement.GetProperty("donorSets").ValueKind);
                    if (format == SharedDonorResponse.AnalysisFormat && operation == "analyze-text")
                        Assert.AreEqual(JsonValueKind.Array, encoded.RootElement.GetProperty("wordAnalyses").ValueKind);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(rawByOperation[requestKey], root),
                        "Reconstruction preserves every field, raw reading, donor alternative, index, limit and coverage value.");
                }
                Assert.AreEqual(version, root.GetProperty("apiVersion").GetString());
                Assert.AreEqual(GraphStressAcceptance.Package.GetProperty("packageId").GetString(), root.GetProperty("package").GetProperty("packageId").GetString());
                Assert.AreEqual(hash, root.GetProperty("package").GetProperty("manifestSha256").GetString()!.ToLowerInvariant());
                Assert.AreEqual(GraphStressAcceptance.Enabled ? "bglang-word-formation:automatic-graph-stress-ist.v1" : "word-formation-source-v0",
                    root.GetProperty("package").GetProperty("wordFormationSource").GetString());
                string surface = text.Contains("колажи\u0301ст", StringComparison.Ordinal) ? "колажи\u0301ст" : "колажист";
                JsonElement wordJson;
                if (operation == "analyze-word") wordJson = root.GetProperty("data");
                else
                {
                    TextAnalysis analysis = root.GetProperty("data").Deserialize<TextAnalysis>(HttpContract.Json)!;
                    Assert.AreEqual(text, analysis.Text);
                    GraphStressAcceptance.AssertNominal(analysis, surface, surface == "колажист" ? 8 : 9);
                    wordJson = root.GetProperty("data").GetProperty("tokens").EnumerateArray()
                        .Single(row => row.GetProperty("text").GetString() == surface).GetProperty("analysis");
                }
                GraphStressAcceptance.AssertWord(wordJson.Deserialize<WordAnalysis>(HttpContract.Json)!, surface);
                Assert.AreEqual(GraphStressAcceptance.Enabled, wordJson.TryGetProperty("graphStress", out _), "Null evidence remains omitted.");
            }
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
    }
}
