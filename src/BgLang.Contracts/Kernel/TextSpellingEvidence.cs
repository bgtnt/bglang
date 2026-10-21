namespace BgLang.Contracts;

// Completed is one bounded ordinary-index invocation, never uniqueness or all-package coverage.
public enum SpellingSearchDisposition
{
    Unavailable,
    NotRequestedKnown,
    NotRequestedPolicy,
    Guarded,
    Completed
}

// One row for every Word occurrence; TokenIndex identifies the original token, including duplicates.
// API-owned collections and nested FuzzyMatch.Readings are immutable request snapshots.
public sealed record SpellingWordEvidence(
    int TokenIndex,
    string Text,
    int Start,
    int Length,
    string NormalizedQuery,
    SpellingSearchDisposition Search,
    string? Reason,
    IReadOnlyList<FuzzyMatch> Alternatives);

// These are configured bounds, not a claim that every word requested a search.
// Alternatives retain existing ranking and Form/Readings; scores are not correction confidence.
public sealed record TextSpellingEvidence(
    int ConfiguredMaxDistance,
    int ConfiguredLimit,
    IReadOnlyList<SpellingWordEvidence> Words);
