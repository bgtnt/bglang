namespace BgLang.Contracts;

/// <summary>Exact surface owners resolved before the presentation limit.</summary>
public sealed record LexicalEntryLookup(
    string Text,
    string Normalized,
    bool MetadataAvailable,
    IReadOnlyList<LexicalEntry> Entries,
    bool Truncated)
{
    /// <summary>True when this package retains an unavailable whole source relation phrase.</summary>
    public bool PackageHasUnresolvedRelations { get; init; }
}

/// <summary>Exact owner lookup, distinguishing missing metadata from an absent owner.</summary>
public sealed record LexicalEntryResult(bool MetadataAvailable, LexicalEntry? Entry)
{
    public bool PackageHasUnresolvedRelations { get; init; }
}
