namespace BgLang.Contracts;

/// <summary>Complete accepted own-form evidence; native replay provenance is a separate unavailable axis.</summary>
public sealed record WordConstructionResult(WordConstructionStatus Status, LexicalEntry? Owner,
    IReadOnlyList<TextReadingSnapshot>? OwnForms, IReadOnlyList<WordConstructionCandidate> Candidates)
{
    public bool ProductiveReplayAvailable => false;
    public string ProductiveReplayUnavailableReason => "OriginalGenerationSeedNotRetained";
}
