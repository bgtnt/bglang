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
            var cases = new[] { "FP01", "FP02", "FP03", "FP09", "FP10", "FP12" }.Select(id =>
            {
                JsonElement gold = FinitePredicateAcceptance.Case(id);
                return (Id: (string?)id, Text: gold.GetProperty("text").GetString()!, Expected: gold.GetProperty("expected").GetString()!);
            }).Concat([(null, "Вратата скърца, защото аз работя.", "N"),
                       (null, "Вратата скърца, защото аз работим.", "P")]);
            foreach (var (id, text, expected) in cases)
            {
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
                    if (id is not null)
                    {
                        FinitePredicateAcceptance.AssertIndependent(session!, id, text, expected, actual);
                        if (id == "FP10")
                            Assert.IsTrue(actual.PredicateLinks!.Sentences.Any(sentence => !sentence.ConflictScopeComplete &&
                                sentence.UncoveredReasons.Contains("quoted_scope_unsupported")),
                                "Quoted speech retains its explicit unsupported conflict scope without a warning.");
                        Assert.AreEqual(expected == "P" ? 1 : 0, actual.Diagnostics.Count(d => d.Code == FinitePredicateAcceptance.Code));
                        Assert.IsTrue(actual.Diagnostics.Where(d => d.Code == FinitePredicateAcceptance.Code).All(d => d.ProjectedText is null));
                    }
                    else
                    {
                        const string rule = "licensed_subordinate_pronoun_prefix_agreement.v1";
                        var warnings = actual.Diagnostics.Where(d => d.Code is "productive_morphology_agreement_mismatch"
                            or "pronoun_verb_agreement_mismatch" or "productive_attribute_agreement_mismatch"
                            or "unseparated_finite_predications").ToArray();
                        var marker = actual.ReadingInventory!.Occurrences.Single(o => o.TokenIndex == 3);
                        Assert.AreEqual(MorphologicalPartOfSpeech.Adverb,
                            actual.ReadingInventory.Sets[marker.ReadingSetIndex].ResolvedReadings.Single().Features.PartOfSpeech);
                        Assert.HasCount(expected == "P" ? 1 : 0, warnings);
                        if (expected == "P")
                        {
                            var warning = warnings.Single();
                            Assert.AreEqual(("productive_morphology_agreement_mismatch", rule, 26, 7),
                                (warning.Code, warning.RuleId, warning.Start, warning.Length));
                            Assert.IsNull(warning.ProjectedText);
                            Assert.IsNull(warning.GraphReadings);
                            CollectionAssert.AreEqual(new[] { new TextReadingReference(4, 0), new TextReadingReference(5, 0) },
                                warning.Readings!.ToArray());
                            var proof = actual.Context!.Sentences.SelectMany(s => s.Candidates).Single(c => c.AuthorityLane == rule);
                            Assert.IsTrue(proof.EvidenceCodes.Contains("connector_readings=0"));
                            Assert.IsFalse(proof.Selectable);
                        }
                    }
                }
            }
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
    }
}
