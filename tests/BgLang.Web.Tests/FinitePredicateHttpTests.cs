using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class FinitePredicateHttpTests
{
    [TestMethod, TestCategory("FullPackage"), DataRow("1"), DataRow("2")]
    public async Task Default_finite_evidence_and_no_rewrite_warning_round_trip_every_existing_wire_format(string version)
    {
        using (var verified = FinitePredicateAcceptance.Open()) { }
        string manifest = GraphStressAcceptance.Manifest, hash = GraphStressAcceptance.ManifestSha;
        BulgarianLanguageSession? session = null;
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => session = BulgarianLanguageSession.Open(manifest));
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            foreach (string id in new[] { "FP01", "FP02", "FP03", "FP09", "FP10", "FP12" })
            {
                JsonElement gold = FinitePredicateAcceptance.Case(id);
                string text = gold.GetProperty("text").GetString()!, expected = gold.GetProperty("expected").GetString()!;
                JsonElement raw = default;
                foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    // No opt-in flag: PredicateLinks is always-populated additive default evidence.
                    using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/analyze-text",
                        new WebInput(text, EvidenceFormat: format), HttpContract.Json);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                    using JsonDocument restored = Restore(encoded);
                    JsonElement root = restored.RootElement;
                    if (format == HttpContract.RawFormat) raw = root.Clone();
                    else Assert.IsTrue(SharedDonorResponseTests.SameJson(raw, root),
                        "Existing raw/shared-donors/shared-analyses routes preserve every old and new field and reference.");
                    Assert.AreEqual(hash, root.GetProperty("package").GetProperty("manifestSha256").GetString()!.ToLowerInvariant());
                    JsonElement data = root.GetProperty("data");
                    CollectionAssert.AreEqual(TextFields, data.EnumerateObject().Select(property => property.Name).ToArray());
                    Assert.AreEqual("local-predicate-links.v1", data.GetProperty("predicateLinks").GetProperty("version").GetString());
                    TextAnalysis actual = data.Deserialize<TextAnalysis>(HttpContract.Json)!;
                    FinitePredicateAcceptance.AssertIndependent(session!, id, text, expected, actual);
                    if (id == "FP10")
                        Assert.IsTrue(actual.PredicateLinks!.Sentences.Any(sentence => !sentence.ConflictScopeComplete &&
                            sentence.UncoveredReasons.Contains("quoted_scope_unsupported")),
                            "Quoted speech retains its explicit unsupported conflict scope without a warning.");
                    Assert.AreEqual(expected == "P" ? 1 : 0, actual.Diagnostics.Count(d => d.Code == FinitePredicateAcceptance.Code));
                    Assert.IsTrue(actual.Diagnostics.Where(d => d.Code == FinitePredicateAcceptance.Code).All(d => d.ProjectedText is null));
                }
            }
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
    }
}
