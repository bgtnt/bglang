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
        Dictionary<string, TokenAnalysis> seen = new(StringComparer.Ordinal);
        TokenAnalysis? firstRepetition = null;
        foreach (TokenAnalysis token in tokens)
        {
            if (token.Kind != TokenKind.Word ||
                token.Analysis is not { IsKnown: true } analysis ||
                BulgarianAbbreviationLexicon.TryNormalizeExact(token.Text.AsSpan(), out _))
            {
                continue;
            }

            string normalized = analysis.NormalizedWord;
            if (!IsBulgarianWord(normalized) || !analysis.Readings.Any(IsContentReading))
                continue;

            if (seen.TryGetValue(normalized, out TokenAnalysis? previous))
            {
                TokenAnalysis repeated = token.Start < previous.Start ? previous : token;
                if (token.Start < previous.Start) seen[normalized] = token;
                if (firstRepetition is null || repeated.Start < firstRepetition.Start)
                    firstRepetition = repeated;
            }
            else seen.Add(normalized, token);
        }

        return firstRepetition is null ? [] :
            [new TextDiagnostic(DiagnosticCode, firstRepetition.Start, firstRepetition.Length, RuleId)];
    }

    private static bool IsContentReading(WordReading reading)
    {
        MorphologicalFeatures features = MorphologicalFeatures.FromTag(reading.PartOfSpeech);
        return features.PartOfSpeech is not (MorphologicalPartOfSpeech.Preposition or
            MorphologicalPartOfSpeech.Conjunction or MorphologicalPartOfSpeech.Particle or
            MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun) &&
            !(features.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma) is "съм" or "бъда");
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
