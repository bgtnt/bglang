namespace BgLang.Contracts;

/// <summary>One source-preserving match of a candidate and one complete original anchor reading.</summary>
public sealed record WordConstructionMatch(int CandidateIndex, int Start, int Length,
    TextReadingReference Anchor);
