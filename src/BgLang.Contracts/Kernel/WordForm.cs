using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>An accepted package form; LegacyBaseId is optional migration provenance only.</summary>
public sealed record WordForm(
    string Form,
    string Lemma,
    string? Tag,
    uint? LegacyBaseId,
    string Source)
{
    /// <summary>The same feature projection used for dictionary readings and morphology hypotheses.</summary>
    [JsonIgnore]
    public MorphologicalFeatures Features => MorphologicalFeatures.FromTag(Tag);
}
