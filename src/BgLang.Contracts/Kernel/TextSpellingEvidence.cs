using System.Text.Json.Serialization;

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
    IReadOnlyList<FuzzyMatch> Alternatives)
{
    // Optional local evidence, ordered by Distance/support/original index. Raw Alternatives stay unchanged.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SpellingContextCandidate>? ContextualCandidates { get; init; }
}

public enum SpellingContextAssessment { Unassessed, Supported, NoCompatiblePair }

// Candidate indices address Alternatives and its complete Readings, never the unknown token's inventory.
public sealed record SpellingContextMatch(int CandidateReadingIndex, TextReadingReference FollowingNounReading);

// Matches are deterministic measured witnesses, not an exhaustive Cartesian product or correction confidence.
public sealed record SpellingContextCandidate(int AlternativeIndex, SpellingContextAssessment Assessment,
    IReadOnlyList<SpellingContextMatch> Matches);

// These are configured bounds, not a claim that every word requested a search.
// Alternatives retain existing ranking and Form/Readings; scores are not correction confidence.
public sealed record TextSpellingEvidence(
    int ConfiguredMaxDistance,
    int ConfiguredLimit,
    IReadOnlyList<SpellingWordEvidence> Words);
