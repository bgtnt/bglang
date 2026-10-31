using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>Coarse retrieval keys over existing evidence, without selecting a reading or declaring grammatical validity.</summary>
public sealed record TextConstructionProjection(string Version, TextAnalysis Source,
    IReadOnlyList<ConstructionSentence> Sentences, IReadOnlyList<SentenceSpan> UncoveredSpans);

/// <summary>Coverage describes extraction, never grammatical completeness. Source retains every raw alternative and original UTF16 character.</summary>
public sealed record ConstructionSentence(int Start, int Length, IReadOnlyList<ConstructionSlot> Slots,
    IReadOnlyList<string> Signature, bool HasAmbiguity, IReadOnlyList<string> CoverageIssues);

/// <summary>One outer slot. A nominal slot reuses the existing coherent head/member witnesses; competing raw readings remain in Source.</summary>
public sealed record ConstructionSlot(string Symbol, int Start, int Length, IReadOnlyList<int> TokenIndices,
    IReadOnlyList<TextReadingReference> Readings, IReadOnlyList<string> ReadingSymbols,
    TextNominalPhrase? Nominal, bool HasAmbiguity, IReadOnlyList<string> InternalLinks)
{
    /// <summary>Unselected raw token branch. ReadingSymbols[i] addresses complete reading i, including duplicates and connector words.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConstructionTokenAlternatives>? FactoredTokens { get; init; }
    public bool HasNonNominalAlternatives { get; init; }
}

public sealed record ConstructionTokenAlternatives(int TokenIndex, IReadOnlyList<string> ReadingSymbols);
