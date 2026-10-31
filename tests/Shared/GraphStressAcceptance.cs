using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Tests.Shared;

// Prospective canonical successor; historical GraphStressPackageTests keep their 825-byte authority.
internal static class GraphStressAcceptance
{
    internal static readonly JsonElement Root = Load();
    internal static string Mode => Environment.GetEnvironmentVariable("BGLANG_GRAPH_STRESS_PROFILE") switch
    {
        null or "Baseline" => "Baseline",
        "CurrentGraphStress" => "CurrentGraphStress",
        _ => throw new InvalidOperationException("Unknown explicit GraphStress acceptance profile.")
    };
    internal static bool Enabled => Mode == "CurrentGraphStress";
    internal static JsonElement Package => Root.GetProperty("packages").GetProperty(Mode);
    internal static string Manifest
    {
        get
        {
            string? path = Enabled ? Environment.GetEnvironmentVariable("BGLANG_GRAPH_STRESS_SUCCESSOR_MANIFEST") :
                Environment.GetEnvironmentVariable("BGLANG_GRAPH_STRESS_BASELINE_MANIFEST") ??
                Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST") ??
                Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST");
            Assert.IsFalse(string.IsNullOrWhiteSpace(path), "An explicit complete package is mandatory; this gate never skips.");
            Assert.IsTrue(Path.IsPathFullyQualified(path!));
            return Path.GetFullPath(path!);
        }
    }
    internal static string ManifestSha => Package.GetProperty("files").EnumerateArray()
        .Single(row => row.GetProperty("name").GetString() == "manifest.json").GetProperty("sha256").GetString()!;

    private static JsonElement Load()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "graph-stress-acceptance.v1.json"));
        Assert.AreEqual("5d8c0b0d97cfbabb2b00c67e328212173044e104c9be700d301ff8efa3917cad", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using JsonDocument document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }
    internal static void AssertBinding(string manifest)
    {
        string directory = Path.GetDirectoryName(manifest)!;
        var expected = Package.GetProperty("files").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(expected.Select(row => row.GetProperty("name").GetString()!).ToArray(),
            Directory.GetFileSystemEntries(directory).Select(path => Path.GetFileName(path)!).ToArray());
        foreach (JsonElement file in expected)
        {
            string path = Path.Combine(directory, file.GetProperty("name").GetString()!);
            Assert.AreEqual(file.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
            using FileStream stream = File.OpenRead(path);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(stream)), path);
        }
        using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(manifest));
        Assert.AreEqual(Package.GetProperty("packageId").GetString(), json.RootElement.GetProperty("packageId").GetString());
    }
    internal static BulgarianLanguageSession Open()
    {
        string manifest = Manifest; AssertBinding(manifest);
        return BulgarianLanguageSession.Open(manifest);
    }
    internal static void AssertAll(BulgarianLanguageSession session)
    {
        Assert.AreEqual(Package.GetProperty("packageId").GetString(), session.Provenance.PackageId);
        Assert.AreEqual(Enabled ? "bglang-word-formation:automatic-graph-stress-ist.v1" : "word-formation-source-v0", session.Provenance.WordFormationSource);
        Assert.IsEmpty(session.Lookup("колажист"), "The useful target must remain exact-unknown.");
        Assert.IsNotEmpty(session.Lookup("колаж").Where(row => row.Form == "колаж" && row.Lemma == "колаж" &&
            row.PartOfSpeech == "Съществително:7,Мъжки"));
        Assert.IsEmpty(session.Lookup("тереминист")); Assert.IsEmpty(session.Lookup("теремин"));
        AssertWord(session.AnalyzeWord("колажист", 128));
        const string text = "😀 колажист!";
        TextAnalysis analysis = session.AnalyzeText(text);
        TokenAnalysis token = analysis.Tokens.Single(row => row.Text == "колажист");
        Assert.AreEqual(3, token.Start); Assert.AreEqual(8, token.Length);
        Assert.AreEqual("колажист", text.Substring(token.Start, token.Length));
        AssertWord(token.Analysis!);
        Assert.AreEqual(text, analysis.Text, "Automatic graph evidence does not rewrite text.");
    }
    internal static void AssertNominal(TextAnalysis analysis, string surface, int expectedLength)
    {
        string text = "😀 " + surface + "!";
        Assert.AreEqual(text, analysis.Text);
        var indexed = analysis.Tokens.Select((token, index) => (token, index)).Single(row => row.token.Text == surface);
        Assert.AreEqual(3, indexed.token.Start); Assert.AreEqual(expectedLength, indexed.token.Length);
        Assert.AreEqual(surface, text.Substring(indexed.token.Start, indexed.token.Length));
        AssertWord(indexed.token.Analysis!, surface);
        Assert.IsNotNull(analysis.NominalConstructions); Assert.IsTrue(analysis.NominalConstructions.Available);
        Assert.IsNull(analysis.NominalConstructions.UnavailableReason);
        if (Enabled)
        {
            TextNominalPhrase phrase = analysis.NominalConstructions.Phrases.Single(row => row.HeadTokenIndex == indexed.index);
            Assert.AreEqual("NP", phrase.ConstructionKey); Assert.AreEqual(3, phrase.Start); Assert.AreEqual(expectedLength, phrase.Length);
            Assert.IsEmpty(phrase.ModifierTokenIndices); Assert.IsEmpty(phrase.ConnectorTokenIndices);
            foreach (var support in phrase.HeadSupports)
            {
                Assert.IsNull(support.Reading); Assert.IsNotNull(support.GraphReading);
                var reference = support.GraphReading.Value;
                Assert.AreEqual(indexed.index, reference.TokenIndex);
                var path = indexed.token.Analysis!.GraphCandidates[reference.GraphCandidateIndex];
                Assert.IsTrue((uint)reference.FullTagIndex < (uint)path.FullTags!.Count);
            }
        }
        // A local NP is structural/advisory; incomplete graph search may leave its agreement unassessed.
        Assert.IsFalse(analysis.Context?.Sentences.SelectMany(sentence => sentence.SelectedRoles)
            .Any(role => role.Role == TextContextRole.Subject) == true, "Nominal opt-in does not create sentence Subject proof.");
    }
    internal static void AssertWord(WordAnalysis word, string surface = "колажист")
    {
        Assert.AreEqual(surface, word.Word); Assert.IsFalse(word.IsKnown); Assert.IsEmpty(word.Readings);
        // The immutable Baseline has no accent-support graph lane; preserve that honest boundary.
        if (!Enabled && surface != "колажист") { Assert.IsNull(word.GraphStress); return; }
        const string edge = "transition|suffix|m:suffix:ист|state:noun:7|stem-transform:identity|state:noun:7a|accepted-lemma-derivational-suffix-ist.v0";
        var indexed = word.GraphCandidates.Select((path, index) => (path, index)).Single(row =>
            row.path.Word == "колажист" && row.path.Base == "колаж" && row.path.FromState == "state:noun:7" &&
            row.path.ToState == "state:noun:7a" && row.path.EdgeIds.SequenceEqual(new[] { edge }) &&
            row.path.FullTags?.Contains("Съществително:7a,Мъжки", StringComparer.Ordinal) == true);
        if (!Enabled) { Assert.IsNull(word.GraphStress); return; }
        Assert.IsNotNull(word.GraphStress); Assert.IsFalse(word.GraphStress.CompletePronunciation);
        if (surface == "колажи\u0301ст")
            Assert.AreEqual(new StressMark(2, 5, StressMarkKind.Acute), word.GraphStress.ObservedMarks.Single());
        Assert.AreEqual("profiled_anchor_only_secondary_stress_not_assessed", word.GraphStress.Coverage);
        var projected = word.GraphStress.Candidates.Single(row => row.CandidateIndex == indexed.index);
        Assert.AreEqual(StressStatus.Inferred, projected.Status);
        CollectionAssert.AreEqual(new[] { "accent-profile:ist:dominant-base.v1" }, projected.ProfileIds.ToArray());
        CollectionAssert.Contains(projected.PolicyEvidenceIds.ToArray(), "ev:accent:patseva:5.3.1.1:ist");
        var owners = projected.Readings.Where(row => row.RootReading.Form == "колаж" && row.RootReading.Lemma == "колаж" &&
            row.RootReading.PartOfSpeech == "Съществително:7,Мъжки" && row.FullTag == "Съществително:7a,Мъжки").ToArray();
        Assert.IsNotEmpty(owners);
        foreach (var row in owners)
        {
            Assert.AreEqual("legacy-base:2064768", row.OwnerEntryId); Assert.AreEqual(StressStatus.Inferred, row.Status);
            Assert.AreEqual(new StressMark(2, 5, StressMarkKind.Unspecified), row.Marks.Single());
            Assert.IsNull(row.AccentEvidenceSourceId, "A suffix policy does not become a root accent model.");
        }
    }
}
