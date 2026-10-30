using BgLang.Contracts;

namespace BgLang.Core;

internal static class WordFormationEvidenceAnalyzer
{
    private const string DiagnosticCode = "reviewed_word_formation_evidence";
    private const string RuleId = "word-formation.accepted-graph-base-projection.v1";

    internal static IReadOnlyList<TextDiagnostic> Analyze(IReadOnlyList<TokenAnalysis> tokens)
    {
        List<TextDiagnostic> diagnostics = [];
        foreach (TokenAnalysis token in tokens)
        {
            if (token.Kind != TokenKind.Word ||
                token.Analysis is not { IsKnown: false, BaseEvidence: { HasEvidence: true } evidence } ||
                string.IsNullOrWhiteSpace(evidence.Base))
            {
                continue;
            }

            string normalizedBase = BulgarianTextNormalizer.NormalizeKey(evidence.Base);
            if (normalizedBase.Length == 0 ||
                string.Equals(normalizedBase, token.Analysis.NormalizedWord, StringComparison.Ordinal))
            {
                continue;
            }

            diagnostics.Add(new TextDiagnostic(
                DiagnosticCode,
                token.Start,
                token.Length,
                RuleId)
            {
                ProjectedText = normalizedBase
            });
        }

        return diagnostics;
    }
}
