using BgLang.Contracts;

namespace BgLang.Core;

// Uses completed word analyses and the shared request-local immutable fuzzy cache.
// Keep existing OCR/accent/unique-distance-one diagnostics separate and unchanged.
internal static class TextSpellingAnalyzer
{
    // Archive SuggestionOptions defaults; the current FindSimilar facade agrees.
    internal const int DefaultMaxDistance = 2;
    internal const int DefaultLimit = 10;
    private const int MaximumQueryLength = 256; // Existing current FindSimilar guard, not an owner cap.

    internal static TextSpellingEvidence Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        SpellingCandidateCache search,
        CancellationToken cancellationToken,
        int maxDistance = DefaultMaxDistance,
        int limit = DefaultLimit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(search);
        if (maxDistance is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(maxDistance));
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit));
        List<SpellingWordEvidence> words = [];
        for (int index = 0; index < tokens.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[index];
            if (token.Kind != TokenKind.Word) continue;
            string query = BulgarianTextNormalizer.NormalizeKey(token.Text);
            cancellationToken.ThrowIfCancellationRequested();
            SpellingSearchDisposition disposition;
            string? reason;
            IReadOnlyList<FuzzyMatch> alternatives = Array.Empty<FuzzyMatch>();
            if (token.Analysis is null)
                (disposition, reason) = (SpellingSearchDisposition.Unavailable, "word-analysis-unavailable");
            else if (token.Analysis.IsKnown)
                (disposition, reason) = (SpellingSearchDisposition.NotRequestedKnown, "exact-known");
            else if (TouchesWordOrNumber(tokens, index))
                (disposition, reason) = (SpellingSearchDisposition.NotRequestedPolicy, "adjacent-word-or-number");
            else if (query.Length == 0)
                (disposition, reason) = (SpellingSearchDisposition.Guarded, "empty-normalized-query");
            else if (query.Length > MaximumQueryLength)
                (disposition, reason) = (SpellingSearchDisposition.Guarded, "normalized-query-too-long");
            else
            {
                // Caller may bind the shared request cache. Cancellation is never caught or converted.
                alternatives = search.FindSimilarNormalized(query, maxDistance, limit, cancellationToken);
                (disposition, reason) = (SpellingSearchDisposition.Completed, null);
            }
            cancellationToken.ThrowIfCancellationRequested();
            words.Add(new(index, token.Text, token.Start, token.Length, query, disposition, reason, alternatives));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(maxDistance, limit, Array.AsReadOnly(words.ToArray()));
    }

    // Advisory modifier-to-following-noun evidence over existing snapshots; no new fuzzy search or raw reranking.
    internal static TextSpellingEvidence? WithContext(TextAnalysis analysis,
        MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (analysis.Spelling is not { } spelling || morphology.UnavailableReason is not null) return analysis.Spelling;
        List<SpellingWordEvidence> words = [];
        foreach (SpellingWordEvidence word in spelling.Words)
        {
            ct.ThrowIfCancellationRequested();
            int next = word.TokenIndex + 1;
            if (word.Search != SpellingSearchDisposition.Completed || word.Alternatives.Count == 0 ||
                word.TokenIndex < 0 || word.TokenIndex >= analysis.Tokens.Count || next >= analysis.Tokens.Count)
            { words.Add(word); continue; }
            TokenAnalysis token = analysis.Tokens[word.TokenIndex], noun = analysis.Tokens[next];
            if (token.Kind != TokenKind.Word || token.Start != word.Start || token.Length != word.Length ||
                !StringComparer.Ordinal.Equals(token.Text, word.Text))
                throw new ArgumentException("Spelling evidence is not bound to its original Word occurrence.", nameof(analysis));
            if (noun.Kind != TokenKind.Word || !morphology.ByToken.TryGetValue(next, out var rows) ||
                rows.UsesGraph || rows.Raw.Length == 0 ||
                !analysis.Sentences.Any(s => token.Start >= s.Start && (long)noun.Start + noun.Length <= (long)s.Start + s.Length) ||
                !Whitespace(token.Start + token.Length, noun.Start))
            { words.Add(word); continue; }
            List<int> measuredNouns = [];
            bool unmeasuredNoun = false;
            for (int i = 0; i < rows.Lanes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = rows.Lanes[i];
                if (row.Category != ContextLanes.Category.Noun) continue;
                if (row.Features.PartOfSpeech != MorphologicalPartOfSpeech.Noun || !ContextEvidenceGuards.HasNominalAxes(row.Features))
                    unmeasuredNoun = true;
                else measuredNouns.Add(i);
            }
            if (measuredNouns.Count == 0 && !unmeasuredNoun) { words.Add(word); continue; }
            // One original noun witness per typed tuple avoids a Cartesian output while retaining every raw row.
            int[] nounWitnesses = measuredNouns.GroupBy(i => (rows.Lanes[i].Features.Number,
                Gender: rows.Lanes[i].Features.Number == MorphologicalNumber.Plural ? null : rows.Lanes[i].Features.Gender))
                .Select(group => group.First()).ToArray();
            List<SpellingContextCandidate> candidates = [];
            for (int a = 0; a < word.Alternatives.Count; a++)
            {
                ct.ThrowIfCancellationRequested();
                FuzzyMatch alternative = word.Alternatives[a];
                List<SpellingContextMatch> matches = [];
                bool measured = false, unmeasured = unmeasuredNoun;
                for (int r = 0; r < alternative.Readings.Count; r++)
                {
                    ct.ThrowIfCancellationRequested();
                    WordReading reading = alternative.Readings[r];
                    MorphologicalFeatures features = reading.Features;
                    if (features.PartOfSpeech != MorphologicalPartOfSpeech.Adjective) continue;
                    if (!StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Form),
                            BulgarianTextNormalizer.NormalizeKey(alternative.Form)) || !ContextEvidenceGuards.HasNominalAxes(features))
                    { unmeasured = true; continue; }
                    measured = true;
                    foreach (int n in nounWitnesses)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!ContextEvidenceGuards.AttributeAgreement(features, rows.Lanes[n].Features)) continue;
                        matches.Add(new(r, new(next, n)));
                        break;
                    }
                }
                SpellingContextAssessment assessment = matches.Count > 0 ? SpellingContextAssessment.Supported :
                    !measured || unmeasured || measuredNouns.Count == 0 ? SpellingContextAssessment.Unassessed :
                        SpellingContextAssessment.NoCompatiblePair;
                candidates.Add(new(a, assessment, Array.AsReadOnly(matches.ToArray())));
            }
            words.Add(word with { ContextualCandidates = Array.AsReadOnly(candidates
                .OrderBy(candidate => word.Alternatives[candidate.AlternativeIndex].Distance)
                .ThenBy(candidate => candidate.Assessment == SpellingContextAssessment.Supported ? 0 : 1)
                .ThenBy(candidate => candidate.AlternativeIndex).ToArray()) });
        }
        ct.ThrowIfCancellationRequested();
        return spelling with { Words = Array.AsReadOnly(words.ToArray()) };

        bool Whitespace(int start, int end)
        {
            if (start < 0 || end > analysis.Text.Length || start >= end) return false;
            for (int i = start; i < end; i++) { ct.ThrowIfCancellationRequested(); if (!char.IsWhiteSpace(analysis.Text[i])) return false; }
            return true;
        }
    }

    // Prospective advisory policy: avoid proposing replacement of only an identifier/OCR fragment.
    // Whitespace-separated numbers do not join a Word; existing whole-span OCR diagnostics still run.
    private static bool TouchesWordOrNumber(IReadOnlyList<TokenAnalysis> tokens, int index) =>
        index > 0 && tokens[index - 1].Kind is TokenKind.Word or TokenKind.Number &&
            tokens[index - 1].Start + tokens[index - 1].Length == tokens[index].Start ||
        index + 1 < tokens.Count && tokens[index + 1].Kind is TokenKind.Word or TokenKind.Number &&
            tokens[index].Start + tokens[index].Length == tokens[index + 1].Start;

}
