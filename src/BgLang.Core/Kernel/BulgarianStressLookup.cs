using BgLang.Contracts;

namespace BgLang.Core;

// Authored grave support must not replace a canonical lexical owner or productive identity.
internal static class BulgarianStressLookup
{
    internal static IReadOnlyList<WordReading> LookupExact(IRuntimeLexicon lexicon, string surface,
        int limit, out string normalized, CancellationToken cancellationToken = default) =>
        LookupExact(lexicon, surface, limit, false, out normalized, out _, cancellationToken);

    internal static IReadOnlyList<WordReading> LookupExact(IRuntimeLexicon lexicon, string surface,
        int limit, bool captureComplete, out string normalized, out ExactInventory? exactInventory,
        CancellationToken cancellationToken = default, bool captureOwnerPairing = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (captureOwnerPairing && !captureComplete)
            throw new ArgumentException("Owner pairing requires complete exact acquisition.", nameof(captureComplete));
        normalized = BulgarianTextNormalizer.NormalizeKey(surface);
        string canonicalKey = normalized;
        bool hasSupport = TryGetSupportKey(surface, normalized, out string support);
        cancellationToken.ThrowIfCancellationRequested();
        ExactLookupResult? canonicalPair = captureOwnerPairing
            ? lexicon.LookupExactPairedNormalized(normalized, int.MaxValue, cancellationToken) : null;
        IReadOnlyList<WordReading> canonical = canonicalPair is not null ? canonicalPair.Readings
            : lexicon.LookupExactNormalized(normalized, captureComplete || hasSupport ? int.MaxValue : limit);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WordReading>? supported = null;
        ExactLookupResult? supportPair = null;
        string? searchedSupportKey = null;
        if (canonical.Count == 0 && hasSupport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            supportPair = captureOwnerPairing
                ? lexicon.LookupExactPairedNormalized(support, int.MaxValue, cancellationToken) : null;
            supported = supportPair is not null ? supportPair.Readings
                : lexicon.LookupExactNormalized(support, captureComplete ? int.MaxValue : limit);
            searchedSupportKey = support;
            cancellationToken.ThrowIfCancellationRequested();
            if (supported.Count > 0)
                normalized = support;
        }

        exactInventory = captureComplete
            ? new(canonicalKey, canonical, searchedSupportKey, supported)
            { CanonicalOwnerOrdinals = canonicalPair?.OwnerOrdinals, SupportOwnerOrdinals = supportPair?.OwnerOrdinals }
            : null;
        IReadOnlyList<WordReading> selected = supported ?? canonical;
        IReadOnlyList<WordReading> display = selected.Count > limit ? selected.Take(limit).ToArray() : selected;
        cancellationToken.ThrowIfCancellationRequested();
        return display;
    }

    // Present only after complete exact acquisition. Null support means not searched;
    // an empty support list means searched empty. The lexicon's route precedence is retained.
    internal sealed record ExactInventory(string CanonicalKey, IReadOnlyList<WordReading> CanonicalReadings,
        string? SupportKey, IReadOnlyList<WordReading>? SupportReadings)
    {
        internal IReadOnlyList<WordReading> ResolvedReadings => SupportReadings ?? CanonicalReadings;
        internal string ResolvedKey => SupportReadings is { Count: > 0 } ? SupportKey! : CanonicalKey;
        internal IReadOnlyList<int>? CanonicalOwnerOrdinals { get; init; }
        internal IReadOnlyList<int>? SupportOwnerOrdinals { get; init; }
        // Route presence, not null-coalescing owners: searched-empty support is still the resolved route.
        internal IReadOnlyList<int>? ResolvedOwnerOrdinals =>
            SupportReadings is not null ? SupportOwnerOrdinals : CanonicalOwnerOrdinals;

        internal int? RequireOwnerOrdinal(int completeReadingIndex, WordReading observed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WordReading> rows = ResolvedReadings;
            IReadOnlyList<int>? owners = ResolvedOwnerOrdinals;
            if ((uint)completeReadingIndex >= (uint)rows.Count ||
                owners is not null && owners.Count != rows.Count)
                throw new InvalidOperationException("Owner support is outside its aligned complete inventory.");
            WordReading acquired = rows[completeReadingIndex];
            if (!ReferenceEquals(acquired, observed) ||
                BitConverter.DoubleToInt64Bits(acquired.Score) != BitConverter.DoubleToInt64Bits(observed.Score))
                throw new InvalidOperationException("Owner support is not the original acquired reading.");
            int? ordinal = owners is null ? null : owners[completeReadingIndex];
            if (ordinal < 0) throw new InvalidOperationException("Owner ordinal is invalid.");
            cancellationToken.ThrowIfCancellationRequested();
            return ordinal;
        }
    }

    internal static bool TryGetSupportKey(string surface, string normalized, out string support)
    {
        support = string.Empty;
        if (normalized.Length <= 1 || normalized.AsSpan().IndexOfAny('ѐ', 'ѝ') < 0 ||
            !BulgarianOrthography.IsWellFormedWord(surface.AsSpan().Trim()))
            return false;

        support = normalized.Replace('ѐ', 'е').Replace('ѝ', 'и');
        return true;
    }
}
