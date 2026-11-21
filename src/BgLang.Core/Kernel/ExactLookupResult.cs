using BgLang.Contracts;

namespace BgLang.Core;

// Request-local exact rows; null owners means unavailable, never an empty owner claim.
internal sealed class ExactLookupResult
{
    internal ExactLookupResult(IReadOnlyList<WordReading> readings, IReadOnlyList<int>? ownerOrdinals)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (ownerOrdinals is not null && ownerOrdinals.Count != readings.Count)
            throw new InvalidOperationException("Exact readings and owner ordinals are not aligned.");
        Readings = readings;
        OwnerOrdinals = ownerOrdinals;
    }

    internal IReadOnlyList<WordReading> Readings { get; }
    internal IReadOnlyList<int>? OwnerOrdinals { get; }
}
