namespace BgLang.Contracts;

/// <summary>A source-aligned token and its word analysis when applicable.</summary>
public sealed record TokenAnalysis(
    string Text,
    TokenKind Kind,
    int Start,
    int Length,
    WordAnalysis? Analysis);
