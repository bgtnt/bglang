using BgLang.Contracts;

namespace BgLang.Core;

internal static class HyphenatedDegreeEvidenceAnalyzer
{
    private const string DiagnosticCode = "hyphenated_degree_morphology_evidence";
    private const string ComparativePrefix = "по-";
    private const string SuperlativePrefix = "най-";
    private const string ComparativeRuleId = "morphology.hyphenated_degree.comparative_exact_root.v1";
    private const string SuperlativeRuleId = "morphology.hyphenated_degree.superlative_exact_root.v1";

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IRuntimeLexicon lexicon)
    {
        List<TextDiagnostic> diagnostics = [];
        Dictionary<string, IReadOnlyList<WordReading>> rootReadings = new(StringComparer.Ordinal);
        foreach (TokenAnalysis token in tokens)
        {
            if (token.Kind != TokenKind.Word ||
                token.Analysis is null ||
                !TryParse(token.Analysis.NormalizedWord, out string root, out string ruleId))
            {
                continue;
            }

            if (!rootReadings.TryGetValue(root, out IReadOnlyList<WordReading>? readings))
            {
                readings = lexicon.LookupExactNormalized(root, int.MaxValue);
                rootReadings.Add(root, readings);
            }

            if (!readings.Any(IsEligibleRootReading))
                continue;

            diagnostics.Add(new TextDiagnostic(
                DiagnosticCode,
                token.Start,
                token.Length,
                ruleId)
            {
                ProjectedText = root
            });
        }

        return diagnostics;
    }

    private static bool TryParse(string surface, out string root, out string ruleId)
    {
        if (surface.StartsWith(ComparativePrefix, StringComparison.Ordinal))
        {
            root = surface[ComparativePrefix.Length..];
            ruleId = ComparativeRuleId;
        }
        else if (surface.StartsWith(SuperlativePrefix, StringComparison.Ordinal))
        {
            root = surface[SuperlativePrefix.Length..];
            ruleId = SuperlativeRuleId;
        }
        else
        {
            root = string.Empty;
            ruleId = string.Empty;
            return false;
        }

        return root.Length > 0 && root.All(IsBulgarianLetter);
    }

    private static bool IsBulgarianLetter(char value) =>
        BulgarianOrthography.IsLowercaseLetterOrStressedVowel(char.ToLowerInvariant(value));

    private static bool IsEligibleRootReading(WordReading reading)
    {
        string? stateId = WordFormationGraph.TryCreateStateId(reading.PartOfSpeech);
        if (stateId is null)
            return false;
        string partOfSpeech = WordFormationGraph.GetStatePartOfSpeech(stateId);
        return partOfSpeech is "adjective" or "adverb";
    }
}
