using BgLang.Contracts;

namespace BgLang.Core;

internal static class ProperNameEvidenceAnalyzer
{
    private const string TitleContextDiagnosticCode = "proper_name_person_candidate";
    private const string TitleContextRuleId = "proper-name.title-context.v1";
    private const string ExactMorphologyDiagnosticCode = "proper_name_exact_morphology_evidence";
    private const string ExactMorphologyRuleId = "proper-name.exact-morphology.v1";
    private const string CompatiblePersonSpanDiagnosticCode =
        "proper_name_compatible_exact_person_morphology_span_evidence";
    private const string CompatiblePersonSpanRuleId =
        "proper-name.compatible-exact-person-morphology-span.v1";

    private static readonly HashSet<string> PersonTitles = new(
        ["г-н", "г-жа", "д-р", "проф.", "доц.", "инж."],
        StringComparer.Ordinal);

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        string text,
        IReadOnlyList<TokenAnalysis> tokens,
        IRuntimeLexicon lexicon)
    {
        List<TextDiagnostic> diagnostics = [];
        Dictionary<string, string[]?> completeNamePartsOfSpeech = new(StringComparer.Ordinal);
        foreach (TokenAnalysis token in tokens)
        {
            if (!TryGetCompleteNamePartsOfSpeech(
                    token,
                    lexicon,
                    completeNamePartsOfSpeech,
                    out string[] partsOfSpeech))
                continue;

            diagnostics.Add(new TextDiagnostic(
                SelectExactMorphologyDiagnosticCode(partsOfSpeech),
                token.Start,
                token.Length,
                ExactMorphologyRuleId));
        }

        for (int index = 0; index + 1 < tokens.Count; index++)
        {
            TokenAnalysis left = tokens[index];
            TokenAnalysis right = tokens[index + 1];
            if (!HasOnlyWhitespaceBetween(text, left, right) ||
                !TryGetCompleteNamePartsOfSpeech(
                    left, lexicon, completeNamePartsOfSpeech, out string[] leftParts) ||
                !TryGetCompleteNamePartsOfSpeech(
                    right, lexicon, completeNamePartsOfSpeech, out string[] rightParts) ||
                !leftParts.All(IsPersonNamePartOfSpeech) ||
                !rightParts.All(IsPersonNamePartOfSpeech) ||
                !leftParts.Contains("given_name", StringComparer.Ordinal) ||
                !rightParts.Contains("family_name", StringComparer.Ordinal))
            {
                continue;
            }

            diagnostics.Add(new TextDiagnostic(
                CompatiblePersonSpanDiagnosticCode,
                left.Start,
                right.Start + right.Length - left.Start,
                CompatiblePersonSpanRuleId));
            index++;
        }

        for (int index = 1; index < tokens.Count; index++)
        {
            TokenAnalysis title = tokens[index - 1];
            TokenAnalysis candidate = tokens[index];
            if (!IsPersonTitle(title) ||
                candidate.Kind != TokenKind.Word ||
                candidate.Analysis is not { IsKnown: false, BaseEvidence: null } ||
                !IsBulgarianTitleCase(candidate.Text) ||
                !HasOnlyWhitespaceBetween(text, title, candidate))
            {
                continue;
            }

            diagnostics.Add(new TextDiagnostic(
                TitleContextDiagnosticCode,
                candidate.Start,
                candidate.Length,
                TitleContextRuleId));
        }

        return diagnostics
            .OrderBy(diagnostic => diagnostic.Start)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsProperNamePartOfSpeech(string value) => value is
        "given_name" or "family_name" or "city_name";

    private static bool IsPersonNamePartOfSpeech(string value) => value is
        "given_name" or "family_name";

    private static bool TryGetCompleteNamePartsOfSpeech(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        Dictionary<string, string[]?> cache,
        out string[] partsOfSpeech)
    {
        partsOfSpeech = [];
        if (token.Kind != TokenKind.Word ||
            token.Analysis is not { IsKnown: true } ||
            !IsBulgarianTitleCase(token.Text))
        {
            return false;
        }

        string normalized = BulgarianTextNormalizer.NormalizeKey(token.Text);
        if (!cache.TryGetValue(normalized, out string[]? cached))
        {
            List<string> parsed = [];
            foreach (WordReading reading in lexicon.LookupExactNormalized(normalized, int.MaxValue)
                         .Where(reading => string.Equals(
                             BulgarianTextNormalizer.NormalizeKey(reading.Form),
                             normalized,
                             StringComparison.Ordinal)))
            {
                string? stateId = WordFormationGraph.TryCreateStateId(reading.PartOfSpeech);
                if (stateId is null ||
                    !IsProperNamePartOfSpeech(WordFormationGraph.GetStatePartOfSpeech(stateId)))
                {
                    cache.Add(normalized, null);
                    return false;
                }
                parsed.Add(WordFormationGraph.GetStatePartOfSpeech(stateId));
            }

            cached = parsed.Count == 0 ? null : parsed.ToArray();
            cache.Add(normalized, cached);
        }

        if (cached is null)
            return false;
        partsOfSpeech = cached;
        return true;
    }

    private static string SelectExactMorphologyDiagnosticCode(IEnumerable<string> partsOfSpeech) =>
        partsOfSpeech.Distinct(StringComparer.Ordinal).Take(2).ToArray() switch
        {
            ["given_name"] => "proper_name_exact_morphology_given_name_evidence",
            ["family_name"] => "proper_name_exact_morphology_family_name_evidence",
            ["city_name"] => "proper_name_exact_morphology_city_name_evidence",
            _ => ExactMorphologyDiagnosticCode
        };

    internal static bool IsPersonTitle(TokenAnalysis token) =>
        token.Kind == TokenKind.Word &&
        BulgarianAbbreviationLexicon.TryNormalizeExact(token.Text.AsSpan(), out string canonical) &&
        PersonTitles.Contains(canonical);

    internal static bool IsBulgarianTitleCase(string surface)
    {
        if (surface.Length < 2 ||
            !char.IsUpper(surface[0]) ||
            !BulgarianOrthography.IsLowercaseLetter(char.ToLowerInvariant(surface[0])))
        {
            return false;
        }

        for (int index = 1; index < surface.Length; index++)
        {
            char value = surface[index];
            if (!char.IsLower(value) || !BulgarianOrthography.IsLowercaseLetter(value))
                return false;
        }

        return true;
    }

    internal static bool HasOnlyWhitespaceBetween(
        string text,
        TokenAnalysis left,
        TokenAnalysis right)
    {
        int gapStart = left.Start + left.Length;
        int gapLength = right.Start - gapStart;
        if (gapStart < 0 || gapLength < 1 || gapStart + gapLength > text.Length)
            return false;

        foreach (char value in text.AsSpan(gapStart, gapLength))
        {
            if (!char.IsWhiteSpace(value))
                return false;
        }

        return true;
    }
}
