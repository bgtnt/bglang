namespace BgLang.Contracts;

/// <summary>Reading-preserving local predicate evidence, not a full parse or a grammatical-validity certificate.</summary>
public sealed record TextPredicateLinkEvidence(string Version, bool Available, string? UnavailableReason,
    IReadOnlyList<TextPredicateSentence> Sentences);

/// <summary>ConflictScopeComplete concerns only the closed pronoun/two-present-predications check.</summary>
public sealed record TextPredicateSentence(int Start, int Length, IReadOnlyList<TextPredicateOccurrence> Occurrences,
    IReadOnlyList<TextPredicateLink> Links, IReadOnlyList<string> UncoveredReasons, bool ConflictScopeComplete);

/// <summary>Every original reading is retained at a verb or future-marker occurrence, including nonverb alternatives.</summary>
public sealed record TextPredicateOccurrence(int TokenIndex, int Start, int Length,
    IReadOnlyList<TextPredicateReading> Readings);

/// <summary>Kind: finite, imperative, participle, converb, unmeasured_verb or other. Coverage describes local evidence only.</summary>
public sealed record TextPredicateReading(string Kind, string Coverage, bool AuxiliaryOrCopulaCandidate,
    TextReadingReference? Reading, TextGraphReadingReference? GraphReading);

/// <summary>A possible local relation for exactly the cited reading tuple; it does not select those readings.</summary>
public sealed record TextPredicateLink(string Kind, TextSyntaxSupport Left, TextSyntaxSupport Right,
    IReadOnlyList<TextSyntaxSupport> Markers);
