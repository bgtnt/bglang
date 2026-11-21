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

// Reuses the existing host and transport fixtures; full-package controls have no conditional skips.
public sealed partial class StressHttpTests
{
    private const string StressPrototype = "legacy-base:64", NovelStressLemma = "космоаванпост";

    [TestMethod]
    public void Morphology_stress_requests_require_only_applicable_exact_fields_without_a_result_limit()
    {
        var generate = new WebInput(NovelStressLemma, PrototypeEntryId: " legacy-base:64 ");
        ApiOperations.Validate("generate-stress-forms", generate);
        Assert.AreEqual(" legacy-base:64 ", generate.PrototypeEntryId, "Opaque identity is not trimmed or normalized.");
        foreach (string? missing in new string?[] { null, "", " ", "\t" })
            Assert.AreEqual("prototype-required", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate("generate-stress-forms", generate with { PrototypeEntryId = missing })).Code);
        foreach (string operation in new[] { "lookup", "stress", "derivational-stress", "generate-forms", "decompose-word" })
            Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, generate)).Code, operation);
        foreach (string operation in new[] { "generate-stress-forms", "derivational-stress" })
        {
            WebInput valid = operation == "generate-stress-forms" ? new(NovelStressLemma, PrototypeEntryId: StressPrototype) : new("плетач");
            ApiOperations.Validate(operation, valid);
            Assert.AreEqual("limit-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, valid with { Limit = 1 })).Code);
            Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, valid with { StressSelection = new(0, "owner", "плетач", null) })).Code);
            Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, valid with { IncludeUnknownContextProperNames = false })).Code);
            Assert.AreEqual("query-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, valid with { Query = "друга" })).Code);
            Assert.AreEqual("invalid-evidence-format", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, valid with { EvidenceFormat = "unknown" })).Code);
            foreach (string? text in new string?[] { null, "", " ", "плетач\n", new string('я', 257) })
                Assert.Throws<ApiRequestException>(() => ApiOperations.Validate(operation, valid with { Text = text }));
        }
        ApiOperations.Validate("stress", new("плетач", StressSelection: new(0, "owner", "плетач", null)));
        foreach (string value in new[] { "1", "true", "[]", "{}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(
                "{\"text\":\"нова\",\"prototypeEntryId\":" + value + "}", HttpContract.Json));
    }

    [TestMethod]
    public async Task Morphology_stress_payloads_preserve_canonical_bytes_and_cancel_before_headers_in_every_format()
    {
        GeneratedStressParadigm generated = new(true, StressPrototype, "аванпост", NovelStressLemma,
            "test-rule", "test-accent-class", "test-graph",
            [new(NovelStressLemma, "raw-tag", StressStatus.Inferred, [new(3, 7, StressMarkKind.Unspecified)]),
             new(NovelStressLemma + "ове", "raw-tag,мн.ч.", StressStatus.Unknown, [])]);
        WordDecomposition proof = new("ПЛЕТАЧ", "плетач", "плета",
            [new("ач", "ач", MorphKind.DerivationalSuffix, 4, 2, "m:suffix:ач")], "a:плетач",
            "derivational_suffix.stem_family.ach.v0", .8, ["reviewed-only"], "test-graph")
            { Transparency = "transparent", ProductiveGeneration = false };
        DerivationalStressEvidence derived = new(proof, StressStatus.Inferred,
            new(StressOrigin.DerivationalRulePrediction, StressStatus.Inferred,
                [new(1, 4, StressMarkKind.Unspecified)], "test-accent-rule"));
        foreach (string version in new[] { "1", "2" })
        foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
        foreach (object data in new object[] { generated, derived })
        {
            ApiEnvelope envelope = Envelope(data, version) with { Limits = new(null, null, null, false, "qualified-stress-evidence") };
            byte[] bytes = Encode(envelope, format);
            using JsonDocument encoded = JsonDocument.Parse(bytes); using JsonDocument restored = Restore(encoded);
            Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(data, HttpContract.Json), restored.RootElement.GetProperty("data")));
            DefaultHttpContext exact = new(); using MemoryStream accepted = new(); exact.Response.Body = accepted;
            await new ResponseWriter(bytes.Length).WriteAsync(exact, envelope, format, CancellationToken.None);
            CollectionAssert.AreEqual(bytes, accepted.ToArray()); Assert.AreEqual(bytes.LongLength, exact.Response.ContentLength);
            DefaultHttpContext rejected = new(); using MemoryStream body = new(); rejected.Response.Body = body;
            var error = await Assert.ThrowsAsync<ApiRequestException>(() =>
                new ResponseWriter(bytes.Length - 1).WriteAsync(rejected, envelope, format, CancellationToken.None));
            Assert.AreEqual("response-too-large", error.Code); Assert.AreEqual(413, error.Status);
            Assert.AreEqual(0, body.Length); Assert.IsNull(rejected.Response.ContentLength); Assert.IsNull(rejected.Response.ContentType);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            DefaultHttpContext cancelledContext = new(); using MemoryStream cancelledBody = new(); cancelledContext.Response.Body = cancelledBody;
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new ResponseWriter(bytes.Length).WriteAsync(cancelledContext, envelope, format, cancelled.Token));
            Assert.AreEqual(0, cancelledBody.Length); Assert.IsNull(cancelledContext.Response.ContentLength); Assert.IsNull(cancelledContext.Response.ContentType);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Complete_package_morphology_stress_routes_bind_exact_data_and_roundtrip_all_formats(string version)
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "Strict complete-package binding is mandatory.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit));
        BulgarianLanguageSession? session = null; var clock = new ManualClock();
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => session = BulgarianLanguageSession.Open(manifest), clock);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            Assert.AreEqual(hash.ToLowerInvariant(), host.Provenance.ManifestSha256.ToLowerInvariant());
            Assert.IsEmpty(session!.Lookup(NovelStressLemma)); Assert.IsEmpty(session.GenerateForms(NovelStressLemma));
            GeneratedStressParadigm generated = session.GenerateStressForms(StressPrototype, NovelStressLemma);
            Assert.IsTrue(generated.Available, "The candidate package must carry the explicit bound prototype model; no conditional skip.");
            Assert.AreEqual(StressPrototype, generated.PrototypeEntryId); Assert.AreEqual("аванпост", generated.PrototypeLemma);
            Assert.AreEqual(NovelStressLemma, generated.Lemma); Assert.IsFalse(string.IsNullOrWhiteSpace(generated.RuleId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(generated.EvidenceSourceId)); Assert.IsFalse(string.IsNullOrWhiteSpace(generated.GraphSourceId));
            var baseForm = generated.Forms.Single(row => row.Form == NovelStressLemma);
            Assert.AreEqual(StressStatus.Inferred, baseForm.Status); Assert.HasCount(1, baseForm.Marks);
            Assert.AreEqual(new StressMark(3, 7, StressMarkKind.Unspecified), baseForm.Marks.Single());
            var padded = session.GenerateStressForms(" " + StressPrototype + " ", NovelStressLemma);
            Assert.IsFalse(padded.Available); Assert.IsEmpty(padded.Forms);
            DerivationalStressEvidence player = session.GetDerivationalStressEvidence("играч"), knitter = session.GetDerivationalStressEvidence("плетач");
            AssertDerivationalLiteral(player, "играч", "a:играч", 3);
            AssertDerivationalLiteral(knitter, "плетач", "a:плетач", 4);
            (string Operation, WebInput Input, object Expected)[] cases =
            [
                ("generate-stress-forms", new(NovelStressLemma, PrototypeEntryId: StressPrototype), generated),
                ("generate-stress-forms", new(NovelStressLemma, PrototypeEntryId: " " + StressPrototype + " "), padded),
                ("derivational-stress", new("играч"), player),
                ("derivational-stress", new("плетач"), knitter)
            ];
            foreach (var test in cases)
            {
                ApiOperations.Validate(test.Operation, test.Input);
                var direct = ApiOperations.Invoke(test.Operation, test.Input, session, host.Provenance, host.Versions);
                Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(test.Expected, HttpContract.Json),
                    JsonSerializer.SerializeToElement(direct.Data, HttpContract.Json)));
                Assert.IsNull(direct.Limits.Requested); Assert.IsNull(direct.Limits.Effective); Assert.IsNull(direct.Limits.HostMaximum);
                Assert.IsFalse(direct.Coverage.HostTruncated); Assert.IsFalse(direct.Coverage.PossibleTruncation);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                var cancellation = Assert.Throws<OperationCanceledException>(() =>
                    ApiOperations.Invoke(test.Operation, test.Input, session, host.Provenance, host.Versions, cancelled.Token));
                Assert.AreEqual(cancelled.Token, cancellation.CancellationToken);
                await Assert.ThrowsAsync<OperationCanceledException>(() => host.ExecuteResponseAsync(test.Operation, test.Input,
                    (_, _) => throw new AssertFailedException("Cancelled work must not start a success response."), cancelled.Token));
                foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/" + test.Operation,
                        test.Input with { EvidenceFormat = format }, HttpContract.Json);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                    Assert.AreEqual(bytes.LongLength, response.Content.Headers.ContentLength);
                    using JsonDocument encoded = JsonDocument.Parse(bytes); using JsonDocument restored = Restore(encoded);
                    Assert.AreEqual(version, encoded.RootElement.GetProperty("apiVersion").GetString());
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(test.Expected, HttpContract.Json), restored.RootElement.GetProperty("data")));
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(host.Provenance, HttpContract.Json), restored.RootElement.GetProperty("package")));
                    Assert.IsFalse(restored.RootElement.GetProperty("coverage").GetProperty("hostTruncated").GetBoolean());
                    Assert.IsFalse(restored.RootElement.GetProperty("coverage").GetProperty("possibleTruncation").GetBoolean());
                    foreach (string field in new[] { "requested", "effective", "hostMaximum" })
                        Assert.AreEqual(JsonValueKind.Null, restored.RootElement.GetProperty("limits").GetProperty(field).ValueKind);
                }
            }
            // Invalid requests must be rejected before admission while another response owns the lane.
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task active = host.ExecuteResponseAsync("lookup", new("книга"), async (_, _) =>
                { entered.TrySetResult(); await release.Task; }, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                foreach ((string operation, string body, string code) in new[]
                {
                    ("generate-stress-forms", "{\"text\":\"нова\"}", "prototype-required"),
                    ("generate-stress-forms", "{\"text\":\"нова\",\"prototypeEntryId\":\"legacy-base:64\",\"limit\":1}", "limit-not-applicable"),
                    ("derivational-stress", "{\"text\":\"плетач\",\"limit\":1}", "limit-not-applicable"),
                    ("derivational-stress", "{\"text\":\"плетач\",\"prototypeEntryId\":\"legacy-base:64\"}", "option-not-applicable"),
                    ("derivational-stress", "{\"text\":\"плетач\",\"stressSelection\":{\"tokenIndex\":0,\"entryId\":\"id\",\"form\":\"плетач\",\"fullTag\":null}}", "option-not-applicable"),
                    ("generate-stress-forms", "{\"text\":\"нова\",\"prototypeEntryId\":7}", "invalid-json")
                })
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using var response = await client.PostAsync("/api/v" + version + "/" + operation, new StringContent(body, Encoding.UTF8, "application/json"));
                    Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                    using var error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                    Assert.AreEqual(code, error.RootElement.GetProperty("code").GetString()); Assert.IsFalse(active.IsCompleted);
                }
            }
            finally { release.TrySetResult(); await active; }
            Assert.IsEmpty(session.Lookup(NovelStressLemma));
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
    }

    private static void AssertDerivationalLiteral(DerivationalStressEvidence actual, string word, string application, int offset)
    {
        Assert.AreEqual(StressStatus.Inferred, actual.Status); Assert.IsNotNull(actual.Realization);
        Assert.AreEqual(word, actual.Decomposition.Word); Assert.AreEqual(word, actual.Decomposition.NormalizedWord);
        Assert.AreEqual(application, actual.Decomposition.ApplicationId);
        Assert.AreEqual("derivational_suffix.stem_family.ach.v0", actual.Decomposition.RuleId);
        Assert.IsNotNull(actual.Decomposition.Base); Assert.AreEqual("transparent", actual.Decomposition.Transparency);
        Assert.AreEqual(false, actual.Decomposition.ProductiveGeneration);
        Assert.AreEqual(StressOrigin.DerivationalRulePrediction, actual.Realization.Origin);
        Assert.AreEqual(StressStatus.Inferred, actual.Realization.Status); Assert.HasCount(1, actual.Realization.Marks);
        Assert.AreEqual(new StressMark(1, offset, StressMarkKind.Unspecified), actual.Realization.Marks.Single());
        Assert.IsNull(actual.Realization.LegacyUtf16Positions); Assert.IsNull(actual.Realization.LegacyDisplayUtf16Positions);
    }
}
