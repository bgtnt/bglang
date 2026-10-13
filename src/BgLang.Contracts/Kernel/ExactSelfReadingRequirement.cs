namespace BgLang.Contracts;

/// <summary>One exact self reading that would turn conditional graph evidence into anchored evidence.</summary>
public sealed record ExactSelfReadingRequirement(
    string InputSurface,
    string FullTag,
    string EdgeId);
