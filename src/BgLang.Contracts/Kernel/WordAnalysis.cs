namespace BgLang.Contracts;

/// <summary>Exact readings and optional graph-backed base/state evidence for one word.</summary>
public sealed record WordAnalysis(
    string Word,
    string NormalizedWord,
    IReadOnlyList<WordReading> Readings,
    WordDecomposition? BaseEvidence)
{
    public bool IsKnown => Readings.Count > 0;

    /// <summary>
    /// Returned licensed paths with exact dictionary roots precede conditional-root hypotheses.
    /// Both remain hypotheses, never stored readings. Incomplete discovery may omit an exact-root path.
    /// </summary>
    public IReadOnlyList<WordDerivation> GraphCandidates { get; init; } = [];

    /// <summary>Discovery, retention or proof could not finish. Returned hypotheses are partial; empty does not prove absence.</summary>
    public bool GraphSearchIncomplete { get; init; }

    /// <summary>The requested output count omitted otherwise verified graph hypotheses; independent of search completeness.</summary>
    public bool GraphCandidatesTruncated { get; init; }

    /// <summary>Bounded, ranked morphology hypotheses for an exact-unknown input; never accepted readings.</summary>
    public IReadOnlyList<MorphologyCandidate> Candidates { get; init; } = [];

    /// <summary>Discovery or replay work was exhausted. Returned hypotheses are already verified, but may omit better candidates; empty does not prove absence.</summary>
    public bool CandidateSearchLimited { get; init; }

    /// <summary>At least one suffix sample had more than the 256 donor readings used as evidence.</summary>
    public bool CandidateEvidenceLimited { get; init; }

    /// <summary>Complete-program hypotheses anchored to ordinary exact self readings, including authored productive bases; never stored readings.</summary>
    public IReadOnlyList<MorphologyCandidate> KnownBaseCandidates { get; init; } = [];

    /// <summary>This lane exhausted discovery or replay work. Returned hypotheses are a verified, incomplete subset; independent of donor-search flags.</summary>
    public bool KnownBaseSearchLimited { get; init; }

    /// <summary>Unknown input treated as a potential lemma, supported by ordinary self donors and complete productive programs.</summary>
    public IReadOnlyList<MorphologyCandidate> BaseTypeCandidates { get; init; } = [];

    /// <summary>The independent base-only search exhausted discovery or replay work. Returned hypotheses are already verified, without an exhaustive ranking guarantee.</summary>
    public bool BaseTypeSearchLimited { get; init; }

    /// <summary>At least one base-only suffix bucket exceeded its own 256-reading evidence sample.</summary>
    public bool BaseTypeEvidenceLimited { get; init; }
}
