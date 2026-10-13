namespace BgLang.Contracts;

/// <summary>Bulgarian tokenization with typed analysis for every word token.</summary>
public sealed record TextAnalysis(
    string Text,
    IReadOnlyList<TokenAnalysis> Tokens)
{
    private static readonly PhoneticEvidence EmptyPhonetics = new([], [], []);

    /// <summary>Source-aligned sentence boundaries derived from the token stream.</summary>
    public IReadOnlyList<SentenceSpan> Sentences { get; init; } = [];

    /// <summary>Bounded phonetic evidence derived from the same source-aligned word tokens.</summary>
    public PhoneticEvidence Phonetics { get; init; } = EmptyPhonetics;

    /// <summary>Source-aligned diagnostics and candidate evidence from explicitly supported text rules.</summary>
    public IReadOnlyList<TextDiagnostic> Diagnostics { get; init; } = [];
}
