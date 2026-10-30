using BgLang.Contracts;

namespace BgLang.Core;

internal static class PrepositionComplementAnalyzer
{
    private const string DiagnosticCode = "preposition_missing_complement";
    private const string RuleId = "preposition_complement";
    private const string CanonicalTag = "Предлог:192";
    private static readonly string[] ReviewedSurfaces =
        ["без", "в", "във", "до", "за", "зад", "към", "между", "на", "над", "около", "от", "под", "пред",
         "през", "при", "с", "след", "сред", "със"];
    internal static IReadOnlyList<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences,
        IRuntimeLexicon lexicon,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<TextDiagnostic> diagnostics = [];
        Dictionary<string, bool> authority = new(StringComparer.Ordinal);
        int first = 0;
        foreach (SentenceSpan sentence in sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sentenceEnd = sentence.Start + sentence.Length;
            while (first < tokens.Count && tokens[first].Start < sentence.Start)
                first++;
            int end = first;
            while (end < tokens.Count && tokens[end].Start < sentenceEnd)
                end++;
            for (int index = first; index < end; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TokenAnalysis token = tokens[index];
                string? surface = token.Kind == TokenKind.Word ? token.Analysis?.NormalizedWord : null;
                if (surface is null || !IsReviewed(surface) || IsQuotedWord(tokens, index, first, end) ||
                    !HasAuthority(surface, lexicon, authority, cancellationToken) ||
                    !IsMissingComplement(tokens, index, end, surface, lexicon, authority, cancellationToken))
                {
                    continue;
                }

                diagnostics.Add(new TextDiagnostic(DiagnosticCode, token.Start, token.Length, RuleId));
            }
            first = end;
        }

        return diagnostics;
    }
    private static bool IsMissingComplement(
        IReadOnlyList<TokenAnalysis> tokens,
        int index,
        int end,
        string sourceSurface,
        IRuntimeLexicon lexicon,
        IDictionary<string, bool> authority,
        CancellationToken cancellationToken)
    {
        if (++index >= end)
            return true;
        if (IsEllipsis(tokens, index, end))
            return false;
        TokenAnalysis next = tokens[index];
        if (next.Kind == TokenKind.Punctuation)
            return next.Text.Length != 1 || !SentenceBoundaryDetector.IsSentenceQuote(next.Text[0]);
        if (next.Kind != TokenKind.Word || next.Analysis is null)
            return false;
        string surface = next.Analysis.NormalizedWord;
        return IsLegacyChainSurface(sourceSurface) && IsLegacyChainSurface(surface) &&
            HasAuthority(surface, lexicon, authority, cancellationToken);
    }

    private static bool IsQuotedWord(IReadOnlyList<TokenAnalysis> tokens, int index, int first, int end)
    {
        if (index <= first || index + 1 >= end)
            return false;
        TokenAnalysis left = tokens[index - 1], word = tokens[index], right = tokens[index + 1];
        return QuotePair(left.Text, right.Text) && left.Start + left.Length == word.Start &&
            word.Start + word.Length == right.Start;
    }

    private static bool QuotePair(string left, string right) => (left, right) is
        ("„", "“") or ("«", "»") or ("“", "”") or ("‘", "’") or ("\"", "\"") or ("'", "'");

    private static bool IsEllipsis(IReadOnlyList<TokenAnalysis> tokens, int index, int end) =>
        tokens[index].Text == "…" || index + 2 < end && tokens[index].Text == "." &&
        tokens[index + 1].Text == "." && tokens[index + 2].Text == "." &&
        tokens[index].Start + tokens[index].Length == tokens[index + 1].Start &&
        tokens[index + 1].Start + tokens[index + 1].Length == tokens[index + 2].Start;

    private static bool HasAuthority(
        string surface,
        IRuntimeLexicon lexicon,
        IDictionary<string, bool> memo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memo.TryGetValue(surface, out bool cached))
            return cached;
        bool found = false;
        IReadOnlyList<WordReading> readings = lexicon.LookupExactNormalized(surface, int.MaxValue);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (WordReading reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? tag = reading.PartOfSpeech;
            if (tag?.StartsWith("Предлог", StringComparison.OrdinalIgnoreCase) != true)
                continue;
            if (tag != CanonicalTag || BulgarianTextNormalizer.NormalizeKey(reading.Form) != surface ||
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma) != surface)
            {
                return memo[surface] = false;
            }

            found = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return memo[surface] = found;
    }

    // Preserve the original chain contract; expanded surfaces receive terminal advice only.
    private static bool IsLegacyChainSurface(string surface) =>
        surface is "на" or "към" or "с" or "без" or "от" or "до" or "пред" or "след";

    private static bool IsReviewed(string surface) => ReviewedSurfaces.Contains(surface, StringComparer.Ordinal);
}
