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

    // Used only by the profiled automatic API lane, after its one graph traversal.
    // Graph candidate segments remain relative to candidate.Word; only decomposition source spans move.
    internal static WordDecomposition RestoreGraphSource(WordDecomposition proof, string original)
    {
        string canonical = BulgarianTextNormalizer.NormalizeKey(original);
        bool supportRoute = canonical != proof.NormalizedWord;
        if (!BulgarianOrthography.IsWellFormedWord(original.AsSpan().Trim()) ||
            supportRoute && (!TryGetSupportKey(original, canonical, out string supported) || proof.NormalizedWord != supported))
            return proof with { Word = original, Base = null, Segments = [] };
        string expected = proof.NormalizedWord;
        List<int> boundaries = [];
        List<char> letters = [];
        int start = original.Length - original.AsSpan().TrimStart().Length;
        int end = original.AsSpan().TrimEnd().Length;
        for (int offset = start; offset < end;)
        {
            boundaries.Add(offset);
            char value = char.ToLowerInvariant(BulgarianOrthography.ReadLetter(original.AsSpan(), offset, out int width));
            offset += width;
            if (offset < end && (original[offset] is '\u0300' or '\u0301')) offset++;
            letters.Add(supportRoute ? value is 'ѝ' ? 'и' : value is 'ѐ' ? 'е' : value : value);
        }
        boundaries.Add(end);
        if (new string(letters.ToArray()) != expected)
            return proof with { Word = original, Base = null, Segments = [] };
        List<MorphSegment> segments = [];
        foreach (MorphSegment segment in proof.Segments)
        {
            if (segment.Start < 0 || segment.Length < 0 || segment.Length > letters.Count ||
                segment.Start > letters.Count - segment.Length ||
                !expected.AsSpan(segment.Start, segment.Length).SequenceEqual(segment.Surface.AsSpan()))
                return proof with { Word = original, Base = null, Segments = [] };
            int from = boundaries[segment.Start], to = boundaries[segment.Start + segment.Length];
            segments.Add(segment with { Surface = original[from..to], Start = from, Length = to - from });
        }
        return proof with { Word = original, Segments = segments.AsReadOnly() };
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
