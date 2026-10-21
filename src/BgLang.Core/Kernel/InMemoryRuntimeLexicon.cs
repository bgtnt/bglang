using System.Collections.ObjectModel;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Current small-package lexicon backed by in-memory forward and reversed DAWGs.</summary>
internal sealed class InMemoryRuntimeLexicon : IRuntimeLexicon
{
    private readonly DawgIndex _forward;
    private readonly DawgIndex _suffix;
    private readonly DawgIndex? _routed;
    private readonly IFormInventory _forms;
    private readonly string[]? _lexemeKeys;

    internal InMemoryRuntimeLexicon(
        IEnumerable<WordReading> readings,
        IFormInventory forms,
        IEnumerable<WordReading>? exactPrefixOnly = null,
        bool allowEmptyOrdinary = false,
        IReadOnlyDictionary<WordReading, int>? lexemeOrdinals = null,
        IReadOnlyList<string>? lexemeKeys = null)
    {
        ArgumentNullException.ThrowIfNull(forms);
        WordReading[] retained = readings.ToArray();
        _forward = new DawgIndex(retained, allowEmptyOrdinary, lexemeOrdinals);
        _suffix = DawgIndex.CreateSuffixIndex(retained, allowEmptyOrdinary);
        WordReading[] routed = exactPrefixOnly?.ToArray() ?? [];
        _routed = routed.Length == 0 ? null : new DawgIndex(routed, lexemeOrdinals: lexemeOrdinals);
        _forms = forms;
        _lexemeKeys = lexemeKeys?.ToArray();
    }

    public IReadOnlyList<WordReading> LookupExactNormalized(string normalizedForm, int limit)
    {
        IReadOnlyList<WordReading> ordinary = _forward.LookupExactNormalized(normalizedForm, limit);
        return ordinary.Count > 0 || _routed is null
            ? ordinary
            : _routed.LookupExactNormalized(normalizedForm, limit);
    }

    public ExactLookupResult LookupExactPairedNormalized(string normalizedForm, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExactLookupResult result = _forward.LookupExactPairedNormalized(normalizedForm, limit, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Readings.Count == 0 && _routed is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = _routed.LookupExactPairedNormalized(normalizedForm, limit, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public IReadOnlyList<WordReading> LookupOrdinaryExactNormalized(string normalizedForm, int limit) =>
        _forward.LookupExactNormalized(normalizedForm, limit);

    public string? GetLexemeKey(int lexemeOrdinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lexemeOrdinal < 0 || _lexemeKeys is not null && lexemeOrdinal >= _lexemeKeys.Length)
            throw new InvalidOperationException("Owner ordinal is outside the retained lexeme keys.");
        string? key = _lexemeKeys is null ? null : _lexemeKeys[lexemeOrdinal];
        cancellationToken.ThrowIfCancellationRequested();
        return key;
    }

    public IReadOnlyList<int> FindExactLexemeOrdinals(string normalizedForm)
    {
        IReadOnlyList<int> ordinary = _forward.FindExactLexemeOrdinals(normalizedForm);
        return ordinary.Count > 0 || _routed is null ? ordinary : _routed.FindExactLexemeOrdinals(normalizedForm);
    }

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


    // Outer null means global pairing unavailable; a null value identifies one unavailable owner.
    // Dictionary keys are internal addresses. Aliases remain distinct in the outer catalog binding.
    public IReadOnlyDictionary<int, IReadOnlyList<WordReading>?>? CollectAcceptedOwnForms(
        IReadOnlyCollection<int> ownerOrdinals, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(ownerOrdinals);
        Dictionary<int, List<WordReading>> pending = [];
        foreach (int owner in ownerOrdinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner < 0) throw new ArgumentOutOfRangeException(nameof(ownerOrdinals));
            pending.TryAdd(owner, []); // Repeated tokens/aliases share one inventory acquisition.
        }
        Dictionary<int, IReadOnlyList<WordReading>?> result = new(pending.Count);
        if (pending.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ReadOnlyDictionary<int, IReadOnlyList<WordReading>?>(result);
        }
        if (!_forward.HasAcceptedOwnerPairing || _routed is { HasAcceptedOwnerPairing: false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        // Exactly one pass over each retained forward/routed payload array, never the reverse DAWG.
        _forward.AppendAcceptedOwnForms(pending, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _routed?.AppendAcceptedOwnForms(pending, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep available owners; no retained rows means unavailable, never NoApplicable.
            result.Add(item.Key, item.Value.Count == 0 ? null : Array.AsReadOnly(item.Value.ToArray()));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ReadOnlyDictionary<int, IReadOnlyList<WordReading>?>(result);
    }
    public void Dispose()
    {
    }
}
