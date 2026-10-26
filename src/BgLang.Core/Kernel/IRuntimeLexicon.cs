using System.Collections.ObjectModel;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Internal package-bound lexical inventory used by the public session facade.</summary>
internal interface IRuntimeLexicon : IDisposable
{
    IReadOnlyList<WordReading> LookupExactNormalized(string normalizedForm, int limit);
    ExactLookupResult LookupExactPairedNormalized(string normalizedForm, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WordReading> readings = LookupExactNormalized(normalizedForm, limit);
        cancellationToken.ThrowIfCancellationRequested();
        return new(readings, null);
    }
    IReadOnlyList<WordReading> LookupOrdinaryExactNormalized(string normalizedForm, int limit);
    IReadOnlyList<int> FindExactLexemeOrdinals(string normalizedForm) => [];
    string? GetLexemeKey(int lexemeOrdinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lexemeOrdinal < 0) throw new InvalidOperationException("Owner ordinal is invalid.");
        return null;
    }

    // Unsupported implementations must not manufacture a complete empty accepted inventory.
    IReadOnlyDictionary<int, IReadOnlyList<WordReading>?>? CollectAcceptedOwnForms(
        IReadOnlyCollection<int> ownerOrdinals, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(ownerOrdinals);
        bool any = false;
        foreach (int owner in ownerOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner < 0) throw new ArgumentOutOfRangeException(nameof(ownerOrdinals));
            any = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return any ? null : new ReadOnlyDictionary<int, IReadOnlyList<WordReading>?>(
            new Dictionary<int, IReadOnlyList<WordReading>?>());
    }
    // Unsupported backends must not manufacture a complete empty dictionary search.
    PrefixDiscoveryResult DiscoverByPrefix(string prefix, int formLimit,
        Func<WordReading, bool> compatible, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(compatible);
        if (formLimit <= 0) throw new ArgumentOutOfRangeException(nameof(formLimit));
        return new(false, false, false, 0, []);
    }
    IReadOnlyList<WordReading> FindByPrefix(string prefix, int limit);
    IReadOnlyList<WordReading> FindBySuffix(string suffix, int limit, IReadOnlySet<string>? tags = null);
    // A legacy implementation cannot promise a metered scan by materializing
    // FindBySuffix first. Unsupported cursors explicitly leave proof incomplete.
    SuffixReadingCursor OpenSuffixCursor(string suffix, IReadOnlySet<string>? tags = null) =>
        new(Array.Empty<WordReading>(), tags, sourceComplete: false);
    SuffixReadingCursor OpenExactCursor(string form, bool ordinary = false) =>
        new(Array.Empty<WordReading>(), sourceComplete: false);
    IReadOnlyList<WordReading> FindBaseBySuffix(string suffix, int limit);
    IReadOnlyList<FuzzyMatch> FindSimilarNormalized(string normalizedQuery, int maxDistance, int limit);
    IReadOnlyList<WordForm> GenerateForms(string lemma, int limit);
}
