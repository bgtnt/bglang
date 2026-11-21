using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Per-call authority limits, optionally charged against one shared text budget.</summary>
internal sealed class BinaryProofBudget : IRuntimeLexicon
{
    private readonly IRuntimeLexicon? _lexicon;
    private readonly BinaryProofBudget? _shared;
    private readonly CancellationToken _cancellationToken;
    private int _structural, _pairs, _replays, _exact;

    internal BinaryProofBudget(IRuntimeLexicon? lexicon = null, BinaryProofBudget? shared = null,
        CancellationToken cancellationToken = default)
    {
        _cancellationToken = shared?._cancellationToken ?? cancellationToken;
        _lexicon = lexicon;
        _shared = shared;
        (_structural, _pairs, _replays, _exact) = lexicon is null ? (8192, 1024, 256, 16384) : (512, 256, 256, 4096);
    }

    internal CancellationToken CancellationToken => _cancellationToken;
    internal int RemainingReplays => _replays;
    internal void Structural() { Take(ref _structural); _shared?.Structural(); }
    internal void Pair() { Take(ref _pairs); _shared?.Pair(); }
    internal void Replay() { Take(ref _replays); _shared?.Replay(); }
    private void Exact() { Take(ref _exact); _shared?.Exact(); }
    private void Take(ref int remaining)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (remaining <= 0) throw new Failure(Outcome.WorkExhausted);
        remaining--;
    }

    public IReadOnlyList<WordReading> LookupExactNormalized(string normalizedForm, int limit) => Read(normalizedForm, limit, false);
    public IReadOnlyList<WordReading> LookupOrdinaryExactNormalized(string normalizedForm, int limit) => Read(normalizedForm, limit, true);
    private IReadOnlyList<WordReading> Read(string form, int limit, bool ordinary)
    {
        Exact();
        // Exact DAWG lookup establishes the normalized form; consumers prove lemma and tags.
        IReadOnlyList<WordReading> rows = ordinary
            ? _lexicon!.LookupOrdinaryExactNormalized(form, 257) : _lexicon!.LookupExactNormalized(form, 257);
        if (rows.Count > 256) throw new Failure(Outcome.ProofOverflow);
        return rows.Count <= limit ? rows : rows.Take(limit).ToArray();
    }

    public IReadOnlyList<WordReading> FindByPrefix(string prefix, int limit) => _lexicon!.FindByPrefix(prefix, limit);
    public SuffixReadingCursor OpenSuffixCursor(string suffix, IReadOnlySet<string>? tags = null) => _lexicon!.OpenSuffixCursor(suffix, tags);
    public SuffixReadingCursor OpenExactCursor(string form, bool ordinary = false)
    {
        Exact();
        return _lexicon!.OpenExactCursor(form, ordinary);
    }
    public IReadOnlyList<WordReading> FindBySuffix(string suffix, int limit, IReadOnlySet<string>? tags = null) => _lexicon!.FindBySuffix(suffix, limit, tags);
    public IReadOnlyList<WordReading> FindBaseBySuffix(string suffix, int limit) => _lexicon!.FindBaseBySuffix(suffix, limit);
    public IReadOnlyList<FuzzyMatch> FindSimilarNormalized(string query, int maxDistance, int limit) => _lexicon!.FindSimilarNormalized(query, maxDistance, limit);
    public IReadOnlyList<WordForm> GenerateForms(string lemma, int limit) => _lexicon!.GenerateForms(lemma, limit);
    public void Dispose() { } // The package owns the borrowed lexicon.

    internal enum Outcome { NotApplicable, Complete, Ambiguous, ProofOverflow, ProgramUnproved, WorkExhausted }
    internal sealed class Failure(Outcome outcome) : Exception
    {
        internal Outcome Reason { get; } = outcome;
    }
}
