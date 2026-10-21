using System.Collections;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class ContextCompositionResponseTests
{
    private static readonly string[] FeatureFields = ["rawTag", "partOfSpeech", "paradigmId", "person", "number", "gender",
        "tense", "verbForm", "article", "hasUnrecognizedFeatures", "hasConflictingFeatures", "nameKind",
        "case", "isShortForm", "participleKind"];

    [TestMethod]
    [DataRow("raw-v1")][DataRow("shared-donors-v1")][DataRow("shared-analyses-v1")]
    public async Task Every_format_keeps_fifteen_feature_fields_refs31_32_33_raw_duplicates_and_exact_budget(string format)
    {
        foreach (bool supportRoute in new[] { false, true })
        {
            TextAnalysis text = LiteralText(supportRoute);
            ApiEnvelope raw = Envelope(text);
            byte[] expected = Encode(raw, format);
            using var rejectedBody = new MemoryStream();
            var rejected = new DefaultHttpContext(); rejected.Response.Body = rejectedBody;
            ApiRequestException error = await Assert.ThrowsAsync<ApiRequestException>(() => new ResponseWriter(expected.Length - 1)
                .WriteAsync(rejected, raw, format, CancellationToken.None));
            Assert.AreEqual(413, error.Status); Assert.AreEqual("response-too-large", error.Code);
            Assert.AreEqual(0L, rejectedBody.Length); Assert.IsFalse(rejected.Response.HasStarted);
            Assert.IsNull(rejected.Response.ContentLength); Assert.IsNull(rejected.Response.ContentType);
            foreach (long budget in new[] { (long)expected.Length, expected.Length + 1L })
            {
                using var body = new MemoryStream(); var response = new DefaultHttpContext(); response.Response.Body = body;
                await new ResponseWriter(budget).WriteAsync(response, raw, format, CancellationToken.None);
                CollectionAssert.AreEqual(expected, body.ToArray());
                Assert.AreEqual((long)expected.Length, response.Response.ContentLength);
                using JsonDocument encoded = JsonDocument.Parse(body.ToArray());
                using JsonDocument restored = Restore(encoded);
                using JsonDocument complete = JsonDocument.Parse(JsonSerializer.Serialize(raw, HttpContract.Json));
                Assert.IsTrue(SharedDonorResponseTests.SameJson(complete.RootElement, restored.RootElement));
                AssertLiteralReferences(restored.RootElement.GetProperty("data"), supportRoute);
                if (format == SharedDonorResponse.AnalysisFormat)
                {
                    Assert.AreEqual(2, encoded.RootElement.GetProperty("wordAnalyses").GetArrayLength());
                    CollectionAssert.AreEqual(new[] { 0, 0, 1 }, encoded.RootElement.GetProperty("data").GetProperty("tokens")
                        .EnumerateArray().Where(token => token.GetProperty("kind").GetString() == "Word")
                        .Select(token => token.GetProperty("analysisRef").GetInt32()).ToArray());
                    Assert.AreEqual(32, encoded.RootElement.GetProperty("wordAnalyses")[0].GetProperty("readings").GetArrayLength());
                }
            }
        }
    }

    [TestMethod]
    public async Task Context_preflight_cancellation_never_publishes_empty_or_partial_success()
    {
        using var cancellation = new CancellationTokenSource();
        TextAnalysis text = LiteralText();
        text = text with { Context = text.Context! with
            { Sentences = new CancelList<TextContextSentence>(text.Context.Sentences, cancellation) } };
        using var body = new MemoryStream(); var response = new DefaultHttpContext(); response.Response.Body = body;
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ResponseWriter(1_000_000)
            .WriteAsync(response, Envelope(text), HttpContract.RawFormat, cancellation.Token));
        Assert.IsTrue(cancellation.IsCancellationRequested); Assert.AreEqual(0L, body.Length);
        Assert.IsFalse(response.Response.HasStarted); Assert.IsNull(response.Response.ContentLength);
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ResponseWriter(1_000_000)
            .WriteAsync(response, Envelope(LiteralText()), SharedDonorResponse.AnalysisFormat, before.Token));
        Assert.AreEqual(0L, body.Length); Assert.IsNull(response.Response.ContentLength);
    }

    // Independent wire-only objects: no analyzer/FromTag output is used to generate expectations.
    // Repeated raw tuples deliberately occupy every index; equality must not replace positional identity.
    private static TextAnalysis LiteralText(bool supportRoute = false)
    {
        const string source = "😀 го го писал.";
        const string pronounTag = "Лично_местоимение:92,Мъжки,Винителен,Кратка";
        const string participleTag = "Глагол:187,Минало_свършено_деятелно,Мъжки";
        WordReading go = new("го", "той", pronounTag, 1, 92, 100, 101, "independent-wire-" + new string('x', 512));
        MorphologicalFeatures goFeatures = new(pronounTag, MorphologicalPartOfSpeech.PersonalPronoun, "92",
            MorphologicalPerson.Third, MorphologicalNumber.Singular, MorphologicalGender.Masculine,
            null, null, null, false, false) { Case = MorphologicalCase.Accusative, IsShortForm = true };
        TextReadingSnapshot[] rows = Enumerable.Repeat(new TextReadingSnapshot(go, goFeatures), 34).ToArray();
        WordReading wrote = new("писал", "пиша", participleTag, 1, 187, 200, 201, "independent-wire");
        MorphologicalFeatures wroteFeatures = new(participleTag, MorphologicalPartOfSpeech.Verb, "187",
            null, MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalTense.Past,
            MorphologicalVerbForm.Participle, MorphologicalArticle.Indefinite, false, false)
            { ParticipleKind = MorphologicalParticipleKind.PastAoristActive };
        WordAnalysis goWord = new("го", "го", rows.Take(32).Select(row => row.Raw).ToArray(), null);
        WordAnalysis wroteWord = new("писал", "писал", [wrote], null);
        TextReadingReference[] firstRefs = [new(1, 31), new(1, 32), new(1, 33)];
        TextReadingReference[] secondRefs = [new(2, 31), new(2, 32), new(2, 33)];
        TextContextRoleSelection role = new(TextContextRole.Attribute, 1, 3, 2, firstRefs, ["wire.literal"]);
        return new(source, [new("😀", TokenKind.Symbol, 0, 2, null), new("го", TokenKind.Word, 3, 2, goWord),
            new("го", TokenKind.Word, 6, 2, goWord), new("писал", TokenKind.Word, 9, 5, wroteWord),
            new(".", TokenKind.Punctuation, 14, 1, null)])
        {
            Sentences = [new(0, 15)],
            ReadingInventory = new([new("го", "го", supportRoute ? Array.Empty<TextReadingSnapshot>() : rows,
                    supportRoute ? "го" : null, supportRoute ? rows : null, 32),
                new("писал", "писал", [new(wrote, wroteFeatures)], null, null, 1)],
                [new(1, "го", 3, 2, 0), new(2, "го", 6, 2, 0), new(3, "писал", 9, 5, 1)]),
            Context = new("bounded-context.v1", true, null, [new(0, 15, false, TextContextSelectionStatus.Partial, null, 3,
                [new(3, false, null, 0, "wire-only", [role], ["wire.literal"])], [role], [], ["wire.literal"])]),
            Syntax = new("bounded-syntax-roles.v1", true, null, [new(0, 15, "partial", "wire-only", null,
                [new(TextSyntaxRole.Attribute, "wire.literal", 3, 5,
                    [new(1, "wire-only", firstRefs), new(2, "wire-only", secondRefs)],
                    [new(3, "wire-only", [new(3, 0)])])])], null),
            Validation = new("bounded-validation.v1", true, null, [new("wire.literal", 6, 2, "wire-only", secondRefs)], [], null, null, [])
        };
    }

    private static void AssertLiteralReferences(JsonElement text, bool supportRoute)
    {
        CollectionAssert.AreEqual(new[] { "text", "tokens", "sentences", "phonetics", "diagnostics", "lexical", "readingInventory",
            "metrics", "spelling", "profile", "properNames", "context", "syntax", "validation", "wordConstructions" },
            text.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(JsonValueKind.Null, text.GetProperty("wordConstructions").ValueKind);
        Assert.AreEqual("😀 го го писал.", text.GetProperty("text").GetString());
        JsonElement sets = text.GetProperty("readingInventory").GetProperty("sets");
        Assert.AreEqual(supportRoute ? 0 : 34, sets[0].GetProperty("canonicalReadings").GetArrayLength());
        Assert.AreEqual(supportRoute ? JsonValueKind.Array : JsonValueKind.Null, sets[0].GetProperty("supportReadings").ValueKind);
        if (supportRoute) Assert.AreEqual("го", sets[0].GetProperty("supportKey").GetString());
        Assert.AreEqual(32, sets[0].GetProperty("displayedReadingCount").GetInt32());
        Assert.IsTrue(sets[0].GetProperty("displayTruncated").GetBoolean());
        JsonElement rows = sets[0].GetProperty(supportRoute ? "supportReadings" : "canonicalReadings");
        Assert.AreEqual(34, rows.GetArrayLength());
        foreach (int index in new[] { 31, 32, 33 })
        {
            Assert.IsTrue(SharedDonorResponseTests.SameJson(rows[0].GetProperty("raw"), rows[index].GetProperty("raw")));
            JsonElement raw = rows[index].GetProperty("raw"), features = rows[index].GetProperty("features");
            CollectionAssert.AreEquivalent(FeatureFields, features.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual("го", raw.GetProperty("form").GetString()); Assert.AreEqual("той", raw.GetProperty("lemma").GetString());
            Assert.AreEqual("Лично_местоимение:92,Мъжки,Винителен,Кратка", raw.GetProperty("partOfSpeech").GetString());
            Assert.AreEqual(101U, raw.GetProperty("legacyWordId").GetUInt32());
            Assert.AreEqual("Accusative", features.GetProperty("case").GetString());
            Assert.IsTrue(features.GetProperty("isShortForm").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, features.GetProperty("participleKind").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, features.GetProperty("nameKind").ValueKind);
        }
        JsonElement part = sets[1].GetProperty("canonicalReadings")[0].GetProperty("features");
        CollectionAssert.AreEquivalent(FeatureFields, part.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual("PastAoristActive", part.GetProperty("participleKind").GetString());
        Assert.AreEqual(JsonValueKind.Null, part.GetProperty("case").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, part.GetProperty("isShortForm").ValueKind);
        JsonElement contextRole = text.GetProperty("context").GetProperty("sentences")[0].GetProperty("localRoles")[0];
        JsonElement syntax = text.GetProperty("syntax").GetProperty("sentences")[0].GetProperty("candidates")[0];
        JsonElement diagnostic = text.GetProperty("validation").GetProperty("diagnostics")[0];
        AssertRefs(contextRole.GetProperty("readings"), 1);
        AssertRefs(syntax.GetProperty("targets")[0].GetProperty("readings"), 1);
        AssertRefs(syntax.GetProperty("targets")[1].GetProperty("readings"), 2);
        AssertRefs(diagnostic.GetProperty("supports"), 2);
        Assert.AreEqual(3, contextRole.GetProperty("start").GetInt32()); Assert.AreEqual(2, contextRole.GetProperty("length").GetInt32());
        Assert.AreEqual(3, syntax.GetProperty("start").GetInt32()); Assert.AreEqual(5, syntax.GetProperty("length").GetInt32());
        Assert.AreEqual(6, diagnostic.GetProperty("start").GetInt32()); Assert.AreEqual(2, diagnostic.GetProperty("length").GetInt32());
        Assert.AreEqual("го", text.GetProperty("text").GetString()!.Substring(6, 2));
        void AssertRefs(JsonElement references, int tokenIndex)
        {
            CollectionAssert.AreEqual(new[] { tokenIndex, tokenIndex, tokenIndex }, references.EnumerateArray()
                .Select(value => value.GetProperty("tokenIndex").GetInt32()).ToArray());
            CollectionAssert.AreEqual(new[] { 31, 32, 33 }, references.EnumerateArray()
                .Select(value => value.GetProperty("completeReadingIndex").GetInt32()).ToArray());
            foreach (JsonElement reference in references.EnumerateArray())
            {
                JsonElement occurrence = text.GetProperty("readingInventory").GetProperty("occurrences").EnumerateArray()
                    .Single(value => value.GetProperty("tokenIndex").GetInt32() == reference.GetProperty("tokenIndex").GetInt32());
                Assert.AreEqual(0, occurrence.GetProperty("readingSetIndex").GetInt32());
                JsonElement set = sets[occurrence.GetProperty("readingSetIndex").GetInt32()];
                JsonElement resolved = set.GetProperty("supportReadings").ValueKind == JsonValueKind.Null
                    ? set.GetProperty("canonicalReadings") : set.GetProperty("supportReadings");
                JsonElement exact = resolved[reference.GetProperty("completeReadingIndex").GetInt32()];
                Assert.AreEqual(101U, exact.GetProperty("raw").GetProperty("legacyWordId").GetUInt32());
            }
        }
    }

    private sealed class CancelList<T>(IReadOnlyList<T> values, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => values.Count;
        public T this[int index] { get { cancellation.Cancel(); return values[index]; } }
        public IEnumerator<T> GetEnumerator() { cancellation.Cancel(); return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
