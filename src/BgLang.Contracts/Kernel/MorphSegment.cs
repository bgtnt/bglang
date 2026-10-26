namespace BgLang.Contracts;

/// <summary>A typed, source-aligned morph segment.</summary>
public sealed record MorphSegment(
    string Surface,
    string Canonical,
    MorphKind Kind,
    int Start,
    int Length,
    string? EvidenceId);
