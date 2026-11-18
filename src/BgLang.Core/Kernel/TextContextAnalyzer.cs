using System.Collections.Immutable;
using System.Globalization;
using BgLang.Contracts;
using Rows = BgLang.Core.MorphologyEvidenceProjection.Rows;

namespace BgLang.Core;

/// <summary>One request-local binding of the context and role calculators to complete response snapshots.</summary>
internal static class TextContextAnalyzer
{
    private static readonly string[] AdditionalGroupIds =
    [
        "unknown-preverbal-attribute", "closed-register-title-attribute", "closed-adverbials",
        "nominal-possessive-clitic", "preposed-object-clitic", "special-preposed-dual-and-suppression",
        "closed-da-short-clitic-subpredicate", "closed-postverbal-clitic-subject", "closed-dative-speech-subpredicate"
    ];

    internal static (TextContextEvidence Context, TextSyntaxEvidence Syntax) Calculate(
        TextAnalysis analysis, TextContextOptions options, CancellationToken cancellationToken,
        MorphologyEvidenceProjection.View? sharedMorphology = null,
        VerbFrameCatalog? verbFrames = null,
        double postverbalDirectObjectMaxRatio = VerbFrameCatalog.DefaultPostverbalDirectObjectMaxRatio,
        ClauseFusionAnalyzer.DirectObjectFrame? directObjectFrame = null,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? lookup = null,
        SimpleClauseAgreementResolver.VerbReadingAuthority? verbAuthority = null)
    {
        var (context, syntax, _, _) = CalculateWithArtifacts(
            analysis, options, cancellationToken, sharedMorphology,
            verbFrames, postverbalDirectObjectMaxRatio, directObjectFrame, lookup, verbAuthority);
        return (context, syntax);
    }

    internal static (TextContextEvidence Context, TextSyntaxEvidence Syntax,
        IReadOnlyList<SentenceClauseMap?> ClauseMaps,
        IReadOnlyDictionary<(int Start, int Length), IReadOnlyList<ClauseFusionAnalyzer.ArticleTarget>> ArticleTargetsBySentence)
        CalculateWithArtifacts(
            TextAnalysis analysis, TextContextOptions options, CancellationToken cancellationToken,
            MorphologyEvidenceProjection.View? sharedMorphology = null,
            VerbFrameCatalog? verbFrames = null,
            double postverbalDirectObjectMaxRatio = VerbFrameCatalog.DefaultPostverbalDirectObjectMaxRatio,
            ClauseFusionAnalyzer.DirectObjectFrame? directObjectFrame = null,
            Func<string, IReadOnlyList<TextReadingSnapshot>>? lookup = null,
            SimpleClauseAgreementResolver.VerbReadingAuthority? verbAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        MorphologyEvidenceProjection.View morphology = sharedMorphology ?? MorphologyEvidenceProjection.Create(analysis, cancellationToken);
        if (morphology.UnavailableReason is { } unavailable)
        {
            var (unavailableContext, unavailableSyntax) = Unavailable(unavailable, options);
            return (unavailableContext, unavailableSyntax, [],
                new Dictionary<(int Start, int Length), IReadOnlyList<ClauseFusionAnalyzer.ArticleTarget>>());
        }
        var byToken = morphology.ByToken;
        var orderedTokens = morphology.OrderedTokens;
        var contextSentences = new List<TextContextSentence>();
        var syntaxSentences = new List<TextSyntaxSentence>();
        var clauseMaps = new List<SentenceClauseMap?>(analysis.Sentences.Count);
        var articleTargetsBySentence = new Dictionary<(int Start, int Length), IReadOnlyList<ClauseFusionAnalyzer.ArticleTarget>>();
        int[] groupCounts = new int[AdditionalGroupIds.Length];
        bool hasComma = analysis.Text.Contains(',');
        IReadOnlyList<(int Start, int End)> protectedSpans =
            TextPredicateLinkAnalyzer.QuoteSpans(analysis.Text, cancellationToken, protectBackticks: true, protectBrackets: true);
        int firstProtected = 0;
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var laneWords = new List<ContextLanes.TokenView>();
            var exactLaneWords = new List<ContextLanes.TokenView>();
            var scopeEvidence = new List<string>();
            var roleWords = ImmutableArray.CreateBuilder<SyntaxRoleCalculator.Word>();
            foreach (var entry in orderedTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TokenAnalysis token = analysis.Tokens[entry.Index];
                if (token.Start < sentence.Start || (long)token.Start + token.Length > (long)sentence.Start + sentence.Length) continue;
                Rows r = entry.Rows;
                laneWords.Add(new(entry.Index, token, r.Canonical, r.Lanes, r.Complete) { UsesGraph = r.UsesGraph });
                exactLaneWords.Add(new(entry.Index, token, r.Canonical, r.UsesGraph ? [] : r.Lanes, r.Complete));
                if (r.GraphUnavailableReason is { } reason) scopeEvidence.Add("graph_token=" + entry.Index + ":" + reason);
                roleWords.Add(new(entry.Index, token.Start, token.Length, r.Canonical, r.Raw, r.Syntax, r.Complete)
                { GraphTags = r.UsesGraph ? r.Syntax.Select(x => x.Features.RawTag!).ToImmutableArray() : [] });
            }
            bool sentenceComplete = laneWords.All(w => w.Complete);
            string? graphReason = null;
            ContextOverlayInput? productive = sentenceComplete
                ? ContextLanes.CalculateProductive(analysis.Text, sentence, laneWords, CanonicalKey, cancellationToken, out graphReason)
                : null;
            if (graphReason is not null) scopeEvidence.Add(graphReason);
            while (firstProtected < protectedSpans.Count && protectedSpans[firstProtected].End <= sentence.Start) firstProtected++;
            bool protectedSentence = firstProtected < protectedSpans.Count &&
                protectedSpans[firstProtected].Start < sentence.Start + sentence.Length;
            SentenceClauseMap? sharedMap = productive is null && laneWords.Count > 0
                ? SentenceClauseMap.Create(analysis, sentence, laneWords, protectedSpans, cancellationToken, lookup)
                : null;
            SimpleClauseAgreementResolver.SentenceOutcome? r3 = sharedMap is not null
                ? SimpleClauseAgreementResolver.ResolveSentence(sharedMap, CanonicalKey, verbAuthority, cancellationToken, verbFrames)
                : null;
            bool matrixOwnedByR3 = r3 is { MatrixOwned: true };
            ContextOverlayInput input = productive is not null
                ? productive
                : matrixOwnedByR3
                    ? r3!.MatrixOverlayInput!
                    : !sentenceComplete
                        ? new ContextOverlayInput(true, true, "reading_limit_reached", [], [], [])
                        : ContextLanes.Calculate(analysis.Text, sentence, exactLaneWords, CanonicalKey, cancellationToken);
            ContextSelectionOverlay? selected = ContextJointSelection.Select(input, cancellationToken);
            ContextOverlayInput? exactDiagnostics = productive is null && sentenceComplete
                ? ContextLanes.CalculateExactAgreement(analysis.Text, sentence, laneWords, CanonicalKey, cancellationToken,
                    includeFiniteAgreement: !matrixOwnedByR3) : null;
            TextContextRoleSelection MapRole(ContextOverlayRoleSelection role) => new((TextContextRole)role.Role,
                role.TokenIndex, role.Start, role.Length, References(role.TokenIndex, role.AlternativeIndices), Freeze(role.EvidenceCodes))
                { GraphReadings = GraphReferences(role.TokenIndex, role.AlternativeIndices) };
            ContextOverlayInput? prefixDiagnostics = productive is null && sentenceComplete && !matrixOwnedByR3
                ? ContextLanes.CalculateExactPronounPrefix(analysis.Text, sentence, laneWords, CanonicalKey, cancellationToken) : null;
            var subordinate = hasComma && sentenceComplete ? ContextLanes.CalculateSubordinatePronounPrefixes(analysis.Text, sentence, analysis.Tokens,
                laneWords, protectedSpans, firstProtected, CanonicalKey, cancellationToken) : [];
            var clause = productive is null && !protectedSentence && !(r3?.HasOwnedClause == true)
                ? LocalClauseRoleLinker.Calculate(analysis, sentence, laneWords, CanonicalKey, cancellationToken) : null;
            var fusion = productive is null
                ? ClauseFusionAnalyzer.Analyze(analysis, sentence, laneWords, protectedSpans, CanonicalKey, cancellationToken,
                    verbFrames, postverbalDirectObjectMaxRatio, directObjectFrame, lookup, sharedMap, r3?.OwnedClauseIds) : null;
            clauseMaps.Add(fusion?.ClauseMap ?? sharedMap);
            if (fusion is not null)
                articleTargetsBySentence[(sentence.Start, sentence.Length)] = fusion.ArticleTargets;
            ClauseFusionAnalyzer.MatrixSelection? selectedFusion = null;
            if (!matrixOwnedByR3 && options.IncludeAdditionalRoleHeuristics && selected is not null && selected.SelectedPredicateTokenIndex is null &&
                selected.DiagnosticPredicateTokenIndex is null && fusion?.Selection is { } promoted)
            {
                var mergedRoles = selected.Roles
                    .Concat(promoted.Overlay.Roles)
                    .OrderBy(r => r.Start)
                    .ThenBy(r => RoleName(r.Role), StringComparer.Ordinal)
                    .ToArray();
                var mergedCodes = selected.EvidenceCodes
                    .Where(c => c != "insufficient_hypothesis_margin")
                    .Concat(promoted.Overlay.EvidenceCodes)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                selected = promoted.Overlay with { Roles = mergedRoles, EvidenceCodes = mergedCodes };
                selectedFusion = promoted;
            }
            var candidates = input.Candidates.Concat(r3?.NonMatrixCandidates ?? []).Concat(exactDiagnostics?.Candidates ?? [])
                .Concat(prefixDiagnostics?.Candidates ?? []).Concat(subordinate).Concat(clause is null ? [] : [clause])
                .Concat(fusion?.AgreementCandidates ?? [])
                .Select(candidate => new TextContextCandidate(candidate.PredicateTokenIndex,
                candidate.Selectable, candidate.AuthorityLane, candidate.Score, candidate.CanonicalKey,
                Freeze(candidate.Roles.Select(MapRole)), Freeze(candidate.EvidenceCodes)));
            contextSentences.Add(new(sentence.Start, sentence.Length, sentenceComplete && !input.Truncated,
                selected is null ? null : (TextContextSelectionStatus)selected.Status,
                selected?.SelectedPredicateTokenIndex, selected?.DiagnosticPredicateTokenIndex,
                Freeze(candidates), Freeze(input.LocalRoleCandidates.Select(MapRole)),
                selected is null ? [] : Freeze(selected.Roles.Select(MapRole)),
                Freeze((selected?.EvidenceCodes ?? []).Concat(scopeEvidence))));
            ImmutableArray<SyntaxRoleCalculator.Word> words = roleWords.ToImmutable();
            SyntaxRoleCalculator.Result result;
            if (productive is not null)
            {
                var results = new List<SyntaxRoleCalculator.Result>();
                foreach (ContextOverlayCandidate candidate in productive.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var route = (candidate.EvidenceCodes.Contains(ContextLanes.ProductiveGraphMismatch, StringComparer.Ordinal) ||
                        candidate.EvidenceCodes.Contains(ContextLanes.ProductiveAttributeMismatch, StringComparer.Ordinal))
                        ? SyntaxRoleCalculator.Route.ProductiveGraphMismatch : candidate.Selectable
                            ? SyntaxRoleCalculator.Route.ProductiveGraph : SyntaxRoleCalculator.Route.ProductiveGraphPreview;
                    var provenance = candidate.Roles.Select(role => new SyntaxRoleCalculator.Support(role.TokenIndex,
                        role.AlternativeIndices.ToImmutableArray(), byToken[role.TokenIndex].UsesGraph
                            ? SyntaxRoleCalculator.Basis.LicensedGraphMorphology : SyntaxRoleCalculator.Basis.TypedFeatures)).ToImmutableArray();
                    int predicateWord = -1, subjectWord = -1;
                    int subjectToken = candidate.Roles.Single(role => role.Role == ContextOverlayRole.Subject).TokenIndex;
                    for (int i = 0; i < words.Length; i++)
                    {
                        if (words[i].TokenIndex == candidate.PredicateTokenIndex) predicateWord = i;
                        if (words[i].TokenIndex == subjectToken) subjectWord = i;
                    }
                    results.Add(SyntaxRoleCalculator.Calculate(analysis.Text, sentence.Start, sentence.Length, words,
                        options.IncludeAdditionalRoleHeuristics, new(route, predicateWord, provenance)
                        { GraphSubjectWordIndex = subjectWord }, cancellationToken));
                }
                result = new(SyntaxRoleCalculator.Status.Calculated, words,
                    results.SelectMany(x => x.Candidates).ToImmutableArray(),
                    results.Count == 0 ? new(SyntaxRoleCalculator.Route.Abstained, -1, []) : results[0].Resolution);
            }
            else
            {
                // Unsupported graph topology retains the old exact-only source scan, not a broader graph parser.
                words = words.Select(word => word.UsesGraph ? word with { Projection = [], GraphTags = [] } : word).ToImmutableArray();
                SyntaxRoleCalculator.Route route = selected is null ? SyntaxRoleCalculator.Route.SourceScan :
                    selected.SelectedPredicateTokenIndex is not null ? SyntaxRoleCalculator.Route.Selected :
                    selected.DiagnosticPredicateTokenIndex is not null ? SyntaxRoleCalculator.Route.ClosedThirdPersonMismatchPreview :
                    SyntaxRoleCalculator.Route.Abstained;
                int? predicateToken = selected?.SelectedPredicateTokenIndex ?? selected?.DiagnosticPredicateTokenIndex;
                int predicateWord = -1;
                for (int i = 0; i < words.Length; i++) if (words[i].TokenIndex == predicateToken) predicateWord = i;
                var provenance = selected is null ? ImmutableArray<SyntaxRoleCalculator.Support>.Empty : selected.Roles
                    .Select(role => new SyntaxRoleCalculator.Support(role.TokenIndex, role.AlternativeIndices.ToImmutableArray(),
                        SyntaxRoleCalculator.Basis.TypedFeatures)).ToImmutableArray();
                // Keep source-scan Fusion hints, or the extent of the very relation promoted above.
                var fusionExtent = !matrixOwnedByR3 && route == SyntaxRoleCalculator.Route.SourceScan && options.IncludeAdditionalRoleHeuristics
                    ? fusion?.Selection : selectedFusion;
                int extentPred = matrixOwnedByR3 && route == SyntaxRoleCalculator.Route.Selected
                    ? r3!.SelectedPredicateWordIndex : fusionExtent?.PredicateWordIndex ?? -1;
                int extentSubjFirst = matrixOwnedByR3 && route == SyntaxRoleCalculator.Route.Selected
                    ? (r3!.SelectedSubjectFirstWordIndex >= 0
                        ? r3.SelectedSubjectFirstWordIndex
                        : options.IncludeAdditionalRoleHeuristics ? r3.HeuristicSvoSubjectFirstWordIndex : -1)
                    : fusionExtent?.SubjectFirstWordIndex ?? -1;
                int extentSubjHead = matrixOwnedByR3 && route == SyntaxRoleCalculator.Route.Selected
                    ? (r3!.SelectedSubjectHeadWordIndex >= 0
                        ? r3.SelectedSubjectHeadWordIndex
                        : options.IncludeAdditionalRoleHeuristics ? r3.HeuristicSvoSubjectHeadWordIndex : -1)
                    : fusionExtent?.SubjectWordIndex ?? -1;
                var resolution = new SyntaxRoleCalculator.Resolution(route, predicateWord, provenance)
                {
                    SelectedRoles = route == SyntaxRoleCalculator.Route.Selected ? selected!.Roles : [],
                    FusionPredicateWordIndex = extentPred,
                    FusionSubjectFirstWordIndex = extentSubjFirst,
                    FusionSubjectWordIndex = extentSubjHead,
                    SuppressSourceScanSubject = !matrixOwnedByR3 && route == SyntaxRoleCalculator.Route.SourceScan &&
                        fusionExtent is { SuppressHeuristicSubject: true }
                };
                result = SyntaxRoleCalculator.Calculate(analysis.Text, sentence.Start, sentence.Length,
                    words, options.IncludeAdditionalRoleHeuristics, resolution, cancellationToken);
            }
            TextSyntaxSupport MapSupport(SyntaxRoleCalculator.Support support) => new(support.TokenIndex,
                support.Basis.ToString(), References(support.TokenIndex, support.ReadingIndices))
                { GraphReadings = support.Basis == SyntaxRoleCalculator.Basis.LicensedGraphMorphology
                    ? GraphReferences(support.TokenIndex, support.ReadingIndices) : null };
            var roles = new List<TextSyntaxCandidate>();
            foreach (SyntaxRoleCalculator.Candidate candidate in result.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roles.Add(new((TextSyntaxRole)candidate.Role, candidate.Rule.ToString(), candidate.Start, candidate.Length,
                    Freeze(candidate.Targets.Select(MapSupport)), Freeze(candidate.Context.Select(MapSupport))));
                if (Group(candidate.Rule) is int group) groupCounts[group]++;
            }
            int? resultPredicate = roles.FirstOrDefault(candidate => candidate.Role == TextSyntaxRole.Predicate)
                is { Targets.Count: > 0 } main ? main.Targets[0].TokenIndex : null;
            syntaxSentences.Add(new(sentence.Start, sentence.Length, result.Status.ToString(), result.Resolution.Route.ToString(),
                resultPredicate, Freeze(roles)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (new("bounded-context.v2", true, null, Freeze(contextSentences)),
            new("bounded-syntax-roles.v2", true, null, Freeze(syntaxSentences), options.IncludeAdditionalRoleHeuristics ?
                Freeze(AdditionalGroupIds.Select((id, i) => new TextSyntaxGroupStatus(id, true, null, groupCounts[i]))) : null),
            Freeze(clauseMaps),
            articleTargetsBySentence);

        IReadOnlyList<TextReadingReference> References(int token, IEnumerable<int> members)
        {
            if (!byToken.TryGetValue(token, out Rows? bound)) throw new InvalidOperationException("Support token is absent.");
            if (bound.UsesGraph) return [];
            var refs = new List<TextReadingReference>();
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)member >= (uint)bound.Raw.Length) throw new InvalidOperationException("Support reading is outside complete inventory.");
                refs.Add(new(token, member));
            }
            return Freeze(refs);
        }
        IReadOnlyList<TextGraphReadingReference>? GraphReferences(int token, IEnumerable<int> members)
        {
            if (!byToken.TryGetValue(token, out Rows? bound) || !bound.UsesGraph) return null;
            var refs = new List<TextGraphReadingReference>();
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)member >= (uint)bound.Graph.Count) throw new InvalidOperationException("Graph support is outside original cells.");
                var cell = bound.Graph[member];
                refs.Add(new(token, cell.CandidateIndex, cell.FullTagIndex));
            }
            return refs.Count == 0 ? null : Freeze(refs);
        }
        string CanonicalKey(IReadOnlyList<ContextOverlayRoleSelection> roles) => string.Join('|', roles.Select(role =>
            RoleName(role.Role) + ":" + role.Start.ToString(CultureInfo.InvariantCulture) + ":" +
            string.Join(',', role.AlternativeIndices.Select(index => byToken[role.TokenIndex].Identities[index]))));
    }

    internal const string DeicticTemporalTenseRule = "grammar.tense.deictic_temporal_adverb.v1";

    // Existing graph routes and the explicit diagnostic-only exact lane share original relation binding.
    internal static IReadOnlyList<TextDiagnostic> GraphAgreementDiagnostics(TextContextEvidence context, CancellationToken ct,
        IReadOnlyList<TextDiagnostic>? existingDiagnostics = null)
    {
        var output = new List<TextDiagnostic>();
        foreach (TextContextSentence sentence in context.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var group in sentence.Candidates.Where(candidate =>
                candidate.AuthorityLane is ContextLanes.ProductiveGraphLane or ContextLanes.ProductiveNominalLane or ContextLanes.ExactAgreementLane or ContextLanes.ExactPronounPrefixLane or ContextLanes.SubordinatePronounPrefixLane or LocalClauseRoleLinker.Lane or ClauseFusionAnalyzer.AgreementLane &&
                candidate.EvidenceCodes.Any(code => code is ContextLanes.ProductiveGraphMismatch or ContextLanes.ProductiveAttributeMismatch
                    or "particle_participle_mismatch" or "compound_aux_participle_mismatch" or "compound_subj_participle_mismatch"
                    or "deictic_temporal_mismatch" ||
                    code.StartsWith("pronoun_case_token=", StringComparison.Ordinal)))
                .GroupBy(candidate => (candidate.PredicateTokenIndex, candidate.AuthorityLane,
                    Attribute: candidate.EvidenceCodes.FirstOrDefault(code => code.StartsWith("attribute_mismatch_token=", StringComparison.Ordinal)),
                    Special: candidate.EvidenceCodes.FirstOrDefault(code =>
                        code is "particle_participle_mismatch" or "compound_aux_participle_mismatch" or "compound_subj_participle_mismatch"
                            or "deictic_temporal_mismatch" ||
                        code.StartsWith("pronoun_case_token=", StringComparison.Ordinal)))))
            {
                TextContextRoleSelection[] roles = group.SelectMany(candidate => candidate.Roles).ToArray();
                bool particleParticiple = group.Key.Special == "particle_participle_mismatch";
                bool compoundAuxParticiple = group.Key.Special == "compound_aux_participle_mismatch";
                bool compoundSubjParticiple = group.Key.Special == "compound_subj_participle_mismatch";
                bool deicticTemporal = group.Key.Special == "deictic_temporal_mismatch";
                bool pronounCase = group.Key.Special?.StartsWith("pronoun_case_token=", StringComparison.Ordinal) == true;
                int pronounCaseToken = pronounCase
                    ? int.Parse(group.Key.Special!.AsSpan("pronoun_case_token=".Length), CultureInfo.InvariantCulture)
                    : -1;
                bool attributeMismatch = group.Key.Attribute is not null;
                int attributeToken = attributeMismatch ? int.Parse(group.Key.Attribute!.AsSpan("attribute_mismatch_token=".Length), CultureInfo.InvariantCulture) : -1;
                TextContextRoleSelection[] relation = particleParticiple || pronounCase || compoundAuxParticiple || compoundSubjParticiple || deicticTemporal
                    ? roles
                    : roles.Where(role => role.Role == TextContextRole.Subject ||
                        (attributeMismatch ? role.Role == TextContextRole.Attribute && role.TokenIndex == attributeToken : role.Role == TextContextRole.Predicate)).ToArray();
                bool prefix = ContextLanes.IsPronounPrefixLane(group.Key.AuthorityLane);
                bool clause = group.Key.AuthorityLane is LocalClauseRoleLinker.Lane or ClauseFusionAnalyzer.AgreementLane;
                bool exact = prefix || clause || group.Key.AuthorityLane == ContextLanes.ExactAgreementLane;
                TextContextRoleSelection? target = pronounCase
                    ? relation.FirstOrDefault(role => role.TokenIndex == pronounCaseToken)
                    : exact
                        ? relation.FirstOrDefault(role => attributeMismatch ? role.Role == TextContextRole.Attribute && role.TokenIndex == attributeToken
                            : role.Role == TextContextRole.Predicate)
                        : relation.Where(role => role.GraphReadings is { Count: > 0 }).OrderBy(role => role.Start).FirstOrDefault();
                if (target is null || exact && (target.Readings.Count == 0 || relation.Any(role => role.GraphReadings is { Count: > 0 })))
                    throw new InvalidOperationException("Agreement mismatch needs original support in its actual relation.");
                string diagnosticCode = particleParticiple
                    ? "particle_participle_verb_form_mismatch"
                    : pronounCase
                        ? "pronoun_case_governance_mismatch"
                        : deicticTemporal
                            ? "deictic_temporal_tense_mismatch"
                            : compoundAuxParticiple
                                ? NominalGrammarRules.AuxiliaryParticipleCode
                                : compoundSubjParticiple
                                    ? NominalGrammarRules.PredicativeCode
                                    : attributeMismatch
                                        ? ContextLanes.ProductiveAttributeMismatch
                                        : ContextLanes.ProductiveGraphMismatch;
                string ruleId = deicticTemporal ? DeicticTemporalTenseRule : group.Key.AuthorityLane!;
                if (exact && !attributeMismatch && !particleParticiple && !pronounCase && !compoundAuxParticiple && !compoundSubjParticiple && !deicticTemporal &&
                    existingDiagnostics is not null && existingDiagnostics.Any(old =>
                    old.Code == "pronoun_verb_agreement_mismatch" && old.Start == target.Start && old.Length == target.Length)) continue;
                if ((prefix || clause) && (output.Any(old => old.Code == diagnosticCode &&
                    old.Start == target.Start && old.Length == target.Length) || existingDiagnostics is not null &&
                    existingDiagnostics.Any(old => old.RuleId != ruleId && old.Code == diagnosticCode &&
                        old.Start == target.Start && old.Length == target.Length))) continue;
                output.Add(new(diagnosticCode,
                    target.Start, target.Length, ruleId)
                {
                    Readings = Freeze((clause ? roles : relation).SelectMany(role => role.Readings).Distinct()
                        .OrderBy(reference => reference.TokenIndex).ThenBy(reference => reference.CompleteReadingIndex)),
                    GraphReadings = exact ? null : Freeze(relation.SelectMany(role => role.GraphReadings ?? []).Distinct()
                        .OrderBy(reference => reference.TokenIndex).ThenBy(reference => reference.GraphCandidateIndex).ThenBy(reference => reference.FullTagIndex))
                });
            }
        }
        return Freeze(output);
    }

    internal const string LocalAttributeAgreementRule = NominalGrammarRules.AttributeId;

    // Local exact modifier/head evidence; no subject or predicate role is inferred.
    internal static IReadOnlyList<TextDiagnostic> LocalAttributeAgreementDiagnostics(TextAnalysis analysis,
        CancellationToken ct, MorphologyEvidenceProjection.View? sharedMorphology = null,
        List<NominalGrammarRules.AttributeMatch>? grammar = null, bool includeOpenNominal = false,
        IReadOnlyList<PredicativeGrammarEvidence.Frame>? predicativeFrames = null,
        List<TextGrammarMatch>? numeralGrammar = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ct.ThrowIfCancellationRequested();
        MorphologyEvidenceProjection.View morphology = sharedMorphology ?? MorphologyEvidenceProjection.Create(analysis, ct);
        if (morphology.UnavailableReason is not null) return [];
        var competingTails = (predicativeFrames ?? PredicativeGrammarEvidence.Bind(analysis, morphology, includeNominalReview: true, ct))
            .Select(frame => frame.CompetingTail).OfType<NominalStructuralProjection.Binding>()
            .Select(tail => (tail.Start, tail.Length)).ToHashSet();
        var protectedSpans = TextPredicateLinkAnalyzer.QuoteSpans(analysis.Text, ct, protectBackticks: true, protectBrackets: true);
        List<TextDiagnostic> output = [];
        int cursor = 0, previousEnd = 0, documentEnd = analysis.Text.AsSpan().TrimEnd().Length;
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            long end = (long)sentence.Start + sentence.Length;
            if (sentence.Start < previousEnd || sentence.Length < 0 || end > analysis.Text.Length)
                throw new ArgumentException("Local attribute sentences must be ordered original spans.", nameof(analysis));
            previousEnd = (int)end;
            while (cursor < morphology.OrderedTokens.Count &&
                analysis.Tokens[morphology.OrderedTokens[cursor].Index].Start < sentence.Start)
            { ct.ThrowIfCancellationRequested(); cursor++; }
            List<ContextLanes.TokenView> words = [];
            while (cursor < morphology.OrderedTokens.Count)
            {
                ct.ThrowIfCancellationRequested();
                var entry = morphology.OrderedTokens[cursor];
                TokenAnalysis token = analysis.Tokens[entry.Index];
                if (token.Start >= end) break;
                if ((long)token.Start + token.Length > end)
                    throw new ArgumentException("Local attribute Word straddles its original sentence.", nameof(analysis));
                Rows rows = entry.Rows;
                bool degreeGraph = rows.UsesGraph &&
                    rows.Canonical is not ("по-малко" or "най-малко" or "по-много" or "най-много") &&
                    (rows.Canonical.StartsWith("по-", StringComparison.Ordinal) ||
                     rows.Canonical.StartsWith("най-", StringComparison.Ordinal));
                words.Add(new(entry.Index, token, rows.Canonical, rows.UsesGraph && !degreeGraph ? [] : rows.Lanes,
                    !rows.UsesGraph || rows.GraphUnavailableReason is null)
                    { UsesGraph = rows.UsesGraph });
                cursor++;
            }
            if (words.Count == 0) continue;
            TokenAnalysis lastWord = words[^1].Token;
            bool terminal = TerminalTail(lastWord);
            int wordEnd = lastWord.Start + lastWord.Length;
            bool openScope = !terminal && includeOpenNominal &&
                analysis.Text.AsSpan(wordEnd, (int)end - wordEnd).Trim().IsEmpty;
            if (!terminal && !openScope && grammar is null) continue;
            bool TerminalTail(TokenAnalysis last)
            {
                ReadOnlySpan<char> tail = analysis.Text.AsSpan(last.Start + last.Length,
                    (int)end - last.Start - last.Length).Trim();
                bool terminator = false;
                foreach (char c in tail)
                {
                    ct.ThrowIfCancellationRequested();
                    if (SentenceBoundaryDetector.IsSentenceTerminator(c)) terminator = true;
                    else if (!SentenceBoundaryDetector.IsSentenceClosingWrapper(c) && c != '}') return false;
                }
                // A complete finite context can already support a local relation while the editor is still open.
                return terminator || tail.IsEmpty && end == documentEnd &&
                    words.Any(word => !word.UsesGraph && word.Complete && word.Readings.Any(row =>
                        row.Features.VerbForm == MorphologicalVerbForm.Finite &&
                        ContextEvidenceGuards.IsPersonalPredicate(row.Features)));
            }
            bool hasFinite = grammar is not null && words.Any(word => !word.UsesGraph &&
                word.Readings.Any(TextPredicateLinkAnalyzer.Finite));
            for (int head = words.Count - 1; head > 0; head--)
            {
                ct.ThrowIfCancellationRequested();
                var binding = NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, head, ct,
                    exactContiguous: true, allowDegreeGraph: true, allowPossessiveCliticBridge: true);
                if (binding is null || binding.Participants.Count < 2)
                {
                    if (grammar is not null && NominalStructuralProjection.BindHead(analysis.Text, sentence,
                        analysis.Tokens, words, head, ct) is { Participants.Count: > 1 } candidate)
                    {
                        bool incomplete = candidate.Participants.Any(member => !member.Word.Complete ||
                            member.Word.UsesGraph || member.RoleReadings.Count == 0);
                        grammar.Add(new(candidate, incomplete ? null : NominalStructuralProjection.Measure(candidate, ct),
                            incomplete ? "unmeasured_nominal_axes" : "competing_nominal_construction"));
                    }
                    continue;
                }
                head = binding.Participants[0].WordIndex;
                bool competing = competingTails.Contains((binding.Start, binding.Length)) ||
                    NominalStructuralProjection.AssessPartition(analysis.Text, words, head, binding.Head.WordIndex, ct) !=
                        NominalStructuralProjection.Partition.None ||
                    ClassifierCompoundHead(binding);
                bool catalogCompeting = (grammar is not null || openScope) && (competing || CoordinatedAlternative(binding));
                if (catalogCompeting) grammar?.Add(new(binding,
                    NominalStructuralProjection.Measure(binding, ct), "competing_nominal_construction"));
                if ((!terminal && (!openScope || catalogCompeting)) || competing)
                {
                    if (!catalogCompeting) grammar?.Add(new(binding, null, "insufficient_local_scope"));
                    continue;
                }
                // In a mixed wider context a standalone participle can be a predicate, and a comma-preceded
                // or post-copular indefinite present-active participle can head its own participial clause.
                bool initialIndefPresentActive = !binding.Participants[0].Word.UsesGraph &&
                    binding.Participants[0].Word.Readings.Any(row =>
                        ContextEvidenceGuards.IsPresentActiveAttributeShape(row.Features) &&
                        row.Features.Article is null or MorphologicalArticle.Indefinite);
                bool precededByCommaOrCopula = head > 0 &&
                    (analysis.Text.AsSpan(words[head - 1].Token.Start + words[head - 1].Token.Length,
                         binding.Start - words[head - 1].Token.Start - words[head - 1].Token.Length).Contains(',') ||
                     !words[head - 1].UsesGraph && words[head - 1].Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier));
                bool hasParticipialModifier = binding.Participants.Take(binding.Participants.Count - 1)
                    .Any(member => !member.Word.UsesGraph &&
                        member.RoleReadings.Any(i =>
                            (ContextEvidenceGuards.IsPassiveAttributeShape(member.Word.Readings[i].Features) ||
                             ContextEvidenceGuards.IsPresentActiveAttributeShape(member.Word.Readings[i].Features)) &&
                            member.Word.Readings[i].Features.Article is null or MorphologicalArticle.Indefinite) &&
                        !member.Word.Readings.Any(row => row.Category == ContextLanes.Category.Adjective));
                if (initialIndefPresentActive && precededByCommaOrCopula ||
                    hasParticipialModifier && !(head == 0 && binding.Head.WordIndex == words.Count - 1) &&
                    !(head > 0 && words[head - 1].OriginalIndex + 1 == binding.Participants[0].Word.OriginalIndex &&
                        analysis.Text.AsSpan(words[head - 1].Token.Start + words[head - 1].Token.Length,
                            binding.Start - words[head - 1].Token.Start - words[head - 1].Token.Length).Trim().IsEmpty &&
                        words[head - 1] is { Complete: true, UsesGraph: false } adposition && adposition.Readings.Count > 0 &&
                        adposition.Readings.All(row => row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Preposition,
                            HasConflictingFeatures: false, HasUnrecognizedFeatures: false })))
                {
                    if (!catalogCompeting) grammar?.Add(new(binding, null, "competing_participle_role"));
                    continue;
                }
                if (binding.Participants.Count == 2 && head > 0 && !binding.Participants[0].Word.UsesGraph &&
                    (binding.Participants[0].Word.CanonicalText.EndsWith("ски", StringComparison.Ordinal) ||
                     binding.Participants[0].Word.CanonicalText.EndsWith("жки", StringComparison.Ordinal) ||
                     binding.Participants[0].Word.CanonicalText.EndsWith("шки", StringComparison.Ordinal) ||
                     binding.Participants[0].Word.CanonicalText.EndsWith("чки", StringComparison.Ordinal)) &&
                    binding.Participants[0].RoleReadings.All(i => binding.Participants[0].Word.Readings[i].Features is
                        { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Article: MorphologicalArticle.Indefinite }) &&
                    words[head - 1].OriginalIndex + 1 == binding.Participants[0].Word.OriginalIndex &&
                    analysis.Text.AsSpan(words[head - 1].Token.Start + words[head - 1].Token.Length,
                        binding.Start - words[head - 1].Token.Start - words[head - 1].Token.Length).Trim().IsEmpty &&
                    !words[head - 1].UsesGraph &&
                    words[head - 1].Readings.Any(r => r.Features is
                        { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Finite,
                          DeclaredTransitivity: MorphologicalVerbTransitivity.Transitive } &&
                        !TextPredicateLinkAnalyzer.CopularLemma(r.Lemma)))
                {
                    if (!catalogCompeting) grammar?.Add(new(binding, null, "competing_adverbial_role"));
                    continue;
                }
                var measured = NominalStructuralProjection.Measure(binding, ct);
                if (grammar is not null && !catalogCompeting)
                {
                    bool governed = head > 0 && ContextEvidenceGuards.IsExactAdposition(words[head - 1]) &&
                        words[head - 1].OriginalIndex + 1 == binding.Participants[0].Word.OriginalIndex &&
                        analysis.Text.AsSpan(words[head - 1].Token.Start + words[head - 1].Token.Length,
                            binding.Start - words[head - 1].Token.Start - words[head - 1].Token.Length).Trim().IsEmpty;
                    grammar.Add(new(binding, measured, measured is null ? "unmeasured_nominal_axes" : null, governed, hasFinite));
                }
                if (measured is not { } phrase) continue;
                ContextLanes.TokenView noun = binding.Head.Word;
                for (int a = 0; a < phrase.AttributeMembers.Length; a++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (phrase.AnyAttributeMatch[a]) continue;
                    ContextLanes.TokenView target = binding.Participants[a].Word;
                    IReadOnlyList<TextGraphReadingReference>? graphRefs = null;
                    IEnumerable<TextReadingReference> exactRefs = phrase.NounMembers
                        .Select(r => new TextReadingReference(noun.OriginalIndex, r));
                    if (target.UsesGraph)
                    {
                        var targetCells = morphology.ByToken[target.OriginalIndex].Graph;
                        graphRefs = Freeze(phrase.AttributeMembers[a]
                            .Select(r => new TextGraphReadingReference(target.OriginalIndex, targetCells[r].CandidateIndex, targetCells[r].FullTagIndex))
                            .OrderBy(r => r.TokenIndex).ThenBy(r => r.GraphCandidateIndex).ThenBy(r => r.FullTagIndex));
                    }
                    else
                    {
                        exactRefs = phrase.AttributeMembers[a]
                            .Select(r => new TextReadingReference(target.OriginalIndex, r))
                            .Concat(exactRefs);
                    }
                    output.Add(new(ContextLanes.ProductiveAttributeMismatch, target.Token.Start,
                        target.Token.Length, LocalAttributeAgreementRule)
                    {
                        Readings = Freeze(exactRefs.OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex)),
                        GraphReadings = graphRefs
                    });
                }
            }
            if (terminal || openScope)
                AppendGenderedNumeralDiagnostics(analysis, sentence, morphology, words, protectedSpans, terminal, output, grammar, numeralGrammar, ct);
            bool ClassifierCompoundHead(NominalStructuralProjection.Binding binding)
            {
                int last = binding.Head.WordIndex;
                if (last + 1 >= words.Count) return false;
                ContextLanes.TokenView mid = binding.Head.Word, outerNoun = words[last + 1];
                if (outerNoun.OriginalIndex != mid.OriginalIndex + 1 || outerNoun.UsesGraph || !outerNoun.Complete ||
                    !analysis.Text.AsSpan(mid.Token.Start + mid.Token.Length,
                        outerNoun.Token.Start - mid.Token.Start - mid.Token.Length).Trim().IsEmpty ||
                    !mid.Readings.All(r => r.Features is
                        {
                            PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                            Number: MorphologicalNumber.Singular,
                            Article: MorphologicalArticle.Indefinite,
                            Gender: MorphologicalGender.Masculine or MorphologicalGender.Neuter
                        }) ||
                    !outerNoun.Readings.All(r => r.Category == ContextLanes.Category.Noun ||
                        ContextEvidenceGuards.IsSourcePlaceholder(outerNoun, r) ||
                        ContextEvidenceGuards.IsUnwrittenCapitalizedReading(outerNoun, r)))
                    return false;
                var outerWords = binding.Participants.Take(binding.Participants.Count - 1)
                    .Select(m => m.Word)
                    .Append(outerNoun)
                    .ToArray();
                return ContextLanes.NominalMembers(outerWords, outerWords.Length - 1, ct,
                    allowAdnominalReadings: true,
                    sentenceInitialFirst: binding.Participants[0].WordIndex == 0) is { } outerPhrase &&
                    outerPhrase.Heads.Any(h => h.Attributes.All(rows => rows.Length > 0));
            }
            bool CoordinatedAlternative(NominalStructuralProjection.Binding binding)
            {
                int last = binding.Head.WordIndex;
                if (NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, last, ct)
                    is { } wide && wide.Bridges.Any(bridge => bridge.Kind != NominalStructuralProjection.BridgeKind.Whitespace))
                    return true;

                if (last + 1 >= words.Count) return false;
                TokenAnalysis head = binding.Head.Word.Token;
                int next = last + 1;
                bool coordinator = words[next].Readings.Any(row => row.SourceConjunction);
                if (!coordinator && !analysis.Text.AsSpan(head.Start + head.Length,
                    words[next].Token.Start - head.Start - head.Length).Trim().SequenceEqual(",")) return false;
                if (coordinator) next++;
                return next < words.Count && words[next].Readings.Any(row =>
                    row.Category is ContextLanes.Category.Noun or ContextLanes.Category.Adjective);
            }
        }
        ct.ThrowIfCancellationRequested();
        return Freeze(output.OrderBy(d => d.Start));
    }

    private static void AppendGenderedNumeralDiagnostics(
        TextAnalysis analysis,
        SentenceSpan sentence,
        MorphologyEvidenceProjection.View morphology,
        IReadOnlyList<ContextLanes.TokenView> words,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        bool terminal,
        List<TextDiagnostic> output,
        List<NominalGrammarRules.AttributeMatch>? grammar,
        List<TextGrammarMatch>? numeralGrammar,
        CancellationToken ct)
    {
        bool Protected(int start, int end) => protectedSpans.Any(span => span.Start < end && span.End > start);
        bool HorizontalAdjacent(ContextLanes.TokenView left, ContextLanes.TokenView right) =>
            LocalClauseRoleLinker.Connected(analysis, left, right, symbols: false, ct) &&
            TextPredicateLinkAnalyzer.Horizontal(analysis.Text[(left.Token.Start + left.Token.Length)..right.Token.Start]);

        for (int numIdx = 0; numIdx < words.Count - 1; numIdx++)
        {
            ct.ThrowIfCancellationRequested();
            ContextLanes.TokenView numWord = words[numIdx];
            if (morphology.ByToken[numWord.OriginalIndex].GraphUnavailableReason is not null ||
                output.Any(d => d.Start == numWord.Token.Start && d.Length == numWord.Token.Length))
                continue;
            bool sentenceInitial = numIdx == 0 &&
                analysis.Text.AsSpan(sentence.Start, numWord.Token.Start - sentence.Start).Trim().IsEmpty;
            bool isPluralQuantifier = false;
            bool isInvariantCardinal = false;
            if (!ContextEvidenceGuards.TryGetGenderedNumeralReadings(numWord, sentenceInitial, ct, out int[] numRows, out bool masculineOnly) &&
                !TryGetPluralQuantifierReadings(numWord, sentenceInitial, out numRows, out isPluralQuantifier) &&
                !TryGetCardinalPluralNumeralReadings(numWord, sentenceInitial, out numRows, out isInvariantCardinal))
                continue;

            int prevTokIdx = numWord.OriginalIndex - 1;
            while (prevTokIdx >= 0 && analysis.Tokens[prevTokIdx].Kind == TokenKind.Whitespace) prevTokIdx--;
            if (prevTokIdx >= 0 && analysis.Tokens[prevTokIdx].Start >= sentence.Start &&
                (analysis.Tokens[prevTokIdx].Text.Any(char.IsDigit) || analysis.Tokens[prevTokIdx].Text == "№"))
                continue;
            if (numIdx > 0 && HorizontalAdjacent(words[numIdx - 1], numWord) &&
                numWord.CanonicalText is "два" or "две" &&
                words[numIdx - 1].Readings.Any(r => r.Category == ContextLanes.Category.Noun &&
                    r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }))
                continue;

            int modEnd = numIdx;
            while (modEnd + 1 < words.Count && HorizontalAdjacent(words[modEnd], words[modEnd + 1]) &&
                IsCleanPluralIndefiniteAdjective(words[modEnd + 1], morphology))
            {
                ct.ThrowIfCancellationRequested();
                modEnd++;
            }

            // H1b: [CardinalNumeral >= 2 | няколко] + [MismatchingAdjective] + [MasculineCountNoun] ("два широк стола").
            if (modEnd == numIdx && numIdx + 2 < words.Count &&
                HorizontalAdjacent(numWord, words[numIdx + 1]) &&
                HorizontalAdjacent(words[numIdx + 1], words[numIdx + 2]))
            {
                ContextLanes.TokenView adjWord = words[numIdx + 1];
                ContextLanes.TokenView countNounWord = words[numIdx + 2];
                bool coordTail = !terminal && numIdx + 3 < words.Count &&
                    (words[numIdx + 3].Readings.Any(r => r.SourceConjunction) ||
                     analysis.Text.AsSpan(countNounWord.Token.Start + countNounWord.Token.Length,
                         words[numIdx + 3].Token.Start - countNounWord.Token.Start - countNounWord.Token.Length).Trim().SequenceEqual(","));
                if (!coordTail &&
                    !Protected(numWord.Token.Start, countNounWord.Token.Start + countNounWord.Token.Length) &&
                    !output.Any(d => d.Start == adjWord.Token.Start && d.Length == adjWord.Token.Length) &&
                    TryGetNonPluralIndefiniteAdjectiveReadings(adjWord, morphology, out int[] adjRows) &&
                    TryGetCountFormNounReadings(countNounWord, morphology, isPluralQuantifier, isInvariantCardinal, masculineOnly, out int[] countNounRows))
                {
                    IReadOnlyList<TextReadingReference> cNumRefs = Freeze(numRows.Select(r => new TextReadingReference(numWord.OriginalIndex, r)));
                    IReadOnlyList<TextReadingReference> cAdjRefs = Freeze(adjRows.Select(r => new TextReadingReference(adjWord.OriginalIndex, r)));
                    IReadOnlyList<TextReadingReference> cNounRefs = Freeze(countNounRows.Select(r => new TextReadingReference(countNounWord.OriginalIndex, r)));
                    output.Add(new(ContextLanes.ProductiveAttributeMismatch, adjWord.Token.Start,
                        adjWord.Token.Length, LocalAttributeAgreementRule)
                    {
                        Readings = Freeze(cNumRefs.Concat(cAdjRefs).Concat(cNounRefs)
                            .OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex))
                    });
                    if (grammar is not null)
                    {
                        grammar.RemoveAll(m =>
                            m.Binding.Participants.Count == 2 &&
                            m.Binding.Participants[0].Word.OriginalIndex == adjWord.OriginalIndex &&
                            m.Binding.Head.Word.OriginalIndex == countNounWord.OriginalIndex);
                        var adjMember = new NominalStructuralProjection.Member(numIdx + 1, adjWord, Array.AsReadOnly(adjRows));
                        var nounMember = new NominalStructuralProjection.Member(numIdx + 2, countNounWord, Array.AsReadOnly(countNounRows));
                        var bridge = new NominalStructuralProjection.Bridge(
                            NominalStructuralProjection.BridgeKind.Whitespace,
                            adjWord.Token.Start + adjWord.Token.Length,
                            countNounWord.Token.Start - adjWord.Token.Start - adjWord.Token.Length,
                            null);
                        var countBinding = new NominalStructuralProjection.Binding(
                            adjWord.Token.Start,
                            countNounWord.Token.Start + countNounWord.Token.Length - adjWord.Token.Start,
                            Array.AsReadOnly(new[] { adjMember, nounMember }),
                            Array.AsReadOnly(new[] { bridge }));
                        grammar.Add(new(countBinding, null, "numeral_count_attribute_mismatch"));
                    }
                    continue;
                }
            }

            if (isInvariantCardinal) continue;
            if (modEnd + 1 >= words.Count || !HorizontalAdjacent(words[modEnd], words[modEnd + 1])) continue;
            ContextLanes.TokenView nounWord = words[modEnd + 1];
            if (Protected(numWord.Token.Start, nounWord.Token.Start + nounWord.Token.Length) ||
                nounWord.UsesGraph || morphology.ByToken[nounWord.OriginalIndex].GraphUnavailableReason is not null ||
                nounWord.Readings.Count == 0 || nounWord.CanonicalText == "души")
                continue;
            if (!terminal && modEnd + 2 < words.Count &&
                (words[modEnd + 2].Readings.Any(r => r.SourceConjunction) ||
                 analysis.Text.AsSpan(nounWord.Token.Start + nounWord.Token.Length,
                     words[modEnd + 2].Token.Start - nounWord.Token.Start - nounWord.Token.Length).Trim().SequenceEqual(",")))
                continue;

            bool hasNonPlaceholder = nounWord.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(nounWord, r));
            List<int> nounRows = [];
            bool cleanNoun = true;
            for (int r = 0; r < nounWord.Readings.Count; r++)
            {
                ContextLanes.ReadingView row = nounWord.Readings[r];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(nounWord, row) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(nounWord, row, sentenceInitial: false) ||
                    ContextEvidenceGuards.IsSyncreticVocativeCompanion(nounWord, row))
                    continue;
                MorphologicalFeatures nf = row.Features;
                if (row.Category != ContextLanes.Category.Noun ||
                    nf is not { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false, HasUnrecognizedFeatures: false } ||
                    nf.Gender is not (MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter) ||
                    nf.Number is not (MorphologicalNumber.Singular or MorphologicalNumber.Count or MorphologicalNumber.Plural))
                {
                    cleanNoun = false;
                    break;
                }
                nounRows.Add(r);
            }
            if (!cleanNoun || nounRows.Count == 0 ||
                !nounRows.Any(r => nounWord.Readings[r].Features.Article == MorphologicalArticle.Indefinite))
                continue;

            string firstLemma = BulgarianTextNormalizer.NormalizeKey(nounWord.Readings[nounRows[0]].Lemma);
            if (!isPluralQuantifier && masculineOnly && firstLemma is "стол" or "компютър" or "ученик" &&
                nounRows.All(r => BulgarianTextNormalizer.NormalizeKey(nounWord.Readings[r].Lemma) == firstLemma))
                continue;

            bool allMismatch = isPluralQuantifier
                ? nounRows.All(r => nounWord.Readings[r].Features is
                      { Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite } &&
                      !(nounWord.Readings[r].Features.Gender == MorphologicalGender.Neuter &&
                        (nounWord.CanonicalText.EndsWith('и') || nounWord.CanonicalText.EndsWith('у') || nounWord.CanonicalText.EndsWith('ю'))))
                : nounRows.All(r =>
                {
                    MorphologicalFeatures nf = nounWord.Readings[r].Features;
                    bool genderOk = masculineOnly
                        ? nf.Gender == MorphologicalGender.Masculine
                        : nf.Gender is MorphologicalGender.Feminine or MorphologicalGender.Neuter;
                    bool numberOk = !(nf.Number == MorphologicalNumber.Singular && nf.Article == MorphologicalArticle.Indefinite);
                    return !genderOk || !numberOk;
                });
            if (!allMismatch) continue;

            IReadOnlyList<TextReadingReference> numRefs = Freeze(numRows.Select(r => new TextReadingReference(numWord.OriginalIndex, r)));
            IReadOnlyList<TextReadingReference> nounRefs = Freeze(nounRows.Select(r => new TextReadingReference(nounWord.OriginalIndex, r)));
            output.Add(new(ContextLanes.ProductiveAttributeMismatch, numWord.Token.Start,
                numWord.Token.Length, LocalAttributeAgreementRule)
            {
                Readings = Freeze(numRefs.Concat(nounRefs).OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex))
            });
            numeralGrammar?.Add(new(
                NominalGrammarRules.AttributeId,
                numWord.Token.Start,
                nounWord.Token.Start + nounWord.Token.Length - numWord.Token.Start,
                Array.AsReadOnly(new[] { nounWord.OriginalIndex }),
                Freeze(words.Skip(numIdx).Take(modEnd - numIdx + 1).Select(w => w.OriginalIndex)),
                TextGrammarAssessment.Mismatch,
                Array.AsReadOnly(new[] { new TextGrammarConflict(numWord.OriginalIndex, numRefs, nounRefs) }),
                [],
                "no_verified_single_token_repair"));
        }

        static bool IsCleanPluralIndefiniteAdjective(ContextLanes.TokenView word, MorphologyEvidenceProjection.View morph)
        {
            if (word.UsesGraph || morph.ByToken[word.OriginalIndex].GraphUnavailableReason is not null || word.Readings.Count == 0)
                return false;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            int count = 0;
            foreach (ContextLanes.ReadingView row in word.Readings)
            {
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, row) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, row, sentenceInitial: false))
                    continue;
                if (row.Category != ContextLanes.Category.Adjective ||
                    row.Features is not { PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
                        Number: MorphologicalNumber.Plural, Article: MorphologicalArticle.Indefinite } ||
                    !ContextEvidenceGuards.HasNominalAxes(row.Features))
                    return false;
                count++;
            }
            return count > 0;
        }

        static bool TryGetPluralQuantifierReadings(
            ContextLanes.TokenView word, bool sentenceInitial, out int[] rows, out bool pluralQuantifier)
        {
            rows = [];
            pluralQuantifier = false;
            if (word.CanonicalText != "няколко" || !word.Complete || word.UsesGraph || word.Readings.Count == 0 ||
                word.Readings.Any(r => r.Features.HasConflictingFeatures))
                return false;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            List<int> selected = [];
            for (int i = 0; i < word.Readings.Count; i++)
            {
                ContextLanes.ReadingView row = word.Readings[i];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, row) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, row, sentenceInitial))
                    continue;
                if (BulgarianTextNormalizer.NormalizeKey(row.Lemma) != "няколко")
                    return false;
                selected.Add(i);
            }
            if (selected.Count == 0) return false;
            rows = selected.ToArray();
            pluralQuantifier = true;
            return true;
        }

        static bool TryGetCardinalPluralNumeralReadings(
            ContextLanes.TokenView word, bool sentenceInitial, out int[] rows, out bool invariantCardinal)
        {
            rows = [];
            invariantCardinal = false;
            if (word.CanonicalText is not ("три" or "четири" or "пет" or "шест" or "седем" or "осем" or "девет" or "десет") ||
                !word.Complete || word.UsesGraph || word.Readings.Count == 0 ||
                word.Readings.Any(r => r.Features.HasConflictingFeatures))
                return false;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            List<int> selected = [];
            for (int i = 0; i < word.Readings.Count; i++)
            {
                ContextLanes.ReadingView row = word.Readings[i];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, row) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, row, sentenceInitial))
                    continue;
                if (row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral)
                    selected.Add(i);
            }
            if (selected.Count == 0) return false;
            rows = selected.ToArray();
            invariantCardinal = true;
            return true;
        }

        static bool TryGetNonPluralIndefiniteAdjectiveReadings(
            ContextLanes.TokenView word, MorphologyEvidenceProjection.View morph, out int[] rows)
        {
            rows = [];
            if (!word.Complete || word.UsesGraph || morph.ByToken[word.OriginalIndex].GraphUnavailableReason is not null ||
                word.Readings.Count == 0)
                return false;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            List<int> selected = [];
            for (int i = 0; i < word.Readings.Count; i++)
            {
                ContextLanes.ReadingView row = word.Readings[i];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, row) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, row, sentenceInitial: false))
                    continue;
                if (row.Category != ContextLanes.Category.Adjective ||
                    row.Features.PartOfSpeech != MorphologicalPartOfSpeech.Adjective ||
                    !ContextEvidenceGuards.HasNominalAxes(row.Features) ||
                    row.Features is { Number: MorphologicalNumber.Plural, Article: MorphologicalArticle.Indefinite })
                    return false;
                selected.Add(i);
            }
            if (selected.Count == 0) return false;
            rows = selected.ToArray();
            return true;
        }

        static bool TryGetCountFormNounReadings(
            ContextLanes.TokenView nounWord, MorphologyEvidenceProjection.View morph,
            bool isPluralQuantifier, bool isInvariantCardinal, bool masculineOnly, out int[] countRows)
        {
            countRows = [];
            if (!nounWord.Complete || nounWord.UsesGraph ||
                morph.ByToken[nounWord.OriginalIndex].GraphUnavailableReason is not null ||
                nounWord.Readings.Count == 0 || nounWord.CanonicalText == "души")
                return false;
            if (!isPluralQuantifier && !isInvariantCardinal && !masculineOnly)
                return false;
            if (!isPluralQuantifier && !isInvariantCardinal &&
                nounWord.CanonicalText is not null &&
                nounWord.Readings.Any(r => r.Features.RawTag?.Contains("Мъжколична", StringComparison.Ordinal) == true))
                return false;

            bool hasNonPlaceholder = nounWord.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(nounWord, r));
            List<int> selectedCount = [];
            for (int i = 0; i < nounWord.Readings.Count; i++)
            {
                ContextLanes.ReadingView row = nounWord.Readings[i];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(nounWord, row) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(nounWord, row, sentenceInitial: false) ||
                    ContextEvidenceGuards.IsSyncreticVocativeCompanion(nounWord, row))
                    continue;
                MorphologicalFeatures nf = row.Features;
                if (row.Category != ContextLanes.Category.Noun ||
                    nf is not { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false, HasUnrecognizedFeatures: false })
                    return false;
                if (nf is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite })
                    selectedCount.Add(i);
            }
            if (selectedCount.Count == 0) return false;
            countRows = selectedCount.ToArray();
            return true;
        }
    }

    private static string RoleName(ContextOverlayRole role) => role switch
    {
        ContextOverlayRole.Adposition => "adposition", ContextOverlayRole.Attribute => "attribute",
        ContextOverlayRole.NominalContext => "nominal-context", ContextOverlayRole.Predicate => "predicate",
        ContextOverlayRole.Subject => "subject", _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static int? Group(SyntaxRoleCalculator.Rule rule) => rule switch
    {
        SyntaxRoleCalculator.Rule.UnknownAttributeShape => 0, SyntaxRoleCalculator.Rule.RegisterTitle => 1,
        SyntaxRoleCalculator.Rule.Zadneshkom or SyntaxRoleCalculator.Rule.KrivoLyavo or SyntaxRoleCalculator.Rule.KadeKade or
            SyntaxRoleCalculator.Rule.Directional or SyntaxRoleCalculator.Rule.Zamalko => 2,
        SyntaxRoleCalculator.Rule.NominalPossessive => 3,
        SyntaxRoleCalculator.Rule.FutureClitic or SyntaxRoleCalculator.Rule.DaClitic or SyntaxRoleCalculator.Rule.ReflexiveClitic or
            SyntaxRoleCalculator.Rule.ImmediateClitic => 4,
        SyntaxRoleCalculator.Rule.ImpersonalDoshya => 5, SyntaxRoleCalculator.Rule.DaSubpredicate => 6,
        SyntaxRoleCalculator.Rule.PostverbalChaka => 7, SyntaxRoleCalculator.Rule.DativeSpeech => 8, _ => null
    };
    private static (TextContextEvidence, TextSyntaxEvidence) Unavailable(string reason, TextContextOptions options) =>
        (new("bounded-context.v2", false, reason, []), new("bounded-syntax-roles.v2", false, reason, [],
            options.IncludeAdditionalRoleHeuristics ? Freeze(AdditionalGroupIds.Select(id => new TextSyntaxGroupStatus(id, false, reason, 0))) : null));
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
