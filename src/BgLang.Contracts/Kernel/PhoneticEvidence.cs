namespace BgLang.Contracts;

/// <summary>Deterministic phonetic candidates; this is evidence, not a selected pronunciation.</summary>
/// <param name="SyllableCandidates">Candidate syllables preserving the source spelling of word tokens.</param>
/// <param name="ExplicitStressPositions">UTF-16 vowel offsets marked explicitly in the analyzed text.</param>
/// <param name="InferredSingleVowelStressPositions">UTF-16 vowel offsets inferred only for single-vowel words.</param>
public sealed record PhoneticEvidence(
    IReadOnlyList<string> SyllableCandidates,
    IReadOnlyList<int> ExplicitStressPositions,
    IReadOnlyList<int> InferredSingleVowelStressPositions)
{
    /// <summary>Normalized rhyme tails derived only from explicit source stress.</summary>
    public IReadOnlyList<string> ExplicitRhymeTailCandidates { get; init; } = [];

    /// <summary>Normalized rhyme tails derived only from deterministic single-vowel stress.</summary>
    public IReadOnlyList<string> InferredSingleVowelRhymeTailCandidates { get; init; } = [];

    /// <summary>UTF-16 vowel offsets from package-bound reviewed exact-form evidence.</summary>
    public IReadOnlyList<int> ReviewedExactStressPositions { get; init; } = [];

    /// <summary>Normalized rhyme tails derived only from accepted reviewed exact stress.</summary>
    public IReadOnlyList<string> ReviewedExactRhymeTailCandidates { get; init; } = [];

    /// <summary>UTF-16 vowel offsets from unanimous exact-reading editorial annotations in this package.</summary>
    public IReadOnlyList<int> LexicalStressPositions { get; init; } = [];

    /// <summary>Rhyme tails from authored lexical stress, separate from reviewed or explicit evidence.</summary>
    public IReadOnlyList<string> LexicalRhymeTailCandidates { get; init; } = [];
}
