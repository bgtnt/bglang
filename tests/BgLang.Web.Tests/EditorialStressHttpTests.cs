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
public sealed class EditorialStressHttpTests
{
    [TestMethod, DataRow("1"), DataRow("2")]
    public async Task Explicit_editorial_target_binds_independent_selected_generation_and_control_evidence_in_all_formats(string version)
    {
        string manifest = EditorialStressAcceptance.Manifest;
        EditorialStressAcceptance.AssertBinding(manifest, EditorialStressAcceptance.Mode);
        string hash = EditorialStressAcceptance.Root.GetProperty("packages").GetProperty(EditorialStressAcceptance.Mode)
            .GetProperty("files").EnumerateArray().Single(file => file.GetProperty("name").GetString() == "manifest.json").GetProperty("sha256").GetString()!;
        BulgarianLanguageSession? session = null; var clock = new ManualClock();
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => session = BulgarianLanguageSession.Open(manifest), clock);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
            {
                foreach (JsonElement row in EditorialStressAcceptance.Rows)
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/stress",
                        new WebInput(row.GetProperty("form").GetString()!, StressSelection: EditorialStressAcceptance.Selection(row), EvidenceFormat: format), HttpContract.Json);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    Assert.AreEqual(bytes.LongLength, response.Content.Headers.ContentLength);
                    using JsonDocument encoded = JsonDocument.Parse(bytes); using JsonDocument restored = Restore(encoded);
                    AssertEnvelope(restored.RootElement, version, hash);
                    StressEvidence actual = restored.RootElement.GetProperty("data").Deserialize<StressEvidence>(HttpContract.Json)!;
                    Assert.IsNotNull(actual); EditorialStressAcceptance.AssertSelected(actual, row);
                }
                clock.Now = clock.Now.AddMinutes(1);
                using HttpResponseMessage generatedResponse = await client.PostAsJsonAsync("/api/v" + version + "/generate-stress-forms",
                    new WebInput("чета", PrototypeEntryId: EditorialStressAcceptance.Owner, EvidenceFormat: format), HttpContract.Json);
                Assert.AreEqual(HttpStatusCode.OK, generatedResponse.StatusCode, await generatedResponse.Content.ReadAsStringAsync());
                using JsonDocument generatedEncoded = JsonDocument.Parse(await generatedResponse.Content.ReadAsByteArrayAsync());
                using JsonDocument generatedRestored = Restore(generatedEncoded);
                AssertEnvelope(generatedRestored.RootElement, version, hash);
                GeneratedStressParadigm generated = generatedRestored.RootElement.GetProperty("data").Deserialize<GeneratedStressParadigm>(HttpContract.Json)!;
                Assert.IsNotNull(generated); EditorialStressAcceptance.AssertGenerated(generated);
                foreach ((string text, StressReadingSelection? selection, string rendered) in new (string, StressReadingSelection?, string)[]
                {
                    ("чета", (StressReadingSelection?)new(0, "legacy-base:1465984", "чета", "Съществително:41,Женски"), "че\u0300та"),
                    ("чета", null, EditorialStressAcceptance.IsCurrent ? "чета" : "че\u0300та"),
                    ("чете", null, EditorialStressAcceptance.IsCurrent ? "чете" : "че\u0300те"),
                    ("😀 чете!", new(1, EditorialStressAcceptance.Owner, "чете", "Глагол:145ti,3л.,Сегашно"), EditorialStressAcceptance.IsCurrent ? "😀 чете\u0300!" : "😀 че\u0300те!"),
                    ("чета", new(0, " " + EditorialStressAcceptance.Owner, "чета", "Глагол:145ti,1л.,Сегашно"), "чета")
                })
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/stress", new WebInput(text, StressSelection: selection, EvidenceFormat: format), HttpContract.Json);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync()); using JsonDocument restored = Restore(encoded);
                    AssertEnvelope(restored.RootElement, version, hash);
                    StressEvidence actual = restored.RootElement.GetProperty("data").Deserialize<StressEvidence>(HttpContract.Json)!;
                    Assert.AreEqual(text, actual.Text); Assert.AreEqual(rendered, actual.RenderedText);
                    Assert.AreEqual(selection is null ? StressSelectionStatus.NotRequested : selection.EntryId.StartsWith(' ') ? StressSelectionStatus.NotConnected : StressSelectionStatus.Matched, actual.SelectionStatus);
                    if (selection is null && EditorialStressAcceptance.IsCurrent) Assert.AreEqual(StressStatus.Ambiguous, actual.Tokens.Single().Status);
                    if (text.StartsWith("😀", StringComparison.Ordinal)) { Assert.AreEqual(3, actual.Tokens.Single().Start); Assert.AreEqual(4, actual.Tokens.Single().Length); }
                }
            }
            Assert.IsNotNull(session);
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => session!.GetStressEvidence("чета", cancellationToken: cancellation.Token));
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
    }
    private static void AssertEnvelope(JsonElement root, string version, string hash)
    {
        Assert.AreEqual(version, root.GetProperty("apiVersion").GetString());
        JsonElement expected = EditorialStressAcceptance.Root.GetProperty("packages").GetProperty(EditorialStressAcceptance.Mode);
        Assert.AreEqual(expected.GetProperty("packageId").GetString(), root.GetProperty("package").GetProperty("packageId").GetString());
        Assert.AreEqual(hash, root.GetProperty("package").GetProperty("manifestSha256").GetString()!.ToLowerInvariant());
        Assert.IsFalse(root.GetProperty("coverage").GetProperty("hostTruncated").GetBoolean());
        Assert.IsFalse(root.GetProperty("coverage").GetProperty("possibleTruncation").GetBoolean());
        foreach (string field in new[] { "requested", "effective", "hostMaximum" }) Assert.AreEqual(JsonValueKind.Null, root.GetProperty("limits").GetProperty(field).ValueKind);
    }
}
