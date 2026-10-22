using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed partial class StressHttpTests
{
    [TestMethod]
    public void Stress_request_preserves_exact_selection_and_rejects_missing_or_inapplicable_fields()
    {
        var input = new WebInput("😀 полети!", StressSelection: new(1, " owner:id ", "полети", null));
        ApiOperations.Validate("stress", input); Assert.AreEqual(" owner:id ", input.StressSelection!.EntryId);
        Assert.AreEqual("limit-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("stress", input with { Limit = 1 })).Code);
        Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("lookup", input)).Code);
        Assert.AreEqual("invalid-stress-selection", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("stress", input with { StressSelection = new(-1, "id", "полети", null) })).Code);
        foreach (string body in new[] {
            "{\"text\":\"полети\",\"stressSelection\":{}}",
            "{\"text\":\"полети\",\"stressSelection\":{\"tokenIndex\":0,\"entryId\":\"id\",\"form\":\"полети\"}}",
            "{\"text\":\"полети\",\"stressSelection\":{\"tokenIndex\":0,\"entryId\":\"id\",\"form\":\"полети\",\"fullTag\":null,\"reviewed\":true}}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(body, HttpContract.Json));
    }

    [TestMethod]
    public async Task All_formats_preserve_complete_readings_raw_spans_and_unclassified_marks_before_byte_admission()
    {
        WordReading raw = new("полети", "полет", "Съществително:7,Мъжки,мн.ч.", .75, null, null, null, "wire-only");
        var multi = new StressRealization(StressOrigin.LegacyStored, StressStatus.Unclassified,
            [new(0, 4, StressMarkKind.Unspecified), new(2, 8, StressMarkKind.Unspecified)], "legacy-wire-only")
        { LegacyUtf16Positions = [1, 5] };
        StressReadingEvidence[] rows = Enumerable.Range(0, 36).Select(i => new StressReadingEvidence(i, raw,
            [new("owner-" + i, "key-" + i, i == 35 ? [] : [multi])], [])).ToArray();
        StressEvidence evidence = new("😀 полети.", "😀 полети.", true, StressSelectionStatus.NotRequested,
            [new(1, "полети", 3, 6, StressStatus.Unclassified, [], [], rows, "полети")]);
        foreach (string version in new[] { "1", "2" })
        foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
        {
            ApiEnvelope envelope = Envelope(evidence, version);
            byte[] bytes = Encode(envelope, format);
            using JsonDocument encoded = JsonDocument.Parse(bytes);
            using JsonDocument restored = Restore(encoded);
            JsonElement data = restored.RootElement.GetProperty("data");
            Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(evidence, HttpContract.Json), data));
            JsonElement token = data.GetProperty("tokens")[0]; Assert.AreEqual(3, token.GetProperty("start").GetInt32());
            Assert.AreEqual(36, token.GetProperty("readings").GetArrayLength());
            Assert.AreEqual(35, token.GetProperty("readings")[35].GetProperty("completeReadingIndex").GetInt32());
            Assert.AreEqual(0, token.GetProperty("readings")[35].GetProperty("owners")[0].GetProperty("realizations").GetArrayLength());
            JsonElement realization = token.GetProperty("readings")[0].GetProperty("owners")[0].GetProperty("realizations")[0];
            Assert.AreEqual("Unclassified", realization.GetProperty("status").GetString());
            Assert.AreEqual(2, realization.GetProperty("marks").GetArrayLength());
            DefaultHttpContext rejected = new(); using MemoryStream body = new(); rejected.Response.Body = body;
            DefaultHttpContext exact = new(); using MemoryStream accepted = new(); exact.Response.Body = accepted;
            await new ResponseWriter(bytes.Length).WriteAsync(exact, envelope, format, CancellationToken.None);
            Assert.AreEqual(bytes.LongLength, accepted.Length); Assert.AreEqual(bytes.LongLength, exact.Response.ContentLength);
            ApiRequestException error = await Assert.ThrowsAsync<ApiRequestException>(() =>
                new ResponseWriter(bytes.Length - 1).WriteAsync(rejected, envelope, format, CancellationToken.None));
            Assert.AreEqual(413, error.Status); Assert.AreEqual("response-too-large", error.Code);
            Assert.AreEqual(0, body.Length); Assert.IsNull(rejected.Response.ContentType); Assert.IsNull(rejected.Response.ContentLength);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Complete_package_stress_route_roundtrips_all_formats_and_rejects_before_the_busy_lane(string version)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "Strict complete-package binding is mandatory."); Assert.HasCount(64, hash);
        BulgarianLanguageSession? session = null; var clock = new ManualClock();
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => session = BulgarianLanguageSession.Open(manifest), clock);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (_, _) =>
                { entered.TrySetResult(); await release.Task; }, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                foreach ((string body, string code) in new[] { ("{\"text\":\"полети\",\"limit\":1}", "limit-not-applicable"),
                    ("{\"text\":\"полети\",\"stressSelection\":{}}", "invalid-json") })
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v" + version + "/stress")
                        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                    using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                    Assert.AreEqual(code, error.RootElement.GetProperty("code").GetString()); Assert.IsFalse(active.IsCompleted);
                }
            }
            finally { release.TrySetResult(); await active; }
            const string text = "😀 кни\u0300ги дом жжжабракадабрю.";
            StressEvidence expected = session!.GetStressEvidence(text);
            Assert.AreEqual(5, expected.Tokens[0].Observations.Single().Marks.Single().Utf16Position);
            Assert.AreEqual(StressOrigin.InputObservation, expected.Tokens[0].Observations[0].Origin);
            Assert.AreEqual(StressStatus.Unknown, expected.Tokens[^1].Status);
            foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
            {
                clock.Now = clock.Now.AddMinutes(1);
                using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/stress", new WebInput(text, EvidenceFormat: format), HttpContract.Json);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                using JsonDocument restored = Restore(encoded);
                Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(expected, HttpContract.Json), restored.RootElement.GetProperty("data")));
                Assert.AreEqual(version, encoded.RootElement.GetProperty("apiVersion").GetString());
                Assert.IsFalse(encoded.RootElement.GetProperty("coverage").GetProperty("hostTruncated").GetBoolean());
                Assert.AreEqual(JsonValueKind.Null, encoded.RootElement.GetProperty("limits").GetProperty("effective").ValueKind);
            }
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
    }
}
