using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BgLang.Contracts;
using Category = BgLang.Core.ContextLanes.Category;
using Guard = BgLang.Core.ContextEvidenceGuards;
using ReadingView = BgLang.Core.ContextLanes.ReadingView;
using TokenView = BgLang.Core.ContextLanes.TokenView;
using static BgLang.Core.SentenceClauseMap;
using static BgLang.Core.VerbalComplexBinder;

namespace BgLang.Core;

/// <summary>
/// Compact slot-filtered chunk and clause fusion for natural Bulgarian sentences.
/// Operates strictly over immutable complete-inventory reading indices without mutating ReadingInventory.
/// </summary>
internal static class ClauseFusionAnalyzer
{
    internal const string SelectionLane = "clause_fusion_selection.v1";
    internal const string AgreementLane = "clause_fusion_agreement.v1";
    internal const string SubjectArticleRule = "grammar.full_article.clause_subject.v1";

    internal enum ArticleExpectationKind { SubjectFull, DirectObjectShort }

    internal sealed record ArticleTarget(
        ArticleExpectationKind Kind,
        int CarrierTokenIndex,
        int[] PhraseWordIndices,
        int AnchorTokenIndex,
        int[] AnchorReadings);

    internal sealed record MatrixSelection(
        int PredicateWordIndex,
        int PredicateTokenIndex,
        int[] PredicateReadings,
        int SubjectFirstWordIndex,
        int SubjectWordIndex,
        int SubjectTokenIndex,
        int[] SubjectReadings,
        bool SuppressHeuristicSubject,
        ContextSelectionOverlay Overlay);

    internal sealed record Result(
        MatrixSelection? Selection,
        IReadOnlyList<ContextOverlayCandidate> AgreementCandidates,
        IReadOnlyList<ArticleTarget> ArticleTargets,
        SentenceClauseMap? ClauseMap = null);

    private sealed record NominalChunk(
        int FirstWord,
        int HeadWord,
        int LastWord,
        int[] HeadReadings,
        MorphologicalPerson Person,
        MorphologicalNumber? Number,
        MorphologicalGender? Gender,
        bool PronounSubject,
        bool FullMasculineSingular,
        bool ShortMasculineSingular,
        bool TemporalAdjunct,
        bool Coherent);

    internal delegate bool DirectObjectFrame(TokenView predicate, IReadOnlyList<int> finiteReadings);

    internal static Result Analyze(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        CancellationToken ct,
        VerbFrameCatalog? verbFrames = null,
        double postverbalDirectObjectMaxRatio = VerbFrameCatalog.DefaultPostverbalDirectObjectMaxRatio,
        DirectObjectFrame? directObjectFrame = null,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? lookup = null,
        SentenceClauseMap? sharedClauseMap = null,
        IReadOnlySet<int>? r3OwnedClauseIds = null)
    {
        ct.ThrowIfCancellationRequested();
        if (words.Count == 0) return new(null, [], [], null);

        SentenceClauseMap map = sharedClauseMap ?? SentenceClauseMap.Create(analysis, sentence, words, protectedSpans, ct, lookup);
        MatrixSelection? matrixSelection = null;
        List<ContextOverlayCandidate> agreement = [];
        List<ArticleTarget> articles = [];

        if (map.AllExactOrDegree)
        {
            bool matrixCaptured = false;
            foreach (ClauseSpan clause in map.Clauses)
            {
                ct.ThrowIfCancellationRequested();
                bool skipOwnedR3 = r3OwnedClauseIds?.Contains(clause.ClauseId) == true;
                AnalyzeClause(map, clause, !skipOwnedR3 && !matrixCaptured && clause.Matrix, skipOwnedR3,
                    canonicalKey, verbFrames, postverbalDirectObjectMaxRatio, directObjectFrame,
                    ref matrixSelection, agreement, articles, ct);
                if (clause.Matrix && matrixSelection is not null)
                    matrixCaptured = true;
            }
            if (!map.HasUnexpectedSymbol)
            {
                ScanSharedSubjectVpCoordinationMismatches(map, canonicalKey, agreement, r3OwnedClauseIds, ct);
            }
        }

        if (!map.HasUnexpectedSymbol)
        {
            EmitVerbalInternalMismatches(map, CheckInternalWellFormedness(map, ct), canonicalKey, agreement);
            if (map.AllExactOrDegree && map.HasTerminalPunctuation &&
                !analysis.Text.AsSpan(sentence.Start, sentence.Length).ContainsAny(":;\u2014"))
            {
                ScanClauseDomainVerbFormIntrusions(map, canonicalKey, agreement, ct);
                ScanVerblessCliticPredicativeIntrusions(map, canonicalKey, agreement, ct);
            }
            ScanNominalChunkAgreementMismatches(map, canonicalKey, agreement, ct);
            ScanPrepositionCaseMismatches(map, canonicalKey, agreement, ct);
            ScanVocativeIntrusions(map, canonicalKey, agreement, ct);
            ScanRelativePronounAntecedentMismatches(map, canonicalKey, agreement, ct);
            RemoveCascadingRelativeSubjectMismatches(agreement);
            ScanDeicticTemporalTenseMismatches(map, canonicalKey, agreement, ct);
        }

        return new(matrixSelection, agreement, articles, map);
    }

    private static void RemoveCascadingRelativeSubjectMismatches(List<ContextOverlayCandidate> agreement)
    {
        HashSet<int>? mismatchedRelTokens = null;
        foreach (ContextOverlayCandidate cand in agreement)
        {
            if (!cand.EvidenceCodes.Any(c => c.StartsWith("pronoun_case_token=", StringComparison.Ordinal)))
                continue;
            foreach (ContextOverlayRoleSelection role in cand.Roles)
            {
                if (role.EvidenceCodes.Contains("clause_fusion_relative_pronoun") &&
                    cand.EvidenceCodes.Contains("pronoun_case_token=" + role.TokenIndex.ToString(CultureInfo.InvariantCulture)))
                {
                    (mismatchedRelTokens ??= []).Add(role.TokenIndex);
                }
            }
        }
        if (mismatchedRelTokens is not null)
        {
            agreement.RemoveAll(cand =>
                cand.EvidenceCodes.Contains("nominal_subject_clause_mismatch") &&
                cand.Roles.Any(r => r.Role == ContextOverlayRole.Subject && mismatchedRelTokens.Contains(r.TokenIndex)));
        }
    }

    internal static IEnumerable<TextGrammarMatch> ExplainGrammar(
        TextAnalysis analysis,
        TextContextEvidence context,
        IEnumerable<TextGrammarMatch>? existingMatches = null)
    {
        HashSet<int> coveredPredicates = [];
        HashSet<int> coveredAttrDependents = [];
        if (existingMatches is not null)
        {
            foreach (TextGrammarMatch existing in existingMatches)
            {
                if (existing.Assessment == TextGrammarAssessment.Mismatch &&
                    existing.RuleId is NominalGrammarRules.CopularFiniteId
                        or NominalGrammarRules.AnalyticFiniteId
                        or NominalGrammarRules.ActiveControllerId
                        or NominalGrammarRules.PassiveParticipleId
                        or NominalGrammarRules.CopularAdjectiveId
                        or NominalGrammarRules.AuxiliaryParticipleId
                        or NominalGrammarRules.SubjectPredicateId)
                {
                    foreach (int dep in existing.DependentTokenIndices)
                        coveredPredicates.Add(dep);
                }
                else if (existing.Assessment == TextGrammarAssessment.Mismatch &&
                         existing.RuleId == NominalGrammarRules.AttributeId)
                {
                    foreach (int dep in existing.DependentTokenIndices)
                        coveredAttrDependents.Add(dep);
                }
            }
        }

        if (context.Available)
        {
            foreach (TextContextSentence sentence in context.Sentences)
            {
                foreach (TextContextCandidate candidate in sentence.Candidates.Where(c => c.AuthorityLane == AgreementLane &&
                    c.EvidenceCodes.Contains(ContextLanes.ProductiveAttributeMismatch) &&
                    c.EvidenceCodes.Any(code => code.StartsWith("attribute_mismatch_token=", StringComparison.Ordinal))))
                    if (TryProjectNominalAttributeMismatch(analysis, candidate, coveredAttrDependents) is { } attrMatch)
                        yield return attrMatch;

                foreach (TextContextCandidate candidate in sentence.Candidates.Where(c => c.AuthorityLane == AgreementLane &&
                    c.EvidenceCodes.Contains("compound_aux_participle_mismatch")))
                    if (TryProjectAuxiliaryParticipleMismatch(analysis, candidate, coveredPredicates) is { } auxPart)
                        yield return auxPart;

                foreach (TextContextCandidate candidate in sentence.Candidates.Where(c => c.AuthorityLane == AgreementLane &&
                    c.EvidenceCodes.Contains("compound_subj_participle_mismatch")))
                    if (TryProjectActiveParticipleControllerMismatch(analysis, candidate, coveredPredicates) is { } subjPart)
                        yield return subjPart;

                foreach (TextContextCandidate candidate in sentence.Candidates.Where(c => c.AuthorityLane == AgreementLane &&
                    c.EvidenceCodes.Contains(ContextLanes.ProductiveGraphMismatch)))
                    if (TryProjectSubjectPredicateMismatch(analysis, candidate, coveredPredicates) is { } primary)
                        yield return primary;

                foreach (TextContextCandidate candidate in sentence.Candidates.Where(c => c.AuthorityLane == AgreementLane &&
                    c.EvidenceCodes.Contains(SimpleClauseAgreementResolver.DisjointAmbiguousMismatchCode)))
                    if (TryProjectDisjointAmbiguousSubjectPredicate(analysis, candidate, coveredPredicates) is { } ambig)
                        yield return ambig;

                foreach (TextContextCandidate candidate in sentence.Candidates.Where(IsExactSubjectPredicateFallbackLane))
                    if (TryProjectSubjectPredicateMismatch(analysis, candidate, coveredPredicates) is { } fallback)
                        yield return fallback;
            }
        }

        if (analysis.ReadingInventory is { } inventory)
        {
            Dictionary<int, TextReadingOccurrence>? occByToken = null;
            foreach (TextDiagnostic diagnostic in analysis.Diagnostics)
            {
                if (diagnostic.Code != "pronoun_verb_agreement_mismatch")
                    continue;
                int predIdx = -1;
                for (int i = 0; i < analysis.Tokens.Count; i++)
                {
                    if (analysis.Tokens[i] is { Kind: TokenKind.Word } tok && tok.Start == diagnostic.Start && tok.Length == diagnostic.Length)
                    {
                        predIdx = i;
                        break;
                    }
                }
                if (predIdx < 0 || coveredPredicates.Contains(predIdx)) continue;

                int subjIdx = -1;
                foreach (SentenceSpan span in analysis.Sentences)
                {
                    if (diagnostic.Start < span.Start || diagnostic.Start >= span.Start + span.Length)
                        continue;
                    for (int i = 0; i < predIdx; i++)
                    {
                        TokenAnalysis t = analysis.Tokens[i];
                        if (t.Kind == TokenKind.Word && t.Start >= span.Start && t.Start + t.Length <= span.Start + span.Length)
                        {
                            subjIdx = i;
                            break;
                        }
                    }
                    break;
                }
                occByToken ??= inventory.Occurrences.ToDictionary(o => o.TokenIndex);
                if (subjIdx < 0 ||
                    !occByToken.TryGetValue(subjIdx, out TextReadingOccurrence subjOcc) ||
                    !occByToken.TryGetValue(predIdx, out TextReadingOccurrence predOcc))
                    continue;

                IReadOnlyList<TextReadingSnapshot> subjRows = inventory.Sets[subjOcc.ReadingSetIndex].ResolvedReadings;
                IReadOnlyList<TextReadingSnapshot> predRows = inventory.Sets[predOcc.ReadingSetIndex].ResolvedReadings;
                TextReadingReference[] expected = subjRows
                    .Select((row, idx) => (row, idx))
                    .Where(x => x.row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.PersonalPronoun, Person: not null, Number: not null })
                    .Select(x => new TextReadingReference(subjIdx, x.idx))
                    .ToArray();
                TextReadingReference[] actual = predRows
                    .Select((row, idx) => (row, idx))
                    .Where(x => x.row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Finite, Person: not null, Number: not null })
                    .Select(x => new TextReadingReference(predIdx, x.idx))
                    .ToArray();
                if (expected.Length == 0 || actual.Length == 0 || !coveredPredicates.Add(predIdx))
                    continue;

                TokenAnalysis firstTok = analysis.Tokens[subjIdx], lastTok = analysis.Tokens[predIdx];
                yield return new TextGrammarMatch(
                    NominalGrammarRules.SubjectPredicateId,
                    firstTok.Start,
                    lastTok.Start + lastTok.Length - firstTok.Start,
                    Array.AsReadOnly(new[] { subjIdx }),
                    Array.AsReadOnly(new[] { predIdx }),
                    TextGrammarAssessment.Mismatch,
                    Array.AsReadOnly(new[] { new TextGrammarConflict(predIdx, Array.AsReadOnly(actual), Array.AsReadOnly(expected)) }),
                    [],
                    "no_verified_single_token_repair");
            }
        }

        static bool IsExactSubjectPredicateFallbackLane(TextContextCandidate candidate) =>
            candidate.AuthorityLane is ContextLanes.ExactAgreementLane or ContextLanes.ExactPronounPrefixLane
                or ContextLanes.SubordinatePronounPrefixLane or LocalClauseRoleLinker.Lane &&
            candidate.EvidenceCodes.Contains(ContextLanes.ProductiveGraphMismatch) &&
            !candidate.EvidenceCodes.Any(code => code.StartsWith("attribute_mismatch_token=", StringComparison.Ordinal));

        static TextGrammarMatch? TryProjectAuxiliaryParticipleMismatch(
            TextAnalysis analysis, TextContextCandidate candidate, HashSet<int> covered)
        {
            TextContextRoleSelection? pred = candidate.Roles.FirstOrDefault(r => r.Role == TextContextRole.Predicate);
            TextContextRoleSelection? aux = candidate.Roles.FirstOrDefault(r =>
                r.Role == TextContextRole.NominalContext && r.EvidenceCodes.Contains("clause_fusion_auxiliary_readings"));
            if (pred is null || aux is null || pred.Readings.Count == 0 || aux.Readings.Count == 0 ||
                pred.GraphReadings is { Count: > 0 } || aux.GraphReadings is { Count: > 0 } ||
                !covered.Add(pred.TokenIndex))
                return null;
            int minTok = Math.Min(aux.TokenIndex, pred.TokenIndex);
            int maxTok = Math.Max(aux.TokenIndex, pred.TokenIndex);
            TokenAnalysis firstTok = analysis.Tokens[minTok], lastTok = analysis.Tokens[maxTok];
            int start = firstTok.Start, end = lastTok.Start + lastTok.Length;
            return new TextGrammarMatch(
                NominalGrammarRules.AuxiliaryParticipleId,
                start,
                end - start,
                Array.AsReadOnly(new[] { aux.TokenIndex }),
                Array.AsReadOnly(new[] { pred.TokenIndex }),
                TextGrammarAssessment.Mismatch,
                Array.AsReadOnly(new[] { new TextGrammarConflict(pred.TokenIndex, pred.Readings, aux.Readings) }),
                [],
                "no_verified_single_token_repair");
        }

        static TextGrammarMatch? TryProjectActiveParticipleControllerMismatch(
            TextAnalysis analysis, TextContextCandidate candidate, HashSet<int> covered)
        {
            TextContextRoleSelection? pred = candidate.Roles.FirstOrDefault(r => r.Role == TextContextRole.Predicate);
            TextContextRoleSelection[] subjs = candidate.Roles.Where(r => r.Role == TextContextRole.Subject).ToArray();
            TextContextRoleSelection? aux = candidate.Roles.FirstOrDefault(r =>
                r.Role == TextContextRole.NominalContext &&
                (r.EvidenceCodes.Contains("clause_fusion_auxiliary_readings") ||
                 r.EvidenceCodes.Contains("clause_fusion_copular_carrier")));
            if (pred is null || subjs.Length == 0 || pred.Readings.Count == 0 || pred.GraphReadings is { Count: > 0 } ||
                subjs.Any(s => s.Readings.Count == 0 || s.GraphReadings is { Count: > 0 }) ||
                !covered.Add(pred.TokenIndex))
                return null;
            int[] controllers = candidate.Roles
                .Where(r => r.Role is TextContextRole.Subject or TextContextRole.Attribute)
                .Select(r => r.TokenIndex)
                .Distinct()
                .OrderBy(idx => idx)
                .ToArray();
            int minTok = Math.Min(Math.Min(controllers[0], pred.TokenIndex), aux?.TokenIndex ?? pred.TokenIndex);
            int maxTok = Math.Max(Math.Max(controllers[^1], pred.TokenIndex), aux?.TokenIndex ?? pred.TokenIndex);
            TokenAnalysis firstTok = analysis.Tokens[minTok], lastTok = analysis.Tokens[maxTok];
            int start = firstTok.Start, end = lastTok.Start + lastTok.Length;
            TextReadingReference[] expectedRefs = subjs.SelectMany(s => s.Readings).ToArray();
            string grammarRuleId = pred.EvidenceCodes.Contains("clause_fusion_predicative_adjective")
                ? NominalGrammarRules.CopularAdjectiveId
                : pred.EvidenceCodes.Contains("clause_fusion_passive_participle")
                    ? NominalGrammarRules.PassiveParticipleId
                    : NominalGrammarRules.ActiveControllerId;
            return new TextGrammarMatch(
                grammarRuleId,
                start,
                end - start,
                Array.AsReadOnly(controllers),
                Array.AsReadOnly(new[] { pred.TokenIndex }),
                TextGrammarAssessment.Mismatch,
                Array.AsReadOnly(new[] { new TextGrammarConflict(pred.TokenIndex, pred.Readings, Array.AsReadOnly(expectedRefs)) }),
                [],
                "no_verified_single_token_repair");
        }

        static TextGrammarMatch? TryProjectNominalAttributeMismatch(
            TextAnalysis analysis, TextContextCandidate candidate, HashSet<int> coveredAttrDeps)
        {
            TextContextRoleSelection? attrRole = candidate.Roles.FirstOrDefault(r =>
                r.Role == TextContextRole.Attribute && r.EvidenceCodes.Contains("clause_fusion_nominal_modifier"));
            TextContextRoleSelection? headRole = candidate.Roles.FirstOrDefault(r =>
                r.Role == TextContextRole.NominalContext && r.EvidenceCodes.Contains("clause_fusion_nominal_head"));
            if (attrRole is null || headRole is null || attrRole.Readings.Count == 0 || headRole.Readings.Count == 0 ||
                attrRole.GraphReadings is { Count: > 0 } || headRole.GraphReadings is { Count: > 0 } ||
                !coveredAttrDeps.Add(attrRole.TokenIndex))
                return null;
            int minTok = Math.Min(attrRole.TokenIndex, headRole.TokenIndex);
            int maxTok = Math.Max(attrRole.TokenIndex, headRole.TokenIndex);
            TokenAnalysis firstTok = analysis.Tokens[minTok], lastTok = analysis.Tokens[maxTok];
            int start = firstTok.Start, end = lastTok.Start + lastTok.Length;
            return new TextGrammarMatch(
                NominalGrammarRules.AttributeId,
                start,
                end - start,
                Array.AsReadOnly(new[] { headRole.TokenIndex }),
                Array.AsReadOnly(new[] { attrRole.TokenIndex }),
                TextGrammarAssessment.Mismatch,
                Array.AsReadOnly(new[] { new TextGrammarConflict(attrRole.TokenIndex, attrRole.Readings, headRole.Readings) }),
                [],
                "no_verified_single_token_repair");
        }

        static TextGrammarMatch? TryProjectSubjectPredicateMismatch(
            TextAnalysis analysis, TextContextCandidate candidate, HashSet<int> covered)
        {
            TextContextRoleSelection? pred = candidate.Roles.FirstOrDefault(r => r.Role == TextContextRole.Predicate);
            TextContextRoleSelection[] subjs = candidate.Roles.Where(r => r.Role == TextContextRole.Subject).ToArray();
            if (pred is null || subjs.Length == 0 || pred.Readings.Count == 0 || pred.GraphReadings is { Count: > 0 } ||
                subjs.Any(s => s.Readings.Count == 0 || s.GraphReadings is { Count: > 0 }) ||
                !covered.Add(pred.TokenIndex))
                return null;
            int[] controllers = candidate.Roles
                .Where(r => r.Role is TextContextRole.Subject or TextContextRole.Attribute ||
                    (r.Role == TextContextRole.NominalContext && r.EvidenceCodes.Contains("coordinated_subject_conjunction")))
                .Select(r => r.TokenIndex)
                .Distinct()
                .OrderBy(idx => idx)
                .ToArray();
            int minTok = Math.Min(controllers[0], pred.TokenIndex);
            int maxTok = Math.Max(controllers[^1], pred.TokenIndex);
            TokenAnalysis firstTok = analysis.Tokens[minTok], lastTok = analysis.Tokens[maxTok];
            int start = firstTok.Start, end = lastTok.Start + lastTok.Length;
            TextReadingReference[] expectedRefs = subjs.SelectMany(s => s.Readings).ToArray();
            string reason = candidate.EvidenceCodes.Contains("coordinated_subject_clause_mismatch")
                ? "coordinated_subject_clause_mismatch"
                : "no_verified_single_token_repair";
            return new TextGrammarMatch(
                NominalGrammarRules.SubjectPredicateId,
                start,
                end - start,
                Array.AsReadOnly(controllers),
                Array.AsReadOnly(new[] { pred.TokenIndex }),
                TextGrammarAssessment.Mismatch,
                Array.AsReadOnly(new[] { new TextGrammarConflict(pred.TokenIndex, pred.Readings, Array.AsReadOnly(expectedRefs)) }),
                [],
                reason);
        }

        static TextGrammarMatch? TryProjectDisjointAmbiguousSubjectPredicate(
            TextAnalysis analysis, TextContextCandidate candidate, HashSet<int> covered)
        {
            TextContextRoleSelection? pred = candidate.Roles.FirstOrDefault(r => r.Role == TextContextRole.Predicate);
            TextContextRoleSelection[] subjs = candidate.Roles.Where(r => r.Role == TextContextRole.Subject).ToArray();
            if (pred is null || subjs.Length < 2 || pred.Readings.Count == 0 || pred.GraphReadings is { Count: > 0 } ||
                subjs.Any(s => s.Readings.Count == 0 || s.GraphReadings is { Count: > 0 }) ||
                !covered.Add(pred.TokenIndex))
                return null;
            int[] controllers = subjs.Select(s => s.TokenIndex).Distinct().OrderBy(idx => idx).ToArray();
            int minTok = Math.Min(controllers[0], pred.TokenIndex);
            int maxTok = Math.Max(controllers[^1], pred.TokenIndex);
            TokenAnalysis firstTok = analysis.Tokens[minTok], lastTok = analysis.Tokens[maxTok];
            int start = firstTok.Start, end = lastTok.Start + lastTok.Length;
            TextGrammarConflict[] conflicts = subjs
                .Select(s => new TextGrammarConflict(pred.TokenIndex, pred.Readings, s.Readings))
                .ToArray();
            return new TextGrammarMatch(
                NominalGrammarRules.SubjectPredicateId,
                start,
                end - start,
                Array.AsReadOnly(controllers),
                Array.AsReadOnly(new[] { pred.TokenIndex }),
                TextGrammarAssessment.Ambiguous,
                Array.AsReadOnly(conflicts),
                [],
                "disjoint_subject_predicate_locus");
        }
    }

    internal static IReadOnlyList<ArticleTarget> CollectArticleTargets(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        CancellationToken ct,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? lookup = null,
        SentenceClauseMap? sharedClauseMap = null) =>
        Analyze(analysis, sentence, words, protectedSpans, static _ => string.Empty, ct, lookup: lookup, sharedClauseMap: sharedClauseMap).ArticleTargets;

    private static void AnalyzeClause(
        SentenceClauseMap map,
        ClauseSpan clause,
        bool captureMatrix,
        bool skipOwnedR3,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        VerbFrameCatalog? verbFrames,
        double postverbalDirectObjectMaxRatio,
        DirectObjectFrame? directObjectFrame,
        ref MatrixSelection? matrixSelection,
        List<ContextOverlayCandidate> agreement,
        List<ArticleTarget> articles,
        CancellationToken ct)
    {
        TextAnalysis analysis = map.Analysis;
        WordSlot[] slots = map.Slots;
        int[] idx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        if (bodyStart >= idx.Length) return;

        // Bind primary finite VerbalComplex (before any unconsumed "да" complement sub-clause).
        VerbalComplex? verb = BindClauseMainComplex(map, clause, ct);
        if (verb is null)
        {
            if (idx.All(i => !slots[i].Quoted) &&
                !HasUnexpectedSymbol(analysis, slots[idx[0]].Word.OriginalIndex, slots[idx[^1]].Word.OriginalIndex, map.ProtectedSpans))
            {
                CollectBareParticipleClauseArticleTargets(map, clause, articles, ct);
            }
            return;
        }
        bool allowsPerfPres = verb.AllowsPerfectivePresent(map, clause);
        if (!allowsPerfPres)
            verb = verb with { CarrierFiniteReadings = FilterIndicativeFinite(slots[verb.FiniteCarrierWordIndex].Word, verb.CarrierFiniteReadings) };

        int verbFirstPos = Array.IndexOf(idx, verb.FirstWordIndex);
        int verbLastPos = Array.IndexOf(idx, verb.LastWordIndex);

        // Truncate postverbal domain at "да" subpredicate boundary so complement arguments belong to the subpredicate.
        int postEndPos = idx.Length - 1;
        for (int p = verbLastPos + 1; p < idx.Length; p++)
            if (slots[idx[p]].Word.CanonicalText == "да")
            {
                postEndPos = p - 1;
                break;
            }

        // Mark governed prepositional phrases in preverbal and postverbal domains.
        bool[] governed = MarkGovernedPhrases(map, idx, bodyStart, postEndPos, verbFirstPos, verbLastPos, ct);

        List<NominalChunk> preNps = CollectNominalChunks(map, idx, bodyStart, verbFirstPos - 1, governed, ct);
        List<NominalChunk> postNps = CollectNominalChunks(map, idx, verbLastPos + 1, postEndPos, governed, ct);

        bool quotedClause = slots[verb.FiniteCarrierWordIndex].Quoted || slots[verb.LexicalHeadWordIndex].Quoted;
        bool hasSymbolGap = HasUnexpectedSymbol(analysis, slots[idx[0]].Word.OriginalIndex, slots[idx[^1]].Word.OriginalIndex, map.ProtectedSpans) ||
            HasPictographBrokenPreverbalClitic(analysis, slots, idx, bodyStart, verbFirstPos - 1, governed, preNps);

        // Identify candidate preverbal or postverbal subject for matrix selection.
        NominalChunk[] nonTemporalPreNps = preNps.Where(np => !np.TemporalAdjunct).ToArray();
        NominalChunk? preSubjectCand = nonTemporalPreNps.Length == 1 ? nonTemporalPreNps[0] : null;
        bool preCommaBlocked = preSubjectCand is not null &&
            map.HasDirectCommaBetween(idx, Array.IndexOf(idx, preSubjectCand.LastWord), verbFirstPos);

        // Coordinated preverbal singular NPs ("Учителя и лекарят дойдоха"): leave existing coordinated-subject lane intact.
        bool coordinatedPreSubject = HasCoordinatedPreverbalNominals(map, idx, bodyStart, verbFirstPos - 1, governed, ct);

        if (skipOwnedR3)
        {
            if (!quotedClause && !idx.Any(i => slots[i].Quoted) && !hasSymbolGap)
            {
                if (!coordinatedPreSubject)
                    CollectClauseArticleTargets(map, clause, preSubjectCand, preCommaBlocked, verb, postNps, governed, articles, ct);
                else
                    CollectCoordinatedSubjectArticleTargets(map, clause, verbFirstPos, nonTemporalPreNps, verb, articles, ct);
            }
            return;
        }

        WordSlot predicate = slots[verb.FiniteCarrierWordIndex];
        NominalChunk? postSubject = null;
        int[] postFinite = [];
        bool postRoleProven = false;
        bool passivePatient = false;
        NominalChunk? selectedSubject = null;
        int[] compatibleVerbReadings = verb.CarrierFiniteReadings;

        if (!coordinatedPreSubject && !preCommaBlocked && preSubjectCand is not null &&
            !IsCliticResumedObject(slots, preSubjectCand, verb) &&
            !IsCliticResumedFrontedFullObject(slots, preSubjectCand, verb, postNps) &&
            !verb.ImpersonalModal)
        {
            int[] matchingVerb = MatchingVerbReadings(predicate, verb.CarrierFiniteReadings, preSubjectCand);
            if (matchingVerb.Length > 0 && preSubjectCand.Coherent &&
                (!preSubjectCand.ShortMasculineSingular || IsSobriquetSubject(slots, preSubjectCand)))
            {
                selectedSubject = preSubjectCand;
                compatibleVerbReadings = matchingVerb;
            }
        }
        else if (!coordinatedPreSubject && nonTemporalPreNps.Length == 0 && !verb.ImpersonalModal &&
                 !Enumerable.Range(bodyStart, Math.Max(0, verbFirstPos - bodyStart))
                     .Any(p => !governed[p] && slots[idx[p]].Quoted && !map.HasDirectCommaBetween(idx, p, verbFirstPos)))
        {
            NominalChunk[] ungoverned = postNps.Where(np => !np.TemporalAdjunct).ToArray();
            if (!quotedClause && !hasSymbolGap && !verb.CopularCarrier &&
                (verb.LastWordIndex == verb.FiniteCarrierWordIndex || verb.PostposedSe) &&
                verb.CarrierAccusativeCliticWordIndex < 0 &&
                slots[idx[bodyStart]].Word.CanonicalText != "се" &&
                ungoverned is [var nominal] && !nominal.ShortMasculineSingular &&
                !Enumerable.Range(verbLastPos + 1, Math.Max(0, postEndPos - verbLastPos))
                    .Any(p => !governed[p] && slots[idx[p]].Word.CanonicalText is "и" or "или") &&
                !(nominal.PronounSubject && nominal.LastWord == idx[postEndPos] &&
                  postEndPos + 1 < idx.Length && slots[idx[postEndPos + 1]].Word.CanonicalText == "да") &&
                !IsAdverbialTimeOrMeasureHead(slots[nominal.HeadWord]) && !HasVocativeReading(slots[nominal.HeadWord]) &&
                !IsPersonPlaceHomograph(analysis, slots[nominal.HeadWord].Word.OriginalIndex) &&
                map.AreSpaceBridged(verb.LastWordIndex, nominal.FirstWord, ct) &&
                HasCleanSelectionTokens(map, verb, nominal, ct))
            {
                postFinite = AllFinitePersonalReadings(predicate.Word, ct);
                if (!allowsPerfPres)
                    postFinite = FilterIndicativeFinite(predicate.Word, postFinite);
                bool hasThird = postFinite.Any(r => predicate.Word.Readings[r].Features.Person == MorphologicalPerson.Third);
                bool initialGovernedFront = bodyStart < verbFirstPos &&
                    Enumerable.Range(bodyStart, verbFirstPos - bodyStart).All(p => governed[p]);
                bool mismatchPatient = nominal.Coherent && nominal.Number is not null &&
                    MatchingVerbReadings(predicate, postFinite, nominal).Length == 0 &&
                    !HasSecondPersonImperative(predicate.Word, nominal.Number.Value);
                bool intransitive = postFinite.Length > 0 &&
                    (postFinite.All(r =>
                        predicate.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                        !IsLinkingOrSemiCopularLemma(predicate.Word.Readings[r].Lemma)) ||
                     mismatchPatient && initialGovernedFront && IsIntransitiveMotionOrSpeechLemma(predicate, postFinite));
                bool hasIntransitive = postFinite.Any(r =>
                    predicate.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive) ||
                    mismatchPatient && initialGovernedFront && IsIntransitiveMotionOrSpeechLemma(predicate, postFinite);
                int nominalEnd = Array.IndexOf(idx, nominal.LastWord);
                WordSlot head = slots[nominal.HeadWord];
                // The sole bare NP can be a passive patient. A categorical conflict
                // also needs a source declaration of the finite verb's argument frame.
                bool pluralMismatchPatient = mismatchPatient && nominal.Number == MorphologicalNumber.Plural;
                bool preposedPassiveSe = verb.HasReflexiveSe &&
                    (verb.PreCarrierClitics.HasReflexiveSe && verb.PreCarrierClitics.DativeCliticWordIndex < 0 && initialGovernedFront ||
                     verb.Kind == VerbalComplexKind.DaConstruction && verb.PostDaOrAuxClitics.HasReflexiveSe &&
                     verb.PostDaOrAuxClitics.DativeCliticWordIndex < 0 && pluralMismatchPatient) &&
                    mismatchPatient &&
                    !IsNumeralWord(slots[nominal.FirstWord]) &&
                    slots[nominal.FirstWord].Word.CanonicalText is not ("няколко" or "много" or "малко" or "толкова" or "доста") &&
                    !nominal.HeadReadings.Any(r =>
                        PartitiveOrCollectiveHeads.Contains(Norm(head.Word.Readings[r].Lemma)) ||
                        Norm(head.Word.Readings[r].Lemma) is "множество" or "предвид" or "участие" or "сметка" or "отношение");
                passivePatient = (verb.PostposedSe && hasThird || preposedPassiveSe) &&
                    AllFiniteReadingsTransitive(predicate.Word, postFinite) &&
                    postFinite.All(r => !IsLinkingOrSemiCopularLemma(predicate.Word.Readings[r].Lemma)) &&
                    (IsDefiniteOrProperSubjectCandidate(slots, nominal) || mismatchPatient) &&
                    nominal.HeadReadings.All(r => head.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun) &&
                    postEndPos == idx.Length - 1 &&
                    Enumerable.Range(nominalEnd + 1, postEndPos - nominalEnd).All(p =>
                        governed[p] && map.AreSpaceBridged(idx[p - 1], idx[p], ct));
                bool singularPassiveVerb = hasThird && postFinite.All(r =>
                    predicate.Word.Readings[r].Features.Number == MorphologicalNumber.Singular);
                postRoleProven = postFinite.Length > 0 && (nominal.PronounSubject ||
                    (hasThird || initialGovernedFront) && intransitive ||
                    passivePatient && !HasAnimateNominalHead(head, nominal.HeadReadings) &&
                    (directObjectFrame?.Invoke(predicate.Word, postFinite) == true ||
                     pluralMismatchPatient && singularPassiveVerb ||
                     preposedPassiveSe));
                if (postRoleProven || hasThird && (hasIntransitive ||
                    verb.HasReflexiveSe && postFinite.All(r => !IsLinkingOrSemiCopularLemma(predicate.Word.Readings[r].Lemma))))
                    postSubject = nominal;
            }

            // Existing compatible selection and optional frame-catalog coverage.
            NominalChunk? postCand = postSubject ?? postNps.FirstOrDefault(np =>
                !np.TemporalAdjunct && np.Coherent && !np.PronounSubject && !np.ShortMasculineSingular);
            if (postCand is { Coherent: true })
            {
                int postCandPos = Array.IndexOf(idx, postCand.FirstWord);
                bool postposedSeAcrossPpOrComma = postSubject is null && verb.PostposedSe && postCandPos > verbLastPos &&
                    (map.HasDirectCommaBetween(idx, verbLastPos, postCandPos) ||
                     Enumerable.Range(verbLastPos + 1, postCandPos - verbLastPos - 1).Any(p => governed[p]));
                int[] matchingVerb = MatchingVerbReadings(predicate, verb.CarrierFiniteReadings, postCand);
                WordSlot lexicalSlot = verb.HasActiveParticipleTail && IsActiveLexicalParticiple(slots[verb.LexicalHeadWordIndex])
                    ? slots[verb.LexicalHeadWordIndex] : predicate;
                int[] lexicalReadings = lexicalSlot == predicate ? matchingVerb : lexicalSlot.Active;
                bool nonLinking = lexicalSlot.Active.All(r => !IsLinkingOrSemiCopularLemma(lexicalSlot.Word.Readings[r].Lemma));
                if (!postposedSeAcrossPpOrComma && matchingVerb.Length > 0 &&
                    (postRoleProven || !IsExclusivelyFirstOrSecond(predicate.Word, verb.CarrierFiniteReadings) && nonLinking &&
                     (verb.HasReflexiveSe ||
                      (!verb.CopularCarrier || lexicalSlot != predicate) && AllowsPostverbalSubject(analysis, lexicalSlot, lexicalReadings, postCand,
                          verbFrames, postverbalDirectObjectMaxRatio))))
                {
                    selectedSubject = postCand;
                    compatibleVerbReadings = matchingVerb;
                }
            }
        }

        // Check SV mismatch on preverbal subject candidate; emit diagnostic on closed sentences or 3+ word clauses (matching ExactPronounPrefix).
        bool emitDiagnostic = map.HasTerminalPunctuation || idx.Length >= 3;
        bool svMismatch = false;
        bool obliqueSubjectMismatch = !quotedClause && !hasSymbolGap && emitDiagnostic &&
            TryAddObliqueSubjectPronounMismatch(map, clause, verbFirstPos, governed, verb, postNps, canonicalKey, agreement, ct);
        bool postEncliticMismatch = !quotedClause && !hasSymbolGap && emitDiagnostic && !coordinatedPreSubject && !preCommaBlocked &&
            TryAddPostverbalUnhostedEncliticMismatch(map, clause, preSubjectCand, verb, canonicalKey, agreement, ct);
        if (!quotedClause && !hasSymbolGap && emitDiagnostic &&
            TryAddCompoundParticipleMismatch(map, clause, !coordinatedPreSubject && !preCommaBlocked ? preSubjectCand : null, verb, canonicalKey, agreement, ct))
        {
            svMismatch = true;
        }
        else if (!quotedClause && !hasSymbolGap && emitDiagnostic && !coordinatedPreSubject && !preCommaBlocked &&
                 preSubjectCand is { Coherent: true } &&
                 TryAddModalDaSubjectVerbMismatch(map, clause, preSubjectCand, verb, canonicalKey, agreement, ct))
        {
            svMismatch = true;
        }
        else if (!quotedClause && !hasSymbolGap && emitDiagnostic && !coordinatedPreSubject && !preCommaBlocked &&
                 preSubjectCand is { Coherent: true } &&
                 TryAddCopularOrLinkingPredicativeMismatch(map, clause, verb, preSubjectCand, canonicalKey, agreement, ct))
        {
            svMismatch = true;
        }
        else if (!quotedClause && !hasSymbolGap && emitDiagnostic &&
                 TryAddSentenceInitialToPronounMismatch(map, clause, verbFirstPos, verb, canonicalKey, agreement, ct))
        {
            svMismatch = true;
        }
        else if (!quotedClause && !hasSymbolGap && emitDiagnostic &&
                 !verb.InvariantCarrier &&
                 TryAddRelativeKoytoSubjectVerbMismatch(map, clause, verbFirstPos, verb, postNps, canonicalKey, agreement, ct))
        {
            svMismatch = true;
        }
        else if (!quotedClause && !hasSymbolGap && !coordinatedPreSubject &&
            preSubjectCand is not null && preSubjectCand.Coherent &&
            (!preCommaBlocked || IsCommaEnclosedGovernedComitativePp(map, idx, slots, preSubjectCand, verbFirstPos, governed, ct)) &&
            !verb.InvariantCarrier)
        {
            svMismatch = TryAddSubjectVerbMismatch(map, clause, preSubjectCand, verb, verbFirstPos, governed, postNps, emitDiagnostic, canonicalKey, agreement, ct);
            if (!svMismatch && !preCommaBlocked && emitDiagnostic && !verb.CopularCarrier && !verb.ImpersonalModal &&
                (IsCliticResumedObject(slots, preSubjectCand, verb) ||
                 preSubjectCand.FullMasculineSingular && verb.CarrierAccusativeCliticWordIndex >= 0 &&
                 slots[verb.CarrierAccusativeCliticWordIndex].Word.CanonicalText == "го") &&
                AllFiniteReadingsTransitive(predicate.Word, verb.CarrierFiniteReadings) &&
                postNps.Where(np => !np.TemporalAdjunct).ToArray() is [{ Coherent: true, ShortMasculineSingular: false, Number: not null } cliticPostSubj] &&
                (cliticPostSubj.FullMasculineSingular || cliticPostSubj.PronounSubject || IsDefiniteOrProperSubjectCandidate(slots, cliticPostSubj)) &&
                map.AreSpaceBridged(verb.LastWordIndex, cliticPostSubj.FirstWord, ct) &&
                !Enumerable.Range(verbLastPos + 1, Math.Max(0, postEndPos - verbLastPos))
                    .Any(p => !governed[p] && slots[idx[p]].Word.CanonicalText is "и" or "или") &&
                !IsAdverbialTimeOrMeasureHead(slots[cliticPostSubj.HeadWord], cliticPostSubj) &&
                !HasVocativeReading(slots[cliticPostSubj.HeadWord]) &&
                !IsPersonPlaceHomograph(analysis, slots[cliticPostSubj.HeadWord].Word.OriginalIndex) &&
                HasCleanSelectionTokens(map, verb, cliticPostSubj, ct))
            {
                int[] cliticPostFinite = AllFinitePersonalReadings(predicate.Word, ct);
                if (!allowsPerfPres)
                    cliticPostFinite = FilterIndicativeFinite(predicate.Word, cliticPostFinite);
                if (cliticPostFinite.Length > 0 &&
                    MatchingVerbReadings(predicate, cliticPostFinite, cliticPostSubj).Length == 0 &&
                    !HasSecondPersonImperative(predicate.Word, cliticPostSubj.Number.Value))
                {
                    AddSubjectRelation(slots, cliticPostSubj, verb, cliticPostFinite, canonicalKey, agreement,
                        "postverbal_nominal_reading", mismatch: true, subjectProven: true);
                    svMismatch = true;
                }
            }
        }
        else if (!quotedClause && !hasSymbolGap && coordinatedPreSubject && emitDiagnostic &&
                 !verb.InvariantCarrier)
        {
            svMismatch = TryAddCoordinatedSubjectVerbMismatch(map, clause, verbFirstPos, nonTemporalPreNps, verb, postNps, canonicalKey, agreement, ct);
        }
        else if (postSubject is { } post)
        {
            bool conflicting = postRoleProven && post.Coherent && post.Number is not null &&
                MatchingVerbReadings(predicate, postFinite, post).Length == 0 &&
                !HasSecondPersonImperative(predicate.Word, post.Number.Value);
            bool pluralVerbWithSingularPost = conflicting && !post.PronounSubject && post.Number == MorphologicalNumber.Singular &&
                postFinite.Any(r => predicate.Word.Readings[r].Features is
                    { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Plural });
            bool associativeAlternative = pluralVerbWithSingularPost &&
                (idx.Skip(Array.IndexOf(idx, post.LastWord) + 1).Any(w => slots[w].Word.CanonicalText is "с" or "със") ||
                 post.LastWord + 1 < slots.Length && map.HasCommaBetween(post.LastWord, post.LastWord + 1) &&
                 (IsNominalModifierOrDeterminer(slots[post.LastWord + 1]) || HasNominalHeadReading(slots[post.LastWord + 1])));
            svMismatch = conflicting && !associativeAlternative;
            bool subjectProven = postRoleProven && !associativeAlternative;
            if (emitDiagnostic || !svMismatch)
                AddSubjectRelation(slots, post, verb, postFinite, canonicalKey, agreement,
                    subjectProven ? "postverbal_nominal_reading" : passivePatient
                        ? "postverbal_passive_patient_candidate" : "postverbal_nominal_role_unassessed", svMismatch, subjectProven);
        }
        else if (!quotedClause && !hasSymbolGap && verb.PostposedSe)
        {
            TokenView finite = predicate.Word;
            List<ContextOverlayRoleSelection> roles =
            [
                new(ContextOverlayRole.Predicate, finite.OriginalIndex, finite.Token.Start, finite.Token.Length,
                    verb.CarrierFiniteReadings, ["clause_fusion_finite_readings"]),
                PostposedReflexive(slots[verb.LastWordIndex])
            ];
            agreement.Add(new(finite.OriginalIndex, false, AgreementLane, 0,
                canonicalKey(roles), roles, ["postverbal_reflexive_group"]));
        }

        // Capture matrix selection when clause is clean and has no SV or preverbal NP mismatch.
        if (captureMatrix && !quotedClause && !hasSymbolGap && !svMismatch && !obliqueSubjectMismatch && !postEncliticMismatch && !coordinatedPreSubject &&
            !(preSubjectCand is { Coherent: false }) &&
            HasCleanSelectionTokens(map, verb, selectedSubject ?? preSubjectCand, ct))
        {
            TokenView predWord = predicate.Word;
            List<ContextOverlayRoleSelection> roles =
            [
                new(ContextOverlayRole.Predicate, predWord.OriginalIndex, predWord.Token.Start, predWord.Token.Length,
                    compatibleVerbReadings, ["clause_fusion_finite_predicate"])
            ];
            int subjFirst = -1, subjHead = -1, subjTok = -1;
            int[] subjReadings = [];
            if (selectedSubject is { HeadReadings.Length: > 0 } && !slots[selectedSubject.HeadWord].Word.UsesGraph)
            {
                TokenView subjWord = slots[selectedSubject.HeadWord].Word;
                subjFirst = selectedSubject.FirstWord;
                subjHead = selectedSubject.HeadWord;
                subjTok = subjWord.OriginalIndex;
                subjReadings = selectedSubject.HeadReadings;
                string dirCode = selectedSubject.HeadWord < verb.FiniteCarrierWordIndex ? "preverbal_nominal_reading" : "postverbal_nominal_reading";
                roles.Add(new(ContextOverlayRole.Subject, subjTok, subjWord.Token.Start, subjWord.Token.Length,
                    subjReadings, ["clause_fusion_subject", dirCode]));
            }
            roles = roles.OrderBy(r => r.Start).ToList();
            var overlay = new ContextSelectionOverlay(
                ContextOverlayStatus.Selected,
                Truncated: false,
                SelectedPredicateTokenIndex: predWord.OriginalIndex,
                DiagnosticPredicateTokenIndex: null,
                CandidateCount: 1,
                Roles: roles,
                EvidenceCodes: ["accepted_authority_lane=" + SelectionLane, "clause_fusion_matrix_selected"]);

            matrixSelection = new(
                PredicateWordIndex: verb.FiniteCarrierWordIndex,
                PredicateTokenIndex: predWord.OriginalIndex,
                PredicateReadings: compatibleVerbReadings,
                SubjectFirstWordIndex: subjFirst,
                SubjectWordIndex: subjHead,
                SubjectTokenIndex: subjTok,
                SubjectReadings: subjReadings,
                SuppressHeuristicSubject: selectedSubject is null,
                Overlay: overlay);
        }

        // Collect clause-role article targets (SubjectFull and DirectObjectShort) outside quotes/symbols.
        if (!quotedClause && !idx.Any(i => slots[i].Quoted) && !hasSymbolGap)
        {
            if (!coordinatedPreSubject)
                CollectClauseArticleTargets(map, clause, preSubjectCand, preCommaBlocked, verb, postNps, governed, articles, ct);
            else
                CollectCoordinatedSubjectArticleTargets(map, clause, verbFirstPos, nonTemporalPreNps, verb, articles, ct);
        }
    }

    private static bool TryAddSubjectVerbMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        NominalChunk subj,
        VerbalComplex verb,
        int verbFirstPos,
        bool[] governed,
        List<NominalChunk> postNps,
        bool emitDiagnostic,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (verb.CopularCarrier) return false;
        TextAnalysis analysis = map.Analysis;
        SentenceSpan sentence = map.Sentence;
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        WordSlot subjSlot = slots[subj.HeadWord];
        if (verbSlot.Word.UsesGraph || subjSlot.Word.UsesGraph) return false;
        if (!IsCleanPredicateToken(verbSlot.Word, IsSentenceInitialWord(analysis, sentence, verb.FiniteCarrierWordIndex, verbSlot.Word), ct))
            return false;
        for (int w = subj.FirstWord; w <= subj.HeadWord; w++)
        {
            if (slots[w].HyphenatedCompoundNominal is not null)
                continue;
            if (!IsCleanSubjectToken(slots[w].Word, IsSentenceInitialWord(analysis, sentence, w, slots[w].Word),
                    subj.PronounSubject && w == subj.HeadWord, ct))
                return false;
        }

        // Check ALL original finite/imperative readings of the verb so imperatives on 2nd-person subjects never false-trigger.
        int[] allFinite = AllFinitePersonalReadings(verbSlot.Word, ct);
        if (!verb.AllowsPerfectivePresent(map, clause))
            allFinite = FilterIndicativeFinite(verbSlot.Word, allFinite);
        if (allFinite.Length == 0 || subj.Number is null) return false;

        if (subj.PronounSubject)
        {
            // Defer subordinate че/защото pronoun prefixes to SubordinatePronounPrefixLane.
            if (!clause.Matrix && slots[clauseIdx[0]].Word.CanonicalText is "че" or "защото")
                return false;
            // Guard clitic edge cases from NominalDiagnosticsPackageTests ("Тя я видяхте.", "Тя ги пристигнахте.").
            if (verb.CarrierAccusativeCliticWordIndex >= 0)
            {
                string cliticText = slots[verb.CarrierAccusativeCliticWordIndex].Word.CanonicalText;
                if (cliticText == "я" || !AllFiniteReadingsTransitive(verbSlot.Word, allFinite))
                    return false;
            }
            if (subj.Person == MorphologicalPerson.Second && HasSecondPersonImperative(verbSlot.Word, subj.Number.Value))
                return false;
            if (allFinite.Any(r => Guard.PersonNumberTupleMatches(
                    (subj.Person, subj.Number),
                    (verbSlot.Word.Readings[r].Features.Person, verbSlot.Word.Readings[r].Features.Number))))
                return false;
            if (IsNonInitialPreverbalAccusativeTeClitic(map, clauseIdx, subj, verb, verbSlot, allFinite, ct))
                return false;

            if (emitDiagnostic)
                AddSubjectRelation(slots, subj, verb, allFinite, canonicalKey, agreement,
                    "pronoun_subject_clause_mismatch", mismatch: true, subjectProven: true);
            return true;
        }

        // 3rd-person NP subject mismatch:
        // Never accuse short-article masculine singular NPs (marked as non-subject, or handled by ART),
        // temporal/measure adverbials, vocatives, or Person|Place proper-name homographs ("Иван пътуваме с теб.").
        if (subj.ShortMasculineSingular || subj.TemporalAdjunct) return false;
        if (IsPersonPlaceHomograph(analysis, subjSlot.Word.OriginalIndex)) return false;
        if (HasUnassignedNominativePronoun(slots, clauseIdx, bodyStart, subj)) return false;
        if (subj.Number == MorphologicalNumber.Singular && subj.LastWord + 1 < verb.FirstWordIndex &&
            allFinite.Any(r => verbSlot.Word.Readings[r].Features is
                { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Plural }) &&
            (subjSlot.Active.Any(r => PartitiveOrCollectiveHeads.Contains(Norm(subjSlot.Word.Readings[r].Lemma))) ||
             IsAnimateComitativeWithPluralPredicate(map, clauseIdx, subj, subjSlot, verbFirstPos, governed, ct)))
            return false;

        // Check if ANY finite reading agrees with (Third, subj.Number).
        if (allFinite.Any(r => Guard.PersonNumberTupleMatches(
                (MorphologicalPerson.Third, subj.Number),
                (verbSlot.Word.Readings[r].Features.Person, verbSlot.Word.Readings[r].Features.Number))))
            return false;

        if (!HasFullMasculineSingularSubjectCarrier(slots, subj) &&
            HasOpenTransitivePostverbalDaComplement(map, clauseIdx, subj, verb, verbSlot, allFinite, ct))
            return false;

        int verbLastPosInClause = Array.IndexOf(clauseIdx, verb.LastWordIndex);
        bool hasPostverbalGovernedComplement = verbLastPosInClause >= 0 && verbLastPosInClause + 1 < clauseIdx.Length &&
            IsActivePreposition(slots[clauseIdx[verbLastPosInClause + 1]].Word, slots[clauseIdx[verbLastPosInClause + 1]].Active);
        bool saturatedObliqueComplementVerb = hasPostverbalGovernedComplement &&
            allFinite.All(r => IsPrepositionalComplementVerbLemma(Norm(verbSlot.Word.Readings[r].Lemma)));

        bool anyThird = verb.CarrierFiniteReadings.Any(r => verbSlot.Word.Readings[r].Features.Person == MorphologicalPerson.Third);
        bool anyFirstOrSecondPluralOrPresent = allFinite.Any(r =>
        {
            MorphologicalFeatures f = verbSlot.Word.Readings[r].Features;
            return f.Person == MorphologicalPerson.First ||
                   f.Person == MorphologicalPerson.Second &&
                   (f.Number == MorphologicalNumber.Plural || f.Tense == MorphologicalTense.Present);
        });

        if (!anyThird || anyFirstOrSecondPluralOrPresent)
        {
            // With 1st/2nd-person verbs, a preverbal 3rd-person NP is a fronted object for transitive verbs ("Малката книга четем."),
            // a predicative complement for linking/small-clause verbs ("Най-добрият отбор станахме.", "Сирак израснах на село."),
            // or a bare time/measure/vocative adjunct ("Последната седмица пътуваме.").
            // Definite/proper subjects before strictly Intransitive verbs OR before non-predicative Transitive verbs whose
            // object slot is already occupied ("Старият майстор поправих счупения прозорец.") prove person mismatch.
            bool definiteOrProperSubj = IsDefiniteOrProperSubjectCandidate(slots, subj);
            bool hasFirstPluralReading = allFinite.Any(r => verbSlot.Word.Readings[r].Features is
                { Person: MorphologicalPerson.First, Number: MorphologicalNumber.Plural });
            bool hasSecondPluralReading = allFinite.Any(r => verbSlot.Word.Readings[r].Features is
                { Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Plural });

            // [P1-I3] Inclusive plural ("Двамата млади лекари прегледахме пациента внимателно.", "Войниците вървим в строй.") is grammatical in Bulgarian,
            // as is singular animate + uncomma'd 'с'/'със' + 1st/2nd-person oblique pronoun ("Майката с мен тръгнахме.", "Майката с теб тръгнахте.").
            if (subj.Number == MorphologicalNumber.Plural && (hasFirstPluralReading || hasSecondPluralReading) &&
                (HasAnimateNominalHead(subjSlot, subj.HeadReadings) ||
                 slots[subj.FirstWord].Word.CanonicalText is "двамата" or "тримата" or "четиримата" or "петимата" or "шестимата" or "седмината" or
                     "двама" or "трима" or "четирима" or "петима" or "шестима" or "седмина" or "мнозина" or "мнозината" or "малцина" or "малцината" or
                     "всички" or "всичките") ||
                IsInclusiveSingularComitativePronoun(map, clauseIdx, subj, subjSlot, verbFirstPos, hasFirstPluralReading, hasSecondPluralReading, ct))
                return false;

            bool personalNumeralHumanSubj = !definiteOrProperSubj &&
                subj.Number == MorphologicalNumber.Plural &&
                slots[subj.FirstWord].Word.CanonicalText is "двама" or "трима" or "четирима" or "петима" or "шестима" or "седмина" or "мнозина" or "малцина" &&
                HasAnimateNominalHead(subjSlot, subj.HeadReadings);

            bool strictlyIntransitive = (definiteOrProperSubj || subj.FirstWord < subj.HeadWord || subj.LastWord > subj.HeadWord) &&
                allFinite.All(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                    Norm(verbSlot.Word.Readings[r].Lemma) is not ("остана" or "оставам")) &&
                verb.CarrierFiniteReadings.All(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                    Norm(verbSlot.Word.Readings[r].Lemma) is not ("остана" or "оставам"));

            // G3a: Detransitivized reflexive се-verb ("Настоящото решение се прилагам за всички продукти.") cannot take a fronted accusative NP.
            bool detransitivizedReflexiveSe = definiteOrProperSubj &&
                verb.PreCarrierClitics.HasReflexiveSe &&
                verb.PreCarrierClitics.DativeCliticWordIndex < 0 &&
                verb.CarrierAccusativeCliticWordIndex < 0 &&
                !Enumerable.Range(subj.HeadWord + 1, Math.Max(0, verb.FiniteCarrierWordIndex - subj.HeadWord - 1))
                    .Any(w => slots[w].Word.CanonicalText is "да" or "дали" ||
                              hasFirstPluralReading && slots[w].Word.CanonicalText is "с" or "със") &&
                !Enumerable.Range(subj.FirstWord, subj.HeadWord - subj.FirstWord + 1)
                    .Any(w => slots[w].Word.Readings.Any(r => r.Category == Category.Verb &&
                        !Guard.IsMeasuredPassiveAttribute(r.Features) &&
                        !Guard.IsMeasuredPresentActiveAttribute(r.Features))) &&
                !(subj.Number == MorphologicalNumber.Plural && hasFirstPluralReading &&
                  (HasAnimateNominalHead(subjSlot, subj.HeadReadings) ||
                   slots[subj.FirstWord].Word.CanonicalText is "всички" or "всичките"));

            bool nonPredicativeTransitiveWithProvenSubject = (definiteOrProperSubj || personalNumeralHumanSubj) &&
                allFinite.All(r => !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                    !TextPredicateLinkAnalyzer.CopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                    Norm(verbSlot.Word.Readings[r].Lemma) is not ("остана" or "оставам") &&
                    (subj.FullMasculineSingular ||
                     Norm(verbSlot.Word.Readings[r].Lemma) is not ("правя" or "направя" or
                        "смятам" or "сметна" or "считам" or "наричам" or "нарека" or "избирам" or "избера" or
                        "обявявам" or "обявя" or "назначавам" or "назнача" or "оставям" or "оставя" or
                        "приемам" or "приема" or "определям" or "определя" or "казвам" or "кажа" or
                        "водя" or "намирам" or "намеря" or "чувствам" or "виждам" or "видя" or "чувам" or "чуя" or
                        "заварвам" or "заваря" or "карам" or "накарам" or
                        "уча" or "науча" or "научавам" or "питам" or "попитам" or "разпитвам" or "моля" or "помоля"))) &&
                (definiteOrProperSubj && (subj.FullMasculineSingular || saturatedObliqueComplementVerb ||
                    HasAnimateNominalHead(subjSlot, subj.HeadReadings) &&
                    subj.LastWord + 1 < verb.FirstWordIndex &&
                    slots[subj.LastWord + 1].Word.CanonicalText is "с" or "със" &&
                    IsActivePreposition(slots[subj.LastWord + 1].Word, slots[subj.LastWord + 1].Active) &&
                    allFinite.All(r => Norm(verbSlot.Word.Readings[r].Lemma) is "говоря" or "разговарям") ||
                    postNps.Any(p => !p.TemporalAdjunct && !p.PronounSubject && !IsAdverbialTimeOrMeasureHead(slots[p.HeadWord], p))) ||
                 personalNumeralHumanSubj && postNps.Any(p => !p.TemporalAdjunct && !p.PronounSubject &&
                    !IsAdverbialTimeOrMeasureHead(slots[p.HeadWord], p) &&
                    (IsDefiniteOrProperSubjectCandidate(slots, p) || IsNumeralWord(slots[p.FirstWord]) ||
                     slots[p.FirstWord].Word.CanonicalText is "няколко" or "мнозина" or "малцина")));

            bool personalNyamaDa = verbSlot.Word.CanonicalText is "нямам" or "нямаш" or "нямаме" or "нямате" or "нямат" &&
                (verb.DaParticleWordIndex >= 0 || HasPostverbalDaComplement(slots, clauseIdx, verb.LastWordIndex));
            if (personalNyamaDa)
                return true;

            var imperativeRows = verbSlot.Word.Readings
                .Where(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative && !r.Features.HasConflictingFeatures)
                .ToArray();
            bool syncreticPluralImperativeAllowed = imperativeRows.Length > 0 &&
                imperativeRows.All(r => r.Features.Number == MorphologicalNumber.Plural) &&
                definiteOrProperSubj &&
                analysis.Text.AsSpan(sentence.Start, sentence.Length).TrimEnd().EndsWith(".", StringComparison.Ordinal) &&
                !slots.Any(s => s.Word.CanonicalText == "ли");

            // G3b: Full masculine singular subject ("Учителят му я дадеш...") is not blocked by non-resumptive accusative clitics.
            if (verb.CarrierAccusativeCliticWordIndex >= 0 && (!subj.FullMasculineSingular || IsCliticResumedObject(slots, subj, verb)) ||
                imperativeRows.Length > 0 && !syncreticPluralImperativeAllowed ||
                !(strictlyIntransitive || detransitivizedReflexiveSe || nonPredicativeTransitiveWithProvenSubject) ||
                !subj.FullMasculineSingular && IsAdverbialTimeOrMeasureHead(subjSlot, subj) ||
                HasVocativeReading(subjSlot))
                return false;
        }
        else
        {
            // Exclude fronted direct objects: clitic-resumed ("Хорото го гледаха."), reflexive-possessed ("Домакинските си мачове играе..."),
            // bare O-V topicalization before a transitive verb with an unoccupied object slot ("Книгата четат."), or OVS with postverbal definite/proper subject.
            if (IsCliticResumedObject(slots, subj, verb)) return false;
            if (!subj.FullMasculineSingular &&
                Enumerable.Range(subj.FirstWord + 1, Math.Max(0, subj.LastWord - subj.FirstWord))
                    .Any(w => slots[w].Word.CanonicalText == "си"))
                return false;
            bool provenSubjectRole = subj.FullMasculineSingular ||
                verb.HasReflexiveSe ||
                verb.CarrierAccusativeCliticWordIndex >= 0 ||
                verb.CarrierFiniteReadings.All(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    Norm(verbSlot.Word.Readings[r].Lemma) != "играя") ||
                IsIntransitiveMotionOrSpeechLemma(verbSlot, verb.CarrierFiniteReadings) ||
                saturatedObliqueComplementVerb && IsDefiniteOrProperSubjectCandidate(slots, subj) ||
                IsAnimateSubjectWithInanimateOnlyVerb(subjSlot, subj.HeadReadings, verbSlot, verb.CarrierFiniteReadings) ||
                postNps.Any(p => !p.TemporalAdjunct);
            if (!provenSubjectRole) return false;
            if (!subj.FullMasculineSingular && HasCompatiblePostverbalSubject(slots, postNps, verbSlot, verb.CarrierFiniteReadings))
                return false;
        }

        if (emitDiagnostic)
            AddSubjectRelation(slots, subj, verb, allFinite, canonicalKey, agreement,
                "nominal_subject_clause_mismatch", mismatch: true, subjectProven: true);
        return true;
    }

    private static bool IsNonInitialPreverbalAccusativeTeClitic(
        SentenceClauseMap map,
        int[] clauseIdx,
        NominalChunk subj,
        VerbalComplex verb,
        WordSlot verbSlot,
        int[] allFinite,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        if (subj.FirstWord != subj.HeadWord ||
            slots[subj.HeadWord].Word.CanonicalText != "те" ||
            subj.HeadWord + 1 != verb.FirstWordIndex ||
            !map.AreSpaceBridged(subj.HeadWord, verb.FirstWordIndex, ct))
            return false;

        int subjPos = Array.IndexOf(clauseIdx, subj.HeadWord);
        if (subjPos <= 0 ||
            map.HasDirectCommaBetween(clauseIdx, subjPos - 1, subjPos) ||
            !map.AreSpaceBridged(clauseIdx[subjPos - 1], subj.HeadWord, ct))
            return false;

        return allFinite.Any(r =>
            verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive ||
            Norm(verbSlot.Word.Readings[r].Lemma) is "боля" or "сърбя" or "треса" or "мързя");
    }

    private static bool HasOpenTransitivePostverbalDaComplement(
        SentenceClauseMap map,
        int[] clauseIdx,
        NominalChunk subj,
        VerbalComplex verb,
        WordSlot verbSlot,
        int[] allFinite,
        CancellationToken ct)
    {
        if (verb.DaParticleWordIndex >= 0 || !verb.PreCarrierClitics.HasReflexiveSe || verb.CarrierAccusativeCliticWordIndex >= 0)
            return false;

        WordSlot[] slots = map.Slots;
        int vPos = Array.IndexOf(clauseIdx, verb.LastWordIndex);
        if (vPos < 0 || vPos + 2 >= clauseIdx.Length ||
            slots[clauseIdx[vPos + 1]].Word.CanonicalText != "да" ||
            !map.AreSpaceBridged(verb.LastWordIndex, clauseIdx[vPos + 1], ct))
            return false;

        int subPos = vPos + 2;
        while (subPos < clauseIdx.Length &&
               map.AreSpaceBridged(clauseIdx[subPos - 1], clauseIdx[subPos], ct))
        {
            WordSlot clSlot = slots[clauseIdx[subPos]];
            string ck = clSlot.Word.CanonicalText;
            if (ck == "се")
                return false;
            if (BulgarianFunctionWords.HasAny(ck, FunctionWordClasses.PureAccusativeClitic))
            {
                if (ck is not ("я" or "го" or "ги") || subj.Number is null ||
                    !clSlot.Active.Any(r =>
                    {
                        MorphologicalFeatures cf = clSlot.Word.Readings[r].Features;
                        return cf.Person == MorphologicalPerson.Third &&
                               (Guard.NominalTupleMatches((subj.Number, subj.Gender), (cf.Number, cf.Gender)) ||
                                subj.Number == MorphologicalNumber.Singular && subj.Gender is null &&
                                cf.Number == MorphologicalNumber.Singular &&
                                cf.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine);
                    }))
                    return false;
                subPos++;
                continue;
            }
            if (BulgarianFunctionWords.HasAny(ck,
                    FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.ReflexiveDativeSi | FunctionWordClasses.NegativeParticle))
            {
                subPos++;
                continue;
            }
            break;
        }
        if (subPos >= clauseIdx.Length || !map.AreSpaceBridged(clauseIdx[subPos - 1], clauseIdx[subPos], ct))
            return false;

        WordSlot subSlot = slots[clauseIdx[subPos]];
        if (subSlot.Word.UsesGraph)
            return false;

        bool hasCorefTransitive = AllFinitePersonalReadings(subSlot.Word, ct).Any(er =>
        {
            ReadingView rv = subSlot.Word.Readings[er];
            MorphologicalFeatures ef = rv.Features;
            return ef.Tense == MorphologicalTense.Present &&
                   ef.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                   !IsLinkingOrSemiCopularLemma(rv.Lemma) &&
                   !TextPredicateLinkAnalyzer.CopularLemma(rv.Lemma) &&
                   allFinite.Any(mr => Guard.PersonNumberTupleMatches(
                       (verbSlot.Word.Readings[mr].Features.Person, verbSlot.Word.Readings[mr].Features.Number),
                       (ef.Person, ef.Number)));
        });
        if (!hasCorefTransitive)
            return false;

        int postEndPos = clauseIdx.Length - 1;
        for (int p = subPos + 1; p < clauseIdx.Length; p++)
            if (slots[clauseIdx[p]].Word.CanonicalText == "да") { postEndPos = p - 1; break; }
        if (subPos + 1 > postEndPos)
            return true;

        bool[] subGov = MarkGovernedPhrases(map, clauseIdx, subPos + 1, postEndPos, 0, subPos, ct);
        List<NominalChunk> subPostNps = CollectNominalChunks(map, clauseIdx, subPos + 1, postEndPos, subGov, ct);
        return !subPostNps.Any(np => !np.TemporalAdjunct && !IsAdverbialTimeOrMeasureHead(slots[np.HeadWord], np));
    }

    private static bool TryAddRelativeKoytoSubjectVerbMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        int verbFirstPos,
        VerbalComplex verb,
        List<NominalChunk> postNps,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (verb.CopularCarrier || clause.BodyStartPos != 1 || verbFirstPos != 1 || clause.WordIndices.Length < 2)
            return false;
        WordSlot[] slots = map.Slots;
        int relIdx = clause.WordIndices[0];
        if (relIdx < 1 || slots[relIdx].Word.CanonicalText != "който" ||
            !IsExactWord(slots[relIdx]) ||
            !DirectlySeparatedBySingleComma(map.Analysis, slots[relIdx - 1].Word, slots[relIdx].Word))
            return false;
        if (verb.CarrierAccusativeCliticWordIndex >= 0)
            return false;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        if (verbSlot.Word.UsesGraph || !IsCleanPredicateToken(verbSlot.Word, sentenceInitial: false, ct))
            return false;
        if (verbSlot.Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative && !r.Features.HasConflictingFeatures))
            return false;

        int[] relReadings = NonPlaceholderReadings(slots[relIdx].Word, sentenceInitial: false).Order().ToArray();
        int[] allFinite = AllFinitePersonalReadings(verbSlot.Word, ct);
        if (!verb.AllowsPerfectivePresent(map, clause))
            allFinite = FilterIndicativeFinite(verbSlot.Word, allFinite);
        if (relReadings.Length == 0 || allFinite.Length == 0)
            return false;

        if (allFinite.Any(r => Guard.PersonNumberTupleMatches(
                (MorphologicalPerson.Third, MorphologicalNumber.Singular),
                (verbSlot.Word.Readings[r].Features.Person, verbSlot.Word.Readings[r].Features.Number))))
            return false;

        // БЕРОН § 22.3 guard: bare "който" can also be an inanimate direct object ("документът, който подписахме").
        // Require strictly intransitive verb, detransitivized reflexive "се", or monotransitive verb with an explicit postverbal object NP.
        bool strictlyIntransitive = allFinite.All(r =>
            verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
            !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma) &&
            Norm(verbSlot.Word.Readings[r].Lemma) is not ("остана" or "оставам" or "играя"));
        bool detransitivizedSe = verb.PreCarrierClitics.HasReflexiveSe &&
            verb.PreCarrierClitics.DativeCliticWordIndex < 0 &&
            !HasCompatiblePostverbalSubject(slots, postNps, verbSlot, verb.CarrierFiniteReadings);
        bool saturatedMonotransitive = !strictlyIntransitive &&
            allFinite.All(r => !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                !TextPredicateLinkAnalyzer.CopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                Norm(verbSlot.Word.Readings[r].Lemma) is not ("остана" or "оставам" or "правя" or "направя" or
                    "смятам" or "сметна" or "считам" or "наричам" or "нарека" or "избирам" or "избера" or
                    "обявявам" or "обявя" or "назначавам" or "назнача" or "оставям" or "оставя" or
                    "приемам" or "приема" or "определям" or "определя" or "казвам" or "кажа" or
                    "уча" or "науча" or "научавам" or "питам" or "попитам" or "разпитвам" or "моля" or "помоля" or
                    "дам" or "давам" or "подаря" or "подарявам" or "изпратя" or "изпращам" or "покажа" or "показвам")) &&
            postNps.Any(p => !p.TemporalAdjunct && !p.PronounSubject) &&
            !HasCompatiblePostverbalSubject(slots, postNps, verbSlot, verb.CarrierFiniteReadings);

        if (!(strictlyIntransitive || detransitivizedSe || saturatedMonotransitive))
            return false;

        TokenView predWord = verbSlot.Word;
        TokenView relWord = slots[relIdx].Word;
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, predWord.OriginalIndex, predWord.Token.Start, predWord.Token.Length,
                allFinite, ["clause_fusion_finite_readings"]),
            new(ContextOverlayRole.Subject, relWord.OriginalIndex, relWord.Token.Start, relWord.Token.Length,
                relReadings, ["nominal_subject_clause_mismatch"])
        ];
        if (verb.PostposedSe) roles.Add(PostposedReflexive(slots[verb.LastWordIndex]));
        agreement.Add(new(predWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, ["nominal_subject_clause_mismatch", ContextLanes.ProductiveGraphMismatch]));
        return true;
    }

    private static void AddSubjectRelation(WordSlot[] slots, NominalChunk nominal, VerbalComplex verb, int[] finite,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey, List<ContextOverlayCandidate> agreement,
        string reason, bool mismatch, bool subjectProven)
    {
        TokenView predicate = slots[verb.FiniteCarrierWordIndex].Word;
        TokenView head = slots[nominal.HeadWord].Word;
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, predicate.OriginalIndex, predicate.Token.Start, predicate.Token.Length,
                finite, ["clause_fusion_finite_readings"]),
            new(subjectProven ? ContextOverlayRole.Subject : ContextOverlayRole.NominalContext,
                head.OriginalIndex, head.Token.Start, head.Token.Length, nominal.HeadReadings, [reason])
        ];
        for (int w = nominal.FirstWord; w < nominal.HeadWord; w++)
        {
            WordSlot slot = slots[w];
            if (slot.Word.UsesGraph || slot.Active.Length == 0) continue;
            TokenView modifier = slot.Word;
            roles.Add(new(ContextOverlayRole.Attribute, modifier.OriginalIndex, modifier.Token.Start, modifier.Token.Length,
                slot.Active, ["clause_fusion_nominal_modifier"]));
        }
        if (verb.PostposedSe) roles.Add(PostposedReflexive(slots[verb.LastWordIndex]));
        agreement.Add(new(predicate.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, mismatch ? [reason, ContextLanes.ProductiveGraphMismatch] : [reason]));
    }

    private static ContextOverlayRoleSelection PostposedReflexive(WordSlot slot) =>
        new(ContextOverlayRole.NominalContext, slot.Word.OriginalIndex, slot.Word.Token.Start, slot.Word.Token.Length,
            slot.Active, ["postverbal_reflexive_group"]);

    private static bool TryAddCoordinatedSubjectVerbMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        int verbFirstPos,
        NominalChunk[] nonTemporalPreNps,
        VerbalComplex verb,
        List<NominalChunk> postNps,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (nonTemporalPreNps.Length != 2) return false;
        if (verb.CopularCarrier && postNps.Any(p => !p.TemporalAdjunct)) return false;

        TextAnalysis analysis = map.Analysis;
        SentenceSpan sentence = map.Sentence;
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;

        NominalChunk np1 = nonTemporalPreNps[0], np2 = nonTemporalPreNps[1];
        if (!np1.Coherent || !np2.Coherent || np1.ShortMasculineSingular || np2.ShortMasculineSingular)
            return false;

        int np1LastPos = Array.IndexOf(clauseIdx, np1.LastWord);
        int np2FirstPos = Array.IndexOf(clauseIdx, np2.FirstWord);
        int np2LastPos = Array.IndexOf(clauseIdx, np2.LastWord);
        if (np1LastPos < bodyStart || np2FirstPos != np1LastPos + 2 || np2LastPos < np2FirstPos)
            return false;
        if (map.HasDirectCommaBetween(clauseIdx, np2LastPos, verbFirstPos))
            return false;

        WordSlot conjSlot = slots[clauseIdx[np1LastPos + 1]];
        if (conjSlot.Word.UsesGraph || conjSlot.Word.CanonicalText != "и" ||
            conjSlot.Word.Readings.Any(r => r.Category == Category.Noun && !Guard.IsSourcePlaceholder(conjSlot.Word, r)) ||
            !map.AreSpaceBridged(np1.LastWord, conjSlot.Index, ct) ||
            !map.AreSpaceBridged(conjSlot.Index, np2.FirstWord, ct))
            return false;
        int[] conjReadings = conjSlot.Active.Where(r => conjSlot.Word.Readings[r].SourceConjunction).ToArray();
        if (conjReadings.Length == 0) return false;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        WordSlot h1 = slots[np1.HeadWord], h2 = slots[np2.HeadWord];
        if (verbSlot.Word.UsesGraph || h1.Word.UsesGraph || h2.Word.UsesGraph) return false;
        if (!IsCleanPredicateToken(verbSlot.Word, IsSentenceInitialWord(analysis, sentence, verb.FiniteCarrierWordIndex, verbSlot.Word), ct))
            return false;
        for (int w = np1.FirstWord; w <= np1.HeadWord; w++)
            if (!IsCleanSubjectToken(slots[w].Word, IsSentenceInitialWord(analysis, sentence, w, slots[w].Word),
                    np1.PronounSubject && w == np1.HeadWord, ct))
                return false;
        for (int w = np2.FirstWord; w <= np2.HeadWord; w++)
            if (!IsCleanSubjectToken(slots[w].Word, IsSentenceInitialWord(analysis, sentence, w, slots[w].Word),
                    np2.PronounSubject && w == np2.HeadWord, ct))
                return false;

        // [P1-I5] Guard coreferential single-referent coordination ("Моят приятел и колега пристигна навреме."):
        // require both coordinated conjuncts to carry independent definiteness/properhood and resolved readings.
        if (np1.HeadReadings.Length == 0 || np2.HeadReadings.Length == 0 ||
            !IsDefiniteOrProperSubjectCandidate(slots, np1) || !IsDefiniteOrProperSubjectCandidate(slots, np2))
            return false;

        // Skip abstract verbal-noun hendiadys ("Писането и четенето изисква време.").
        bool bothVerbalNouns = np1.HeadReadings.All(r => Norm(h1.Word.Readings[r].Lemma) is var l1 &&
                (l1.EndsWith("не", StringComparison.Ordinal) || l1.EndsWith("ние", StringComparison.Ordinal))) &&
            np2.HeadReadings.All(r => Norm(h2.Word.Readings[r].Lemma) is var l2 &&
                (l2.EndsWith("не", StringComparison.Ordinal) || l2.EndsWith("ние", StringComparison.Ordinal)));
        if (bothVerbalNouns) return false;

        int[] allFinite = AllFinitePersonalReadings(verbSlot.Word, ct);
        if (!verb.AllowsPerfectivePresent(map, clause))
            allFinite = FilterIndicativeFinite(verbSlot.Word, allFinite);
        if (allFinite.Length == 0) return false;
        if (!verb.CarrierFiniteReadings.Any(r => verbSlot.Word.Readings[r].Features is
                { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Singular }) ||
            !verb.CarrierFiniteReadings.All(r => verbSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Singular))
            return false;
        if (allFinite.Any(r => verbSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Plural ||
                verbSlot.Word.Readings[r].Features.Person == MorphologicalPerson.First ||
                verbSlot.Word.Readings[r].Features is { Person: MorphologicalPerson.Second, Tense: MorphologicalTense.Present }))
            return false;

        if (verb.CarrierAccusativeCliticWordIndex >= 0 && slots[verb.CarrierAccusativeCliticWordIndex].Word.CanonicalText == "ги")
            return false;
        if (!(np1.FullMasculineSingular || np2.FullMasculineSingular) &&
            HasCompatiblePostverbalSubject(slots, postNps, verbSlot, verb.CarrierFiniteReadings))
            return false;

        bool provenSubject = np1.PronounSubject || np2.PronounSubject ||
            np1.FullMasculineSingular || np2.FullMasculineSingular ||
            verb.CopularCarrier || verb.HasReflexiveSe ||
            verb.CarrierFiniteReadings.All(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                Norm(verbSlot.Word.Readings[r].Lemma) != "играя") ||
            IsIntransitiveMotionOrSpeechLemma(verbSlot, verb.CarrierFiniteReadings) ||
            IsAnimateSubjectWithInanimateOnlyVerb(h1, np1.HeadReadings, verbSlot, verb.CarrierFiniteReadings) ||
            IsAnimateSubjectWithInanimateOnlyVerb(h2, np2.HeadReadings, verbSlot, verb.CarrierFiniteReadings) ||
            postNps.Any(p => !p.TemporalAdjunct);
        if (!provenSubject) return false;

        List<ContextOverlayRoleSelection> coordAttrs = [];
        for (int w = np1.FirstWord; w < np1.HeadWord; w++)
            if (!slots[w].Word.UsesGraph && slots[w].Active.Length > 0)
                coordAttrs.Add(new(ContextOverlayRole.Attribute, slots[w].Word.OriginalIndex,
                    slots[w].Word.Token.Start, slots[w].Word.Token.Length, slots[w].Active, ["clause_fusion_nominal_modifier"]));
        for (int w = np2.FirstWord; w < np2.HeadWord; w++)
            if (!slots[w].Word.UsesGraph && slots[w].Active.Length > 0)
                coordAttrs.Add(new(ContextOverlayRole.Attribute, slots[w].Word.OriginalIndex,
                    slots[w].Word.Token.Start, slots[w].Word.Token.Length, slots[w].Active, ["clause_fusion_nominal_modifier"]));

        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, verbSlot.Word.OriginalIndex, verbSlot.Word.Token.Start, verbSlot.Word.Token.Length,
                allFinite, ["clause_fusion_finite_readings"]),
            new(ContextOverlayRole.Subject, h1.Word.OriginalIndex, h1.Word.Token.Start, h1.Word.Token.Length,
                np1.HeadReadings, ["coordinated_subject_clause_mismatch"]),
            new(ContextOverlayRole.Subject, h2.Word.OriginalIndex, h2.Word.Token.Start, h2.Word.Token.Length,
                np2.HeadReadings, ["coordinated_subject_clause_mismatch"]),
            new(ContextOverlayRole.NominalContext, conjSlot.Word.OriginalIndex, conjSlot.Word.Token.Start, conjSlot.Word.Token.Length,
                conjReadings, ["coordinated_subject_conjunction"]),
            .. coordAttrs
        ];
        agreement.Add(new(verbSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, ["coordinated_subject_clause_mismatch", ContextLanes.ProductiveGraphMismatch]));
        return true;
    }

    private static void CollectClauseArticleTargets(
        SentenceClauseMap map,
        ClauseSpan clause,
        NominalChunk? preSubject,
        bool preCommaBlocked,
        VerbalComplex verb,
        List<NominalChunk> postNps,
        bool[] governed,
        List<ArticleTarget> articles,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        if (verbSlot.Word.UsesGraph) return;

        // 0. Clitic-resumed fronted masculine singular Full-article direct object ("Счупеният прозорец го поправи старият майстор.").
        if (TryCollectCliticResumedFrontedObjectArticle(slots, clauseIdx, bodyStart, preSubject, preCommaBlocked, verb, postNps, articles))
            return;

        // 1. SubjectFull: clause-initial coherent masculine singular Short-article NP before agreeing 3sg finite verb,
        // where subjecthood is structurally established (not a fronted object).
        if (!preCommaBlocked && preSubject is { Coherent: true, ShortMasculineSingular: true, TemporalAdjunct: false } &&
            preSubject.FirstWord == clauseIdx[bodyStart] &&
            !slots[preSubject.FirstWord].Word.UsesGraph &&
            !IsAdverbialTimeOrMeasureHead(slots[preSubject.HeadWord], preSubject) &&
            !IsCliticResumedObject(slots, preSubject, verb) &&
            !IsSobriquetSubject(slots, preSubject) &&
            !HasOtherPossibleSubject(slots, clauseIdx, bodyStart, preSubject, postNps, verb))
        {
            int[] matching3Sg = verb.CarrierFiniteReadings.Where(r =>
            {
                MorphologicalFeatures f = verbSlot.Word.Readings[r].Features;
                return f.Person == MorphologicalPerson.Third && f.Number == MorphologicalNumber.Singular;
            }).ToArray();

            bool provenSubjectRole = verb.CopularCarrier || verb.HasReflexiveSe ||
                verb.CarrierAccusativeCliticWordIndex >= 0 ||
                verb.CarrierFiniteReadings.All(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    Norm(verbSlot.Word.Readings[r].Lemma) != "играя") ||
                verb.CarrierFiniteReadings.All(r => IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma)) ||
                IsPhaseVerbWithoutDirectObject(verbSlot, verb.CarrierFiniteReadings, postNps) ||
                postNps.Any(p => !p.TemporalAdjunct) ||
                verb.HasDaComplement ||
                HasPostverbalDaComplement(slots, clauseIdx, verb.LastWordIndex) ||
                IsIntransitiveMotionOrSpeechLemma(verbSlot, verb.CarrierFiniteReadings) ||
                IsAnimateSubjectWithInanimateOnlyVerb(slots[preSubject.HeadWord], preSubject.HeadReadings, verbSlot, verb.CarrierFiniteReadings);

            if (matching3Sg.Length > 0 && provenSubjectRole)
            {
                int[] phraseWords = Enumerable.Range(preSubject.FirstWord, preSubject.HeadWord - preSubject.FirstWord + 1).ToArray();
                if (phraseWords.All(w => !slots[w].Word.UsesGraph && slots[w].Word.Readings.Count > 0))
                    articles.Add(new(ArticleExpectationKind.SubjectFull,
                        slots[preSubject.FirstWord].Word.OriginalIndex,
                        phraseWords,
                        verbSlot.Word.OriginalIndex,
                        matching3Sg));
            }
        }

        // 2. DirectObjectShort: immediately postverbal coherent masculine singular Full-article NP after a transitive-capable,
        // non-copular, non-linking, non-reflexive verb (including compound active participle & modal/future-in-past да-chains)
        // whose subject is already established.
        bool useLexicalHead = verb.HasActiveParticipleTail || verb.HasDaComplement;
        WordSlot lexSlot = useLexicalHead ? slots[verb.LexicalHeadWordIndex] : verbSlot;
        int[] lexReadings = useLexicalHead ? verb.LexicalReadings : verb.CarrierFiniteReadings;
        int anchorVerbWord = verb.LastWordIndex;
        List<NominalChunk> targetPostNps = postNps;
        bool[] targetGoverned = governed;
        bool hasSubstantivizedPartTail = false;

        if (verb.HasDaComplement && postNps.Count == 0)
        {
            int lexPos = Array.IndexOf(clauseIdx, verb.LexicalHeadWordIndex);
            int daPostEndPos = clauseIdx.Length - 1;
            for (int p = lexPos + 1; p < clauseIdx.Length; p++)
                if (slots[clauseIdx[p]].Word.CanonicalText == "да")
                {
                    daPostEndPos = p - 1;
                    break;
                }
            if (lexPos >= 0 && lexPos + 1 <= daPostEndPos)
            {
                anchorVerbWord = verb.LexicalHeadWordIndex;
                targetGoverned = MarkGovernedPhrases(map, clauseIdx, lexPos + 1, daPostEndPos, lexPos, lexPos, ct);
                targetPostNps = CollectNominalChunks(map, clauseIdx, lexPos + 1, daPostEndPos, targetGoverned, ct);
            }
        }
        else if (verb.CopularCarrier && !verb.HasActiveParticipleTail && !verb.HasDaComplement)
        {
            int auxPos = Array.IndexOf(clauseIdx, verb.LastWordIndex);
            if (auxPos >= 0 && auxPos + 1 < clauseIdx.Length &&
                clauseIdx[auxPos + 1] == verb.LastWordIndex + 1 &&
                !slots[clauseIdx[auxPos + 1]].Quoted &&
                map.AreSpaceBridged(verb.LastWordIndex, clauseIdx[auxPos + 1], ct) &&
                TryGetSubstantivizedActiveParticipleReadings(
                    slots[clauseIdx[auxPos + 1]].Word, verbSlot, verb.CarrierFiniteReadings, out int[] partRows))
            {
                hasSubstantivizedPartTail = true;
                lexSlot = slots[clauseIdx[auxPos + 1]];
                lexReadings = partRows;
                anchorVerbWord = clauseIdx[auxPos + 1];
                targetPostNps = postNps.Where(np => np.FirstWord > anchorVerbWord).ToList();
            }
        }

        bool noObjectCliticOrSe = !verb.HasReflexiveSe && verb.CarrierAccusativeCliticWordIndex < 0 &&
            (!verb.HasDaComplement || verb.PostDaOrAuxClitics is { HasReflexiveSe: false, AccusativeCliticWordIndex: < 0 });

        if ((!verb.CopularCarrier || verb.HasActiveParticipleTail || hasSubstantivizedPartTail) &&
            !lexSlot.Word.UsesGraph && noObjectCliticOrSe && targetPostNps.Count > 0)
        {
            NominalChunk obj = targetPostNps[0];
            int verbLastPos = Array.IndexOf(clauseIdx, anchorVerbWord);
            int objFirstPos = Array.IndexOf(clauseIdx, obj.FirstWord);
            bool adjacentToVerb = objFirstPos == verbLastPos + 1 &&
                map.AreSpaceBridged(anchorVerbWord, obj.FirstWord, ct);
            bool strictlyTransitive = lexReadings.Length > 0 && lexReadings.All(r =>
                lexSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                !TextPredicateLinkAnalyzer.CopularLemma(lexSlot.Word.Readings[r].Lemma) &&
                !IsLinkingOrSemiCopularLemma(lexSlot.Word.Readings[r].Lemma));
            bool transitiveCapable = lexReadings.Length > 0 &&
                lexReadings.Any(r => lexSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive) &&
                lexReadings.All(r => !TextPredicateLinkAnalyzer.CopularLemma(lexSlot.Word.Readings[r].Lemma) &&
                    !IsLinkingOrSemiCopularLemma(lexSlot.Word.Readings[r].Lemma));
            bool strictSubjectOccupied = IsExclusivelyFirstOrSecond(verbSlot.Word, verb.CarrierFiniteReadings) ||
                verb.HasDaComplement && IsExclusivelyFirstOrSecond(lexSlot.Word, lexReadings) ||
                !preCommaBlocked && preSubject is { Coherent: true, TemporalAdjunct: false } &&
                (preSubject.PronounSubject || preSubject.FullMasculineSingular ||
                 preSubject.Number == MorphologicalNumber.Plural &&
                 (verb.CarrierFiniteReadings.All(r => verbSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Plural) ||
                  verb.ImpersonalModal && lexReadings.All(r => lexSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Plural)));
            bool subjectAlreadyOccupied = strictSubjectOccupied ||
                !preCommaBlocked && strictlyTransitive && preSubject is { Coherent: true, TemporalAdjunct: false, ShortMasculineSingular: false } &&
                (MatchingVerbReadings(verbSlot, verb.CarrierFiniteReadings, preSubject).Length > 0 ||
                 verb.ImpersonalModal && MatchingVerbReadings(lexSlot, lexReadings, preSubject).Length > 0);

            if (adjacentToVerb && transitiveCapable && subjectAlreadyOccupied &&
                obj is { Coherent: true, FullMasculineSingular: true, TemporalAdjunct: false } &&
                !IsAdverbialTimeOrMeasureHead(slots[obj.HeadWord], obj) &&
                !targetGoverned[objFirstPos])
            {
                int[] phraseWords = Enumerable.Range(obj.FirstWord, obj.HeadWord - obj.FirstWord + 1).ToArray();
                if (phraseWords.All(w => !slots[w].Word.UsesGraph && slots[w].Word.Readings.Count > 0))
                    articles.Add(new(ArticleExpectationKind.DirectObjectShort,
                        slots[obj.FirstWord].Word.OriginalIndex,
                        phraseWords,
                        lexSlot.Word.OriginalIndex,
                        lexReadings));
            }
        }
    }

    private static bool TryGetSubstantivizedActiveParticipleReadings(
        TokenView word, WordSlot auxSlot, int[] auxReadings, out int[] partReadings)
    {
        partReadings = [];
        if (word.UsesGraph || !word.Complete || word.Readings.Count == 0) return false;
        bool hasNonPlaceholder = word.Readings.Any(r => !Guard.IsSourcePlaceholder(word, r));
        List<int> rows = [];
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ReadingView row = word.Readings[i];
            if (hasNonPlaceholder && Guard.IsSourcePlaceholder(word, row) ||
                Guard.IsUnwrittenCapitalizedReading(word, row, sentenceInitial: false) ||
                Guard.IsSyncreticVocativeCompanion(word, row))
                continue;
            MorphologicalFeatures f = row.Features;
            if (f is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, ParadigmId: "5" } &&
                Norm(row.Lemma).EndsWith('л'))
                continue;
            if (f is not
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    HasConflictingFeatures: false,
                    Article: null or MorphologicalArticle.Indefinite
                } ||
                !SentenceClauseMap.IsCleanOrBenignLegacyVerb(row) ||
                TextPredicateLinkAnalyzer.CopularLemma(row.Lemma))
                return false;
            rows.Add(i);
        }
        if (rows.Count == 0) return false;
        int[] agreed = rows.Where(pr => auxReadings.Any(ar =>
            auxSlot.Word.Readings[ar].Features.Number == word.Readings[pr].Features.Number)).ToArray();
        if (agreed.Length == 0) return false;
        partReadings = agreed;
        return true;
    }

    private static void CollectCoordinatedSubjectArticleTargets(
        SentenceClauseMap map,
        ClauseSpan clause,
        int verbFirstPos,
        NominalChunk[] nonTemporalPreNps,
        VerbalComplex verb,
        List<ArticleTarget> articles,
        CancellationToken ct)
    {
        if (nonTemporalPreNps.Length != 2) return;
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        NominalChunk np1 = nonTemporalPreNps[0], np2 = nonTemporalPreNps[1];
        if (!np1.Coherent || !np2.Coherent || np1.FirstWord != clauseIdx[bodyStart])
            return;

        int np1LastPos = Array.IndexOf(clauseIdx, np1.LastWord);
        int np2FirstPos = Array.IndexOf(clauseIdx, np2.FirstWord);
        int np2LastPos = Array.IndexOf(clauseIdx, np2.LastWord);
        if (np1LastPos < bodyStart || np2FirstPos != np1LastPos + 2 || np2LastPos < np2FirstPos ||
            map.HasDirectCommaBetween(clauseIdx, np2LastPos, verbFirstPos))
            return;

        WordSlot conjSlot = slots[clauseIdx[np1LastPos + 1]];
        if (conjSlot.Word.UsesGraph || conjSlot.Word.CanonicalText != "и" ||
            !map.AreSpaceBridged(np1.LastWord, conjSlot.Index, ct) ||
            !map.AreSpaceBridged(conjSlot.Index, np2.FirstWord, ct))
            return;

        // [P0-I6] Route mixed [FullMasc + и + ShortMasc] / [ShortMasc + и + FullMasc] coordinated subjects before an agreeing 3pl verb
        // through ArticleTarget(SubjectFull) so TextValidationAnalyzer runs its G5 single-paradigm lemma check.
        NominalChunk? shortTarget = np1.FullMasculineSingular && np2.ShortMasculineSingular ? np2
            : np2.FullMasculineSingular && np1.ShortMasculineSingular ? np1
            : null;
        if (shortTarget is null || IsSobriquetSubject(slots, shortTarget) || IsAdverbialTimeOrMeasureHead(slots[shortTarget.HeadWord], shortTarget))
            return;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        if (verbSlot.Word.UsesGraph) return;
        int[] matching3Pl = verb.CarrierFiniteReadings.Where(r =>
            verbSlot.Word.Readings[r].Features is { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Plural }).ToArray();
        if (matching3Pl.Length == 0) return;

        int[] phraseWords = Enumerable.Range(shortTarget.FirstWord, shortTarget.HeadWord - shortTarget.FirstWord + 1).ToArray();
        if (phraseWords.All(w => !slots[w].Word.UsesGraph && slots[w].Word.Readings.Count > 0))
        {
            articles.Add(new(ArticleExpectationKind.SubjectFull,
                slots[shortTarget.FirstWord].Word.OriginalIndex,
                phraseWords,
                verbSlot.Word.OriginalIndex,
                matching3Pl));
        }
    }

    private static bool IsPhaseVerbWithoutDirectObject(WordSlot verbSlot, int[] finiteReadings, List<NominalChunk> postNps) =>
        postNps.All(p => p.TemporalAdjunct) &&
        finiteReadings.Length > 0 &&
        finiteReadings.All(r => Norm(verbSlot.Word.Readings[r].Lemma) is
            "продължа" or "продължавам" or "започна" or "започвам" or
            "спра" or "спирам" or "завърша" or "завършвам" or "приключа" or "приключвам");

    private static bool IsCliticResumedFrontedFullObject(
        WordSlot[] slots, NominalChunk np, VerbalComplex verb, List<NominalChunk> postNps)
    {
        if (!np.FullMasculineSingular || verb.CarrierAccusativeCliticWordIndex < 0 ||
            slots[verb.CarrierAccusativeCliticWordIndex].Word.CanonicalText != "го")
            return false;
        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        return IsExclusivelyFirstOrSecond(verbSlot.Word, verb.CarrierFiniteReadings) ||
            postNps.Any(p => !p.TemporalAdjunct && p.Coherent && IsDefiniteOrProperSubjectCandidate(slots, p) &&
                MatchingVerbReadings(verbSlot, verb.CarrierFiniteReadings, p).Length > 0);
    }

    private static bool TryCollectCliticResumedFrontedObjectArticle(
        WordSlot[] slots,
        int[] clauseIdx,
        int bodyStart,
        NominalChunk? preNp,
        bool preCommaBlocked,
        VerbalComplex verb,
        List<NominalChunk> postNps,
        List<ArticleTarget> articles)
    {
        if (preCommaBlocked ||
            preNp is not { Coherent: true, FullMasculineSingular: true, TemporalAdjunct: false } ||
            preNp.FirstWord != clauseIdx[bodyStart] ||
            IsAdverbialTimeOrMeasureHead(slots[preNp.HeadWord], preNp) ||
            verb.CopularCarrier || verb.HasReflexiveSe ||
            verb.CarrierAccusativeCliticWordIndex < 0 ||
            slots[verb.CarrierAccusativeCliticWordIndex].Word.CanonicalText != "го")
            return false;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        bool useLexicalHead = verb.HasActiveParticipleTail || verb.HasDaComplement;
        WordSlot lexSlot = useLexicalHead ? slots[verb.LexicalHeadWordIndex] : verbSlot;
        int[] lexReadings = useLexicalHead ? verb.LexicalReadings : verb.CarrierFiniteReadings;
        if (lexSlot.Word.UsesGraph || lexReadings.Length == 0 ||
            !lexReadings.All(r => lexSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                !TextPredicateLinkAnalyzer.CopularLemma(lexSlot.Word.Readings[r].Lemma) &&
                !IsLinkingOrSemiCopularLemma(lexSlot.Word.Readings[r].Lemma)))
            return false;

        if (!IsCliticResumedFrontedFullObject(slots, preNp, verb, postNps))
            return false;

        int[] phraseWords = Enumerable.Range(preNp.FirstWord, preNp.HeadWord - preNp.FirstWord + 1).ToArray();
        if (phraseWords.Any(w => slots[w].Word.UsesGraph || slots[w].Word.Readings.Count == 0))
            return false;

        articles.Add(new(ArticleExpectationKind.DirectObjectShort,
            slots[preNp.FirstWord].Word.OriginalIndex,
            phraseWords,
            lexSlot.Word.OriginalIndex,
            lexReadings));
        return true;
    }

    private static void CollectBareParticipleClauseArticleTargets(
        SentenceClauseMap map,
        ClauseSpan clause,
        List<ArticleTarget> articles,
        CancellationToken ct)
    {
        if (!map.HasTerminalPunctuation ||
            map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length).TrimEnd().EndsWith("!", StringComparison.Ordinal))
            return;

        WordSlot[] slots = map.Slots;
        int[] idx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        int partPos = -1;
        int[] partReadings = [];

        for (int p = bodyStart + 1; p < idx.Length; p++)
        {
            WordSlot s = slots[idx[p]];
            if (!IsExactWord(s) || HasFiniteReading(s)) continue;
            int[] rows = NonPlaceholderReadings(s.Word, idx[p] == 0);
            if (rows.Length == 0) continue;
            if (rows.All(r => s.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    Article: null or MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false,
                    HasUnrecognizedFeatures: false
                }))
            {
                if (partPos >= 0) return;
                partPos = p;
                partReadings = rows.Order().ToArray();
            }
        }

        if (partPos < 0) return;
        WordSlot partSlot = slots[idx[partPos]];
        if (!partReadings.All(r => partSlot.Word.Readings[r].Features is
            { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular }))
            return;

        bool[] governed = MarkGovernedPhrases(map, idx, bodyStart, idx.Length - 1, partPos, partPos, ct);
        if (governed[partPos] || HasCoordinatedPreverbalNominals(map, idx, bodyStart, partPos - 1, governed, ct))
            return;

        List<NominalChunk> preNps = CollectNominalChunks(map, idx, bodyStart, partPos - 1, governed, ct);
        List<NominalChunk> postNps = CollectNominalChunks(map, idx, partPos + 1, idx.Length - 1, governed, ct);
        NominalChunk[] nonTemporalPre = preNps.Where(np => !np.TemporalAdjunct).ToArray();
        if (nonTemporalPre is not [{ Coherent: true, ShortMasculineSingular: true } preSubject] ||
            preSubject.FirstWord != idx[bodyStart] ||
            slots[preSubject.FirstWord].Word.UsesGraph ||
            map.HasDirectCommaBetween(idx, Array.IndexOf(idx, preSubject.LastWord), partPos) ||
            IsSobriquetSubject(slots, preSubject) ||
            IsAdverbialTimeOrMeasureHead(slots[preSubject.HeadWord], preSubject) ||
            postNps.Any(np => np.FullMasculineSingular))
            return;

        bool hasReflexiveSe = partPos > bodyStart && slots[idx[partPos - 1]].Word.CanonicalText == "се" &&
                map.AreSpaceBridged(idx[partPos - 1], idx[partPos], ct) ||
            partPos + 1 < idx.Length && slots[idx[partPos + 1]].Word.CanonicalText == "се" &&
                map.AreSpaceBridged(idx[partPos], idx[partPos + 1], ct);
        bool provenSubject = hasReflexiveSe ||
            partReadings.All(r => partSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                Norm(partSlot.Word.Readings[r].Lemma) != "играя") ||
            partReadings.All(r => IsLinkingOrSemiCopularLemma(partSlot.Word.Readings[r].Lemma)) ||
            IsPhaseVerbWithoutDirectObject(partSlot, partReadings, postNps) ||
            postNps.Any(np => !np.TemporalAdjunct) ||
            IsIntransitiveMotionOrSpeechLemma(partSlot, partReadings) ||
            IsAnimateSubjectWithInanimateOnlyVerb(slots[preSubject.HeadWord], preSubject.HeadReadings, partSlot, partReadings);

        if (!provenSubject) return;
        int[] phraseWords = Enumerable.Range(preSubject.FirstWord, preSubject.HeadWord - preSubject.FirstWord + 1).ToArray();
        if (phraseWords.All(w => !slots[w].Word.UsesGraph && slots[w].Word.Readings.Count > 0))
            articles.Add(new(ArticleExpectationKind.SubjectFull,
                slots[preSubject.FirstWord].Word.OriginalIndex,
                phraseWords,
                partSlot.Word.OriginalIndex,
                partReadings));
    }

    private static bool[] MarkGovernedPhrases(
        SentenceClauseMap map, int[] idx, int fromPos, int toPos, int verbFirstPos, int verbLastPos, CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        bool[] governed = new bool[idx.Length];
        for (int p = fromPos; p <= toPos; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (p >= verbFirstPos && p <= verbLastPos) continue;
            WordSlot s = slots[idx[p]];
            if (s.Quoted || !IsActivePreposition(s.Word, s.Active)) continue;
            governed[p] = true;
            bool sawNominal = false;
            for (int q = p + 1; q <= toPos; q++)
            {
                if (q >= verbFirstPos && q <= verbLastPos) break;
                WordSlot cur = slots[idx[q]];
                if (cur.Quoted)
                {
                    governed[q] = true;
                    sawNominal = true;
                    continue;
                }
                if (!map.AreSpaceBridged(idx[q - 1], idx[q], ct) && !slots[idx[q - 1]].Quoted &&
                    !(sawNominal && HasDigitOnlyBridge(map.Analysis, slots[idx[q - 1]].Word, cur.Word) && IsIndefiniteCountedNoun(cur)))
                {
                    // Allow "от девет до пет часа": nested preposition starts its own loop iteration.
                    break;
                }
                if (IsActivePreposition(cur.Word, cur.Active)) break;
                if (IsPossessiveClitic(cur.Word.CanonicalText) && sawNominal)
                {
                    governed[q] = true;
                    break;
                }
                if (!sawNominal && cur.Word.CanonicalText is "дори" or "даже" or "само" or "едва" or "почти" or "чак" or "тъкмо" or "именно")
                {
                    governed[q] = true;
                    continue;
                }
                if (cur.ComparativeGraphModifier || IsNominalModifierOrDeterminer(cur) || IsNumeralWord(cur) ||
                    cur.Word.CanonicalText is "тоя" or "тая" or "туй" or "тия" or "оня" or "оная" or "онуй" or "ония" or "тез" or "онез")
                {
                    governed[q] = true;
                    sawNominal = true;
                    continue;
                }
                if (HasNominalHeadReading(cur) || IsRelativeWord(cur.Word.CanonicalText))
                {
                    governed[q] = true;
                    sawNominal = true;
                    if (q + 1 <= toPos && !(q + 1 >= verbFirstPos && q + 1 <= verbLastPos) && slots[idx[q + 1]].Quoted)
                        continue;
                    // Continue only if possessive clitic follows; otherwise stop at the governed head noun.
                    if (q + 1 <= toPos && !(q + 1 >= verbFirstPos && q + 1 <= verbLastPos) &&
                        map.AreSpaceBridged(cur.Index, idx[q + 1], ct) &&
                        IsPossessiveClitic(slots[idx[q + 1]].Word.CanonicalText))
                        continue;
                    // Handle numeral + noun + appositive noun ("с два гола разлика") or classifier + proper name ("на село Тодорово").
                    if (q + 1 <= toPos && !(q + 1 >= verbFirstPos && q + 1 <= verbLastPos) &&
                        map.AreSpaceBridged(cur.Index, idx[q + 1], ct) &&
                        (HasPureNominal(slots[idx[q + 1]].Word, slots[idx[q + 1]].Active) &&
                         q > p + 1 && IsNumeralWord(slots[idx[q - 1]]) ||
                         cur.Active.Any(r => Norm(cur.Word.Readings[r].Lemma) is
                             "село" or "град" or "община" or "област" or "улица" or "булевард" or "връх" or "река" or "езеро") &&
                         slots[idx[q + 1]].Word.Token.Text.Length > 0 && char.IsUpper(slots[idx[q + 1]].Word.Token.Text[0]) &&
                         slots[idx[q + 1]].Active.Any(r => slots[idx[q + 1]].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun)))
                        continue;
                    // Handle measure/capacity noun + cardinal digits + counted noun ("с вместимост 12 266 зрители").
                    if (q + 1 <= toPos && !(q + 1 >= verbFirstPos && q + 1 <= verbLastPos) &&
                        HasDigitOnlyBridge(map.Analysis, cur.Word, slots[idx[q + 1]].Word) &&
                        IsIndefiniteCountedNoun(slots[idx[q + 1]]))
                        continue;
                    break;
                }
                break;
            }
        }
        return governed;
    }

    private static bool HasDigitOnlyBridge(TextAnalysis analysis, TokenView left, TokenView right)
    {
        int from = left.Token.Start + left.Token.Length;
        int to = right.Token.Start;
        if (to <= from) return false;
        bool sawDigit = false;
        foreach (char ch in analysis.Text.AsSpan(from, to - from))
        {
            if (char.IsAsciiDigit(ch)) sawDigit = true;
            else if (!char.IsWhiteSpace(ch)) return false;
        }
        return sawDigit;
    }

    private static bool IsIndefiniteCountedNoun(WordSlot slot) =>
        HasPureNominal(slot.Word, slot.Active) &&
        slot.Active.Any(r =>
        {
            MorphologicalFeatures f = slot.Word.Readings[r].Features;
            return f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                   f.Article == MorphologicalArticle.Indefinite &&
                   f.Number is MorphologicalNumber.Plural or MorphologicalNumber.Count;
        });

    private static List<NominalChunk> CollectNominalChunks(
        SentenceClauseMap map,
        int[] idx,
        int fromPos,
        int toPos,
        bool[] governed,
        CancellationToken ct)
    {
        SentenceSpan sentence = map.Sentence;
        WordSlot[] slots = map.Slots;
        List<NominalChunk> chunks = [];
        for (int p = fromPos; p <= toPos; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (p < 0 || p >= idx.Length || governed[p]) continue;
            WordSlot s = slots[idx[p]];
            if (s.Quoted || s.Word.UsesGraph) continue;

            // 1. Explicit nominative personal pronoun (аз, ти, той, тя, то, ние, вие, те) or никой.
            WordSlot? previous = p > 0 && map.AreSpaceBridged(idx[p - 1], idx[p], ct)
                ? slots[idx[p - 1]] : null;
            int[] pronSubject = NominativePronounReadings(s, previous);
            if (pronSubject.Length > 0)
            {
                MorphologicalFeatures pf = s.Word.Readings[pronSubject[0]].Features;
                MorphologicalPerson person = pf.Person ?? MorphologicalPerson.Third;
                MorphologicalNumber? number = pf.Number ?? MorphologicalNumber.Singular;
                chunks.Add(new(idx[p], idx[p], idx[p], pronSubject, person, number, pf.Gender,
                    PronounSubject: s.Word.CanonicalText != "никой",
                    FullMasculineSingular: false, ShortMasculineSingular: false,
                    TemporalAdjunct: false, Coherent: true));
                continue;
            }

            // 2. Standalone numeral-pronoun head before PP ("Едно от децата играе.").
            if (s.Word.CanonicalText is "един" or "една" or "едно" or "едни" &&
                (p == toPos || governed[p + 1] || !map.AreSpaceBridged(idx[p], idx[p + 1], ct)))
            {
                int[] numRows = s.Active.Where(r => Guard.TryGetDeterminerAxes(s.Word, s.Word.Readings[r], out _)).ToArray();
                if (numRows.Length > 0 && Guard.TryGetDeterminerAxes(s.Word, s.Word.Readings[numRows[0]], out var axes))
                {
                    chunks.Add(new(idx[p], idx[p], idx[p], numRows, MorphologicalPerson.Third, axes.Number, axes.Gender,
                        PronounSubject: false, FullMasculineSingular: false, ShortMasculineSingular: false,
                        TemporalAdjunct: false, Coherent: true));
                    continue;
                }
            }
        }

        // 3. Noun / ProperNoun-headed chunks (scanning left-to-right over ungoverned spans).
        int cursor = fromPos;
        while (cursor <= toPos)
        {
            ct.ThrowIfCancellationRequested();
            if (cursor < 0 || governed[cursor] || slots[idx[cursor]].Quoted ||
                chunks.Any(c => c.FirstWord <= idx[cursor] && idx[cursor] <= c.LastWord))
            {
                cursor++;
                continue;
            }
            // Find maximal contiguous modifier* + head Noun/ProperNoun run starting at or after cursor.
            int modStart = cursor;
            WordSlot startSlot = slots[idx[modStart]];
            bool isQuantifier = startSlot.Word.CanonicalText is
                    "много" or "многото" or "малко" or "немалко" or "повече" or "повечето" or "няколко" or "толкова" or "колко" or "хиляди" ||
                IsNumeralWord(startSlot);
            bool isModifier = startSlot.ComparativeGraphModifier || isQuantifier || IsNominalModifierOrDeterminer(startSlot);

            int headPos = -1;
            if (HasNominalHeadReading(startSlot) &&
                !(isModifier && modStart + 1 <= toPos && !governed[modStart + 1] && !slots[idx[modStart + 1]].Quoted &&
                  map.AreSpaceBridged(idx[modStart], idx[modStart + 1], ct) &&
                  (slots[idx[modStart + 1]].ComparativeGraphModifier ||
                   IsNominalModifierOrDeterminer(slots[idx[modStart + 1]]) ||
                   HasNominalHeadReading(slots[idx[modStart + 1]]))))
            {
                headPos = modStart;
            }
            else if (isModifier)
            {
                int scan = modStart + 1;
                while (scan <= toPos && !governed[scan] && !slots[idx[scan]].Quoted &&
                       map.AreSpaceBridged(idx[scan - 1], idx[scan], ct))
                {
                    WordSlot cur = slots[idx[scan]];
                    if (HasNominalHeadReading(cur) &&
                        !(IsNominalModifierOrDeterminer(cur) && scan + 1 <= toPos && !governed[scan + 1] && !slots[idx[scan + 1]].Quoted &&
                          map.AreSpaceBridged(idx[scan], idx[scan + 1], ct) &&
                          HasNominalHeadReading(slots[idx[scan + 1]])))
                    {
                        headPos = scan;
                        break;
                    }
                    if (cur.ComparativeGraphModifier || IsNominalModifierOrDeterminer(cur) || IsNumeralWord(cur))
                    {
                        scan++;
                        continue;
                    }
                    break;
                }
            }

            if (headPos < 0)
            {
                cursor++;
                continue;
            }

            WordSlot headSlot = slots[idx[headPos]];
            int lastWord = idx[headPos];
            if (headPos + 1 <= toPos && !governed[headPos + 1] && !slots[idx[headPos + 1]].Quoted &&
                map.AreSpaceBridged(idx[headPos], idx[headPos + 1], ct) &&
                IsPossessiveClitic(slots[idx[headPos + 1]].Word.CanonicalText))
            {
                lastWord = idx[headPos + 1];
            }

            if (headSlot.HyphenatedCompoundNominal is { } compAxes)
            {
                WordSlot compCarrier = slots[idx[modStart]];
                bool compCoherent = AssessHyphenatedCompoundCoherence(slots, idx, modStart, headPos, compAxes, ct);
                bool compFullMasc = modStart == headPos
                    ? compAxes is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Full }
                    : IsCarrierArticle(compCarrier, MorphologicalArticle.Full);
                bool compShortMasc = modStart == headPos
                    ? compAxes is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Short }
                    : IsCarrierArticle(compCarrier, MorphologicalArticle.Short) &&
                      !(modStart > fromPos && IsNumeralWord(slots[idx[modStart - 1]]));
                chunks.Add(new(
                    FirstWord: idx[modStart],
                    HeadWord: idx[headPos],
                    LastWord: lastWord,
                    HeadReadings: [],
                    Person: MorphologicalPerson.Third,
                    Number: compAxes.Number,
                    Gender: compAxes.Gender,
                    PronounSubject: false,
                    FullMasculineSingular: compFullMasc,
                    ShortMasculineSingular: compShortMasc,
                    TemporalAdjunct: false,
                    Coherent: compCoherent));
                cursor = (lastWord == idx[headPos] ? headPos : headPos + 1) + 1;
                continue;
            }

            int[] headRows = NominalHeadReadings(headSlot);
            if (headRows.Length == 0)
            {
                cursor = headPos + 1;
                continue;
            }

            bool coherent = AssessChunkCoherence(slots, idx, modStart, headPos, headRows, sentence, ct,
                out MorphologicalNumber? number, out MorphologicalGender? gender, out int[] agreedHeadRows);
            WordSlot carrierSlot = slots[idx[modStart]];
            bool fullMasc = IsCarrierArticle(carrierSlot, MorphologicalArticle.Full);
            bool shortMasc = IsCarrierArticle(carrierSlot, MorphologicalArticle.Short) &&
                !(modStart > fromPos && IsNumeralWord(slots[idx[modStart - 1]]));
            bool temporal = !fullMasc && IsTemporalAdjunctChunk(slots, idx, fromPos, modStart, headPos);

            chunks.Add(new(
                FirstWord: idx[modStart],
                HeadWord: idx[headPos],
                LastWord: lastWord,
                HeadReadings: agreedHeadRows.Length > 0 ? agreedHeadRows : headRows,
                Person: MorphologicalPerson.Third,
                Number: number,
                Gender: gender,
                PronounSubject: false,
                FullMasculineSingular: fullMasc,
                ShortMasculineSingular: shortMasc,
                TemporalAdjunct: temporal,
                Coherent: coherent));

            cursor = (lastWord == idx[headPos] ? headPos : headPos + 1) + 1;
        }

        return chunks.OrderBy(c => c.FirstWord).ToList();
    }

    private static bool AssessChunkCoherence(
        WordSlot[] slots,
        int[] idx,
        int modStart,
        int headPos,
        int[] headRows,
        SentenceSpan sentence,
        CancellationToken ct,
        out MorphologicalNumber? number,
        out MorphologicalGender? gender,
        out int[] agreedHeadRows)
    {
        WordSlot headSlot = slots[idx[headPos]];
        bool hasCountTrigger = Enumerable.Range(modStart, headPos - modStart).Any(m =>
            slots[idx[m]].Word.CanonicalText is "няколко" or "колко" or "толкова" ||
            IsNumeralWord(slots[idx[m]]) &&
            !slots[idx[m]].Active.Any(r => Guard.TryGetDeterminerAxes(slots[idx[m]].Word, slots[idx[m]].Word.Readings[r], out _)));
        bool hasSingularHead = headRows.Any(r => headSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Singular);
        bool hasCountHead = headRows.Any(r => headSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Count);
        List<int> compatibleHeads = [];
        foreach (int hr in headRows)
        {
            ct.ThrowIfCancellationRequested();
            ReadingView headRow = headSlot.Word.Readings[hr];
            MorphologicalFeatures hf = headRow.Features;
            if (!hasCountTrigger && hf.Number == MorphologicalNumber.Count && hasSingularHead) continue;
            if (hasCountTrigger && hf.Number == MorphologicalNumber.Singular && hasCountHead) continue;
            MorphologicalNumber? hn = hf.Number == MorphologicalNumber.Count ? MorphologicalNumber.Plural : hf.Number;
            if (hn is null && hf.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun)
                hn = MorphologicalNumber.Singular;
            if (hn is not (MorphologicalNumber.Singular or MorphologicalNumber.Plural)) continue;

            bool allModifiersAgree = true;
            for (int m = modStart; m < headPos; m++)
            {
                WordSlot ms = slots[idx[m]];
                if (ms.ComparativeGraphModifier)
                {
                    if (ms.Word.Readings.Count > 0 &&
                        !ms.Word.Readings.Any(mr => Guard.ModifierAgreement(ms.Word, mr, headRow,
                            includeAdnominalReadings: true, phraseInitial: m == modStart)))
                    {
                        allModifiersAgree = false;
                        break;
                    }
                    continue;
                }
                if (ms.Word.CanonicalText is "много" or "многото" or "малко" or "немалко" or "повече" or "повечето" or "няколко" or "хиляди")
                {
                    if (hf.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
                        ms.Word.CanonicalText is "многото" or "повечето" && m != modStart ||
                        ms.Word.CanonicalText is "няколко" or "хиляди" && hn != MorphologicalNumber.Plural)
                    {
                        allModifiersAgree = false;
                        break;
                    }
                    continue;
                }
                if (IsNumeralWord(ms) && !ms.Active.Any(r => Guard.TryGetDeterminerAxes(ms.Word, ms.Word.Readings[r], out _)))
                    continue;
                if (!Guard.DeterminerAllowsArticledHead(ms.Word) &&
                    ms.Active.All(r => Guard.TryGetDeterminerAxes(ms.Word, ms.Word.Readings[r], out _)) &&
                    hf.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short)
                {
                    allModifiersAgree = false;
                    break;
                }
                bool anyModRow = ms.Active.Any(mr =>
                {
                    ReadingView mRow = ms.Word.Readings[mr];
                    if (m == modStart && Guard.IsSuppressedDeterminerHomograph(ms.Word, mRow)) return false;
                    return Guard.ModifierAgreement(ms.Word, mRow, headRow, includeAdnominalReadings: true, phraseInitial: m == modStart);
                });
                if (!anyModRow)
                {
                    allModifiersAgree = false;
                    break;
                }
            }
            if (allModifiersAgree)
                compatibleHeads.Add(hr);
        }

        agreedHeadRows = compatibleHeads.ToArray();
        if (compatibleHeads.Count == 0)
        {
            MorphologicalFeatures f0 = headSlot.Word.Readings[headRows[0]].Features;
            number = f0.Number == MorphologicalNumber.Count ? MorphologicalNumber.Plural : (f0.Number ?? MorphologicalNumber.Singular);
            gender = f0.Gender;
            return false;
        }

        var distinctNumbers = compatibleHeads
            .Select(r =>
            {
                var n = headSlot.Word.Readings[r].Features.Number;
                return n == MorphologicalNumber.Count ? MorphologicalNumber.Plural : (n ?? MorphologicalNumber.Singular);
            })
            .Distinct()
            .ToArray();
        number = distinctNumbers.Length == 1 ? distinctNumbers[0] : null;

        if (modStart == headPos && compatibleHeads.Count > 1 &&
            compatibleHeads.All(r => headSlot.Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.ProperNoun, ParadigmId: "207" }))
        {
            string c = headSlot.Word.CanonicalText;
            bool isUnisex207 = c is "валя" or "ваня" or "вася" or "гриша" or "дима" or "женя" or "миша" or "паша" or
                "петя" or "саша" or "тоня" or "тони" or "ники" or "боби" or "роси" or "алекс" or "крис" or "дани" ||
                c.EndsWith('и') && c is not ("георги" or "добри" or "методи" or "евгени" or "валери" or "анатоли" or "димитри" or "захари" or "арсени");
            if (isUnisex207)
            {
                gender = null;
            }
            else if ((c.EndsWith('а') || c.EndsWith('я')) &&
                     c is not ("лука" or "тома" or "никола" or "илия" or "сава" or "никита" or "коста" or "мита" or "горица"))
            {
                compatibleHeads = compatibleHeads
                    .Where(r => headSlot.Word.Readings[r].Features.Gender == MorphologicalGender.Feminine)
                    .ToList();
                agreedHeadRows = compatibleHeads.ToArray();
                gender = MorphologicalGender.Feminine;
            }
            else
            {
                compatibleHeads = compatibleHeads
                    .Where(r => headSlot.Word.Readings[r].Features.Gender == MorphologicalGender.Masculine)
                    .ToList();
                agreedHeadRows = compatibleHeads.ToArray();
                gender = MorphologicalGender.Masculine;
            }
        }
        else if (number == MorphologicalNumber.Singular &&
                 compatibleHeads.All(r => Guard.IsEpiceneHumanNounLemma(headSlot.Word.Readings[r].Lemma)))
        {
            HashSet<MorphologicalGender> modGenders = [];
            for (int m = modStart; m < headPos; m++)
            {
                WordSlot ms = slots[idx[m]];
                IEnumerable<ReadingView> mRows = ms.ComparativeGraphModifier
                    ? ms.Word.Readings
                    : ms.Active.Select(r => ms.Word.Readings[r]);
                foreach (ReadingView mRow in mRows)
                {
                    if (m == modStart && Guard.IsSuppressedDeterminerHomograph(ms.Word, mRow)) continue;
                    if (Guard.TryGetDeterminerAxes(ms.Word, mRow, out var detAx))
                    {
                        if (detAx.Number == MorphologicalNumber.Singular &&
                            detAx.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine)
                            modGenders.Add(detAx.Gender.Value);
                    }
                    else if (Guard.TryGetOrdinalAxes(mRow, out var ordAx))
                    {
                        if (ordAx.Number == MorphologicalNumber.Singular &&
                            ordAx.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine)
                            modGenders.Add(ordAx.Gender.Value);
                    }
                    else if (mRow.Features.Number == MorphologicalNumber.Singular &&
                             mRow.Features.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine)
                    {
                        modGenders.Add(mRow.Features.Gender.Value);
                    }
                }
            }
            gender = modGenders.Count == 1 ? modGenders.First() : null;
        }
        else
        {
            gender = headSlot.Word.Readings[compatibleHeads[0]].Features.Gender;
        }
        return distinctNumbers.Length == 1;
    }

    private static bool AssessHyphenatedCompoundCoherence(
        WordSlot[] slots,
        int[] idx,
        int modStart,
        int headPos,
        (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article) compAxes,
        CancellationToken ct)
    {
        if (modStart == headPos) return true;
        MorphologicalFeatures headFeatures = new()
        {
            PartOfSpeech = MorphologicalPartOfSpeech.Noun,
            Number = compAxes.Number,
            Gender = compAxes.Gender,
            Article = compAxes.Article
        };
        for (int m = modStart; m < headPos; m++)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot ms = slots[idx[m]];
            if (ms.ComparativeGraphModifier)
            {
                if (ms.Word.Readings.Count > 0 &&
                    !ms.Word.Readings.Any(mr => Guard.ModifierAgreement(ms.Word, mr, headFeatures,
                        includeAdnominalReadings: true, phraseInitial: m == modStart)))
                    return false;
                continue;
            }
            if (ms.Word.CanonicalText is "много" or "многото" or "малко" or "немалко" or "повече" or "повечето" or "няколко" or "хиляди")
            {
                if (compAxes.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
                    ms.Word.CanonicalText is "многото" or "повечето" && m != modStart ||
                    ms.Word.CanonicalText is "няколко" or "хиляди" && compAxes.Number != MorphologicalNumber.Plural)
                    return false;
                continue;
            }
            if (IsNumeralWord(ms) && !ms.Active.Any(r => Guard.TryGetDeterminerAxes(ms.Word, ms.Word.Readings[r], out _)))
                continue;
            if (!Guard.DeterminerAllowsArticledHead(ms.Word) &&
                ms.Active.All(r => Guard.TryGetDeterminerAxes(ms.Word, ms.Word.Readings[r], out _)) &&
                compAxes.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short)
                return false;
            bool anyModRow = ms.Active.Any(mr =>
            {
                ReadingView mRow = ms.Word.Readings[mr];
                if (m == modStart && Guard.IsSuppressedDeterminerHomograph(ms.Word, mRow)) return false;
                return Guard.ModifierAgreement(ms.Word, mRow, headFeatures, includeAdnominalReadings: true, phraseInitial: m == modStart);
            });
            if (!anyModRow) return false;
        }
        return true;
    }

    private static bool IsTemporalAdjunctChunk(WordSlot[] slots, int[] idx, int clauseStartPos, int modStart, int headPos)
    {
        WordSlot headSlot = slots[idx[headPos]];
        string headLemma = headSlot.Active.Select(r => Norm(headSlot.Word.Readings[r].Lemma)).FirstOrDefault() ?? "";
        string headSurface = headSlot.Word.CanonicalText;
        if (modStart == headPos)
        {
            // Definite day-part nouns ("сутринта", "вечерта", "нощта") function as bare or adverb-modified temporal adjuncts.
            if (headSurface is "сутринта" or "вечерта" or "нощта" ||
                headSurface == "обед" && modStart > clauseStartPos &&
                slots[idx[modStart - 1]].Word.CanonicalText is "вчера" or "днес" or "утре" or "снощи" or "сутрин" or "вечер")
                return true;
            return !headSlot.Word.UsesGraph && headSlot.Active.Length > 0 &&
                headSlot.Active.All(r =>
                {
                    ReadingView rv = headSlot.Word.Readings[r];
                    MorphologicalFeatures f = rv.Features;
                    string l = Norm(rv.Lemma);
                    return f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                           Guard.IsTemporalOrMeasureLemma(l) &&
                           l is not ("път" or "зима" or "лято" or "пролет" or "есен" or "сезон" or "смяна" or "ваканция" or "почивка" or "класа" or "спирка") &&
                           (f.Article == MorphologicalArticle.Indefinite ||
                            f is { Gender: MorphologicalGender.Masculine, Article: MorphologicalArticle.Short });
                });
        }

        string first = slots[idx[modStart]].Word.CanonicalText;
        if (headLemma == "живот")
            return first is "цял" or "целия";
        if (!headSlot.Active.Any(r => Guard.IsTemporalOrMeasureLemma(Norm(headSlot.Word.Readings[r].Lemma))))
            return false;
        if (headLemma == "време" && first is "известно" or "дълго" or "кратко" or "кратичко" or "немалко" or "доста")
            return true;

        return IsNumeralWord(slots[idx[modStart]]) ||
            first is "тази" or "този" or "това" or "тези" or "миналата" or "миналия" or "миналото" or "по-миналата" or
                "следващата" or "следващия" or "следващото" or "идната" or "идния" or "последната" or "последния" or "последното" or
                "всяка" or "всеки" or "всяко" or "цяла" or "цял" or "цялата" or "целия" or "цялото" or
                "една" or "един" or "едно" or "много" or "няколко" or "малко" or "немалко" or "доста" or "стотици" or "хиляди" ||
            (modStart < headPos - 1 && IsNumeralWord(slots[idx[headPos - 1]]) &&
             first is "последните" or "първите" or "следващите" or "миналите" or "тези" or "онези");
    }

    private static bool IsCarrierArticle(WordSlot slot, MorphologicalArticle expected)
    {
        if (slot.Word.UsesGraph || slot.Active.Length == 0) return false;
        string surface = slot.Word.CanonicalText;
        bool sawExpected = false;
        foreach (int r in slot.Active)
        {
            ReadingView rv = slot.Word.Readings[r];
            MorphologicalFeatures f = rv.Features;
            if (Guard.TryGetOrdinalAxes(rv, out var ord))
            {
                if (ord.Gender != MorphologicalGender.Masculine || ord.Number != MorphologicalNumber.Singular)
                    return false;
                bool ordMatches = expected == MorphologicalArticle.Short
                    ? ord.Article == MorphologicalArticle.Short ||
                      ord.Article == MorphologicalArticle.Definite && !surface.EndsWith("ият", StringComparison.Ordinal)
                    : ord.Article == expected ||
                      expected == MorphologicalArticle.Full && ord.Article == MorphologicalArticle.Definite &&
                      surface.EndsWith("ият", StringComparison.Ordinal);
                if (!ordMatches) return false;
                sawExpected = true;
                continue;
            }
            bool adjectivalParticiple = Guard.IsMeasuredPassiveAttribute(f) || Guard.IsMeasuredPresentActiveAttribute(f);
            if (f.PartOfSpeech is not (MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective) && !adjectivalParticiple ||
                f.Gender != MorphologicalGender.Masculine)
                return false;
            bool matchesArticle = expected == MorphologicalArticle.Short
                ? f.Article == MorphologicalArticle.Short ||
                  f.Article == MorphologicalArticle.Definite &&
                  Guard.IsShortMasculineSurface(surface, Norm(slot.Word.Readings[r].Lemma), f.PartOfSpeech, adjectivalParticiple)
                : f.Article == expected ||
                  expected == MorphologicalArticle.Full && adjectivalParticiple &&
                  f.Article == MorphologicalArticle.Definite && surface.EndsWith("ият", StringComparison.Ordinal);
            if (f.Number == MorphologicalNumber.Singular && matchesArticle)
                sawExpected = true;
            else if (!(expected == MorphologicalArticle.Short &&
                       f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                       f.Number == MorphologicalNumber.Count &&
                       f.Article == MorphologicalArticle.Indefinite))
                return false;
        }
        return sawExpected;
    }

    private static int[] MatchingVerbReadings(WordSlot verbSlot, int[] finiteReadings, NominalChunk np)
    {
        if (np.Number is null) return [];
        return finiteReadings.Where(r => Guard.PersonNumberTupleMatches(
            (np.Person, np.Number),
            (verbSlot.Word.Readings[r].Features.Person, verbSlot.Word.Readings[r].Features.Number))).ToArray();
    }

    private static bool AllowsPostverbalSubject(
        TextAnalysis analysis,
        WordSlot verbSlot,
        int[] matchingVerbReadings,
        NominalChunk postCand,
        VerbFrameCatalog? verbFrames,
        double maxDirectObjectRatio) =>
        postCand.FullMasculineSingular ||
        matchingVerbReadings.Any(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive) ||
        verbFrames is not null && AllowsPostverbalSubjectByFrames(analysis, verbSlot, matchingVerbReadings, verbFrames, maxDirectObjectRatio);

    private static bool AllowsPostverbalSubjectByFrames(
        TextAnalysis analysis,
        WordSlot verbSlot,
        int[] matchingVerbReadings,
        VerbFrameCatalog verbFrames,
        double maxDirectObjectRatio)
    {
        LexicalTokenEvidence? lexTok = analysis.Lexical is { MetadataAvailable: true } lexical
            ? lexical.Tokens.FirstOrDefault(t => t.Start == verbSlot.Word.Token.Start && t.Length == verbSlot.Word.Token.Length)
            : null;

        foreach (int r in matchingVerbReadings)
        {
            string normLemma = Norm(verbSlot.Word.Readings[r].Lemma);
            IReadOnlyList<VerbFrameLexemeEntry> candidates = [];
            if (lexTok is { Owners.Truncated: false } && lexTok.Owners.Entries.Count > 0)
            {
                List<VerbFrameLexemeEntry> exactOwners = [];
                foreach (LexicalEntry owner in lexTok.Owners.Entries)
                    if (verbFrames.TryGetByLexemeKey(owner.LexemeKey, out VerbFrameLexemeEntry? entry) &&
                        entry is not null && entry.Lemma == normLemma)
                        exactOwners.Add(entry);
                if (exactOwners.Count > 0)
                    candidates = exactOwners;
            }
            if (candidates.Count == 0)
                candidates = verbFrames.GetByLemma(normLemma);

            if (candidates.Count == 0 ||
                !candidates.All(e => e.NonReflexiveDirectObjectFrameRatio is double ratio && ratio <= maxDirectObjectRatio))
                return false;
        }
        return matchingVerbReadings.Length > 0;
    }

    private static bool IsCliticResumedObject(WordSlot[] slots, NominalChunk np, VerbalComplex verb)
    {
        if (verb.CarrierAccusativeCliticWordIndex < 0 || np.PronounSubject || np.FullMasculineSingular || np.Number is null)
            return false;
        WordSlot cliticSlot = slots[verb.CarrierAccusativeCliticWordIndex];
        return verb.CarrierAccusativeCliticReadings.Any(r =>
        {
            MorphologicalFeatures cf = cliticSlot.Word.Readings[r].Features;
            return cf.Person == MorphologicalPerson.Third &&
                   (Guard.NominalTupleMatches((np.Number, np.Gender), (cf.Number, cf.Gender)) ||
                    np.Number == MorphologicalNumber.Singular && np.Gender is null &&
                    cf.Number == MorphologicalNumber.Singular &&
                    cf.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine);
        });
    }

    private static bool IsSobriquetSubject(WordSlot[] slots, NominalChunk np) =>
        np.FirstWord == np.HeadWord &&
        char.IsUpper(slots[np.HeadWord].Word.Token.Text[0]) &&
        slots[np.HeadWord].Active.Any(r => Norm(slots[np.HeadWord].Word.Readings[r].Lemma) is
            "апостол" or "дякон" or "щастливец" or "моканин" or "левски" or "ботев");

    private static bool HasOtherPossibleSubject(
        WordSlot[] slots, int[] clauseIdx, int bodyStart, NominalChunk candidate,
        List<NominalChunk> postNps, VerbalComplex verb)
    {
        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        for (int p = bodyStart; p < clauseIdx.Length; p++)
        {
            int w = clauseIdx[p];
            if (w >= candidate.FirstWord && w <= candidate.LastWord) continue;
            if (w >= verbSlot.Index) break;
            WordSlot s = slots[w];
            if (IsCarrierArticle(s, MorphologicalArticle.Full) || NominativePronounReadings(s, null).Length > 0)
                return true;
        }
        if (postNps.Any(np => np.FullMasculineSingular)) return true;
        if (postNps.Count == 0 || verb.CopularCarrier || verb.HasReflexiveSe || verb.CarrierAccusativeCliticWordIndex >= 0) return false;

        // A compatible postverbal subject leaves the preverbal nominal available as a fronted object.
        int[] objectTakingReadings = verb.CarrierFiniteReadings.Where(r =>
            verbSlot.Word.Readings[r].Features.DeclaredTransitivity != MorphologicalVerbTransitivity.Intransitive).ToArray();
        return HasCompatiblePostverbalSubject(slots, postNps, verbSlot, objectTakingReadings);
    }

    private static bool HasCompatiblePostverbalSubject(
        WordSlot[] slots, List<NominalChunk> postNps, WordSlot verbSlot, int[] finiteReadings) =>
        postNps.Any(p => !p.TemporalAdjunct && IsDefiniteOrProperSubjectCandidate(slots, p) &&
            MatchingVerbReadings(verbSlot, finiteReadings, p).Length > 0);

    private static bool HasPostverbalDaComplement(WordSlot[] slots, int[] clauseIdx, int verbLastWord)
    {
        int pos = Array.IndexOf(clauseIdx, verbLastWord);
        return pos >= 0 && pos + 1 < clauseIdx.Length && slots[clauseIdx[pos + 1]].Word.CanonicalText == "да";
    }

    private static bool IsIntransitiveMotionOrSpeechLemma(WordSlot verbSlot, int[] finiteReadings) =>
        finiteReadings.Length > 0 &&
        finiteReadings.All(r => Guard.IsIntransitiveMotionOrStateLemma(Norm(verbSlot.Word.Readings[r].Lemma)));

    // These performance/text uses admit cognate or text objects. Пиша also
    // admits a person as the object in its dictionary sense for registering someone.
    private static bool IsAnimateSubjectWithInanimateOnlyVerb(
        WordSlot headSlot, int[] headReadings, WordSlot verbSlot, int[] finiteReadings) =>
        finiteReadings.Length > 0 &&
        finiteReadings.All(r => Guard.IsAnimateSubjectInanimateVerbLemma(Norm(verbSlot.Word.Readings[r].Lemma))) &&
        HasAnimateNominalHead(headSlot, headReadings);

    private static bool HasAnimateNominalHead(WordSlot headSlot, int[] headReadings) =>
        headReadings.Length > 0 && headReadings.All(r => IsAnimateForFusion(headSlot.Word.Readings[r]));

    internal static bool IsAnimateForFusion(ContextLanes.ReadingView row) =>
        Guard.IsAnimateNominalReading(row) ||
        row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
        (row.Features.ParadigmId is "1d" or "18" or "38" or "38a" or "38b" or "39" or "40" ||
         Norm(row.Lemma) is var nl && (nl is "съпруга" or "дъщеря" or "лекарка" or "директорка" or "авторка" or "секретарка" or
             "богиня" or "героиня" or "домакиня" or "монахиня" ||
             nl.EndsWith("истка", StringComparison.Ordinal) || nl.EndsWith("нтка", StringComparison.Ordinal) ||
             nl.EndsWith("ителка", StringComparison.Ordinal) || nl.EndsWith("ателка", StringComparison.Ordinal) ||
             nl.EndsWith("киня", StringComparison.Ordinal)));

    private static bool IsCommaEnclosedGovernedComitativePp(
        SentenceClauseMap map,
        int[] idx,
        WordSlot[] slots,
        NominalChunk subj,
        int verbFirstPos,
        bool[] governed,
        CancellationToken ct)
    {
        if (!subj.Coherent || subj.ShortMasculineSingular || subj.TemporalAdjunct)
            return false;
        if (IsPersonPlaceHomograph(map.Analysis, slots[subj.HeadWord].Word.OriginalIndex))
            return false;
        if (!subj.PronounSubject && !IsDefiniteOrProperSubjectCandidate(slots, subj))
            return false;

        int subjFirstPos = Array.IndexOf(idx, subj.FirstWord);
        int subjLastPos = Array.IndexOf(idx, subj.LastWord);
        if (subjFirstPos < 0 || subjLastPos < subjFirstPos || subjLastPos + 2 >= verbFirstPos ||
            idx[subjLastPos + 1] != subj.LastWord + 1)
        {
            return false;
        }

        if (map.HasDirectCommaBetween(idx, subjFirstPos, subjLastPos))
            return false;

        // Require BOTH opening comma after subj.LastWord AND closing comma immediately before idx[verbFirstPos].
        if (!map.HasCommaBetween(subj.LastWord, idx[subjLastPos + 1]) ||
            idx[verbFirstPos] != idx[verbFirstPos - 1] + 1 ||
            !map.HasCommaBetween(idx[verbFirstPos - 1], idx[verbFirstPos]))
        {
            return false;
        }

        WordSlot prepSlot = slots[idx[subjLastPos + 1]];
        if (prepSlot.Word.CanonicalText is not ("с" or "със") || !IsActivePreposition(prepSlot.Word, prepSlot.Active))
            return false;

        // Require zero internal commas and contiguous space bridging inside (subjLastPos + 1)..(verbFirstPos - 1).
        for (int p = subjLastPos + 1; p < verbFirstPos - 1; p++)
        {
            if (idx[p + 1] != idx[p] + 1 ||
                !map.AreSpaceBridged(idx[p], idx[p + 1], ct) ||
                map.HasCommaBetween(idx[p], idx[p + 1]))
            {
                return false;
            }
        }

        if (verbFirstPos == subjLastPos + 3 &&
            Guard.ObliquePronounReadings(slots[idx[subjLastPos + 2]].Word, ct).Length > 0)
        {
            return true;
        }

        for (int p = subjLastPos + 2; p < verbFirstPos; p++)
        {
            if (!governed[p] || IsActivePreposition(slots[idx[p]].Word, slots[idx[p]].Active))
                return false;
        }

        return governed[subjLastPos + 1];
    }

    private static bool IsAnimateComitativeWithPluralPredicate(
        SentenceClauseMap map,
        int[] clauseIdx,
        NominalChunk subj,
        WordSlot subjSlot,
        int verbFirstPos,
        bool[] governed,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        int subjLastPos = Array.IndexOf(clauseIdx, subj.LastWord);
        if (subjLastPos < 0 || subjLastPos + 2 >= verbFirstPos || clauseIdx[subjLastPos + 1] != subj.LastWord + 1)
            return false;

        WordSlot prepSlot = slots[subj.LastWord + 1];
        if (prepSlot.Word.CanonicalText is not ("с" or "със") || !IsActivePreposition(prepSlot.Word, prepSlot.Active))
            return false;
        if (!map.AreSpaceBridged(subj.LastWord, subj.LastWord + 1, ct) || map.HasCommaBetween(subj.LastWord, subj.LastWord + 1))
            return false;
        if (!HasAnimateNominalHead(subjSlot, subj.HeadReadings))
            return false;

        for (int q = subjLastPos + 2; q < verbFirstPos; q++)
        {
            ct.ThrowIfCancellationRequested();
            if (clauseIdx[q] != clauseIdx[q - 1] + 1 ||
                !map.AreSpaceBridged(clauseIdx[q - 1], clauseIdx[q], ct) ||
                map.HasCommaBetween(clauseIdx[q - 1], clauseIdx[q]))
            {
                return false;
            }

            WordSlot cur = slots[clauseIdx[q]];
            if (cur.Word.CanonicalText is "дори" or "даже" or "само" or "едва" or "почти" or "чак" or "тъкмо" or "именно")
                continue;

            if (cur.Word.UsesGraph || !cur.Word.Complete || cur.Word.Readings.Count == 0)
                return true;

            if (Guard.ObliquePronounReadings(cur.Word, ct).Length > 0)
                return true;

            bool isPreModifier = governed[q] &&
                (cur.ComparativeGraphModifier || IsNominalModifierOrDeterminer(cur) || IsNumeralWord(cur) ||
                 cur.Word.CanonicalText is "тоя" or "тая" or "туй" or "тия" or "оня" or "оная" or "онуй" or "ония" or "тез" or "онез");
            if (isPreModifier)
            {
                bool canAdvancePastModifier = !HasNominalHeadReading(cur);
                if (!canAdvancePastModifier && q + 1 < verbFirstPos && governed[q + 1] &&
                    clauseIdx[q + 1] == clauseIdx[q] + 1 &&
                    map.AreSpaceBridged(clauseIdx[q], clauseIdx[q + 1], ct) &&
                    !map.HasCommaBetween(clauseIdx[q], clauseIdx[q + 1]))
                {
                    WordSlot next = slots[clauseIdx[q + 1]];
                    if (!IsPossessiveClitic(next.Word.CanonicalText) &&
                        !IsActivePreposition(next.Word, next.Active) &&
                        (next.ComparativeGraphModifier || IsNominalModifierOrDeterminer(next) || HasNominalHeadReading(next)))
                    {
                        canAdvancePastModifier = true;
                    }
                }

                if (canAdvancePastModifier)
                    continue;
            }

            int[] compHeadReadings = NominalHeadReadings(cur);
            if (compHeadReadings.Length == 0)
                return false;

            return IsPersonPlaceHomograph(map.Analysis, cur.Word.OriginalIndex) ||
                   HasAnimateNominalHead(cur, compHeadReadings);
        }

        return false;
    }

    private static bool IsInclusiveSingularComitativePronoun(
        SentenceClauseMap map,
        int[] clauseIdx,
        NominalChunk subj,
        WordSlot subjSlot,
        int verbFirstPos,
        bool hasFirstPluralReading,
        bool hasSecondPluralReading,
        CancellationToken ct)
    {
        if (subj.Number != MorphologicalNumber.Singular || (!hasFirstPluralReading && !hasSecondPluralReading))
            return false;
        if (!HasAnimateNominalHead(subjSlot, subj.HeadReadings))
            return false;

        WordSlot[] slots = map.Slots;
        int subjLastPos = Array.IndexOf(clauseIdx, subj.LastWord);
        if (subjLastPos < 0 || subjLastPos + 2 >= verbFirstPos || clauseIdx[subjLastPos + 1] != subj.LastWord + 1)
            return false;

        WordSlot prepSlot = slots[subj.LastWord + 1];
        if (prepSlot.Word.CanonicalText is not ("с" or "със") || !IsActivePreposition(prepSlot.Word, prepSlot.Active))
            return false;
        if (!map.AreSpaceBridged(subj.LastWord, subj.LastWord + 1, ct) || map.HasCommaBetween(subj.LastWord, subj.LastWord + 1))
            return false;

        for (int q = subjLastPos + 2; q < verbFirstPos; q++)
        {
            ct.ThrowIfCancellationRequested();
            if (clauseIdx[q] != clauseIdx[q - 1] + 1 ||
                !map.AreSpaceBridged(clauseIdx[q - 1], clauseIdx[q], ct) ||
                map.HasCommaBetween(clauseIdx[q - 1], clauseIdx[q]))
            {
                return false;
            }

            WordSlot cur = slots[clauseIdx[q]];
            if (cur.Word.CanonicalText is "дори" or "даже" or "само" or "едва" or "почти" or "чак" or "тъкмо" or "именно")
                continue;

            if (Guard.ObliquePronounReadings(cur.Word, ct).Length == 0)
                return false;

            return cur.Word.CanonicalText switch
            {
                "мен" or "мене" or "нас" => hasFirstPluralReading,
                "теб" or "тебе" or "вас" => hasSecondPluralReading,
                _ => false
            };
        }

        return false;
    }

    private static bool IsAdverbialTimeOrMeasureHead(WordSlot headSlot, NominalChunk? np = null) =>
        headSlot.Active.Any(r =>
        {
            string lemma = Norm(headSlot.Word.Readings[r].Lemma);
            if (lemma == "път" && np is not null && np.FirstWord == np.HeadWord)
                return false;
            return Guard.IsTemporalOrMeasureLemma(lemma);
        });

    private static bool HasVocativeReading(WordSlot headSlot) =>
        headSlot.Word.Readings.Any(r =>
            !Guard.IsSyncreticVocativeCompanion(headSlot.Word, r) &&
            (r.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) == true ||
             r.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) == true));

    private static bool IsPrepositionalComplementVerbLemma(string lemma) =>
        lemma is "отговарям" or "отговоря" or "разчитам" or "говоря" or "разговарям" or
            "вярвам" or "помагам" or "помогна" or "преча" or "попреча" or
            "принадлежа" or "завися" or "съответствам" or "влияя" or "повлияя" or
            "съдействам" or "участвам" or "присъствам" or "служа" or "подлежа" or
            "приличам" or "благодаря" or "работя" or "живея" or "гласувам";

    private static bool HasCoordinatedPreverbalNominals(
        SentenceClauseMap map, int[] idx, int fromPos, int toPos, bool[] governed, CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        for (int p = fromPos + 1; p < toPos; p++)
        {
            if (governed[p] || slots[idx[p]].Word.CanonicalText != "и") continue;
            if (slots[idx[p]].Word.Readings.Any(r => r.Category == Category.Noun && !Guard.IsSourcePlaceholder(slots[idx[p]].Word, r)))
                return true; // Preserve abstention when coordinator itself has a lexical Noun reading in synthetic tests.
            if (HasNominalHeadReading(slots[idx[p - 1]]) &&
                map.AreSpaceBridged(idx[p - 1], idx[p], ct) &&
                map.AreSpaceBridged(idx[p], idx[p + 1], ct) &&
                (HasNominalHeadReading(slots[idx[p + 1]]) || IsNominalModifierOrDeterminer(slots[idx[p + 1]])))
                return true;
        }
        return false;
    }

    private static bool HasUnassignedNominativePronoun(WordSlot[] slots, int[] clauseIdx, int bodyStart, NominalChunk subj)
    {
        for (int p = bodyStart; p < clauseIdx.Length; p++)
        {
            int w = clauseIdx[p];
            if (w >= subj.FirstWord && w <= subj.LastWord) continue;
            if (NominativePronounReadings(slots[w], p > bodyStart ? slots[clauseIdx[p - 1]] : null).Length > 0)
                return true;
        }
        return false;
    }

    private static bool IsPersonPlaceHomograph(TextAnalysis analysis, int tokenIndex) =>
        Guard.IsPersonPlaceHomograph(analysis, tokenIndex);

    private static bool IsNumeralWord(WordSlot slot) =>
        !slot.Word.UsesGraph && slot.Active.Length > 0 &&
        slot.Active.All(r => slot.Word.Readings[r].Category == Category.Number ||
            slot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral);

    private static bool IsPossessiveClitic(string c) =>
        BulgarianFunctionWords.HasAny(c, FunctionWordClasses.PossessiveOrDativeClitic);

    private static bool HasCleanSelectionTokens(
        SentenceClauseMap map, VerbalComplex verb, NominalChunk? subj, CancellationToken ct)
    {
        TextAnalysis analysis = map.Analysis;
        SentenceSpan sentence = map.Sentence;
        WordSlot[] slots = map.Slots;
        TokenView predWord = slots[verb.FiniteCarrierWordIndex].Word;
        if (!IsCleanPredicateToken(predWord, IsSentenceInitialWord(analysis, sentence, verb.FiniteCarrierWordIndex, predWord), ct))
            return false;
        if (subj is null) return true;
        for (int w = subj.FirstWord; w <= subj.HeadWord; w++)
        {
            if (slots[w].HyphenatedCompoundNominal is not null)
                continue;
            TokenView sw = slots[w].Word;
            if (!IsCleanSubjectToken(sw, IsSentenceInitialWord(analysis, sentence, w, sw),
                    subj.PronounSubject && w == subj.HeadWord, ct))
                return false;
        }
        return true;
    }

    private static bool IsCleanPredicateToken(TokenView word, bool sentenceInitial, CancellationToken ct)
    {
        if (word.UsesGraph) return false;
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ReadingView r = word.Readings[i];
            MorphologicalFeatures f = r.Features;
            if (f.HasConflictingFeatures) return false;
            if (Guard.IsSourcePlaceholder(word, r) || Guard.IsUnwrittenCapitalizedReading(word, r, sentenceInitial))
                continue;
            if (r.Category == Category.Unknown || f.PartOfSpeech == MorphologicalPartOfSpeech.Unknown)
                return false;
            if (r.Category == Category.Verb)
            {
                if (!IsCleanOrBenignLegacyVerb(r)) return false;
                if (f.VerbForm == MorphologicalVerbForm.Finite && !Guard.IsPersonalPredicate(f)) return false;
            }
        }
        return true;
    }

    private static bool IsCleanSubjectToken(TokenView word, bool sentenceInitial, bool pronounHead, CancellationToken ct)
    {
        if (word.UsesGraph) return IsDegreeGraphModifier(word);
        string c = word.CanonicalText;
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ReadingView r = word.Readings[i];
            MorphologicalFeatures f = r.Features;
            if (f.HasConflictingFeatures) return false;
            if (Guard.IsSourcePlaceholder(word, r) || Guard.IsUnwrittenCapitalizedReading(word, r, sentenceInitial))
                continue;
            if (r.Category == Category.Unknown || f.PartOfSpeech == MorphologicalPartOfSpeech.Unknown)
                return false;
            if (pronounHead)
            {
                bool allowedPronoun = Guard.LicensedSubject(c, r) ||
                    c == "те" && (r.Category == Category.Particle && !f.HasUnrecognizedFeatures || Guard.IsAccusativeCliticReading(r)) ||
                    c == "ти" && !f.HasUnrecognizedFeatures && (r.Category == Category.Particle || f.IsShortForm == true) ||
                    c == "вие" && r.Category == Category.Verb && IsCleanOrBenignLegacyVerb(r);
                if (!allowedPronoun) return false;
                continue;
            }
            if (f.PartOfSpeech == MorphologicalPartOfSpeech.Noun && !IsCleanOrBenignLegacyNoun(r))
                return false;
            if (f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && !Guard.HasNominalAxes(f))
                return false;
        }
        return true;
    }

    private static bool IsCleanOrBenignLegacyNoun(ReadingView row)
    {
        MorphologicalFeatures f = row.Features;
        if (f.HasConflictingFeatures) return false;
        if (Guard.HasNominalAxes(f) ||
            !f.HasUnrecognizedFeatures && f.Number == MorphologicalNumber.Count && f.Gender is not null)
            return true;
        if (!f.HasUnrecognizedFeatures || row.Reading is not { PartOfSpeech: { } rawTag } || f != MorphologicalFeatures.FromTag(rawTag))
            return false;
        string stripped = rawTag
            .Replace(",Звателна", "", StringComparison.Ordinal)
            .Replace(",Звателен", "", StringComparison.Ordinal)
            .Replace(";Звателна", "", StringComparison.Ordinal)
            .Replace(";Звателен", "", StringComparison.Ordinal);
        return stripped != rawTag && Guard.HasNominalAxes(MorphologicalFeatures.FromTag(stripped));
    }

    private static bool HasSecondPersonImperative(TokenView word, MorphologicalNumber number) =>
        word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative &&
            r.Features.Number == number && !r.Features.HasConflictingFeatures);

    private static bool AllFiniteReadingsTransitive(TokenView word, int[] finite) =>
        finite.Length > 0 && finite.All(r => word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive);

    private static bool IsExclusivelyFirstOrSecond(TokenView word, int[] finite) =>
        finite.Length > 0 && finite.All(r => word.Readings[r].Features.Person is MorphologicalPerson.First or MorphologicalPerson.Second);

    private static VerbalComplex SingleTokenComplex(int wordIndex, int[] readings) =>
        new(VerbalComplexKind.SyntheticFinite, -1, wordIndex, wordIndex, wordIndex, wordIndex,
            -1, -1, readings, readings, CliticCluster.Empty, CliticCluster.Empty, [], false, false, false, []);

    private static bool TryAddCompoundParticipleMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        NominalChunk? preSubject,
        VerbalComplex verb,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (!verb.CopularCarrier || !verb.HasActiveParticipleTail || verb.InvariantCarrier) return false;
        WordSlot[] slots = map.Slots;
        WordSlot auxSlot = slots[verb.FiniteCarrierWordIndex];
        if (!IsExactWord(auxSlot)) return false;

        string auxCanon = auxSlot.Word.CanonicalText;
        bool isConditionalAux = auxCanon is "бих" or "би" or "бихме" or "бихте" or "биха";
        bool isAnalyticPerfect = verb.Kind == VerbalComplexKind.AnalyticPerfect && !isConditionalAux && !verb.HasFutureMarker;
        if (!isConditionalAux && !verb.HasFutureMarker && !isAnalyticPerfect)
            return false;
        if (isAnalyticPerfect && preSubject is null)
            return false;

        int[] clauseIdx = clause.WordIndices;
        int verbPos = Array.IndexOf(clauseIdx, verb.FiniteCarrierWordIndex);
        if (verbPos < 0) return false;
        if (auxCanon == "би" && verbPos > 0 && slots[clauseIdx[verbPos - 1]].Word.CanonicalText is "може" or "не")
            return false;
        if (isAnalyticPerfect)
        {
            for (int k = 1; k < verbPos; k++)
                if (!map.Peripheral[clauseIdx[k]] && !slots[clauseIdx[k]].Quoted &&
                    slots[clauseIdx[k]].Word.Token.Text.Length > 0 &&
                    char.IsUpper(slots[clauseIdx[k]].Word.Token.Text[0]))
                    return false;
        }

        VerbalDependent partDep = verb.Dependents.Last(d => d.Kind == VerbalDependentKind.ActiveParticiple);
        int partPosInClause = Array.IndexOf(clauseIdx, partDep.WordIndex);
        if (partPosInClause < 0) return false;
        WordSlot partSlot = slots[partDep.WordIndex];
        int[] partReadings = partDep.Readings;

        int[] auxCarrierReadings = Enumerable.Range(0, auxSlot.Word.Readings.Count)
            .Where(r => TextPredicateLinkAnalyzer.CopularCarrier(auxSlot.Word.Readings[r]) &&
                Guard.IsPersonalPredicate(auxSlot.Word.Readings[r].Features))
            .ToArray();
        if (auxCarrierReadings.Length == 0) return false;

        // 1. Auxiliary <-> -л Participle number compatibility (per БЕРОН § 24.1.1, -л active participle uses plural even for polite Вие).
        if (!auxCarrierReadings.Any(ar =>
                partReadings.Any(pr => auxSlot.Word.Readings[ar].Features.Number == partSlot.Word.Readings[pr].Features.Number)))
        {
            bool partHasNonVerbHomograph = NonPlaceholderReadings(partSlot.Word, sentenceInitial: false)
                .Any(r => partSlot.Word.Readings[r].Features.PartOfSpeech != MorphologicalPartOfSpeech.Verb);
            if (isAnalyticPerfect && !partHasNonVerbHomograph)
                return false; // Handled directly by PredicativeGrammarEvidence (R5) when participle is verb-pure.
            List<ContextOverlayRoleSelection> roles =
            [
                new(ContextOverlayRole.Predicate, partSlot.Word.OriginalIndex, partSlot.Word.Token.Start, partSlot.Word.Token.Length,
                    partReadings, ["clause_fusion_participle_readings"]),
                new(ContextOverlayRole.NominalContext, auxSlot.Word.OriginalIndex, auxSlot.Word.Token.Start, auxSlot.Word.Token.Length,
                    auxCarrierReadings, ["clause_fusion_auxiliary_readings"])
            ];
            agreement.Add(new(partSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                canonicalKey(roles), roles, ["compound_aux_participle_mismatch"]));
            return true;
        }

        if (preSubject is not { Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } ||
            preSubject.Number is null)
            return false;

        WordSlot subjHead = slots[preSubject.HeadWord];
        if (subjHead.Word.UsesGraph || IsPersonPlaceHomograph(map.Analysis, subjHead.Word.OriginalIndex) ||
            IsAdverbialTimeOrMeasureHead(subjHead) || HasVocativeReading(subjHead) ||
            HasUnassignedNominativePronoun(slots, clauseIdx, clause.BodyStartPos, preSubject) ||
            IsCliticResumedObject(slots, preSubject, verb) ||
            preSubject.Number == MorphologicalNumber.Singular && preSubject.LastWord + 1 < verb.FirstWordIndex &&
                subjHead.Active.Any(r => PartitiveOrCollectiveHeads.Contains(Norm(subjHead.Word.Readings[r].Lemma))) ||
            isAnalyticPerfect && !IsDefiniteOrProperSubjectCandidate(slots, preSubject))
            return false;
        for (int w = preSubject.FirstWord; w <= preSubject.HeadWord; w++)
        {
            if (slots[w].HyphenatedCompoundNominal is not null) continue;
            if (!IsCleanSubjectToken(slots[w].Word, IsSentenceInitialWord(map.Analysis, map.Sentence, w, slots[w].Word),
                    preSubject.PronounSubject && w == preSubject.HeadWord, ct))
                return false;
        }

        int postEndPos = clauseIdx.Length - 1;
        for (int p = partPosInClause + 1; p < clauseIdx.Length; p++)
            if (slots[clauseIdx[p]].Word.CanonicalText == "да") { postEndPos = p - 1; break; }
        bool[] subGov = MarkGovernedPhrases(map, clauseIdx, partPosInClause + 1, postEndPos, 0, partPosInClause, ct);
        List<NominalChunk> postPartNps = CollectNominalChunks(map, clauseIdx, partPosInClause + 1, postEndPos, subGov, ct);

        bool partIntransitive = partReadings.All(r =>
            partSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
            !IsLinkingOrSemiCopularLemma(partSlot.Word.Readings[r].Lemma));
        bool provenSubject = preSubject.PronounSubject || preSubject.FullMasculineSingular ||
            verb.HasReflexiveSe || partIntransitive || postPartNps.Any(p => !p.TemporalAdjunct);
        if (!provenSubject) return false;
        if (!preSubject.PronounSubject && !preSubject.FullMasculineSingular &&
            postPartNps.Any(p => !p.TemporalAdjunct && IsDefiniteOrProperSubjectCandidate(slots, p) &&
                auxCarrierReadings.Any(ar => Guard.PersonNumberTupleMatches(
                    (p.Person, p.Number),
                    (auxSlot.Word.Readings[ar].Features.Person, auxSlot.Word.Readings[ar].Features.Number)))))
            return false;
        // Guard copular-attribute + animate predicate-nominal frames ("Мария е преживял много човек.").
        if (isAnalyticPerfect && auxCanon is "е" or "са" && !preSubject.FullMasculineSingular &&
            postPartNps.Any(p => !p.TemporalAdjunct &&
                !IsDefiniteOrProperSubjectCandidate(slots, p) && !p.ShortMasculineSingular &&
                p.HeadReadings.Any(r => slots[p.HeadWord].Word.Readings[r].Features.Article == MorphologicalArticle.Indefinite) &&
                HasAnimateNominalHead(slots[p.HeadWord], p.HeadReadings) &&
                partReadings.Any(pr => Guard.NominalTupleMatches(
                    (partSlot.Word.Readings[pr].Features.Number, partSlot.Word.Readings[pr].Features.Gender),
                    (p.Number, p.Gender)))))
            return false;

        // 2. Subject <-> Auxiliary (person, number) agreement.
        if (!auxCarrierReadings.Any(ar => Guard.PersonNumberTupleMatches(
                (preSubject.Person, preSubject.Number),
                (auxSlot.Word.Readings[ar].Features.Person, auxSlot.Word.Readings[ar].Features.Number))))
        {
            AddSubjectRelation(slots, preSubject, verb with { CarrierFiniteReadings = auxCarrierReadings }, auxCarrierReadings,
                canonicalKey, agreement, preSubject.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch",
                mismatch: true, subjectProven: true);
            return true;
        }

        // 3. Subject <-> -л Participle (number, gender) agreement (per БЕРОН § 24.1.1, -л active participle uses plural for polite Вие).
        MorphologicalGender? expectedGender = !preSubject.PronounSubject && preSubject.Number == MorphologicalNumber.Singular
            ? preSubject.Gender
            : null;
        if (isAnalyticPerfect && preSubject.Number == MorphologicalNumber.Singular && expectedGender is null)
            return false;
        if (!partReadings.Any(pr =>
        {
            MorphologicalFeatures pf = partSlot.Word.Readings[pr].Features;
            return pf.Number == preSubject.Number && (expectedGender is null || pf.Gender == expectedGender);
        }))
        {
            TokenView sw = subjHead.Word;
            List<ContextOverlayRoleSelection> roles =
            [
                new(ContextOverlayRole.Predicate, partSlot.Word.OriginalIndex, partSlot.Word.Token.Start, partSlot.Word.Token.Length,
                    partReadings, ["clause_fusion_participle_readings"]),
                new(ContextOverlayRole.Subject, sw.OriginalIndex, sw.Token.Start, sw.Token.Length,
                    preSubject.HeadReadings, ["nominal_subject_clause_mismatch"]),
                new(ContextOverlayRole.NominalContext, auxSlot.Word.OriginalIndex, auxSlot.Word.Token.Start, auxSlot.Word.Token.Length,
                    auxCarrierReadings, ["clause_fusion_auxiliary_readings"])
            ];
            for (int w = preSubject.FirstWord; w < preSubject.HeadWord; w++)
                if (!slots[w].Word.UsesGraph && slots[w].Active.Length > 0)
                    roles.Add(new(ContextOverlayRole.Attribute, slots[w].Word.OriginalIndex, slots[w].Word.Token.Start,
                        slots[w].Word.Token.Length, slots[w].Active, ["clause_fusion_nominal_modifier"]));
            agreement.Add(new(partSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                canonicalKey(roles), roles, ["compound_subj_participle_mismatch"]));
            return true;
        }

        return false;
    }

    private static readonly HashSet<string> PartitiveOrCollectiveHeads = new(StringComparer.Ordinal)
    {
        "част", "половина", "третина", "четвъртина", "група", "редица",
        "мнозинство", "малцинство", "болшинство"
    };

    private static bool TryAddCopularOrLinkingPredicativeMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        VerbalComplex verb,
        NominalChunk? preSubject,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (preSubject is not { Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } subj)
            return false;

        WordSlot[] slots = map.Slots;
        WordSlot headVerbSlot = slots[verb.FiniteCarrierWordIndex];
        string copCanon = headVerbSlot.Word.CanonicalText;

        bool isCopula = verb.Kind is VerbalComplexKind.SyntheticFinite or VerbalComplexKind.CopularPredicative &&
            !verb.HasFutureMarker && verb.DaParticleWordIndex < 0 &&
            copCanon is "съм" or "си" or "е" or "сме" or "сте" or "са" or "бях" or "беше" or "бе" or "бяхме" or "бяхте" or "бяха" &&
            !(copCanon == "си" && (!subj.PronounSubject || slots[subj.HeadWord].Word.CanonicalText != "ти"));
        bool isLinking = verb.Kind == VerbalComplexKind.SyntheticFinite &&
            !verb.HasFutureMarker && verb.DaParticleWordIndex < 0 &&
            copCanon is "остава" or "остават" or "остана" or "останаха" or "става" or "стават" or "стана" or "станаха";
        if (!isCopula && !isLinking)
            return false;

        int[] cWords = clause.WordIndices;
        int verbClausePos = Array.IndexOf(cWords, verb.FiniteCarrierWordIndex);
        if (verbClausePos < 1 || verbClausePos + 1 >= cWords.Length)
            return false;

        // Abstain if capitalized proper-name appositives appear between clause start and copula ("поетеса Елизабет Барет Браунинг е...").
        for (int k = 1; k < verbClausePos; k++)
        {
            if (!map.Peripheral[cWords[k]] && slots[cWords[k]].Word.Token.Text.Length > 0 &&
                char.IsUpper(slots[cWords[k]].Word.Token.Text[0]))
                return false;
        }

        WordSlot subjSlot = slots[subj.HeadWord];
        if (subjSlot.Word.UsesGraph || IsPersonPlaceHomograph(map.Analysis, subjSlot.Word.OriginalIndex) ||
            IsAdverbialTimeOrMeasureHead(subjSlot, subj) || HasVocativeReading(subjSlot) ||
            subjSlot.Active.Any(r => PartitiveOrCollectiveHeads.Contains(Norm(subjSlot.Word.Readings[r].Lemma))))
            return false;
        if (!subj.PronounSubject && !IsDefiniteOrProperSubjectCandidate(slots, subj))
            return false;

        // Skip optional degree intensifiers right after the copula/linking verb.
        int predClausePos = verbClausePos + 1;
        bool hasIntensifier = false;
        while (predClausePos < cWords.Length && !map.Peripheral[cWords[predClausePos]] &&
               map.AreSpaceBridged(cWords[predClausePos - 1], cWords[predClausePos], ct) &&
               slots[cWords[predClausePos]].Word.CanonicalText is "много" or "изключително" or "съвсем" or "напълно" or "твърде" or "особено" or "доста")
        {
            hasIntensifier = true;
            predClausePos++;
        }
        if (predClausePos >= cWords.Length || !map.AreSpaceBridged(cWords[predClausePos - 1], cWords[predClausePos], ct))
            return false;

        WordSlot predSlot = slots[cWords[predClausePos]];
        if (map.Peripheral[cWords[predClausePos]] || !IsExactWord(predSlot) || predSlot.Quoted ||
            predSlot.Word.Token.Text.Length == 0 || char.IsUpper(predSlot.Word.Token.Text[0]))
            return false;

        bool hasInterveningMaterialBeforeCopula = subj.LastWord + 1 != cWords[verbClausePos];
        bool hasTrailingMaterialAfterPred = Enumerable.Range(predClausePos + 1, cWords.Length - predClausePos - 1)
            .Any(k => !map.Peripheral[cWords[k]]);
        bool isPoliteVie = subj.PronounSubject && subjSlot.Word.CanonicalText == "вие";
        if (!isLinking && !hasIntensifier && !hasInterveningMaterialBeforeCopula && !hasTrailingMaterialAfterPred && !isPoliteVie)
            return false;

        // Guard predicate-nominal NPs ("Тази книга е истинско съкровище.", "Тя е добър за възрастта си специалист."):
        // track governed PPs so an ungoverned Noun/Pronoun/ProperNoun after stacked modifiers or a modifier-internal PP causes abstention.
        bool inPrep = false;
        for (int scan = predClausePos + 1; scan < cWords.Length; scan++)
        {
            int swIdx = cWords[scan];
            if (map.Peripheral[swIdx]) continue;
            if (map.HasCommaBetween(cWords[scan - 1], swIdx)) break;
            string sc = slots[swIdx].Word.CanonicalText;
            if (IsClauseIntroducer(slots[swIdx].Word, slots[swIdx].Active) || IsRelativeWord(sc) || sc == "да")
                break;
            if (IsActivePreposition(slots[swIdx].Word, slots[swIdx].Active))
            {
                inPrep = true;
                continue;
            }
            if (sc is "и" or "или" or "много" or "доста" or "съвсем" or "напълно" ||
                IsPossessiveClitic(sc))
                continue;
            int[] sr = slots[swIdx].Active.Length > 0 ? slots[swIdx].Active : NonPlaceholderReadings(slots[swIdx].Word, sentenceInitial: false);
            if (sr.Length > 0 && sr.All(r => slots[swIdx].Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective } or
                { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle } ||
                Guard.TryGetDeterminerAxes(slots[swIdx].Word, slots[swIdx].Word.Readings[r], out _) ||
                slots[swIdx].Word.Readings[r].Category == Category.Number))
                continue;
            if (sr.Any(r => slots[swIdx].Word.Readings[r].Features.PartOfSpeech is
                MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.ProperNoun))
            {
                if (inPrep)
                {
                    inPrep = false;
                    continue;
                }
                return false;
            }
            inPrep = false;
            break;
        }

        int[] rawPred = NonPlaceholderReadings(predSlot.Word, sentenceInitial: false);
        if (rawPred.Length == 0) return false;

        bool allowFinalNeuterAdjAdv = hasIntensifier && !hasTrailingMaterialAfterPred && predSlot.Word.CanonicalText.EndsWith('о') &&
            rawPred.Any(r => predSlot.Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }) &&
            rawPred.All(r => predSlot.Word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Adverb);

        bool purePredAdjOrPass = rawPred.All(r =>
            predSlot.Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Article: MorphologicalArticle.Indefinite } ||
            predSlot.Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle, ParticipleKind: MorphologicalParticipleKind.PastPassive } pf &&
            pf.Article is null or MorphologicalArticle.Indefinite);

        if (!purePredAdjOrPass && !allowFinalNeuterAdjAdv)
            return false;

        int[] predSupport = rawPred
            .Where(r => predSlot.Word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Verb)
            .Order()
            .ToArray();
        if (predSupport.Length == 0) return false;

        MorphologicalNumber copNum = copCanon is "сме" or "сте" or "са" or "бяхме" or "бяхте" or "бяха" or "остават" or "останаха" or "стават" or "станаха"
            ? MorphologicalNumber.Plural
            : MorphologicalNumber.Singular;

        // Guard CompetingPrepositionalSplit ("Тя е поканен от младата лекар." and present-tense "от"-agentive split).
        int firstAfterPred = -1;
        for (int k = predClausePos + 1; k < cWords.Length; k++)
            if (!map.Peripheral[cWords[k]]) { firstAfterPred = cWords[k]; break; }
        if (firstAfterPred >= 0 && IsActivePreposition(slots[firstAfterPred].Word, slots[firstAfterPred].Active))
        {
            bool presentOtSplit = slots[firstAfterPred].Word.CanonicalText == "от" && copCanon is "е" or "са" &&
                predSupport.Any(pr => predSlot.Word.Readings[pr].Features.ParticipleKind == MorphologicalParticipleKind.PastPassive);
            int lastCoreIdx = -1;
            for (int k = cWords.Length - 1; k > predClausePos; k--)
                if (!map.Peripheral[cWords[k]]) { lastCoreIdx = cWords[k]; break; }
            bool ppNounSaturatedByArticledMod = lastCoreIdx > firstAfterPred + 1 &&
                map.AreSpaceBridged(lastCoreIdx - 1, lastCoreIdx, ct) &&
                slots[lastCoreIdx - 1].Active.Length > 0 &&
                slots[lastCoreIdx - 1].Active.All(mr => IsArticledModifierFeature(slots[lastCoreIdx - 1].Word.Readings[mr].Features)) &&
                slots[lastCoreIdx].Active.Any(lr =>
                    slots[lastCoreIdx - 1].Active.Any(mr => Guard.ModifierAgreement(
                        slots[lastCoreIdx - 1].Word, slots[lastCoreIdx - 1].Word.Readings[mr],
                        slots[lastCoreIdx].Word.Readings[lr], includeAdnominalReadings: true)));
            if (presentOtSplit || !ppNounSaturatedByArticledMod && lastCoreIdx >= 0 && slots[lastCoreIdx].Active.Any(lr =>
                slots[lastCoreIdx].Word.Readings[lr].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Indefinite } lnf &&
                lnf.Number == copNum &&
                predSupport.Any(pr => Guard.NominalTupleMatches(
                    (predSlot.Word.Readings[pr].Features.Number, predSlot.Word.Readings[pr].Features.Gender),
                    (lnf.Number, lnf.Gender)))))
                return false;
        }

        int[] copulaReadings = isCopula
            ? Enumerable.Range(0, headVerbSlot.Word.Readings.Count)
                .Where(r => TextPredicateLinkAnalyzer.CopularCarrier(headVerbSlot.Word.Readings[r]) &&
                    Guard.IsPersonalPredicate(headVerbSlot.Word.Readings[r].Features))
                .ToArray()
            : verb.CarrierFiniteReadings;
        if (copulaReadings.Length == 0) return false;

        // Case 1: Polite "Вие" + singular predicative participle/adjective + explicit honorific+name vocative ("Вие сте поканена на срещата, господин Петров.").
        if (isPoliteVie)
        {
            if (!predSupport.All(r => predSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Singular))
                return false;
            MorphologicalGender? vocGender = FindVocativeAddresseeGender(map, ct);
            if (vocGender is null || predSupport.Any(r => predSlot.Word.Readings[r].Features.Gender == vocGender))
                return false;
            return EmitPredicativeSubjMismatch(slots, subj, predSlot, predSupport, headVerbSlot.Word, copulaReadings, canonicalKey, agreement);
        }

        if (subj.Number != copNum ||
            !copulaReadings.Any(cr => Guard.PersonNumberTupleMatches(
                (subj.Person, subj.Number),
                (headVerbSlot.Word.Readings[cr].Features.Person, headVerbSlot.Word.Readings[cr].Features.Number))))
        {
            if (isLinking || map.HasCommaBetween(subj.LastWord, verb.FiniteCarrierWordIndex) || subj.HeadReadings.Length == 0)
            {
                AddSubjectRelation(slots, subj, verb with { CarrierFiniteReadings = copulaReadings }, copulaReadings,
                    canonicalKey, agreement, subj.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch",
                    mismatch: true, subjectProven: true);
                return true;
            }
            return false;
        }

        MorphologicalGender? reqGen = copNum == MorphologicalNumber.Singular ? subj.Gender : null;
        if (copNum == MorphologicalNumber.Singular && reqGen is null)
            return false;

        // Locked Test Guard (NominalDiagnosticsPackageTests.cs:504, 619):
        // Preserve elliptic "[PlainDefFemNP + е + (много) + MascSgAdj]" ("Малката книга е много хубав.", "Малката стая е хубав.").
        if (copCanon == "е" && copNum == MorphologicalNumber.Singular && reqGen == MorphologicalGender.Feminine &&
            !hasInterveningMaterialBeforeCopula &&
            predSupport.Any(r => predSlot.Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular }))
        {
            return false;
        }

        if (predSupport.Any(r =>
        {
            MorphologicalFeatures pf = predSlot.Word.Readings[r].Features;
            return pf.Number == copNum && (reqGen is null || pf.Gender == reqGen);
        }))
            return false;
        if (subj.HeadReadings.Length == 0)
            return false;

        return EmitPredicativeSubjMismatch(slots, subj, predSlot, predSupport, headVerbSlot.Word, copulaReadings, canonicalKey, agreement);
    }

    private static MorphologicalGender? FindVocativeAddresseeGender(SentenceClauseMap map, CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        for (int k = 0; k + 1 < slots.Length; k++)
        {
            if (slots[k].Quoted || slots[k + 1].Quoted || !map.AreSpaceBridged(k, k + 1, ct))
                continue;
            string nameText = slots[k + 1].Word.Token.Text;
            if (nameText.Length == 0 || !char.IsUpper(nameText[0]))
                continue;
            bool commaDelimited = k > 0 && map.HasCommaBetween(k - 1, k) ||
                k == 0 && k + 2 < slots.Length && map.HasCommaBetween(k + 1, k + 2);
            if (!commaDelimited)
                continue;
            string hon = slots[k].Word.CanonicalText;
            if (hon is "господин" or "г-н")
                return MorphologicalGender.Masculine;
            if (hon is "госпожо" or "госпожице" or "г-жо" or "г-це")
                return MorphologicalGender.Feminine;
        }
        return null;
    }

    private static bool EmitPredicativeSubjMismatch(
        WordSlot[] slots,
        NominalChunk subj,
        WordSlot predSlot,
        int[] predReadings,
        TokenView copulaWord,
        int[] copulaReadings,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement)
    {
        TokenView predWord = predSlot.Word;
        TokenView subjWord = slots[subj.HeadWord].Word;
        string predEvidence = predReadings.Any(r => Guard.IsMeasuredPassiveAttribute(predSlot.Word.Readings[r].Features))
            ? "clause_fusion_passive_participle"
            : predReadings.All(r => predSlot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective)
                ? "clause_fusion_predicative_adjective"
                : "clause_fusion_participle_readings";
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, predWord.OriginalIndex, predWord.Token.Start, predWord.Token.Length,
                predReadings.Order().ToArray(), [predEvidence]),
            new(ContextOverlayRole.NominalContext, copulaWord.OriginalIndex, copulaWord.Token.Start, copulaWord.Token.Length,
                copulaReadings.Order().ToArray(), ["clause_fusion_copular_carrier"]),
            new(ContextOverlayRole.Subject, subjWord.OriginalIndex, subjWord.Token.Start, subjWord.Token.Length,
                subj.HeadReadings.Order().ToArray(), [subj.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch"])
        ];
        for (int w = subj.FirstWord; w < subj.HeadWord; w++)
            if (!slots[w].Word.UsesGraph && slots[w].Active.Length > 0)
                roles.Add(new(ContextOverlayRole.Attribute, slots[w].Word.OriginalIndex, slots[w].Word.Token.Start,
                    slots[w].Word.Token.Length, slots[w].Active, ["clause_fusion_nominal_modifier"]));
        agreement.Add(new(predWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, ["compound_subj_participle_mismatch"]));
        return true;
    }

    private static bool TryAddSentenceInitialToPronounMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        int verbFirstPos,
        VerbalComplex verb,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        if (!clause.Matrix || clause.BodyStartPos != 0 || clauseIdx.Length < 3 || clauseIdx[0] != 0 ||
            verbFirstPos != 1 || !map.AreSpaceBridged(0, clauseIdx[1], ct))
            return false;
        WordSlot pronSlot = slots[0];
        ReadOnlySpan<char> sentTrim = map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length).TrimEnd();
        if (pronSlot.Word.CanonicalText != "то" || pronSlot.Quoted || !IsExactWord(pronSlot) ||
            !map.Analysis.Text.AsSpan(map.Sentence.Start, pronSlot.Word.Token.Start - map.Sentence.Start).Trim().IsEmpty ||
            !sentTrim.EndsWith(".", StringComparison.Ordinal) ||
            slots.Any(s => s.Word.CanonicalText is "ли" or "бе" or "бре" or "абе" or "ма" or "де"))
            return false;
        if (verb.Kind != VerbalComplexKind.SyntheticFinite || verb.CopularCarrier || verb.InvariantCarrier)
            return false;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        int[] rawVerb = NonPlaceholderReadings(verbSlot.Word, sentenceInitial: false);
        if (!IsExactWord(verbSlot) || rawVerb.Length == 0 ||
            !rawVerb.All(r => verbSlot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb) ||
            !IsCleanPredicateToken(verbSlot.Word, sentenceInitial: false, ct))
            return false;

        int[] allFinite = AllFinitePersonalReadings(verbSlot.Word, ct);
        if (!verb.AllowsPerfectivePresent(map, clause))
            allFinite = FilterIndicativeFinite(verbSlot.Word, allFinite);
        if (allFinite.Length == 0 || !IsExclusivelyFirstOrSecond(verbSlot.Word, allFinite) ||
            verbSlot.Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative && !r.Features.HasConflictingFeatures))
            return false;

        int[] toPronReadings = Enumerable.Range(0, pronSlot.Word.Readings.Count)
            .Where(r => Guard.LicensedSubject("то", pronSlot.Word.Readings[r]))
            .ToArray();
        if (toPronReadings.Length == 0)
            return false;

        NominalChunk toChunk = new(
            FirstWord: 0,
            HeadWord: 0,
            LastWord: 0,
            HeadReadings: toPronReadings,
            Person: MorphologicalPerson.Third,
            Number: MorphologicalNumber.Singular,
            Gender: MorphologicalGender.Neuter,
            PronounSubject: true,
            FullMasculineSingular: false,
            ShortMasculineSingular: false,
            TemporalAdjunct: false,
            Coherent: true);
        AddSubjectRelation(slots, toChunk, verb, allFinite, canonicalKey, agreement,
            "pronoun_subject_clause_mismatch", mismatch: true, subjectProven: true);
        return true;
    }

    private static bool TryAddPostverbalUnhostedEncliticMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        NominalChunk? preSubject,
        VerbalComplex verb,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (preSubject is not { Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } subj ||
            !subj.PronounSubject && !subj.FullMasculineSingular)
            return false;
        if (verb.Kind != VerbalComplexKind.SyntheticFinite || verb.CopularCarrier || verb.InvariantCarrier ||
            verb.CarrierAccusativeCliticWordIndex >= 0 || verb.LastWordIndex != verb.FiniteCarrierWordIndex)
            return false;

        WordSlot[] slots = map.Slots;
        ReadOnlySpan<char> sentTrim = map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length).TrimEnd();
        if (!sentTrim.EndsWith(".", StringComparison.Ordinal) || slots.Any(s => s.Word.CanonicalText == "ли"))
            return false;

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        if (!IsExactWord(verbSlot) || verbSlot.Quoted)
            return false;
        int[] rawVerb = NonPlaceholderReadings(verbSlot.Word, sentenceInitial: false);
        if (rawVerb.Length == 0 || !rawVerb.All(r => verbSlot.Word.Readings[r].Features is
            {
                PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                VerbForm: MorphologicalVerbForm.Finite,
                DeclaredTransitivity: MorphologicalVerbTransitivity.Transitive,
                HasConflictingFeatures: false
            } && !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma)))
            return false;
        if (MatchingVerbReadings(verbSlot, rawVerb, subj).Length == 0)
            return false;

        int[] cWords = clause.WordIndices;
        int verbPos = Array.IndexOf(cWords, verb.LastWordIndex);
        if (verbPos < 0 || verbPos + 1 >= cWords.Length)
            return false;
        int cliticIdx = cWords[verbPos + 1];
        if (cliticIdx != verb.LastWordIndex + 1 || map.Peripheral[cliticIdx] ||
            !map.AreSpaceBridged(verb.LastWordIndex, cliticIdx, ct))
            return false;

        WordSlot cliticSlot = slots[cliticIdx];
        string cc = cliticSlot.Word.CanonicalText;
        if (!BulgarianFunctionWords.HasAny(cc, FunctionWordClasses.PureAccusativeClitic) || cc == "те" ||
            !IsExactWord(cliticSlot) || cliticSlot.Quoted ||
            cc == "я" && slots.Skip(cliticIdx + 1).Any(s => s.Word.CanonicalText == "я"))
            return false;

        if (verbPos + 2 < cWords.Length)
        {
            int nextIdx = cWords[verbPos + 2];
            WordSlot nextSlot = slots[nextIdx];
            string nc = nextSlot.Word.CanonicalText;
            if (cc == "я" && nc is "и" or "или" ||
                map.AreSpaceBridged(cliticIdx, nextIdx, ct) &&
                (HasFiniteReading(nextSlot) ||
                 nextSlot.Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative) ||
                 nextSlot.Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) ||
                 BulgarianFunctionWords.HasAny(nc, FunctionWordClasses.PreVerbalClauseBridge)))
                return false;
        }

        EmitPronounCaseMismatch(cliticSlot.Word, verbSlot.Word, ContextOverlayRole.Predicate, canonicalKey, agreement);
        return true;
    }

    private static bool TryAddModalDaSubjectVerbMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        NominalChunk subj,
        VerbalComplex verb,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (subj.ShortMasculineSingular || subj.TemporalAdjunct || subj.Number is null ||
            !verb.HasDaComplement || verb.CarrierAccusativeCliticWordIndex >= 0)
            return false;

        TextAnalysis analysis = map.Analysis;
        SentenceSpan sentence = map.Sentence;
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;

        WordSlot modalSlot = slots[verb.FiniteCarrierWordIndex];
        string modalCanon = modalSlot.Word.CanonicalText;
        bool isFutPast = modalCanon is "щях" or "щеше" or "щяхме" or "щяхте" or "щяха";
        bool isPersonalMoga = modalCanon is "мога" or "можеш" or "можем" or "можете" or "могат" or
            "можех" or "можехме" or "можехте" or "можеха" &&
            verb.CarrierAccusativeCliticWordIndex < 0;
        bool isInvariantModal = modalCanon is "няма" or "нямаше" or "трябва" or "трябваше" or
            "бива" or "биваше" or "следва" or "следваше" or "може" or "можеше" &&
            verb.CarrierAccusativeCliticWordIndex < 0;
        if (!isFutPast && !isPersonalMoga && !isInvariantModal)
            return false;

        if (isPersonalMoga)
        {
            int[] mogaFin = AllFinitePersonalReadings(modalSlot.Word, ct);
            if (!mogaFin.Any(mr => Guard.PersonNumberTupleMatches(
                    (subj.Person, subj.Number),
                    (modalSlot.Word.Readings[mr].Features.Person, modalSlot.Word.Readings[mr].Features.Number))))
                return false;
        }

        if (verb.BridgeWordIndices.Any(b => slots[b].Word.CanonicalText != "ли"))
            return false;

        CliticCluster daClitics = verb.PostDaOrAuxClitics;
        bool hasSubPronClitic = daClitics.AccusativeCliticWordIndex >= 0 ||
            daClitics.DativeCliticWordIndex >= 0 ||
            daClitics.ReflexiveWordIndex >= 0 && slots[daClitics.ReflexiveWordIndex].Word.CanonicalText == "си";
        if (hasSubPronClitic)
            return false;

        VerbalDependent subDep = verb.Dependents.First(d => d.Kind == VerbalDependentKind.DaFiniteComplement);
        int subPos = Array.IndexOf(clauseIdx, subDep.WordIndex);
        if (subPos < 0) return false;

        WordSlot subSlot = slots[subDep.WordIndex];
        if (!IsExactWord(subSlot) || !IsCleanPredicateToken(subSlot.Word, sentenceInitial: false, ct))
            return false;

        WordSlot subjHead = slots[subj.HeadWord];
        if (subjHead.Word.UsesGraph || IsPersonPlaceHomograph(analysis, subjHead.Word.OriginalIndex) ||
            IsAdverbialTimeOrMeasureHead(subjHead) || HasVocativeReading(subjHead) ||
            HasUnassignedNominativePronoun(slots, clauseIdx, bodyStart, subj))
            return false;
        for (int w = subj.FirstWord; w <= subj.HeadWord; w++)
            if (!IsCleanSubjectToken(slots[w].Word, IsSentenceInitialWord(analysis, sentence, w, slots[w].Word),
                    subj.PronounSubject && w == subj.HeadWord, ct))
                return false;

        int[] subPres = subDep.Readings
            .Where(r => subSlot.Word.Readings[r].Features.Tense == MorphologicalTense.Present)
            .ToArray();
        if (subPres.Length == 0) return false;
        if (subj.PronounSubject && subj.Person == MorphologicalPerson.Second &&
            HasSecondPersonImperative(subSlot.Word, subj.Number.Value))
            return false;

        int[] jointSub = subPres;
        if (isFutPast)
        {
            int[] auxFin = AllFinitePersonalReadings(modalSlot.Word, ct);
            jointSub = subPres.Where(sr => auxFin.Any(ar => Guard.PersonNumberTupleMatches(
                (modalSlot.Word.Readings[ar].Features.Person, modalSlot.Word.Readings[ar].Features.Number),
                (subSlot.Word.Readings[sr].Features.Person, subSlot.Word.Readings[sr].Features.Number)))).ToArray();
            if (jointSub.Length == 0)
                return true; // Already diagnosed by VerbalComplexBinder.CheckInternalWellFormedness (ща <-> Vfin).
        }

        if (jointSub.Any(r => Guard.PersonNumberTupleMatches(
                (subj.Person, subj.Number),
                (subSlot.Word.Readings[r].Features.Person, subSlot.Word.Readings[r].Features.Number))))
            return false;

        int postEndPos = clauseIdx.Length - 1;
        for (int p = subPos + 1; p < clauseIdx.Length; p++)
            if (slots[clauseIdx[p]].Word.CanonicalText == "да") { postEndPos = p - 1; break; }
        bool[] subGov = MarkGovernedPhrases(map, clauseIdx, subPos + 1, postEndPos, 0, subPos, ct);
        List<NominalChunk> subPostNps = CollectNominalChunks(map, clauseIdx, subPos + 1, postEndPos, subGov, ct);

        bool subIntransitive = subPres.All(r =>
            subSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
            !IsLinkingOrSemiCopularLemma(subSlot.Word.Readings[r].Lemma)) ||
            IsIntransitiveMotionOrSpeechLemma(subSlot, subPres);
        bool subObjectOccupied = subPostNps.Any(np => !np.TemporalAdjunct &&
            (np.ShortMasculineSingular ||
             !IsExclusivelyFirstOrSecond(subSlot.Word, jointSub) &&
             !HasCompatiblePostverbalSubject(slots, subPostNps, subSlot, jointSub)));
        bool provenSubject = subj.PronounSubject || subj.FullMasculineSingular ||
            isPersonalMoga && IsDefiniteOrProperSubjectCandidate(slots, subj) ||
            daClitics.HasReflexiveSe && IsDefiniteOrProperSubjectCandidate(slots, subj) &&
                !HasCompatiblePostverbalSubject(slots, subPostNps, subSlot, jointSub) ||
            subIntransitive && !HasCompatiblePostverbalSubject(slots, subPostNps, subSlot, jointSub) ||
            subObjectOccupied;
        if (!provenSubject)
            return false;

        AddSubjectRelation(slots, subj, SingleTokenComplex(subSlot.Index, jointSub), jointSub, canonicalKey, agreement,
            subj.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch",
            mismatch: true, subjectProven: true);
        return true;
    }

    private static void EmitVerbalInternalMismatches(
        SentenceClauseMap map,
        IReadOnlyList<VerbalInternalMismatch> mismatches,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement)
    {
        WordSlot[] slots = map.Slots;
        foreach (VerbalInternalMismatch m in mismatches)
        {
            TokenView target = slots[m.TargetSlotIndex].Word;
            switch (m.Kind)
            {
                case VerbalInternalMismatchKind.ParticleOrAuxVerbForm:
                {
                    TokenView ctx = slots[m.PartnerSlotIndex].Word;
                    List<ContextOverlayRoleSelection> roles =
                    [
                        new(ContextOverlayRole.Predicate, target.OriginalIndex, target.Token.Start, target.Token.Length,
                            m.TargetReadings, ["clause_fusion_participle_readings"]),
                        new(ContextOverlayRole.NominalContext, ctx.OriginalIndex, ctx.Token.Start, ctx.Token.Length,
                            m.PartnerReadings, ["clause_fusion_verbal_particle"])
                    ];
                    agreement.Add(new(target.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                        canonicalKey(roles), roles, ["particle_participle_mismatch"]));
                    break;
                }
                case VerbalInternalMismatchKind.ModalDaPersonNumberOrTransitivity:
                {
                    TokenView partner = slots[m.PartnerSlotIndex].Word;
                    List<ContextOverlayRoleSelection> roles =
                    [
                        new(ContextOverlayRole.Predicate, target.OriginalIndex, target.Token.Start, target.Token.Length,
                            m.TargetReadings, ["clause_fusion_finite_readings"]),
                        new(ContextOverlayRole.NominalContext, partner.OriginalIndex, partner.Token.Start, partner.Token.Length,
                            m.PartnerReadings, ["clause_fusion_modal_partner"])
                    ];
                    agreement.Add(new(target.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                        canonicalKey(roles), roles, ["modal_da_verb_mismatch", ContextLanes.ProductiveGraphMismatch]));
                    break;
                }
                case VerbalInternalMismatchKind.CliticClusterCaseOrOrder:
                {
                    TokenView partner = slots[m.PartnerSlotIndex].Word;
                    EmitPronounCaseMismatch(target, partner, ContextOverlayRole.Predicate, canonicalKey, agreement);
                    break;
                }
            }
        }
    }

    private readonly record struct ClauseDomain(
        ClauseSpan SourceClause,
        int[] WordIndices,
        int BodyStartPos,
        bool IsPreDaMatrixSlice,
        bool IsRelativeClause);

    private static List<ClauseDomain> BuildClauseDomains(SentenceClauseMap map, CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        List<ClauseDomain> domains = new(map.Clauses.Count + 2);
        foreach (ClauseSpan c in map.Clauses)
        {
            ct.ThrowIfCancellationRequested();
            if (c.WordIndices.Length == 0 || c.WordIndices.Any(w => slots[w].Quoted))
                continue;

            int[] baseWords = c.WordIndices;
            int baseBodyStart = c.BodyStartPos;

            // Carve out any unextracted comma-introduced finite-free relative sub-clause
            // ("Книгата, която прочелата сега, е много интересна.", "Хората, които чакал отвън, влязоха в залата.")
            // that SegmentClauses kept inside the outer ClauseSpan because sawFinite was false.
            for (int relStart = baseBodyStart + 1; relStart < baseWords.Length; relStart++)
            {
                if (!map.HasCommaBetween(baseWords[relStart - 1], baseWords[relStart]))
                    continue;
                int relWordPos = -1;
                if (IsRelativeWord(slots[baseWords[relStart]].Word.CanonicalText))
                {
                    relWordPos = relStart;
                }
                else if (IsActivePreposition(slots[baseWords[relStart]].Word, slots[baseWords[relStart]].Active) &&
                         relStart + 1 < baseWords.Length &&
                         map.AreSpaceBridged(baseWords[relStart], baseWords[relStart + 1], ct) &&
                         IsRelativeWord(slots[baseWords[relStart + 1]].Word.CanonicalText))
                {
                    relWordPos = relStart + 1;
                }
                if (relWordPos < 0 || relWordPos + 1 >= baseWords.Length)
                    continue;

                int relEnd = -1;
                bool relHasFiniteOrModal = false;
                for (int k = relWordPos + 1; k < baseWords.Length; k++)
                {
                    if (SlotHasTrueFiniteOrModal(slots, baseWords, k))
                    {
                        relHasFiniteOrModal = true;
                        break;
                    }
                    if (k + 1 < baseWords.Length && map.HasCommaBetween(baseWords[k], baseWords[k + 1]))
                    {
                        relEnd = k;
                        break;
                    }
                }

                if (!relHasFiniteOrModal && relEnd < 0 &&
                    Enumerable.Range(0, relStart).Any(p => SlotHasTrueFiniteOrModal(slots, baseWords, p)))
                {
                    relEnd = baseWords.Length - 1;
                }

                if (!relHasFiniteOrModal && relEnd > relWordPos)
                {
                    int[] relSlice = baseWords[relWordPos..(relEnd + 1)];
                    domains.Add(new(c, relSlice, 0, IsPreDaMatrixSlice: false, IsRelativeClause: true));
                    baseWords = baseWords[..relStart].Concat(baseWords[(relEnd + 1)..]).ToArray();
                    break;
                }
            }

            // Carve out any unextracted comma-introduced finite-free 'че' complement sub-clause
            // ("Директорът каза, че новата система работейки отлично.") that SegmentClauses kept
            // inside the outer ClauseSpan because RangeHasFinite on the right side was false.
            for (int subStart = baseBodyStart + 1; subStart < baseWords.Length; subStart++)
            {
                if (!map.HasCommaBetween(baseWords[subStart - 1], baseWords[subStart]) ||
                    slots[baseWords[subStart]].Word.CanonicalText != "че" ||
                    slots[baseWords[subStart - 1]].Word.CanonicalText is "макар" or "въпреки" or "само" or "освен" ||
                    subStart + 1 >= baseWords.Length)
                    continue;

                bool subHasFiniteOrModal = Enumerable.Range(subStart + 1, baseWords.Length - subStart - 1)
                    .Any(k => SlotHasTrueFiniteOrModal(slots, baseWords, k));
                if (!subHasFiniteOrModal &&
                    Enumerable.Range(0, subStart).Any(p => SlotHasTrueFiniteOrModal(slots, baseWords, p)))
                {
                    int cheWordIdx = baseWords[subStart];
                    var cheBoundary = new ClauseBoundary(
                        ClauseBoundaryKind.Subordinator,
                        PrepositionWordIndex: -1,
                        IntroducerFirstWordIndex: cheWordIdx,
                        IntroducerLastWordIndex: cheWordIdx,
                        CommaTokenIndex: -1,
                        HasConcessiveIntroducer: false);
                    var cheClause = new ClauseSpan(
                        ClauseId: -1,
                        Boundary: cheBoundary,
                        WordIndices: baseWords[subStart..],
                        BodyStartPos: 1,
                        Matrix: false,
                        AllowsPerfectivePresent: false);
                    domains.Add(new(cheClause, baseWords[(subStart + 1)..], 0, IsPreDaMatrixSlice: false, IsRelativeClause: false));
                    baseWords = baseWords[..subStart];
                    break;
                }
            }

            if (baseWords.Length == 0)
                continue;

            bool isRel = c.Boundary.Kind == ClauseBoundaryKind.ParentheticalRelative ||
                c.Boundary.IntroducerLastWordIndex >= 0 &&
                IsRelativeWord(slots[c.Boundary.IntroducerLastWordIndex].Word.CanonicalText);

            // Expose pre-'да' matrix slice ("Умореният пътник искала | да намери тиха стая.", "Младите инженери можейки | да поправят..."),
            // EXCEPT when 'да' is part of the clause's own PrepositionalSubordinator introducer ("Без поканен гост да дойде..." / "за да влезе")!
            int daPos = -1;
            int searchStart = Math.Min(baseBodyStart, baseWords.Length);
            for (int p = searchStart; p < baseWords.Length; p++)
            {
                int w = baseWords[p];
                if (w == c.Boundary.IntroducerLastWordIndex ||
                    c.Boundary.Kind == ClauseBoundaryKind.PrepositionalSubordinator ||
                    map.TryGetPrepositionalSubordinatorForIntroducer(w, out _))
                    continue;
                if (slots[w].Word.CanonicalText == "да")
                {
                    daPos = p;
                    break;
                }
            }

            if (daPos > searchStart)
            {
                domains.Add(new(c, baseWords[..daPos], isRel ? 0 : searchStart, IsPreDaMatrixSlice: true, IsRelativeClause: isRel));
            }
            else
            {
                domains.Add(new(c, baseWords, isRel ? 0 : searchStart, IsPreDaMatrixSlice: false, IsRelativeClause: isRel));
            }
        }
        return domains;
    }

    private static bool SlotHasTrueFiniteOrModal(WordSlot[] slots, int[] indices, int pos)
    {
        WordSlot s = slots[indices[pos]];
        if (!IsExactWord(s)) return true;
        string c = s.Word.CanonicalText;
        if (c is "ще" or "няма" or "нямаше" or "има" or "имаше" or "трябва" or "трябваше" or "може" or
            "нека" or "дано" or "стига" or "хайде" or "ей" or "ето" or "да")
            return true;
        int[] active = s.Active.Length > 0 ? s.Active : NonPlaceholderReadings(s.Word, indices[pos] == 0);
        foreach (int r in active)
        {
            ReadingView row = s.Word.Readings[r];
            MorphologicalFeatures f = row.Features;
            bool isTrueCopula = TextPredicateLinkAnalyzer.CopularCarrier(row) &&
                !(c == "си" && (pos == 0 || slots[indices[pos - 1]].Word.CanonicalText != "ти"));
            if (isTrueCopula || f.PartOfSpeech == MorphologicalPartOfSpeech.Verb && f.VerbForm == MorphologicalVerbForm.Finite)
                return true;
        }
        return false;
    }

    private static bool SentenceHasAnyPredicateAnchor(WordSlot[] slots, bool[] peripheral)
    {
        int[] core = Enumerable.Range(0, slots.Length).Where(i => !peripheral[i]).ToArray();
        for (int pos = 0; pos < core.Length; pos++)
        {
            if (SlotHasTrueFiniteOrModal(slots, core, pos))
                return true;
            WordSlot s = slots[core[pos]];
            int[] active = s.Active.Length > 0 ? s.Active : NonPlaceholderReadings(s.Word, core[pos] == 0);
            if (active.Any(r => s.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    Article: null or MorphologicalArticle.Indefinite
                }))
                return true;
        }
        return false;
    }

    private static void ScanClauseDomainVerbFormIntrusions(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (!map.HasTerminalPunctuation)
            return;

        TextAnalysis analysis = map.Analysis;
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        bool sentenceHasPredicateAnchor = SentenceHasAnyPredicateAnchor(slots, peripheral);

        foreach (ClauseDomain domain in BuildClauseDomains(map, ct))
        {
            ct.ThrowIfCancellationRequested();
            int[] domIdx = domain.WordIndices.Where(w => !peripheral[w] && !slots[w].Quoted).ToArray();
            if (domIdx.Length < 2) continue;
            if (domIdx[0] > 0 && slots[domIdx[0] - 1].Quoted && map.HasCommaBetween(domIdx[0] - 1, domIdx[0]))
                continue;

            bool anyFiniteOrAuxOrModal = false;
            bool anyImperative = false;
            bool anyBareActiveL = false;
            int converbPos = -1;
            int[] converbReadings = [];

            for (int pos = 0; pos < domIdx.Length; pos++)
            {
                if (SlotHasTrueFiniteOrModal(slots, domIdx, pos))
                {
                    anyFiniteOrAuxOrModal = true;
                    break;
                }
                WordSlot s = slots[domIdx[pos]];
                int[] active = s.Active.Length > 0 ? s.Active : NonPlaceholderReadings(s.Word, domIdx[pos] == 0);
                foreach (int r in active)
                {
                    MorphologicalFeatures f = s.Word.Readings[r].Features;
                    if (f.PartOfSpeech == MorphologicalPartOfSpeech.Verb && f.VerbForm == MorphologicalVerbForm.Imperative)
                    {
                        anyImperative = true;
                    }
                    else if (f.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
                             f.VerbForm == MorphologicalVerbForm.Participle &&
                             f.ParticipleKind is MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive &&
                             f.Article is null or MorphologicalArticle.Indefinite)
                    {
                        anyBareActiveL = true;
                    }
                }
                if (converbPos < 0 && active.Length > 0 && active.All(r => s.Word.Readings[r].Features is
                    { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Converb, HasConflictingFeatures: false }))
                {
                    converbPos = pos;
                    converbReadings = active.Order().ToArray();
                }
            }

            if (anyFiniteOrAuxOrModal) continue;

            // 1. Converb-only clause domain (QA V1-A #1 safe).
            if (!anyImperative && !anyBareActiveL && converbPos >= 0)
            {
                bool[] convGov = MarkGovernedPhrases(map, domIdx, 0, converbPos, converbPos, converbPos, ct);
                List<NominalChunk> preConvNps = converbPos > 0
                    ? CollectNominalChunks(map, domIdx, 0, converbPos - 1, convGov, ct)
                    : [];
                NominalChunk? cSubj = converbPos > 0 &&
                    !map.HasDirectCommaBetween(domIdx, 0, converbPos) &&
                    preConvNps is [{ Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } cs] &&
                    IsDefiniteOrProperSubjectCandidate(slots, cs)
                    ? cs
                    : null;
                bool hasPreConverbSubject = cSubj is not null;
                bool postDaHasCommaMatrixVerb = false;
                if (domain.IsPreDaMatrixSlice)
                {
                    int[] srcWords = domain.SourceClause.WordIndices;
                    int daInSrc = Array.FindIndex(srcWords, w => slots[w].Word.CanonicalText == "да" && w > domIdx[^1]);
                    if (daInSrc >= 0)
                    {
                        bool sawCommaAfterDa = false;
                        for (int k = daInSrc + 1; k < srcWords.Length; k++)
                        {
                            if (map.HasCommaBetween(srcWords[k - 1], srcWords[k])) sawCommaAfterDa = true;
                            if (sawCommaAfterDa && SlotHasTrueFiniteOrModal(slots, srcWords, k))
                            {
                                postDaHasCommaMatrixVerb = true;
                                break;
                            }
                        }
                    }
                }
                if (domain.SourceClause.Boundary.Kind == ClauseBoundaryKind.Subordinator &&
                    domain.SourceClause.WordIndices.Length > 0 &&
                    slots[domain.SourceClause.WordIndices[0]].Word.CanonicalText == "че" &&
                    cSubj is not null)
                {
                    EmitParticleOrAuxVerbFormMismatch(slots[cSubj.HeadWord].Word, slots[domIdx[converbPos]].Word,
                        converbReadings, canonicalKey, agreement);
                    continue;
                }
                bool converbIntrusion = !sentenceHasPredicateAnchor ||
                    domain.IsPreDaMatrixSlice && hasPreConverbSubject && !postDaHasCommaMatrixVerb ||
                    domain.IsRelativeClause && converbPos > 0 && !map.HasDirectCommaBetween(domIdx, 0, converbPos) ||
                    domain.SourceClause.Boundary.Kind is ClauseBoundaryKind.SentenceInitial or ClauseBoundaryKind.Coordinator && hasPreConverbSubject;
                if (converbIntrusion)
                {
                    TokenView convWord = slots[domIdx[converbPos]].Word;
                    List<ContextOverlayRoleSelection> roles =
                    [
                        new(ContextOverlayRole.Predicate, convWord.OriginalIndex, convWord.Token.Start, convWord.Token.Length,
                            converbReadings, ["clause_fusion_converb_intrusion"])
                    ];
                    agreement.Add(new(convWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                        canonicalKey(roles), roles, ["particle_participle_mismatch"]));
                    continue;
                }
            }

            // 2. V1: Definite active -л participle replacing finite predicate (QA V1-A #2 safe).
            if (!anyImperative && !anyBareActiveL)
            {
                for (int pos = 1; pos < domIdx.Length; pos++)
                {
                    WordSlot ps = slots[domIdx[pos]];
                    int[] rawPRows = (ps.Active.Length > 0 ? ps.Active : NonPlaceholderReadings(ps.Word, sentenceInitial: false)).Order().ToArray();
                    int[] pRows = rawPRows;
                    if (domain.IsRelativeClause && pos == 1 &&
                        BulgarianFunctionWords.HasAny(slots[domIdx[0]].Word.CanonicalText, FunctionWordClasses.RelativeSubjectPronoun))
                    {
                        int[] fullPRows = NonPlaceholderReadings(ps.Word, sentenceInitial: false).Order().ToArray();
                        int[] defActOnly = fullPRows.Where(r => ps.Word.Readings[r].Features is
                        {
                            PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                            VerbForm: MorphologicalVerbForm.Participle,
                            ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                            Article: MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short,
                            HasConflictingFeatures: false
                        }).ToArray();
                        if (defActOnly.Length > 0 && fullPRows.All(r => defActOnly.Contains(r) ||
                                ps.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun))
                            pRows = defActOnly;
                    }
                    bool isDefActL = pRows.Length > 0 && pRows.All(r => ps.Word.Readings[r].Features is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                        VerbForm: MorphologicalVerbForm.Participle,
                        ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                        Article: MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short,
                        HasConflictingFeatures: false
                    });
                    if (!isDefActL || !map.AreSpaceBridged(domIdx[pos - 1], domIdx[pos], ct))
                        continue;

                    // Skip definite active participles modifying a following indefinite noun phrase ("[пристигналите [нови] ученици]").
                    bool modifiesFollowingIndefNoun = false;
                    for (int m = pos + 1; m < domIdx.Length; m++)
                    {
                        if (!map.AreSpaceBridged(domIdx[m - 1], domIdx[m], ct)) break;
                        WordSlot ms = slots[domIdx[m]];
                        if (ms.Active.Any(r => ms.Word.Readings[r].Features is
                            { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Indefinite }))
                        {
                            modifiesFollowingIndefNoun = true;
                            break;
                        }
                        if (!ms.ComparativeGraphModifier &&
                            !(ms.Active.Length > 0 && ms.Active.All(r => ms.Word.Readings[r].Features is
                                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Article: MorphologicalArticle.Indefinite })))
                            break;
                    }
                    if (modifiesFollowingIndefNoun)
                        continue;

                    bool[] preGov = MarkGovernedPhrases(map, domIdx, 0, pos - 1, pos, pos, ct);
                    WordSlot prev = slots[domIdx[pos - 1]];
                    bool prevIsSubjectAnchor;
                    if (prev.Word.CanonicalText is "не" or "се" or "си")
                    {
                        if (pos < 2 || preGov[pos - 2] || !map.AreSpaceBridged(domIdx[pos - 2], domIdx[pos - 1], ct))
                            continue;
                        WordSlot preParticle = slots[domIdx[pos - 2]];
                        string preCanon = preParticle.Word.CanonicalText;
                        prevIsSubjectAnchor = BulgarianFunctionWords.TryGetPersonalSubjectPronoun(preCanon, out _) ||
                            BulgarianFunctionWords.HasAny(preCanon, FunctionWordClasses.RelativeSubjectPronoun) ||
                            HasPureNominal(preParticle.Word, preParticle.Active) &&
                            preParticle.Active.All(r => preParticle.Word.Readings[r].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full);
                    }
                    else
                    {
                        if (preGov[pos - 1])
                            continue;
                        string prevCanon = prev.Word.CanonicalText;
                        prevIsSubjectAnchor = BulgarianFunctionWords.TryGetPersonalSubjectPronoun(prevCanon, out _) ||
                            BulgarianFunctionWords.HasAny(prevCanon, FunctionWordClasses.RelativeSubjectPronoun) ||
                            HasPureNominal(prev.Word, prev.Active);
                    }

                    if (prevIsSubjectAnchor)
                    {
                        EmitParticleOrAuxVerbFormMismatch(prev.Word, ps.Word, pRows, canonicalKey, agreement);
                        break;
                    }
                }
            }

            // 3. V3a: Bare active -л participle without auxiliary (1p/2p "*Аз чел...", relative "*които чакал...", or 3p gender/number mismatch "*Умореният пътник искала да...").
            if (anyBareActiveL && !anyImperative)
            {
                for (int pos = 1; pos < domIdx.Length; pos++)
                {
                    WordSlot ps = slots[domIdx[pos]];
                    if (!TryGetUnambiguousActiveParticipleReadings(ps.Word, sentenceInitial: false, out int[] lRows) &&
                        !TryGetPrunedActiveParticipleReadings(ps, out lRows))
                        continue;
                    lRows = lRows.Order().ToArray();

                    int preEnd = pos - 1;
                    bool sawAccusativeClitic = false;
                    bool sawReflexiveSe = false;
                    while (preEnd >= 0 && map.AreSpaceBridged(domIdx[preEnd], domIdx[preEnd + 1], ct) &&
                           BulgarianFunctionWords.HasAny(slots[domIdx[preEnd]].Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge))
                    {
                        if (preEnd == 0 && slots[domIdx[0]].Word.CanonicalText is "ти" or "те" or "ви")
                            break;
                        if (slots[domIdx[preEnd]].Word.CanonicalText == "се")
                            sawReflexiveSe = true;
                        if (BulgarianFunctionWords.HasAny(slots[domIdx[preEnd]].Word.CanonicalText, FunctionWordClasses.ShortAccusativeClitic))
                            sawAccusativeClitic = true;
                        preEnd--;
                    }
                    if (preEnd < 0 || map.HasCommaBetween(domIdx[preEnd], domIdx[preEnd + 1]))
                        continue;

                    bool lIntransitive = lRows.All(r =>
                        ps.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                        !IsLinkingOrSemiCopularLemma(ps.Word.Readings[r].Lemma));
                    bool[] gov = MarkGovernedPhrases(map, domIdx, 0, domIdx.Length - 1, preEnd + 1, pos, ct);
                    List<NominalChunk> postNps = CollectNominalChunks(map, domIdx, pos + 1, domIdx.Length - 1, gov, ct);
                    bool postCompatibleSubject = postNps.Any(p => !p.TemporalAdjunct &&
                        lRows.Any(r => Guard.NominalTupleMatches(
                            (ps.Word.Readings[r].Features.Number, ps.Word.Readings[r].Features.Gender),
                            (p.Number, p.Gender))));
                    for (int p = pos + 1; !postCompatibleSubject && p < domIdx.Length; p++)
                        if (!gov[p] && slots[domIdx[p]].Word.Token.Text is { Length: > 0 } t && char.IsUpper(t[0]))
                            postCompatibleSubject = true;
                    bool postDirectObject = !lIntransitive && postNps.Any(p => !p.TemporalAdjunct) && !postCompatibleSubject;

                    // Relative subject pronoun ("Хората, които дошъл навреме, ...") — guarded against OVS/pro-drop object-relatives ("това, което ги научил Олаф", "книгата, която взел от библиотеката").
                    if (preEnd == 0 && domain.IsRelativeClause &&
                        BulgarianFunctionWords.TryGetRelativeSubjectAxes(slots[domIdx[0]].Word.CanonicalText,
                            out MorphologicalNumber rNum, out MorphologicalGender? rGen))
                    {
                        if (!sawAccusativeClitic && (lIntransitive || sawReflexiveSe && !postCompatibleSubject || postDirectObject))
                        {
                            bool relMatches = lRows.Any(r => ps.Word.Readings[r].Features.Number == rNum &&
                                (rGen is null || ps.Word.Readings[r].Features.Gender == rGen));
                            if (!relMatches)
                            {
                                EmitParticleOrAuxVerbFormMismatch(slots[domIdx[0]].Word, ps.Word, lRows, canonicalKey, agreement);
                                break;
                            }
                        }
                        continue;
                    }

                    if (domIdx.Take(preEnd + 1).Any(w => slots[w].Word.CanonicalText is "и" or "или"))
                        continue;

                    List<NominalChunk> preNps = CollectNominalChunks(map, domIdx, 0, preEnd, gov, ct);
                    if (preNps is not [{ Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } preNp])
                        continue;

                    if (preNp.PronounSubject && preNp.Person is MorphologicalPerson.First or MorphologicalPerson.Second)
                    {
                        if (!lIntransitive && postNps.Any(p => !p.TemporalAdjunct) &&
                            lRows.Any(r => ps.Word.Readings[r].Features.Number == preNp.Number))
                        {
                            EmitParticleOrAuxVerbFormMismatch(slots[preNp.HeadWord].Word, ps.Word, lRows, canonicalKey, agreement);
                            break;
                        }
                        if (!lIntransitive && postNps.Any(p => !p.TemporalAdjunct))
                        {
                            TokenView sw = slots[preNp.HeadWord].Word;
                            List<ContextOverlayRoleSelection> roles =
                            [
                                new(ContextOverlayRole.Predicate, ps.Word.OriginalIndex, ps.Word.Token.Start, ps.Word.Token.Length,
                                    lRows, ["clause_fusion_participle_readings"]),
                                new(ContextOverlayRole.Subject, sw.OriginalIndex, sw.Token.Start, sw.Token.Length,
                                    preNp.HeadReadings, ["pronoun_subject_clause_mismatch"])
                            ];
                            agreement.Add(new(ps.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                                canonicalKey(roles), roles, ["compound_subj_participle_mismatch"]));
                            break;
                        }
                        continue;
                    }

                    WordSlot headSlot = slots[preNp.HeadWord];
                    if (headSlot.Word.UsesGraph || IsPersonPlaceHomograph(analysis, headSlot.Word.OriginalIndex) ||
                        IsAdverbialTimeOrMeasureHead(headSlot) || HasVocativeReading(headSlot) ||
                        preNp.Number == MorphologicalNumber.Singular && preNp.LastWord != domIdx[preEnd] &&
                            headSlot.Active.Any(r => PartitiveOrCollectiveHeads.Contains(Norm(headSlot.Word.Readings[r].Lemma))))
                        continue;

                    bool resumedMascFullByGo = preNp.FullMasculineSingular &&
                        Enumerable.Range(preNp.HeadWord + 1, Math.Max(0, domIdx[pos] - preNp.HeadWord - 1))
                            .Any(w => slots[w].Word.CanonicalText == "го");
                    bool proven3pSubj = preNp.PronounSubject ||
                        (!sawAccusativeClitic || preNp.FullMasculineSingular && !resumedMascFullByGo) &&
                        IsDefiniteOrProperSubjectCandidate(slots, preNp) &&
                        (preNp.FullMasculineSingular ||
                         !postCompatibleSubject && (domain.IsPreDaMatrixSlice || lIntransitive || postDirectObject));
                    if (proven3pSubj && preNp.Number is not null)
                    {
                        MorphologicalGender? reqGen = !preNp.PronounSubject && preNp.Number == MorphologicalNumber.Singular ? preNp.Gender : null;
                        if (!preNp.PronounSubject && preNp.Number == MorphologicalNumber.Singular && reqGen is null)
                            continue;
                        bool lMatches = lRows.Any(r =>
                        {
                            MorphologicalFeatures pf = ps.Word.Readings[r].Features;
                            return pf.Number == preNp.Number && (reqGen is null || pf.Gender == reqGen);
                        });
                        if (!lMatches)
                        {
                            // Avoid duplicating PredicativeGrammarEvidence.BindBareActiveParticiples on simple whole-clause bare -л mismatches without postNps or pre-'да' slice.
                            if (!domain.IsPreDaMatrixSlice && !domain.IsRelativeClause && !postNps.Any(p => !p.TemporalAdjunct) &&
                                preEnd == pos - 1 && preNp.FirstWord == domIdx[0] &&
                                preNp.LastWord == domIdx[preEnd] && pos == domIdx.Length - 1)
                                continue;
                            TokenView sw = headSlot.Word;
                            List<ContextOverlayRoleSelection> roles =
                            [
                                new(ContextOverlayRole.Predicate, ps.Word.OriginalIndex, ps.Word.Token.Start, ps.Word.Token.Length,
                                    lRows, ["clause_fusion_participle_readings"]),
                                new(ContextOverlayRole.Subject, sw.OriginalIndex, sw.Token.Start, sw.Token.Length,
                                    preNp.HeadReadings, [preNp.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch"])
                            ];
                            for (int w = preNp.FirstWord; w < preNp.HeadWord; w++)
                                if (!slots[w].Word.UsesGraph && slots[w].Active.Length > 0)
                                    roles.Add(new(ContextOverlayRole.Attribute, slots[w].Word.OriginalIndex, slots[w].Word.Token.Start,
                                        slots[w].Word.Token.Length, slots[w].Active, ["clause_fusion_nominal_modifier"]));
                            agreement.Add(new(ps.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                                canonicalKey(roles), roles, ["compound_subj_participle_mismatch"]));
                            break;
                        }
                    }
                }
            }

            // 4. V3b: Pure Imperative or bare PastPassive intrusion in this finite-free ClauseDomain.
            ScanDomainImperativeAndPassiveIntrusions(map, domain, domIdx, anyBareActiveL, canonicalKey, agreement, ct);
        }
    }

    private static bool TryGetPrunedActiveParticipleReadings(WordSlot ps, out int[] lRows)
    {
        lRows = [];
        if (ps.Word.UsesGraph || ps.Active.Length == 0) return false;
        foreach (int r in ps.Active)
        {
            ReadingView row = ps.Word.Readings[r];
            if (row.Features is not
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    Article: null or MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                } || !IsCleanOrBenignLegacyVerb(row) || TextPredicateLinkAnalyzer.CopularLemma(row.Lemma))
                return false;
        }
        lRows = ps.Active;
        return true;
    }

    private static void ScanDomainImperativeAndPassiveIntrusions(
        SentenceClauseMap map,
        ClauseDomain domain,
        int[] domIdx,
        bool anyBareActiveL,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        TextAnalysis analysis = map.Analysis;
        WordSlot[] slots = map.Slots;

        for (int pos = 0; pos < domIdx.Length; pos++)
        {
            int i = domIdx[pos];
            WordSlot s = slots[i];
            int[] realRows = NonPlaceholderReadings(s.Word, i == 0);
            if (realRows.Length == 0) continue;

            bool pureImperative = realRows.All(r => s.Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Imperative, HasConflictingFeatures: false });
            int[] barePassiveRows = realRows.Where(r => s.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastPassive,
                    Article: null or MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                }).Order().ToArray();
            bool pureBarePassive = barePassiveRows.Length > 0 && realRows.All(r =>
                barePassiveRows.Contains(r) ||
                s.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
                    Article: MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                });
            if (!pureImperative && !pureBarePassive) continue;

            int preEnd = pos - 1;
            bool sawNeg = false, sawPronClitic = false;
            int cliticIdx = -1;
            while (preEnd >= 0 && map.AreSpaceBridged(domIdx[preEnd], domIdx[preEnd + 1], ct))
            {
                string pc = slots[domIdx[preEnd]].Word.CanonicalText;
                if (pc == "не") { sawNeg = true; preEnd--; continue; }
                if (BulgarianFunctionWords.HasAny(pc, FunctionWordClasses.ReflexiveAccusativeSe | FunctionWordClasses.ReflexiveDativeSi))
                { preEnd--; continue; }
                if (pc != "те" && BulgarianFunctionWords.HasAny(pc, FunctionWordClasses.ShortPronominalClitic))
                {
                    sawPronClitic = true;
                    cliticIdx = domIdx[preEnd];
                    preEnd--;
                    continue;
                }
                break;
            }

            if (domain.IsRelativeClause &&
                (preEnd == 0 || preEnd == 1 && IsActivePreposition(slots[domIdx[0]].Word, slots[domIdx[0]].Active)) &&
                BulgarianFunctionWords.HasAny(slots[domIdx[preEnd]].Word.CanonicalText, FunctionWordClasses.RelativeSubjectPronoun) &&
                !map.HasCommaBetween(domIdx[preEnd], domIdx[preEnd + 1]) &&
                !(pureBarePassive && barePassiveRows.Length < realRows.Length &&
                  pos + 1 < domIdx.Length && map.AreSpaceBridged(domIdx[pos], domIdx[pos + 1], ct) &&
                  HasNominalHeadReading(slots[domIdx[pos + 1]])))
            {
                EmitParticleOrAuxVerbFormMismatch(slots[domIdx[preEnd]].Word, s.Word,
                    pureImperative ? realRows : barePassiveRows, canonicalKey, agreement);
                return;
            }

            if (pureImperative)
            {
                if (sawPronClitic && !sawNeg && cliticIdx >= 0 && preEnd < 0 && slots[domIdx[0]].Word.CanonicalText != "я")
                {
                    EmitParticleOrAuxVerbFormMismatch(slots[cliticIdx].Word, s.Word, realRows, canonicalKey, agreement);
                    return;
                }
                if (preEnd < 0 || map.HasCommaBetween(domIdx[preEnd], domIdx[preEnd + 1]))
                    continue;

                bool[] gov = MarkGovernedPhrases(map, domIdx, 0, domIdx.Length - 1, preEnd + 1, pos, ct);
                List<NominalChunk> preNps = CollectNominalChunks(map, domIdx, 0, preEnd, gov, ct);
                List<NominalChunk> postNps = CollectNominalChunks(map, domIdx, pos + 1, domIdx.Length - 1, gov, ct);
                if (postNps.FirstOrDefault(p => p.Coherent && p.FullMasculineSingular) is { } postFullSubj)
                {
                    AddSubjectRelation(slots, postFullSubj, SingleTokenComplex(s.Index, realRows), realRows, canonicalKey, agreement,
                        "postverbal_nominal_reading", mismatch: true, subjectProven: true);
                    return;
                }
                if (preNps is not [{ Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } preNp])
                    continue;
                WordSlot headSlot = slots[preNp.HeadWord];
                if (HasVocativeReading(headSlot) || IsAdverbialTimeOrMeasureHead(headSlot) ||
                    IsPersonPlaceHomograph(analysis, headSlot.Word.OriginalIndex))
                    continue;

                bool non2ndPronoun = domIdx.Length >= 3 && preNp.PronounSubject && preNp.Person != MorphologicalPerson.Second;
                bool impIntransitive = realRows.All(r =>
                    s.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(s.Word.Readings[r].Lemma));
                bool definite3rdSubject = !preNp.PronounSubject && IsDefiniteOrProperSubjectCandidate(slots, preNp) &&
                    (preNp.FullMasculineSingular || impIntransitive || postNps.Any(p => !p.TemporalAdjunct));
                if (non2ndPronoun || definite3rdSubject)
                {
                    AddSubjectRelation(slots, preNp, SingleTokenComplex(s.Index, realRows), realRows, canonicalKey, agreement,
                        preNp.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch",
                        mismatch: true, subjectProven: true);
                    return;
                }
            }
            else if (pureBarePassive && !anyBareActiveL)
            {
                if (sawPronClitic && cliticIdx >= 0)
                {
                    EmitParticleOrAuxVerbFormMismatch(slots[cliticIdx].Word, s.Word, barePassiveRows, canonicalKey, agreement);
                    return;
                }

                bool[] gov = MarkGovernedPhrases(map, domIdx, 0, domIdx.Length - 1, Math.Max(0, preEnd + 1), pos, ct);
                List<NominalChunk> preNps = preEnd >= 0
                    ? CollectNominalChunks(map, domIdx, 0, preEnd, gov, ct)
                    : [];
                List<NominalChunk> postNps = CollectNominalChunks(map, domIdx, pos + 1, domIdx.Length - 1, gov, ct);

                if (pos == 0 && postNps is [{ Coherent: true, TemporalAdjunct: false } firstObj] &&
                    map.AreSpaceBridged(s.Index, firstObj.FirstWord, ct) &&
                    (firstObj.ShortMasculineSingular ||
                     IsDefiniteOrProperSubjectCandidate(slots, firstObj) &&
                     !barePassiveRows.Any(r => s.Word.Readings[r].Features.Number == firstObj.Number)))
                {
                    EmitParticleOrAuxVerbFormMismatch(slots[firstObj.HeadWord].Word, s.Word, barePassiveRows, canonicalKey, agreement);
                    return;
                }

                if (preEnd < 0 || map.HasCommaBetween(domIdx[preEnd], domIdx[preEnd + 1]) ||
                    preNps is not [{ Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false } preNp] ||
                    map.HasDirectCommaBetween(domIdx, Array.IndexOf(domIdx, preNp.LastWord), pos))
                    continue;

                bool pronSubject = preNp.PronounSubject;
                bool passIntransitive = barePassiveRows.All(r =>
                    s.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive);
                bool followedByDa = pos + 1 < domIdx.Length && slots[domIdx[pos + 1]].Word.CanonicalText == "да";
                bool genderNumberMismatch = !pronSubject && preNp.Number is not null &&
                    !barePassiveRows.Any(r =>
                    {
                        MorphologicalFeatures pf = s.Word.Readings[r].Features;
                        return pf.Number == preNp.Number &&
                               (preNp.Number != MorphologicalNumber.Singular || preNp.Gender is null || pf.Gender == preNp.Gender);
                    });
                bool hasSyncreticAdj = barePassiveRows.Length < realRows.Length;
                bool hasDirectObject = hasSyncreticAdj
                    ? postNps.Any(p => !p.TemporalAdjunct && (p.ShortMasculineSingular || IsDefiniteOrProperSubjectCandidate(slots, p)))
                    : postNps.Any(p => !p.TemporalAdjunct);

                if ((pronSubject && (!hasSyncreticAdj || passIntransitive || followedByDa || genderNumberMismatch || hasDirectObject)) ||
                    IsDefiniteOrProperSubjectCandidate(slots, preNp) &&
                    (passIntransitive && !hasSyncreticAdj || followedByDa || genderNumberMismatch && !hasSyncreticAdj || hasDirectObject))
                {
                    EmitParticleOrAuxVerbFormMismatch(slots[preNp.HeadWord].Word, s.Word, barePassiveRows, canonicalKey, agreement);
                    return;
                }
            }
        }
    }

    private static void EmitParticleOrAuxVerbFormMismatch(
        TokenView contextWord,
        TokenView badVerbWord,
        int[] badVerbReadings,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement)
    {
        int[] ctxReadings = Enumerable.Range(0, contextWord.Readings.Count)
            .Where(r => !Guard.IsSourcePlaceholder(contextWord, contextWord.Readings[r]))
            .ToArray();
        if (ctxReadings.Length == 0 || badVerbReadings.Length == 0) return;

        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, badVerbWord.OriginalIndex, badVerbWord.Token.Start, badVerbWord.Token.Length,
                badVerbReadings, ["clause_fusion_participle_readings"]),
            new(ContextOverlayRole.NominalContext, contextWord.OriginalIndex, contextWord.Token.Start, contextWord.Token.Length,
                ctxReadings, ["clause_fusion_verbal_particle"])
        ];
        agreement.Add(new(badVerbWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, ["particle_participle_mismatch"]));
    }

    private static void ScanPrepositionCaseMismatches(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;

        for (int i = 0; i < slots.Length - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot slot = slots[i], next = slots[i + 1];
            if (peripheral[i] || peripheral[i + 1] || slot.Quoted || next.Quoted ||
                !IsExactWord(slot) || !IsExactWord(next) ||
                !map.AreSpaceBridged(i, i + 1, ct))
                continue;

            string prep = slot.Word.CanonicalText;
            string c2 = next.Word.CanonicalText;
            if (prep == "като" || !IsActivePreposition(slot.Word, slot.Active) ||
                !BulgarianFunctionWords.HasAny(prep, FunctionWordClasses.ClauseCaseGovernedPreposition))
                continue;
            if (map.IsPrepositionalSubordinatorPrep(i))
                continue;

            bool precedesVerbGroup = i + 2 < slots.Length && map.AreSpaceBridged(i + 1, i + 2, ct) &&
                (HasFiniteReading(slots[i + 2]) ||
                 BulgarianFunctionWords.HasAny(slots[i + 2].Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge) ||
                 slots[i + 2].Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) ||
                 slots[i + 2].Word.CanonicalText is "ще" or "би" or "бих" or "бихме" or "бихте" or "биха" ||
                 IsActiveLexicalParticiple(slots[i + 2]));

            bool nominativeAfterPrep = c2 != "то" &&
                BulgarianFunctionWords.TryGetPersonalSubjectPronoun(c2, out _) &&
                !(prep == "на" && c2 is "ти" or "вие") &&
                !(c2 is "те" or "ти" && i + 2 < slots.Length &&
                  map.AreSpaceBridged(i + 1, i + 2, ct) &&
                  (HasFiniteReading(slots[i + 2]) ||
                   BulgarianFunctionWords.HasAny(slots[i + 2].Word.CanonicalText, FunctionWordClasses.PreVerbalClauseBridge)));
            bool cliticAfterPrep = ((c2 != "те" && BulgarianFunctionWords.HasAny(c2, FunctionWordClasses.PureAccusativeClitic)) ||
                    c2 is "му" or "им" or "ѝ") &&
                prep != "по" &&
                !(prep == "до" && c2 == "я" && i + 2 < slots.Length && slots[i + 2].Word.CanonicalText is "и" or "или") &&
                !precedesVerbGroup;
            bool extraCliticAfterStrictPrep = c2 is "ми" or "ни" or "ви" or "то" &&
                prep != "по" &&
                !(c2 == "ми" && (prep is "в" or "във" ||
                    i + 2 < slots.Length && slots[i + 2].Word.CanonicalText is "минор" or "мажор" or "бемол" or "диез")) &&
                !(c2 == "ни" && i + 2 < slots.Length && map.AreSpaceBridged(i + 1, i + 2, ct) &&
                  (slots[i + 2].Word.CanonicalText is "един" or "една" or "едно" or "едни" or "що" ||
                   slots[i + 2].Word.CanonicalText.StartsWith("единствен", StringComparison.Ordinal))) &&
                !(c2 == "то" && prep is "на" or "за" or "в" or "във" &&
                  !(i + 2 < slots.Length && map.AreSpaceBridged(i + 1, i + 2, ct) && IsCoordinatingConjunction(slots[i + 2]))) &&
                !precedesVerbGroup &&
                (i + 2 >= slots.Length || !map.AreSpaceBridged(i + 1, i + 2, ct) ||
                 IsCoordinatingConjunction(slots[i + 2]) || IsClauseIntroducer(slots[i + 2].Word, slots[i + 2].Active) ||
                 IsActivePreposition(slots[i + 2].Word, slots[i + 2].Active) ||
                 c2 != "то" && !HasPureNominal(slots[i + 2].Word, slots[i + 2].Active));
            if (!nominativeAfterPrep && !cliticAfterPrep && !extraCliticAfterStrictPrep)
                continue;

            int[] prepReadings = slot.Active
                .Where(r => slot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition)
                .ToArray();
            int[] pronReadings = Enumerable.Range(0, next.Word.Readings.Count)
                .Where(r => !Guard.IsSourcePlaceholder(next.Word, next.Word.Readings[r]))
                .ToArray();
            if (prepReadings.Length == 0 || pronReadings.Length == 0) continue;

            string evidenceCode = "pronoun_case_token=" + next.Word.OriginalIndex.ToString(CultureInfo.InvariantCulture);
            List<ContextOverlayRoleSelection> roles =
            [
                new(ContextOverlayRole.Adposition, slot.Word.OriginalIndex, slot.Word.Token.Start, slot.Word.Token.Length,
                    prepReadings, ["clause_fusion_preposition"]),
                new(ContextOverlayRole.NominalContext, next.Word.OriginalIndex, next.Word.Token.Start, next.Word.Token.Length,
                    pronReadings, ["clause_fusion_governed_pronoun"])
            ];
            agreement.Add(new(next.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                canonicalKey(roles), roles, [evidenceCode]));
        }
    }

    private static void EmitPronounCaseMismatch(
        TokenView pronWord,
        TokenView partnerWord,
        ContextOverlayRole partnerRole,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement)
    {
        int[] pronReadings = Enumerable.Range(0, pronWord.Readings.Count)
            .Where(r => !Guard.IsSourcePlaceholder(pronWord, pronWord.Readings[r]))
            .ToArray();
        int[] partnerReadings = Enumerable.Range(0, partnerWord.Readings.Count)
            .Where(r => !Guard.IsSourcePlaceholder(partnerWord, partnerWord.Readings[r]))
            .ToArray();
        if (pronReadings.Length == 0 || partnerReadings.Length == 0) return;

        string evidenceCode = "pronoun_case_token=" + pronWord.OriginalIndex.ToString(CultureInfo.InvariantCulture);
        List<ContextOverlayRoleSelection> roles =
        [
            new(partnerRole, partnerWord.OriginalIndex, partnerWord.Token.Start, partnerWord.Token.Length,
                partnerReadings, ["clause_fusion_case_partner"]),
            new(ContextOverlayRole.NominalContext, pronWord.OriginalIndex, pronWord.Token.Start, pronWord.Token.Length,
                pronReadings, ["clause_fusion_governed_pronoun"])
        ];
        agreement.Add(new(pronWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, [evidenceCode]));
    }

    private static void ScanVocativeIntrusions(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        TextAnalysis analysis = map.Analysis;
        SentenceSpan sentence = map.Sentence;
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;

        for (int i = 0; i < slots.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[i] || slots[i].Quoted || !TryGetPureCommonVocativeReadings(slots, i, out int[] vocReadings))
                continue;

            TokenView vocWord = slots[i].Word;

            // 1. Preceded across whitespace by an incompatible modifier/determiner/numeral or governed preposition.
            if (i > 0 && !peripheral[i - 1] && !slots[i - 1].Quoted && map.AreSpaceBridged(i - 1, i, ct))
            {
                WordSlot prev = slots[i - 1];
                if (IsExactWord(prev) && prev.Word.CanonicalText != "като" && IsActivePreposition(prev.Word, prev.Active))
                {
                    int[] prepRows = prev.Active.Where(r => prev.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition).ToArray();
                    if (prepRows.Length > 0)
                    {
                        EmitVocativeAttributeMismatch(vocWord, vocReadings, prev.Word, prepRows, canonicalKey, agreement);
                        continue;
                    }
                }

                // Walk left across contiguous NP modifiers (including comparative degree modifiers like "по-голям").
                int firstMod = i;
                for (int m = i - 1; m >= 0; m--)
                {
                    if (peripheral[m] || slots[m].Quoted || !map.AreSpaceBridged(m, m + 1, ct))
                        break;
                    WordSlot ms = slots[m];
                    if (ms.ComparativeGraphModifier)
                    {
                        firstMod = m;
                        continue;
                    }
                    if (!IsExactWord(ms)) break;
                    bool isModOrNum = IsNominalModifierOrDeterminer(ms) || IsNumeralWord(ms) ||
                        ms.Word.CanonicalText is "няколко" or "много" or "хиляди" or "всички";
                    if (!isModOrNum) break;
                    firstMod = m;
                }

                // Governed preposition before the modifier chain ("Отивам при уважаеми директоре.").
                if (firstMod < i && firstMod > 0 && !peripheral[firstMod - 1] && !slots[firstMod - 1].Quoted &&
                    IsExactWord(slots[firstMod - 1]) && map.AreSpaceBridged(firstMod - 1, firstMod, ct))
                {
                    WordSlot prepSlot = slots[firstMod - 1];
                    if (prepSlot.Word.CanonicalText != "като" && IsActivePreposition(prepSlot.Word, prepSlot.Active))
                    {
                        int[] prepRows = prepSlot.Active.Where(r => prepSlot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition).ToArray();
                        if (prepRows.Length > 0)
                        {
                            EmitVocativeAttributeMismatch(vocWord, vocReadings, prepSlot.Word, prepRows, canonicalKey, agreement);
                            continue;
                        }
                    }
                }

                bool mascVocative = vocReadings.Any(r => vocWord.Readings[r].Features.Gender == MorphologicalGender.Masculine);
                bool addressRightDelimited = i == slots.Length - 1 || !map.AreSpaceBridged(i, i + 1, ct) ||
                    slots[i + 1].Word.CanonicalText == "и" && i + 2 < slots.Length && TryGetPureCommonVocativeReadings(slots, i + 2, out _);
                bool addressLeftDelimited = firstMod == 0 || !map.AreSpaceBridged(firstMod - 1, firstMod, ct) ||
                    slots[firstMod - 1].Word.CanonicalText is "о" or "ей" or "хей" or "абе" or "бе" or "и";
                bool addressDelimited = addressRightDelimited && addressLeftDelimited;

                int incompatibleMod = -1;
                for (int m = i - 1; m >= firstMod; m--)
                {
                    WordSlot ms = slots[m];
                    if (ms.ComparativeGraphModifier || !IsExactWord(ms)) continue;
                    string mc = ms.Word.CanonicalText;
                    bool articledMod = ms.Active.Length > 0 && ms.Active.All(r =>
                        IsArticledModifierFeature(ms.Word.Readings[r].Features));
                    bool isVocativeMascAdj = mascVocative && addressDelimited && mc.EndsWith('и') &&
                        mc is not ("мои" or "твои" or "свои" or "наши" or "ваши" or "техни" or "чии" or "нечии" or "ничии" or
                            "някои" or "никои" or "други" or "всички" or "тези" or "онези" or "такива" or "няколко" or "хиляди") &&
                        !IsNumeralWord(ms) && ms.Active.Length > 0 &&
                        ms.Active.Any(r => ms.Word.Readings[r].Features is
                            { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Article: MorphologicalArticle.Indefinite } &&
                            !Guard.TryGetDeterminerAxes(ms.Word, ms.Word.Readings[r], out _));
                    bool pluralOrQuantMod = !isVocativeMascAdj &&
                        (mc is "няколко" or "хиляди" or "всички" or "всеки" or "всяка" or "всяко" or
                            "този" or "тази" or "това" or "тези" or "онзи" or "онази" or "онези" ||
                         IsNumeralWord(ms) && mc is not ("един" or "една" or "едно") ||
                         ms.Active.Length > 0 && ms.Active.All(r =>
                             ms.Word.Readings[r].Features.Number is MorphologicalNumber.Plural or MorphologicalNumber.Count ||
                             Guard.TryGetDeterminerAxes(ms.Word, ms.Word.Readings[r], out var ax) && ax.Number == MorphologicalNumber.Plural));
                    if (articledMod || pluralOrQuantMod)
                        incompatibleMod = m;
                }

                // Also flag predicative vocative after linking verb ("стана известен лекарю").
                if (incompatibleMod < 0 && firstMod < i && firstMod > 0 && IsExactWord(slots[firstMod]) &&
                    IsExactWord(slots[firstMod - 1]) && map.AreSpaceBridged(firstMod - 1, firstMod, ct) &&
                    FiniteReadings(slots[firstMod - 1]).Any(r => IsLinkingOrSemiCopularLemma(slots[firstMod - 1].Word.Readings[r].Lemma)))
                {
                    incompatibleMod = firstMod;
                }

                if (incompatibleMod >= 0 && IsExactWord(slots[incompatibleMod]) && slots[incompatibleMod].Active.Length > 0)
                {
                    EmitVocativeAttributeMismatch(vocWord, vocReadings, slots[incompatibleMod].Word,
                        slots[incompatibleMod].Active, canonicalKey, agreement);
                    continue;
                }
            }

            // 2. Uncommaed sentence-initial vocative acting as subject before preverbal clitics or non-imperative 3rd-person verb.
            if (i == 0 && slots.Length >= 2 && LeadingDialogueOrSpace(analysis.Text, sentence.Start, vocWord.Token.Start) &&
                !slots.Any(s => s.Word.CanonicalText == "ли"))
            {
                int scan = 1;
                // Skip optional PP modifier ("Авторе на новата статия отговори...") or comma-enclosed relative clause ("Човеко, който дойде вчера, донесе...").
                if (scan < slots.Length && IsExactWord(slots[scan]) && map.AreSpaceBridged(0, scan, ct) &&
                    IsActivePreposition(slots[scan].Word, slots[scan].Active))
                {
                    scan++;
                    while (scan < slots.Length && IsExactWord(slots[scan]) &&
                           map.AreSpaceBridged(scan - 1, scan, ct) &&
                           (IsNominalModifierOrDeterminer(slots[scan]) || HasNominalHeadReading(slots[scan])))
                    {
                        bool stopAtNoun = HasPureNominal(slots[scan].Word, slots[scan].Active) &&
                            !IsNominalModifierOrDeterminer(slots[scan]);
                        scan++;
                        if (stopAtNoun) break;
                    }
                }
                else if (scan + 2 < slots.Length && map.HasCommaBetween(0, scan) &&
                         IsRelativeWord(slots[scan].Word.CanonicalText))
                {
                    for (int k = scan + 1; k < slots.Length - 1; k++)
                        if (map.HasCommaBetween(k, k + 1))
                        { scan = k + 1; break; }
                }

                if (scan < slots.Length && (scan > 1 || !map.HasCommaBetween(0, scan)))
                {
                    bool sawPronClitic = false, sawNeg = false;
                    int verbIdx = -1;
                    for (int k = scan; k < slots.Length && k <= scan + 3; k++)
                    {
                        if (peripheral[k] || slots[k].Quoted || !IsExactWord(slots[k]) ||
                            k > scan && !map.AreSpaceBridged(k - 1, k, ct))
                            break;
                        string ck = slots[k].Word.CanonicalText;
                        if (ck == "не") { sawNeg = true; continue; }
                        if (BulgarianFunctionWords.HasAny(ck, FunctionWordClasses.ReflexiveAccusativeSe | FunctionWordClasses.ReflexiveDativeSi))
                            continue;
                        if (ck != "те" && BulgarianFunctionWords.HasAny(ck, FunctionWordClasses.ShortPronominalClitic))
                        {
                            sawPronClitic = true;
                            continue;
                        }
                        if (HasFiniteReading(slots[k]))
                            verbIdx = k;
                        break;
                    }

                    if (verbIdx >= 0)
                    {
                        WordSlot vSlot = slots[verbIdx];
                        int[] fin = AllFinitePersonalReadings(vSlot.Word, ct);
                        bool hasImp = vSlot.Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative);
                        bool has2ndPres = fin.Any(r => vSlot.Word.Readings[r].Features is
                            { Person: MorphologicalPerson.Second, Tense: MorphologicalTense.Present });
                        bool declarative3rd = !analysis.Text.AsSpan(sentence.Start, sentence.Length).TrimEnd().EndsWith("!", StringComparison.Ordinal) &&
                            fin.Any(r => vSlot.Word.Readings[r].Features.Person == MorphologicalPerson.Third);
                        if (fin.Length > 0 && !has2ndPres &&
                            (sawPronClitic || sawNeg && !hasImp || !hasImp || scan > 1 || declarative3rd))
                        {
                            List<ContextOverlayRoleSelection> roles =
                            [
                                new(ContextOverlayRole.Predicate, vSlot.Word.OriginalIndex, vSlot.Word.Token.Start, vSlot.Word.Token.Length,
                                    fin, ["clause_fusion_finite_readings"]),
                                new(ContextOverlayRole.Subject, vocWord.OriginalIndex, vocWord.Token.Start, vocWord.Token.Length,
                                    vocReadings, ["vocative_subject_intrusion"])
                            ];
                            agreement.Add(new(vSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                                canonicalKey(roles), roles, ["vocative_subject_intrusion", ContextLanes.ProductiveGraphMismatch]));
                        }
                    }
                }
            }
        }
    }

    private static readonly HashSet<string> DegreeOrLexicalizedPrepAdverbs = new(StringComparer.Ordinal)
    {
        "много", "малко", "немалко", "повече", "толкова", "колкото", "няколко", "доста", "твърде",
        "съвсем", "особено", "изключително", "крайно", "напълно", "почти", "приблизително", "около",
        "само", "единствено", "точно", "именно", "предимно", "главно", "основно", "чисто", "просто",
        "чак", "още", "вече", "все", "сравнително", "относително", "достатъчно", "прекалено",
        "недостатъчно", "еднакво", "равно", "общо", "отдавна", "скоро", "рядко", "често", "бързо",
        "късно", "рано", "близо", "кратко", "дълго", "живо", "цяло", "открито", "закрито",
        "тъмно", "светло", "топло", "студено", "сухо", "високо", "ниско", "ляво", "дясно",
        "ново", "старо", "сигурно"
    };

    private static readonly HashSet<string> IntransitiveExtrapositionLemmas = new(StringComparer.Ordinal)
    {
        "вляза", "влизам", "изляза", "излизам", "дойда", "идвам", "пристигна", "пристигам",
        "остана", "оставам", "седна", "сядам", "застана", "заставам", "тръгна", "тръгвам",
        "падна", "падам", "върна", "връщам", "лежа", "стоя", "седя", "вися",
        "живея", "работя", "чакам", "спя", "раста"
    };

    private static void ScanNominalChunkAgreementMismatches(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (!map.HasTerminalPunctuation || !map.AllExactOrDegree)
            return;

        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;

        for (int i = 0; i + 1 < slots.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[i] || slots[i].Quoted) continue;
            WordSlot modSlot = slots[i];
            if (modSlot.Word.Token.Text.Length == 0 || i > 0 && char.IsUpper(modSlot.Word.Token.Text[0]))
                continue;

            bool afterPrep = i > 0 && map.AreSpaceBridged(i - 1, i, ct) &&
                IsActivePreposition(slots[i - 1].Word, slots[i - 1].Active) &&
                slots[i - 1].Word.CanonicalText != "като";

            int headIdx = i + 1;
            bool bridgedAcrossPp = false;
            bool bridgedAcrossQuantifier = false;
            if (headIdx + 1 < slots.Length &&
                BulgarianFunctionWords.HasAny(slots[headIdx].Word.CanonicalText, FunctionWordClasses.PossessiveOrDativeClitic) &&
                map.AreSpaceBridged(i, headIdx, ct) &&
                map.AreSpaceBridged(headIdx, headIdx + 1, ct) &&
                modSlot.Active.Length > 0 && modSlot.Active.All(r =>
                    IsArticledModifierFeature(modSlot.Word.Readings[r].Features)))
            {
                headIdx = i + 2;
            }
            else if (headIdx + 1 < slots.Length &&
                     slots[headIdx].ComparativeGraphModifier &&
                     map.AreSpaceBridged(i, headIdx, ct) &&
                     map.AreSpaceBridged(headIdx, headIdx + 1, ct))
            {
                int scanDeg = headIdx + 1;
                while (scanDeg + 1 < slots.Length &&
                       !peripheral[scanDeg] && !slots[scanDeg].Quoted &&
                       map.AreSpaceBridged(scanDeg, scanDeg + 1, ct) &&
                       (slots[scanDeg].ComparativeGraphModifier ||
                        IsNominalModifierOrDeterminer(slots[scanDeg]) && !HasPureNominal(slots[scanDeg].Word, slots[scanDeg].Active)))
                {
                    scanDeg++;
                }
                headIdx = scanDeg;
            }
            else if (TryBridgeSingularModifierAcrossPluralQuantifier(map, slots, peripheral, i, ct, out int qHeadIdx))
            {
                headIdx = qHeadIdx;
                bridgedAcrossQuantifier = true;
            }
            else if (TryBridgeParticipialPrepositionalComplement(map, slots, peripheral, i, afterPrep, ct, out int ppHeadIdx))
            {
                headIdx = ppHeadIdx;
                bridgedAcrossPp = true;
            }
            else if (!map.AreSpaceBridged(i, headIdx, ct))
            {
                continue;
            }

            if (peripheral[headIdx] || slots[headIdx].Quoted || !IsExactWord(slots[headIdx]) ||
                slots[headIdx].Word.Token.Text.Length == 0 || char.IsUpper(slots[headIdx].Word.Token.Text[0]))
                continue;

            // Guard B1-C: Skip coordinated heads ("Малките книга и тетрадка.", "Моите брат и сестра...") and coordinated modifiers.
            if (headIdx + 1 < slots.Length &&
                (map.HasCommaBetween(headIdx, headIdx + 1) ||
                 slots[headIdx + 1].Word.CanonicalText is "и" or "или"))
                continue;
            if (i >= 2 && (map.HasCommaBetween(i - 1, i) ||
                           slots[i - 1].Word.CanonicalText is "и" or "или"))
                continue;

            // Skip participial/adjectival internal PPs ("по подгизналата [в червено] стръмнина").
            if (afterPrep && i >= 2 && map.AreSpaceBridged(i - 2, i - 1, ct) &&
                slots[i - 2].Active.Any(r => slots[i - 2].Word.Readings[r].Features is
                    { PartOfSpeech: MorphologicalPartOfSpeech.Adjective } or
                    { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle }))
                continue;

            // Substantival demonstrative PP ("а след това работи като общински лекар").
            if (afterPrep && modSlot.Word.CanonicalText is "това" or "онова")
                continue;

            bool afterCardNum = i > 0 && map.AreSpaceBridged(i - 1, i, ct) &&
                slots[i - 1].Word.CanonicalText is "два" or "три" or "четири" or "пет" or "шест" or
                    "двама" or "трима" or "четирима" or "петима" or "шестима";
            bool afterTransitiveVerb = i > 0 && map.AreSpaceBridged(i - 1, i, ct) &&
                FiniteReadings(slots[i - 1]).Length > 0 &&
                FiniteReadings(slots[i - 1]).All(r =>
                    slots[i - 1].Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                    !TextPredicateLinkAnalyzer.CopularLemma(slots[i - 1].Word.Readings[r].Lemma) &&
                    !IsLinkingOrSemiCopularLemma(slots[i - 1].Word.Readings[r].Lemma));

            // Post-verbal bare -ски/-жки/-шки adjectives function productively as manner adverbs ("протегна приятелски ръка").
            if (!afterPrep && afterTransitiveVerb &&
                (modSlot.Word.CanonicalText.EndsWith("ски", StringComparison.Ordinal) ||
                 modSlot.Word.CanonicalText.EndsWith("жки", StringComparison.Ordinal) ||
                 modSlot.Word.CanonicalText.EndsWith("шки", StringComparison.Ordinal)))
                continue;

            WordSlot nounSlot = slots[headIdx];
            bool followedByNoun = headIdx + 1 < slots.Length &&
                map.AreSpaceBridged(headIdx, headIdx + 1, ct) &&
                HasNominalHeadReading(slots[headIdx + 1]);

            // Prune relational-adjective 88 homographs on terminal noun heads ("риби" in "големи шарен риби").
            bool nounHadRelationalAdj88 = false;
            int[] nounActive = nounSlot.Active.Where(r =>
            {
                ReadingView nr = nounSlot.Word.Readings[r];
                if (Guard.IsSourcePlaceholder(nounSlot.Word, nr) ||
                    Guard.IsUnwrittenCapitalizedReading(nounSlot.Word, nr) ||
                    Guard.IsSyncreticVocativeCompanion(nounSlot.Word, nr))
                    return false;
                if (!followedByNoun && nr.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, ParadigmId: "88" } &&
                    nounSlot.Active.Any(or => nounSlot.Word.Readings[or].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun))
                {
                    nounHadRelationalAdj88 = true;
                    return false;
                }
                return true;
            }).Order().ToArray();

            if (nounActive.Length == 0 || HasVocativeReading(nounSlot) ||
                !nounActive.All(r => nounSlot.Word.Readings[r].Features is
                    { PartOfSpeech: MorphologicalPartOfSpeech.Noun, NameKind: null, HasConflictingFeatures: false }))
                continue;
            if (bridgedAcrossPp && (followedByNoun || IsAdverbialTimeOrMeasureHead(nounSlot)))
                continue;

            if (modSlot.Word.CanonicalText == "същи" &&
                nounActive.Any(r => nounSlot.Word.Readings[r].Features is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular }))
                continue;

            // Case B2: Qualitative -о adverb/neuter-adj between [Prep | CardinalNumeral] and bare non-neuter noun ("в тихо стая", "два широко стола").
            if ((afterPrep || afterCardNum) && headIdx == i + 1 && IsExactWord(modSlot) &&
                modSlot.Word.CanonicalText.EndsWith('о') &&
                !DegreeOrLexicalizedPrepAdverbs.Contains(modSlot.Word.CanonicalText))
            {
                int[] rawMod = NonPlaceholderReadings(modSlot.Word, i == 0);
                bool hasNeutAdj = rawMod.Any(r => modSlot.Word.Readings[r].Features is
                    { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite });
                bool onlyAdjOrAdv = rawMod.Length > 0 && rawMod.All(r =>
                    modSlot.Word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Adverb &&
                    !Guard.IsMeasuredPassiveAttribute(modSlot.Word.Readings[r].Features));
                bool bareNonNeuterNoun = nounActive.All(r => nounSlot.Word.Readings[r].Features.Article == MorphologicalArticle.Indefinite &&
                    !(nounSlot.Word.Readings[r].Features.Gender == MorphologicalGender.Neuter &&
                      nounSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Singular));
                bool nounStartsSubjectBeforeVerb = afterPrep && headIdx + 1 < slots.Length &&
                    map.AreSpaceBridged(headIdx, headIdx + 1, ct) &&
                    (HasFiniteReading(slots[headIdx + 1]) ||
                     slots[headIdx + 1].Word.CanonicalText is "не" or "ще" or "се" or "е" or "са" or "беше" or "бяха");
                if (hasNeutAdj && onlyAdjOrAdv && bareNonNeuterNoun && !nounStartsSubjectBeforeVerb)
                {
                    int[] adjRows = rawMod.Where(r => modSlot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective).Order().ToArray();
                    EmitNominalChunkAttributeMismatch(modSlot.Word, adjRows, nounSlot.Word, nounActive, canonicalKey, agreement);
                    continue;
                }
            }

            bool allowCountOption = afterCardNum || bridgedAcrossQuantifier ||
                i > 0 && map.AreSpaceBridged(i - 1, i, ct) &&
                slots[i - 1].Word.CanonicalText is "няколко" or "колко" or "толкова";
            bool modStrictlyArticled = !Guard.DeterminerAllowsArticledHead(modSlot.Word) &&
                modSlot.Active.Length > 0 &&
                modSlot.Active.All(r => IsArticledModifierFeature(modSlot.Word.Readings[r].Features) ||
                    Guard.TryGetDeterminerAxes(modSlot.Word, modSlot.Word.Readings[r], out _) &&
                    Guard.GetDeterminerArticle(modSlot.Word.Readings[r].Features) is
                        MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short);
            bool nounHadPrunedArticleOrCount = false;
            if (modStrictlyArticled && !allowCountOption &&
                nounActive.Any(r => nounSlot.Word.Readings[r].Features is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                        Article: MorphologicalArticle.Indefinite,
                        Number: MorphologicalNumber.Singular or MorphologicalNumber.Plural
                    }))
            {
                int[] prunedIndef = nounActive.Where(r => nounSlot.Word.Readings[r].Features is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                        Article: MorphologicalArticle.Indefinite,
                        Number: MorphologicalNumber.Singular or MorphologicalNumber.Plural
                    }).ToArray();
                if (prunedIndef.Length < nounActive.Length)
                {
                    nounActive = prunedIndef;
                    nounHadPrunedArticleOrCount = true;
                }
            }

            bool anyNounDefinite = nounActive.Any(r =>
                nounSlot.Word.Readings[r].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short);
            bool countHomographUnderQuantifier = modSlot.Word.CanonicalText is "всеки" or "всяка" or "всяко" or "всички" &&
                nounActive.Any(r => nounSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Count) &&
                !nounActive.Any(r => nounSlot.Word.Readings[r].Features.Article == MorphologicalArticle.Full) &&
                !(afterTransitiveVerb && modSlot.Word.CanonicalText == "всеки");
            if (anyNounDefinite && !countHomographUnderQuantifier)
                continue;
            if (afterPrep && headIdx == i + 1 && !nounHadPrunedArticleOrCount &&
                modSlot.Active.Any(r => IsArticledModifierFeature(modSlot.Word.Readings[r].Features)) &&
                nounActive.Any(r => nounSlot.Word.Readings[r].Features is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular }))
                continue;

            if (modSlot.ComparativeGraphModifier || !IsExactWord(modSlot))
                continue;

            int[] rawModRows = NonPlaceholderReadings(modSlot.Word, i == 0);
            if (rawModRows.Length == 0) continue;

            // Defer strictly to LocalAttributeAgreementDiagnostics / ExactAgreementLane whenever both tokens
            // have no unpruned verb/noun/adverb/relational-88 homographs, preserving RuleId & ProjectedText repair.
            int prepCur = i;
            while (prepCur > 0 && map.AreSpaceBridged(prepCur - 1, prepCur, ct) &&
                   !Guard.IsExactAdposition(slots[prepCur - 1].Word) &&
                   IsNominalModifierOrDeterminer(slots[prepCur - 1]))
            {
                prepCur--;
            }
            bool exactPrepBeforePair = headIdx == i + 1 && prepCur > 0 &&
                map.AreSpaceBridged(prepCur - 1, prepCur, ct) &&
                Guard.IsExactAdposition(slots[prepCur - 1].Word);
            bool unambiguousVerbAfterHead = false;
            if (headIdx == i + 1 && headIdx + 1 < slots.Length &&
                map.AreSpaceBridged(headIdx, headIdx + 1, ct) &&
                IsExactWord(slots[headIdx + 1]))
            {
                int[] nextVerbRows = NonPlaceholderReadings(slots[headIdx + 1].Word, sentenceInitial: false);
                unambiguousVerbAfterHead = nextVerbRows.Length > 0 && nextVerbRows.All(r =>
                    slots[headIdx + 1].Word.Readings[r].Category == Category.Verb &&
                    slots[headIdx + 1].Word.Readings[r].Features.VerbForm == MorphologicalVerbForm.Finite &&
                    Guard.IsPersonalPredicate(slots[headIdx + 1].Word.Readings[r].Features));
            }
            bool localPrunesNounVerb = (exactPrepBeforePair || unambiguousVerbAfterHead) &&
                nounSlot.Word.Readings.Any(r =>
                    r.Category == Category.Noun &&
                    r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                    Guard.HasNominalAxes(r.Features) &&
                    !Guard.IsSyncreticVocativeCompanion(nounSlot.Word, r)) &&
                nounSlot.Word.Readings
                    .Where(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb)
                    .All(r => r.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative);
            bool localPrunesModVerb = exactPrepBeforePair &&
                rawModRows.Any(r =>
                {
                    ReadingView mr = modSlot.Word.Readings[r];
                    return mr.Category == Category.Adjective && mr.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
                           Guard.HasNominalAxes(mr.Features) ||
                           Guard.IsMeasuredPassiveAttribute(mr.Features) ||
                           Guard.IsMeasuredPresentActiveAttribute(mr.Features) ||
                           Guard.TryGetOrdinalAxes(mr, out _) ||
                           Guard.TryGetDeterminerAxes(modSlot.Word, mr, out _);
                });
            bool nounHadPrunedVerb = !localPrunesNounVerb &&
                nounSlot.Word.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb);
            bool modHasNonPassiveVerbOrNounHomograph = rawModRows.Any(r =>
            {
                ReadingView mr = modSlot.Word.Readings[r];
                MorphologicalFeatures mf = mr.Features;
                if (Guard.TryGetOrdinalAxes(mr, out _)) return false;
                if (localPrunesModVerb && mr.Category == Category.Verb &&
                    mf.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative)
                    return false;
                return mf.PartOfSpeech == MorphologicalPartOfSpeech.Noun ||
                       mf.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
                       !Guard.IsMeasuredPassiveAttribute(mf) &&
                       !Guard.IsMeasuredPresentActiveAttribute(mf);
            });
            bool isQuantifierWithCountNoun = modSlot.Word.CanonicalText is "всеки" or "всяка" or "всяко" or "всички" &&
                nounActive.Any(r => nounSlot.Word.Readings[r].Features.Number == MorphologicalNumber.Count);
            if ((headIdx == i + 1 || headIdx == i + 2 && Guard.IsPossessiveBridgeClitic(slots[i + 1].Word.CanonicalText)) &&
                !anyNounDefinite && !nounHadPrunedVerb && !nounHadRelationalAdj88 && !nounHadPrunedArticleOrCount &&
                !modHasNonPassiveVerbOrNounHomograph && !isQuantifierWithCountNoun)
                continue;

            bool prevIsIndefAdj = false;
            if (i > 0 && map.AreSpaceBridged(i - 1, i, ct))
            {
                int[] prevRows = NonPlaceholderReadings(slots[i - 1].Word, i - 1 == 0);
                if (i >= 2 && map.AreSpaceBridged(i - 2, i - 1, ct) && HasFiniteReading(slots[i - 2]) &&
                    prevRows.Any(r => slots[i - 1].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective))
                {
                    prevRows = prevRows.Where(r => slots[i - 1].Word.Readings[r].Features.PartOfSpeech != MorphologicalPartOfSpeech.Verb).ToArray();
                }
                prevIsIndefAdj = prevRows.Length > 0 && prevRows.All(r =>
                    slots[i - 1].Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Article: MorphologicalArticle.Indefinite });
            }

            List<(MorphologicalNumber? Num, MorphologicalGender? Gen)> modTuples = [];
            List<int> modSupportRows = [];
            bool validModSlot = true;

            foreach (int r in rawModRows)
            {
                ReadingView row = modSlot.Word.Readings[r];
                MorphologicalFeatures f = row.Features;
                if (Guard.TryGetDeterminerAxes(modSlot.Word, row, out var detAxes))
                {
                    modTuples.Add((detAxes.Number, detAxes.Gender));
                    modSupportRows.Add(r);
                }
                else if (Guard.TryGetOrdinalAxes(row, out var ordAxes))
                {
                    modTuples.Add((ordAxes.Number, ordAxes.Gender));
                    modSupportRows.Add(r);
                }
                else if (f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(f))
                {
                    modTuples.Add((f.Number, f.Gender));
                    modSupportRows.Add(r);
                }
                else if (f.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
                         f.VerbForm == MorphologicalVerbForm.Participle &&
                         (f.ParticipleKind == MorphologicalParticipleKind.PastPassive &&
                          f.Number is not null &&
                          (f.Article is not (null or MorphologicalArticle.Indefinite) || bridgedAcrossPp || afterPrep || afterTransitiveVerb || prevIsIndefAdj) ||
                          Guard.IsMeasuredPresentActiveAttribute(f) &&
                          (f.Article is not (null or MorphologicalArticle.Indefinite) || bridgedAcrossPp || afterPrep || afterTransitiveVerb || prevIsIndefAdj)))
                {
                    modTuples.Add((f.Number, f.Gender));
                    modSupportRows.Add(r);
                }
                else if (afterTransitiveVerb && f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                         (f.Gender == MorphologicalGender.Masculine && f.Number == MorphologicalNumber.Singular &&
                          f.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite ||
                          f.Number == MorphologicalNumber.Count))
                {
                    if (!rawModRows.Any(or => modSlot.Word.Readings[or].Features is
                        { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Gender: MorphologicalGender.Feminine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }) ||
                        !nounActive.All(nr => nounSlot.Word.Readings[nr].Features is
                        { Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }))
                    {
                        validModSlot = false;
                        break;
                    }
                }
                else if (!modSlot.Active.Contains(r))
                {
                    continue;
                }
                else
                {
                    validModSlot = false;
                    break;
                }
            }

            if (!validModSlot || modTuples.Count == 0 || modSupportRows.Count == 0)
                continue;

            bool allowCountAsPlural = allowCountOption;

            bool anyAgrees = modTuples.Any(mt => nounActive.Any(nr =>
            {
                ReadingView nRow = nounSlot.Word.Readings[nr];
                MorphologicalFeatures nf = nRow.Features;
                MorphologicalNumber? nn = nounSlot.Word.CanonicalText is "хора" or "хората"
                    ? MorphologicalNumber.Plural
                    : nf.Number == MorphologicalNumber.Count && allowCountAsPlural
                        ? MorphologicalNumber.Plural
                        : nf.Number;
                if (mt.Num is not null && nn is not null && mt.Num != nn) return false;
                if (mt.Num == MorphologicalNumber.Singular && mt.Gen is not null && nf.Gender is not null && mt.Gen != nf.Gender)
                {
                    return nf.Gender == MorphologicalGender.Masculine &&
                           mt.Gen == MorphologicalGender.Feminine &&
                           Guard.IsEpiceneHumanNounLemma(nRow.Lemma);
                }
                return true;
            }));

            if (!anyAgrees && followedByNoun &&
                nounActive.All(nr => nounSlot.Word.Readings[nr].Features is
                    {
                        Article: MorphologicalArticle.Indefinite,
                        Number: MorphologicalNumber.Singular,
                        Gender: MorphologicalGender.Masculine or MorphologicalGender.Neuter
                    }))
            {
                WordSlot outerSlot = slots[headIdx + 1];
                int[] outerNouns = NominalHeadReadings(outerSlot);
                if (outerNouns.Length > 0 &&
                    outerSlot.Active.All(or => outerSlot.Word.Readings[or].Category == Category.Noun) &&
                    modTuples.Any(mt => outerNouns.Any(or =>
                    {
                        MorphologicalFeatures of = outerSlot.Word.Readings[or].Features;
                        if (mt.Num is not null && of.Number is not null && mt.Num != of.Number) return false;
                        if (mt.Num == MorphologicalNumber.Singular && mt.Gen is not null && of.Gender is not null && mt.Gen != of.Gender)
                            return false;
                        return true;
                    })))
                    continue;
            }

            if (!anyAgrees)
            {
                EmitNominalChunkAttributeMismatch(modSlot.Word, modSupportRows.Distinct().Order().ToArray(),
                    nounSlot.Word, nounActive, canonicalKey, agreement);
            }
        }
    }

    private static bool TryBridgeSingularModifierAcrossPluralQuantifier(
        SentenceClauseMap map,
        WordSlot[] slots,
        bool[] peripheral,
        int modIndex,
        CancellationToken ct,
        out int headIdx)
    {
        headIdx = -1;
        if (modIndex + 2 >= slots.Length) return false;
        WordSlot modSlot = slots[modIndex];
        if (peripheral[modIndex] || modSlot.Quoted || !IsExactWord(modSlot) || modSlot.ComparativeGraphModifier)
            return false;

        WordSlot quantSlot = slots[modIndex + 1];
        if (peripheral[modIndex + 1] || quantSlot.Quoted || !map.AreSpaceBridged(modIndex, modIndex + 1, ct) ||
            quantSlot.Word.CanonicalText is not ("няколко" or "колко" or "толкова" or
                "два" or "две" or "три" or "четири" or "пет" or "шест" or
                "двама" or "трима" or "четирима" or "петима" or "шестима"))
            return false;

        int[] rawMod = NonPlaceholderReadings(modSlot.Word, modIndex == 0)
            .Where(r => !Guard.IsSuppressedDeterminerHomograph(modSlot.Word, modSlot.Word.Readings[r]) &&
                        !Guard.IsSuppressedPresentActiveNounHomograph(modSlot.Word, modSlot.Word.Readings[r]))
            .ToArray();
        if (rawMod.Length == 0) return false;

        // Require every reading on modSlot to be a strictly singular adnominal modifier/determiner,
        // excluding lemma "един" and bare neuter -о adjective/adverb homographs ("близо/точно/средно няколко години").
        foreach (int r in rawMod)
        {
            ReadingView row = modSlot.Word.Readings[r];
            MorphologicalFeatures f = row.Features;
            if (Norm(row.Lemma) == "един") return false;
            if (Guard.TryGetDeterminerAxes(modSlot.Word, row, out var det))
            {
                if (det.Number != MorphologicalNumber.Singular) return false;
                continue;
            }
            if (Guard.TryGetOrdinalAxes(row, out var ord))
            {
                if (ord.Number != MorphologicalNumber.Singular) return false;
                continue;
            }
            bool adnominalAdjOrPart =
                f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(f) &&
                !(f.Gender == MorphologicalGender.Neuter && f.Article == MorphologicalArticle.Indefinite) ||
                (Guard.IsMeasuredPassiveAttribute(f) || Guard.IsMeasuredPresentActiveAttribute(f)) &&
                IsArticledModifierFeature(f);
            if (!adnominalAdjOrPart || f.Number != MorphologicalNumber.Singular)
                return false;
        }

        int cur = modIndex + 2;
        while (cur + 1 < slots.Length && !peripheral[cur] && !slots[cur].Quoted &&
               map.AreSpaceBridged(cur - 1, cur, ct) &&
               (slots[cur].ComparativeGraphModifier ||
                IsNominalModifierOrDeterminer(slots[cur]) && !HasPureNominal(slots[cur].Word, slots[cur].Active)))
        {
            cur++;
        }
        if (cur >= slots.Length || peripheral[cur] || slots[cur].Quoted ||
            !map.AreSpaceBridged(cur - 1, cur, ct))
            return false;

        WordSlot cand = slots[cur];
        if (!IsExactWord(cand) || cand.Word.Token.Text.Length == 0 || char.IsUpper(cand.Word.Token.Text[0]))
            return false;
        int[] candRows = NonPlaceholderReadings(cand.Word, sentenceInitial: false)
            .Where(r => !Guard.IsSyncreticVocativeCompanion(cand.Word, cand.Word.Readings[r]))
            .ToArray();
        if (candRows.Length == 0 || !candRows.All(r => cand.Word.Readings[r].Features is
            {
                PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                Article: MorphologicalArticle.Indefinite,
                Number: MorphologicalNumber.Plural or MorphologicalNumber.Count,
                NameKind: null,
                HasConflictingFeatures: false
            }))
            return false;

        headIdx = cur;
        return true;
    }

    private static bool TryBridgeParticipialPrepositionalComplement(
        SentenceClauseMap map,
        WordSlot[] slots,
        bool[] peripheral,
        int modIndex,
        bool afterPrep,
        CancellationToken ct,
        out int headIdx)
    {
        headIdx = -1;
        WordSlot modSlot = slots[modIndex];
        if (!IsExactWord(modSlot) || modSlot.Active.Length == 0 ||
            !modSlot.Active.All(r => IsArticledModifierFeature(modSlot.Word.Readings[r].Features)) ||
            !modSlot.Word.Readings.Any(r => Guard.IsMeasuredPassiveAttribute(r.Features) || Guard.IsMeasuredPresentActiveAttribute(r.Features)) ||
            modSlot.Word.Readings.Any(r => Guard.TryGetDeterminerAxes(modSlot.Word, r, out _) || Guard.TryGetOrdinalAxes(r, out _)))
            return false;
        if (afterPrep && slots.Take(modIndex - 1).Any(HasFiniteReading))
            return false;

        int cur = modIndex + 1;
        int ppCount = 0;
        while (ppCount < 2 && cur + 2 < slots.Length &&
               !peripheral[cur] && !slots[cur].Quoted &&
               map.AreSpaceBridged(cur - 1, cur, ct) &&
               IsActivePreposition(slots[cur].Word, slots[cur].Active) &&
               slots[cur].Word.CanonicalText != "като")
        {
            cur++;
            while (cur < slots.Length && !peripheral[cur] && !slots[cur].Quoted &&
                   map.AreSpaceBridged(cur - 1, cur, ct) &&
                   (slots[cur].ComparativeGraphModifier ||
                    IsNominalModifierOrDeterminer(slots[cur]) && !HasPureNominal(slots[cur].Word, slots[cur].Active) ||
                    IsNumeralWord(slots[cur])))
            {
                cur++;
            }
            if (cur >= slots.Length || peripheral[cur] || slots[cur].Quoted ||
                !map.AreSpaceBridged(cur - 1, cur, ct) ||
                !HasPureNominal(slots[cur].Word, slots[cur].Active))
                return false;
            cur++;
            if (cur < slots.Length && !peripheral[cur] && !slots[cur].Quoted &&
                map.AreSpaceBridged(cur - 1, cur, ct) &&
                IsPossessiveClitic(slots[cur].Word.CanonicalText))
            {
                cur++;
            }
            ppCount++;
        }

        if (ppCount == 0 || cur >= slots.Length || peripheral[cur] || slots[cur].Quoted ||
            !map.AreSpaceBridged(cur - 1, cur, ct))
            return false;

        WordSlot cand = slots[cur];
        if (!IsExactWord(cand) || cand.Word.Token.Text.Length == 0 || char.IsUpper(cand.Word.Token.Text[0]) ||
            cand.Word.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb))
            return false;

        int[] candRows = NonPlaceholderReadings(cand.Word, sentenceInitial: false)
            .Where(r => !Guard.IsSyncreticVocativeCompanion(cand.Word, cand.Word.Readings[r]))
            .ToArray();
        if (candRows.Length == 0 || !candRows.All(r => cand.Word.Readings[r].Features is
            {
                PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                Article: MorphologicalArticle.Indefinite,
                NameKind: null,
                HasConflictingFeatures: false
            }))
            return false;

        headIdx = cur;
        return true;
    }

    private static bool IsArticledModifierFeature(MorphologicalFeatures f) =>
        f.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
        f.RawTag is { } tag &&
        (tag.Contains("Пълен", StringComparison.Ordinal) ||
         tag.Contains("Непълен", StringComparison.Ordinal) ||
         tag.Contains("Членуван", StringComparison.Ordinal));

    private static void EmitNominalChunkAttributeMismatch(
        TokenView modWord,
        int[] modReadings,
        TokenView nounWord,
        int[] nounReadings,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement)
    {
        string attrCode = "attribute_mismatch_token=" + modWord.OriginalIndex.ToString(CultureInfo.InvariantCulture);
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Attribute, modWord.OriginalIndex, modWord.Token.Start, modWord.Token.Length,
                modReadings, ["clause_fusion_nominal_modifier"]),
            new(ContextOverlayRole.NominalContext, nounWord.OriginalIndex, nounWord.Token.Start, nounWord.Token.Length,
                nounReadings, ["clause_fusion_nominal_head"])
        ];
        agreement.Add(new(modWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, [attrCode, ContextLanes.ProductiveAttributeMismatch]));
    }

    private static void ScanRelativePronounAntecedentMismatches(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (!map.HasTerminalPunctuation || map.Slots.Any(s => s.Quoted))
            return;

        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        TextAnalysis analysis = map.Analysis;

        for (int i = 1; i < slots.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[i] || slots[i].Quoted || !IsExactWord(slots[i])) continue;
            string relCanon = slots[i].Word.CanonicalText;
            if (!BulgarianFunctionWords.TryGetRelativeSubjectAxes(relCanon,
                    out MorphologicalNumber reqNum, out MorphologicalGender? reqGen))
                continue;

            int[] relReadings = NonPlaceholderReadings(slots[i].Word, sentenceInitial: false).Order().ToArray();
            if (relReadings.Length == 0) continue;

            // Guard 1: Free/correlative relative determiner ("който щете период", "която и да е", "който период").
            if (i + 1 < slots.Length)
            {
                string next1 = slots[i + 1].Word.CanonicalText;
                string next2 = i + 2 < slots.Length ? slots[i + 2].Word.CanonicalText : string.Empty;
                if (next1 is "щете" or "щеш" or "ще" || next1 == "и" && next2 == "да")
                    continue;
                if (map.AreSpaceBridged(i, i + 1, ct) && IsExactWord(slots[i + 1]))
                {
                    int[] nr = NonPlaceholderReadings(slots[i + 1].Word, sentenceInitial: false);
                    if (nr.Length > 0 && nr.All(r => slots[i + 1].Word.Readings[r].Features is
                        { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Indefinite }) &&
                        nr.Any(r => slots[i + 1].Word.Readings[r].Features.Number == reqNum &&
                            (reqGen is null || slots[i + 1].Word.Readings[r].Features.Gender == reqGen)))
                        continue;
                }
            }

            int relClauseStart = i;
            if (i >= 2 && map.AreSpaceBridged(i - 1, i, ct) &&
                IsActivePreposition(slots[i - 1].Word, slots[i - 1].Active))
            {
                relClauseStart = i - 1;
                if (relClauseStart >= 2 && slots[i - 1].Word.CanonicalText == "на" &&
                    map.AreSpaceBridged(relClauseStart - 1, i - 1, ct) &&
                    HasPureNominal(slots[relClauseStart - 1].Word, slots[relClauseStart - 1].Active))
                {
                    relClauseStart--;
                    while (relClauseStart >= 2 && map.AreSpaceBridged(relClauseStart - 1, relClauseStart, ct) &&
                           IsNominalModifierOrDeterminer(slots[relClauseStart - 1]))
                        relClauseStart--;
                }
            }
            if (relClauseStart < 1 || !DirectlySeparatedBySingleComma(analysis, slots[relClauseStart - 1].Word, slots[relClauseStart].Word))
                continue;

            int antEnd = relClauseStart - 1;
            // Stacked relative clauses ("продукти, които пречат на вноса, които могат...") -> abstain.
            if (slots.Take(relClauseStart).Any(s =>
                    BulgarianFunctionWords.HasAny(s.Word.CanonicalText, FunctionWordClasses.RelativeSubjectPronoun) ||
                    s.Word.CanonicalText == "когото"))
                continue;
            int[]? disambiguatedAntEndNouns = null;
            if (slots[antEnd].Quoted || !IsExactWord(slots[antEnd]) ||
                !HasPureNominal(slots[antEnd].Word, slots[antEnd].Active) &&
                !TryGetSyntacticallyDisambiguatedAntecedentNouns(map, slots, antEnd, ct, out disambiguatedAntEndNouns))
            {
                bool emitted = false;
                if (relCanon is "който" or "която" or "които" && !slots[antEnd].Quoted)
                    emitted = TryEmitGlobalLeftContextRelativeMismatch(map, i, relClauseStart, reqNum, reqGen, relReadings, canonicalKey, agreement, ct);
                if (!emitted && relClauseStart == i && !slots[antEnd].Quoted)
                    TryEmitClauseInternalRelativeSubjectMismatch(map, i, reqNum, reqGen, relReadings, canonicalKey, agreement, ct);
                continue;
            }

            bool hasVerbBeforeComma = false;
            for (int p = 0; p <= antEnd; p++)
            {
                if (slots[p].Quoted) continue;
                string cp = slots[p].Word.CanonicalText;
                if (cp is "е" or "са" or "беше" or "бяха" or "бе" or "съм" or "си" or "сме" or "сте" or
                    "ще" or "трябва" or "може" or "има" or "няма" || HasFiniteReading(slots[p]))
                {
                    hasVerbBeforeComma = true;
                    break;
                }
            }
            if (relCanon == "което" && hasVerbBeforeComma)
                continue;

            List<(int SlotIndex, bool IsQuantified)> candNouns = [];
            bool hasCoord = false;
            bool unsafeChain = false;
            bool sawTopicPrep = false;

            for (int p = antEnd; p >= 0; p--)
            {
                WordSlot cur = slots[p];
                if (cur.Quoted) break;
                if (p < antEnd)
                {
                    ReadOnlySpan<char> gap = analysis.Text.AsSpan(
                        cur.Word.Token.Start + cur.Word.Token.Length,
                        slots[p + 1].Word.Token.Start - (cur.Word.Token.Start + cur.Word.Token.Length));
                    if (gap.IndexOfAny("()[]{}:;-\u2013\u2014") >= 0) { unsafeChain = true; break; }
                    if (gap.Contains(',')) hasCoord = true;
                }
                string cc = cur.Word.CanonicalText;
                if (cc.All(char.IsDigit)) continue;
                if (cc is "и" or "или" or "както" or "а" or "така") { hasCoord = true; continue; }
                if (cc is "за" or "върху" or "относно") sawTopicPrep = true;
                if (cc is "че" or "защото" or "когато" or "ако" or "докато" or "където")
                    break;
                if (!IsExactWord(cur) || p > 0 && cur.Word.Token.Text.Length > 0 && char.IsUpper(cur.Word.Token.Text[0]))
                {
                    unsafeChain = true;
                    break;
                }
                int[] active = p == antEnd && disambiguatedAntEndNouns is { Length: > 0 }
                    ? disambiguatedAntEndNouns
                    : cur.Active.Length > 0 ? cur.Active : NonPlaceholderReadings(cur.Word, p == 0);
                if (active.Length == 0 || active.Any(r => cur.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun ||
                    cur.Word.Readings[r].Features.NameKind is not null))
                {
                    unsafeChain = true;
                    break;
                }

                bool pureFiniteStop = cc is "е" or "са" or "беше" or "бяха" or "бе" or "съм" or "си" or "сме" or "сте" ||
                    active.Any(r => cur.Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative }) &&
                    !active.Any(r => cur.Word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Preposition or MorphologicalPartOfSpeech.Adverb or MorphologicalPartOfSpeech.Numeral or MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun ||
                        cur.Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle });
                if (pureFiniteStop)
                {
                    if (!sawTopicPrep &&
                        active.Where(r => cur.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb)
                              .All(r => IntransitiveExtrapositionLemmas.Contains(Norm(cur.Word.Readings[r].Lemma))))
                    {
                        continue;
                    }
                    break;
                }

                // Skip adnominal determiners ("до моя стол") when immediately followed by a noun.
                if (p < antEnd && map.AreSpaceBridged(p, p + 1, ct) &&
                    active.All(r => Guard.TryGetDeterminerAxes(cur.Word, cur.Word.Readings[r], out _)))
                    continue;

                if (active.Any(r => cur.Word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun))
                {
                    // Skip preverbal PersonalPronoun subjects ("Тя влезе в отряд войници, която...") once a lexical noun candidate was collected.
                    if (candNouns.Count > 0 && active.All(r => cur.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.PersonalPronoun))
                        continue;
                    bool quantBefore = false;
                    for (int q = Math.Max(0, p - 3); q < p; q++)
                    {
                        if (slots[q].Word.Token.Text.All(char.IsDigit) ||
                            slots[q].Word.CanonicalText is "няколко" or "колко" or "толкова" or "много" or "малко" ||
                            slots[q].Active.Any(r => slots[q].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral))
                        {
                            quantBefore = true;
                            break;
                        }
                    }
                    candNouns.Add((p, quantBefore));
                }
            }

            if (unsafeChain || candNouns.Count == 0)
            {
                bool emitted = false;
                if (relCanon is "който" or "която" or "които")
                    emitted = TryEmitGlobalLeftContextRelativeMismatch(map, i, relClauseStart, reqNum, reqGen, relReadings, canonicalKey, agreement, ct);
                if (!emitted && relClauseStart == i)
                    TryEmitClauseInternalRelativeSubjectMismatch(map, i, reqNum, reqGen, relReadings, canonicalKey, agreement, ct);
                continue;
            }
            if (reqNum == MorphologicalNumber.Plural && hasCoord)
                continue;
            if (reqNum == MorphologicalNumber.Plural && candNouns.Count >= 2)
            {
                int[] CandidateRows(int slotIdx) =>
                    slotIdx == antEnd && disambiguatedAntEndNouns is { Length: > 0 }
                        ? disambiguatedAntEndNouns
                        : slots[slotIdx].Active.Length > 0 ? slots[slotIdx].Active : NonPlaceholderReadings(slots[slotIdx].Word, slotIdx == 0);
                var lemmaSets = candNouns.Select(c => CandidateRows(c.SlotIndex)
                    .Where(r => slots[c.SlotIndex].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun)
                    .Select(r => Norm(slots[c.SlotIndex].Word.Readings[r].Lemma)).ToHashSet(StringComparer.Ordinal)).ToArray();
                bool repeatedLemma = false;
                for (int a = 0; a < lemmaSets.Length && !repeatedLemma; a++)
                    for (int b = a + 1; b < lemmaSets.Length; b++)
                        if (lemmaSets[a].Overlaps(lemmaSets[b])) { repeatedLemma = true; break; }
                if (repeatedLemma) continue;
            }

            bool anyMatch = candNouns.Any(cand =>
            {
                WordSlot ns = slots[cand.SlotIndex];
                int[] nr = cand.SlotIndex == antEnd && disambiguatedAntEndNouns is { Length: > 0 }
                    ? disambiguatedAntEndNouns
                    : ns.Active.Length > 0 ? ns.Active : NonPlaceholderReadings(ns.Word, cand.SlotIndex == 0);
                return nr.Any(r =>
                {
                    MorphologicalFeatures nf = ns.Word.Readings[r].Features;
                    if (nf.PartOfSpeech is not (MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun))
                        return false;
                    MorphologicalNumber effNum = ns.Word.CanonicalText is "хора" or "хората" ||
                        nf.Number == MorphologicalNumber.Plural ||
                        nf.Number == MorphologicalNumber.Count && cand.IsQuantified
                        ? MorphologicalNumber.Plural
                        : MorphologicalNumber.Singular;
                    if (reqNum == MorphologicalNumber.Plural)
                        return effNum == MorphologicalNumber.Plural;
                    bool epiceneHumanNoun = nf.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                        Norm(ns.Word.Readings[r].Lemma) is "роднина" or "колега" or "съдия" or "слуга" or "пияница";
                    return effNum == MorphologicalNumber.Singular &&
                        (nf.Gender is null || nf.Gender == reqGen ||
                         epiceneHumanNoun && reqGen is MorphologicalGender.Masculine or MorphologicalGender.Feminine);
                });
            });

            if (!anyMatch)
            {
                WordSlot nearestAnt = slots[candNouns[0].SlotIndex];
                int[] antReadings = (candNouns[0].SlotIndex == antEnd && disambiguatedAntEndNouns is { Length: > 0 }
                    ? disambiguatedAntEndNouns
                    : nearestAnt.Active.Length > 0 ? nearestAnt.Active : NonPlaceholderReadings(nearestAnt.Word, candNouns[0].SlotIndex == 0)).Order().ToArray();
                string evidenceCode = "pronoun_case_token=" + slots[i].Word.OriginalIndex.ToString(CultureInfo.InvariantCulture);
                List<ContextOverlayRoleSelection> roles =
                [
                    new(ContextOverlayRole.Predicate, nearestAnt.Word.OriginalIndex, nearestAnt.Word.Token.Start, nearestAnt.Word.Token.Length,
                        antReadings, ["clause_fusion_relative_antecedent"]),
                    new(ContextOverlayRole.NominalContext, slots[i].Word.OriginalIndex, slots[i].Word.Token.Start, slots[i].Word.Token.Length,
                        relReadings, ["clause_fusion_relative_pronoun"])
                ];
                agreement.Add(new(nearestAnt.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                    canonicalKey(roles), roles, [evidenceCode]));
            }
        }
    }

    private static bool TryEmitGlobalLeftContextRelativeMismatch(
        SentenceClauseMap map,
        int relSlotIdx,
        int relClauseStart,
        MorphologicalNumber reqNum,
        MorphologicalGender? reqGen,
        int[] relReadings,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        TextAnalysis analysis = map.Analysis;
        int antEnd = relClauseStart - 1;
        if (antEnd < 0) return false;

        // [P1-I1e] Scan the full ';'/':'-bounded left prefix up to antEnd; abstain on parentheses/brackets/quotes/em-dashes.
        int leftStart = 0;
        for (int p = antEnd; p > 0; p--)
        {
            ReadOnlySpan<char> gap = analysis.Text.AsSpan(
                slots[p - 1].Word.Token.Start + slots[p - 1].Word.Token.Length,
                slots[p].Word.Token.Start - (slots[p - 1].Word.Token.Start + slots[p - 1].Word.Token.Length));
            if (gap.IndexOfAny("()[]{}\"\u201e\u201c\u00ab\u00bb\u2013\u2014") >= 0)
                return false;
            if (gap.IndexOfAny(":;") >= 0)
            {
                leftStart = p;
                break;
            }
        }

        List<(int SlotIndex, int[] SupportReadings)> nominalCandidates = [];
        bool anyMatches = false;

        for (int p = antEnd; p >= leftStart; p--)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot cur = slots[p];
            if (cur.Quoted || !IsExactWord(cur) || cur.Word.Token.Text.Length == 0 || cur.Word.Token.Text.All(char.IsDigit))
                return false;

            if (p < antEnd && reqNum == MorphologicalNumber.Plural)
            {
                ReadOnlySpan<char> gap = analysis.Text.AsSpan(
                    cur.Word.Token.Start + cur.Word.Token.Length,
                    slots[p + 1].Word.Token.Start - (cur.Word.Token.Start + cur.Word.Token.Length));
                if (gap.Contains(','))
                    return false;
            }

            string cc = cur.Word.CanonicalText;
            // [P1-I1a] License 1p/2p, genderless, indefinite, demonstrative, and universal pronouns.
            if (cc is "аз" or "ти" or "ние" or "вие" or "мен" or "мене" or "теб" or "тебе" or "нас" or "вас" or
                "някой" or "някого" or "някому" or "никой" or "никого" or "всеки" or "всяка" or
                "мнозина" or "малцина" or "тези" or "онези" or "всички" or "всичките" or
                "това" or "този" or "тази" or "онзи" or "онова")
                return false;

            // [P1-I1d] License coordination and numerals/quantifiers before ', които'.
            if (reqNum == MorphologicalNumber.Plural &&
                (cc is "и" or "или" or "няколко" or "много" or "малко" or "редица" or "множество" ||
                 cur.Word.Readings.Any(r => r.Category == Category.Number || r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral)))
                return false;

            bool isCapitalized = char.IsUpper(cur.Word.Token.Text[0]);
            int[] allRows = Enumerable.Range(0, cur.Word.Readings.Count)
                .Where(r => !Guard.IsSourcePlaceholder(cur.Word, cur.Word.Readings[r]) &&
                            !Guard.IsSyncreticVocativeCompanion(cur.Word, cur.Word.Readings[r]) &&
                            (isCapitalized || !Guard.IsUnwrittenCapitalizedReading(cur.Word, cur.Word.Readings[r], p == 0)))
                .Order()
                .ToArray();
            if (allRows.Length == 0)
                return false;

            int[] commonNounRows = allRows
                .Where(r => cur.Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, NameKind: null })
                .ToArray();
            int[] countryRows = allRows
                .Where(r => cur.Word.Readings[r].Features.RawTag?.Contains("Държава:", StringComparison.Ordinal) == true ||
                            cur.Word.Readings[r].Reading?.PartOfSpeech?.Contains("Държава:", StringComparison.Ordinal) == true)
                .ToArray();
            bool hasOtherProperName = commonNounRows.Length == 0 && countryRows.Length == 0 &&
                allRows.Any(r => cur.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun ||
                                 cur.Word.Readings[r].Features.NameKind is not null);
            // [P1-I1b] Abstain on person/city/place/acronym proper names lacking explicit common-noun or country morphology.
            if (hasOtherProperName)
                return false;

            if (commonNounRows.Length > 0)
            {
                if (reqNum == MorphologicalNumber.Plural && commonNounRows.Any(r => Norm(cur.Word.Readings[r].Lemma) is
                    "народ" or "публика" or "младеж" or "полиция" or "семейство" or "екип" or "общество" or
                    "комисия" or "съвет" or "парламент" or "правителство" or "ръководство" or "персонал" or
                    "колектив" or "армия" or "войска" or "група" or "част" or "мнозинство" or "малцинство"))
                    return false;

                bool quantBefore = false;
                for (int q = Math.Max(leftStart, p - 3); q < p; q++)
                {
                    if (slots[q].Word.Token.Text.All(char.IsDigit) ||
                        slots[q].Word.CanonicalText is "няколко" or "колко" or "толкова" or "много" or "малко" ||
                        slots[q].Word.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral))
                    {
                        quantBefore = true;
                        break;
                    }
                }

                nominalCandidates.Add((p, commonNounRows));
                if (commonNounRows.Any(r =>
                {
                    MorphologicalFeatures nf = cur.Word.Readings[r].Features;
                    MorphologicalNumber effNum = cc is "хора" or "хората" ||
                        nf.Number == MorphologicalNumber.Plural ||
                        nf.Number == MorphologicalNumber.Count && quantBefore
                        ? MorphologicalNumber.Plural
                        : MorphologicalNumber.Singular;
                    if (reqNum == MorphologicalNumber.Plural)
                        return effNum == MorphologicalNumber.Plural;
                    bool epicene = Guard.IsEpiceneHumanNounLemma(cur.Word.Readings[r].Lemma);
                    return effNum == MorphologicalNumber.Singular &&
                        (nf.Gender is null || nf.Gender == reqGen ||
                         epicene && reqGen is MorphologicalGender.Masculine or MorphologicalGender.Feminine);
                }))
                {
                    anyMatches = true;
                }
                continue;
            }

            if (countryRows.Length > 0)
            {
                nominalCandidates.Add((p, countryRows));
                (MorphologicalNumber Num, MorphologicalGender? Gen) countryAxes = cc.EndsWith('а') || cc.EndsWith('я')
                    ? (MorphologicalNumber.Singular, MorphologicalGender.Feminine)
                    : cc.EndsWith('о') || cc.EndsWith('е')
                        ? (MorphologicalNumber.Singular, MorphologicalGender.Neuter)
                        : cc.EndsWith('и')
                            ? (MorphologicalNumber.Plural, null)
                            : (MorphologicalNumber.Singular, MorphologicalGender.Masculine);
                if (reqNum == MorphologicalNumber.Plural
                    ? countryAxes.Num == MorphologicalNumber.Plural
                    : countryAxes.Num == MorphologicalNumber.Singular && (countryAxes.Gen is null || countryAxes.Gen == reqGen))
                {
                    anyMatches = true;
                }
                continue;
            }

            int[] pronRows = allRows
                .Where(r => cur.Word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun &&
                            !Guard.TryGetDeterminerAxes(cur.Word, cur.Word.Readings[r], out _))
                .ToArray();
            if (pronRows.Length > 0)
            {
                if (pronRows.Any(r => cur.Word.Readings[r].Features.Person is MorphologicalPerson.First or MorphologicalPerson.Second))
                    return false;
                nominalCandidates.Add((p, pronRows));
                if (pronRows.Any(r =>
                {
                    MorphologicalFeatures pf = cur.Word.Readings[r].Features;
                    if (reqNum == MorphologicalNumber.Plural)
                        return pf.Number is null or MorphologicalNumber.Plural;
                    return pf.Number is null or MorphologicalNumber.Singular && (pf.Gender is null || pf.Gender == reqGen);
                }))
                {
                    anyMatches = true;
                }
            }
        }

        // [P1-I1c] Headless free relatives have zero nominal candidates in left prefix; composite plural antecedents have >= 2.
        if (anyMatches || nominalCandidates.Count == 0 || reqNum == MorphologicalNumber.Plural && nominalCandidates.Count >= 2)
            return false;

        var nearest = nominalCandidates[0];
        WordSlot nearestAnt = slots[nearest.SlotIndex];
        string evidenceCode = "pronoun_case_token=" + slots[relSlotIdx].Word.OriginalIndex.ToString(CultureInfo.InvariantCulture);
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, nearestAnt.Word.OriginalIndex, nearestAnt.Word.Token.Start, nearestAnt.Word.Token.Length,
                nearest.SupportReadings, ["clause_fusion_relative_antecedent"]),
            new(ContextOverlayRole.NominalContext, slots[relSlotIdx].Word.OriginalIndex, slots[relSlotIdx].Word.Token.Start, slots[relSlotIdx].Word.Token.Length,
                relReadings, ["clause_fusion_relative_pronoun"])
        ];
        agreement.Add(new(nearestAnt.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, [evidenceCode]));
        return true;
    }

    private static bool TryGetSyntacticallyDisambiguatedAntecedentNouns(
        SentenceClauseMap map,
        IReadOnlyList<WordSlot> slots,
        int antEnd,
        CancellationToken ct,
        out int[]? nounRows)
    {
        nounRows = null;
        if (antEnd < 1 || !IsExactWord(slots[antEnd]) || !IsExactWord(slots[antEnd - 1]) ||
            !map.AreSpaceBridged(antEnd - 1, antEnd, ct))
            return false;
        WordSlot ant = slots[antEnd];
        if (ant.Word.Token.Text.Length > 0 && char.IsUpper(ant.Word.Token.Text[0]))
            return false;
        int[] active = ant.Active.Length > 0 ? ant.Active : NonPlaceholderReadings(ant.Word, sentenceInitial: false);
        if (active.Length == 0 ||
            active.Any(r => ant.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun ||
                            ant.Word.Readings[r].Features.NameKind is not null))
            return false;
        int[] nouns = active.Where(r => ant.Word.Readings[r].Category == ContextLanes.Category.Noun &&
            ant.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
            Guard.HasNominalAxes(ant.Word.Readings[r].Features)).ToArray();
        if (nouns.Length == 0 ||
            !active.All(r => nouns.Contains(r) ||
                ant.Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative }))
            return false;

        WordSlot prev = slots[antEnd - 1];
        string pc = prev.Word.CanonicalText;
        int[] prevActive = prev.Active.Length > 0 ? prev.Active : NonPlaceholderReadings(prev.Word, antEnd - 1 == 0);
        if (prevActive.Length == 0)
            return false;
        bool prevForcesNominal =
            pc is "е" or "са" or "беше" or "бяха" or "бе" or "съм" or "си" or "сме" or "сте" or "бъде" or "бъдат" ||
            Guard.IsExactAdposition(prev.Word) ||
            prevActive.All(r => prev.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral) ||
            IsNominalModifierOrDeterminer(prev) && prevActive.Any(pr =>
            {
                MorphologicalFeatures pf = prev.Word.Readings[pr].Features;
                var pAxes = Guard.TryGetDeterminerAxes(prev.Word, prev.Word.Readings[pr], out var d) ? (d.Number, d.Gender)
                    : Guard.TryGetOrdinalAxes(prev.Word.Readings[pr], out var o) ? (o.Number, o.Gender)
                    : (pf.Number, pf.Gender);
                return nouns.Any(nr =>
                {
                    MorphologicalFeatures nf = ant.Word.Readings[nr].Features;
                    MorphologicalNumber? nNum = nf.Number == MorphologicalNumber.Count ? MorphologicalNumber.Plural : nf.Number;
                    return Guard.NominalTupleMatches(pAxes, (nNum, nf.Gender));
                });
            });
        if (!prevForcesNominal)
            return false;
        nounRows = nouns;
        return true;
    }

    private static void TryEmitClauseInternalRelativeSubjectMismatch(
        SentenceClauseMap map,
        int relSlotIdx,
        MorphologicalNumber reqNum,
        MorphologicalGender? reqGen,
        int[] relReadings,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        if (relSlotIdx + 2 >= slots.Length || peripheral[relSlotIdx + 1] || peripheral[relSlotIdx + 2] ||
            !IsExactWord(slots[relSlotIdx + 1]) || !IsExactWord(slots[relSlotIdx + 2]) ||
            !map.AreSpaceBridged(relSlotIdx, relSlotIdx + 1, ct) ||
            !map.AreSpaceBridged(relSlotIdx + 1, relSlotIdx + 2, ct))
            return;

        WordSlot auxSlot = slots[relSlotIdx + 1];
        WordSlot partSlot = slots[relSlotIdx + 2];
        int[] auxReadings = Enumerable.Range(0, auxSlot.Word.Readings.Count)
            .Where(r => TextPredicateLinkAnalyzer.CopularCarrier(auxSlot.Word.Readings[r]))
            .Order()
            .ToArray();
        int[] passiveReadings = Enumerable.Range(0, partSlot.Word.Readings.Count)
            .Where(r => partSlot.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastPassive,
                    Article: null or MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                })
            .Order()
            .ToArray();
        if (auxReadings.Length == 0 || passiveReadings.Length == 0)
            return;

        // Ensure no ungoverned postverbal nominative NP inside the relative clause could serve as inverted subject.
        bool insidePp = false;
        for (int k = relSlotIdx + 3; k < slots.Length; k++)
        {
            if (map.HasCommaBetween(k - 1, k)) break;
            string ck = slots[k].Word.CanonicalText;
            if (ck is "че" or "да" or "защото" or "когато" or "ако" or "докато" or "където" or "и" or "но" or "а" or "или")
                break;
            if (!IsExactWord(slots[k])) return;
            if (IsActivePreposition(slots[k].Word, slots[k].Active)) { insidePp = true; continue; }
            if (insidePp)
            {
                if (HasPureNominal(slots[k].Word, slots[k].Active) && !IsNominalModifierOrDeterminer(slots[k]))
                    insidePp = false;
                continue;
            }
            if (HasNominalHeadReading(slots[k]) || slots[k].Word.CanonicalText is "той" or "тя" or "то" or "те")
                return;
        }

        bool agrees = auxReadings.Any(ar =>
        {
            MorphologicalFeatures af = auxSlot.Word.Readings[ar].Features;
            if (af.Person != MorphologicalPerson.Third || af.Number != reqNum) return false;
            return passiveReadings.Any(pr =>
            {
                MorphologicalFeatures pf = partSlot.Word.Readings[pr].Features;
                return pf.Number == reqNum && (reqGen is null || pf.Gender == reqGen);
            });
        });
        if (agrees) return;

        string evidenceCode = "pronoun_case_token=" + slots[relSlotIdx].Word.OriginalIndex.ToString(CultureInfo.InvariantCulture);
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, auxSlot.Word.OriginalIndex, auxSlot.Word.Token.Start, auxSlot.Word.Token.Length,
                auxReadings, ["clause_fusion_relative_copula"]),
            new(ContextOverlayRole.NominalContext, slots[relSlotIdx].Word.OriginalIndex, slots[relSlotIdx].Word.Token.Start, slots[relSlotIdx].Word.Token.Length,
                relReadings, ["clause_fusion_relative_pronoun"]),
            new(ContextOverlayRole.Attribute, partSlot.Word.OriginalIndex, partSlot.Word.Token.Start, partSlot.Word.Token.Length,
                passiveReadings, ["clause_fusion_relative_participle"])
        ];
        agreement.Add(new(auxSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, [evidenceCode]));
    }

    private static void ScanDeicticTemporalTenseMismatches(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        if (!map.HasTerminalPunctuation || !map.AllExactOrDegree || map.Clauses.Count != 1)
            return;

        TextAnalysis analysis = map.Analysis;
        SentenceSpan sentence = map.Sentence;
        if (analysis.Text.AsSpan(sentence.Start, sentence.Length).ContainsAny(",;:\u2013\u2014"))
            return;

        ClauseSpan clause = map.Clauses[0];
        if (clause.Boundary.Kind != ClauseBoundaryKind.SentenceInitial)
            return;
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        int[] idx = clause.WordIndices;
        if (idx.Length < 2) return;

        if (BindClauseMainComplex(map, clause, ct) is not { } verb) return;
        if (verb.InvariantCarrier || verb.ImpersonalModal || verb.Dependents.Length > 0)
            return;
        for (int p = clause.BodyStartPos; p < idx.Length; p++)
        {
            int w = idx[p];
            if ((w < verb.FirstWordIndex || w > verb.LastWordIndex) &&
                !slots[w].Quoted && SentenceClauseMap.HasFiniteReading(slots[w]))
                return;
        }

        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        if (verbSlot.Quoted || !IsExactWord(verbSlot) ||
            !IsCleanPredicateToken(verbSlot.Word, IsSentenceInitialWord(analysis, sentence, verb.FiniteCarrierWordIndex, verbSlot.Word), ct) ||
            verbSlot.Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative && !r.Features.HasConflictingFeatures))
            return;

        int[] allFinite = AllFinitePersonalReadings(verbSlot.Word, ct);
        if (allFinite.Length == 0) return;

        int verbFirstPos = Array.IndexOf(idx, verb.FirstWordIndex);
        int verbLastPos = Array.IndexOf(idx, verb.LastWordIndex);
        if (verbFirstPos < 0 || verbLastPos < 0) return;
        bool[] gov = MarkGovernedPhrases(map, idx, 0, idx.Length - 1, verbFirstPos, verbLastPos, ct);
        for (int pos = 0; pos < idx.Length; pos++)
        {
            int wIdx = idx[pos];
            if (peripheral[wIdx] || slots[wIdx].Quoted || gov[pos] || !IsExactWord(slots[wIdx])) continue;
            string advCanon = slots[wIdx].Word.CanonicalText;
            bool isPastDeictic = advCanon is "вчера" or "завчера" or "снощи";
            bool isFutureDeictic = advCanon is "утре" or "вдругиден";
            if (!isPastDeictic && !isFutureDeictic) continue;

            // Guard G4-CP1: Skip governed PPs ("за утре", "от вчера") and adnominal postmodifiers of definite event nouns ("срещата вчера").
            if (pos > 0)
            {
                WordSlot prev = slots[idx[pos - 1]];
                if (IsActivePreposition(prev.Word, prev.Active)) continue;
                if (wIdx < verb.FirstWordIndex &&
                    prev.Active.Any(r => prev.Word.Readings[r].Features is
                        { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short }))
                    continue;
            }

            int[] advReadings = NonPlaceholderReadings(slots[wIdx].Word, wIdx == 0)
                .Where(r => slots[wIdx].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb)
                .Order()
                .ToArray();
            if (advReadings.Length == 0) continue;

            if (isPastDeictic)
            {
                if (verb.Kind != VerbalComplexKind.Future || verb.FutureParticleWordIndex < 0 ||
                    verb.DaParticleWordIndex >= 0 || verb.CopularCarrier ||
                    allFinite.Any(r => TextPredicateLinkAnalyzer.CopularLemma(verbSlot.Word.Readings[r].Lemma)))
                    continue;
                WordSlot shteSlot = slots[verb.FutureParticleWordIndex];
                int[] shteReadings = NonPlaceholderReadings(shteSlot.Word, verb.FutureParticleWordIndex == 0)
                    .Where(r => shteSlot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle)
                    .Order()
                    .ToArray();
                int[] presentFinite = allFinite
                    .Where(r => verbSlot.Word.Readings[r].Features.Tense == MorphologicalTense.Present)
                    .ToArray();
                if (shteReadings.Length == 0 || presentFinite.Length == 0)
                    continue;

                List<ContextOverlayRoleSelection> roles =
                [
                    new(ContextOverlayRole.Predicate, verbSlot.Word.OriginalIndex, verbSlot.Word.Token.Start, verbSlot.Word.Token.Length,
                        presentFinite, ["clause_fusion_finite_readings"]),
                    new(ContextOverlayRole.NominalContext, slots[wIdx].Word.OriginalIndex, slots[wIdx].Word.Token.Start, slots[wIdx].Word.Token.Length,
                        advReadings, ["deictic_past_temporal_adverb"]),
                    new(ContextOverlayRole.NominalContext, shteSlot.Word.OriginalIndex, shteSlot.Word.Token.Start, shteSlot.Word.Token.Length,
                        shteReadings, ["deictic_future_particle"])
                ];
                agreement.Add(new(verbSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                    canonicalKey(roles), roles, ["deictic_temporal_mismatch"]));
                return;
            }

            if (isFutureDeictic)
            {
                if (verb.Kind != VerbalComplexKind.SyntheticFinite || verb.FutureParticleWordIndex >= 0 ||
                    verb.DaParticleWordIndex >= 0 || verb.CopularCarrier)
                    continue;
                static bool SchedulingOrCancellationLemma(string l) =>
                    l is "отменя" or "отменям" or "отложа" or "отлагам" or "насроча" or "насрочвам" or
                        "планирам" or "обявя" or "обявявам" or "пропадна" or "пропадам" or "отпадна" or "отпадам";
                if (!allFinite.All(r => verbSlot.Word.Readings[r].Features.Tense == MorphologicalTense.Aorist &&
                        !SchedulingOrCancellationLemma(Norm(verbSlot.Word.Readings[r].Lemma))))
                    continue;

                List<ContextOverlayRoleSelection> roles =
                [
                    new(ContextOverlayRole.Predicate, verbSlot.Word.OriginalIndex, verbSlot.Word.Token.Start, verbSlot.Word.Token.Length,
                        allFinite, ["clause_fusion_finite_readings"]),
                    new(ContextOverlayRole.NominalContext, slots[wIdx].Word.OriginalIndex, slots[wIdx].Word.Token.Start, slots[wIdx].Word.Token.Length,
                        advReadings, ["deictic_future_temporal_adverb"])
                ];
                agreement.Add(new(verbSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                    canonicalKey(roles), roles, ["deictic_temporal_mismatch"]));
                return;
            }
        }
    }

    private static bool DirectlySeparatedBySingleComma(TextAnalysis analysis, TokenView left, TokenView right)
    {
        int start = left.Token.Start + left.Token.Length;
        if (right.Token.Start <= start) return false;
        ReadOnlySpan<char> gap = analysis.Text.AsSpan(start, right.Token.Start - start);
        int commas = 0;
        foreach (char ch in gap)
        {
            if (ch == ',') commas++;
            else if (!char.IsWhiteSpace(ch)) return false;
        }
        return commas == 1;
    }

    private static bool TryGetPureCommonVocativeReadings(WordSlot[] slots, int index, out int[] readings)
    {
        readings = [];
        WordSlot slot = slots[index];
        if (!IsExactWord(slot)) return false;
        string surface = slot.Word.Token.Text;
        if (surface.Length == 0) return false;
        if (index > 0 && char.IsUpper(surface[0])) return false;
        if (index == 0 && index + 1 < slots.Length && slots[index + 1].Word.Token.Text is { Length: > 0 } nextSurf &&
            char.IsUpper(nextSurf[0]))
            return false;

        int[] realRows = NonPlaceholderReadings(slot.Word, index == 0);
        if (realRows.Length == 0) return false;

        foreach (int r in realRows)
        {
            ReadingView row = slot.Word.Readings[r];
            MorphologicalFeatures f = row.Features;
            bool isVoc = f.PartOfSpeech == MorphologicalPartOfSpeech.Noun && !f.HasConflictingFeatures &&
                (f.RawTag?.Contains("Звател", StringComparison.Ordinal) == true ||
                 row.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) == true);
            if (!isVoc || Norm(row.Lemma) == slot.Word.CanonicalText)
                return false;
        }

        readings = realRows;
        return true;
    }

    private static void EmitVocativeAttributeMismatch(
        TokenView vocWord,
        int[] vocReadings,
        TokenView partnerWord,
        int[] partnerReadings,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement)
    {
        string attrCode = "attribute_mismatch_token=" + vocWord.OriginalIndex.ToString(CultureInfo.InvariantCulture);
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Attribute, vocWord.OriginalIndex, vocWord.Token.Start, vocWord.Token.Length,
                vocReadings, ["clause_fusion_vocative_intrusion"]),
            new(ContextOverlayRole.NominalContext, partnerWord.OriginalIndex, partnerWord.Token.Start, partnerWord.Token.Length,
                partnerReadings, ["clause_fusion_nominal_modifier"])
        ];
        agreement.Add(new(vocWord.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, [attrCode, ContextLanes.ProductiveAttributeMismatch]));
    }

    private static bool TryAddObliqueSubjectPronounMismatch(
        SentenceClauseMap map,
        ClauseSpan clause,
        int verbFirstPos,
        bool[] governed,
        VerbalComplex verb,
        List<NominalChunk> postNps,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        int[] clauseIdx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        if (bodyStart != 0 || clauseIdx[0] != 0 || bodyStart >= verbFirstPos || governed[bodyStart] ||
            verb.CarrierAccusativeCliticWordIndex >= 0)
            return false;

        WordSlot pronSlot = slots[clauseIdx[bodyStart]];
        WordSlot verbSlot = slots[verb.FiniteCarrierWordIndex];
        if (pronSlot.Word.UsesGraph || verbSlot.Word.UsesGraph) return false;

        if (!BulgarianFunctionWords.TryGetFullObliquePronoun(pronSlot.Word.CanonicalText,
                out MorphologicalPerson pronPerson, out MorphologicalNumber pronNumber))
            return false;

        int[] oblReadings = Guard.ObliquePronounReadings(pronSlot.Word, ct);
        if (oblReadings.Length == 0) return false;
        if (map.HasDirectCommaBetween(clauseIdx, bodyStart, verbFirstPos)) return false;

        for (int p = bodyStart + 1; p < verbFirstPos; p++)
        {
            string mid = slots[clauseIdx[p]].Word.CanonicalText;
            if (mid is not ("не" or "ще" or "се") ||
                !map.AreSpaceBridged(clauseIdx[p - 1], clauseIdx[p], ct))
                return false;
        }
        if (!map.AreSpaceBridged(clauseIdx[verbFirstPos - 1], clauseIdx[verbFirstPos], ct))
            return false;
        for (int w = verb.FirstWordIndex; w < verb.FiniteCarrierWordIndex; w++)
            if (BulgarianFunctionWords.HasAny(slots[w].Word.CanonicalText, FunctionWordClasses.ShortDativeClitic))
                return false;

        int[] allFinite = AllFinitePersonalReadings(verbSlot.Word, ct);
        if (!verb.AllowsPerfectivePresent(map, clause))
            allFinite = FilterIndicativeFinite(verbSlot.Word, allFinite);
        if (allFinite.Length == 0 || verb.InvariantCarrier) return false;

        bool verbAgreesWithPronoun = allFinite.Any(r => Guard.PersonNumberTupleMatches(
                (pronPerson, pronNumber),
                (verbSlot.Word.Readings[r].Features.Person, verbSlot.Word.Readings[r].Features.Number))) &&
            allFinite.All(r =>
            {
                MorphologicalFeatures vf = verbSlot.Word.Readings[r].Features;
                return Guard.PersonNumberTupleMatches((pronPerson, pronNumber), (vf.Person, vf.Number)) ||
                       pronPerson == MorphologicalPerson.Third && pronNumber == MorphologicalNumber.Singular &&
                       vf is { Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Singular,
                           Tense: MorphologicalTense.Aorist or MorphologicalTense.Imperfect };
            });

        int verbLastPos = Array.IndexOf(clauseIdx, verb.LastWordIndex);
        bool postHasCoordination = verbLastPos >= 0 &&
            Enumerable.Range(verbLastPos + 1, clauseIdx.Length - verbLastPos - 1)
                .Any(p => !governed[p] && slots[clauseIdx[p]].Word.CanonicalText is "и" or "или");

        if (verbAgreesWithPronoun)
        {
            if (pronPerson == MorphologicalPerson.Second && HasSecondPersonImperative(verbSlot.Word, pronNumber))
                return false;
            if (pronPerson == MorphologicalPerson.Third)
            {
                bool strictlyIntransitive = allFinite.All(r =>
                    verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma)) &&
                    !postNps.Any(np => !np.TemporalAdjunct);
                bool saturatedThirdOblique = pronSlot.Word.CanonicalText is "него" or "нея" or "тях" &&
                    !postHasCoordination && !verb.CopularCarrier && !verb.HasReflexiveSe &&
                    allFinite.All(r => verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                        !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma) &&
                        !IsSecondaryPredicativeOrDitransitiveLemma(verbSlot.Word.Readings[r].Lemma)) &&
                    !postNps.Any(np => !np.TemporalAdjunct && !np.ShortMasculineSingular &&
                        (!np.Coherent || np.Number is null || MatchingVerbReadings(verbSlot, allFinite, np).Length > 0)) &&
                    postNps.Any(np => !np.TemporalAdjunct && np.Coherent && !np.FullMasculineSingular &&
                        (np.ShortMasculineSingular ||
                         IsDefiniteOrProperSubjectCandidate(slots, np) &&
                         MatchingVerbReadings(verbSlot, allFinite, np).Length == 0));
                if (!strictlyIntransitive && !saturatedThirdOblique)
                    return false;
            }
        }
        else
        {
            // Discordant bare oblique pronoun at <BOS> when the verb cannot license it as a fronted direct object.
            bool unresumedIntransitiveOrSaturatedObject;
            if (verb.CopularCarrier && verb.HasActiveParticipleTail && verb.LastWordIndex > verb.FiniteCarrierWordIndex)
            {
                VerbalDependent partDep = verb.Dependents.Last(d => d.Kind == VerbalDependentKind.ActiveParticiple);
                TokenView partWord = slots[partDep.WordIndex].Word;
                int[] partRows = partDep.Readings;
                bool partIntransitive = partRows.All(r =>
                    partWord.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(partWord.Readings[r].Lemma));
                unresumedIntransitiveOrSaturatedObject = partIntransitive ||
                    !postHasCoordination && !verb.HasReflexiveSe &&
                    partRows.All(r => !IsSecondaryPredicativeOrDitransitiveLemma(partWord.Readings[r].Lemma)) &&
                    IsExclusivelyFirstOrSecond(verbSlot.Word, allFinite) && postNps.Any(np => !np.TemporalAdjunct);
            }
            else if (!verb.CopularCarrier)
            {
                bool strictlyIntransitive = allFinite.All(r =>
                    verbSlot.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(verbSlot.Word.Readings[r].Lemma));
                bool directObjectSaturated = !postHasCoordination && !verb.HasReflexiveSe &&
                    allFinite.All(r => !IsSecondaryPredicativeOrDitransitiveLemma(verbSlot.Word.Readings[r].Lemma)) &&
                    postNps.Any(np => !np.TemporalAdjunct &&
                    (IsExclusivelyFirstOrSecond(verbSlot.Word, allFinite) ||
                     np.ShortMasculineSingular ||
                     np.Coherent && np.Number is not null && MatchingVerbReadings(verbSlot, allFinite, np).Length == 0));
                unresumedIntransitiveOrSaturatedObject = strictlyIntransitive || directObjectSaturated;
            }
            else
            {
                unresumedIntransitiveOrSaturatedObject = false;
            }

            if (!unresumedIntransitiveOrSaturatedObject)
                return false;
        }

        string evidenceCode = "pronoun_case_token=" + pronSlot.Word.OriginalIndex.ToString(CultureInfo.InvariantCulture);
        List<ContextOverlayRoleSelection> roles =
        [
            new(ContextOverlayRole.Predicate, verbSlot.Word.OriginalIndex, verbSlot.Word.Token.Start, verbSlot.Word.Token.Length,
                allFinite, ["clause_fusion_finite_readings"]),
            new(ContextOverlayRole.NominalContext, pronSlot.Word.OriginalIndex, pronSlot.Word.Token.Start, pronSlot.Word.Token.Length,
                oblReadings, ["clause_fusion_oblique_subject_pronoun"])
        ];
        agreement.Add(new(verbSlot.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
            canonicalKey(roles), roles, [evidenceCode]));
        return true;
    }

    private static bool IsSecondaryPredicativeOrDitransitiveLemma(string lemma) =>
        Norm(lemma) is "правя" or "направя" or "смятам" or "сметна" or "считам" or
            "наричам" or "нарека" or "избирам" or "избера" or "обявявам" or "обявя" or "назначавам" or "назнача" or
            "оставям" or "оставя" or "приемам" or "приема" or "определям" or "определя" or
            "уча" or "науча" or "научавам" or "питам" or "попитам" or "разпитвам" or "моля" or "помоля" or "карам" or "накарам";

    private static int[] FilterIndicativeFinite(TokenView verbWord, int[] finite)
    {
        int[] nonPerfPres = finite.Where(r =>
        {
            MorphologicalFeatures f = verbWord.Readings[r].Features;
            return !(f.Tense == MorphologicalTense.Present && f.DeclaredAspect == MorphologicalVerbAspect.Perfective);
        }).ToArray();
        return nonPerfPres.Length > 0 ? nonPerfPres : finite;
    }

    private static bool IsDefiniteOrProperSubjectCandidate(WordSlot[] slots, NominalChunk np)
    {
        if (np.PronounSubject || np.FullMasculineSingular) return true;
        if (np.ShortMasculineSingular) return false;
        WordSlot carrier = slots[np.FirstWord];
        if (carrier.HyphenatedCompoundNominal is { Article: MorphologicalArticle.Definite or MorphologicalArticle.Full })
            return true;
        if (!carrier.Word.UsesGraph && carrier.Active.Any(r =>
            {
                ReadingView rv = carrier.Word.Readings[r];
                MorphologicalFeatures f = rv.Features;
                if (f.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full)
                    return true;
                if (Guard.TryGetDeterminerAxes(carrier.Word, rv, out _))
                {
                    MorphologicalArticle? detArt = Guard.GetDeterminerArticle(f);
                    if (detArt is MorphologicalArticle.Definite or MorphologicalArticle.Full ||
                        Norm(rv.Lemma) is "този" or "онзи")
                        return true;
                }
                return false;
            }))
            return true;
        WordSlot head = slots[np.HeadWord];
        if (head.HyphenatedCompoundNominal is { Article: MorphologicalArticle.Definite or MorphologicalArticle.Full })
            return true;
        return !head.Word.UsesGraph && head.Active.Any(r =>
            head.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun);
    }

    private static bool HasFullMasculineSingularSubjectCarrier(WordSlot[] slots, NominalChunk subj)
    {
        if (subj.FullMasculineSingular) return true;
        if (subj.Gender != MorphologicalGender.Masculine || subj.Number != MorphologicalNumber.Singular)
            return false;
        WordSlot carrier = slots[subj.FirstWord];
        return !carrier.Word.UsesGraph && carrier.Active.Length > 0 && carrier.Active.All(r =>
            Guard.TryGetDeterminerAxes(carrier.Word, carrier.Word.Readings[r], out var d) &&
            d.Gender == MorphologicalGender.Masculine && d.Number == MorphologicalNumber.Singular &&
            Guard.GetDeterminerArticle(carrier.Word.Readings[r].Features) == MorphologicalArticle.Full);
    }

    private static void ScanSharedSubjectVpCoordinationMismatches(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        IReadOnlySet<int>? r3OwnedClauseIds,
        CancellationToken ct)
    {
        if (!map.HasTerminalPunctuation || map.Clauses.Count == 0)
            return;
        ReadOnlySpan<char> sentText = map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length).TrimEnd();
        if (!sentText.EndsWith(".", StringComparison.Ordinal))
            return;

        WordSlot[] slots = map.Slots;
        ClauseSpan c1 = map.Clauses[0];
        if (!c1.Matrix || c1.WordIndices.Any(w => slots[w].Quoted))
            return;

        VerbalComplex? v1 = BindClauseMainComplex(map, c1, ct);
        if (v1 is null || v1.InvariantCarrier || v1.ImpersonalModal)
            return;

        int[] idx1 = c1.WordIndices;
        int v1FirstPos = Array.IndexOf(idx1, v1.FirstWordIndex);
        int v1LastPos = Array.IndexOf(idx1, v1.LastWordIndex);
        if (v1FirstPos <= c1.BodyStartPos || v1LastPos < v1FirstPos)
            return;

        bool[] gov1 = MarkGovernedPhrases(map, idx1, c1.BodyStartPos, idx1.Length - 1, v1FirstPos, v1LastPos, ct);
        if (HasCoordinatedPreverbalNominals(map, idx1, c1.BodyStartPos, v1FirstPos - 1, gov1, ct))
            return;
        List<NominalChunk> pre1 = CollectNominalChunks(map, idx1, c1.BodyStartPos, v1FirstPos - 1, gov1, ct);
        NominalChunk[] nonTempPre1 = pre1.Where(np => !np.TemporalAdjunct).ToArray();
        if (nonTempPre1 is not [{ Coherent: true, HeadReadings.Length: > 0 } subj] ||
            !HasFullMasculineSingularSubjectCarrier(slots, subj) ||
            subj.FirstWord != idx1[c1.BodyStartPos] ||
            map.HasDirectCommaBetween(idx1, Array.IndexOf(idx1, subj.LastWord), v1FirstPos) ||
            HasVocativeReading(slots[subj.HeadWord]) ||
            IsAdverbialTimeOrMeasureHead(slots[subj.HeadWord], subj) ||
            IsPersonPlaceHomograph(map.Analysis, slots[subj.HeadWord].Word.OriginalIndex) ||
            IsCliticResumedObject(slots, subj, v1) ||
            !HasCleanSelectionTokens(map, v1, subj, ct))
            return;

        WordSlot v1Slot = slots[v1.FiniteCarrierWordIndex];
        if (MatchingVerbReadings(v1Slot, v1.CarrierFiniteReadings, subj).Length == 0)
            return;

        // Case 1: Two finite clauses coordinated by 'и' without an intervening comma ("[Subj-ът + V1 + PP] и [V2 + PP]").
        if (map.Clauses.Count >= 2)
        {
            ClauseSpan c2 = map.Clauses[1];
            int[] idx2 = c2.WordIndices;
            if (r3OwnedClauseIds?.Contains(c2.ClauseId) != true &&
                c2.Boundary.Kind == ClauseBoundaryKind.Coordinator &&
                idx2.Length >= 2 &&
                c2.BodyStartPos == 1 &&
                slots[idx2[0]].Word.CanonicalText == "и" &&
                !idx2.Any(w => slots[w].Quoted) &&
                idx2[0] == idx1[^1] + 1 &&
                !map.HasCommaBetween(idx1[^1], idx2[0]))
            {
                VerbalComplex? v2 = BindClauseMainComplex(map, c2, ct);
                if (v2 is not null && !v2.InvariantCarrier && !v2.ImpersonalModal && !v2.CopularCarrier)
                {
                    int v2FirstPos = Array.IndexOf(idx2, v2.FirstWordIndex);
                    int v2LastPos = Array.IndexOf(idx2, v2.LastWordIndex);
                    if (v2FirstPos == c2.BodyStartPos && HasCleanSelectionTokens(map, v2, subj, ct))
                    {
                        bool[] gov2 = MarkGovernedPhrases(map, idx2, c2.BodyStartPos, idx2.Length - 1, v2FirstPos, v2LastPos, ct);
                        List<NominalChunk> post2 = CollectNominalChunks(map, idx2, v2LastPos + 1, idx2.Length - 1, gov2, ct);
                        WordSlot v2Slot = slots[v2.FiniteCarrierWordIndex];
                        int[] allFin2 = AllFinitePersonalReadings(v2Slot.Word, ct);
                        if (!v2.AllowsPerfectivePresent(map, c2))
                            allFin2 = FilterIndicativeFinite(v2Slot.Word, allFin2);

                        bool hasPostNomPron2 = Enumerable.Range(v2LastPos + 1, idx2.Length - v2LastPos - 1)
                            .Any(p => !gov2[p] && NominativePronounReadings(slots[idx2[p]], slots[idx2[p - 1]]).Length > 0);
                        bool hasPostSubj2 = hasPostNomPron2 ||
                            post2.Any(np => np.FullMasculineSingular) ||
                            HasCompatiblePostverbalSubject(slots, post2, v2Slot, allFin2);

                        if (allFin2.Length > 0 && !hasPostSubj2 &&
                            !allFin2.Any(r => Guard.PersonNumberTupleMatches(
                                (MorphologicalPerson.Third, MorphologicalNumber.Singular),
                                (v2Slot.Word.Readings[r].Features.Person, v2Slot.Word.Readings[r].Features.Number))))
                        {
                            // [P1-I4] Participant-licensing guard for inclusive 1pl/2pl/3pl ("Моят колега дойде при нас и работихме заедно.").
                            var postV1Words = idx1.Skip(v1LastPos + 1).Select(w => slots[w]).ToArray();
                            bool licensed1Pl = allFin2.Any(r => v2Slot.Word.Readings[r].Features is
                                { Person: MorphologicalPerson.First, Number: MorphologicalNumber.Plural }) &&
                                postV1Words.Any(s =>
                                    BulgarianFunctionWords.TryGetFullObliquePronoun(s.Word.CanonicalText, out var op, out _) && op == MorphologicalPerson.First ||
                                    s.Word.CanonicalText is "ми" or "ни" ||
                                    s.Word.Readings.Any(r => Norm(r.Lemma) is "мой" or "наш"));
                            bool licensed2Pl = allFin2.Any(r => v2Slot.Word.Readings[r].Features is
                                { Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Plural }) &&
                                postV1Words.Any(s =>
                                    BulgarianFunctionWords.TryGetFullObliquePronoun(s.Word.CanonicalText, out var op, out _) && op == MorphologicalPerson.Second ||
                                    s.Word.CanonicalText is "ти" or "ви" ||
                                    s.Word.Readings.Any(r => Norm(r.Lemma) is "твой" or "ваш"));
                            bool licensed3Pl = allFin2.Any(r => v2Slot.Word.Readings[r].Features is
                                { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Plural }) &&
                                postV1Words.Any(s =>
                                    BulgarianFunctionWords.TryGetFullObliquePronoun(s.Word.CanonicalText, out var op, out _) && op == MorphologicalPerson.Third ||
                                    s.Word.CanonicalText is "му" or "ѝ" or "им" ||
                                    HasAnimateNominalHead(s, s.Active));
                            var impRows2 = v2Slot.Word.Readings
                                .Where(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative && !r.Features.HasConflictingFeatures)
                                .ToArray();
                            bool blockedByImp2 = impRows2.Length > 0 && !impRows2.All(r => r.Features.Number == MorphologicalNumber.Plural);
                            bool sharedCopulaMascPass2 = v1.CopularCarrier && v2Slot.Word.Readings.Any(r => r.Features is
                            {
                                PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                                VerbForm: MorphologicalVerbForm.Participle,
                                ParticipleKind: MorphologicalParticipleKind.PastPassive,
                                Number: MorphologicalNumber.Singular,
                                Gender: MorphologicalGender.Masculine,
                                Article: null or MorphologicalArticle.Indefinite,
                                HasConflictingFeatures: false
                            });

                            if (!licensed1Pl && !licensed2Pl && !licensed3Pl && !blockedByImp2 && !sharedCopulaMascPass2 &&
                                !agreement.Any(a => a.PredicateTokenIndex == v2Slot.Word.OriginalIndex))
                            {
                                AddSubjectRelation(slots, subj, v2, allFin2, canonicalKey, agreement,
                                    "nominal_subject_clause_mismatch", mismatch: true, subjectProven: true);
                                return;
                            }
                        }
                    }
                }
            }
        }

        // Case 2: Non-finite V2 inside single clause ("[Subj-ът + V1 + PP + и + BareActL(non-MascSg) / BarePassPart + PP]").
        if (map.Clauses.Count == 1)
        {
            for (int p = v1LastPos + 2; p + 1 < idx1.Length; p++)
            {
                if (gov1[p] || slots[idx1[p]].Word.CanonicalText != "и" ||
                    map.HasCommaBetween(idx1[v1LastPos], idx1[p]) ||
                    !map.AreSpaceBridged(idx1[p], idx1[p + 1], ct))
                    continue;
                WordSlot cand = slots[idx1[p + 1]];
                if (!IsExactWord(cand) || HasFiniteReading(cand) ||
                    p + 2 < idx1.Length && map.AreSpaceBridged(idx1[p + 1], idx1[p + 2], ct) && HasNominalHeadReading(slots[idx1[p + 2]]))
                    continue;
                int[] candRows = NonPlaceholderReadings(cand.Word, sentenceInitial: false).Order().ToArray();
                if (candRows.Length == 0) continue;

                bool pureNonMascActL = candRows.All(r => cand.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    Article: null or MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                }) && !candRows.Any(r => cand.Word.Readings[r].Features is
                    { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine });
                if (pureNonMascActL && !agreement.Any(a => a.PredicateTokenIndex == cand.Word.OriginalIndex))
                {
                    TokenView sw = slots[subj.HeadWord].Word;
                    List<ContextOverlayRoleSelection> roles =
                    [
                        new(ContextOverlayRole.Predicate, cand.Word.OriginalIndex, cand.Word.Token.Start, cand.Word.Token.Length,
                            candRows, ["clause_fusion_participle_readings"]),
                        new(ContextOverlayRole.Subject, sw.OriginalIndex, sw.Token.Start, sw.Token.Length,
                            subj.HeadReadings, ["nominal_subject_clause_mismatch"])
                    ];
                    for (int w = subj.FirstWord; w < subj.HeadWord; w++)
                        if (!slots[w].Word.UsesGraph && slots[w].Active.Length > 0)
                            roles.Add(new(ContextOverlayRole.Attribute, slots[w].Word.OriginalIndex, slots[w].Word.Token.Start,
                                slots[w].Word.Token.Length, slots[w].Active, ["clause_fusion_nominal_modifier"]));
                    agreement.Add(new(cand.Word.OriginalIndex, Selectable: false, AgreementLane, Score: 0,
                        canonicalKey(roles), roles, ["compound_subj_participle_mismatch"]));
                    return;
                }

                bool pureBarePass = candRows.All(r => cand.Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastPassive,
                    Article: null or MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                }) && !(v1.CopularCarrier && candRows.Any(r => cand.Word.Readings[r].Features is
                    { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine }));
                if (pureBarePass && !agreement.Any(a => a.PredicateTokenIndex == cand.Word.OriginalIndex))
                {
                    EmitParticleOrAuxVerbFormMismatch(slots[subj.HeadWord].Word, cand.Word, candRows, canonicalKey, agreement);
                    return;
                }
            }
        }
    }

    private static void ScanVerblessCliticPredicativeIntrusions(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        List<ContextOverlayCandidate> agreement,
        CancellationToken ct)
    {
        // [P1-I8] Restrict strictly to declarative '.'-terminated sentences with zero quotes or dashes.
        ReadOnlySpan<char> sentSpan = map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length).TrimEnd();
        if (!sentSpan.EndsWith(".", StringComparison.Ordinal) || sentSpan.ContainsAny(":;\u2013\u2014"))
            return;

        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        if (slots.Length < 3 || slots.Any(s => s.Quoted || s.Word.CanonicalText == "ли"))
            return;

        int siIdx = -1;
        for (int i = 1; i < slots.Length - 1; i++)
        {
            if (peripheral[i] || slots[i].Word.CanonicalText != "си") continue;
            if (siIdx >= 0) return;
            siIdx = i;
        }
        if (siIdx < 1 || !IsExactWord(slots[siIdx]) || !map.AreSpaceBridged(siIdx - 1, siIdx, ct))
            return;

        // Verify zero other finite, modal/future particle, or active -л participle anchors exist anywhere in the sentence
        // (protecting 3rd-person renarratives with ethical/possessive 'си' like "Опитните работници си завършили новия мост.").
        for (int k = 0; k < slots.Length; k++)
        {
            if (k == siIdx) continue;
            WordSlot sk = slots[k];
            if (sk.Word.CanonicalText is "ще" or "да" or "нека" or "дано" or "би" or "бих" or "бихме" or "бихте" or "биха" ||
                HasFiniteReading(sk) ||
                sk.Word.Readings.Any(r => r.Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive
                }))
                return;
        }

        int[] siVerbReadings = Enumerable.Range(0, slots[siIdx].Word.Readings.Count)
            .Where(r => TextPredicateLinkAnalyzer.CopularCarrier(slots[siIdx].Word.Readings[r]) &&
                        Guard.IsPersonalPredicate(slots[siIdx].Word.Readings[r].Features))
            .Order()
            .ToArray();
        if (siVerbReadings.Length == 0 || agreement.Any(a => a.PredicateTokenIndex == slots[siIdx].Word.OriginalIndex))
            return;

        // Validate pre-'си' subject span [0 .. siIdx - 1]: no commas or prepositions.
        for (int k = 0; k < siIdx; k++)
        {
            if (peripheral[k] || IsActivePreposition(slots[k].Word, slots[k].Active) ||
                k > 0 && !map.AreSpaceBridged(k - 1, k, ct))
                return;
        }
        int[] preIdx = Enumerable.Range(0, siIdx).ToArray();
        bool[] preGov = new bool[siIdx];
        List<NominalChunk> preNps = CollectNominalChunks(map, preIdx, 0, siIdx - 1, preGov, ct);
        if (preNps is not [{ Coherent: true, ShortMasculineSingular: false, TemporalAdjunct: false, HeadReadings.Length: > 0 } subj] ||
            subj.FirstWord != 0 || subj.LastWord != siIdx - 1 ||
            HasVocativeReading(slots[subj.HeadWord]) ||
            IsAdverbialTimeOrMeasureHead(slots[subj.HeadWord], subj) ||
            IsPersonPlaceHomograph(map.Analysis, slots[subj.HeadWord].Word.OriginalIndex))
            return;

        bool validPreSubject = subj.PronounSubject
            ? slots[subj.HeadWord].Word.CanonicalText is "аз" or "той" or "тя" or "то" or "ние" or "вие" or "те"
            : subj.FirstWord < subj.HeadWord &&
              IsDefiniteOrProperSubjectCandidate(slots, subj) &&
              subj.HeadReadings.All(r => slots[subj.HeadWord].Word.Readings[r].Features.Article == MorphologicalArticle.Indefinite);
        if (!validPreSubject)
            return;

        // Skip optional degree intensifier right after 'си'.
        int predIdx = siIdx + 1;
        while (predIdx < slots.Length && !peripheral[predIdx] &&
               map.AreSpaceBridged(predIdx - 1, predIdx, ct) &&
               slots[predIdx].Word.CanonicalText is "много" or "изключително" or "съвсем" or "напълно" or "твърде" or "особено" or "доста")
        {
            predIdx++;
        }
        if (predIdx >= slots.Length || peripheral[predIdx] || !map.AreSpaceBridged(predIdx - 1, predIdx, ct))
            return;

        WordSlot predSlot = slots[predIdx];
        if (!IsExactWord(predSlot) || predSlot.Word.Token.Text.Length == 0 || char.IsUpper(predSlot.Word.Token.Text[0]))
            return;
        int[] rawPred = NonPlaceholderReadings(predSlot.Word, sentenceInitial: false);
        if (rawPred.Length == 0 || !rawPred.All(r =>
            predSlot.Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Article: MorphologicalArticle.Indefinite } ||
            predSlot.Word.Readings[r].Features is
            {
                PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                VerbForm: MorphologicalVerbForm.Participle,
                ParticipleKind: MorphologicalParticipleKind.PastPassive,
                Article: null or MorphologicalArticle.Indefinite
            }))
            return;

        // [P1-I8] Ensure predSlot does not precede an ungoverned noun/pronoun phrase.
        bool inPrep = false;
        for (int scan = predIdx + 1; scan < slots.Length; scan++)
        {
            if (peripheral[scan]) continue;
            if (map.HasCommaBetween(scan - 1, scan)) break;
            string sc = slots[scan].Word.CanonicalText;
            if (IsActivePreposition(slots[scan].Word, slots[scan].Active)) { inPrep = true; continue; }
            if (sc is "и" or "или" or "много" or "доста" or "съвсем" or "напълно" || IsPossessiveClitic(sc))
                continue;
            int[] sr = slots[scan].Active.Length > 0 ? slots[scan].Active : NonPlaceholderReadings(slots[scan].Word, sentenceInitial: false);
            if (sr.Length > 0 && sr.All(r => slots[scan].Word.Readings[r].Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective } or
                { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle } ||
                Guard.TryGetDeterminerAxes(slots[scan].Word, slots[scan].Word.Readings[r], out _) ||
                slots[scan].Word.Readings[r].Category == Category.Number))
                continue;
            if (sr.Any(r => slots[scan].Word.Readings[r].Features.PartOfSpeech is
                MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.ProperNoun))
            {
                if (inPrep) { inPrep = false; continue; }
                return;
            }
            inPrep = false;
        }

        AddSubjectRelation(slots, subj, SingleTokenComplex(siIdx, siVerbReadings), siVerbReadings, canonicalKey, agreement,
            subj.PronounSubject ? "pronoun_subject_clause_mismatch" : "nominal_subject_clause_mismatch",
            mismatch: true, subjectProven: true);
    }

    private static bool HasUnexpectedSymbol(
        TextAnalysis analysis,
        int firstTok,
        int lastTok,
        IReadOnlyList<(int Start, int End)>? protectedSpans = null) =>
        Guard.HasUnexpectedNonPictographSymbol(analysis, firstTok, lastTok, protectedSpans);

    private static bool HasPictographBrokenPreverbalClitic(
        TextAnalysis analysis, WordSlot[] slots, int[] idx, int fromPos, int toPos, bool[] governed, List<NominalChunk> preNps)
    {
        for (int p = fromPos; p <= toPos; p++)
        {
            if (p < 0 || p + 1 >= idx.Length || governed[p]) continue;
            int w = idx[p];
            if (preNps.Any(np => np.FirstWord <= w && w <= np.LastWord)) continue;
            if (!HasDecorativePictographBetween(analysis, slots[w].Word.OriginalIndex, slots[idx[p + 1]].Word.OriginalIndex))
                continue;
            if (slots[w].Word.CanonicalText == "се" ||
                IsVerbalClitic(slots[w], p > fromPos ? slots[idx[p - 1]] : null, out _))
                return true;
        }
        return false;
    }

    private static bool HasDecorativePictographBetween(TextAnalysis analysis, int leftTokenIndex, int rightTokenIndex)
    {
        for (int t = leftTokenIndex + 1; t < rightTokenIndex; t++)
            if (Guard.IsDecorativePictograph(analysis.Tokens[t]))
                return true;
        return false;
    }

    private static string Norm(string value) => BulgarianTextNormalizer.NormalizeKey(value);
}
