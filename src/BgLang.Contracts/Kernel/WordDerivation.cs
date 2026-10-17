using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>A generated word-formation edge application, not a dictionary or spelling verdict.</summary>
public sealed record WordDerivation(
    string Word,
    string Base,
    string? FromState,
    string? ToState,
    IReadOnlyList<MorphSegment> Segments,
    IReadOnlyList<string> EdgeIds,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> ApplicationIds,
    double Confidence,
    IReadOnlyList<string> EvidenceCodes,
    string Source)
{
    /// <summary>Shared feature projections of the licensed tags; preserves alternative and conditional path semantics.</summary>
    [JsonIgnore]
    public IReadOnlyList<MorphologicalFeatures> Features =>
        FullTags?.Select(MorphologicalFeatures.FromTag).ToArray() ?? [];

    /// <summary>Normalized surface immediately before terminal inflection, or Word for a base path; distinct from the root Base.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DerivedLemma { get; init; }

    /// <summary>Explicit licensed terminal-cell or base-cell tags. Null means no complete morphology claim.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? FullTags { get; init; }

    /// <summary>True when the root is a graph-supported assumption rather than an exact self reading.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsConditional { get; init; }

    /// <summary>Productive program used by a conditional root hypothesis.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConditionalRootProgramId { get; init; }

    /// <summary>Required exact base tag for the assumed conditional root.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConditionalRootBaseFullTag { get; init; }


    /// <summary>Distinct exact self donor lemmas supporting the conditional hypothesis.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConditionalRootSupportCount { get; init; }

    /// <summary>Distinct normalized donor lemmas whose ordinary cell supports this conditional path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ConditionalSupportingLemmas { get; init; }

    /// <summary>Exact self readings still required by the conditional path.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ExactSelfReadingRequirement>? UnmetExactSelfReadingRequirements { get; init; }
}
