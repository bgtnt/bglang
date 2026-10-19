using BgLang.Contracts;

namespace BgLang.Core;

// Authored grave support must not replace a canonical lexical owner or productive identity.
internal static class BulgarianStressLookup
{
    internal static IReadOnlyList<WordReading> LookupExact(IRuntimeLexicon lexicon, string surface,
        int limit, out string normalized, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        normalized = BulgarianTextNormalizer.NormalizeKey(surface);
        bool hasSupport = TryGetSupportKey(surface, normalized, out string support);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WordReading> canonical = lexicon.LookupExactNormalized(normalized,
            hasSupport ? int.MaxValue : limit);
        cancellationToken.ThrowIfCancellationRequested();
        if (canonical.Count == 0 && hasSupport)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<WordReading> supported = lexicon.LookupExactNormalized(support, limit);
            cancellationToken.ThrowIfCancellationRequested();
            if (supported.Count > 0)
                normalized = support;
            return supported;
        }

        return canonical.Count > limit ? canonical.Take(limit).ToArray() : canonical;
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
