namespace BgLang.Contracts;

/// <summary>A nearest normalized dictionary form with exact OSA distance and all typed readings.</summary>
public sealed record FuzzyMatch(
    string Form,
    int Distance,
    IReadOnlyList<WordReading> Readings);
