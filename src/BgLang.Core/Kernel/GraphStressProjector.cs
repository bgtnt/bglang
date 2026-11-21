using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Owner-bound accent projection over existing paths, with no discovery or rendering authority.</summary>
internal static class GraphStressProjector
{
    internal static GraphStressEvidence? Project(WordAnalysis analysis, IRuntimeLexicon lexicon,
        PackageLexicalCatalog? catalog, WordFormationAnalyzer graph, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!graph.HasGraphAccentProfiles || analysis.GraphCandidates.Count == 0) return null;
        var sourceMarks = StressEvidenceAnalyzer.ReadMarks(analysis.Word, 0);
        Dictionary<string, BulgarianStressLookup.ExactInventory> roots = new(StringComparer.Ordinal);
        Dictionary<(string Owner, string Derived), GeneratedStressParadigm> generated = [];
        List<GraphCandidateStressEvidence> candidates = [];
        for (int candidateIndex = 0; candidateIndex < analysis.GraphCandidates.Count; candidateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WordDerivation candidate = analysis.GraphCandidates[candidateIndex];
            WordFormationGraph.AccentRoute? route = graph.GetAccentRoute(candidate, cancellationToken, out string reason);
            if (route is null || catalog is null)
            {
                candidates.Add(new(candidateIndex, StressStatus.Unknown,
                    route is null ? reason : "lexical_owner_catalog_unavailable", [], [], []));
                continue;
            }
            if (!roots.TryGetValue(candidate.Base, out var inventory))
            {
                _ = BulgarianStressLookup.LookupExact(lexicon, candidate.Base, int.MaxValue, true,
                    out _, out var acquired, cancellationToken, captureOwnerPairing: true);
                inventory = acquired!; roots.Add(candidate.Base, inventory);
            }
            List<GraphReadingStressEvidence> rows = [];
            for (int rootIndex = 0; rootIndex < inventory.ResolvedReadings.Count; rootIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WordReading root = inventory.ResolvedReadings[rootIndex];
                if (BulgarianTextNormalizer.NormalizeKey(root.Form) != candidate.Base ||
                    BulgarianTextNormalizer.NormalizeKey(root.Lemma) != candidate.Base ||
                    WordFormationGraph.TryCreateStateId(root.PartOfSpeech) != route.RootState) continue;
                int? ordinal = inventory.RequireOwnerOrdinal(rootIndex, root, cancellationToken);
                if (ordinal is null) continue;
                foreach (LexicalEntry owner in catalog.GetOwners(ordinal.Value, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LexemeAccentParadigm? model = catalog.FindAccentParadigm(owner.EntryId);
                    foreach (string? tag in candidate.FullTags?.Cast<string?>() ?? new string?[] { null })
                    {
                        if (route.DominantMarks is { } dominant)
                        {
                            foreach (StressMark anchor in dominant)
                            {
                                var mappedDominant = MapToOriginal(analysis.Word, candidate.Word, anchor, sourceMarks);
                                rows.Add(new(owner.EntryId, root, tag, mappedDominant.Status, mappedDominant.Reason,
                                    mappedDominant.Marks, null));
                            }
                            continue;
                        }
                        string? refusal = model is null ? "owner_has_no_accent_model" :
                            model.Lemma != root.Lemma || model.BaseFullTag != root.PartOfSpeech ? "model_root_reading_mismatch" :
                            route.TerminalBaseTag is not null && (route.TerminalBaseTag != model.BaseFullTag ||
                                route.TerminalRuleId != model.RuleId) ? "terminal_model_mismatch" :
                            route.TerminalBaseTag is null && tag != model.BaseFullTag ? "base_tag_mismatch" : null;
                        if (refusal is not null)
                        {
                            rows.Add(new(owner.EntryId, root, tag, StressStatus.Unknown, refusal, [], model?.EvidenceSourceId));
                            continue;
                        }
                        var key = (owner.EntryId, route.DerivedLemma);
                        if (!generated.TryGetValue(key, out GeneratedStressParadigm? paradigm))
                        {
                            paradigm = model!.Generate(owner.EntryId, route.DerivedLemma, graph, cancellationToken);
                            generated.Add(key, paradigm);
                        }
                        GeneratedStressForm? form = paradigm.Forms.SingleOrDefault(row => row.Form == candidate.Word && row.FullTag == tag);
                        if (form is null || form.Status != StressStatus.Inferred || form.Marks.Count != 1)
                        {
                            rows.Add(new(owner.EntryId, root, tag, StressStatus.Unknown,
                                "accent_cell_or_retained_anchor_unavailable", [], model!.EvidenceSourceId));
                            continue;
                        }
                        var mapped = MapToOriginal(analysis.Word, candidate.Word, form.Marks[0], sourceMarks);
                        rows.Add(new(owner.EntryId, root, tag, mapped.Status, mapped.Reason,
                            mapped.Marks, model!.EvidenceSourceId));
                    }
                }
            }
            candidates.Add(new(candidateIndex, Aggregate(rows.Select(row => row.Status), rows.SelectMany(row => row.Marks)),
                rows.Count == 0 ? "exact_owner_reading_unavailable" : "owner_bound_profile_projection",
                Array.AsReadOnly(route.ProfileIds), Array.AsReadOnly(route.EvidenceIds), rows.AsReadOnly()));
        }
        bool incomplete = analysis.GraphSearchIncomplete || analysis.GraphCandidatesTruncated;
        StressStatus status = Aggregate(candidates.Select(row => row.Status), candidates.SelectMany(row => row.Readings).SelectMany(row => row.Marks));
        if (incomplete && status != StressStatus.Conflict) status = StressStatus.Incomplete;
        cancellationToken.ThrowIfCancellationRequested();
        return new(status, incomplete, candidates.AsReadOnly())
            { ObservedMarks = Array.AsReadOnly(sourceMarks.Observed) };
    }

    private static StressStatus Aggregate(IEnumerable<StressStatus> statuses, IEnumerable<StressMark> marks)
    {
        StressStatus[] values = statuses.ToArray();
        if (values.Contains(StressStatus.Conflict)) return StressStatus.Conflict;
        if (values.Contains(StressStatus.Ambiguous) || marks.Select(mark => mark.VowelOrdinal).Distinct().Skip(1).Any())
            return StressStatus.Ambiguous;
        bool inferred = values.Contains(StressStatus.Inferred);
        if (values.Contains(StressStatus.Incomplete) || inferred && values.Any(value => value != StressStatus.Inferred))
            return StressStatus.Incomplete;
        return inferred ? StressStatus.Inferred : StressStatus.Unknown;
    }

    // Original offsets are recovered by vowel ordinal, preserving NFD width and leading whitespace.
    // Explicit input accents are observations only; they do not erase or choose an owner/path.
    private static (StressStatus Status, string Reason, IReadOnlyList<StressMark> Marks) MapToOriginal(
        string original, string target, StressMark generated, (int[] Vowels, StressMark[] Observed) sourceMarks)
    {
        string canonical = BulgarianTextNormalizer.NormalizeKey(original);
        if (!BulgarianOrthography.IsWellFormedWord(original.AsSpan().Trim()) ||
            canonical != target && (!BulgarianStressLookup.TryGetSupportKey(original, canonical, out string support) || support != target))
            return (StressStatus.Unknown, "original_surface_alignment_unavailable", []);
        var (vowels, observations) = sourceMarks;
        int[] observed = observations.Select(mark => mark.VowelOrdinal).ToArray();
        if ((uint)generated.VowelOrdinal >= (uint)vowels.Length)
            return (StressStatus.Unknown, "original_surface_alignment_unavailable", []);
        IReadOnlyList<StressMark> marks = Array.AsReadOnly(new[]
            { new StressMark(generated.VowelOrdinal, vowels[generated.VowelOrdinal], StressMarkKind.Unspecified) });
        if (observed.Length > 1)
            return (StressStatus.Incomplete, "multiple_written_marks_not_fully_assessed", marks);
        bool conflict = observed.Length == 1 && observed[0] != generated.VowelOrdinal;
        return (conflict ? StressStatus.Conflict : StressStatus.Inferred,
            conflict ? "single_written_mark_disagrees_with_projected_anchor" : "inferred_profiled_anchor_not_complete_pronunciation", marks);
    }
}
