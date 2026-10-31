using System.Collections.Immutable;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>One request-local view of exact rows or licensed exact-root graph cells, never synthetic exact readings.</summary>
internal static class MorphologyEvidenceProjection
{
    internal readonly record struct GraphCell(int CandidateIndex, int FullTagIndex);
    internal sealed record Rows(IReadOnlyList<ContextLanes.ReadingView> Lanes,
        ImmutableArray<WordReading> Raw, ImmutableArray<SyntaxRoleCalculator.Projection> Syntax,
        IReadOnlyList<string> Identities, string Canonical, IReadOnlyList<GraphCell> Graph,
        string? GraphUnavailableReason = null)
    {
        internal bool UsesGraph => Graph.Count > 0;
        internal int Count => UsesGraph ? Graph.Count : Raw.Length;
    }
    internal sealed record View(string? UnavailableReason, IReadOnlyDictionary<int, Rows> ByToken,
        IReadOnlyList<(int Index, Rows Rows)> OrderedTokens);

    internal static View Create(TextAnalysis analysis, CancellationToken ct)
    {
        if (analysis.ReadingInventory is not { } inventory) return new("reading-inventory-unavailable", new Dictionary<int, Rows>(), []);
        var occurrences = inventory.Occurrences.ToDictionary(x => x.TokenIndex);
        var sets = new Dictionary<int, Rows>();
        var graphSets = new Dictionary<WordAnalysis, Rows>(ReferenceEqualityComparer.Instance);
        var byToken = new Dictionary<int, Rows>();
        var ordered = new List<(int, Rows)>();
        for (int i = 0; i < analysis.Tokens.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[i];
            if (token.Kind != TokenKind.Word) continue;
            if (!occurrences.TryGetValue(i, out TextReadingOccurrence occurrence))
                return new("word-reading-occurrence-missing", byToken, ordered);
            if (occurrence.Start != token.Start || occurrence.Length != token.Length ||
                !StringComparer.Ordinal.Equals(occurrence.Text, token.Text) || token.Start < 0 || token.Length < 1 ||
                (long)token.Start + token.Length > analysis.Text.Length ||
                !analysis.Text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text))
                throw new ArgumentException("Context occurrence is not the original Word span.", nameof(analysis));
            if (!sets.TryGetValue(occurrence.ReadingSetIndex, out Rows? rows))
            {
                TextReadingSet set = inventory.Sets[occurrence.ReadingSetIndex];
                string canonical = set.SupportReadings is { Count: > 0 } ? set.SupportKey! : set.CanonicalKey;
                if (string.IsNullOrWhiteSpace(canonical)) return new("word-canonical-key-empty", byToken, ordered);
                List<ContextReadingProjection> projections = [];
                foreach (TextReadingSnapshot snapshot in set.ResolvedReadings)
                {
                    ct.ThrowIfCancellationRequested();
                    projections.Add(ContextReadingProjection.Create(snapshot, canonical));
                }
                rows = new(Array.AsReadOnly(projections.Select(x => x.Lane).ToArray()),
                    set.ResolvedReadings.Select(x => x.Raw).ToImmutableArray(),
                    projections.Select(x => x.Syntax).ToImmutableArray(),
                    Array.AsReadOnly(projections.Select(x => x.Identity).ToArray()), canonical, []);
                sets.Add(occurrence.ReadingSetIndex, rows);
            }
            if (rows.Raw.Length == 0 && token.Analysis is { } word &&
                (word.GraphCandidates.Count > 0 || word.GraphSearchIncomplete || word.GraphCandidatesTruncated))
            {
                if (!graphSets.TryGetValue(word, out Rows? graph))
                {
                    graph = GraphRows(token, rows, word, ct);
                    graphSets.Add(word, graph);
                }
                rows = graph;
            }
            byToken.Add(i, rows);
            ordered.Add((i, rows));
        }
        if (occurrences.Count != byToken.Count)
            throw new ArgumentException("Inventory contains a non-Word or absent occurrence.", nameof(analysis));
        return new(null, byToken, ordered);
    }

    private static Rows GraphRows(TokenAnalysis token, Rows exact, WordAnalysis word, CancellationToken ct)
    {
        RequireWordBinding(token, word);
        if (word.Readings.Count != 0) throw new ArgumentException("Graph view cannot replace nonempty exact readings.");
        if (word.GraphSearchIncomplete) return exact with { GraphUnavailableReason = "graph_search_incomplete" };
        if (!word.GraphExactRootCandidatesComplete) return exact with
        {
            GraphUnavailableReason = word.GraphCandidatesTruncated ? "graph_candidates_truncated" : "exact_root_graph_completeness_unavailable"
        };
        List<ContextReadingProjection> projections = [];
        List<GraphCell> cells = [];
        for (int candidateIndex = 0; candidateIndex < word.GraphCandidates.Count; candidateIndex++)
        {
            ct.ThrowIfCancellationRequested();
            WordDerivation path = word.GraphCandidates[candidateIndex];
            if (path.IsConditional) continue;
            RequirePathWord(word, path);
            if (path.UnmetExactSelfReadingRequirements is { Count: > 0 } ||
                HasConditionalRootEvidence(path) || string.IsNullOrWhiteSpace(path.DerivedLemma) ||
                path.FullTags is not { Count: > 0 })
                return exact with { GraphUnavailableReason = "graph_morphology_support_unavailable" };
            IReadOnlyList<MorphologicalFeatures> features = path.Features;
            for (int tagIndex = 0; tagIndex < path.FullTags.Count; tagIndex++)
            {
                ct.ThrowIfCancellationRequested();
                string tag = path.FullTags[tagIndex];
                if (string.IsNullOrWhiteSpace(tag) || !StringComparer.Ordinal.Equals(features[tagIndex].RawTag, tag))
                    throw new ArgumentException("Graph tag/features are not bound to the original licensed cell.");
                cells.Add(new(candidateIndex, tagIndex));
                projections.Add(ContextReadingProjection.CreateGraph(path, candidateIndex, tagIndex, features[tagIndex], exact.Canonical));
            }
        }
        if (cells.Count == 0) return exact with { GraphUnavailableReason = "no_admissible_exact_root_graph_morphology" };
        return new(Array.AsReadOnly(projections.Select(x => x.Lane).ToArray()), [],
            projections.Select(x => x.Syntax).ToImmutableArray(),
            Array.AsReadOnly(projections.Select(x => x.Identity).ToArray()), exact.Canonical, Array.AsReadOnly(cells.ToArray()));
    }

    internal static MorphologicalFeatures RequireGraphSupport(TokenAnalysis token, TextGraphReadingReference reference,
        int owner, int exactCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (reference.TokenIndex != owner || exactCount != 0 || token.Analysis is not { } word)
            throw new ArgumentException("Graph support is not an exact-empty original Word occurrence.");
        RequireWordBinding(token, word);
        if (word.GraphSearchIncomplete || !word.GraphExactRootCandidatesComplete ||
            (uint)reference.GraphCandidateIndex >= (uint)word.GraphCandidates.Count)
            throw new ArgumentException("Graph support is outside the complete exact-root scope.");
        WordDerivation path = word.GraphCandidates[reference.GraphCandidateIndex];
        RequirePathWord(word, path);
        if (path.IsConditional || path.UnmetExactSelfReadingRequirements is { Count: > 0 } ||
            HasConditionalRootEvidence(path) || string.IsNullOrWhiteSpace(path.DerivedLemma) ||
            path.FullTags is null || (uint)reference.FullTagIndex >= (uint)path.FullTags.Count)
            throw new ArgumentException("Graph support is not a licensed unconditional morphology cell.");
        string tag = path.FullTags[reference.FullTagIndex];
        MorphologicalFeatures features = MorphologicalFeatures.FromTag(tag);
        if (string.IsNullOrWhiteSpace(tag) || features.HasConflictingFeatures || features.HasUnrecognizedFeatures ||
            !StringComparer.Ordinal.Equals(features.RawTag, tag))
            throw new ArgumentException("Graph support tag/features binding is invalid.");
        return features;
    }

    private static bool HasConditionalRootEvidence(WordDerivation path) =>
        path.ConditionalRootProgramId is not null || path.ConditionalRootBaseFullTag is not null ||
        path.ConditionalRootSupportCount is not null || path.ConditionalSupportingLemmas is not null;

    private static void RequireWordBinding(TokenAnalysis token, WordAnalysis word)
    {
        string normalized = BulgarianTextNormalizer.NormalizeKey(token.Text);
        if (token.Kind != TokenKind.Word || !StringComparer.Ordinal.Equals(normalized, word.NormalizedWord) ||
            !StringComparer.Ordinal.Equals(normalized, BulgarianTextNormalizer.NormalizeKey(word.Word)))
            throw new ArgumentException("Graph analysis does not belong to the original Word occurrence.");
    }
    internal static void RequirePathWord(WordAnalysis word, WordDerivation path)
    {
        string normalizedPath = BulgarianTextNormalizer.NormalizeKey(path.Word);
        bool supportedStressVariant = BulgarianStressLookup.TryGetSupportKey(word.Word, word.NormalizedWord, out string support) &&
            StringComparer.Ordinal.Equals(support, normalizedPath);
        if (!StringComparer.Ordinal.Equals(word.NormalizedWord, normalizedPath) && !supportedStressVariant)
            throw new ArgumentException("Graph path belongs to a foreign normalized word.");
    }
}
