using BgLang.Contracts;

namespace BgLang.Core;

internal static class PrepositionComplementAnalyzer
{
    private const string DiagnosticCode = "preposition_missing_complement";
    private const string RuleId = "preposition_complement";
    private const string CanonicalTag = "Предлог:192";
    private static readonly string[] ReviewedSurfaces = ["на", "към", "с", "без", "от", "до", "пред", "след"];
    internal static IReadOnlyList<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences,
        IRuntimeLexicon lexicon)
    {
        List<TextDiagnostic> diagnostics = [];
        Dictionary<string, bool> authority = new(StringComparer.Ordinal);
        int first = 0;
        foreach (SentenceSpan sentence in sentences)
        {
            int sentenceEnd = sentence.Start + sentence.Length;
            while (first < tokens.Count && tokens[first].Start < sentence.Start)
                first++;
            int end = first;
            while (end < tokens.Count && tokens[end].Start < sentenceEnd)
                end++;
            for (int index = first; index < end; index++)
            {
                TokenAnalysis token = tokens[index];
                string? surface = token.Kind == TokenKind.Word ? token.Analysis?.NormalizedWord : null;
                if (surface is null || !IsReviewed(surface) || !HasAuthority(surface, lexicon, authority) ||
                    !IsMissingComplement(tokens, index, end, lexicon, authority))
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
        IRuntimeLexicon lexicon,
        IDictionary<string, bool> authority)
    {
        if (++index >= end)
            return true;
        TokenAnalysis next = tokens[index];
        if (next.Kind == TokenKind.Punctuation)
            return !IsQuotedComplement(tokens, index, end);
        if (next.Kind != TokenKind.Word || next.Analysis is null)
            return false;
        string surface = next.Analysis.NormalizedWord;
        return IsReviewed(surface) && HasAuthority(surface, lexicon, authority);
    }

    private static bool IsQuotedComplement(IReadOnlyList<TokenAnalysis> tokens, int index, int end) =>
        index + 2 < end && tokens[index].Text == "„" && tokens[index + 1].Kind == TokenKind.Word &&
        tokens[index + 2].Text == "“" && tokens[index].Start + tokens[index].Length == tokens[index + 1].Start &&
        tokens[index + 1].Start + tokens[index + 1].Length == tokens[index + 2].Start;

    private static bool HasAuthority(
        string surface,
        IRuntimeLexicon lexicon,
        IDictionary<string, bool> memo)
    {
        if (memo.TryGetValue(surface, out bool cached))
            return cached;
        bool found = false;
        foreach (WordReading reading in lexicon.LookupExactNormalized(surface, int.MaxValue))
        {
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

        return memo[surface] = found;
    }

    private static bool IsReviewed(string surface) => ReviewedSurfaces.Contains(surface, StringComparer.Ordinal);
}
