namespace BgLang.Contracts;

/// <summary>Auto prefers a licensed finite context before considering an adjective-only noun context.</summary>
public enum TextContinuationTarget { Finite = 0, Noun = 1, Auto = 2 }
