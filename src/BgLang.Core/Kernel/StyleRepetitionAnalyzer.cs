using BgLang.Contracts;

namespace BgLang.Core;

internal static class StyleRepetitionAnalyzer
{
    private const string DiagnosticCode = "style_repetition_evidence";
    private const string RuleId = "style.normalized_exact_repetition.v1";

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        string text,
        IReadOnlyList<TokenAnalysis> tokens)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (TokenAnalysis token in tokens)
        {
            if (token.Kind != TokenKind.Word ||
                token.Analysis is not { IsKnown: true } analysis ||
                BulgarianAbbreviationLexicon.TryNormalizeExact(token.Text.AsSpan(), out _))
            {
                continue;
            }

            string normalized = analysis.NormalizedWord;
            if (!IsBulgarianWord(normalized))
                continue;

            if (!seen.Add(normalized))
            {
                return
                [
                    new TextDiagnostic(
                        DiagnosticCode,
                        0,
                        text.Length,
                        RuleId)
                ];
            }
        }

        return [];
    }

    private static bool IsBulgarianWord(string normalized)
    {
        if (normalized.Length == 0)
            return false;

        foreach (char character in normalized)
        {
            if (!BulgarianOrthography.IsLowercaseLetterOrStressedVowel(character))
                return false;
        }

        return true;
    }
}
