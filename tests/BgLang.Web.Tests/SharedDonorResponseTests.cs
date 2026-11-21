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
public sealed class SharedDonorResponseTests
{
    [TestMethod]
    public void Reconstruction_preserves_all_fields_authority_flags_readings_and_utf16_offsets()
    {
        const string novel = "свръхсупернепресен", negative = "супернесуперпресен";
        string[] donors = ["пресен", "тесен"];
        WordAnalysis unknown = Word(novel) with
        {
            BaseEvidence = new(novel, novel, "пресен", [new("пресен", "пресен", MorphKind.Stem, 12, 6, "stem-proof")],
                "application", "prefix-rule", .8, ["selected-structural-evidence"], "graph-source"),
            GraphCandidates = [Path(novel, "пресен", null), Path(novel, novel, donors)],
            Candidates = [Candidate(novel, "potential-lemma", ["пресен", "тесен"])],
            KnownBaseCandidates = [Candidate(novel, "пресен", [])],
            BaseTypeCandidates = [Candidate(novel, novel, ["пресен", "тесен"]) with
            { ShorterEndingCandidates = [Candidate(novel, novel, ["пресен", "тесен"]) with { MatchedEndingLength = 1 }] }],
            GraphSearchIncomplete = true,
            GraphCandidatesTruncated = true,
            CandidateSearchLimited = true,
            CandidateEvidenceLimited = true,
            KnownBaseSearchLimited = true,
            BaseTypeSearchLimited = true,
            BaseTypeEvidenceLimited = true
        };
        WordAnalysis ambiguous = Word("Пила", Reading("Пила", "пила", "Съществително:41,Женски"),
            Reading("Пила", "пия", "Глагол:174,Минало"));
        WordAnalysis refused = Word(negative) with { GraphCandidates = [Path(negative, "непресен", ["пресен", "тесен"])] };
        string text = $"📚 {novel}\tПила,\n{negative}";
        TextAnalysis analysis = new(text,
        [new("📚", TokenKind.Symbol, 0, 2, null), Token(text, novel, unknown), Token(text, "Пила", ambiguous), Token(text, negative, refused)]);
        TextMatchResult result = TextMatchProjection.Find(unknown, analysis);
        ApiEnvelope raw = Envelope(result);
        using JsonDocument shared = Project(raw);
        using JsonDocument reconstructed = Reconstruct(shared);

        AssertEquivalent(raw, reconstructed);
        Assert.AreEqual(SharedDonorResponse.Format, shared.RootElement.GetProperty("evidenceFormat").GetString());
        Assert.AreEqual(2, shared.RootElement.GetProperty("donorSets").GetArrayLength(), "One full list and one empty list; no object-identity dedupe.");
        JsonElement data = reconstructed.RootElement.GetProperty("data").GetProperty("textAnalysis");
        JsonElement first = data.GetProperty("tokens")[1];
        Assert.AreEqual(3, first.GetProperty("start").GetInt32(), "Emoji occupies two UTF-16 units.");
        Assert.AreEqual(novel, text.Substring(first.GetProperty("start").GetInt32(), first.GetProperty("length").GetInt32()));
        Assert.IsFalse(first.GetProperty("analysis").GetProperty("isKnown").GetBoolean());
        Assert.AreEqual(0, first.GetProperty("analysis").GetProperty("readings").GetArrayLength());
        Assert.AreEqual(2, data.GetProperty("tokens")[2].GetProperty("analysis").GetProperty("readings").GetArrayLength());
        JsonElement proof = first.GetProperty("analysis").GetProperty("graphCandidates")[1];
        Assert.AreEqual(2, proof.GetProperty("conditionalRootSupportCount").GetInt32());
        CollectionAssert.AreEqual(donors, proof.GetProperty("conditionalSupportingLemmas").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.AreEqual(novel, proof.GetProperty("unmetExactSelfReadingRequirements")[0].GetProperty("inputSurface").GetString());
        Assert.AreEqual("непресен", data.GetProperty("tokens")[3].GetProperty("analysis").GetProperty("graphCandidates")[0].GetProperty("base").GetString());
        Assert.IsFalse(reconstructed.RootElement.GetProperty("coverage").GetProperty("complete").GetBoolean());
    }

    [TestMethod]
    public void Pool_equality_uses_the_whole_ordered_array_and_distinguishes_empty_from_missing()
    {
        WordAnalysis first = Word("нов1") with { GraphCandidates = [Path("нов1", "нов1", ["а", "б"])] };
        WordAnalysis second = Word("нов2") with
        {
            GraphCandidates = [Path("нов2", "нов2", ["а", "б"]) with { ConditionalRootProgramId = "different-program", ConditionalRootBaseFullTag = "different-tag" }, Path("нов2", "нов2", ["б", "а"]),
                Path("нов2", "нов2", ["а", "в"]), Path("нов2", "нов2", ["а"]), Path("нов2", "нов2", ["а", "б", "в"]),
                Path("нов2", "нов2", []), Path("нов2", "точна", null)],
            BaseTypeCandidates = [Candidate("нов2", "нов2", ["а", "б"]) with
            { ShorterEndingCandidates = [Candidate("нов2", "нов2", ["а", "б"])] }]
        };
        ApiEnvelope raw = Envelope(new TextAnalysis("нов1 нов2", [new("нов1", TokenKind.Word, 0, 4, first), new("нов2", TokenKind.Word, 5, 4, second)]));
        using JsonDocument shared = Project(raw);
        JsonElement root = shared.RootElement, tokens = root.GetProperty("data").GetProperty("tokens");
        JsonElement paths = tokens[1].GetProperty("analysis").GetProperty("graphCandidates");
        int sharedIndex = tokens[0].GetProperty("analysis").GetProperty("graphCandidates")[0].GetProperty("conditionalSupportingLemmasRef").GetInt32();

        Assert.AreEqual(6, root.GetProperty("donorSets").GetArrayLength());
        Assert.AreEqual(sharedIndex, paths[0].GetProperty("conditionalSupportingLemmasRef").GetInt32());
        Assert.AreEqual(6, paths.EnumerateArray().Take(6).Select(path => path.GetProperty("conditionalSupportingLemmasRef").GetInt32()).Distinct().Count());
        Assert.AreEqual(0, root.GetProperty("donorSets")[paths[5].GetProperty("conditionalSupportingLemmasRef").GetInt32()].GetArrayLength());
        Assert.IsFalse(paths[6].TryGetProperty("conditionalSupportingLemmasRef", out _));
        Assert.IsFalse(paths[6].TryGetProperty("conditionalSupportingLemmas", out _));
        JsonElement morphology = tokens[1].GetProperty("analysis").GetProperty("baseTypeCandidates")[0];
        Assert.AreEqual(sharedIndex, morphology.GetProperty("supportingLemmasRef").GetInt32());
        Assert.AreEqual(sharedIndex, morphology.GetProperty("shorterEndingCandidates")[0].GetProperty("supportingLemmasRef").GetInt32());
        using JsonDocument reconstructed = Reconstruct(shared);
        AssertEquivalent(raw, reconstructed);
    }

    [TestMethod]
    public void Donor_table_is_response_local_and_raw_v1_remains_unprojected()
    {
        ApiEnvelope first = Envelope(Word("нов1") with { GraphCandidates = [Path("нов1", "нов1", ["а", "б"])] });
        ApiEnvelope second = Envelope(Word("нов2") with { GraphCandidates = [Path("нов2", "нов2", ["в", "г"])] });
        using JsonDocument raw = JsonDocument.Parse(JsonSerializer.Serialize(first, HttpContract.Json));
        Assert.IsFalse(raw.RootElement.TryGetProperty("evidenceFormat", out _));
        Assert.IsFalse(raw.RootElement.TryGetProperty("donorSets", out _));
        Assert.IsTrue(raw.RootElement.GetProperty("data").GetProperty("graphCandidates")[0].TryGetProperty("conditionalSupportingLemmas", out _));
        using JsonDocument one = Project(first), two = Project(second), again = Project(first);
        Assert.AreEqual(1, two.RootElement.GetProperty("donorSets").GetArrayLength());
        CollectionAssert.AreEqual(new[] { "в", "г" }, two.RootElement.GetProperty("donorSets")[0].EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.AreEqual(0, two.RootElement.GetProperty("data").GetProperty("graphCandidates")[0].GetProperty("conditionalSupportingLemmasRef").GetInt32());
        Assert.IsTrue(SameJson(one.RootElement, again.RootElement));
        IReadOnlyList<WordDerivation> derived = [Path("производна", "точна", null),
            Path("производна", "условна", ["а", "б"]), Path("друга", "друга", ["а", "б"])];
        ApiEnvelope list = Envelope(derived);
        using JsonDocument projectedList = Project(list), restoredList = Reconstruct(projectedList);
        Assert.AreEqual(1, projectedList.RootElement.GetProperty("donorSets").GetArrayLength());
        Assert.AreEqual(3, projectedList.RootElement.GetProperty("data").GetArrayLength());
        Assert.IsFalse(projectedList.RootElement.GetProperty("data")[0].TryGetProperty("conditionalSupportingLemmasRef", out _));
        Assert.AreEqual(projectedList.RootElement.GetProperty("data")[1].GetProperty("conditionalSupportingLemmasRef").GetInt32(),
            projectedList.RootElement.GetProperty("data")[2].GetProperty("conditionalSupportingLemmasRef").GetInt32());
        AssertEquivalent(list, restoredList);
        foreach (string operation in new[] { "lookup", "analyze-word", "analyze-text", "generate-forms", "derive-words", "decompose-word", "find-forms-in-text" })
        {
            string? query = operation == "find-forms-in-text" ? "дума" : null;
            ApiOperations.Validate(operation, new("дума", Query: query));
            ApiOperations.Validate(operation, new("дума", Query: query, EvidenceFormat: SharedDonorResponse.Format));
            ApiRequestException error = Assert.Throws<ApiRequestException>(() => ApiOperations.Validate(operation,
                new("дума", Query: query, EvidenceFormat: "unsupported-donor-format")));
            Assert.AreEqual(400, error.Status, operation);
            Assert.AreEqual("invalid-evidence-format", error.Code, operation);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Native23_text_projection_preserves_fifteen_exact_anchors_complete_proofs_and_negative_controls()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete release package is required; no package-free acceptance.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit), "Set the independently selected manifest SHA-256.");
        BulgarianLanguageSession? captured = null;
        int opens = 0;
        var app = WebHostFactory.Create(
            ["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest, "--BgLang:ManifestSha256", hash], () =>
            { opens++; return captured = BulgarianLanguageSession.Open(manifest); });
        try
        {
            await app.StartAsync();
            Assert.IsNotNull(captured);
            BulgarianLanguageSession session = captured!;
            Assert.AreEqual(hash.ToLowerInvariant(), session.Provenance.ManifestSha256.ToLowerInvariant());
            string[] words = ["несуперсвръхпресен", "несуперсвръхпресните", "несвръхсуперпресен", "несвръхсуперпресните",
                "супернесвръхпресен", "супернесвръхпресните", "суперсвръхнепресен", "суперсвръхнепресните",
                "свръхнесуперпресен", "свръхнесуперпресните", "свръхсупернепресен", "свръхсупернепресните",
                "суперсвръхпресен", "суперсвръхпресните", "свръхсуперпресните", "супернесуперпресен", "свръхсупернепресените",
                "жжжжжж", "жжжжж", "жжжж", "пресен", "книга", "работник"];
            string text = string.Join(" ", words) + ".";
            TextAnalysis direct = session.AnalyzeText(text);
            TokenAnalysis[] tokens = direct.Tokens.Where(token => token.Kind == TokenKind.Word).ToArray();
            CollectionAssert.AreEqual(words, tokens.Select(token => token.Text).ToArray());
            Assert.AreEqual(23, tokens.Length);
            for (int index = 0; index < tokens.Length; index++)
            {
                TokenAnalysis token = tokens[index];
                Assert.AreEqual(token.Text, text.Substring(token.Start, token.Length));
                WordAnalysis word = token.Analysis!;
                if (index < 15)
                    Assert.IsTrue(word.GraphCandidates.Any(path => !path.IsConditional && path.Base == "пресен"), token.Text);
                if (index is >= 15 and < 20)
                {
                    Assert.IsFalse(word.GraphCandidates.Any(path => path.Base == "пресен"), token.Text);
                    Assert.AreNotEqual("пресен", word.BaseEvidence?.Base, token.Text);
                }
                if (index < 20) { Assert.IsFalse(word.IsKnown, token.Text); Assert.AreEqual(0, word.Readings.Count, token.Text); }
                else Assert.IsTrue(word.Readings.Count > 0, token.Text);
                foreach (WordDerivation path in word.GraphCandidates.Where(path => path.IsConditional))
                {
                    Assert.IsNotNull(path.ConditionalSupportingLemmas);
                    IReadOnlyList<string> donors = path.ConditionalSupportingLemmas!;
                    Assert.AreEqual(path.ConditionalRootSupportCount, (int?)donors.Count);
                    Assert.AreEqual(donors.Count, donors.Distinct(StringComparer.Ordinal).Count());
                    Assert.IsFalse(donors.Contains(path.Base, StringComparer.Ordinal), "A conditional root cannot support itself.");
                    Assert.IsTrue(donors.Count >= 2);
                    Assert.IsTrue(path.UnmetExactSelfReadingRequirements?.Count > 0, "Conditional evidence is not an exact-self reading.");
                }
            }
            Assert.IsTrue(tokens.Any(token => token.Analysis!.GraphCandidates.Any(path => path.IsConditional)), "The selected runtime must provide a real conditional witness.");
            ApiEnvelope raw = Envelope(direct, session.Provenance);
            using JsonDocument shared = Project(raw);
            using JsonDocument reconstructed = Reconstruct(shared);
            AssertEquivalent(raw, reconstructed);
            JsonElement[] restored = reconstructed.RootElement.GetProperty("data").GetProperty("tokens").EnumerateArray()
                .Where(token => token.GetProperty("kind").GetString() == "Word").ToArray();
            Assert.AreEqual(15, restored.Take(15).Count(token => token.GetProperty("analysis").GetProperty("graphCandidates").EnumerateArray()
                .Any(path => path.GetProperty("base").GetString() == "пресен" && !path.TryGetProperty("isConditional", out _))));

            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            // Keep cold evidence above; compare the paired HTTP requests against a direct warm public result.
            using JsonDocument directWarm = JsonDocument.Parse(JsonSerializer.Serialize(session.AnalyzeText(text), HttpContract.Json));
            using HttpResponseMessage rawHttp = await client.PostAsJsonAsync("/api/v1/analyze-text", new WebInput(text), HttpContract.Json);
            Assert.AreEqual(HttpStatusCode.OK, rawHttp.StatusCode);
            using JsonDocument actualRaw = JsonDocument.Parse(await rawHttp.Content.ReadAsStringAsync());
            Assert.IsFalse(actualRaw.RootElement.TryGetProperty("donorSets", out _));
            Assert.IsFalse(actualRaw.RootElement.TryGetProperty("evidenceFormat", out _));
            Assert.IsTrue(SameJson(directWarm.RootElement, actualRaw.RootElement.GetProperty("data")), "Raw HTTP data must equal the direct public result.");
            using HttpResponseMessage compactHttp = await client.PostAsJsonAsync("/api/v1/analyze-text",
                new WebInput(text, EvidenceFormat: SharedDonorResponse.Format), HttpContract.Json);
            Assert.AreEqual(HttpStatusCode.OK, compactHttp.StatusCode);
            using JsonDocument actualCompact = JsonDocument.Parse(await compactHttp.Content.ReadAsStringAsync());
            Assert.AreEqual(SharedDonorResponse.Format, actualCompact.RootElement.GetProperty("evidenceFormat").GetString());
            Assert.IsTrue(actualCompact.RootElement.GetProperty("donorSets").GetArrayLength() > 0);
            using JsonDocument restoredHttp = Reconstruct(actualCompact);
            Assert.IsTrue(SameJson(actualRaw.RootElement, restoredHttp.RootElement), "Actual raw and reconstructed compact envelopes must preserve data, coverage, versions and package.");
            using HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/v1/analyze-text",
                new WebInput(text, EvidenceFormat: "unsupported-donor-format"), HttpContract.Json);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using JsonDocument error = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
            Assert.AreEqual("invalid-evidence-format", error.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(1, opens, "One host and one Open for direct and actual HTTP controls.");
        }
        finally
        {
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
        Assert.Throws<ObjectDisposedException>(() => _ = captured!.Provenance);
    }

    private static WordAnalysis Word(string word, params WordReading[] readings) => new(word, word, readings, null);
    private static WordReading Reading(string form, string lemma, string tag) => new(form, lemma, tag, 1, null, null, null, "stored-source");
    private static TokenAnalysis Token(string text, string surface, WordAnalysis analysis) => new(surface, TokenKind.Word, text.IndexOf(surface, StringComparison.Ordinal), surface.Length, analysis);
    private static MorphologyCandidate Candidate(string word, string lemma, string[] donors) => new(word, lemma, "base-tag", "form-tag", "morphology-program", 2, donors, "package-source");
    private static WordDerivation Path(string word, string root, string[]? donors) =>
        new(word, root, "state:from", "state:to", [new(word, word, MorphKind.Stem, 0, word.Length, "segment-proof")],
            ["edge"], ["rule"], ["application"], .85, ["replayed-path"], "graph-source")
        {
            DerivedLemma = word, FullTags = ["base-tag", "form-tag"], IsConditional = donors is not null,
            ConditionalRootProgramId = donors is null ? null : "productive-program",
            ConditionalRootBaseFullTag = donors is null ? null : "base-tag",
            ConditionalRootSupportCount = donors?.Length, ConditionalSupportingLemmas = donors,
            UnmetExactSelfReadingRequirements = donors is null ? null : [new(root, "base-tag", "base-edge")]
        };
    private static ApiEnvelope Envelope(object data, PackageProvenance? provenance = null) => new("1", new("host", "api", "contracts", "dotnet"),
        provenance ?? new("package", "schema", new string('a', 64), "lexicon-hash", "lexicon-source", "graph-hash", "graph-source"),
        new(null, 32, 32, false, "per-word-analysis"), new(false, true, false), data);
    private static JsonDocument Project(ApiEnvelope raw)
    {
        var response = new SharedDonorResponse(raw);
        return JsonDocument.Parse(JsonSerializer.Serialize(response.Envelope, response.Json));
    }
    private static void AssertEquivalent(ApiEnvelope raw, JsonDocument reconstructed)
    {
        using JsonDocument expected = JsonDocument.Parse(JsonSerializer.Serialize(raw, HttpContract.Json));
        Assert.IsTrue(SameJson(expected.RootElement, reconstructed.RootElement), "Reconstruction must preserve every public field, including proof authority and limit flags.");
    }
    // Independent consumer reconstruction: do not call the production projection to expand its own output.
    internal static JsonDocument Reconstruct(JsonDocument shared, JsonElement? dataOverride = null)
    {
        JsonElement pool = shared.RootElement.GetProperty("donorSets");
        shared.RootElement.TryGetProperty("wordAnalyses", out JsonElement analyses);
        using var bytes = new MemoryStream();
        using var writer = new Utf8JsonWriter(bytes);
        Write(shared.RootElement, true);
        writer.Flush();
        return JsonDocument.Parse(bytes.ToArray());
        void Write(JsonElement value, bool envelope = false)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    if (envelope && property.Name is "donorSets" or "wordAnalyses" or "evidenceFormat") continue;
                    if (envelope && property.Name == "data" && dataOverride is { } replacement)
                    {
                        writer.WritePropertyName("data"); Write(replacement); continue;
                    }
                    string? field = property.Name switch { "conditionalSupportingLemmasRef" => "conditionalSupportingLemmas", "supportingLemmasRef" => "supportingLemmas", "analysisRef" => "analysis", _ => null };
                    writer.WritePropertyName(field ?? property.Name);
                    if (field is null) Write(property.Value);
                    else if (field == "analysis")
                    {
                        if (property.Value.ValueKind == JsonValueKind.Null) writer.WriteNullValue();
                        else Write(analyses[property.Value.GetInt32()]);
                    }
                    else pool[property.Value.GetInt32()].WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray(); foreach (JsonElement item in value.EnumerateArray()) Write(item); writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
    internal static bool SameJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind) return false;
        if (left.ValueKind == JsonValueKind.Object)
            return left.EnumerateObject().Count() == right.EnumerateObject().Count() && left.EnumerateObject()
                .All(property => right.TryGetProperty(property.Name, out JsonElement peer) && SameJson(property.Value, peer));
        if (left.ValueKind == JsonValueKind.Array)
            return left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => SameJson(pair.First, pair.Second));
        return left.ValueKind == JsonValueKind.String ? left.GetString() == right.GetString() : left.GetRawText() == right.GetRawText();
    }
}
