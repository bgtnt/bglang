using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Bounded structural hypotheses backed by ordinary donors and the existing complete generator.</summary>
internal static class UnknownMorphologyAnalyzer
{
    internal const int WordReplayLimit = 1024;
    internal const int BaseTypeReplayLimit = 256;
    internal const int TextAnalysisLimit = 1024;
    internal const int KnownBaseReplayLimit = 64;
    internal const int TextKnownBaseLimit = 256;
    private const int EvidenceLimit = 256;

    internal static Result Analyze(string word, string normalized, IRuntimeLexicon lexicon,
        WordFormationAnalyzer graph, string packageId, int limit, ReplayBudget budget, bool baseOnly = false,
        DonorCache? donorCache = null)
    {
        if (!ValidToken(normalized)) return new([], false, false, 0);
        if (!budget.TryStartAnalysis()) return new([], true, false, 0);
        bool evidenceLimited = false, workLimited = false;
        int attempts = 0;
        // A reading whose raw tag has no inverse proposal cannot license this surface.
        // Filter it before the evidence cap; leave public suffix ranking and all replay bounds intact.
        MorphologyProposal[]? possible = baseOnly ? null : graph.ProposeMorphology(normalized, null, includeSelf: true, cancellationToken: budget.CancellationToken);
        HashSet<string>? possibleTags = null;
        if (possible is not null)
        {
            possibleTags = new(possible.Length, StringComparer.Ordinal);
            foreach (MorphologyProposal proposal in possible) possibleTags.Add(proposal.FullTag);
        }
        Dictionary<(string Lemma, string Form, string Tag), (int Ending, int SampleMask)> observed = [];
        for (int length = baseOnly ? 1 : 2; length <= Math.Min(5, normalized.Length); length++)
        {
            IReadOnlyList<WordReading> sample = baseOnly
                ? lexicon.FindBaseBySuffix(normalized[^length..], EvidenceLimit + 1)
                : lexicon.FindBySuffix(normalized[^length..], EvidenceLimit + 1, possibleTags);
            evidenceLimited |= sample.Count > EvidenceLimit;
            foreach (WordReading reading in sample.Take(EvidenceLimit))
            {
                if (reading.PartOfSpeech is not string tag) continue;
                string lemma = BulgarianTextNormalizer.NormalizeKey(reading.Lemma);
                string form = BulgarianTextNormalizer.NormalizeKey(reading.Form);
                if (!CanonicalLemma(lemma) || !form.EndsWith(normalized[^length..], StringComparison.Ordinal)) continue;
                var key = (lemma, form, tag);
                observed[key] = (length, baseOnly ? observed.GetValueOrDefault(key).SampleMask | (1 << length) : 0);
            }
        }
        Donor[] donors = observed.Select(row => new Donor(row.Key.Lemma, row.Key.Form, row.Key.Tag, row.Value.Ending, row.Value.SampleMask))
            .OrderBy(d => d.Lemma, StringComparer.Ordinal).ThenBy(d => d.Form, StringComparer.Ordinal)
            .ThenBy(d => d.Tag, StringComparer.Ordinal).ToArray();
        HashSet<string> observedTags = donors.Select(d => d.Tag).ToHashSet(StringComparer.Ordinal);
        MorphologyProposal[]? proposals = possible is null
            ? graph.ProposeMorphology(normalized, observedTags, baseOnly, cancellationToken: budget.CancellationToken)
            : possible.Where(p => observedTags.Contains(p.FullTag)).ToArray();
        if (proposals is null) return new([], true, evidenceLimited, 0);
        if (proposals.Length == 0) return new([], false, evidenceLimited, 0);
        Dictionary<string, IReadOnlyList<WordReading>> ordinary = new(StringComparer.Ordinal);
        Dictionary<ReplayKey, (bool ValidBase, HashSet<(string Form, string Tag)>? Tuples)> replays = [];
        Dictionary<(string BaseTag, string Rule, string Tag), Support> supports = [];
        List<MorphologyCandidate> candidates = [];

        Result Finish() => new(candidates.OrderByDescending(c => c.MatchedEndingLength).ThenByDescending(c => c.SupportingLemmas.Count)
            .ThenBy(c => c.Lemma, StringComparer.Ordinal).ThenBy(c => c.FullTag, StringComparer.Ordinal)
            .ThenBy(c => c.ProductiveRuleId, StringComparer.Ordinal).ThenBy(c => c.BaseFullTag, StringComparer.Ordinal)
            .Take(limit).ToArray(), workLimited, evidenceLimited, attempts);

        bool HasOrdinaryBase(string lemma, string baseTag)
        {
            if (!ordinary.TryGetValue(lemma, out IReadOnlyList<WordReading>? readings))
                ordinary.Add(lemma, readings = donorCache?.ReadOrdinary(lemma, budget.CancellationToken)
                    ?? lexicon.LookupOrdinaryExactNormalized(lemma, int.MaxValue));
            return readings.Any(r => r.PartOfSpeech == baseTag &&
                BulgarianTextNormalizer.NormalizeKey(r.Form) == lemma && BulgarianTextNormalizer.NormalizeKey(r.Lemma) == lemma);
        }

        string? SourceOwnedLemma(string lemma)
        {
            IReadOnlyList<WordReading> readings = ordinary[lemma];
            for (int index = 0; index < readings.Count; index++)
                if (readings[index].Lemma == lemma) return readings[index].Lemma;
            return null;
        }

        bool ReplayContains(string lemma, string baseTag, string rule, string form, string tag, bool donor = false)
        {
            ReplayKey key = new(lemma, baseTag, rule);
            if (!replays.TryGetValue(key, out var found))
            {
                if (attempts == budget.WordLimit || !budget.TryTake()) { workLimited = true; return false; }
                attempts++;
                try
                {
                    if (baseOnly)
                    {
                        string? sourceLemma = donor ? SourceOwnedLemma(lemma) : null;
                        if (sourceLemma is not null) found = (graph.IsValidOrdinaryBase(sourceLemma, baseTag, rule, budget.CancellationToken), null);
                        else { graph.ValidateBase(lemma, baseTag, rule, budget.CancellationToken); found = (true, null); }
                    }
                    else found = (false, donor && donorCache is not null
                        ? donorCache.Replay(lemma, baseTag, rule, budget.CancellationToken)
                        : graph.GenerateLexicalTuples(lemma, baseTag, rule, budget.CancellationToken));
                }
                catch (InvalidDataException) { found = default; }
                replays.Add(key, found);
            }
            return baseOnly ? found.ValidBase && form == lemma && tag == baseTag : found.Tuples?.Contains((form, tag)) == true;
        }

        string? exactLemma = null;
        bool exactKnown = false;
        foreach (MorphologyProposal proposal in proposals)
        {
            if (!ValidToken(proposal.Lemma)) continue;
            // Proposals are ordered by lemma; adjacent reuse avoids a separate cache allocation.
            if (exactLemma != proposal.Lemma)
            {
                exactLemma = proposal.Lemma;
                exactKnown = lexicon.LookupExactNormalized(proposal.Lemma, 1).Count > 0;
            }
            if (exactKnown &&
                !HasOrdinaryBase(proposal.Lemma, proposal.BaseFullTag)) continue;
            var supportKey = (proposal.BaseFullTag, proposal.RuleId, proposal.FullTag);
            if (!supports.TryGetValue(supportKey, out Support? support))
            {
                Dictionary<string, int> licensed = new(StringComparer.Ordinal);
                Donor[] eligible = donors.Where(d => d.Tag == proposal.FullTag && HasOrdinaryBase(d.Lemma, proposal.BaseFullTag)).ToArray();
                // Fewer than two potential ordinary lemmas cannot license any supported ending.
                if (eligible.Select(d => d.Lemma).Distinct(StringComparer.Ordinal).Take(2).Count() < 2) eligible = [];
                foreach (Donor donor in eligible)
                {
                    bool licensedForm = ReplayContains(donor.Lemma, proposal.BaseFullTag, proposal.RuleId, donor.Form, donor.Tag, donor: true);
                    if (workLimited) return Finish();
                    if (licensedForm)
                        licensed[donor.Lemma] = baseOnly ? licensed.GetValueOrDefault(donor.Lemma) | donor.SampleMask :
                            Math.Max(licensed.GetValueOrDefault(donor.Lemma), donor.Ending);
                }
                bool Supports(int evidence, int length) => baseOnly ? (evidence & (1 << length)) != 0 : evidence >= length;
                int ending = Enumerable.Range(baseOnly ? 1 : 2, baseOnly ? 5 : 4)
                    .LastOrDefault(length => licensed.Count(row => Supports(row.Value, length)) >= 2);
                support = new(ending, ending == 0 ? [] : licensed.Where(row => Supports(row.Value, ending))
                    .Select(row => row.Key).Order(StringComparer.Ordinal).ToArray(), baseOnly ? licensed : null);
                supports.Add(supportKey, support);
            }
            if (support.Lemmas.Length < 2) continue;
            bool candidateForm = ReplayContains(proposal.Lemma, proposal.BaseFullTag, proposal.RuleId, normalized, proposal.FullTag);
            if (workLimited) return Finish();
            if (candidateForm)
            {
                List<MorphologyCandidate>? shorter = null;
                if (support.SampledEndings is not null)
                {
                    for (int length = support.Ending - 1; length >= 1; length--)
                    {
                        string[] lemmas = support.SampledEndings.Where(row => (row.Value & (1 << length)) != 0)
                            .Select(row => row.Key).Order(StringComparer.Ordinal).ToArray();
                        if (lemmas.Length >= 2)
                            (shorter ??= []).Add(new(word, proposal.Lemma, proposal.BaseFullTag, proposal.FullTag,
                                proposal.RuleId, length, lemmas, packageId));
                    }
                }
                candidates.Add(new(word, proposal.Lemma, proposal.BaseFullTag, proposal.FullTag, proposal.RuleId,
                    support.Ending, (string[])support.Lemmas.Clone(), packageId)
                    { ShorterEndingCandidates = shorter?.ToArray() ?? [] });
            }
        }
        return Finish();
    }

    internal static Result AnalyzeKnownBases(string word, string normalized, IRuntimeLexicon lexicon,
        WordFormationAnalyzer graph, string packageId, int limit, ReplayBudget budget, DonorCache? donorCache = null)
    {
        if (!CanonicalLemma(normalized)) return new([], false, false, 0);
        if (!budget.TryStartAnalysis()) return new([], true, false, 0);
        MorphologyProposal[]? proposals = graph.ProposeMorphology(normalized, observedTags: null, cancellationToken: budget.CancellationToken);
        if (proposals is null) return new([], true, false, 0);
        Dictionary<ReplayKey, HashSet<(string Form, string Tag)>?> replays = [];
        List<MorphologyCandidate> candidates = [];
        IReadOnlyList<WordReading> ordinary = [];
        string? previousLemma = null;
        string? previousBaseTag = null;
        bool ordinaryBase = false;
        int attempts = 0;
        foreach (MorphologyProposal proposal in proposals)
        {
            // The proposer already sorts and deduplicates complete typed identities.
            if (previousLemma != proposal.Lemma)
            {
                previousLemma = proposal.Lemma;
                ordinary = CanonicalLemma(proposal.Lemma)
                    ? donorCache?.ReadOrdinary(proposal.Lemma, budget.CancellationToken)
                        ?? lexicon.LookupOrdinaryExactNormalized(proposal.Lemma, int.MaxValue) : [];
                previousBaseTag = null;
            }
            if (previousBaseTag != proposal.BaseFullTag)
            {
                previousBaseTag = proposal.BaseFullTag;
                ordinaryBase = false;
                foreach (WordReading reading in ordinary)
                    if (reading.PartOfSpeech == proposal.BaseFullTag &&
                        BulgarianTextNormalizer.NormalizeKey(reading.Form) == proposal.Lemma &&
                        BulgarianTextNormalizer.NormalizeKey(reading.Lemma) == proposal.Lemma)
                    { ordinaryBase = true; break; }
            }
            if (!ordinaryBase) continue;
            ReplayKey key = new(proposal.Lemma, proposal.BaseFullTag, proposal.RuleId);
            if (!replays.TryGetValue(key, out HashSet<(string Form, string Tag)>? tuples))
            {
                if (attempts == budget.WordLimit || !budget.TryTake()) return new(candidates.ToArray(), true, false, attempts);
                attempts++;
                try { tuples = donorCache is null
                    ? graph.GenerateLexicalTuples(proposal.Lemma, proposal.BaseFullTag, proposal.RuleId, budget.CancellationToken)
                    : donorCache.Replay(proposal.Lemma, proposal.BaseFullTag, proposal.RuleId, budget.CancellationToken); }
                catch (InvalidDataException) { tuples = null; }
                replays.Add(key, tuples);
            }
            // Retain only the requested prefix, but finish checking all work for completeness.
            if (tuples is not null && tuples.Contains((normalized, proposal.FullTag)) && candidates.Count < limit)
                candidates.Add(new(word, proposal.Lemma, proposal.BaseFullTag, proposal.FullTag,
                    proposal.RuleId, 0, [], packageId));
        }
        return new(candidates.ToArray(), false, false, attempts);
    }

    private static bool ValidToken(string value) => value.Length >= 3 && CanonicalLemma(value);

    private static bool CanonicalLemma(string value)
    {
        if (value.Length is 0 or > 256) return false;
        foreach (char character in value)
            if (!BulgarianOrthography.IsLowercaseLetterOrStressedVowel(character)) return false;
        return true;
    }
    private sealed record Donor(string Lemma, string Form, string Tag, int Ending, int SampleMask);
    private sealed record Support(int Ending, string[] Lemmas, Dictionary<string, int>? SampledEndings);
    private readonly record struct ReplayKey(string Lemma, string BaseTag, string Rule);
    internal sealed record Result(MorphologyCandidate[] Candidates, bool WorkLimited, bool EvidenceLimited, int ReplayAttempts);

    /// <summary>Complete ordinary donor work for one adjacent ending group in a text request.</summary>
    internal sealed class DonorCache(IRuntimeLexicon lexicon, WordFormationAnalyzer graph)
    {
        private string? _ending;
        private readonly Dictionary<string, IReadOnlyList<WordReading>> _ordinary = new(StringComparer.Ordinal);
        private readonly Dictionary<ReplayKey, HashSet<(string Form, string Tag)>?> _replays = [];

        internal void BeginUnknown(string normalized)
        {
            string ending = normalized[^Math.Min(5, normalized.Length)..];
            if (_ending == ending) return;
            _ending = ending;
            _ordinary.Clear();
            _replays.Clear();
        }

        internal IReadOnlyList<WordReading> ReadOrdinary(string lemma, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ordinary.TryGetValue(lemma, out IReadOnlyList<WordReading>? cached)) return cached;
            IReadOnlyList<WordReading> rows = lexicon.LookupOrdinaryExactNormalized(lemma, int.MaxValue);
            cancellationToken.ThrowIfCancellationRequested();
            // Missing/hypothetical lemmas never occupy the shared donor cache.
            if (rows.Any(row => BulgarianTextNormalizer.NormalizeKey(row.Form) == lemma &&
                    BulgarianTextNormalizer.NormalizeKey(row.Lemma) == lemma))
                _ordinary.Add(lemma, rows);
            return rows;
        }

        internal HashSet<(string Form, string Tag)>? Replay(string lemma, string baseTag, string rule,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayKey key = new(lemma, baseTag, rule);
            if (_replays.TryGetValue(key, out var cached)) return cached;
            HashSet<(string Form, string Tag)>? tuples;
            try { tuples = graph.GenerateLexicalTuples(lemma, baseTag, rule, cancellationToken); }
            catch (InvalidDataException) { tuples = null; }
            cancellationToken.ThrowIfCancellationRequested();
            _replays.Add(key, tuples);
            return tuples;
        }
    }

    internal sealed class ReplayBudget(int limit, int analysisLimit = int.MaxValue, int wordLimit = WordReplayLimit,
        CancellationToken cancellationToken = default)
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal int WordLimit { get; } = wordLimit;
        internal int Remaining { get; private set; } = limit;
        internal int RemainingAnalyses { get; private set; } = analysisLimit;
        internal bool TryStartAnalysis()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (Remaining <= 0 || RemainingAnalyses <= 0) return false;
            RemainingAnalyses--;
            return true;
        }
        internal bool TryTake()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (Remaining <= 0) return false;
            Remaining--;
            return true;
        }
    }
}
