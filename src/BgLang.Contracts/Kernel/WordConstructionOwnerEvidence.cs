namespace BgLang.Contracts;

/// <summary>EntryId may be absent without annotations; LexemeKey retains the validated declaration identity.</summary>
public sealed record WordConstructionOwnerEvidence(string? EntryId, string LexemeKey,
    WordConstructionStatus Status, IReadOnlyList<TextReadingSnapshot>? OwnForms);
