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

    private static string Key(string form, string lemma, string? tag) => JsonSerializer.Serialize(new[] { form, lemma, tag });
}
