namespace BgLang.Contracts;

/// <summary>
/// One normalized segment. Exact segments preserve every distinct reading; a morphology-licensed
/// segment has no readings and carries candidate-only forward evidence.
/// </summary>
public sealed record WordSegment(
    string Text,
    IReadOnlyList<WordReading> Readings)
{
    /// <summary>Candidate-only forward evidence for a non-exact segment; otherwise null.</summary>
    public WordDerivation? MorphologyEvidence { get; init; }
}
