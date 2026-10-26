namespace BgLang.Contracts;

/// <summary>
/// Response-local original Word token and candidate/tag positions in its acquired graph hypotheses.
/// TokenIndex addresses TextAnalysis.Tokens; the other indices preserve GraphCandidates and FullTags order.
/// This reference does not establish an exact dictionary reading.
/// </summary>
public readonly record struct TextGraphReadingReference(int TokenIndex, int GraphCandidateIndex, int FullTagIndex);
