namespace BgLang.Contracts;

/// <summary>Enabled construction evidence; Complete accounts for every exact reading in the requested scope.</summary>
public sealed record TextWordConstructionEvidence(string Version, bool Complete,
    IReadOnlyList<WordConstructionOwnerEvidence> Owners, IReadOnlyList<WordConstructionCandidate> Candidates,
    IReadOnlyList<WordConstructionMatch> Matches, IReadOnlyList<TextReadingReference> UnavailableReadings)
{
    /// <summary>Word token indices whose owner pairing/key is unavailable, including a backend returning no rows.</summary>
    public IReadOnlyList<int> UnavailableTokenIndices { get; init; } = [];

    public bool ProductiveReplayAvailable => false;
    public string ProductiveReplayUnavailableReason => "OriginalGenerationSeedNotRetained";
}
