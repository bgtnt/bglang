namespace BgLang.Contracts;

[Flags]
public enum ProperNameKinds { Unspecified = 0, Person = 1, Place = 2 }

/// <summary>Exact lexical support or a bounded advisory context; none establishes an entity identity.</summary>
public enum ProperNameLane { Exact, ClosedClause, Title, MidSentence, Surname }

/// <summary>Source-aligned candidates and maximal compatible runs; all exact alternatives remain in ReadingInventory.</summary>
public sealed record ProperNameEvidence(
    IReadOnlyList<ProperNameCandidate> Candidates,
    IReadOnlyList<ProperNameGroup> Groups,
    IReadOnlyList<ProperNameUnresolved>? UnresolvedContextTokens,
    IReadOnlyList<int> DeferredTokenIndices,
    bool CompleteForEnabledScope)
{
    /// <summary>False means the advisory lanes were not run; an enabled empty result is distinct.</summary>
    public bool UnknownContextEvaluated => UnresolvedContextTokens is not null;
}

/// <summary>Supports address original complete reading positions, preserving duplicates and ambiguity.</summary>
public sealed record ProperNameCandidate(
    int TokenIndex, int Start, int Length, ProperNameLane Lane, ProperNameKinds Kinds,
    bool GroupingKindKnown, IReadOnlyList<TextReadingReference> ExactSupports,
    IReadOnlyList<TextReadingReference> ContextSupports);

public sealed record ProperNameUnresolved(int TokenIndex, string Reason);

/// <summary>A contiguous range in Candidates; maximality is qualified by the enabled scope's completeness.</summary>
public sealed record ProperNameGroup(int FirstCandidateIndex, int CandidateCount, int Start, int Length,
    ProperNameKinds Kinds, bool MaximalWithinCompleteEnabledScope);
