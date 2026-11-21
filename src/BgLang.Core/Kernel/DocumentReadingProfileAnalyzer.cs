using System.Text;
using BgLang.Contracts;

namespace BgLang.Core;

internal static class DocumentReadingProfileAnalyzer
{
    private readonly record struct GroupKey(string? Lemma, MorphologicalPartOfSpeech PartOfSpeech,
        MorphologicalGender? Gender, MorphologicalNumber? Number, MorphologicalArticle? Article,
        MorphologicalPerson? Person, MorphologicalTense? Tense, MorphologicalVerbForm? VerbForm);

    internal sealed record EntrySummary(int DisplayedReadingCount, int? FullReadingCount,
        int? ProjectedGroupCount, ReadingProjectionIssues Issues, bool? ActualDisplayTruncated,
        IReadOnlyList<KeyValuePair<MorphologicalPartOfSpeech, long>> DisplayedPosCounts,
        IReadOnlyList<KeyValuePair<string, long>> DisplayedLemmaCounts)
    {
        internal bool AssessmentCompleteForProjection => FullReadingCount.HasValue && Issues == ReadingProjectionIssues.None;
        internal bool? ObservedProjectedAmbiguity => ProjectedGroupCount is { } count ? count > 1 : null;
        // A displayed exact row proves presence, but no displayed row does not prove full absence.
        internal bool? ExactKnown => FullReadingCount is { } count ? count > 0 : DisplayedReadingCount > 0 ? true : null;
    }

    private static readonly EntrySummary CompleteEmptySummary = new(0, 0, 0,
        ReadingProjectionIssues.None, false,
        Array.AsReadOnly(Array.Empty<KeyValuePair<MorphologicalPartOfSpeech, long>>()),
        Array.AsReadOnly(Array.Empty<KeyValuePair<string, long>>()));

    internal static DocumentReadingProfile Calculate(IReadOnlyList<TokenAnalysis> tokens,
        TextReadingInventory inventory, CancellationToken cancellationToken)
    {
        EntrySummary[] summaries = new EntrySummary[inventory.Sets.Count];
        for (int i = 0; i < summaries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextReadingSet set = inventory.Sets[i];
            summaries[i] = set.ResolvedReadings.Count == 0 ? CompleteEmptySummary :
                Summarize(set.ResolvedReadings.Take(set.DisplayedReadingCount).ToArray(),
                    set.ResolvedReadings, set.DisplayTruncated, cancellationToken);
        }
        int occurrenceIndex = 0;
        EntrySummary Summary(int tokenIndex)
        {
            if (occurrenceIndex >= inventory.Occurrences.Count ||
                inventory.Occurrences[occurrenceIndex].TokenIndex != tokenIndex)
                throw new InvalidOperationException("A Word occurrence has no matching complete inventory.");
            TextReadingOccurrence occurrence = inventory.Occurrences[occurrenceIndex++];
            TokenAnalysis token = tokens[tokenIndex];
            TextReadingSet set = inventory.Sets[occurrence.ReadingSetIndex];
            if (occurrence.Text != token.Text || occurrence.Start != token.Start || occurrence.Length != token.Length ||
                token.Analysis is not { } analysis || analysis.Word != set.LookupText ||
                !analysis.Readings.SequenceEqual(set.ResolvedReadings.Take(set.DisplayedReadingCount).Select(row => row.Raw)))
                throw new InvalidOperationException("A profile occurrence is bound to a different raw token, lookup or displayed prefix.");
            return summaries[occurrence.ReadingSetIndex];
        }
        DocumentReadingProfile result = Calculate(tokens, Summary, cancellationToken);
        if (occurrenceIndex != inventory.Occurrences.Count)
            throw new InvalidOperationException("The complete inventory contains an extra Word occurrence.");
        return result;
    }

    // Call ONCE when an existing cache entry is complete. FullResolved is canonical-first,
    // otherwise the actually searched support result; NEVER concatenate the two inventories.
    // null FullResolved means not captured/unavailable; an empty list means searched exact absence.
    // ActualDisplayTruncated comes from the existing presentation step, not graph search flags.
    internal static EntrySummary Summarize(IReadOnlyList<TextReadingSnapshot> displayed,
        IReadOnlyList<TextReadingSnapshot>? fullResolved, bool? actualDisplayTruncated,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fullResolved is not null && displayed.Count > fullResolved.Count)
            throw new ArgumentException("Displayed exact rows must belong to the resolved complete inventory.");
        Dictionary<MorphologicalPartOfSpeech, long> pos = [];
        Dictionary<string, long> lemmas = new(StringComparer.Ordinal);
        foreach (TextReadingSnapshot row in displayed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Increment(pos, row.Features.PartOfSpeech, 1);
            // Source distributions count raw nonblank lemmas; grouping normalization is separate.
            if (!string.IsNullOrWhiteSpace(row.Raw.Lemma)) Increment(lemmas, row.Raw.Lemma, 1);
        }
        int? groupCount = null;
        ReadingProjectionIssues issues = ReadingProjectionIssues.FullInventoryUnavailable;
        if (fullResolved is not null)
        {
            HashSet<GroupKey> groups = [];
            issues = ReadingProjectionIssues.None;
            foreach (TextReadingSnapshot row in fullResolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MorphologicalFeatures f = row.Features;
                string? lemma = NormalizeLemma(row.Raw.Lemma);
                groups.Add(new(lemma, f.PartOfSpeech, f.Gender, f.Number, f.Article, f.Person, f.Tense, f.VerbForm));
                if (lemma is null) issues |= ReadingProjectionIssues.MissingLemma;
                if (f.PartOfSpeech == MorphologicalPartOfSpeech.Unknown) issues |= ReadingProjectionIssues.UnknownPartOfSpeech;
                if (f.HasUnrecognizedFeatures) issues |= ReadingProjectionIssues.UnrecognizedFeatures;
                if (f.HasConflictingFeatures) issues |= ReadingProjectionIssues.ConflictingFeatures;
            }
            groupCount = groups.Count;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(displayed.Count, fullResolved?.Count, groupCount, issues, actualDisplayTruncated,
            FreezePos(pos), FreezeLemmas(lemmas));
    }

    // One pass over WORD OCCURRENCES. The callback only retrieves the already stored summary;
    // it must not analyze, query, normalize the raw token or build a second cache.
    internal static DocumentReadingProfile Calculate(IReadOnlyList<TokenAnalysis> tokens,
        Func<int, EntrySummary?> getCachedSummary, CancellationToken cancellationToken = default)
    {
        long words = 0, known = 0, unknown = 0, unmeasured = 0, displayed = 0, fullSubtotal = 0;
        long completeInventory = 0, completeAssessment = 0, ambiguous = 0, issueWords = 0, truncated = 0;
        bool truncationMeasured = true;
        Dictionary<MorphologicalPartOfSpeech, long> pos = [];
        Dictionary<string, long> lemmas = new(StringComparer.Ordinal);
        List<WordReadingProfile> observations = [];
        for (int i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[i];
            if (token.Kind != TokenKind.Word) continue;
            // Abbreviation raw text may differ from the ACTUAL AnalyzeCached input.
            if (token.Analysis is null)
                throw new InvalidOperationException("Word occurrence has no existing analysis/cache binding.");
            EntrySummary item = getCachedSummary(i) ??
                throw new InvalidOperationException("Existing analysis cache entry has no profile summary.");
            if (item.DisplayedReadingCount != token.Analysis.Readings.Count)
                throw new InvalidOperationException("Profile summary and displayed reading inventory are not the same entry.");
            words++;
            if (item.ExactKnown == true) known++;
            else if (item.ExactKnown == false) unknown++;
            else unmeasured++;
            displayed += item.DisplayedReadingCount;
            if (item.FullReadingCount is { } full) { fullSubtotal += full; completeInventory++; }
            if (item.AssessmentCompleteForProjection) completeAssessment++;
            if (item.ObservedProjectedAmbiguity == true) ambiguous++;
            if (item.Issues != ReadingProjectionIssues.None) issueWords++;
            if (item.ActualDisplayTruncated is true) truncated++;
            else if (item.ActualDisplayTruncated is null) truncationMeasured = false;
            foreach (var count in item.DisplayedPosCounts)
            { cancellationToken.ThrowIfCancellationRequested(); Increment(pos, count.Key, count.Value); }
            foreach (var count in item.DisplayedLemmaCounts)
            { cancellationToken.ThrowIfCancellationRequested(); Increment(lemmas, count.Key, count.Value); }
            observations.Add(new(i, token.Start, token.Length, item.DisplayedReadingCount,
                item.FullReadingCount, item.ProjectedGroupCount, item.Issues, item.ActualDisplayTruncated));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(words, known, unknown, unmeasured, displayed,
            completeInventory == words ? fullSubtotal : null, completeInventory, completeAssessment,
            completeInventory == words ? ambiguous : null, issueWords, truncationMeasured ? truncated : null,
            FreezePos(pos), FreezeLemmas(lemmas), Array.AsReadOnly(observations.ToArray()));
    }

    private static string? NormalizeLemma(string? lemma) => string.IsNullOrWhiteSpace(lemma)
        ? null : lemma.Trim().Normalize(NormalizationForm.FormC);
    private static void Increment<TKey>(Dictionary<TKey, long> counts, TKey key, long amount) where TKey : notnull =>
        counts[key] = checked(counts.GetValueOrDefault(key) + amount);
    private static IReadOnlyList<KeyValuePair<MorphologicalPartOfSpeech, long>> FreezePos(
        Dictionary<MorphologicalPartOfSpeech, long> counts) =>
        Array.AsReadOnly(counts.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal).ToArray());
    private static IReadOnlyList<KeyValuePair<string, long>> FreezeLemmas(Dictionary<string, long> counts) =>
        Array.AsReadOnly(counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray());
}
