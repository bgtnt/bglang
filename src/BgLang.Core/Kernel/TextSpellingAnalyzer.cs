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

    // Advisory agreement over existing adjacent exact rows; no new search or raw reranking.
    internal static TextSpellingEvidence? WithContext(TextAnalysis analysis,
        MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (analysis.Spelling is not { } spelling || morphology.UnavailableReason is not null) return analysis.Spelling;
        List<SpellingWordEvidence> words = [];
        foreach (SpellingWordEvidence word in spelling.Words)
        {
            ct.ThrowIfCancellationRequested();
            if (word.Search != SpellingSearchDisposition.Completed || word.Alternatives.Count == 0 ||
                word.TokenIndex < 0 || word.TokenIndex >= analysis.Tokens.Count)
            { words.Add(word); continue; }
            TokenAnalysis token = analysis.Tokens[word.TokenIndex];
            if (token.Kind != TokenKind.Word || token.Start != word.Start || token.Length != word.Length ||
                !StringComparer.Ordinal.Equals(token.Text, word.Text))
                throw new ArgumentException("Spelling evidence is not bound to its original Word occurrence.", nameof(analysis));
            var following = Context(word.TokenIndex + 1, true);
            var previous = Context(word.TokenIndex - 1, false);
            if (following is null && previous is null) { words.Add(word); continue; }
            List<SpellingContextCandidate> candidates = [];
            for (int a = 0; a < word.Alternatives.Count; a++)
            {
                ct.ThrowIfCancellationRequested();
                FuzzyMatch alternative = word.Alternatives[a];
                List<SpellingContextMatch> matches = [];
                List<SpellingPreviousModifierMatch>? previousMatches = previous is null ? null : [];
                bool measured = false, unmeasured = false;
                Measure(following, true);
                Measure(previous, false);
                SpellingContextAssessment assessment = matches.Count > 0 || previousMatches is { Count: > 0 }
                    ? SpellingContextAssessment.Supported : !measured || unmeasured
                        ? SpellingContextAssessment.Unassessed : SpellingContextAssessment.NoCompatiblePair;
                candidates.Add(new(a, assessment, Array.AsReadOnly(matches.ToArray()))
                {
                    PreviousModifierMatches = previousMatches is null ? null : Array.AsReadOnly(previousMatches.ToArray())
                });

                void Measure((int Index, MorphologyEvidenceProjection.Rows Rows, int[] Witnesses, bool Unmeasured)? context,
                    bool candidateIsModifier)
                {
                    if (context is not { } known) return;
                    unmeasured |= known.Unmeasured;
                    MorphologicalPartOfSpeech required = candidateIsModifier
                        ? MorphologicalPartOfSpeech.Adjective : MorphologicalPartOfSpeech.Noun;
                    for (int r = 0; r < alternative.Readings.Count; r++)
                    {
                        ct.ThrowIfCancellationRequested();
                        WordReading reading = alternative.Readings[r];
                        MorphologicalFeatures features = reading.Features;
                        if (features.PartOfSpeech != required) continue;
                        if (!StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Form),
                                BulgarianTextNormalizer.NormalizeKey(alternative.Form)) || !ContextEvidenceGuards.HasNominalAxes(features))
                        { unmeasured = true; continue; }
                        measured = true;
                        foreach (int n in known.Witnesses)
                        {
                            ct.ThrowIfCancellationRequested();
                            MorphologicalFeatures contextFeatures = known.Rows.Lanes[n].Features;
                            if (!(candidateIsModifier ? ContextEvidenceGuards.AttributeAgreement(features, contextFeatures)
                                : ContextEvidenceGuards.AttributeAgreement(contextFeatures, features))) continue;
                            if (candidateIsModifier) matches.Add(new(r, new(known.Index, n)));
                            else previousMatches!.Add(new(r, new(known.Index, n)));
                            break;
                        }
                    }
                }
            }
            words.Add(word with { ContextualCandidates = Array.AsReadOnly(candidates
                .OrderBy(candidate => word.Alternatives[candidate.AlternativeIndex].Distance)
                .ThenBy(candidate => candidate.Assessment == SpellingContextAssessment.Supported ? 0 : 1)
                .ThenBy(candidate => candidate.AlternativeIndex).ToArray()) });

            (int Index, MorphologyEvidenceProjection.Rows Rows, int[] Witnesses, bool Unmeasured)? Context(int index, bool followingNoun)
            {
                if (index < 0 || index >= analysis.Tokens.Count) return null;
                TokenAnalysis neighbor = analysis.Tokens[index];
                TokenAnalysis first = followingNoun ? token : neighbor, last = followingNoun ? neighbor : token;
                if (neighbor.Kind != TokenKind.Word || !morphology.ByToken.TryGetValue(index, out var rows) ||
                    rows.UsesGraph || rows.Raw.Length == 0 ||
                    !analysis.Sentences.Any(s => first.Start >= s.Start && (long)last.Start + last.Length <= (long)s.Start + s.Length) ||
                    !Whitespace(first.Start + first.Length, last.Start)) return null;
                ContextLanes.Category category = followingNoun ? ContextLanes.Category.Noun : ContextLanes.Category.Adjective;
                MorphologicalPartOfSpeech required = followingNoun ? MorphologicalPartOfSpeech.Noun : MorphologicalPartOfSpeech.Adjective;
                List<int> measured = [];
                bool unmeasured = false;
                for (int i = 0; i < rows.Lanes.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var row = rows.Lanes[i];
                    if (row.Category != category) continue;
                    if (row.Features.PartOfSpeech != required || !ContextEvidenceGuards.HasNominalAxes(row.Features))
                        unmeasured = true;
                    else measured.Add(i);
                }
                if (measured.Count == 0 && !unmeasured) return null;
                // One original exact witness per tuple; every raw row remains in its complete inventory.
                int[] witnesses = measured.GroupBy(i => (rows.Lanes[i].Features.Number,
                    Gender: rows.Lanes[i].Features.Number == MorphologicalNumber.Plural ? null : rows.Lanes[i].Features.Gender))
                    .Select(group => group.First()).ToArray();
                return (index, rows, witnesses, unmeasured);
            }
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
