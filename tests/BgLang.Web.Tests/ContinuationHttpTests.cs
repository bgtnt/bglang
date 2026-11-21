using System.Net;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class ContinuationHttpTests
{
    private const string Source = "neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7";
    private static readonly string?[] Formats = [HttpContract.RawFormat, null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat];
    // Literal existing public-package oracle, confirmed against the premeasurement raw pool.
    // No expected morphology or source ordinal is generated from a continuation result.
    private static readonly WordReading[] WorkedRows =
    [
        new("работи", "работа", "Съществително:41,Женски,мн.ч.", 1, null, null, null, Source),
        new("работи", "работя", "Глагол:173ti,1л.,Сегашно,Повелително", 1, null, null, null, Source),
        new("работи", "работя", "Глагол:173ti,2л.,Минало_свършено", 1, null, null, null, Source),
        new("работи", "работя", "Глагол:173ti,3л.,Минало_свършено", 1, null, null, null, Source),
        new("работи", "работя", "Глагол:173ti,3л.,Сегашно", 1, null, null, null, Source)
    ];

    private sealed record Example(string Text, int Caret, int Start, int Length, int Limit,
        string Person, int SubjectToken, bool Pronoun = false, bool Empty = false, bool Aliases = false);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Finite_continuation_formats_preserve_graph_or_pronoun_supports_full_homographs_and_original_edit_spans(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (app, session, client) =>
        {
            Example[] examples =
            [
                new("Супертрактор раб", 16, 13, 3, 256, "Third", 0),
                new("Ти раб", 6, 3, 3, 256, "Second", 0, Pronoun: true),
                new("Супертрактор ", 13, 13, 0, 12, "Third", 0, Empty: true),
                new("😀. Супертрактор рабXYZ.\r\nСупертрактор раб ѝ и\u0300", 20, 17, 6, 256, "Third", 2, Aliases: true)
            ];
            foreach (Example example in examples)
            {
                session.AnalyzeText(example.Text, false,
                    new TextContextOptions { Continuation = new(example.Caret, example.Limit) }, CancellationToken.None);
                using JsonDocument raw = await Analyze(client, version, example.Text,
                    new(example.Caret, example.Limit), HttpContract.RawFormat);
                AssertRepresentation(raw.RootElement, version, HttpContract.RawFormat, example.Aliases);
                AssertContinuation(raw.RootElement.GetProperty("data"), example);
                foreach (string? requested in Formats.Skip(1))
                {
                    using JsonDocument encoded = await Analyze(client, version, example.Text,
                        new(example.Caret, example.Limit), requested);
                    AssertRepresentation(encoded.RootElement, version, Effective(version, requested), example.Aliases);
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                        "Restore must retain the entire envelope, complete raw rows, original supports, coverage and edit spans.");
                    AssertContinuation(restored.RootElement.GetProperty("data"), example);
                }
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Default_and_null_or_empty_context_omit_continuation_in_every_http_representation(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (app, session, client) =>
        {
            const string text = "Супертрактор раб";
            session.AnalyzeText(text);
            JsonElement? baseline = null;
            // Each context mode is sent in every format: absent, explicit null and empty object.
            foreach (int contextMode in new[] { 0, 1, 2 })
            foreach (string? requested in Formats)
            {
                using JsonDocument encoded = await Analyze(client, version, text, null, requested, contextMode);
                AssertRepresentation(encoded.RootElement, version, Effective(version, requested), false);
                using JsonDocument restored = Restore(encoded);
                Assert.IsFalse(restored.RootElement.GetProperty("data").TryGetProperty("continuation", out _),
                    "The new evidence is omitted unless explicitly requested.");
                if (baseline is null) baseline = restored.RootElement.Clone();
                else Assert.IsTrue(SharedDonorResponseTests.SameJson(baseline.Value, restored.RootElement),
                    "Default context spellings must retain every old envelope field and original evidence.");
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Continuation_http_rejects_invalid_utf16_grapheme_limits_and_foreign_routes_but_accepts_valid_edges(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (_, _, client) =>
        {
            var invalid = new[]
            {
                (Text: "Супертрактор раб", Caret: -1, Limit: 12, Code: "invalid-continuation-caret"),
                (Text: "Супертрактор раб", Caret: 17, Limit: 12, Code: "invalid-continuation-caret"),
                (Text: "😀. Супертрактор раб", Caret: 1, Limit: 12, Code: "invalid-continuation-caret"),
                (Text: "и\u0300. Супертрактор раб", Caret: 1, Limit: 12, Code: "invalid-continuation-caret"),
                (Text: "👩‍💻. Супертрактор раб", Caret: 2, Limit: 12, Code: "invalid-continuation-caret"),
                (Text: "Супертрактор раб", Caret: 16, Limit: 0, Code: "invalid-continuation-limit"),
                (Text: "Супертрактор раб", Caret: 16, Limit: 257, Code: "invalid-continuation-limit")
            };
            foreach (var item in invalid)
                await AssertError(client, "/api/v" + version + "/analyze-text",
                    Body(item.Text, new(item.Caret, item.Limit), null, 0), item.Code);
            foreach (string operation in new[] { "lookup", "stress" })
                await AssertError(client, "/api/v" + version + "/" + operation,
                    Body("книга", new(0, 12), null, 0), "option-not-applicable");
            Dictionary<string, object?> topLevelLimit = Body("Супертрактор раб", new(16, 12), null, 0);
            topLevelLimit["limit"] = 1;
            await AssertError(client, "/api/v" + version + "/analyze-text", topLevelLimit, "limit-not-applicable");

            using JsonDocument beginning = await Analyze(client, version, "Супертрактор раб", new(0, 1), HttpContract.RawFormat);
            Assert.AreEqual(0, beginning.RootElement.GetProperty("data").GetProperty("continuation").GetProperty("caret").GetInt32());
            using JsonDocument minimum = await Analyze(client, version, "Супертрактор ", new(13, 1), HttpContract.RawFormat);
            AssertContinuation(minimum.RootElement.GetProperty("data"), new("Супертрактор ", 13, 13, 0, 1, "Third", 0, Empty: true));
        });
    }

    private static void AssertContinuation(JsonElement data, Example example)
    {
        Assert.AreEqual(example.Text, data.GetProperty("text").GetString());
        Assert.AreEqual("bounded-context.v2", data.GetProperty("context").GetProperty("version").GetString());
        Assert.AreEqual("bounded-syntax-roles.v2", data.GetProperty("syntax").GetProperty("version").GetString());
        JsonElement continuation = data.GetProperty("continuation");
        Assert.AreEqual("finite_continuation.v1", continuation.GetProperty("version").GetString());
        Assert.IsTrue(continuation.GetProperty("available").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, continuation.GetProperty("unavailableReason").ValueKind);
        Assert.AreEqual(example.Caret, continuation.GetProperty("caret").GetInt32());
        Assert.AreEqual(example.Start, continuation.GetProperty("start").GetInt32());
        Assert.AreEqual(example.Length, continuation.GetProperty("length").GetInt32());
        Assert.AreEqual(example.Empty ? "" : "раб", continuation.GetProperty("prefix").GetString());
        Assert.AreEqual("exact_package_finite_vocabulary", continuation.GetProperty("candidateSource").GetString());
        Assert.AreEqual("dictionary_score", continuation.GetProperty("ranking").GetString());
        Assert.IsTrue(continuation.GetProperty("examinedReadings").GetInt64() > 0);
        _ = continuation.GetProperty("truncated").GetBoolean();
        _ = continuation.GetProperty("searchComplete").GetBoolean();
        JsonElement expectations = continuation.GetProperty("expectations");
        Assert.IsTrue(expectations.GetArrayLength() > 0);
        foreach (JsonElement expectation in expectations.EnumerateArray())
        {
            Assert.AreEqual(example.Person, expectation.GetProperty("person").GetString());
            Assert.AreEqual("Singular", expectation.GetProperty("number").GetString());
            JsonElement[] supports = expectation.GetProperty("supports").EnumerateArray().ToArray();
            Assert.IsTrue(supports.Length > 0);
            Assert.IsFalse(supports.Any(support => support.GetProperty("role").GetString() == "Predicate"),
                "Future forms have no fabricated token in the original response inventory.");
            JsonElement subject = supports.Single(support => support.GetProperty("role").GetString() == "Subject");
            AssertSubject(data, subject, example);
        }
        JsonElement[] suggestions = continuation.GetProperty("suggestions").EnumerateArray().ToArray();
        Assert.IsTrue(suggestions.Length > 0 && suggestions.Length <= example.Limit);
        Assert.AreEqual(suggestions.Length, suggestions.Select(row => row.GetProperty("form").GetString()).Distinct(StringComparer.Ordinal).Count());
        double previous = double.PositiveInfinity;
        foreach (JsonElement suggestion in suggestions)
        {
            double score = suggestion.GetProperty("score").GetDouble();
            Assert.IsTrue(double.IsFinite(score) && score <= previous); previous = score;
            JsonElement readings = suggestion.GetProperty("readings"), matches = suggestion.GetProperty("matches");
            Assert.IsTrue(readings.GetArrayLength() > 0 && matches.GetArrayLength() > 0);
            foreach (JsonElement match in matches.EnumerateArray())
            {
                int index = match.GetProperty("readingIndex").GetInt32();
                Assert.IsTrue(index >= 0 && index < readings.GetArrayLength());
                WordReading row = readings[index].Deserialize<WordReading>(HttpContract.Json)!;
                Assert.AreEqual(MorphologicalVerbForm.Finite, row.Features.VerbForm);
                Assert.IsFalse(row.Features.HasUnrecognizedFeatures || row.Features.HasConflictingFeatures);
                int[] alternatives = match.GetProperty("expectationIndices").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                Assert.IsTrue(alternatives.Length > 0); Assert.AreEqual(alternatives.Length, alternatives.Distinct().Count());
                foreach (int alternative in alternatives)
                {
                    Assert.IsTrue(alternative >= 0 && alternative < expectations.GetArrayLength());
                    Assert.AreEqual(row.Features.Person.ToString(), expectations[alternative].GetProperty("person").GetString());
                    Assert.AreEqual(row.Features.Number.ToString(), expectations[alternative].GetProperty("number").GetString());
                }
            }
        }
        if (example.Empty) return; // A small empty-prefix window has no invented rank1 witness.
        JsonElement worked = suggestions.Single(row => row.GetProperty("form").GetString() == "работи");
        JsonElement expectedRows = JsonSerializer.SerializeToElement(WorkedRows, HttpContract.Json);
        Assert.IsTrue(SharedDonorResponseTests.SameJson(expectedRows, worked.GetProperty("readings")),
            "All five exact source rows, including noun and imperative, retain every raw field and original ordinal.");
        CollectionAssert.AreEqual(example.Pronoun ? new[] { 2 } : new[] { 3, 4 },
            worked.GetProperty("matches").EnumerateArray().Select(row => row.GetProperty("readingIndex").GetInt32()).ToArray());
        string replaced = example.Text[..example.Start] + "работи" + example.Text[(example.Start + example.Length)..];
        Assert.AreEqual(example.Aliases ? "😀. Супертрактор работи.\r\nСупертрактор раб ѝ и\u0300"
            : example.Pronoun ? "Ти работи" : "Супертрактор работи", replaced);
    }

    private static void AssertSubject(JsonElement data, JsonElement support, Example example)
    {
        JsonElement token = data.GetProperty("tokens")[example.SubjectToken];
        Assert.AreEqual(example.SubjectToken, support.GetProperty("tokenIndex").GetInt32());
        Assert.AreEqual(token.GetProperty("start").GetInt32(), support.GetProperty("start").GetInt32());
        Assert.AreEqual(token.GetProperty("length").GetInt32(), support.GetProperty("length").GetInt32());
        JsonElement complete = Complete(data, example.SubjectToken);
        if (example.Pronoun)
        {
            CollectionAssert.AreEqual(new[] { "Притежателно_местоимение:107,Мъжки,Кратка", "Лично_местоимение:91",
                "Лично_местоимение:91,Дателен,Кратка", "Частица:191" },
                complete.EnumerateArray().Select(row => row.GetProperty("raw").GetProperty("partOfSpeech").GetString()).ToArray());
            JsonElement[] refs = support.GetProperty("readings").EnumerateArray().ToArray();
            Assert.AreEqual(1, refs.Length); Assert.AreEqual(0, refs[0].GetProperty("tokenIndex").GetInt32());
            Assert.AreEqual(1, refs[0].GetProperty("completeReadingIndex").GetInt32());
            Assert.AreEqual("ти", complete[1].GetProperty("raw").GetProperty("lemma").GetString());
            Assert.AreEqual(Source, complete[1].GetProperty("raw").GetProperty("source").GetString());
            Assert.IsFalse(support.TryGetProperty("graphReadings", out JsonElement graph) && graph.ValueKind == JsonValueKind.Array && graph.GetArrayLength() > 0);
            return;
        }
        Assert.AreEqual(0, complete.GetArrayLength());
        JsonElement analysis = token.GetProperty("analysis");
        Assert.IsFalse(analysis.GetProperty("isKnown").GetBoolean());
        Assert.AreEqual(0, analysis.GetProperty("readings").GetArrayLength());
        Assert.IsTrue(analysis.GetProperty("graphExactRootCandidatesComplete").GetBoolean());
        Assert.AreEqual(0, support.GetProperty("readings").GetArrayLength());
        JsonElement[] graphRefs = support.GetProperty("graphReadings").EnumerateArray().ToArray();
        Assert.IsTrue(graphRefs.Length > 0);
        foreach (JsonElement reference in graphRefs)
        {
            Assert.AreEqual(example.SubjectToken, reference.GetProperty("tokenIndex").GetInt32());
            Assert.AreEqual(0, reference.GetProperty("graphCandidateIndex").GetInt32());
            Assert.AreEqual(0, reference.GetProperty("fullTagIndex").GetInt32());
            JsonElement path = analysis.GetProperty("graphCandidates")[0];
            Assert.AreEqual("трактор", path.GetProperty("base").GetString());
            Assert.AreEqual("супертрактор", path.GetProperty("derivedLemma").GetString());
            Assert.AreEqual("Съществително:7,Мъжки", path.GetProperty("fullTags")[0].GetString());
            Assert.IsFalse(path.TryGetProperty("isConditional", out JsonElement conditional) && conditional.GetBoolean());
            Assert.IsFalse(path.TryGetProperty("unmetExactSelfReadingRequirements", out JsonElement unmet) && unmet.GetArrayLength() > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(path.GetProperty("source").GetString()));
        }
    }

    private static JsonElement Complete(JsonElement data, int token)
    {
        JsonElement inventory = data.GetProperty("readingInventory");
        JsonElement occurrence = inventory.GetProperty("occurrences").EnumerateArray().Single(row => row.GetProperty("tokenIndex").GetInt32() == token);
        JsonElement set = inventory.GetProperty("sets")[occurrence.GetProperty("readingSetIndex").GetInt32()];
        return set.GetProperty("supportReadings").ValueKind == JsonValueKind.Null
            ? set.GetProperty("canonicalReadings") : set.GetProperty("supportReadings");
    }

    private static void AssertRepresentation(JsonElement envelope, string version, string format, bool aliases)
    {
        Assert.AreEqual(version, envelope.GetProperty("apiVersion").GetString());
        JsonElement[] tokens = envelope.GetProperty("data").GetProperty("tokens").EnumerateArray().ToArray();
        if (format == HttpContract.RawFormat)
        {
            Assert.IsFalse(envelope.TryGetProperty("evidenceFormat", out _));
            Assert.IsFalse(envelope.TryGetProperty("donorSets", out _));
            Assert.IsFalse(envelope.TryGetProperty("wordAnalyses", out _));
            Assert.IsTrue(tokens.All(token => token.TryGetProperty("analysis", out _) && !token.TryGetProperty("analysisRef", out _)));
            return;
        }
        Assert.AreEqual(format, envelope.GetProperty("evidenceFormat").GetString());
        Assert.AreEqual(JsonValueKind.Array, envelope.GetProperty("donorSets").ValueKind);
        if (format == SharedDonorResponse.Format)
        {
            Assert.IsFalse(envelope.TryGetProperty("wordAnalyses", out _));
            Assert.IsTrue(tokens.All(token => token.TryGetProperty("analysis", out _) && !token.TryGetProperty("analysisRef", out _)));
            return;
        }
        JsonElement analyses = envelope.GetProperty("wordAnalyses");
        Assert.IsTrue(tokens.All(token => !token.TryGetProperty("analysis", out _) && token.TryGetProperty("analysisRef", out _)));
        foreach (JsonElement token in tokens)
        {
            JsonElement reference = token.GetProperty("analysisRef");
            if (token.GetProperty("kind").GetString() == "Word") Assert.IsTrue(reference.GetInt32() >= 0 && reference.GetInt32() < analyses.GetArrayLength());
            else Assert.AreEqual(JsonValueKind.Null, reference.ValueKind);
        }
        if (!aliases) return;
        Assert.AreEqual("Супертрактор", tokens[2].GetProperty("text").GetString());
        Assert.AreEqual("Супертрактор", tokens[5].GetProperty("text").GetString());
        Assert.AreEqual(tokens[2].GetProperty("analysisRef").GetInt32(), tokens[5].GetProperty("analysisRef").GetInt32());
        Assert.AreEqual("ѝ", tokens[7].GetProperty("text").GetString());
        Assert.AreEqual("и\u0300", tokens[8].GetProperty("text").GetString());
        Assert.AreNotEqual(tokens[7].GetProperty("analysisRef").GetInt32(), tokens[8].GetProperty("analysisRef").GetInt32());
    }

    private static string Effective(string version, string? requested) => requested ??
        (version == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat);

    private static Dictionary<string, object?> Body(string text, TextContinuationRequest? continuation, string? format, int contextMode)
    {
        var body = new Dictionary<string, object?> { ["text"] = text };
        if (format is not null) body["evidenceFormat"] = format;
        if (continuation is not null) body["context"] = new { continuation };
        else if (contextMode == 1) body["context"] = null;
        else if (contextMode == 2) body["context"] = new Dictionary<string, object?>();
        return body;
    }

    private static async Task<JsonDocument> Analyze(HttpClient client, string version, string text,
        TextContinuationRequest? continuation, string? format, int contextMode = 0)
    {
        using HttpResponseMessage response = await Post(client, "/api/v" + version + "/analyze-text", Body(text, continuation, format, contextMode));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task AssertError(HttpClient client, string route, Dictionary<string, object?> body, string code)
    {
        using HttpResponseMessage response = await Post(client, route, body);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(code, error.RootElement.GetProperty("code").GetString());
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string route, Dictionary<string, object?> body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route)
        { Content = new StringContent(JsonSerializer.Serialize(body, HttpContract.Json), Encoding.UTF8, "application/json") };
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
    // Insert-only HTTP additions. Four declared leaves below; nominal-format two
    // leaves and captured source rows are supplied separately after the pool closes.
    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Nominal_target_addition_preserves_default_explicit_finite_and_auto_whole_envelopes(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (_, session, client) =>
        {
            const string text = "Ти раб";
            session.AnalyzeText(text);
            JsonElement? baseline = null;
            foreach (string? requested in Formats)
            foreach ((bool include, object? target) in new (bool, object?)[] { (false, null), (true, "Finite"), (true, "Auto") })
            {
                using JsonDocument encoded = await NominalAnalyzeBody(client, version,
                    NominalTargetBody(text, 6, 256, include, target, requested));
                AssertRepresentation(encoded.RootElement, version, Effective(version, requested), false);
                using JsonDocument restored = Restore(encoded);
                AssertContinuation(restored.RootElement.GetProperty("data"), new(text, 6, 3, 3, 256, "Second", 0, Pronoun: true));
                JsonElement continuation = restored.RootElement.GetProperty("data").GetProperty("continuation");
                Assert.IsFalse(continuation.TryGetProperty("target", out JsonElement ignoredTarget));
                Assert.IsFalse(continuation.TryGetProperty("nominalExpectations", out JsonElement ignoredNominalExpectations));
                if (baseline is null) baseline = restored.RootElement.Clone();
                else Assert.IsTrue(SharedDonorResponseTests.SameJson(baseline.Value, restored.RootElement),
                    "Omitted/explicit Finite and Auto-selected finite retain the entire old response.");
            }
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Nominal_target_http_rejects_undefined_enums_and_malformed_values_without_changing_enum_conventions(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (_, _, client) =>
        {
            (object? Value, string Code)[] invalid =
            [
                ("Adjective", "invalid-json"), (null, "invalid-json"),
                (false, "invalid-json"), (Array.Empty<object>(), "invalid-json"),
                (new Dictionary<string, object?>(), "invalid-json"),
                (-1, "invalid-continuation-target"), (3, "invalid-continuation-target"),
                (int.MaxValue, "invalid-continuation-target")
            ];
            foreach (var item in invalid)
                await AssertError(client, "/api/v" + version + "/analyze-text",
                    NominalTargetBody("Ти раб", 6, 12, true, item.Value, null), item.Code);
            Dictionary<string, object?> unknown = NominalTargetBody("Ти раб", 6, 12, true, "Auto", null);
            var context = (Dictionary<string, object?>)unknown["context"]!;
            var continuation = (Dictionary<string, object?>)context["continuation"]!;
            continuation["targetOther"] = "Noun";
            await AssertError(client, "/api/v" + version + "/analyze-text", unknown, "invalid-json");
            await AssertError(client, "/api/v" + version + "/lookup",
                NominalTargetBody("книга", 0, 12, true, "Noun", null), "option-not-applicable");

            // The existing global enum converter accepts defined numeric values.
            // This feature rejects undefined values without introducing a new wire policy.
            foreach (int defined in new[] { 0, 1, 2 })
            {
                using JsonDocument response = await NominalAnalyzeBody(client, version,
                    NominalTargetBody("Ти раб", 6, 12, true, defined, HttpContract.RawFormat));
                JsonElement actual = response.RootElement.GetProperty("data").GetProperty("continuation");
                if (defined == (int)TextContinuationTarget.Noun)
                {
                    Assert.AreEqual("Noun", actual.GetProperty("target").GetString());
                    Assert.AreEqual("nominal_continuation.v1", actual.GetProperty("version").GetString());
                    Assert.AreEqual(0, actual.GetProperty("expectations").GetArrayLength());
                }
                else
                {
                    Assert.AreEqual("finite_continuation.v1", actual.GetProperty("version").GetString());
                    Assert.IsFalse(actual.TryGetProperty("target", out _));
                    Assert.IsFalse(actual.TryGetProperty("nominalExpectations", out _));
                }
            }
        });
    }

    private static Dictionary<string, object?> NominalTargetBody(string text, int caret, int limit,
        bool includeTarget, object? target, string? format)
    {
        var continuation = new Dictionary<string, object?> { ["caret"] = caret, ["limit"] = limit };
        if (includeTarget) continuation["target"] = target;
        var body = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["context"] = new Dictionary<string, object?> { ["continuation"] = continuation }
        };
        if (format is not null) body["evidenceFormat"] = format;
        return body;
    }

    private static async Task<JsonDocument> NominalAnalyzeBody(HttpClient client, string version,
        Dictionary<string, object?> body)
    {
        using HttpResponseMessage response = await Post(client, "/api/v" + version + "/analyze-text", body);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
    }

    private sealed record NominalHttpExample(string Text, int Caret, int Start, int Length, string Prefix,
        string Number, string? Gender, string FirstArticle, int[] AttributeTokens, int[][] AttributeRows,
        string? Witness = null, bool Graph = false, TextContinuationTarget RequestTarget = TextContinuationTarget.Noun);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Nominal_formats_preserve_original_attribute_sources_candidate_rows_and_whole_edit_spans(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (_, session, client) =>
        {
            NominalHttpExample[] examples =
            [
                new("Нова кни", 8, 5, 3, "кни", "Singular", "Feminine", "Indefinite", [0], [[0]], "книга", RequestTarget: TextContinuationTarget.Auto),
                new("Новият голям гр", 15, 13, 2, "гр", "Singular", "Masculine", "Full", [0, 1], [[0], [1]], "град"),
                new("Нова ", 5, 5, 0, "", "Singular", "Feminine", "Indefinite", [0], [[0]]),
                new("Новите кн", 9, 7, 2, "кн", "Plural", null, "Definite", [0], [[0]], "книги"),
                new("😀. Нова книXYZ лежат.", 12, 9, 6, "кни", "Singular", "Feminine", "Indefinite", [2], [[0]], "книга"),
                new("Суперхубав гр", 13, 11, 2, "гр", "Singular", "Masculine", "Indefinite", [0], [[]], "град", Graph: true)
            ];
            foreach (NominalHttpExample example in examples)
            {
                TextAnalysis original = session.AnalyzeText(example.Text);
                int limit = example.Prefix.Length == 0 ? 12 : 256;
                using JsonDocument raw = await Analyze(client, version, example.Text,
                    new(example.Caret, limit) { Target = example.RequestTarget }, HttpContract.RawFormat);
                AssertRepresentation(raw.RootElement, version, HttpContract.RawFormat, false);
                AssertNominalHttp(raw.RootElement.GetProperty("data"), example);
                var withoutContinuation = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                    raw.RootElement.GetProperty("data").GetRawText(), HttpContract.Json)!;
                Assert.IsTrue(withoutContinuation.Remove("continuation"));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(
                    JsonSerializer.SerializeToElement(original, HttpContract.Json),
                    JsonSerializer.SerializeToElement(withoutContinuation, HttpContract.Json)),
                    "Nominal evidence does not rewrite existing raw analysis, inventory, roles or article authority.");
                foreach (string? requested in Formats.Skip(1))
                {
                    using JsonDocument encoded = await Analyze(client, version, example.Text,
                        new(example.Caret, limit) { Target = example.RequestTarget }, requested);
                    AssertRepresentation(encoded.RootElement, version, Effective(version, requested), false);
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement));
                    AssertNominalHttp(restored.RootElement.GetProperty("data"), example);
                }
            }
        });
    }

    private static void AssertNominalHttp(JsonElement data, NominalHttpExample example)
    {
        Assert.AreEqual(example.Text, data.GetProperty("text").GetString());
        JsonElement continuation = data.GetProperty("continuation");
        Assert.AreEqual("Noun", continuation.GetProperty("target").GetString());
        Assert.AreEqual("nominal_continuation.v1", continuation.GetProperty("version").GetString());
        Assert.AreEqual("exact_package_nominal_vocabulary", continuation.GetProperty("candidateSource").GetString());
        Assert.AreEqual("dictionary_score", continuation.GetProperty("ranking").GetString());
        Assert.IsTrue(continuation.GetProperty("available").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, continuation.GetProperty("unavailableReason").ValueKind);
        Assert.AreEqual(example.Caret, continuation.GetProperty("caret").GetInt32());
        Assert.AreEqual(example.Start, continuation.GetProperty("start").GetInt32());
        Assert.AreEqual(example.Length, continuation.GetProperty("length").GetInt32());
        Assert.AreEqual(example.Prefix, continuation.GetProperty("prefix").GetString());
        Assert.AreEqual(0, continuation.GetProperty("expectations").GetArrayLength());
        Assert.IsTrue(continuation.GetProperty("examinedReadings").GetInt64() > 0);
        Assert.AreNotEqual(continuation.GetProperty("truncated").GetBoolean(), continuation.GetProperty("searchComplete").GetBoolean());
        JsonElement branches = continuation.GetProperty("nominalExpectations");
        Assert.AreEqual(1, branches.GetArrayLength());
        JsonElement branch = branches[0];
        Assert.AreEqual(example.Number, branch.GetProperty("number").GetString());
        if (example.Gender is null) Assert.AreEqual(JsonValueKind.Null, branch.GetProperty("gender").ValueKind);
        else Assert.AreEqual(example.Gender, branch.GetProperty("gender").GetString());
        Assert.AreEqual(example.FirstArticle, branch.GetProperty("firstAttributeArticle").GetString());
        Assert.AreEqual("Indefinite", branch.GetProperty("nounArticle").GetString());
        Assert.IsFalse(branch.TryGetProperty("person", out _));
        Assert.IsFalse(branch.TryGetProperty("supports", out _));
        JsonElement attributes = branch.GetProperty("attributes");
        Assert.AreEqual(example.AttributeTokens.Length, attributes.GetArrayLength());
        for (int a = 0; a < example.AttributeTokens.Length; a++)
        {
            int tokenIndex = example.AttributeTokens[a];
            JsonElement attribute = attributes[a];
            JsonElement token = data.GetProperty("tokens")[tokenIndex];
            Assert.AreEqual("Attribute", attribute.GetProperty("role").GetString());
            Assert.AreEqual(tokenIndex, attribute.GetProperty("tokenIndex").GetInt32());
            Assert.AreEqual(token.GetProperty("start").GetInt32(), attribute.GetProperty("start").GetInt32());
            Assert.AreEqual(token.GetProperty("length").GetInt32(), attribute.GetProperty("length").GetInt32());
            Assert.IsTrue(attribute.GetProperty("start").GetInt32() + attribute.GetProperty("length").GetInt32() <= example.Start);
            if (example.Graph)
            {
                Assert.AreEqual(0, attribute.GetProperty("readings").GetArrayLength());
                JsonElement refs = attribute.GetProperty("graphReadings");
                Assert.AreEqual(1, refs.GetArrayLength());
                Assert.AreEqual(tokenIndex, refs[0].GetProperty("tokenIndex").GetInt32());
                Assert.AreEqual(0, refs[0].GetProperty("graphCandidateIndex").GetInt32());
                Assert.AreEqual(0, refs[0].GetProperty("fullTagIndex").GetInt32());
                JsonElement analysis = token.GetProperty("analysis");
                Assert.IsFalse(analysis.GetProperty("isKnown").GetBoolean());
                Assert.AreEqual(0, analysis.GetProperty("readings").GetArrayLength());
                Assert.AreEqual(0, Complete(data, tokenIndex).GetArrayLength());
                Assert.IsTrue(analysis.GetProperty("graphExactRootCandidatesComplete").GetBoolean());
                JsonElement graph = analysis.GetProperty("graphCandidates")[0];
                Assert.AreEqual("суперхубав", graph.GetProperty("derivedLemma").GetString());
                Assert.AreEqual("Прилагателно:76,Мъжки", graph.GetProperty("fullTags")[0].GetString());
            }
            else
            {
                JsonElement refs = attribute.GetProperty("readings");
                CollectionAssert.AreEqual(example.AttributeRows[a], refs.EnumerateArray().Select(r => r.GetProperty("completeReadingIndex").GetInt32()).ToArray());
                Assert.IsTrue(refs.EnumerateArray().All(r => r.GetProperty("tokenIndex").GetInt32() == tokenIndex));
                Assert.IsFalse(attribute.TryGetProperty("graphReadings", out _) && attribute.GetProperty("graphReadings").ValueKind != JsonValueKind.Null);
                string key = token.GetProperty("text").GetString()!.ToLowerInvariant();
                JsonElement expected = JsonSerializer.SerializeToElement(NominalPinnedRows(key), HttpContract.Json);
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expected, JsonSerializer.SerializeToElement(
                    Complete(data, tokenIndex).EnumerateArray().Select(cell => cell.GetProperty("raw")).ToArray(), HttpContract.Json)));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(expected, token.GetProperty("analysis").GetProperty("readings")));
            }
        }
        JsonElement suggestions = continuation.GetProperty("suggestions");
        Assert.IsTrue(suggestions.GetArrayLength() > 0);
        foreach (JsonElement suggestion in suggestions.EnumerateArray())
        {
            JsonElement readings = suggestion.GetProperty("readings");
            JsonElement matches = suggestion.GetProperty("matches");
            Assert.IsTrue(readings.GetArrayLength() > 0 && matches.GetArrayLength() > 0);
            foreach (JsonElement match in matches.EnumerateArray())
            {
                int index = match.GetProperty("readingIndex").GetInt32();
                Assert.IsTrue(index >= 0 && index < readings.GetArrayLength());
                WordReading source = readings[index].Deserialize<WordReading>(HttpContract.Json)!;
                Assert.AreEqual(MorphologicalPartOfSpeech.Noun, source.Features.PartOfSpeech);
                Assert.AreEqual(MorphologicalArticle.Indefinite, source.Features.Article);
                Assert.AreEqual(example.Number, source.Features.Number.ToString());
                if (example.Gender is not null) Assert.AreEqual(example.Gender, source.Features.Gender.ToString());
                CollectionAssert.AreEqual(new[] { 0 }, match.GetProperty("expectationIndices").EnumerateArray().Select(x => x.GetInt32()).ToArray());
            }
        }
        if (example.Witness is not null)
        {
            JsonElement witness = suggestions.EnumerateArray().Single(s => s.GetProperty("form").GetString() == example.Witness);
            JsonElement expected = JsonSerializer.SerializeToElement(NominalPinnedRows(example.Witness), HttpContract.Json);
            Assert.IsTrue(SharedDonorResponseTests.SameJson(expected, witness.GetProperty("readings")));
            CollectionAssert.AreEqual(new[] { 0 }, witness.GetProperty("matches").EnumerateArray().Select(m => m.GetProperty("readingIndex").GetInt32()).ToArray());
        }
    }

    // Immutable raw cells from premeasurement pool 0df19d9bf39da84a1c829721f23d9d3d09b6191c08854fd23ede5fadf6dcfa3a; no continuation output used.
    private const string NominalPinnedSource = "neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7";

    private static WordReading[] NominalPinnedRows(string word) => word switch
    {
        "книга" =>
        [
            new("книга", "книга", "Съществително:41,Женски", 1d, null, null, null, NominalPinnedSource),
        ],
        "книги" =>
        [
            new("книги", "книга", "Съществително:41,Женски,мн.ч.", 1d, null, null, null, NominalPinnedSource),
        ],
        "град" =>
        [
            new("град", "град", "Съществително:1,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        "нова" =>
        [
            new("нова", "нов", "Прилагателно:76,Женски", 1d, null, null, null, NominalPinnedSource),
            new("Нова", "Нова", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "новите" =>
        [
            new("новите", "нов", "Прилагателно:76,Мъжки,мн.ч.,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "новият" =>
        [
            new("новият", "нов", "Прилагателно:76,Мъжки,Пълен", 1d, null, null, null, NominalPinnedSource),
        ],
        "голям" =>
        [
            new("Голям", "Голям", "Място:195", 1d, null, null, null, NominalPinnedSource),
            new("голям", "голям", "Прилагателно:81,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        _ => throw new InvalidOperationException("No premeasurement raw fixture: " + word)
    };


}
