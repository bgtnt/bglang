using BgLang.Contracts;

namespace BgLang.Core;

internal static class TextMetricsAnalyzer
{
    // Inputs must come from the same completed AnalyzeText token/sentence pass.
    // Token.Text is the raw UTF-16 source slice; Token.Length includes attached marks.
    internal static TextMetrics Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(sentences);

        HashSet<string> rawTypes = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> normalizedTypes = new(StringComparer.Ordinal);
        int words = 0, punctuation = 0, repeatedExtras = 0, unknownWords = 0;
        long wordCharacters = 0;
        foreach (TokenAnalysis token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind == TokenKind.Punctuation) punctuation++;
            if (token.Kind != TokenKind.Word) continue;

            // Missing analysis is not evidence that a word is unknown.
            WordAnalysis analysis = token.Analysis ?? throw new ArgumentException(
                "Every word token must carry its existing word analysis.", nameof(tokens));
            words++;
            wordCharacters += token.Length;
            rawTypes.Add(token.Text);

            // Do not use Analysis.NormalizedWord: stress-support lookup may change that value.
            string normalized = BulgarianTextNormalizer.NormalizeKey(token.Text);
            if (normalized.Length > 0 && !normalizedTypes.Add(normalized)) repeatedExtras++;
            if (!analysis.IsKnown &&
                !BulgarianAbbreviationLexicon.TryNormalizeExact(token.Text.AsSpan(), out _))
                unknownWords++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new TextMetrics(
            tokens.Count,
            words,
            sentences.Count,
            (double)words / Math.Max(1, sentences.Count),
            words == 0 ? 0 : (double)wordCharacters / words,
            words == 0 ? 0 : (double)rawTypes.Count / words,
            punctuation,
            repeatedExtras,
            words == 0 ? 0 : (double)unknownWords / words);
    }
}

