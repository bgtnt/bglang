namespace BgLang.Contracts;

/// <summary>Qualified stress evidence for already returned graph candidates. No automatic text rendering.</summary>
public sealed record GraphStressEvidence(StressStatus Status, bool SearchIncomplete,
    IReadOnlyList<GraphCandidateStressEvidence> Candidates)
{
    /// <summary>Only the profiled anchor and explicitly modeled inflection, if any, are assessed.</summary>
    public string Coverage => "profiled_anchor_only_secondary_stress_not_assessed";
    public bool CompletePronunciation => false;

    /// <summary>Original written observations, with word-local UTF-16 offsets; never normalized away or selected.</summary>
    public IReadOnlyList<StressMark> ObservedMarks { get; init; } = [];
}

/// <summary>CandidateIndex refers to WordAnalysis.GraphCandidates, preserving every path alternative.</summary>
public sealed record GraphCandidateStressEvidence(int CandidateIndex, StressStatus Status, string Reason,
    IReadOnlyList<string> ProfileIds, IReadOnlyList<string> PolicyEvidenceIds,
    IReadOnlyList<GraphReadingStressEvidence> Readings);

/// <summary>
/// An exact root reading and lexical annotation owner supporting a licensed target tag, or a derivational base path when FullTag is null.
/// Marks are UTF-16 positions relative to WordAnalysis.Word; Text consumers add Token.Start.
/// Unknown aliases remain alternatives. No owner, tag, or stress class is selected for the caller.
/// </summary>
public sealed record GraphReadingStressEvidence(string OwnerEntryId, WordReading RootReading, string? FullTag,
    StressStatus Status, string Reason, IReadOnlyList<StressMark> Marks, string? AccentEvidenceSourceId);
