namespace BgLang.Contracts;

/// <summary>Source-aligned owner and declared relation-target evidence, without semantic selection.</summary>
public sealed record LexicalTextEvidence(
    bool MetadataAvailable,
    IReadOnlyList<LexicalTokenEvidence> Tokens,
    IReadOnlyList<LexicalPhraseEvidence> Phrases)
{
    public bool PackageHasUnresolvedRelations { get; init; }
}

/// <summary>Exact owners of one word token, retaining ambiguity and presentation truncation.</summary>
public sealed record LexicalTokenEvidence(string Surface, int Start, int Length, LexicalEntryLookup Owners);

/// <summary>A matched declared target phrase, identified by its source relation occurrence.</summary>
public sealed record LexicalPhraseEvidence(
    string Surface,
    int Start,
    int Length,
    string SourceEntryId,
    int RelationIndex,
    int PhraseIndex,
    string Kind,
    IReadOnlyList<LexicalWordReference> Words);
