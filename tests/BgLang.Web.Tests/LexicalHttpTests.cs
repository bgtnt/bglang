using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class LexicalHttpTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Shared_formats_reconstruct_literal_lexical_fields_and_utf16_spans(bool shareAnalyses)
    {
        LexicalWordReference word = new("bridge-a", "мост", "Съществително:7,Мъжки");
        string[] meanings = ["Съоръжение.\rред", "", "Съоръжение.\rред", "ред\nред", " \t "];
        LexicalEntry owner = new("bridge-a", "exact-key", meanings,
            [new("синоним", [new([word, word], []), new([], [129, 64, 129]), new([word], [])]), new("синоним_Съществително", [new([word], [])])]);
        LexicalEntryLookup lookup = new("Мост", "мост", true, [owner], true) { PackageHasUnresolvedRelations = true };
        TextAnalysis text = new("🙂 Мост  мост", [])
        {
            Lexical = new(true, [new("Мост", 3, 4, lookup)],
                [new("Мост  мост", 3, 10, "house", 1, 2, "синоним_Съществително", [word, word])]) { PackageHasUnresolvedRelations = true }
        };
        ApiEnvelope envelope = new("1", new("host", "api", "contracts", "dotnet"),
            new("package", "schema", new string('a', 64), "lexicon", "source", "graph", "graph-source"),
            new(null, 32, 32, false, "per-word-analysis"), new(false, true, false), text);
        var shared = new SharedDonorResponse(envelope, shareAnalyses);
        using JsonDocument encoded = JsonDocument.Parse(JsonSerializer.Serialize(shared.Envelope, shared.Json));
        using JsonDocument reconstructed = SharedDonorResponseTests.Reconstruct(encoded);
        using JsonDocument raw = JsonDocument.Parse(JsonSerializer.Serialize(envelope, HttpContract.Json));
        Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, reconstructed.RootElement));
        JsonElement lexical = reconstructed.RootElement.GetProperty("data").GetProperty("lexical");
        JsonElement phrase = lexical.GetProperty("phrases")[0];
        Assert.AreEqual(text.Text.Substring(phrase.GetProperty("start").GetInt32(), phrase.GetProperty("length").GetInt32()),
            phrase.GetProperty("surface").GetString());
        CollectionAssert.AreEqual(meanings,
            lexical.GetProperty("tokens")[0].GetProperty("owners").GetProperty("entries")[0].GetProperty("meaning")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.IsTrue(lexical.GetProperty("tokens")[0].GetProperty("owners").GetProperty("truncated").GetBoolean());
        Assert.AreEqual(2, phrase.GetProperty("phraseIndex").GetInt32());
        JsonElement entry = lexical.GetProperty("tokens")[0].GetProperty("owners").GetProperty("entries")[0];
        Assert.IsFalse(entry.GetProperty("relationsComplete").GetBoolean());
        JsonElement unavailable = entry.GetProperty("related")[0].GetProperty("phrases")[1];
        Assert.IsFalse(unavailable.GetProperty("isResolved").GetBoolean());
        Assert.AreEqual(0, unavailable.GetProperty("words").GetArrayLength());
        CollectionAssert.AreEqual(new[] { 129u, 64u, 129u }, unavailable.GetProperty("unresolvedSourcePhrase").EnumerateArray().Select(id => id.GetUInt32()).ToArray());
        Assert.IsTrue(lexical.GetProperty("packageHasUnresolvedRelations").GetBoolean());
    }

    [TestMethod]
    public void Lexical_request_validation_preserves_opaque_identity_and_existing_bounds()
    {
        ApiOperations.Validate("lexical-entry", new(" owner:exact "));
        ApiOperations.Validate("lookup-lexical-entries", new("мост", 256));
        foreach (string operation in new[] { "lookup-lexical-entries", "lexical-entry" })
        {
            foreach (string? text in new string?[] { null, "", " ", "\0", "мост\n", new string('я', 257) })
                Assert.Throws<ApiRequestException>(() => ApiOperations.Validate(operation, new(text)));
            Assert.AreEqual("query-not-applicable", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, new("мост", Query: "друга"))).Code);
            Assert.AreEqual("invalid-evidence-format", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate(operation, new("мост", EvidenceFormat: "unknown"))).Code);
        }
        Assert.AreEqual("limit-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("lexical-entry", new("bridge-a", 1))).Code);
        foreach (int limit in new[] { 0, -1, 257 })
            Assert.AreEqual("invalid-limit", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate("lookup-lexical-entries", new("мост", limit))).Code);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>("{\"text\":\"мост\",\"owner\":1}", HttpContract.Json));
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Complete_annotated_package_exposes_exact_owners_through_both_http_versions()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete annotated package is required.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit), "An independently selected manifest SHA is required.");
        using var direct = BulgarianLanguageSession.Open(manifest);
        Assert.IsNotNull(direct.Provenance.AnnotationsSource, "An unannotated package cannot accept full lexical integration.");
        LexicalEntryLookup owners = direct.LookupLexicalEntries("книга", 256);
        Assert.IsTrue(owners.MetadataAvailable); Assert.IsNotEmpty(owners.Entries);
        var clock = new ManualClock();
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash], () => BulgarianLanguageSession.Open(manifest), clock);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            foreach (string version in new[] { "1", "2" })
            {
                foreach (string format in new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    await Compare("lookup-lexical-entries", new("книга", 256, EvidenceFormat: format), owners);
                    await Compare("lexical-entry", new(owners.Entries[0].EntryId, EvidenceFormat: format), direct.FindLexicalEntry(owners.Entries[0].EntryId));
                    await Compare("analyze-text", new("🙂 Книга\u0300 книга.\r\nкнига", EvidenceFormat: format),
                        direct.AnalyzeText("🙂 Книга\u0300 книга.\r\nкнига"));
                }
                async Task Compare(string operation, WebInput input, object expected)
                {
                    clock.Now = clock.Now.AddMinutes(1);
                    using HttpResponseMessage response = await client.PostAsJsonAsync("/api/v" + version + "/" + operation, input, HttpContract.Json);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    using JsonDocument encoded = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    using JsonDocument? expanded = input.EvidenceFormat == HttpContract.RawFormat ? null : SharedDonorResponseTests.Reconstruct(encoded);
                    JsonElement data = (expanded ?? encoded).RootElement.GetProperty("data");
                    using JsonDocument literal = JsonDocument.Parse(JsonSerializer.Serialize(expected, HttpContract.Json));
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(literal.RootElement, data), operation);
                    Assert.IsFalse(encoded.RootElement.GetProperty("coverage").GetProperty("hostTruncated").GetBoolean());
                    Assert.AreNotEqual(JsonValueKind.True, encoded.RootElement.GetProperty("coverage").GetProperty("complete").ValueKind);
                    if (expected is LexicalEntryLookup { PackageHasUnresolvedRelations: true } or
                        LexicalEntryResult { PackageHasUnresolvedRelations: true } or
                        TextAnalysis { Lexical.PackageHasUnresolvedRelations: true })
                        Assert.AreEqual(JsonValueKind.False, encoded.RootElement.GetProperty("coverage").GetProperty("complete").ValueKind);
                    if (expected is LexicalEntryLookup lookup)
                        Assert.AreEqual(lookup.Truncated, encoded.RootElement.GetProperty("coverage").GetProperty("possibleTruncation").GetBoolean());
                    if (operation == "lexical-entry")
                        Assert.AreEqual(JsonValueKind.Null, encoded.RootElement.GetProperty("limits").GetProperty("effective").ValueKind);
                }
            }
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
