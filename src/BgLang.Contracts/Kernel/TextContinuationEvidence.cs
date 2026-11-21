using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>Dictionary vocabulary compatible with one declared left-context lane, not next-word probability.</summary>
public sealed record TextContinuationEvidence(bool Available, string? UnavailableReason,
    int Caret, int Start, int Length, string Prefix,
    IReadOnlyList<TextContinuationExpectation> Expectations,
    IReadOnlyList<TextContinuationSuggestion> Suggestions, bool Truncated, bool SearchComplete,
    long ExaminedReadings)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextContinuationTarget Target { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TextNominalContinuationExpectation>? NominalExpectations { get; init; }

    public string Version => Target == TextContinuationTarget.Noun ? "nominal_continuation.v1" : "finite_continuation.v1";
    public string CandidateSource => Target == TextContinuationTarget.Noun ? "exact_package_nominal_vocabulary" : "exact_package_finite_vocabulary";
    public string Ranking => "dictionary_score";
}

/// <summary>One coherent interpretation with original exact/graph source supports.</summary>
public sealed record TextContinuationExpectation(MorphologicalPerson Person, MorphologicalNumber Number,
    IReadOnlyList<TextContextRoleSelection> Supports);

/// <summary>One future-head constraint; Attribute supports do not establish an anchored Context role.</summary>
public sealed record TextNominalContinuationExpectation(MorphologicalNumber Number, MorphologicalGender? Gender,
    MorphologicalArticle FirstAttributeArticle, MorphologicalArticle NounArticle,
    IReadOnlyList<TextContextRoleSelection> Attributes);

/// <summary>One normalized exact dictionary key, retaining every original reading and matching ordinal.</summary>
public sealed record TextContinuationSuggestion(string Form, double Score, IReadOnlyList<WordReading> Readings,
    IReadOnlyList<TextContinuationMatch> Matches);

public sealed record TextContinuationMatch(int ReadingIndex, IReadOnlyList<int> ExpectationIndices);
