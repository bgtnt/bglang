namespace BgLang.Contracts;

public enum TextContextRole { Adposition, Attribute, NominalContext, Predicate, Subject }
public enum TextContextSelectionStatus { Selected, Partial, Ambiguous, Truncated }

/// <summary>Bounded alternative hypotheses over the unchanged response reading inventory.</summary>
public sealed record TextContextEvidence(string Version, bool Available, string? UnavailableReason,
    IReadOnlyList<TextContextSentence> Sentences);

public sealed record TextContextSentence(int Start, int Length, bool Complete,
    TextContextSelectionStatus? Status, int? SelectedPredicateTokenIndex, int? DiagnosticPredicateTokenIndex,
    IReadOnlyList<TextContextCandidate> Candidates, IReadOnlyList<TextContextRoleSelection> LocalRoles,
    IReadOnlyList<TextContextRoleSelection> SelectedRoles, IReadOnlyList<string> EvidenceCodes);

public sealed record TextContextCandidate(int PredicateTokenIndex, bool Selectable, string? AuthorityLane,
    int Score, string CanonicalKey, IReadOnlyList<TextContextRoleSelection> Roles, IReadOnlyList<string> EvidenceCodes);

public sealed record TextContextRoleSelection(TextContextRole Role, int TokenIndex, int Start, int Length,
    IReadOnlyList<TextReadingReference> Readings, IReadOnlyList<string> EvidenceCodes);
