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

    // Prospective advisory policy: avoid proposing replacement of only an identifier/OCR fragment.
    // Whitespace-separated numbers do not join a Word; existing whole-span OCR diagnostics still run.
    private static bool TouchesWordOrNumber(IReadOnlyList<TokenAnalysis> tokens, int index) =>
        index > 0 && tokens[index - 1].Kind is TokenKind.Word or TokenKind.Number &&
            tokens[index - 1].Start + tokens[index - 1].Length == tokens[index].Start ||
        index + 1 < tokens.Count && tokens[index + 1].Kind is TokenKind.Word or TokenKind.Number &&
            tokens[index].Start + tokens[index].Length == tokens[index + 1].Start;

}
