namespace BgLang.Contracts;

/// <summary>A stable source-aligned diagnostic from one explicitly supported text rule.</summary>
/// <param name="Code">Machine-readable diagnostic code.</param>
/// <param name="Start">UTF-16 offset in the analyzed text.</param>
/// <param name="Length">UTF-16 source length; zero identifies an insertion point.</param>
/// <param name="RuleId">Supported rule identity, or null for a document-input precondition.</param>
public sealed record TextDiagnostic(
    string Code,
    int Start,
    int Length,
    string? RuleId)
{
    /// <summary>A deterministic evidence projection; never an automatic source-text correction.</summary>
    public string? ProjectedText { get; init; }
}
