namespace BgLang.Contracts;

/// <summary>Owner-bound template evidence; OwnFormIndex is local to the complete accepted owner inventory.</summary>
public sealed record WordConstructionCandidate(string FamilyId, string? EntryId, string LexemeKey,
    int OwnFormIndex, TextReadingSnapshot BaseForm, IReadOnlyList<string> Parts,
    int BasePartIndex, string TargetConstructionTag)
{
    public bool AcceptedWordForm => false;
    public bool GrammarAuthority => false;
}
