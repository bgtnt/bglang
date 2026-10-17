using BgLang.Contracts;

namespace BgLang.Core;

internal static class SpellingProjectionAnalyzer
{
    private const string OcrDiagnosticCode = "mixed_script_ocr_known_candidate";
    private const string OcrRuleId = "ocr.mixed_script_exact_projection.v1";
    private const string DigitOcrDiagnosticCode = "digit_shape_ocr_known_candidate";
    private const string DigitOcrRuleId = "ocr.digit_shape_exact_projection.v1";
    private const string AccentDiagnosticCode = "accent_sensitive_i_grave";
    private const string AccentRuleId = "spelling.accent_sensitive_i_grave.v1";
    private const string UnknownWordDiagnosticCode =
        "spelling_unknown_word_unique_distance_one_candidate";
    private const string UnknownWordRuleId = "spelling.unknown_word_unique_distance_one.v1";
    private const int MaximumCandidateLength = 256;

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IRuntimeLexicon lexicon)
    {
        List<TextDiagnostic> diagnostics = [];
        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            TokenAnalysis token = tokens[tokenIndex];
            if (token.Kind == TokenKind.Number &&
                token.Text.Length == 1 &&
                tokenIndex + 1 < tokens.Count)
            {
                TokenAnalysis suffix = tokens[tokenIndex + 1];
                char mappedDigit = token.Text[0] switch
                {
                    '0' => 'о',
                    '3' => 'з',
                    '4' => 'ч',
                    '8' => 'в',
                    _ => '\0'
                };
                bool followsWord = tokenIndex > 0 &&
                    tokens[tokenIndex - 1].Kind == TokenKind.Word &&
                    tokens[tokenIndex - 1].Start + tokens[tokenIndex - 1].Length == token.Start;
                bool continuesAsWordOrNumber = tokenIndex + 2 < tokens.Count &&
                    tokens[tokenIndex + 2].Start == suffix.Start + suffix.Length &&
                    tokens[tokenIndex + 2].Kind is TokenKind.Word or TokenKind.Number;
                if (mappedDigit != '\0' &&
                    suffix.Kind == TokenKind.Word &&
                    token.Start + token.Length == suffix.Start &&
                    !followsWord &&
                    !continuesAsWordOrNumber &&
                    suffix.Text.All(character => BulgarianOrthography.IsLowercaseLetterOrStressedVowel(
                        char.ToLowerInvariant(character))))
                {
                    string normalized = BulgarianTextNormalizer.NormalizeKey(mappedDigit + suffix.Text);
                    if (normalized.Length > 0 &&
                        lexicon.LookupExactNormalized(normalized, int.MaxValue).Count > 0)
                    {
                        diagnostics.Add(new TextDiagnostic(
                            DigitOcrDiagnosticCode,
                            token.Start,
                            suffix.Start + suffix.Length - token.Start,
                            DigitOcrRuleId)
                        {
                            ProjectedText = normalized
                        });
                        tokenIndex++;
                        continue;
                    }
                }
            }

            if (token.Kind != TokenKind.Word || token.Analysis is not { IsKnown: false })
                continue;
            if (string.Equals(token.Text, "й", StringComparison.Ordinal) &&
                token.Analysis.BaseEvidence is null)
            {
                diagnostics.Add(new TextDiagnostic(
                    AccentDiagnosticCode,
                    token.Start,
                    token.Length,
                    AccentRuleId)
                {
                    ProjectedText = "ѝ"
                });
                continue;
            }
            if (TryProjectLatinHomoglyphs(token.Text, out string projection))
            {
                string normalized = BulgarianTextNormalizer.NormalizeKey(projection);
                if (normalized.Length > 0 &&
                    lexicon.LookupExactNormalized(normalized, 1).Count > 0)
                {
                    diagnostics.Add(new TextDiagnostic(
                        OcrDiagnosticCode,
                        token.Start,
                        token.Length,
                        OcrRuleId)
                    {
                        ProjectedText = normalized
                    });
                }
                continue;
            }

            if (!TryFindUniqueSpellingCandidate(token, lexicon, out string suggestion))
                continue;

            diagnostics.Add(new TextDiagnostic(
                UnknownWordDiagnosticCode,
                token.Start,
                token.Length,
                UnknownWordRuleId)
            {
                ProjectedText = suggestion
            });
        }

        return diagnostics;
    }

    private static bool TryFindUniqueSpellingCandidate(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        out string suggestion)
    {
        suggestion = string.Empty;
        if (token.Analysis!.BaseEvidence is not null ||
            token.Text.Length is < 4 or > MaximumCandidateLength ||
            token.Text.Any(character => !BulgarianOrthography.IsLowercaseLetter(character)))
        {
            return false;
        }

        IReadOnlyList<FuzzyMatch> matches = lexicon.FindSimilarNormalized(
            BulgarianTextNormalizer.NormalizeKey(token.Text),
            maxDistance: 1,
            limit: 2);
        if (matches.Count != 1 || matches[0].Distance != 1)
            return false;

        suggestion = matches[0].Form;
        return true;
    }

    private static bool TryProjectLatinHomoglyphs(string source, out string projection)
    {
        projection = string.Empty;
        bool hasCyrillic = false;
        bool hasLatin = false;
        char[] characters = source.ToCharArray();
        for (int index = 0; index < characters.Length; index++)
        {
            char character = characters[index];
            if (IsCyrillic(character))
            {
                hasCyrillic = true;
                continue;
            }

            if (!IsLatin(character))
                continue;

            hasLatin = true;
            if (!TryMapLatinHomoglyph(character, out char mapped))
                return false;

            characters[index] = mapped;
        }

        if (!hasCyrillic || !hasLatin)
            return false;

        projection = new string(characters);
        return true;
    }

    private static bool TryMapLatinHomoglyph(char character, out char mapped)
    {
        mapped = character switch
        {
            'A' => 'А',
            'a' => 'а',
            'B' => 'В',
            'C' => 'С',
            'c' => 'с',
            'E' => 'Е',
            'e' => 'е',
            'H' => 'Н',
            'K' => 'К',
            'k' => 'к',
            'M' => 'М',
            'O' => 'О',
            'o' => 'о',
            'P' => 'Р',
            'p' => 'р',
            'T' => 'Т',
            'X' => 'Х',
            'x' => 'х',
            'Y' => 'У',
            'y' => 'у',
            _ => character
        };

        return mapped != character;
    }

    private static bool IsCyrillic(char character) =>
        character is >= '\u0400' and <= '\u04ff';

    private static bool IsLatin(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
