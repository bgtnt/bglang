using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Current small-package lexicon backed by in-memory forward and reversed DAWGs.</summary>
internal sealed class InMemoryRuntimeLexicon : IRuntimeLexicon
{
    private readonly DawgIndex _forward;
    private readonly DawgIndex _suffix;
    private readonly DawgIndex? _routed;
    private readonly IFormInventory _forms;

    internal InMemoryRuntimeLexicon(
        IEnumerable<WordReading> readings,
        IFormInventory forms,
        IEnumerable<WordReading>? exactPrefixOnly = null,
        bool allowEmptyOrdinary = false)
    {
        ArgumentNullException.ThrowIfNull(forms);
        WordReading[] retained = readings.ToArray();
        _forward = new DawgIndex(retained, allowEmptyOrdinary);
        _suffix = DawgIndex.CreateSuffixIndex(retained, allowEmptyOrdinary);
        WordReading[] routed = exactPrefixOnly?.ToArray() ?? [];
        _routed = routed.Length == 0 ? null : new DawgIndex(routed);
        _forms = forms;
    }

    public IReadOnlyList<WordReading> LookupExactNormalized(string normalizedForm, int limit)
    {
        IReadOnlyList<WordReading> ordinary = _forward.LookupExactNormalized(normalizedForm, limit);
        return ordinary.Count > 0 || _routed is null
            ? ordinary
            : _routed.LookupExactNormalized(normalizedForm, limit);
    }

    public IReadOnlyList<WordReading> LookupOrdinaryExactNormalized(string normalizedForm, int limit) =>
        _forward.LookupExactNormalized(normalizedForm, limit);

    public IReadOnlyList<WordReading> FindByPrefix(string prefix, int limit)
    {
        IReadOnlyList<WordReading> ordinary = _forward.FindByPrefix(prefix, limit);
        if (_routed is null || limit <= 0)
            return ordinary;
        return ordinary
            .Concat(_routed.FindByPrefix(prefix, limit))
            .Distinct(LexicalIdentity.ReadingEquality)
            .OrderBy(reading => reading, WordReadingOrder.Comparer)
            .Take(limit)
            .ToArray();
    }

    public IReadOnlyList<WordReading> FindBySuffix(string suffix, int limit, IReadOnlySet<string>? tags = null) =>
        _suffix.FindBySuffix(suffix, limit, tags);

    public SuffixReadingCursor OpenSuffixCursor(string suffix, IReadOnlySet<string>? tags = null) =>
        _suffix.OpenSuffixCursor(suffix, tags);

    public SuffixReadingCursor OpenExactCursor(string form, bool ordinary = false)
    {
        SuffixReadingCursor cursor = _forward.OpenExactCursor(form);
        if (ordinary || _routed is null || cursor.HasRows)
            return cursor;
        return _routed.OpenExactCursor(form);
    }

    public IReadOnlyList<WordReading> FindBaseBySuffix(string suffix, int limit) =>
        _suffix.FindBaseBySuffix(suffix, limit);

    public IReadOnlyList<FuzzyMatch> FindSimilarNormalized(
        string normalizedQuery,
        int maxDistance,
        int limit) =>
        _forward.FindSimilarNormalized(normalizedQuery, maxDistance, limit);

    public IReadOnlyList<WordForm> GenerateForms(string lemma, int limit) =>
        _forms.GenerateForms(lemma, limit);

    public void Dispose()
    {
    }
}
