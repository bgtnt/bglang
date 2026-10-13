namespace BgLang.Contracts;

/// <summary>A UTF-16 source span detected as a sentence without claiming syntactic analysis.</summary>
public sealed record SentenceSpan(
    int Start,
    int Length);
