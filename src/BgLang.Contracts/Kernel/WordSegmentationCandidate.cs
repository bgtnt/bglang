namespace BgLang.Contracts;

/// <summary>
/// A complete cover candidate with exact readings and optional productive graph evidence; it is
/// not a syntax-ranked word decision.
/// </summary>
public sealed record WordSegmentationCandidate(
    string OriginalText,
    string NormalizedText,
    string SegmentedText,
    IReadOnlyList<WordSegment> Segments,
    int Rank,
    bool IsSyntaxRanked,
    string Source);
