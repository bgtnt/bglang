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
    private const string JoinedNegationCode = "joined_verb_negation_should_be_separate";
    private const string JoinedNegationRuleId = "spelling.joined_verb_negation.reviewed_forms.v1";
    private const int MaximumCandidateLength = 256;

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IRuntimeLexicon lexicon,
        CancellationToken cancellationToken = default,
        SpellingCandidateCache? spellingCandidates = null,
        IReadOnlyList<SentenceSpan>? sentences = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<TextDiagnostic> diagnostics = [];
        spellingCandidates ??= new(lexicon);
        HashSet<int> sentenceInitialWords = sentences is null
            ? tokens.Where(token => token.Kind == TokenKind.Word).Take(1).Select(token => token.Start).ToHashSet()
            : SentenceBoundaryDetector.GroupWordsBySentence(tokens, sentences)
                .Where(words => words.Length > 0).Select(words => words[0].Start).ToHashSet();
        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[tokenIndex];
            cancellationToken.ThrowIfCancellationRequested();
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
                    cancellationToken.ThrowIfCancellationRequested();
                    bool known = normalized.Length > 0 && lexicon.LookupExactNormalized(normalized, int.MaxValue).Count > 0;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (known)
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
            if (token.Text is "й" or "и\u0306" &&
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
                cancellationToken.ThrowIfCancellationRequested();
                bool known = normalized.Length > 0 && lexicon.LookupExactNormalized(normalized, 1).Count > 0;
                cancellationToken.ThrowIfCancellationRequested();
                if (known)
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

            // These two normative spelling cells precede a fuzzy one-word projection.
            // Raw fuzzy alternatives and every graph hypothesis remain separate, unchanged evidence.
            string joined = BulgarianTextNormalizer.NormalizeKey(token.Text);
            if ((joined is "незнам" or "неможе") &&
                TryProjectReviewedNegation(tokens, tokenIndex, joined, lexicon,
                    sentenceInitialWords.Contains(token.Start), cancellationToken, out string separated))
            {
                diagnostics.Add(new(JoinedNegationCode, token.Start, token.Length, JoinedNegationRuleId)
                { ProjectedText = separated });
                continue;
            }

            if (!TryFindUniqueSpellingCandidate(token, sentenceInitialWords.Contains(token.Start),
                spellingCandidates, cancellationToken, out string suggestion))
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

        cancellationToken.ThrowIfCancellationRequested();
        return diagnostics;
    }

    // BERON62.3: reviewed незнам → не знам / неможе → не може only, not a productive split engine.
    // Complete suffix rows may contain homographs (знам/interjection, може/unknown:500).
    // A finite witness licenses the named spelling cell; this never claims unanimous POS.
    private static bool TryProjectReviewedNegation(IReadOnlyList<TokenAnalysis> tokens, int index,
        string joined, IRuntimeLexicon lexicon, bool allowCapitalized, CancellationToken ct, out string projection)
    {
        projection = string.Empty;
        TokenAnalysis token = tokens[index];
        WordAnalysis word = token.Analysis!;
        bool lower = StringComparer.Ordinal.Equals(token.Text, joined);
        bool capitalized = StringComparer.Ordinal.Equals(token.Text, joined.ToUpperInvariant()) ||
            StringComparer.Ordinal.Equals(token.Text, char.ToUpperInvariant(joined[0]) + joined[1..]);
        if (!(lower || allowCapitalized && capitalized) || word.BaseEvidence is not null ||
            word.GraphSearchIncomplete || !word.GraphExactRootCandidatesComplete ||
            !StringComparer.Ordinal.Equals(word.NormalizedWord, joined) ||
            !StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(word.Word), joined) ||
            index > 0 &&
                (tokens[index - 1].Kind is TokenKind.Word or TokenKind.Number || tokens[index - 1].Text is "_" or "/" or "@") &&
                tokens[index - 1].Start + tokens[index - 1].Length == token.Start ||
            index + 1 < tokens.Count &&
                (tokens[index + 1].Kind is TokenKind.Word or TokenKind.Number || tokens[index + 1].Text is "_" or "/" or "@") &&
                token.Start + token.Length == tokens[index + 1].Start) return false;
        foreach (WordDerivation path in word.GraphCandidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!path.IsConditional) return false;
        }
        // A truncated conditional tail is not an exact reading; exact-root completeness is required above.
        SuffixReadingCursor whole = lexicon.OpenExactCursor(joined);
        ct.ThrowIfCancellationRequested();
        if (whole.TryRead(out _) || !whole.Complete) return false;
        string suffix = joined[2..], lemma = joined == "незнам" ? "знам" : "мога";
        MorphologicalPerson person = joined == "незнам" ? MorphologicalPerson.First : MorphologicalPerson.Third;
        SuffixReadingCursor cursor = lexicon.OpenExactCursor(suffix);
        ct.ThrowIfCancellationRequested();
        bool finite = false;
        while (cursor.TryRead(out WordReading reading))
        {
            ct.ThrowIfCancellationRequested();
            if (!StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Form), suffix)) return false;
            MorphologicalFeatures f = reading.Features;
            finite |= StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), lemma) &&
                f.PartOfSpeech == MorphologicalPartOfSpeech.Verb && f.VerbForm == MorphologicalVerbForm.Finite &&
                f.Person == person && f.Number == MorphologicalNumber.Singular && f.Tense == MorphologicalTense.Present &&
                !f.HasUnrecognizedFeatures && !f.HasConflictingFeatures;
        }
        ct.ThrowIfCancellationRequested();
        if (!cursor.Complete || !finite) return false;
        projection = token.Text[..2] + " " + token.Text[2..];
        return true;
    }

    private static bool TryFindUniqueSpellingCandidate(
        TokenAnalysis token,
        bool allowCapitalized,
        SpellingCandidateCache cache,
        CancellationToken cancellationToken,
        out string suggestion)
    {
        suggestion = string.Empty;
        bool lower = token.Text.All(BulgarianOrthography.IsLowercaseLetter);
        bool upper = token.Text.All(c => char.IsUpper(c) && BulgarianOrthography.IsLowercaseLetter(char.ToLowerInvariant(c)));
        bool title = token.Text.Length > 0 && char.IsUpper(token.Text[0]) &&
            BulgarianOrthography.IsLowercaseLetter(char.ToLowerInvariant(token.Text[0])) &&
            token.Text.Skip(1).All(BulgarianOrthography.IsLowercaseLetter);
        if (token.Analysis!.BaseEvidence is not null ||
            token.Text.Length is < 4 or > MaximumCandidateLength ||
            !(lower || allowCapitalized && (upper || title)))
        {
            return false;
        }

        string query = BulgarianTextNormalizer.NormalizeKey(token.Text);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FuzzyMatch> matches = cache.FindSimilarNormalized(query, distance: 1, limit: 2, cancellationToken);
        string? candidate = matches.Count == 1 && matches[0].Distance == 1 ? matches[0].Form : null;
        cancellationToken.ThrowIfCancellationRequested();
        suggestion = candidate ?? string.Empty;
        if (candidate is not null && candidate.All(BulgarianOrthography.IsLowercaseLetter))
            suggestion = upper ? candidate.ToUpperInvariant() : title ? char.ToUpperInvariant(candidate[0]) + candidate[1..] : candidate;
        return candidate is not null;
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
