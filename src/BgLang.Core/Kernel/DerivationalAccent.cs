using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Bounded accented-agentive transition over reviewed graph evidence, never a surface-suffix guess.</summary>
internal static class DerivationalAccent
{
    private const string Evidence = "patseva-bulgarian-lexical-accent:section-5.3.1.1:agentive-ach";
    // https://roa.rutgers.edu/content/article/files/1612_mirena_patseva_1.pdf, section5.3.1.1/Table4.
    internal static DerivationalStressEvidence Project(WordDecomposition decomposition)
    {
        bool licensed = decomposition.RuleId == "derivational_suffix.stem_family.ach.v0" &&
            decomposition.ApplicationId is "a:играч" or "a:плетач" && decomposition.Base is not null &&
            decomposition.Transparency == "transparent" &&
            !decomposition.Segments.Any(segment => segment.Kind == MorphKind.InflectionalEnding);
        MorphSegment[] emitted = decomposition.Segments.Where(segment => segment.Kind == MorphKind.DerivationalSuffix &&
            segment.EvidenceId == "m:suffix:ач" && segment.Surface == "ач" && segment.Canonical == "ач").ToArray();
        if (!licensed || emitted.Length != 1 || emitted[0].Start < 0 || emitted[0].Length != 2 ||
            emitted[0].Start > decomposition.Word.Length - emitted[0].Length ||
            decomposition.Word.Length != decomposition.NormalizedWord.Length ||
            !decomposition.Word.AsSpan(emitted[0].Start, emitted[0].Length).Equals(emitted[0].Surface, StringComparison.OrdinalIgnoreCase))
            return new(decomposition, StressStatus.Unknown, null);
        int position = emitted[0].Start;
        int ordinal = decomposition.Word[..position].Count(BulgarianOrthography.IsVowel);
        StressRealization realization = new(StressOrigin.DerivationalRulePrediction, StressStatus.Inferred,
            Array.AsReadOnly(new[] { new StressMark(ordinal, position, StressMarkKind.Unspecified) }), Evidence);
        return new(decomposition, StressStatus.Inferred, realization);
    }
}
