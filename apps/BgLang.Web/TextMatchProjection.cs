using BgLang.Contracts;

namespace BgLang.Web;

/// <summary>Joins public lemma evidence over source-aligned tokens; contains no morphology rules.</summary>
internal static class TextMatchProjection
{
    internal static TextMatchResult Find(WordAnalysis query, TextAnalysis text)
    {
        LemmaEvidence[] queryEvidence = Lemmas(query).ToArray();
        var queryGroups = queryEvidence.ToLookup(evidence => evidence.Lemma, StringComparer.Ordinal);
        bool queryAmbiguous = HasAlternatives(queryEvidence);
        List<TextMatch> matches = [];
        for (int index = 0; index < text.Tokens.Count; index++)
        {
            TokenAnalysis token = text.Tokens[index];
            if (token.Kind != TokenKind.Word || token.Analysis is not { } analysis) continue;
            if (token.Start < 0 || token.Length < 1 || token.Start > text.Text.Length - token.Length ||
                !text.Text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text.AsSpan()))
                throw new InvalidOperationException("Public token offsets do not match the original UTF-16 text.");
            bool exact = string.Equals(token.Text, query.Word, StringComparison.Ordinal);
            LemmaEvidence[] tokenEvidence = Lemmas(analysis).ToArray();
            List<LemmaMatch> stored = [];
            List<LemmaMatch> hypotheses = [];
            foreach (var group in tokenEvidence.ToLookup(evidence => evidence.Lemma, StringComparer.Ordinal))
            {
                LemmaEvidence[] queryGroup = queryGroups[group.Key].ToArray();
                if (queryGroup.Length == 0) continue;
                LemmaEvidence[] tokenGroup = group.ToArray();
                LemmaEvidence[] storedQuery = queryGroup.Where(IsStored).ToArray();
                LemmaEvidence[] storedToken = tokenGroup.Where(IsStored).ToArray();
                if (storedQuery.Length > 0 && storedToken.Length > 0)
                    stored.Add(new(group.Key, storedQuery, storedToken));
                if (queryGroup.Any(evidence => !IsStored(evidence)) || tokenGroup.Any(evidence => !IsStored(evidence)))
                    hypotheses.Add(new(group.Key, queryGroup, tokenGroup));
            }
            if (!exact && stored.Count == 0 && hypotheses.Count == 0) continue;
            matches.Add(new(index, token.Start, token.Length, token.Text, exact, stored, hypotheses,
                queryAmbiguous || HasAlternatives(tokenEvidence), !analysis.IsKnown,
                hypotheses.Any(match => match.QueryEvidence.Any(evidence => evidence.IsConditional) ||
                    match.TokenEvidence.Any(evidence => evidence.IsConditional)),
                ApiOperations.HasLimitFlag(query) || ApiOperations.HasLimitFlag(analysis)));
        }
        return new(query.Word, query, text, queryEvidence, queryAmbiguous, matches);
    }

    private static bool IsStored(LemmaEvidence evidence) => evidence.Kind == LemmaEvidenceKind.StoredReading;

    private static bool HasAlternatives(IEnumerable<LemmaEvidence> evidence) =>
        evidence.SelectMany(item => item.Tags.Count == 0
                ? new[] { (item.Lemma, item.Kind, item.IsConditional, Tag: "") }
                : item.Tags.Select(tag => (item.Lemma, item.Kind, item.IsConditional, Tag: tag)))
            .Distinct().Take(2).Count() > 1;

    private static IEnumerable<LemmaEvidence> Lemmas(WordAnalysis word)
    {
        List<LemmaEvidence> rows = [];
        foreach (WordReading reading in word.Readings)
            Add(new(reading.Lemma, LemmaEvidenceKind.StoredReading, false,
                reading.PartOfSpeech is null ? [] : [reading.PartOfSpeech], reading.Source));
        foreach (LemmaEvidence candidate in Candidates(word.KnownBaseCandidates, LemmaEvidenceKind.KnownBaseCandidate)) Add(candidate);
        foreach (LemmaEvidence candidate in Candidates(word.Candidates, LemmaEvidenceKind.MorphologyCandidate)) Add(candidate);
        foreach (LemmaEvidence candidate in Candidates(word.BaseTypeCandidates, LemmaEvidenceKind.BaseTypeCandidate)) Add(candidate);
        foreach (WordDerivation path in word.GraphCandidates)
            // Root Base and donor lemmas must never stand in for an absent DerivedLemma.
            if (path.DerivedLemma is { Length: > 0 } lemma)
                Add(new(lemma, LemmaEvidenceKind.GraphPath, path.IsConditional, path.FullTags ?? [], path.Source));
        return rows;

        void Add(LemmaEvidence row)
        {
            if (!rows.Any(existing => existing.Lemma == row.Lemma && existing.Kind == row.Kind &&
                existing.IsConditional == row.IsConditional && existing.Source == row.Source &&
                existing.Tags.SequenceEqual(row.Tags, StringComparer.Ordinal))) rows.Add(row);
        }
    }

    private static IEnumerable<LemmaEvidence> Candidates(IReadOnlyList<MorphologyCandidate> candidates, LemmaEvidenceKind kind)
    {
        foreach (MorphologyCandidate candidate in candidates)
        {
            yield return new(candidate.Lemma, kind, false, [candidate.FullTag], candidate.Source);
            foreach (MorphologyCandidate shorter in candidate.ShorterEndingCandidates)
                yield return new(shorter.Lemma, kind, false, [shorter.FullTag], shorter.Source);
        }
    }
}

internal enum LemmaEvidenceKind { StoredReading, GraphPath, MorphologyCandidate, KnownBaseCandidate, BaseTypeCandidate }
internal sealed record LemmaEvidence(string Lemma, LemmaEvidenceKind Kind, bool IsConditional,
    IReadOnlyList<string> Tags, string Source);
internal sealed record LemmaMatch(string Lemma, IReadOnlyList<LemmaEvidence> QueryEvidence,
    IReadOnlyList<LemmaEvidence> TokenEvidence);
internal sealed record TextMatch(int TokenIndex, int Start, int Length, string Text, bool ExactMatch,
    IReadOnlyList<LemmaMatch> StoredLemmaMatches, IReadOnlyList<LemmaMatch> EvidenceLemmaMatches,
    bool Ambiguous, bool Unknown, bool Conditional, bool Incomplete);
internal sealed record TextMatchResult(string Query, WordAnalysis QueryAnalysis, TextAnalysis TextAnalysis,
    IReadOnlyList<LemmaEvidence> QueryLemmaEvidence, bool QueryAmbiguous, IReadOnlyList<TextMatch> Matches);
