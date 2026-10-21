namespace BgLang.Contracts;

public enum TextSyntaxRole { Predicate, Subject, Object, Complement, PrepositionalObject, Attribute, Adverbial, Possessive }

/// <summary>Candidate roles and their explicit source basis; this is not a complete parse.</summary>
public sealed record TextSyntaxEvidence(string Version, bool Available, string? UnavailableReason,
    IReadOnlyList<TextSyntaxSentence> Sentences, IReadOnlyList<TextSyntaxGroupStatus>? AdditionalGroups);

/// <summary>The predicate index names the first emitted main predicate; it is null when no predicate is emitted.</summary>
public sealed record TextSyntaxSentence(int Start, int Length, string Status, string Route,
    int? PredicateTokenIndex, IReadOnlyList<TextSyntaxCandidate> Candidates);

public sealed record TextSyntaxCandidate(TextSyntaxRole Role, string RuleId, int Start, int Length,
    IReadOnlyList<TextSyntaxSupport> Targets, IReadOnlyList<TextSyntaxSupport> Context);

public sealed record TextSyntaxSupport(int TokenIndex, string Basis, IReadOnlyList<TextReadingReference> Readings);
/// <summary>Count of emitted candidate records assigned to this rule group; each record is counted once, and suppressed output contributes zero.</summary>
public sealed record TextSyntaxGroupStatus(string GroupId, bool Available, string? UnavailableReason, int CandidateCount);
