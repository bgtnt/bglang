using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>Opt-in maximal, nonoverlapping local nominal cores and advisory replacements; not all parses or a complete grammar.</summary>
public sealed record TextNominalConstructionEvidence(string Version, bool Available, string? UnavailableReason,
    IReadOnlyList<TextNominalPhrase> Phrases, IReadOnlyList<NominalRepairReview> Reviews);

/// <summary>Measured number/gender support only. Unknown features never establish compatibility.</summary>
public enum NominalAgreementAssessment { Unassessed, Compatible, Mismatch }

/// <summary>One external NP slot. Every index and span addresses the original text, also in replacement branches.
/// Raw alternatives remain in the original reading inventory; supports do not select or discard other analyses.
/// Unassessed with empty HeadSupports means joint measurement was unavailable, not a measured mismatch.</summary>
public sealed record TextNominalPhrase(string ConstructionKey, int Start, int Length, int HeadTokenIndex,
    IReadOnlyList<int> ModifierTokenIndices, IReadOnlyList<int> ConnectorTokenIndices,
    NominalAgreementAssessment Assessment, IReadOnlyList<NominalHeadSupport> HeadSupports)
{
    /// <summary>Exact licensed local conjunction witnesses; competing connector readings stay in the original inventory.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TextReadingReference>? ConjunctionReadings { get; init; }
}

/// <summary>Every member is tested against this same actual head reading, never a union of feature axes.</summary>
public sealed record NominalHeadSupport(TextReadingReference? Reading, TextGraphReadingReference? GraphReading,
    IReadOnlyList<NominalMemberSupport> Members);

public sealed record NominalMemberSupport(int TokenIndex, NominalAgreementAssessment Assessment,
    IReadOnlyList<TextReadingReference> Readings, IReadOnlyList<TextGraphReadingReference> GraphReadings)
{
    /// <summary>Only in a replacement branch: indices in that alternative's FuzzyMatch.Readings.
    /// They never address the original edited token's inventory.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? ReplacementReadings { get; init; }
}

/// <summary>Conditional advice for finite or preposition + nominal-core hypotheses, without a grammar warning or automatic edit.
/// NoChangeRetained is always true. The distance-one, ten-form pool is not exhaustive or unique intent.</summary>
public sealed record NominalRepairReview(int TokenIndex, int Start, int Length, string Pattern,
    bool NoChangeRetained, int MaxDistance, int Limit, bool AllPackageAlternativesExamined,
    IReadOnlyList<NominalRepairAlternative> Alternatives)
{
    /// <summary>Indices into unchanged Alternatives: letter distance, compatible local construction, then raw index.
    /// A supported branch repairs one hypothetical participant role; it is not a probability or unique intent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? ContextualOrder { get; init; }

    /// <summary>Original exact context reading ordinals: all preposition rows, or the finite caller's predicate rows.
    /// Null when the unchanged finite route has graph context; never replacement or role-filtered ordinals.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TextReadingReference>? ContextReadings { get; init; }
}

/// <summary>Alternative.Readings owns replacement ordinals; all other phrase references remain original.
/// Null Phrase means this candidate did not establish the complete supported target construction.</summary>
public sealed record NominalRepairAlternative(FuzzyMatch Alternative, TextNominalPhrase? Phrase);
