using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>One retained normalized exact key, ranked by its best compatible source row.</summary>
/// <remarks>Readings are the complete current exact group. Indices are original exact-row
/// ordinals; no filtering, new deduplication, generation or score rewriting is applied.</remarks>
internal sealed record PrefixDiscoveryForm(
    string NormalizedForm,
    WordReading BestCompatibleReading,
    IReadOnlyList<WordReading> Readings);

/// <summary>Filtered discovery over the loaded exact dictionary and normalized prefix.</summary>
/// <remarks>The form limit counts distinct normalized exact keys. A displayed source Form
/// and dictionary Score come from BestCompatibleReading, never an incompatible homograph.
/// Available=false is unsupported, not complete-empty. SearchComplete means both retained
/// source ranges were exhausted; Truncated means an extra distinct compatible key was found.
/// ExaminedReadings counts ranked payload rows consumed by the merged filtering loop,
/// including skipped/shadowed rows. It excludes at most one unconsumed lookahead per source,
/// exact-group copying and subsequent public expectation-index projection. Cancellation
/// propagates the original token via OperationCanceledException; no complete result is returned.</remarks>
internal sealed record PrefixDiscoveryResult(
    bool Available,
    bool SearchComplete,
    bool Truncated,
    long ExaminedReadings,
    IReadOnlyList<PrefixDiscoveryForm> Forms);
