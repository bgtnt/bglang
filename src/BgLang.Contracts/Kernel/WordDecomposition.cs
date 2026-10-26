namespace BgLang.Contracts;

/// <summary>Reviewed graph segmentation evidence for a word or inflected surface.</summary>
public sealed record WordDecomposition(
    string Word,
    string NormalizedWord,
    string? Base,
    IReadOnlyList<MorphSegment> Segments,
    string? ApplicationId,
    string? RuleId,
    double Confidence,
    IReadOnlyList<string> EvidenceCodes,
    string Source)
{
    /// <summary>Canonical semantic state of the exact root anchor for the selected graph path.</summary>
    public string? FromState { get; init; }

    /// <summary>Canonical semantic state reached by the selected graph path; not a fabricated full tag.</summary>
    public string? ToState { get; init; }

    /// <summary>Canonical reviewed structural/application decision for the selected path, or null when none applies.</summary>
    public string? Transparency { get; init; }

    /// <summary>Whether the selected reviewed structural/application path may productively generate, or null when no decision applies.</summary>
    public bool? ProductiveGeneration { get; init; }

    public bool HasEvidence => Segments.Count > 0;
}
