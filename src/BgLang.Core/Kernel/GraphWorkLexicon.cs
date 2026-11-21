using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Per-search exact authority memo. Only fully visited payloads are cached.</summary>
internal sealed class GraphWorkLexicon(IRuntimeLexicon source, GraphSearchBudget budget) : IRuntimeLexicon
{
    private readonly Dictionary<(string Form, bool Ordinary), WordReading[]> _exact = [];
    internal IRuntimeLexicon Source => source;
    internal GraphSearchBudget Budget => budget;

    public IReadOnlyList<WordReading> LookupExactNormalized(string form, int limit) => Read(form, limit, false);
    public IReadOnlyList<WordReading> LookupOrdinaryExactNormalized(string form, int limit) => Read(form, limit, true);

    private IReadOnlyList<WordReading> Read(string form, int limit, bool ordinary)
    {
        if (limit <= 0) return [];
        if (_exact.TryGetValue((form, ordinary), out WordReading[]? cached))
            return cached.Length <= limit ? cached : cached.AsSpan(0, limit).ToArray();
        budget.Take(); // Exact index operation, before traversal/materialization.
        SuffixReadingCursor cursor = source.OpenExactCursor(form, ordinary);
        List<WordReading>? rows = null;
        while ((rows?.Count ?? 0) < limit && cursor.TryRead(out WordReading row, budget))
            (rows ??= []).Add(row);
        if (!cursor.Complete && (rows?.Count ?? 0) < limit)
            throw new GraphSearchBudget.Exhausted();
        WordReading[] result = rows?.ToArray() ?? [];
        if (cursor.Complete) _exact[(form, ordinary)] = result;
        return result;
    }

    public SuffixReadingCursor OpenSuffixCursor(string suffix, IReadOnlySet<string>? tags = null)
    {
        budget.Take();
        return source.OpenSuffixCursor(suffix, tags);
    }
    public SuffixReadingCursor OpenExactCursor(string form, bool ordinary = false)
    {
        budget.Take();
        return source.OpenExactCursor(form, ordinary);
    }
    public IReadOnlyList<WordReading> FindByPrefix(string prefix, int limit) => source.FindByPrefix(prefix, limit);
    public IReadOnlyList<WordReading> FindBySuffix(string suffix, int limit, IReadOnlySet<string>? tags = null) => source.FindBySuffix(suffix, limit, tags);
    public IReadOnlyList<WordReading> FindBaseBySuffix(string suffix, int limit) => source.FindBaseBySuffix(suffix, limit);
    public IReadOnlyList<FuzzyMatch> FindSimilarNormalized(string query, int distance, int limit) => source.FindSimilarNormalized(query, distance, limit);
    public IReadOnlyList<WordForm> GenerateForms(string lemma, int limit) => source.GenerateForms(lemma, limit);
    public void Dispose() { } // The session owns the underlying lexicon.
}
