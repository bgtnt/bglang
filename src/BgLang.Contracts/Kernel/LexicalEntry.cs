namespace BgLang.Contracts;

/// <summary>One declared lexical owner with literal, ordered source metadata.</summary>
public sealed record LexicalEntry(
    string EntryId,
    string LexemeKey,
    IReadOnlyList<string> Meaning,
    IReadOnlyList<LexicalRelationGroup> Related)
{
    /// <summary>False when at least one whole source relation phrase is unavailable.</summary>
    public bool RelationsComplete => Related.All(group => group.Phrases.All(phrase => phrase.IsResolved));

    /// <summary>Tests an exact owner target among validated complete singleton phrases; consult RelationsComplete for source gaps.</summary>
    public bool HasSingletonRelation(string entryId, string? kind = null) =>
        Related.Any(group => (kind is null || group.Kind == kind) &&
            group.Phrases.Any(phrase => phrase.IsResolved && phrase.Words.Count == 1 && phrase.Words[0].EntryId == entryId));
}

/// <summary>A raw source relation label and its ordered phrases, including repeated occurrences.</summary>
public sealed record LexicalRelationGroup(
    string Kind,
    IReadOnlyList<LexicalRelationPhrase> Phrases);

/// <summary>Complete validated words or the entire ordered raw source phrase that could not be bound.</summary>
public sealed record LexicalRelationPhrase(
    IReadOnlyList<LexicalWordReference> Words,
    IReadOnlyList<uint> UnresolvedSourcePhrase)
{
    public bool IsResolved => UnresolvedSourcePhrase.Count == 0;
}

/// <summary>An exact declared owner and reading reference; FullTag may be absent.</summary>
public sealed record LexicalWordReference(string EntryId, string Form, string? FullTag);
