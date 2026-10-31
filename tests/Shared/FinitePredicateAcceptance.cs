using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Tests.Shared;

// Permanent assertion port of the independently reviewed finite-predicate candidate; no parallel runner.
internal static class FinitePredicateAcceptance
{
    internal const string Code = "unseparated_finite_predications";
    internal static readonly JsonElement Gold = Load();
    private static readonly string[] ReadingKinds = ["finite", "imperative", "participle", "converb", "unmeasured_verb", "other"];
    private static readonly string[] CoverageKinds = ["local_link_candidate", "isolated_finite_candidate", "uncovered", "competing_alternative", "conflict_support"];
    private static readonly string[] LinkKinds = ["comma_separation", "coordinating_conjunction", "da_boundary", "coordinated_da_boundary", "auxiliary_participle", "future_marker"];

    private static JsonElement Load()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "finite-predicate-links-independent.v1.json"));
        Assert.AreEqual("6a4c364ac53c7aba743ee1196110ad46c11b1a45eba547d5580ab9d4493076f3", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement.Clone();
        Assert.AreEqual("bglang.finite-predicate-links.regressions.v1", root.GetProperty("schema").GetString());
        JsonElement[] cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Assert.AreEqual(15, cases.Length); Assert.AreEqual(15, cases.Select(row => row.GetProperty("id").GetString()).Distinct().Count());
        return root;
    }

    internal static JsonElement Case(string id) => Gold.GetProperty("cases").EnumerateArray()
        .Single(row => row.GetProperty("id").GetString() == id);

    internal static BulgarianLanguageSession Open()
    {
        BulgarianLanguageSession session = GraphStressAcceptance.Open();
        try
        {
            Assert.AreEqual(GraphStressAcceptance.Package.GetProperty("packageId").GetString(), session.Provenance.PackageId);
            Assert.AreEqual("bglang-neutral-runtime-package.v2", session.Provenance.SchemaVersion);
            Assert.AreEqual(GraphStressAcceptance.ManifestSha, session.Provenance.ManifestSha256.ToLowerInvariant());
            Assert.IsNotNull(session.Provenance.AnnotationsSha256);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    internal static TextAnalysis Analyze(BulgarianLanguageSession session, string text, TextContextOptions options = default) =>
        session.AnalyzeText(text, false, options, CancellationToken.None);

    internal static void AssertIndependent(BulgarianLanguageSession session, string id, string text, string expected, TextAnalysis actual)
    {
        JsonElement source = Case(id);
        Assert.AreEqual(source.GetProperty("text").GetString(), text);
        Assert.AreEqual(expected, source.GetProperty("expected").GetString());
        string reportHash = JsonSerializer.Serialize(actual.PredicateLinks);
        Assert.IsTrue(actual.Text == text, "Original text changed.");
        ConserveInventory(actual);
        TextDiagnostic[] diagnostics = actual.Diagnostics.Where(diagnostic => diagnostic.Code == Code).ToArray();
        Assert.IsTrue(diagnostics.Length == (expected == "P" ? 1 : 0),
            $"Expected {expected}; observed {diagnostics.Length} new warnings. Uncovered does not pass a positive delivery case.");
        foreach (TextDiagnostic diagnostic in diagnostics)
        {
            Assert.IsTrue(diagnostic.ProjectedText is null, "New warning must not propose an automatic rewrite.");
            Assert.IsTrue(diagnostic.Start >= 0 && diagnostic.Length > 0 && diagnostic.Start + diagnostic.Length <= text.Length,
                "Diagnostic span does not address original UTF16 text.");
            Assert.IsTrue(actual.PredicateLinks!.Sentences.Any(sentence => sentence.ConflictScopeComplete),
                "A warning requires a positively completed narrow conflict scope.");
            Assert.IsTrue(actual.PredicateLinks!.Sentences.SelectMany(sentence => sentence.Occurrences)
                .SelectMany(occurrence => occurrence.Readings).Any(reading => reading.Coverage == "conflict_support"),
                "A delivered warning needs retained conflict-support rows.");
        }
        if (expected == "U")
            Assert.IsTrue(actual.PredicateLinks!.Sentences.Any(sentence => !sentence.ConflictScopeComplete &&
                (sentence.UncoveredReasons.Count > 0 || sentence.Occurrences.SelectMany(occurrence => occurrence.Readings)
                    .Any(reading => reading.Coverage is "uncovered" or "competing_alternative"))),
                "An uncertain control must retain explicit incompleteness or competing alternatives.");
        switch (id)
        {
            case "FP03": RequireLink(actual, "coordinating_conjunction", "и"); break;
            case "FP04": RequireLink(actual, "comma_separation", ","); break;
            case "FP05": RequireLink(actual, "da_boundary", "да"); break;
            case "FP06":
                RequireLink(actual, "da_boundary", "да");
                RequireLink(actual, "coordinated_da_boundary", "и", "да");
                break;
            case "FP07":
                RequireLink(actual, "auxiliary_participle");
                RequireLink(actual, "future_marker");
                break;
            case "FP12":
                TextAnalysis grouped = Analyze(session, text, new() { IncludeNominalConstructions = true });
                ConserveInventory(grouped);
                Assert.IsTrue(JsonSerializer.Serialize(grouped.PredicateLinks) == reportHash,
                    "Nominal grouping changed retained predicate reading/link evidence.");
                Assert.IsTrue(!grouped.Diagnostics.Any(diagnostic => diagnostic.Code == Code),
                    "Nominal grouping created a categorical warning in an uncertain control.");
                break;
        }
        if (expected == "P")
        {
            JsonElement span = Gold.GetProperty("warningSpans").GetProperty(id);
            TextDiagnostic warning = actual.Diagnostics.Single(row => row.Code == Code);
            Assert.AreEqual(span.GetProperty("start").GetInt32(), warning.Start);
            Assert.AreEqual(span.GetProperty("length").GetInt32(), warning.Length);
        }
    }

    internal static void ConserveInventory(TextAnalysis actual)
    {
        TextPredicateLinkEvidence report = actual.PredicateLinks ?? throw new InvalidOperationException("Always-populated PredicateLinks missing.");
        Assert.IsTrue(report.Available && report.UnavailableReason is null, "Complete package report is unavailable.");
        Assert.IsTrue(!string.IsNullOrWhiteSpace(report.Version), "Report version missing.");
        TextReadingInventory inventory = actual.ReadingInventory ?? throw new InvalidOperationException("Complete inventory missing.");
        Assert.IsTrue(report.Sentences.Count == actual.Sentences.Count, "Source sentence count changed.");
        var seenTokens = new HashSet<int>();
        for (int sentenceIndex = 0; sentenceIndex < report.Sentences.Count; sentenceIndex++)
        {
            TextPredicateSentence sentence = report.Sentences[sentenceIndex];
            SentenceSpan source = actual.Sentences[sentenceIndex];
            Assert.IsTrue((sentence.Start, sentence.Length) == (source.Start, source.Length), "Original sentence span changed.");
            foreach (TextPredicateOccurrence occurrence in sentence.Occurrences)
            {
                Assert.IsTrue(seenTokens.Add(occurrence.TokenIndex), "A predicate occurrence was emitted more than once.");
                TokenAnalysis token = ResolveToken(actual, occurrence.TokenIndex);
                Assert.IsTrue((occurrence.Start, occurrence.Length) == (token.Start, token.Length), "Occurrence UTF16 span changed.");
                Assert.IsTrue(occurrence.Start >= sentence.Start && occurrence.Start + occurrence.Length <= sentence.Start + sentence.Length,
                    "Occurrence crosses the original sentence boundary.");
                IReadOnlyList<TextReadingSnapshot> rows = CompleteRows(actual, occurrence.TokenIndex);
                TextReadingReference[] exact = occurrence.Readings.Where(reading => reading.Reading.HasValue)
                    .Select(reading => reading.Reading!.Value).ToArray();
                Assert.IsTrue(exact.Length == rows.Count, "An original exact row was lost or duplicated, including competing nonverb rows.");
                Assert.IsTrue(exact.Distinct().Count() == exact.Length, "Duplicate exact reading reference.");
                Assert.IsTrue(exact.All(reference => reference.TokenIndex == occurrence.TokenIndex) &&
                    exact.Select(reference => reference.CompleteReadingIndex).SequenceEqual(Enumerable.Range(0, rows.Count)),
                    "Original complete exact reading indices or ordering changed.");
                var graphRefs = new HashSet<TextGraphReadingReference>();
                foreach (TextPredicateReading reading in occurrence.Readings)
                {
                    Assert.IsTrue(ReadingKinds.Contains(reading.Kind), "Unknown reading kind.");
                    Assert.IsTrue(CoverageKinds.Contains(reading.Coverage), "Unknown reading coverage.");
                    Assert.IsTrue(reading.Reading.HasValue ^ reading.GraphReading.HasValue, "A report row must retain exactly one original provenance route.");
                    if (reading.Reading is { } reference) ResolveExact(actual, reference);
                    if (reading.GraphReading is { } graph)
                    {
                        Assert.IsTrue(graph.TokenIndex == occurrence.TokenIndex && graphRefs.Add(graph), "Graph reference moved or duplicated.");
                        ResolveGraph(actual, graph);
                    }
                }
            }
            foreach (TextPredicateLink link in sentence.Links)
            {
                Assert.IsTrue(LinkKinds.Contains(link.Kind), "Unknown local link kind.");
                CheckEndpoint(actual, sentence, link.Left);
                CheckEndpoint(actual, sentence, link.Right);
                Assert.IsTrue(link.Left.TokenIndex < link.Right.TokenIndex, "Local link order does not follow original source tokens.");
                foreach (TextSyntaxSupport marker in link.Markers)
                {
                    TokenAnalysis token = ResolveToken(actual, marker.TokenIndex, requireWord: false);
                    Assert.IsTrue(token.Start >= sentence.Start && token.Start + token.Length <= sentence.Start + sentence.Length,
                        "Link marker crosses a sentence boundary.");
                    foreach (TextReadingReference reference in marker.Readings) ResolveExact(actual, reference);
                    foreach (TextGraphReadingReference graph in marker.GraphReadings ?? []) ResolveGraph(actual, graph);
                }
            }
        }
        foreach (TextReadingOccurrence occurrence in inventory.Occurrences)
            if (inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb))
                Assert.IsTrue(seenTokens.Contains(occurrence.TokenIndex), "An acquired verb reading disappeared from the predicate inventory.");
    }

    private static void CheckEndpoint(TextAnalysis actual, TextPredicateSentence sentence, TextSyntaxSupport support)
    {
        Assert.IsTrue(support.Readings.Count + (support.GraphReadings?.Count ?? 0) == 1,
            "Each link endpoint must cite one coherent actual reading, without feature union.");
        TextPredicateOccurrence occurrence = sentence.Occurrences.Single(occurrence => occurrence.TokenIndex == support.TokenIndex);
        foreach (TextReadingReference reference in support.Readings)
        {
            Assert.IsTrue(reference.TokenIndex == support.TokenIndex && occurrence.Readings.Any(reading => reading.Reading == reference),
                "Link exact endpoint does not address a retained original row.");
            ResolveExact(actual, reference);
        }
        foreach (TextGraphReadingReference graph in support.GraphReadings ?? [])
        {
            Assert.IsTrue(graph.TokenIndex == support.TokenIndex && occurrence.Readings.Any(reading => reading.GraphReading == graph),
                "Link graph endpoint does not address a retained original row.");
            ResolveGraph(actual, graph);
        }
    }

    private static void RequireLink(TextAnalysis actual, string kind, params string[] markers)
    {
        Assert.IsTrue(actual.PredicateLinks!.Sentences.SelectMany(sentence => sentence.Links).Any(link => link.Kind == kind &&
            markers.All(marker => link.Markers.Any(support => actual.Tokens[support.TokenIndex].Text.Equals(marker, StringComparison.OrdinalIgnoreCase)))),
            $"Required local {kind} evidence with original marker(s) was not retained.");
    }

    private static TokenAnalysis ResolveToken(TextAnalysis actual, int tokenIndex, bool requireWord = true)
    {
        Assert.IsTrue((uint)tokenIndex < (uint)actual.Tokens.Count, "Token index out of range.");
        TokenAnalysis token = actual.Tokens[tokenIndex];
        Assert.IsTrue(!requireWord || token.Kind == TokenKind.Word, "Reading occurrence is not an original Word token.");
        Assert.IsTrue(token.Text == actual.Text.Substring(token.Start, token.Length), "Token no longer addresses original UTF16 text.");
        return token;
    }

    private static IReadOnlyList<TextReadingSnapshot> CompleteRows(TextAnalysis actual, int tokenIndex)
    {
        ResolveToken(actual, tokenIndex);
        TextReadingInventory inventory = actual.ReadingInventory!;
        TextReadingOccurrence occurrence = inventory.Occurrences.Single(occurrence => occurrence.TokenIndex == tokenIndex);
        return inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
    }

    private static void ResolveExact(TextAnalysis actual, TextReadingReference reference) =>
        Assert.IsTrue((uint)reference.CompleteReadingIndex < (uint)CompleteRows(actual, reference.TokenIndex).Count, "Exact reference out of range.");

    private static void ResolveGraph(TextAnalysis actual, TextGraphReadingReference reference)
    {
        WordAnalysis word = ResolveToken(actual, reference.TokenIndex).Analysis!;
        Assert.IsTrue((uint)reference.GraphCandidateIndex < (uint)word.GraphCandidates.Count, "Graph candidate reference out of range.");
        WordDerivation path = word.GraphCandidates[reference.GraphCandidateIndex];
        Assert.IsTrue(path.FullTags is not null && (uint)reference.FullTagIndex < (uint)path.FullTags.Count,
            "Graph full-tag reference out of range.");
        Assert.IsTrue(!path.IsConditional && path.DerivedLemma is not null &&
            path.UnmetExactSelfReadingRequirements is null or { Count: 0 }, "Conditional or unlicensed graph row was promoted.");
    }

}
