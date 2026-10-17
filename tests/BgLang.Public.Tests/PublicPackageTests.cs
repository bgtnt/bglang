using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

// Complete candidate checks use public API fields and independent literal records only.
[TestClass]
public sealed class PublicPackageTests
{
    private static BulgarianLanguageSession session = null!;
    private static string manifest = null!;
    private static JsonElement candidate;
    private static JsonElement[] cases = [];
    private static JsonElement[] typedTags = [];

    [ClassInitialize]
    public static void OpenExactCompletePackage(TestContext _)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data/public-input-lock.v1.json")));
        Assert.AreEqual("bglang.public-input-lock.v1", document.RootElement.GetProperty("schema").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("contractComplete").GetBoolean(), "Complete public input contract is required; missing inputs never skip.");
        candidate = document.RootElement.GetProperty("expectedCandidate").Clone();
        string? supplied = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(supplied), "Set the explicit complete candidate manifest.");
        Assert.IsTrue(Path.IsPathFullyQualified(supplied!));
        manifest = Path.GetFullPath(supplied!);
        Assert.AreEqual("manifest.json", Path.GetFileName(manifest));
        string directory = Path.GetDirectoryName(manifest)!;
        var files = candidate.GetProperty("files").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(files.Select(file => file.GetProperty("name").GetString()).ToArray(),
            Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray());
        foreach (var file in files)
        {
            string name = file.GetProperty("name").GetString()!;
            Assert.AreEqual(name, Path.GetFileName(name));
            string path = Path.Combine(directory, name);
            Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
            Assert.AreEqual(file.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        byte[] fixtureBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data/public-literal-readings.v1.json"));
        Assert.AreEqual("a8a188b2145a8201b8d6d49ae85ac730bc21839645f1311c52a2e6387f0659f4", Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)));
        using var fixture = JsonDocument.Parse(fixtureBytes);
        cases = fixture.RootElement.GetProperty("cases").EnumerateArray().Select(value => value.Clone()).ToArray();
        typedTags = fixture.RootElement.GetProperty("frozenTypedTagAssertions").EnumerateArray().Select(value => value.Clone()).ToArray();
        session = BulgarianLanguageSession.Open(manifest);
    }

    [ClassCleanup]
    public static void ClosePackage() => session?.Dispose();

    [TestMethod]
    public void Complete_package_identity_is_exact()
    {
        Assert.AreEqual(candidate.GetProperty("packageId").GetString(), session.Provenance.PackageId);
        Assert.AreEqual(candidate.GetProperty("sourceId").GetString(), session.Provenance.LexiconSource);
        Assert.IsNull(session.Provenance.AnnotationsSha256);
    }

    [TestMethod]
    [DataRow("име")]
    [DataRow("знаме")]
    public void Complete_forms_equal_independent_literal_records(string lemma)
    {
        var fixture = cases.Single(value => value.GetProperty("lemma").GetString() == lemma);
        var expected = fixture.GetProperty("rawReadings").EnumerateArray().Select(value => Key(
            value.GetProperty("form").GetString()!, value.GetProperty("lemma").GetString()!, value.GetProperty("fullTag").GetString())).Order(StringComparer.Ordinal).ToArray();
        var actual = session.GenerateForms(lemma, 16).Select(value => Key(value.Form, value.Lemma, value.Tag)).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("име")]
    [DataRow("знаме")]
    public void Lookup_preserves_every_literal_tag_and_score(string lemma)
    {
        foreach (var reading in cases.Single(value => value.GetProperty("lemma").GetString() == lemma).GetProperty("rawReadings").EnumerateArray())
        {
            var actual = session.Lookup(reading.GetProperty("form").GetString()!, LookupMode.Exact, 16);
            Assert.AreEqual(1, actual.Count);
            Assert.AreEqual(reading.GetProperty("lemma").GetString(), actual[0].Lemma);
            Assert.AreEqual(reading.GetProperty("fullTag").GetString(), actual[0].PartOfSpeech);
            Assert.AreEqual(reading.GetProperty("score").GetDouble(), actual[0].Score);
            Assert.IsNull(actual[0].LegacyTypeId);
            Assert.IsNull(actual[0].LegacyBaseId);
            Assert.IsNull(actual[0].LegacyWordId);
            Assert.AreEqual(candidate.GetProperty("sourceId").GetString(), actual[0].Source);
            JsonElement tag = typedTags.Single(value => value.GetProperty("rawTag").GetString() == actual[0].PartOfSpeech);
            MorphologicalFeatures features = actual[0].Features;
            Assert.AreEqual(tag.GetProperty("partOfSpeech").GetString(), features.PartOfSpeech.ToString());
            Assert.AreEqual(tag.GetProperty("paradigmId").GetString(), features.ParadigmId);
            Assert.AreEqual(tag.GetProperty("number").GetString(), features.Number?.ToString());
            Assert.AreEqual(tag.GetProperty("gender").GetString(), features.Gender?.ToString());
            Assert.AreEqual(tag.GetProperty("article").GetString(), features.Article?.ToString());
            Assert.AreEqual(tag.GetProperty("hasUnrecognizedFeatures").GetBoolean(), features.HasUnrecognizedFeatures);
            Assert.AreEqual(tag.GetProperty("hasConflictingFeatures").GetBoolean(), features.HasConflictingFeatures);
        }
    }

    [TestMethod]
    public void Repeated_text_preserves_original_offsets_and_readings()
    {
        var words = session.AnalyzeText("име име").Tokens.Where(value => value.Kind == TokenKind.Word).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 4 }, words.Select(value => value.Start).ToArray());
        foreach (var word in words)
        {
            Assert.AreEqual("име", word.Text);
            Assert.AreEqual(3, word.Length);
            Assert.IsNotNull(word.Analysis);
            Assert.IsTrue(word.Analysis.IsKnown);
            CollectionAssert.AreEqual(session.Lookup("име").Select(value => Key(value.Form, value.Lemma, value.PartOfSpeech)).ToArray(),
                word.Analysis.Readings.Select(value => Key(value.Form, value.Lemma, value.PartOfSpeech)).ToArray());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Canceled_public_overloads_leave_exact_lookup_usable(bool text)
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        OperationCanceledException error = Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            if (text) session.AnalyzeText("име име", source.Token);
            else session.AnalyzeWord("име", 16, source.Token);
        });
        Assert.AreEqual(source.Token, error.CancellationToken);
        Assert.AreEqual("име", session.Lookup("име", LookupMode.Exact, 16).Single().Lemma);
    }

    [TestMethod]
    [DataRow("суперработник", "работник")]
    [DataRow("свръхсупертракторите", "трактор")]
    [DataRow("супертрактор", "трактор")]
    [DataRow("суперхубав", "хубав")]
    [DataRow("суперкнига", "книга")]
    public void Unknown_prefixes_preserve_exact_root_paths_and_independent_limits(string query, string root)
    {
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.AreEqual(query, word.Word);
        Assert.AreEqual(query, word.NormalizedWord);
        Assert.IsFalse(word.IsKnown);
        Assert.AreEqual(0, word.Readings.Count);
        Assert.IsNotNull(word.BaseEvidence);
        Assert.AreEqual(root, word.BaseEvidence.Base);
        WordDerivation exact = word.GraphCandidates.Single(path => path.Base == root && !path.IsConditional);
        Assert.IsNull(exact.ConditionalRootProgramId);
        Assert.IsNull(exact.ConditionalRootBaseFullTag);
        Assert.IsNull(exact.ConditionalRootSupportCount);
        Assert.IsNull(exact.ConditionalSupportingLemmas);
        Assert.IsNull(exact.UnmetExactSelfReadingRequirements);
        AssertSearchFlags(word, false, false, false, true, false, true, true);
    }

    [TestMethod]
    public void Conditional_zero_path_keeps_unmet_exact_self_requirement()
    {
        WordAnalysis word = session.AnalyzeWord("суперработник", 256);
        WordDerivation path = word.GraphCandidates.Single(value => value.IsConditional &&
            value.Base == "суперработник" && value.ConditionalRootProgramId == "productive.noun.masculine_type14.v0");
        Assert.IsFalse(word.IsKnown);
        Assert.AreEqual(0, word.Readings.Count);
        Assert.AreEqual("Съществително:14,Мъжки", path.ConditionalRootBaseFullTag);
        Assert.AreEqual(1042, path.ConditionalRootSupportCount);
        Assert.IsNotNull(path.ConditionalSupportingLemmas);
        Assert.AreEqual(1042, path.ConditionalSupportingLemmas.Count);
        CollectionAssert.Contains(path.ConditionalSupportingLemmas.ToArray(), "авджилък");
        Assert.IsNotNull(path.UnmetExactSelfReadingRequirements);
        ExactSelfReadingRequirement requirement = path.UnmetExactSelfReadingRequirements.Single();
        Assert.AreEqual("суперработник", requirement.InputSurface);
        Assert.AreEqual("Съществително:14,Мъжки", requirement.FullTag);
        Assert.AreEqual("program|productive.noun.masculine_type14.v0|base", requirement.EdgeId);
        Assert.AreEqual("суперработник", path.DerivedLemma);
        CollectionAssert.AreEqual(new[] { "Съществително:14,Мъжки" }, path.FullTags!.ToArray());
    }

    [TestMethod]
    public void Reviewed_decomposition_does_not_imply_productive_generation()
    {
        WordDecomposition value = session.DecomposeWord("писател");
        Assert.IsTrue(value.HasEvidence);
        Assert.AreEqual("a:писател", value.ApplicationId);
        Assert.AreEqual("transparent", value.Transparency);
        Assert.AreEqual(false, value.ProductiveGeneration);
    }

    [TestMethod]
    public void Fuzzy_transposition_preserves_independent_book_reading()
    {
        FuzzyMatch value = session.FindSimilar("книаг", 1, 256).Single();
        Assert.AreEqual("книга", value.Form);
        Assert.AreEqual(1, value.Distance);
        WordReading reading = value.Readings.Single();
        Assert.AreEqual("книга", reading.Form);
        Assert.AreEqual("книга", reading.Lemma);
        Assert.AreEqual("Съществително:41,Женски", reading.PartOfSpeech);
        Assert.AreEqual(1.0, reading.Score);
        Assert.AreEqual(candidate.GetProperty("sourceId").GetString(), reading.Source);
        Assert.IsNull(reading.LegacyTypeId);
        Assert.IsNull(reading.LegacyBaseId);
        Assert.IsNull(reading.LegacyWordId);
    }

    [TestMethod]
    public void Segmentation_preserves_literal_cover_without_syntax_claim()
    {
        WordSegmentationCandidate value = session.SegmentWord("старитекниги", 5, 5)
            .Single(row => row.SegmentedText == "старите книги");
        Assert.AreEqual("старитекниги", value.OriginalText);
        Assert.AreEqual("старитекниги", value.NormalizedText);
        CollectionAssert.AreEqual(new[] { "старите", "книги" }, value.Segments.Select(row => row.Text).ToArray());
        Assert.AreEqual("exact-dawg-segmentation.v1", value.Source);
        Assert.IsFalse(value.IsSyntaxRanked);
        Assert.IsTrue(value.Segments.All(row => row.Readings.Count > 0 && row.MorphologyEvidence is null));
        Assert.AreEqual(0, session.SegmentWord("старитекниги", 5, 1).Count);
    }

    [TestMethod]
    public void Agreement_diagnostic_has_literal_rule_and_source_span()
    {
        const string text = "Аз го четем.";
        TextDiagnostic value = session.AnalyzeText(text).Diagnostics
            .Where(row => row.Code.StartsWith("pronoun_verb_agreement_", StringComparison.Ordinal)).Single();
        Assert.AreEqual("pronoun_verb_agreement_mismatch", value.Code);
        Assert.AreEqual(6, value.Start);
        Assert.AreEqual(5, value.Length);
        Assert.AreEqual("четем", text.Substring(value.Start, value.Length));
        Assert.AreEqual("pronoun_verb_agreement", value.RuleId);
        Assert.IsNull(value.ProjectedText);
    }

    [TestMethod]
    public void Article_diagnostics_preserve_candidate_suggestions_and_coverage_gaps()
    {
        const string text = "Големия човек е лекар. Писателя е човек. " +
            "Големият човек е лекар. Видях големия човек.";
        var diagnostics = session.AnalyzeText(text).Diagnostics;
        TextDiagnostic[] actual = diagnostics.Where(row => row.Code == "full_article_expected_for_subject_candidate").ToArray();
        CollectionAssert.AreEqual(new[] { 0, 23 }, actual.Select(row => row.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 7, 8 }, actual.Select(row => row.Length).ToArray());
        CollectionAssert.AreEqual(new[] { "Големият", "Писателят" }, actual.Select(row => row.ProjectedText).ToArray());
        CollectionAssert.AreEqual(new[] { "Големия", "Писателя" }, actual.Select(row => text.Substring(row.Start, row.Length)).ToArray());
        Assert.IsTrue(actual.All(row => row.RuleId == "grammar.full_article.copular_subject_exact.v1"));
        TextDiagnostic[] gaps = diagnostics.Where(row => row.Code == "full_short_article_check_not_ready").ToArray();
        CollectionAssert.AreEqual(new[] { 41, 71 }, gaps.Select(row => row.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 8, 7 }, gaps.Select(row => row.Length).ToArray());
        Assert.IsTrue(gaps.All(row => row.ProjectedText is null && row.RuleId == "grammar.full_short_article_morphology_coverage.v1"));
    }

    [TestMethod]
    public void Tractor_chain_is_a_graph_relation_for_an_already_known_word()
    {
        WordDerivation path = session.DeriveWords("трактор", 16).Single(value => value.Word == "трактористка");
        Assert.AreEqual("трактор", path.Base);
        Assert.AreEqual("трактористка", path.DerivedLemma);
        Assert.IsFalse(path.IsConditional);
        CollectionAssert.AreEqual(new[] { "accepted-lemma-derivational-suffix-ist.v0", "derivational_suffix.agent_feminine_ka.v1" }, path.RuleIds.ToArray());
        CollectionAssert.AreEqual(new[] { "трактор", "ист", "ка" }, path.Segments.Select(value => value.Surface).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 7, 10 }, path.Segments.Select(value => value.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 7, 3, 2 }, path.Segments.Select(value => value.Length).ToArray());
        CollectionAssert.AreEqual(new[] { "Съществително:41,Женски" }, path.FullTags!.ToArray());
        Assert.IsTrue(session.AnalyzeWord("трактористка", 256).IsKnown);
        WordDecomposition decomposition = session.DecomposeWord("трактористка");
        Assert.AreEqual("трактор", decomposition.Base);
        Assert.IsNull(decomposition.ProductiveGeneration);
    }

    [TestMethod]
    [DataRow("жълто-зелен", null)]
    [DataRow("жълто-зеления", "productive.adjective.type76.v0")]
    public void Known_color_forms_do_not_claim_a_coordinate_compound_path(string query, string? selectedRule)
    {
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.IsTrue(word.IsKnown);
        Assert.AreEqual(0, word.GraphCandidates.Count);
        AssertSearchFlags(word, false, false, false, false, false, false, false);
        WordDecomposition value = session.DecomposeWord(query);
        Assert.AreEqual(selectedRule, value.RuleId);
        Assert.AreEqual(selectedRule is not null, value.HasEvidence);
        Assert.IsNull(value.ProductiveGeneration);
    }

    [TestMethod]
    [DataRow("жълто--зелен", false, 50)]
    [DataRow("жълто-книга", true, 0)]
    public void Unsupported_color_relation_keeps_conditional_or_incomplete_evidence(string query, bool incomplete, int graphCount)
    {
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.IsFalse(word.IsKnown);
        Assert.AreEqual(0, word.Readings.Count);
        Assert.IsNull(word.BaseEvidence);
        Assert.AreEqual(graphCount, word.GraphCandidates.Count);
        Assert.IsTrue(word.GraphCandidates.All(value => value.IsConditional && !value.RuleIds.Contains("productive.adjective.coordinate_color.v1")));
        if (graphCount > 0)
        {
            WordDerivation added = word.GraphCandidates.Single(value => value.Base == query &&
                value.ConditionalRootProgramId == "productive.noun.feminine_type50_en.v0");
            Assert.AreEqual("Съществително:50,Женски", added.ConditionalRootBaseFullTag);
            Assert.AreEqual(2, added.ConditionalRootSupportCount);
            CollectionAssert.AreEqual(new[] { "песен", "суперпесен" }, added.ConditionalSupportingLemmas!.ToArray());
        }
        AssertSearchFlags(word, incomplete, false, false, false, false, false, false);
        Assert.IsFalse(session.DecomposeWord(query).HasEvidence);
    }

    private static void AssertSearchFlags(WordAnalysis word, params bool[] expected)
    {
        CollectionAssert.AreEqual(expected, new[] { word.GraphSearchIncomplete, word.GraphCandidatesTruncated,
            word.CandidateSearchLimited, word.CandidateEvidenceLimited, word.KnownBaseSearchLimited,
            word.BaseTypeSearchLimited, word.BaseTypeEvidenceLimited });
    }

    private static string Key(string form, string lemma, string? tag) => JsonSerializer.Serialize(new[] { form, lemma, tag });
}
