using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Internal package-bound lexical inventory used by the public session facade.</summary>
internal interface IRuntimeLexicon : IDisposable
{
    IReadOnlyList<WordReading> LookupExactNormalized(string normalizedForm, int limit);
    IReadOnlyList<WordReading> LookupOrdinaryExactNormalized(string normalizedForm, int limit);
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
