using BgLang.Contracts;

namespace BgLang.Core;

internal static class ArticleCoverageEvidenceAnalyzer
{
    private const string CoverageDiagnosticCode = "full_short_article_check_not_ready";
    private const string CoverageRuleId = "grammar.full_short_article_morphology_coverage.v1";
    private const string WarningDiagnosticCode = "full_article_expected_for_subject_candidate";
    private const string WarningRuleId = "grammar.full_article.copular_subject_exact.v1";

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        string text,
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences,
        IRuntimeLexicon lexicon)
    {
        List<TextDiagnostic> diagnostics = [];
        Dictionary<string, IReadOnlyList<WordReading>> completeReadings = new(StringComparer.Ordinal);
        HashSet<(int Start, int Length)> resolvedWarnings = [];
        foreach (TokenAnalysis[] words in SentenceBoundaryDetector.GroupWordsBySentence(tokens, sentences))
        {
            if (!TryCreateCopularSubjectWarning(
                    text,
                    words,
                    lexicon,
                    completeReadings,
                    out TextDiagnostic warning))
            {
                continue;
            }

            diagnostics.Add(warning);
            resolvedWarnings.Add((warning.Start, warning.Length));
        }

        foreach (TokenAnalysis token in tokens)
        {
            if (token.Kind != TokenKind.Word || token.Analysis is not { IsKnown: true } analysis)
                continue;

            string normalized = analysis.NormalizedWord;
            if (normalized.Length < 4 || !CouldBeArticleSurface(normalized))
                continue;

            if (!completeReadings.TryGetValue(normalized, out IReadOnlyList<WordReading>? readings))
            {
                readings = lexicon.LookupExactNormalized(normalized, int.MaxValue);
                completeReadings.Add(normalized, readings);
            }

            if (!readings.Any(IsMasculineSingularDefiniteReading) ||
                !IsArticleSurface(normalized, readings))
            {
                continue;
            }

            if (resolvedWarnings.Contains((token.Start, token.Length)))
                continue;

            diagnostics.Add(new TextDiagnostic(
                CoverageDiagnosticCode,
                token.Start,
                token.Length,
                CoverageRuleId));
        }

        return diagnostics;
    }

    private static bool TryCreateCopularSubjectWarning(
        string text,
        TokenAnalysis[] words,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo,
        out TextDiagnostic diagnostic)
    {
        diagnostic = null!;
        if (words.Length is < 3 or > 4 ||
            !TryResolveShortArticleCandidate(
                words[0],
                lexicon,
                memo,
                out bool requiresNounHead,
                out string projectedFullForm))
        {
            return false;
        }

        int copulaIndex;
        if (requiresNounHead)
        {
            if (words.Length != 4 || !HasUnambiguousMasculineNounHead(words[1], lexicon, memo))
                return false;
            copulaIndex = 2;
        }
        else
        {
            if (words.Length != 3)
                return false;
            copulaIndex = 1;
        }

        TokenAnalysis complement = words[copulaIndex + 1];
        if (!IsOnlyWhitespaceBetween(text, words, copulaIndex + 2) ||
            !HasUnambiguousThirdSingularPresentCopula(words[copulaIndex], lexicon, memo) ||
            !HasUnambiguousNominalComplement(complement, lexicon, memo))
        {
            return false;
        }

        diagnostic = new TextDiagnostic(
            WarningDiagnosticCode,
            words[0].Start,
            words[0].Length,
            WarningRuleId)
        {
            ProjectedText = MatchInitialCase(words[0].Text, projectedFullForm)
        };
        return true;
    }

    private static bool TryResolveShortArticleCandidate(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo,
        out bool requiresNounHead,
        out string projectedFullForm)
    {
        requiresNounHead = false;
        projectedFullForm = string.Empty;
        string surface = Normalized(token);
        if (token.Analysis is not { IsKnown: true } ||
            surface.Length < 4 ||
            !CouldBeShortArticleSurface(surface) ||
            !TryGetCompleteReadings(surface, lexicon, memo, out IReadOnlyList<WordReading> readings))
        {
            return false;
        }

        string[] lemmas = readings
            .Select(reading => BulgarianTextNormalizer.NormalizeKey(reading.Lemma))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (lemmas.Length != 1 || lemmas[0].Length == 0)
            return false;

        bool noun = readings.Any(reading => IsExplicitNounArticle(reading, fullForm: false)) &&
            readings.All(reading =>
                IsCategory(reading.PartOfSpeech, "Съществително") &&
                string.Equals(
                    BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
                    lemmas[0],
                    StringComparison.Ordinal) &&
                (IsMasculineSingularDefiniteReading(reading) ||
                 ContainsAtom(reading.PartOfSpeech, "Бройна"))) &&
            (string.Equals(surface, lemmas[0] + "а", StringComparison.Ordinal) ||
             string.Equals(surface, lemmas[0] + "я", StringComparison.Ordinal));
        bool adjective = surface.EndsWith("ия", StringComparison.Ordinal) &&
            readings.All(reading =>
                IsCategory(reading.PartOfSpeech, "Прилагателно") &&
                IsMasculineSingularDefiniteReading(reading));
        if (noun == adjective)
            return false;

        requiresNounHead = adjective;
        projectedFullForm = adjective
            ? surface + "т"
            : lemmas[0] + (surface.EndsWith("я", StringComparison.Ordinal) ? "ят" : "ът");
        if (!TryGetCompleteReadings(projectedFullForm, lexicon, memo, out IReadOnlyList<WordReading> fullReadings))
            return false;

        string fullForm = projectedFullForm;
        return fullReadings.All(reading =>
            string.Equals(
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
                lemmas[0],
                StringComparison.Ordinal) &&
            (adjective
                ? IsCategory(reading.PartOfSpeech, "Прилагателно") &&
                  IsMasculineSingularDefiniteReading(reading) &&
                  fullForm.EndsWith("ият", StringComparison.Ordinal)
                : IsExplicitNounArticle(reading, fullForm: true)));
    }

    private static bool HasUnambiguousMasculineNounHead(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo) =>
        TryGetCompleteReadings(token, lexicon, memo, out IReadOnlyList<WordReading> readings) &&
        readings.All(reading =>
            IsCategory(reading.PartOfSpeech, "Съществително") &&
            ContainsAtom(reading.PartOfSpeech, "Мъжки") &&
            !ContainsAtomPrefix(reading.PartOfSpeech, "мн.ч") &&
            !ContainsAtom(reading.PartOfSpeech, "Бройна") &&
            !IsMasculineSingularDefiniteReading(reading));

    private static bool HasUnambiguousThirdSingularPresentCopula(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo)
    {
        if (!string.Equals(Normalized(token), "е", StringComparison.Ordinal) ||
            !TryGetCompleteReadings(token, lexicon, memo, out IReadOnlyList<WordReading> readings))
        {
            return false;
        }

        WordReading[] typedReadings = readings
            .Where(reading => WordFormationGraph.TryCreateStateId(reading.PartOfSpeech) is not null)
            .ToArray();
        return typedReadings.Length > 0 && typedReadings.All(reading =>
            IsCategory(reading.PartOfSpeech, "Глагол") &&
            string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), "съм", StringComparison.Ordinal) &&
            ContainsAtom(reading.PartOfSpeech, "3л.") &&
            ContainsAtom(reading.PartOfSpeech, "Сегашно") &&
            !ContainsAtomPrefix(reading.PartOfSpeech, "мн.ч") &&
            !ContainsVerbNonFiniteAtom(reading.PartOfSpeech));
    }

    private static bool HasUnambiguousNominalComplement(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo) =>
        TryGetCompleteReadings(token, lexicon, memo, out IReadOnlyList<WordReading> readings) &&
        readings.All(reading =>
            reading.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective, ParadigmId: not null } &&
            !IsMasculineSingularDefiniteReading(reading));

    private static bool TryGetCompleteReadings(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo,
        out IReadOnlyList<WordReading> readings) =>
        TryGetCompleteReadings(Normalized(token), lexicon, memo, out readings);

    /// <summary>The normalized surface already computed for the token, without recomputing it.</summary>
    private static string Normalized(TokenAnalysis token) =>
        token.Analysis?.NormalizedWord ?? BulgarianTextNormalizer.NormalizeKey(token.Text);

    private static bool TryGetCompleteReadings(
        string normalized,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo,
        out IReadOnlyList<WordReading> readings)
    {
        if (!memo.TryGetValue(normalized, out readings!))
        {
            readings = lexicon.LookupExactNormalized(normalized, int.MaxValue);
            memo.Add(normalized, readings);
        }

        return readings.Count > 0;
    }

    private static bool IsOnlyWhitespaceBetween(
        string text,
        IReadOnlyList<TokenAnalysis> words,
        int wordCount)
    {
        for (int index = 1; index < wordCount; index++)
        {
            int start = words[index - 1].Start + words[index - 1].Length;
            int length = words[index].Start - start;
            if (length < 1 || text.AsSpan(start, length).IndexOfAnyExcept(" \t\r\n") >= 0)
                return false;
        }

        return true;
    }

    private static bool ContainsVerbNonFiniteAtom(string? fullTag) =>
        Atoms(fullTag).Any(atom =>
            atom.Contains("деятелно", StringComparison.OrdinalIgnoreCase) ||
            atom.Contains("страдателно", StringComparison.OrdinalIgnoreCase) ||
            atom.StartsWith("Деепричастие", StringComparison.OrdinalIgnoreCase) ||
            atom.StartsWith("Повелително", StringComparison.OrdinalIgnoreCase));

    private static string MatchInitialCase(string source, string normalized) =>
        IsAllUppercaseWord(source)
            ? normalized.ToUpperInvariant()
            : source.Length > 0 && char.IsUpper(source[0])
            ? string.Concat(char.ToUpperInvariant(normalized[0]), normalized[1..])
            : normalized;

    private static bool IsAllUppercaseWord(string value)
    {
        bool sawLetter = false;
        foreach (char character in value)
        {
            if (!char.IsLetter(character))
                continue;
            sawLetter = true;
            if (!char.IsUpper(character))
                return false;
        }

        return sawLetter;
    }

    private static bool IsArticleSurface(string surface, IReadOnlyList<WordReading> readings) =>
        IsShortArticleSurface(surface, readings) || IsFullArticleSurface(surface, readings);

    private static bool CouldBeArticleSurface(string surface) =>
        surface.EndsWith("а", StringComparison.Ordinal) ||
        surface.EndsWith("я", StringComparison.Ordinal) ||
        surface.EndsWith("ът", StringComparison.Ordinal) ||
        surface.EndsWith("ят", StringComparison.Ordinal);

    private static bool CouldBeShortArticleSurface(string surface) =>
        surface.EndsWith("а", StringComparison.Ordinal) ||
        surface.EndsWith("я", StringComparison.Ordinal);

    private static bool IsShortArticleSurface(string surface, IReadOnlyList<WordReading> readings) =>
        (surface.EndsWith("ия", StringComparison.Ordinal) ||
         surface.EndsWith("я", StringComparison.Ordinal) && HasExplicitNounArticle(readings, fullForm: false) ||
         surface.EndsWith("а", StringComparison.Ordinal) && HasExactHardStemNounArticle(surface, readings, fullForm: false)) &&
        !IsFullArticleSurface(surface, readings);

    private static bool IsFullArticleSurface(string surface, IReadOnlyList<WordReading> readings) =>
        surface.EndsWith("ият", StringComparison.Ordinal) ||
        surface.EndsWith("ят", StringComparison.Ordinal) && HasExplicitNounArticle(readings, fullForm: true) ||
        surface.EndsWith("ът", StringComparison.Ordinal) && HasExactHardStemNounArticle(surface, readings, fullForm: true);

    private static bool HasExactHardStemNounArticle(
        string surface,
        IEnumerable<WordReading> readings,
        bool fullForm) =>
        readings.Any(reading =>
            IsExplicitNounArticle(reading, fullForm) &&
            string.Equals(
                surface,
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma) + (fullForm ? "ът" : "а"),
                StringComparison.Ordinal));

    private static bool HasExplicitNounArticle(
        IEnumerable<WordReading> readings,
        bool fullForm) =>
        readings.Any(reading => IsExplicitNounArticle(reading, fullForm));

    private static bool IsExplicitNounArticle(WordReading reading, bool fullForm) =>
        IsCategory(reading.PartOfSpeech, "Съществително") &&
        IsMasculineSingularDefiniteReading(reading) &&
        (fullForm
            ? !ContainsAtom(reading.PartOfSpeech, "Непълен")
            : !ContainsAtom(reading.PartOfSpeech, "Пълен"));

    private static bool IsMasculineSingularDefiniteReading(WordReading reading) =>
        (IsCategory(reading.PartOfSpeech, "Съществително") ||
         IsCategory(reading.PartOfSpeech, "Прилагателно")) &&
        ContainsAtom(reading.PartOfSpeech, "Мъжки") &&
        !ContainsAtomPrefix(reading.PartOfSpeech, "мн.ч") &&
        !ContainsAtom(reading.PartOfSpeech, "Бройна") &&
        (ContainsAtom(reading.PartOfSpeech, "Членуван") ||
         ContainsAtom(reading.PartOfSpeech, "Пълен") ||
         ContainsAtom(reading.PartOfSpeech, "Непълен"));

    private static bool IsCategory(string? fullTag, string category) =>
        !string.IsNullOrWhiteSpace(fullTag) &&
        (fullTag.StartsWith(category + ":", StringComparison.OrdinalIgnoreCase) ||
         fullTag.StartsWith(category + " име:", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAtom(string? fullTag, string value) =>
        Atoms(fullTag).Any(atom => string.Equals(atom, value, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsAtomPrefix(string? fullTag, string value) =>
        Atoms(fullTag).Any(atom => atom.StartsWith(value, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Atoms(string? fullTag) =>
        (fullTag ?? string.Empty).Split(
            [',', ';'],
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
