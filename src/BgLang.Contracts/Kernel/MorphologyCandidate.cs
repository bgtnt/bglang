using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>A structurally supported unknown-word hypothesis, not a dictionary reading or lexical verdict.</summary>
/// <param name="Word">The original input, including its casing and stress marks.</param>
/// <param name="MatchedEndingLength">Supported donor ending length; zero in WordAnalysis.KnownBaseCandidates, which uses exact-base authority.</param>
/// <param name="SupportingLemmas">Distinct ordinary donor lemmas whose observed full-tag forms replay under this rule; empty in KnownBaseCandidates because an exact base is not a donor of its missing form.</param>
/// <param name="Source">The complete package identity binding lexical and graph evidence.</param>
public sealed record MorphologyCandidate(
    string Word,
    string Lemma,
    string BaseFullTag,
    string FullTag,
    string ProductiveRuleId,
    int MatchedEndingLength,
    IReadOnlyList<string> SupportingLemmas,
    string Source)
{
    /// <summary>Features of the proposed form; this remains a hypothesis.</summary>
    [JsonIgnore]
    public MorphologicalFeatures Features => MorphologicalFeatures.FromTag(FullTag);

    /// <summary>Features required by the proposed lemma's paradigm.</summary>
    [JsonIgnore]
    public MorphologicalFeatures BaseFeatures => MorphologicalFeatures.FromTag(BaseFullTag);

    /// <summary>BaseTypeCandidates only: the same typed hypothesis at each supported shorter ending, with its own sampled donors; children have no children.</summary>
    public IReadOnlyList<MorphologyCandidate> ShorterEndingCandidates { get; init; } = [];
}
