using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>A typed dictionary reading; Legacy IDs are optional migration provenance only.</summary>
public sealed record WordReading(
    string Form,
    string Lemma,
    string? PartOfSpeech,
    double Score,
    int? LegacyTypeId,
    uint? LegacyBaseId,
    uint? LegacyWordId,
    string Source)
{
    /// <summary>Shared morphological interpretation of the unchanged source tag; does not confer lexical authority.</summary>
    [JsonIgnore]
    public MorphologicalFeatures Features => MorphologicalFeatures.FromTag(PartOfSpeech);
}
