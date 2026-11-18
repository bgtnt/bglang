using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BgLang.Contracts;
using Category = BgLang.Core.ContextLanes.Category;
using Guard = BgLang.Core.ContextEvidenceGuards;
using ReadingView = BgLang.Core.ContextLanes.ReadingView;
using TokenView = BgLang.Core.ContextLanes.TokenView;

namespace BgLang.Core;

internal enum SimpleClauseScopeDisposition
{
    OutsideScope = 0,
    OwnedSelected = 1,
    OwnedMismatch = 2,
    OwnedButAmbiguous = 3,
    OwnedOpenFrameUnassessed = 4,
    OwnedIncompleteReading = 5,
    OwnedTruncated = 6
}

/// <summary>
/// Internal R3 subject–finite-verb agreement solver over shared SentenceClauseMap and SyntheticFinite VerbalComplex.
/// Evaluates complete token reading inventories (never Slots.Active), distinguishes OutsideScope from inconclusive
/// owned states, and feeds the single ContextJointSelection selector without duplicate discovery/promotion.
/// </summary>
internal static class SimpleClauseAgreementResolver
{
    internal const string DisjointAmbiguousMismatchCode = "disjoint_subject_predicate_mismatch_ambiguous";
    private const int MaxSearchSteps = 8192;

    internal delegate (int? OwnerOrdinal, OwnerVerbValency Valency) VerbReadingAuthority(
        TokenView verbWord,
        int completeReadingIndex,
        CancellationToken ct);

    internal sealed record ClauseOutcome(
        int ClauseId,
        bool IsMatrix,
        SimpleClauseScopeDisposition Disposition,
        ContextOverlayCandidate? SelectionCandidate,
        ContextOverlayCandidate? DiagnosticCandidate,
        int SubjectFirstWordIndex,
        int SubjectHeadWordIndex,
        int PredicateWordIndex,
        int HeuristicSvoSubjectFirstWordIndex = -1,
        int HeuristicSvoSubjectHeadWordIndex = -1);

    internal sealed record SentenceOutcome(
        bool HasOwnedClause,
        bool MatrixOwned,
        SimpleClauseScopeDisposition MatrixDisposition,
        IReadOnlySet<int> OwnedClauseIds,
        ContextOverlayInput? MatrixOverlayInput,
        IReadOnlyList<ContextOverlayCandidate> NonMatrixCandidates,
        int SelectedSubjectFirstWordIndex,
        int SelectedSubjectHeadWordIndex,
        int SelectedPredicateWordIndex,
        int HeuristicSvoSubjectFirstWordIndex,
        int HeuristicSvoSubjectHeadWordIndex);

    private enum SupportKind : byte
    {
        SubjectPronoun,
        FiniteVerb,
        ImperativeVerb,
        Noun,
        Adjective,
        TransparentAdverb
    }

    private enum MasculineArticleKind : byte
    {
        None = 0,
        Full = 1,
        Short = 2
    }

    private readonly record struct ReadingSupport(
        int CompleteReadingIndex,
        SupportKind Kind,
        MorphologicalPerson? Person,
        MorphologicalNumber? Number,
        MorphologicalGender? Gender,
        MorphologicalArticle? Article,
        MasculineArticleKind MasculineArticle,
        bool IsAnimate,
        int? OwnerOrdinal,
        OwnerVerbValency Valency);

    private sealed record NominalConstituent(
        int Id,
        bool IsPronoun,
        int StartPos,
        int EndPos,
        int FirstSentenceWordIndex,
        int HeadSentenceWordIndex,
        int HeadTokenIndex,
        int[] HeadReadingIndices,
        MorphologicalPerson Person,
        MorphologicalNumber Number,
        MasculineArticleKind MasculineArticle,
        bool IsDefinite,
        bool IsAnimate,
        bool HasZeroLocalViolation);

    private readonly record struct ParticipantBinding(
        int ConstituentId,
        int FirstSentenceWordIndex,
        int HeadSentenceWordIndex,
        int HeadTokenIndex,
        MorphologicalPerson Person,
        MorphologicalNumber Number,
        MasculineArticleKind MasculineArticle,
        bool IsDefinite,
        bool IsAnimate)
    {
        internal static ParticipantBinding Empty => new(-1, -1, -1, -1, default, default, MasculineArticleKind.None, false, false);
        internal bool IsEmpty => ConstituentId < 0;
    }

    private readonly record struct PredicateBinding(
        int Pos,
        int SentenceWordIndex,
        int TokenIndex,
        int CompleteReadingIndex,
        bool IsImperative,
        MorphologicalPerson Person,
        MorphologicalNumber Number,
        int OwnerKey,
        OwnerVerbValency Valency)
    {
        internal static PredicateBinding Empty => new(-1, -1, -1, -1, false, default, default, int.MinValue, OwnerVerbValency.OpenUnattested);
        internal bool IsEmpty => Pos < 0;
    }

    private readonly record struct TrellisStateKey(
        int Pos,
        ParticipantBinding Subject,
        ParticipantBinding DirectObject,
        PredicateBinding Predicate);

    internal static SentenceOutcome ResolveSentence(
        SentenceClauseMap map,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        VerbReadingAuthority? verbAuthority,
        CancellationToken ct,
        VerbFrameCatalog? verbFrames = null)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TokenView> words = map.Words;
        if (words.Count < 2 || words.Any(w => w.UsesGraph) || map.HasUnexpectedSymbol ||
            !HasValidSentenceEdges(map.Analysis, map.Sentence, words))
            return OutsideSentence();

        bool hasIncompleteNonPeripheral = false;
        for (int i = 0; i < words.Count; i++)
        {
            if (!map.Peripheral[i] && !words[i].Complete)
            {
                hasIncompleteNonPeripheral = true;
                break;
            }
        }

        ReadOnlySpan<char> sentenceSpan = map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length);
        if (sentenceSpan.ContainsAny(":;\u2014"))
            return OutsideSentence();

        if (hasIncompleteNonPeripheral)
            return ResolveIncompleteSentence(map, canonicalKey, verbAuthority, ct, verbFrames);

        if (!map.AllExactOrDegree || map.Clauses.Count == 0)
            return OutsideSentence();

        List<ClauseOutcome> outcomes = new(map.Clauses.Count);
        HashSet<int> ownedIds = [];
        foreach (ClauseSpan clause in map.Clauses)
        {
            ct.ThrowIfCancellationRequested();
            ClauseOutcome outcome = ResolveClause(map, clause, canonicalKey, verbAuthority, ct, verbFrames);
            outcomes.Add(outcome);
            if (outcome.Disposition != SimpleClauseScopeDisposition.OutsideScope)
                ownedIds.Add(clause.ClauseId);
        }

        return BuildSentenceOutcome(outcomes, ownedIds);

        static SentenceOutcome BuildSentenceOutcome(List<ClauseOutcome> clauseOutcomes, HashSet<int> ownedClauseIds)
        {
            if (ownedClauseIds.Count == 0)
                return OutsideSentence();

            ClauseOutcome? matrix = clauseOutcomes.FirstOrDefault(o => o.IsMatrix);
            bool matrixOwned = matrix is not null && matrix.Disposition != SimpleClauseScopeDisposition.OutsideScope;
            SimpleClauseScopeDisposition matrixDisp = matrix?.Disposition ?? SimpleClauseScopeDisposition.OutsideScope;

            ContextOverlayInput? matrixOverlay = null;
            int selSubjFirst = -1, selSubjHead = -1, selPredWord = -1;
            int heurSubjFirst = -1, heurSubjHead = -1;
            List<ContextOverlayCandidate> extraCandidates = [];

            if (matrixOwned && matrix is not null)
            {
                List<ContextOverlayCandidate> matrixCands = [];
                if (matrix.SelectionCandidate is not null)
                {
                    matrixCands.Add(matrix.SelectionCandidate);
                    selSubjFirst = matrix.SubjectFirstWordIndex;
                    selSubjHead = matrix.SubjectHeadWordIndex;
                    selPredWord = matrix.PredicateWordIndex;
                    heurSubjFirst = matrix.HeuristicSvoSubjectFirstWordIndex;
                    heurSubjHead = matrix.HeuristicSvoSubjectHeadWordIndex;
                }
                if (matrix.DiagnosticCandidate is not null)
                    matrixCands.Add(matrix.DiagnosticCandidate);

                bool truncated = matrix.Disposition == SimpleClauseScopeDisposition.OwnedTruncated;
                matrixOverlay = new ContextOverlayInput(
                    HasTargetTopology: true,
                    Truncated: truncated,
                    TruncationEvidenceCode: truncated ? "reading_limit_reached" : null,
                    Candidates: matrixCands,
                    LocalRoleCandidates: [],
                    RoleAmbiguousTokenIndices: []);
            }

            foreach (ClauseOutcome o in clauseOutcomes)
            {
                if (o.Disposition == SimpleClauseScopeDisposition.OutsideScope) continue;
                if (ReferenceEquals(o, matrix)) continue;
                if (o.DiagnosticCandidate is not null)
                    extraCandidates.Add(o.DiagnosticCandidate);
            }

            return new SentenceOutcome(
                HasOwnedClause: true,
                MatrixOwned: matrixOwned,
                MatrixDisposition: matrixDisp,
                OwnedClauseIds: ownedClauseIds,
                MatrixOverlayInput: matrixOverlay,
                NonMatrixCandidates: extraCandidates,
                SelectedSubjectFirstWordIndex: selSubjFirst,
                SelectedSubjectHeadWordIndex: selSubjHead,
                SelectedPredicateWordIndex: selPredWord,
                HeuristicSvoSubjectFirstWordIndex: heurSubjFirst,
                HeuristicSvoSubjectHeadWordIndex: heurSubjHead);
        }

        static SentenceOutcome OutsideSentence() => new(
            HasOwnedClause: false,
            MatrixOwned: false,
            MatrixDisposition: SimpleClauseScopeDisposition.OutsideScope,
            OwnedClauseIds: new HashSet<int>(),
            MatrixOverlayInput: null,
            NonMatrixCandidates: [],
            SelectedSubjectFirstWordIndex: -1,
            SelectedSubjectHeadWordIndex: -1,
            SelectedPredicateWordIndex: -1,
            HeuristicSvoSubjectFirstWordIndex: -1,
            HeuristicSvoSubjectHeadWordIndex: -1);

        static SentenceOutcome ResolveIncompleteSentence(
            SentenceClauseMap m,
            Func<IReadOnlyList<ContextOverlayRoleSelection>, string> keyFn,
            VerbReadingAuthority? authority,
            CancellationToken token,
            VerbFrameCatalog? frames)
        {
            if (!SentenceClauseMap.LeadingDialogueOrSpace(m.Analysis.Text, m.Sentence.Start, m.Words[0].Token.Start))
                return OutsideSentence();

            List<int> rem = [];
            for (int i = 0; i < m.Words.Count; i++)
                if (!m.Peripheral[i])
                    rem.Add(i);

            if (rem.Count < 2)
                return OutsideSentence();

            WordSlot[] s = m.Slots;
            List<(ClauseBoundary Boundary, int[] WordIndices, int BodyStartPos, bool SubordinatorStart)> segments = [];
            int segStart = 0;
            bool firstIsSub = IsSubordinatorOpener(s[rem[0]].Word);
            ClauseBoundary currentBoundary = firstIsSub
                ? new ClauseBoundary(ClauseBoundaryKind.Subordinator, -1, rem[0], rem[0], -1, false)
                : ClauseBoundary.Initial();
            int currentBodyStart = firstIsSub ? 1 : 0;

            for (int p = 1; p < rem.Count; p++)
            {
                token.ThrowIfCancellationRequested();
                int prevW = rem[p - 1], curW = rem[p];
                WordSlot curSlot = s[curW];
                if (curSlot.Quoted) continue;
                string canon = curSlot.Word.CanonicalText;
                bool commaBefore = m.HasCommaBetween(prevW, curW);

                bool coordBreak = commaBefore && SentenceClauseMap.IsCoordinatingConjunction(curSlot);
                bool subBreak = commaBefore && IsSubordinatorOpener(curSlot.Word);
                bool preposedSubComma = segStart == 0 && firstIsSub && commaBefore;
                int rightStop = NextClauseBreakPos(m, s, rem, p + 1) - 1;
                bool prevIsPureAdjective = s[prevW].Word.Complete && s[prevW].Word.Readings.Count > 0 &&
                    s[prevW].Word.Readings.All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective);
                bool syndeticBreak = !commaBefore && canon is "и" or "или" &&
                    p - segStart >= 2 && rightStop >= p + 1 && !prevIsPureAdjective &&
                    RangeHasVerbOrIncomplete(s, rem, segStart, p - 1) &&
                    RangeHasVerbOrIncomplete(s, rem, p + 1, rightStop);

                if (coordBreak || subBreak || preposedSubComma || syndeticBreak)
                {
                    segments.Add((currentBoundary, rem.GetRange(segStart, p - segStart).ToArray(), currentBodyStart,
                        segStart == 0 && firstIsSub));
                    segStart = p;
                    if (coordBreak || syndeticBreak)
                    {
                        currentBoundary = new ClauseBoundary(ClauseBoundaryKind.Coordinator, -1, curW, curW, commaBefore ? 0 : -1, false);
                        currentBodyStart = 1;
                    }
                    else if (subBreak)
                    {
                        currentBoundary = new ClauseBoundary(ClauseBoundaryKind.Subordinator, -1, curW, curW, 0, false);
                        currentBodyStart = 1;
                    }
                    else
                    {
                        currentBoundary = ClauseBoundary.Initial();
                        currentBodyStart = 0;
                    }
                }
            }
            segments.Add((currentBoundary, rem.GetRange(segStart, rem.Count - segStart).ToArray(), currentBodyStart,
                segStart == 0 && firstIsSub));

            int matrixIdx = segments.FindIndex(seg => !seg.SubordinatorStart);
            if (matrixIdx < 0) matrixIdx = 0;

            List<ClauseOutcome> incOutcomes = new(segments.Count);
            HashSet<int> incOwnedIds = [];
            for (int cid = 0; cid < segments.Count; cid++)
            {
                token.ThrowIfCancellationRequested();
                var seg = segments[cid];
                ClauseSpan spanClause = new(cid, seg.Boundary, seg.WordIndices, seg.BodyStartPos, cid == matrixIdx, false);
                bool clauseHasIncomplete = false;
                for (int k = seg.BodyStartPos; k < seg.WordIndices.Length; k++)
                {
                    if (!s[seg.WordIndices[k]].Word.Complete)
                    {
                        clauseHasIncomplete = true;
                        break;
                    }
                }

                ClauseOutcome outcome = clauseHasIncomplete
                    ? EvaluateIncompleteClause(m, spanClause, segments.Count, authority, frames, token)
                    : ResolveClause(m, spanClause, keyFn, authority, token, frames);
                incOutcomes.Add(outcome);
                if (outcome.Disposition != SimpleClauseScopeDisposition.OutsideScope)
                    incOwnedIds.Add(cid);
            }

            return BuildSentenceOutcome(incOutcomes, incOwnedIds);

            static int NextClauseBreakPos(SentenceClauseMap m, WordSlot[] slots, List<int> rem, int from)
            {
                for (int q = from; q < rem.Count; q++)
                {
                    WordSlot qs = slots[rem[q]];
                    if (qs.Quoted) continue;
                    bool comma = q > 0 && m.HasCommaBetween(rem[q - 1], rem[q]);
                    if (comma && (SentenceClauseMap.IsCoordinatingConjunction(qs) || IsSubordinatorOpener(qs.Word)) ||
                        !comma && qs.Word.CanonicalText is "и" or "или" && q > from &&
                        RangeHasVerbOrIncomplete(slots, rem, from, q - 1))
                    {
                        return q;
                    }
                }
                return rem.Count;
            }

            static ClauseOutcome EvaluateIncompleteClause(
                SentenceClauseMap m,
                ClauseSpan clause,
                int clauseCount,
                VerbReadingAuthority? verbAuthority,
                VerbFrameCatalog? verbFrames,
                CancellationToken ct)
            {
                int[] idx = clause.WordIndices;
                int bodyStart = clause.BodyStartPos;
                int bodyLen = idx.Length - bodyStart;
                if (bodyLen < 2 || clause.Boundary.Kind is not (ClauseBoundaryKind.SentenceInitial or ClauseBoundaryKind.Coordinator))
                    return Outside(clause);
                if (clauseCount == 1 && !m.HasTerminalPunctuation && bodyLen < 3)
                    return Outside(clause);

                WordSlot[] slots = m.Slots;
                int verbCarrierPos = -1;
                int firstIncompletePos = -1;
                for (int pos = 0; pos < bodyLen; pos++)
                {
                    ct.ThrowIfCancellationRequested();
                    int w = idx[bodyStart + pos];
                    WordSlot slot = slots[w];
                    if (slot.Quoted || slot.HyphenatedCompoundNominal is not null || slot.ComparativeGraphModifier)
                        return Outside(clause);
                    if (pos > 0 && (!m.AreClauseWordBridged(idx[bodyStart + pos - 1], w, ct) ||
                                    m.HasCommaBetween(idx[bodyStart + pos - 1], w)))
                    {
                        return Outside(clause);
                    }
                    TokenView word = slot.Word;
                    if (IsOutsideScopeSurface(word.CanonicalText, pos))
                        return Outside(clause);
                    if (!word.Complete && firstIncompletePos < 0)
                        firstIncompletePos = pos;
                    if (word.Readings.Any(r => Guard.IsPersonalPredicate(r.Features) &&
                            r.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative) &&
                        verbCarrierPos < 0)
                    {
                        verbCarrierPos = pos;
                    }
                    if (word.Complete)
                    {
                        bool sentenceInitial = w == 0 && SentenceClauseMap.IsSentenceInitialWord(m.Analysis, m.Sentence, w, word);
                        if (HasLinkingVerbReading(word) ||
                            IsOutsideScopeWord(m.Analysis, word, pos, bodyLen, isCarrier: true, sentenceInitial, ct))
                        {
                            return Outside(clause);
                        }
                    }
                }

                int carrierPos = verbCarrierPos >= 0 ? verbCarrierPos : firstIncompletePos;
                if (carrierPos < 0)
                    return Outside(clause);

                TokenView carrierWord = slots[idx[bodyStart + carrierPos]].Word;
                (ReadingView Row, int Index)[] carrierFinite = carrierWord.Readings
                    .Select((r, rIdx) => (Row: r, Index: rIdx))
                    .Where(x => Guard.IsPersonalPredicate(x.Row.Features) && x.Row.Features.VerbForm == MorphologicalVerbForm.Finite)
                    .ToArray();
                bool carrierHasThirdPerson = carrierFinite.Any(x => x.Row.Features.Person == MorphologicalPerson.Third);
                bool exclusivelyFirstOrSecondCarrier = carrierWord.Complete && carrierFinite.Length > 0 &&
                    carrierFinite.All(x => x.Row.Features.Person is MorphologicalPerson.First or MorphologicalPerson.Second);
                bool carrierHasAttestedTransitive = carrierWord.Complete && carrierFinite.Any(x =>
                    ResolveVerbReadingValency(carrierWord, x.Index, x.Row, verbAuthority, verbFrames, ct).Valency == OwnerVerbValency.AttestedTransitiveAny);

                bool hasPronounSubject = false;
                bool hasPreverbalPronounSubject = false;
                bool hasPreverbalNonShortNominal = false;
                bool hasCaseMarkedMasculine = false;
                bool[] hasAdjectiveAtPos = new bool[bodyLen];
                List<int> nonCarrierNounPositions = [];

                for (int pos = 0; pos < bodyLen; pos++)
                {
                    TokenView w = slots[idx[bodyStart + pos]].Word;
                    bool isPron = w.Readings.Any(r => IsSubjectPronounReading(w, r, out _, out _));
                    if (isPron)
                    {
                        hasPronounSubject = true;
                        if (pos < carrierPos)
                            hasPreverbalPronounSubject = true;
                    }

                    if (pos == carrierPos) continue;

                    if (!w.Complete && w.Readings.Count == 0)
                    {
                        nonCarrierNounPositions.Add(pos);
                        if (pos < carrierPos)
                            hasPreverbalNonShortNominal = true;
                        continue;
                    }

                    bool sawNonShortNoun = false;
                    bool sawAnyNoun = false;
                    foreach (ReadingView r in w.Readings)
                    {
                        MorphologicalFeatures f = r.Features;
                        if (f.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun &&
                            f.Number != MorphologicalNumber.Count)
                        {
                            sawAnyNoun = true;
                            MasculineArticleKind masc = ClassifyMasculineArticle(w.CanonicalText, r);
                            if (masc != MasculineArticleKind.Short)
                                sawNonShortNoun = true;
                            if (masc == MasculineArticleKind.Full ||
                                masc == MasculineArticleKind.Short && (pos > carrierPos || !carrierHasThirdPerson))
                            {
                                hasCaseMarkedMasculine = true;
                            }
                        }
                        else if (f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(f))
                        {
                            hasAdjectiveAtPos[pos] = true;
                            MasculineArticleKind adjMasc = ClassifyMasculineArticle(w.CanonicalText, r);
                            if (adjMasc == MasculineArticleKind.Full ||
                                adjMasc == MasculineArticleKind.Short && (pos > carrierPos || !carrierHasThirdPerson))
                            {
                                hasCaseMarkedMasculine = true;
                            }
                        }
                    }

                    if (sawAnyNoun)
                    {
                        nonCarrierNounPositions.Add(pos);
                        if (pos < carrierPos && sawNonShortNoun)
                            hasPreverbalNonShortNominal = true;
                    }
                }

                // Preverbal short-article masculine singular before 3p belongs to SubjectFull article domain.
                if (carrierHasThirdPerson && !hasPreverbalPronounSubject &&
                    Enumerable.Range(0, carrierPos).Any(p =>
                    {
                        TokenView pw = slots[idx[bodyStart + p]].Word;
                        return pw.Readings.Any(r => ClassifyMasculineArticle(pw.CanonicalText, r) == MasculineArticleKind.Short);
                    }))
                {
                    return Outside(clause);
                }

                // Match complete Coordinator overt preverbal subject gate (:868-873): reject subjectless shared-VP ('и тръгнахме вчера.').
                if (clause.Boundary.Kind == ClauseBoundaryKind.Coordinator &&
                    !hasPreverbalPronounSubject &&
                    !hasPreverbalNonShortNominal)
                {
                    return Outside(clause);
                }

                bool hasTwoDisjointNonCarrierNominals = false;
                for (int i = 0; i < nonCarrierNounPositions.Count && !hasTwoDisjointNonCarrierNominals; i++)
                {
                    int p1 = nonCarrierNounPositions[i];
                    for (int j = i + 1; j < nonCarrierNounPositions.Count; j++)
                    {
                        int p2 = nonCarrierNounPositions[j];
                        bool subsumedSingleNp = (p2 < carrierPos || p1 > carrierPos) &&
                            Enumerable.Range(p1, p2 - p1).All(k => hasAdjectiveAtPos[k]);
                        if (!subsumedSingleNp)
                        {
                            hasTwoDisjointNonCarrierNominals = true;
                            break;
                        }
                    }
                }

                // Match complete F0..F5 family gate (:770-788): do not acquire unowned deferred single-NP 3p clauses.
                if (nonCarrierNounPositions.Count > 0 &&
                    !hasPronounSubject &&
                    !hasCaseMarkedMasculine &&
                    !hasTwoDisjointNonCarrierNominals &&
                    !exclusivelyFirstOrSecondCarrier &&
                    !carrierHasAttestedTransitive)
                {
                    return Outside(clause);
                }

                return new ClauseOutcome(clause.ClauseId, clause.Matrix, SimpleClauseScopeDisposition.OwnedTruncated,
                    null, null, -1, -1, -1);
            }

            static bool IsSubordinatorOpener(TokenView word) =>
                BulgarianFunctionWords.HasAny(word.CanonicalText,
                    FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.RelativeWord) ||
                word.CanonicalText is "чия" or "чие" or "чии" or "какъвто" or "каквато" or "каквото" or "каквито" ||
                word.Complete && word.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition);

            static bool RangeHasVerbOrIncomplete(WordSlot[] slots, List<int> rem, int from, int to)
            {
                for (int i = from; i <= to; i++)
                {
                    TokenView w = slots[rem[i]].Word;
                    if (!w.Complete || w.Readings.Any(r => r.Category == Category.Verb))
                        return true;
                }
                return false;
            }
        }
    }

    private static bool HasValidSentenceEdges(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> sentenceWords)
    {
        int prevWordEnd = -1;
        int nextWordStart = int.MaxValue;
        int sentenceEnd = sentence.Start + sentence.Length;
        for (int i = 0; i < analysis.Tokens.Count; i++)
        {
            TokenAnalysis tok = analysis.Tokens[i];
            if (tok.Kind != TokenKind.Word) continue;
            int tokEnd = tok.Start + tok.Length;
            if (tokEnd <= sentence.Start && tokEnd > prevWordEnd)
                prevWordEnd = tokEnd;
            else if (tok.Start >= sentenceEnd && tok.Start < nextWordStart)
                nextWordStart = tok.Start;
        }

        if (prevWordEnd >= 0 && !ContainsSentenceBoundaryChar(analysis.Text.AsSpan(prevWordEnd, sentence.Start - prevWordEnd)))
            return false;
        int lastInSentEnd = sentenceWords[^1].Token.Start + sentenceWords[^1].Token.Length;
        if (nextWordStart < int.MaxValue && !ContainsSentenceBoundaryChar(analysis.Text.AsSpan(lastInSentEnd, nextWordStart - lastInSentEnd)))
            return false;
        return true;

        static bool ContainsSentenceBoundaryChar(ReadOnlySpan<char> gap)
        {
            foreach (char ch in gap)
                if (SentenceBoundaryDetector.IsSentenceTerminator(ch) || ch is '\r' or '\n')
                    return true;
            return false;
        }
    }

    internal static ClauseOutcome ResolveClause(
        SentenceClauseMap map,
        ClauseSpan clause,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> canonicalKey,
        VerbReadingAuthority? verbAuthority,
        CancellationToken ct,
        VerbFrameCatalog? verbFrames = null)
    {
        ct.ThrowIfCancellationRequested();
        int[] idx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        int bodyLen = idx.Length - bodyStart;
        if (bodyLen <= 0)
            return Outside(clause);

        if (clause.Boundary.Kind is not (ClauseBoundaryKind.SentenceInitial or ClauseBoundaryKind.Coordinator))
            return Outside(clause);

        // Shared-subject VP coordination ("Той влезе и излязоха.") has a 1-word coordinated VP without its own arguments.
        if (clause.Boundary.Kind == ClauseBoundaryKind.Coordinator && bodyLen < 2)
            return Outside(clause);

        // Two-word open editor prefix without terminal punctuation ("Работи учителят") stays outside closed R3 scope.
        if (map.Clauses.Count == 1 && !map.HasTerminalPunctuation && bodyLen < 3)
            return Outside(clause);

        if (clause.ClauseId == 0 && !SentenceClauseMap.LeadingDialogueOrSpace(
                map.Analysis.Text, map.Sentence.Start, map.Words[0].Token.Start))
        {
            return Outside(clause);
        }

        WordSlot[] slots = map.Slots;
        for (int p = bodyStart; p < idx.Length; p++)
        {
            WordSlot s = slots[idx[p]];
            if (s.Quoted || s.HyphenatedCompoundNominal is not null || s.ComparativeGraphModifier)
                return Outside(clause);
            if (p > bodyStart && (!map.AreClauseWordBridged(idx[p - 1], idx[p], ct) ||
                                  map.HasCommaBetween(idx[p - 1], idx[p])))
            {
                return Outside(clause);
            }
            if (!s.Word.Complete)
                return new(clause.ClauseId, clause.Matrix, SimpleClauseScopeDisposition.OwnedTruncated, null, null, -1, -1, -1);
        }

        if (VerbalComplexBinder.BindClauseMainComplex(map, clause, ct) is not { } vc)
            return Outside(clause);

        if (vc.Kind != VerbalComplexKind.SyntheticFinite ||
            vc.CopularCarrier ||
            vc.ImpersonalModal ||
            vc.InvariantCarrier ||
            vc.HasReflexiveSe ||
            vc.HasActiveParticipleTail ||
            vc.HasDaComplement ||
            vc.Dependents.Length > 0 ||
            vc.BridgeWordIndices.Length > 0 ||
            vc.FutureParticleWordIndex >= 0 ||
            vc.DaParticleWordIndex >= 0 ||
            vc.PostDaOrAuxClitics.FirstWordIndex >= 0 ||
            vc.PreCarrierClitics.InterrogativeLiWordIndex >= 0 ||
            vc.PreCarrierClitics.ReflexiveWordIndex >= 0 ||
            vc.PreCarrierClitics.DativeCliticWordIndex >= 0 ||
            vc.PreCarrierClitics.AccusativeCliticWordIndex >= 0)
        {
            return Outside(clause);
        }

        int carrierPos = -1;
        for (int p = 0; p < bodyLen; p++)
        {
            if (idx[bodyStart + p] == vc.FiniteCarrierWordIndex)
            {
                carrierPos = p;
                break;
            }
        }
        if (carrierPos < 0)
            return Outside(clause);

        // Inspect complete map.Words[w].Readings across every word in the clause body.
        TokenView carrierWord = map.Words[vc.FiniteCarrierWordIndex];
        bool allowsPerfPres = vc.AllowsPerfectivePresent(map, clause);

        // Linking/semi-copular carriers stay with PredicativeGrammarEvidence / ClauseFusion copular rules.
        if (HasLinkingVerbReading(carrierWord))
            return Outside(clause);

        List<ReadingSupport>[] supportsByPos = new List<ReadingSupport>[bodyLen];
        bool hasIncompleteBranch = false;
        bool hasPredicateOrFeatureUncertainty = false;
        bool carrierHasNumeralHomograph = false;

        for (int pos = 0; pos < bodyLen; pos++)
        {
            ct.ThrowIfCancellationRequested();
            int w = idx[bodyStart + pos];
            TokenView word = map.Words[w];
            bool sentenceInitial = w == 0 && SentenceClauseMap.IsSentenceInitialWord(map.Analysis, map.Sentence, w, word);

            if (IsOutsideScopeWord(map.Analysis, word, pos, bodyLen, pos == carrierPos, sentenceInitial, ct))
                return Outside(clause);

            List<ReadingSupport> wordSupports = [];
            supportsByPos[pos] = wordSupports;

            bool hasReal = word.Readings.Any(r => !Guard.IsSourcePlaceholder(word, r));
            int[] rawFiniteOnWord = Enumerable.Range(0, word.Readings.Count)
                .Where(r =>
                {
                    ReadingView rv = word.Readings[r];
                    return rv.Features.VerbForm == MorphologicalVerbForm.Finite &&
                           Guard.IsPersonalPredicate(rv.Features) &&
                           SentenceClauseMap.IsCleanOrBenignLegacyVerb(rv);
                })
                .ToArray();
            HashSet<int>? indicativeFiniteSet = null;
            if (rawFiniteOnWord.Length > 0 && !allowsPerfPres)
            {
                int[] nonPerfPres = rawFiniteOnWord.Where(r =>
                {
                    MorphologicalFeatures vf = word.Readings[r].Features;
                    return !(vf.Tense == MorphologicalTense.Present && vf.DeclaredAspect == MorphologicalVerbAspect.Perfective);
                }).ToArray();
                if (nonPerfPres.Length == 0)
                {
                    if (pos == carrierPos)
                        return Outside(clause);
                }
                else
                {
                    indicativeFiniteSet = [.. nonPerfPres];
                }
            }

            for (int r = 0; r < word.Readings.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                ReadingView row = word.Readings[r];
                if (hasReal && Guard.IsSourcePlaceholder(word, row) ||
                    Guard.IsUnwrittenCapitalizedReading(word, row, sentenceInitial) ||
                    Guard.IsSuppressedPresentActiveNounHomograph(word, row) ||
                    Guard.IsSyncreticVocativeCompanion(word, row))
                {
                    continue;
                }

                MorphologicalFeatures f = row.Features;

                // Clause-initial Wackernagel enclitic / particle alternatives on 'ти' / 'те' (e.g. short dative/possessive 'Ти',
                // short accusative 'Те', discourse particle 'ти'/'те') cannot be hosted as non-nominative at pos == 0.
                if (pos == 0 && word.CanonicalText is "ти" or "те" &&
                    (f.IsShortForm == true || f.PartOfSpeech == MorphologicalPartOfSpeech.Particle))
                {
                    continue;
                }

                // Count-form companion on singular short masculine definite nouns (e.g. 'доклада', 'учителя')
                // requires a preceding numeral/quantifier, which is absent in unquantified simple clauses.
                if (f.Number == MorphologicalNumber.Count &&
                    word.Readings.Any(other =>
                        other.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Singular } &&
                        Guard.IsShortMasculineDefiniteReading(word.CanonicalText, other)))
                {
                    continue;
                }

                if (f.HasConflictingFeatures ||
                    (f.HasUnrecognizedFeatures && !SentenceClauseMap.IsCleanOrBenignLegacyVerb(row)))
                {
                    hasIncompleteBranch = true;
                    hasPredicateOrFeatureUncertainty = true;
                    continue;
                }

                if (IsSubjectPronounReading(word, row, out MorphologicalPerson pronPerson, out MorphologicalNumber pronNumber))
                {
                    wordSupports.Add(new(r, SupportKind.SubjectPronoun, pronPerson, pronNumber, f.Gender,
                        MorphologicalArticle.Indefinite, MasculineArticleKind.None, IsAnimate: true, null, OwnerVerbValency.OpenUnattested));
                    continue;
                }

                if (Guard.IsPersonalPredicate(f) && f.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative)
                {
                    if (f.VerbForm == MorphologicalVerbForm.Finite &&
                        indicativeFiniteSet is not null && !indicativeFiniteSet.Contains(r))
                    {
                        continue;
                    }

                    (int? ownerOrd, OwnerVerbValency valency) = ResolveVerbReadingValency(word, r, row, verbAuthority, verbFrames, ct);
                    SupportKind vKind = f.VerbForm == MorphologicalVerbForm.Imperative
                        ? SupportKind.ImperativeVerb
                        : SupportKind.FiniteVerb;
                    wordSupports.Add(new(r, vKind, f.Person, f.Number, f.Gender, null,
                        MasculineArticleKind.None, IsAnimate: false, ownerOrd, valency));
                    continue;
                }

                if (f.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun &&
                    f.Number != MorphologicalNumber.Count &&
                    (Guard.HasNominalAxes(f) || f.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun && f.Number is MorphologicalNumber.Singular or MorphologicalNumber.Plural))
                {
                    MorphologicalNumber num = f.Number ?? MorphologicalNumber.Singular;
                    MasculineArticleKind masc = ClassifyMasculineArticle(word.CanonicalText, row);
                    bool animate = ClauseFusionAnalyzer.IsAnimateForFusion(row) || Guard.IsEpiceneHumanNounLemma(row.Lemma);
                    wordSupports.Add(new(r, SupportKind.Noun, MorphologicalPerson.Third, num, f.Gender, f.Article,
                        masc, animate, null, OwnerVerbValency.OpenUnattested));
                    continue;
                }

                if (f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(f))
                {
                    MasculineArticleKind masc = ClassifyMasculineArticle(word.CanonicalText, row);
                    wordSupports.Add(new(r, SupportKind.Adjective, null, f.Number, f.Gender, f.Article,
                        masc, IsAnimate: false, null, OwnerVerbValency.OpenUnattested));
                    continue;
                }

                if (row.SourceAdverb && f.PartOfSpeech == MorphologicalPartOfSpeech.Adverb ||
                    word.CanonicalText == "не" && f.PartOfSpeech == MorphologicalPartOfSpeech.Particle)
                {
                    wordSupports.Add(new(r, SupportKind.TransparentAdverb, null, null, null, null,
                        MasculineArticleKind.None, IsAnimate: false, null, OwnerVerbValency.OpenUnattested));
                    continue;
                }

                if (pos == carrierPos && f.PartOfSpeech == MorphologicalPartOfSpeech.Numeral)
                    carrierHasNumeralHomograph = true;

                hasIncompleteBranch = true;
            }

            if (wordSupports.Count == 0 && !hasIncompleteBranch)
                hasIncompleteBranch = true;
        }

        // Competing adjacent same-(Person, Number) finite verbs (e.g. synthetic 'Вие[2pl] четете[2pl]!')
        // could form an unmodeled serial verb pair / competing predicate, whereas disjoint verbs ('Вие[3sg] работя[1sg]') cannot.
        if (HasCompetingSerialVerbAlternative(supportsByPos, carrierPos))
        {
            hasIncompleteBranch = true;
            hasPredicateOrFeatureUncertainty = true;
        }

        // Definite plural quantifier NPs ('Малкото книги...', 'Немалкото книги...') stay with existing quantifier owners.
        if (HasDefinitePluralQuantifierPhrase(map, idx, bodyStart, bodyLen, supportsByPos, ct))
            return Outside(clause);

        // Build pronoun & nominal constituents across [0 .. bodyLen - 1] without dropping disagreeing modifiers.
        List<NominalConstituent> constituents = BuildConstituents(map, idx, bodyStart, bodyLen, supportsByPos, ct, out bool truncatedConstituents);
        if (truncatedConstituents)
            return new(clause.ClauseId, clause.Matrix, SimpleClauseScopeDisposition.OwnedTruncated, null, null, -1, -1, -1);

        bool carrierHasThirdPerson = supportsByPos[carrierPos].Any(s =>
            s.Kind == SupportKind.FiniteVerb && s.Person == MorphologicalPerson.Third);
        bool hasPreverbalPronounSubject = Enumerable.Range(0, carrierPos)
            .Any(p => supportsByPos[p].Any(s => s.Kind == SupportKind.SubjectPronoun));

        // Coordinated clauses without an overt preverbal clause-local subject ('Учителят дойде и тръгнахме вчера.')
        // belong to shared-subject VP coordination rather than independent simple-clause R3.
        if (clause.Boundary.Kind == ClauseBoundaryKind.Coordinator &&
            !hasPreverbalPronounSubject &&
            !constituents.Any(c => !c.IsPronoun && c.EndPos < carrierPos && c.MasculineArticle != MasculineArticleKind.Short))
        {
            return Outside(clause);
        }

        // Preverbal short-article masculine singular NPs before 3rd-person verbs belong to the SubjectFull article domain
        // (e.g. 'Учителя видя ученика.', 'Учителя поздрави учениците.', 'Човека пее.').
        if (carrierHasThirdPerson && !hasPreverbalPronounSubject &&
            constituents.Any(c => !c.IsPronoun && c.EndPos < carrierPos && c.MasculineArticle == MasculineArticleKind.Short))
        {
            return Outside(clause);
        }

        // Prospective structural family ownership (Q1 + A1/v3 M3): evaluated strictly BEFORE checking person/number agreement or trellis verdict,
        // identically across matching, mismatching, and locally R1-broken members.
        // Own F0 (0 constituents), F1 (SubjectPronoun), F2 (Full/Short masculine singular), F3 (exclusively 1st/2nd-person carrier),
        // F4 (positive owner-bound AttestedTransitiveAny carrier), and F5 (two disjoint nominal constituents);
        // defer unclosed-valency 3rd-person single bare/feminine/neuter/plural nominal clauses.
        bool hasPronounSubjectCandidate = Enumerable.Range(0, bodyLen)
            .Any(p => supportsByPos[p].Any(s => s.Kind == SupportKind.SubjectPronoun));
        bool hasCaseMarkedMasculineConstituent = constituents.Any(c =>
            !c.IsPronoun && c.MasculineArticle is MasculineArticleKind.Full or MasculineArticleKind.Short);
        static bool DisjointFromCarrier(NominalConstituent c, int vPos) =>
            c.EndPos < vPos || c.StartPos > vPos;

        bool hasTwoDisjointNominalConstituents = false;
        for (int i = 0; i < constituents.Count && !hasTwoDisjointNominalConstituents; i++)
        {
            NominalConstituent first = constituents[i];
            if (first.IsPronoun || !DisjointFromCarrier(first, carrierPos)) continue;
            for (int j = i + 1; j < constituents.Count; j++)
            {
                NominalConstituent second = constituents[j];
                if (!second.IsPronoun &&
                    DisjointFromCarrier(second, carrierPos) &&
                    first.EndPos < second.StartPos &&
                    !constituents.Any(u => !u.IsPronoun && u.StartPos <= first.StartPos && u.EndPos >= second.EndPos))
                {
                    hasTwoDisjointNominalConstituents = true;
                    break;
                }
            }
        }
        ReadingSupport[] carrierFiniteSupports = supportsByPos[carrierPos]
            .Where(s => s.Kind == SupportKind.FiniteVerb)
            .ToArray();
        bool exclusivelyFirstOrSecondCarrier = carrierFiniteSupports.Length > 0 &&
            carrierFiniteSupports.All(s => s.Person is MorphologicalPerson.First or MorphologicalPerson.Second);
        bool carrierHasAttestedTransitive = carrierFiniteSupports.Any(s =>
            s.Valency == OwnerVerbValency.AttestedTransitiveAny);

        if (constituents.Count > 0 &&
            !hasPronounSubjectCandidate &&
            !hasCaseMarkedMasculineConstituent &&
            !hasTwoDisjointNominalConstituents &&
            !exclusivelyFirstOrSecondCarrier &&
            !carrierHasAttestedTransitive)
        {
            return Outside(clause);
        }

        // Solve position-DAG trellis across [0 .. bodyLen].
        List<NominalConstituent>[] startingAt = new List<NominalConstituent>[bodyLen];
        for (int p = 0; p < bodyLen; p++) startingAt[p] = [];
        foreach (NominalConstituent c in constituents)
            startingAt[c.StartPos].Add(c);

        HashSet<TrellisStateKey>[] chart = new HashSet<TrellisStateKey>[bodyLen + 1];
        for (int p = 0; p <= bodyLen; p++) chart[p] = [];
        chart[0].Add(new TrellisStateKey(0, ParticipantBinding.Empty, ParticipantBinding.Empty, PredicateBinding.Empty));

        int steps = 0;
        for (int pos = 0; pos < bodyLen; pos++)
        {
            ct.ThrowIfCancellationRequested();
            if (chart[pos].Count == 0) continue;

            List<ReadingSupport> wordSupports = supportsByPos[pos];
            bool hasAdverbOrNeg = wordSupports.Any(s => s.Kind == SupportKind.TransparentAdverb);
            ReadingSupport[] verbs = wordSupports
                .Where(s => s.Kind is SupportKind.FiniteVerb or SupportKind.ImperativeVerb)
                .ToArray();
            List<NominalConstituent> starting = startingAt[pos];

            foreach (TrellisStateKey state in chart[pos])
            {
                if (++steps > MaxSearchSteps)
                    return new(clause.ClauseId, clause.Matrix, SimpleClauseScopeDisposition.OwnedTruncated, null, null, -1, -1, -1);

                if (hasAdverbOrNeg)
                    chart[pos + 1].Add(state with { Pos = pos + 1 });

                if (state.Predicate.IsEmpty && pos == carrierPos)
                {
                    foreach (ReadingSupport v in verbs)
                    {
                        PredicateBinding pb = new(
                            pos,
                            idx[bodyStart + pos],
                            map.Words[idx[bodyStart + pos]].OriginalIndex,
                            v.CompleteReadingIndex,
                            IsImperative: v.Kind == SupportKind.ImperativeVerb,
                            v.Person!.Value,
                            v.Number!.Value,
                            v.OwnerOrdinal ?? int.MinValue,
                            v.Valency);
                        chart[pos + 1].Add(state with { Pos = pos + 1, Predicate = pb });
                    }
                }

                foreach (NominalConstituent c in starting)
                {
                    ParticipantBinding part = new(
                        c.Id,
                        c.FirstSentenceWordIndex,
                        c.HeadSentenceWordIndex,
                        c.HeadTokenIndex,
                        c.Person,
                        c.Number,
                        c.MasculineArticle,
                        c.IsDefinite,
                        c.IsAnimate);

                    // Short-article masculine singular NPs ('новия доклад') cannot occupy nominative Subject.
                    if (state.Subject.IsEmpty && c.MasculineArticle != MasculineArticleKind.Short)
                        chart[c.EndPos + 1].Add(state with { Pos = c.EndPos + 1, Subject = part });

                    // Nominative personal pronouns and full-article masculine singular NPs ('учителят') cannot occupy DirectObject.
                    if (!c.IsPronoun && state.DirectObject.IsEmpty && c.MasculineArticle != MasculineArticleKind.Full)
                        chart[c.EndPos + 1].Add(state with { Pos = c.EndPos + 1, DirectObject = part });
                }
            }
        }

        List<TrellisStateKey> closedZeroViolation = [];
        List<TrellisStateKey> openZeroViolation = [];
        List<TrellisStateKey> violatingStates = [];

        foreach (TrellisStateKey state in chart[bodyLen])
        {
            if (state.Predicate.IsEmpty) continue;

            // Imperative 2p verbs cannot take an overt 1p or 3p Subject ('*Аз работи!').
            if (state.Predicate.IsImperative && !state.Subject.IsEmpty &&
                state.Subject.Person != MorphologicalPerson.Second)
            {
                continue;
            }

            // Check DirectObject licensing against positive-only owner-bound verb valency.
            bool hasObject = !state.DirectObject.IsEmpty;
            bool openAuthority = hasObject && state.Predicate.Valency != OwnerVerbValency.AttestedTransitiveAny;

            // If carrier has a competing Numeral homograph ('три' 73907) and there is no overt Subject ('Новите програми три вчера.'),
            // do not license a closed pro-drop verbal parse over the plural NP + numeral alternative.
            if (carrierHasNumeralHomograph && state.Subject.IsEmpty && hasObject &&
                state.DirectObject.Number == MorphologicalNumber.Plural)
            {
                openAuthority = true;
            }

            bool r3Compatible = state.Subject.IsEmpty || SubjectAgreesWithPredicate(state.Subject, state.Predicate);
            if (r3Compatible)
            {
                if (openAuthority) openZeroViolation.Add(state);
                else closedZeroViolation.Add(state);
            }
            else if (!state.Predicate.IsImperative)
            {
                if (openAuthority)
                    hasIncompleteBranch = true;
                else
                    violatingStates.Add(state);
            }
        }

        if (hasPredicateOrFeatureUncertainty)
            return new(clause.ClauseId, clause.Matrix, SimpleClauseScopeDisposition.OwnedIncompleteReading, null, null, -1, -1, -1);

        // Row T2a / T2b: at least one closed zero-violation interpretation (with >= 1 Finite reading).
        if (closedZeroViolation.Any(s => !s.Predicate.IsImperative))
        {
            TrellisStateKey[] allValid = closedZeroViolation.Concat(openZeroViolation).ToArray();

            int[] selectedPredReadings = closedZeroViolation
                .Select(s => s.Predicate.CompleteReadingIndex)
                .Distinct()
                .Order()
                .ToArray();

            List<ContextOverlayRoleSelection> roles =
            [
                new(ContextOverlayRole.Predicate, carrierWord.OriginalIndex, carrierWord.Token.Start, carrierWord.Token.Length,
                    selectedPredReadings, ["complete_exact_reading", "clause_fusion_finite_predicate"])
            ];

            int subjFirstWord = -1, subjHeadWord = -1;
            int heurSubjFirst = -1, heurSubjHead = -1;
            int[] distinctSubjHeads = allValid
                .Select(s => s.Subject.IsEmpty ? -1 : s.Subject.HeadTokenIndex)
                .Distinct()
                .ToArray();

            if (distinctSubjHeads is [>= 0])
            {
                int subjHeadTok = distinctSubjHeads[0];
                NominalConstituent[] coherentSubjConstituents = closedZeroViolation
                    .Where(s => s.Subject.HeadTokenIndex == subjHeadTok)
                    .Select(s => constituents[s.Subject.ConstituentId])
                    .Where(c => c.HasZeroLocalViolation)
                    .DistinctBy(c => c.Id)
                    .ToArray();

                // Never promote a locally R1-broken NP ('Новата учител обясни урока.') as a clean Selected Subject.
                if (coherentSubjConstituents.Length > 0)
                {
                    ParticipantBinding subjBind = closedZeroViolation.First(s => s.Subject.HeadTokenIndex == subjHeadTok).Subject;
                    int[] subjReadings = coherentSubjConstituents
                        .SelectMany(c => c.HeadReadingIndices)
                        .Distinct()
                        .Order()
                        .ToArray();
                    TokenView subjWord = map.Words[subjBind.HeadSentenceWordIndex];
                    string dirCode = subjBind.HeadSentenceWordIndex < vc.FiniteCarrierWordIndex
                        ? "preverbal_nominal_reading"
                        : "postverbal_nominal_reading";
                    roles.Add(new(ContextOverlayRole.Subject, subjHeadTok, subjWord.Token.Start, subjWord.Token.Length,
                        subjReadings, ["complete_exact_reading", "clause_fusion_subject", dirCode]));

                    subjFirstWord = coherentSubjConstituents.Min(c => c.FirstSentenceWordIndex);
                    subjHeadWord = subjBind.HeadSentenceWordIndex;
                }
            }
            else
            {
                // Q2: Context proof consensus remains strictly Predicate-only when both SVO and OVS are morphosyntactically valid.
                // Record unmarked SVO extent (preverbal coherent definite NP + postverbal coherent indefinite NP) solely for optional
                // SyntaxRoleCalculator BasicSubject (Basis.ExactCategory) under IncludeAdditionalRoleHeuristics=true.
                NominalConstituent[] unmarkedSvoSubjects = closedZeroViolation
                    .Where(s => !s.Predicate.IsImperative && !s.Subject.IsEmpty && !s.DirectObject.IsEmpty &&
                                s.Subject.HeadSentenceWordIndex < s.Predicate.SentenceWordIndex &&
                                s.DirectObject.FirstSentenceWordIndex > s.Predicate.SentenceWordIndex &&
                                constituents[s.Subject.ConstituentId] is { HasZeroLocalViolation: true, IsDefinite: true } &&
                                constituents[s.DirectObject.ConstituentId] is { HasZeroLocalViolation: true, IsDefinite: false })
                    .Select(s => constituents[s.Subject.ConstituentId])
                    .DistinctBy(c => c.Id)
                    .ToArray();
                int[] heurHeads = unmarkedSvoSubjects.Select(c => c.HeadSentenceWordIndex).Distinct().ToArray();
                if (heurHeads is [>= 0])
                {
                    heurSubjFirst = unmarkedSvoSubjects.Min(c => c.FirstSentenceWordIndex);
                    heurSubjHead = heurHeads[0];
                }
            }

            roles = roles
                .OrderBy(r => r.Start)
                .ThenBy(r => r.Role == ContextOverlayRole.Predicate ? 0 : 1)
                .ToList();

            var selectionCandidate = new ContextOverlayCandidate(
                PredicateTokenIndex: carrierWord.OriginalIndex,
                Selectable: true,
                AuthorityLane: ClauseFusionAnalyzer.SelectionLane,
                Score: 100,
                CanonicalKey: canonicalKey(roles),
                Roles: roles,
                EvidenceCodes:
                [
                    "complete_exact_reading",
                    "clause_fusion_matrix_selected",
                    "subject_predicate_agreement_compatible"
                ]);

            return new(
                clause.ClauseId,
                clause.Matrix,
                SimpleClauseScopeDisposition.OwnedSelected,
                selectionCandidate,
                DiagnosticCandidate: null,
                subjFirstWord,
                subjHeadWord,
                vc.FiniteCarrierWordIndex,
                heurSubjFirst,
                heurSubjHead);
        }

        // Compatible 2p Imperative ('Ти спи.') vetoes a finite R3 mismatch without promoting Imperative to Selected finite predicate.
        if (closedZeroViolation.Count > 0)
            return new(clause.ClauseId, clause.Matrix, SimpleClauseScopeDisposition.OwnedIncompleteReading, null, null, -1, -1, -1);

        // Row T3a: zero closed valid parses, but >= 1 open-frame zero-violation parse ('Новата програма боксувахме вчера.').
        if (openZeroViolation.Count > 0)
        {
            SimpleClauseScopeDisposition disp = carrierHasNumeralHomograph
                ? SimpleClauseScopeDisposition.OwnedIncompleteReading
                : SimpleClauseScopeDisposition.OwnedOpenFrameUnassessed;
            return new(clause.ClauseId, clause.Matrix, disp, null, null, -1, -1, -1);
        }

        // Row T3b / T1: incomplete reading branch or zero closed witnesses.
        if (hasIncompleteBranch || violatingStates.Count == 0)
        {
            SimpleClauseScopeDisposition disp = violatingStates.Count > 0 &&
                supportsByPos[carrierPos].Any(s => s.Kind == SupportKind.FiniteVerb &&
                    s.Valency == OwnerVerbValency.OpenUnattested) &&
                constituents.Any(c => !c.IsPronoun && c.MasculineArticle != MasculineArticleKind.Full)
                    ? SimpleClauseScopeDisposition.OwnedOpenFrameUnassessed
                    : SimpleClauseScopeDisposition.OwnedIncompleteReading;
            return new(clause.ClauseId, clause.Matrix, disp, null, null, -1, -1, -1);
        }

        // Closed authority with only R3-violating interpretations: distinguish T4 (single universal locus) vs T5 (disjoint locus).
        int[] distinctViolatingSubjects = violatingStates
            .Select(s => s.Subject.HeadTokenIndex)
            .Distinct()
            .Order()
            .ToArray();
        int[] participatingPredReadings = violatingStates
            .Select(s => s.Predicate.CompleteReadingIndex)
            .Distinct()
            .Order()
            .ToArray();

        if (distinctViolatingSubjects.Length == 1)
        {
            // Row T4: Universal single-locus R3 mismatch.
            int subjTok = distinctViolatingSubjects[0];
            NominalConstituent[] subjConstituents = violatingStates
                .Select(s => constituents[s.Subject.ConstituentId])
                .DistinctBy(c => c.Id)
                .ToArray();
            int[] subjReadings = subjConstituents
                .SelectMany(c => c.HeadReadingIndices)
                .Distinct()
                .Order()
                .ToArray();
            TokenView subjWord = map.Words[subjConstituents[0].HeadSentenceWordIndex];
            string projectedLane = ProjectMismatchAuthorityLane(map, bodyStart, bodyLen, carrierPos, subjConstituents, ct);

            List<ContextOverlayRoleSelection> diagRoles =
            [
                new(ContextOverlayRole.Predicate, carrierWord.OriginalIndex, carrierWord.Token.Start, carrierWord.Token.Length,
                    participatingPredReadings, ["complete_exact_reading", "clause_fusion_finite_readings"]),
                new(ContextOverlayRole.Subject, subjTok, subjWord.Token.Start, subjWord.Token.Length,
                    subjReadings, ["complete_exact_reading", "clause_fusion_subject"])
            ];
            diagRoles = diagRoles.OrderBy(r => r.Start).ToList();

            var diagCandidate = new ContextOverlayCandidate(
                PredicateTokenIndex: carrierWord.OriginalIndex,
                Selectable: false,
                AuthorityLane: projectedLane,
                Score: 0,
                CanonicalKey: canonicalKey(diagRoles),
                Roles: diagRoles,
                EvidenceCodes:
                [
                    "complete_exact_reading",
                    ContextLanes.ProductiveGraphMismatch,
                    "simple_clause_r3_universal_mismatch"
                ]);

            return new(
                clause.ClauseId,
                clause.Matrix,
                SimpleClauseScopeDisposition.OwnedMismatch,
                SelectionCandidate: null,
                DiagnosticCandidate: diagCandidate,
                SubjectFirstWordIndex: -1,
                SubjectHeadWordIndex: -1,
                PredicateWordIndex: vc.FiniteCarrierWordIndex);
        }

        // Row T5: Disjoint R3 mismatch across >= 2 candidate subjects ('Майката поздравиха дъщерята.').
        // Emit zero single-locus TextDiagnostics (no ProductiveGraphMismatch), retain ambiguous candidate for ExplainGrammar.
        List<ContextOverlayRoleSelection> ambigRoles =
        [
            new(ContextOverlayRole.Predicate, carrierWord.OriginalIndex, carrierWord.Token.Start, carrierWord.Token.Length,
                participatingPredReadings, ["complete_exact_reading", "clause_fusion_finite_readings"])
        ];
        foreach (int subjTok in distinctViolatingSubjects)
        {
            NominalConstituent[] sc = violatingStates
                .Where(s => s.Subject.HeadTokenIndex == subjTok)
                .Select(s => constituents[s.Subject.ConstituentId])
                .DistinctBy(c => c.Id)
                .ToArray();
            int[] sRefs = sc.SelectMany(c => c.HeadReadingIndices).Distinct().Order().ToArray();
            TokenView sWord = map.Words[sc[0].HeadSentenceWordIndex];
            ambigRoles.Add(new(ContextOverlayRole.Subject, subjTok, sWord.Token.Start, sWord.Token.Length,
                sRefs, ["complete_exact_reading", "clause_fusion_ambiguous_subject_candidate"]));
        }
        ambigRoles = ambigRoles.OrderBy(r => r.Start).ToList();

        var ambigCandidate = new ContextOverlayCandidate(
            PredicateTokenIndex: carrierWord.OriginalIndex,
            Selectable: false,
            AuthorityLane: ClauseFusionAnalyzer.AgreementLane,
            Score: 0,
            CanonicalKey: canonicalKey(ambigRoles),
            Roles: ambigRoles,
            EvidenceCodes:
            [
                "complete_exact_reading",
                DisjointAmbiguousMismatchCode
            ]);

        return new(
            clause.ClauseId,
            clause.Matrix,
            SimpleClauseScopeDisposition.OwnedButAmbiguous,
            SelectionCandidate: null,
            DiagnosticCandidate: ambigCandidate,
            SubjectFirstWordIndex: -1,
            SubjectHeadWordIndex: -1,
            PredicateWordIndex: vc.FiniteCarrierWordIndex);
    }

    private static ClauseOutcome Outside(ClauseSpan c) =>
        new(c.ClauseId, c.Matrix, SimpleClauseScopeDisposition.OutsideScope, null, null, -1, -1, -1);

    private static bool SubjectAgreesWithPredicate(ParticipantBinding subject, PredicateBinding predicate)
    {
        if (subject.Person == predicate.Person && subject.Number == predicate.Number)
            return true;
        // Bulgarian inclusive plural ('Колегите обсъдихме новата програма.'):
        // plural animate 3rd-person NP agrees with 1pl / 2pl finite predicate.
        return ! predicate.IsImperative &&
               subject.Person == MorphologicalPerson.Third &&
               subject.Number == MorphologicalNumber.Plural &&
               subject.IsAnimate &&
               predicate.Number == MorphologicalNumber.Plural &&
               predicate.Person is MorphologicalPerson.First or MorphologicalPerson.Second;
    }

    private static List<NominalConstituent> BuildConstituents(
        SentenceClauseMap map,
        int[] clauseWordIndices,
        int bodyStart,
        int bodyLen,
        List<ReadingSupport>[] supportsByPos,
        CancellationToken ct,
        out bool truncated)
    {
        truncated = false;
        List<NominalConstituent> result = [];
        int steps = 0;

        for (int pos = 0; pos < bodyLen; pos++)
        {
            int w = clauseWordIndices[bodyStart + pos];
            TokenView word = map.Words[w];
            ReadingSupport[] pronSupports = supportsByPos[pos]
                .Where(s => s.Kind == SupportKind.SubjectPronoun)
                .ToArray();
            foreach (var grp in pronSupports.GroupBy(p => (Person: p.Person!.Value, Number: p.Number!.Value)))
            {
                if (++steps > MaxSearchSteps) { truncated = true; return result; }
                result.Add(new NominalConstituent(
                    Id: result.Count,
                    IsPronoun: true,
                    StartPos: pos,
                    EndPos: pos,
                    FirstSentenceWordIndex: w,
                    HeadSentenceWordIndex: w,
                    HeadTokenIndex: word.OriginalIndex,
                    HeadReadingIndices: grp.Select(x => x.CompleteReadingIndex).Order().ToArray(),
                    Person: grp.Key.Person,
                    Number: grp.Key.Number,
                    MasculineArticle: MasculineArticleKind.None,
                    IsDefinite: true,
                    IsAnimate: true,
                    HasZeroLocalViolation: true));
            }
        }

        for (int headPos = 0; headPos < bodyLen; headPos++)
        {
            ReadingSupport[] headNouns = supportsByPos[headPos]
                .Where(s => s.Kind == SupportKind.Noun)
                .ToArray();
            if (headNouns.Length == 0) continue;

            int headWordIdx = clauseWordIndices[bodyStart + headPos];
            TokenView headWord = map.Words[headWordIdx];

            for (int startPos = headPos; startPos >= 0; startPos--)
            {
                if (startPos < headPos)
                {
                    int leftWordIdx = clauseWordIndices[bodyStart + startPos];
                    int rightWordIdx = clauseWordIndices[bodyStart + startPos + 1];
                    if (!map.AreHorizontalWordBridged(leftWordIdx, rightWordIdx, ct))
                        break;
                    if (!supportsByPos[startPos].Any(s => s.Kind == SupportKind.Adjective))
                        break;
                }

                int firstWordIdx = clauseWordIndices[bodyStart + startPos];

                foreach (var headGrp in headNouns.GroupBy(h => (
                    Number: h.Number!.Value,
                    Gender: h.Number == MorphologicalNumber.Singular ? h.Gender : null,
                    h.Article,
                    h.MasculineArticle,
                    h.IsAnimate)))
                {
                    if (++steps > MaxSearchSteps) { truncated = true; return result; }
                    int[] headIndices = headGrp.Select(h => h.CompleteReadingIndex).Order().ToArray();
                    ReadingView repHeadRow = headWord.Readings[headIndices[0]];

                    if (startPos == headPos)
                    {
                        bool headDef = IsArticled(headGrp.Key.Article) ||
                            headWord.Readings[headIndices[0]].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun;
                        result.Add(new NominalConstituent(
                            Id: result.Count,
                            IsPronoun: false,
                            StartPos: startPos,
                            EndPos: headPos,
                            FirstSentenceWordIndex: firstWordIdx,
                            HeadSentenceWordIndex: headWordIdx,
                            HeadTokenIndex: headWord.OriginalIndex,
                            HeadReadingIndices: headIndices,
                            Person: MorphologicalPerson.Third,
                            Number: headGrp.Key.Number,
                            MasculineArticle: headGrp.Key.MasculineArticle,
                            IsDefinite: headDef,
                            IsAnimate: headGrp.Key.IsAnimate,
                            HasZeroLocalViolation: true));
                        continue;
                    }

                    ReadingSupport[] initialAdjs = supportsByPos[startPos]
                        .Where(s => s.Kind == SupportKind.Adjective)
                        .ToArray();
                    bool isMascSgHead = headGrp.Key.Number == MorphologicalNumber.Singular &&
                                        headGrp.Key.Gender == MorphologicalGender.Masculine;
                    IEnumerable<(MasculineArticleKind Masc, bool Def, ReadingSupport[] Initials)> mascBuckets =
                        initialAdjs.GroupBy(a => (
                                Masc: isMascSgHead ? a.MasculineArticle : MasculineArticleKind.None,
                                Def: IsArticled(a.Article)))
                            .Select(g => (g.Key.Masc, g.Key.Def, g.ToArray()));

                    foreach ((MasculineArticleKind phraseMasc, bool phraseDef, ReadingSupport[] initials) in mascBuckets)
                    {
                        if (++steps > MaxSearchSteps) { truncated = true; return result; }
                        bool zeroLocal = !IsArticled(headGrp.Key.Article);
                        for (int mPos = startPos; mPos < headPos; mPos++)
                        {
                            TokenView modWord = map.Words[clauseWordIndices[bodyStart + mPos]];
                            ReadingSupport[] mods = mPos == startPos
                                ? initials
                                : supportsByPos[mPos].Where(s => s.Kind == SupportKind.Adjective).ToArray();
                            bool posHasMatch = mods.Any(m =>
                                !(mPos > startPos && IsArticled(m.Article)) &&
                                Guard.ModifierAgreement(modWord, modWord.Readings[m.CompleteReadingIndex], repHeadRow,
                                    includeAdnominalReadings: false, phraseInitial: mPos == startPos) &&
                                !(headGrp.Key.Number == MorphologicalNumber.Plural &&
                                  Guard.IsDefinitePluralQuantifierAdjective(modWord, modWord.Readings[m.CompleteReadingIndex])));
                            zeroLocal &= posHasMatch;
                        }

                        result.Add(new NominalConstituent(
                            Id: result.Count,
                            IsPronoun: false,
                            StartPos: startPos,
                            EndPos: headPos,
                            FirstSentenceWordIndex: firstWordIdx,
                            HeadSentenceWordIndex: headWordIdx,
                            HeadTokenIndex: headWord.OriginalIndex,
                            HeadReadingIndices: headIndices,
                            Person: MorphologicalPerson.Third,
                            Number: headGrp.Key.Number,
                            MasculineArticle: phraseMasc,
                            IsDefinite: phraseDef,
                            IsAnimate: headGrp.Key.IsAnimate,
                            HasZeroLocalViolation: zeroLocal));
                    }
                }
            }
        }

        return result;
    }

    private static bool HasDefinitePluralQuantifierPhrase(
        SentenceClauseMap map,
        int[] clauseWordIndices,
        int bodyStart,
        int bodyLen,
        List<ReadingSupport>[] supportsByPos,
        CancellationToken ct)
    {
        for (int pos = 0; pos + 1 < bodyLen; pos++)
        {
            TokenView modWord = map.Words[clauseWordIndices[bodyStart + pos]];
            if (modWord.CanonicalText is not ("малкото" or "немалкото")) continue;
            if (!modWord.Readings.Any(r => Guard.IsDefinitePluralQuantifierAdjective(modWord, r))) continue;
            for (int cur = pos + 1; cur < bodyLen; cur++)
            {
                int leftIdx = clauseWordIndices[bodyStart + cur - 1];
                int rightIdx = clauseWordIndices[bodyStart + cur];
                if (!map.AreHorizontalWordBridged(leftIdx, rightIdx, ct)) break;
                if (supportsByPos[cur].Any(s => s.Kind == SupportKind.Noun && s.Number == MorphologicalNumber.Plural))
                    return true;
                if (!supportsByPos[cur].Any(s => s.Kind == SupportKind.Adjective))
                    break;
            }
        }
        return false;
    }

    private static bool IsOutsideScopeSurface(string c, int pos) =>
        BulgarianFunctionWords.HasAny(c, FunctionWordClasses.ReflexiveAccusativeSe | FunctionWordClasses.PronominalVerbalClitic) &&
            c is not ("ти" or "те") ||
        BulgarianFunctionWords.HasAny(c, FunctionWordClasses.DaParticle | FunctionWordClasses.FutureParticle |
            FunctionWordClasses.InterrogativeParticle | FunctionWordClasses.FullObliquePronoun) ||
        c is "би" or "бих" or "бихме" or "бихте" or "биха" or "кого" or "кому" ||
        pos > 0 && c is "ти" or "те" ||
        c is "и" or "или" or "нито" or "ту" or "хем" or "а" or "но" or "обаче" or "че" or "защото" or "ако" or
            "колко" or "няколко" or "много" or "малко" or "толкова" or "доста" or "повече" or "повечето" or "многото" or
            "отколкото" or "колкото" ||
        SentenceClauseMap.IsRelativeWord(c);

    private static bool IsOutsideScopeWord(
        TextAnalysis analysis,
        TokenView word,
        int pos,
        int bodyLen,
        bool isCarrier,
        bool sentenceInitial,
        CancellationToken ct)
    {
        if (IsOutsideScopeSurface(word.CanonicalText, pos))
            return true;
        string c = word.CanonicalText;

        if (Guard.IsPersonPlaceHomograph(analysis, word.OriginalIndex))
            return true;

        bool hasReal = word.Readings.Any(r => !Guard.IsSourcePlaceholder(word, r));
        bool sawNonPlaceholder = false;
        bool allNumerals = true;
        bool sawNoun = false;
        bool allNounsTemporal = true;
        bool sawVocativeOnlyNoun = false;

        for (int r = 0; r < word.Readings.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            ReadingView row = word.Readings[r];
            if (hasReal && Guard.IsSourcePlaceholder(word, row) ||
                Guard.IsUnwrittenCapitalizedReading(word, row, sentenceInitial))
            {
                continue;
            }

            sawNonPlaceholder = true;
            MorphologicalFeatures f = row.Features;

            // Sentence-initial discourse 'То' with Conjunction/Particle homograph ('То пиша...', 'То четат книга.') stays outside R3.
            if (pos == 0 && c == "то" && (row.SourceConjunction || f.PartOfSpeech is MorphologicalPartOfSpeech.Conjunction or MorphologicalPartOfSpeech.Particle))
                return true;

            if (f.PartOfSpeech == MorphologicalPartOfSpeech.Preposition || Guard.IsExactAdposition(word))
                return true;

            if (Guard.TryGetDeterminerAxes(word, row, out _) ||
                Guard.TryGetOrdinalAxes(row, out _) ||
                Guard.IsMeasuredPassiveAttribute(f) ||
                Guard.IsMeasuredPresentActiveAttribute(f) ||
                f.VerbForm is MorphologicalVerbForm.Participle or MorphologicalVerbForm.Converb)
            {
                return true;
            }

            if (f.PartOfSpeech != MorphologicalPartOfSpeech.Numeral)
                allNumerals = false;

            if (f.PartOfSpeech == MorphologicalPartOfSpeech.Noun && !Guard.IsSyncreticVocativeCompanion(word, row))
            {
                if (f.RawTag?.Contains("Звател", StringComparison.Ordinal) == true ||
                    row.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) == true)
                {
                    sawVocativeOnlyNoun = true;
                }
                else
                {
                    sawNoun = true;
                    string normLemma = BulgarianTextNormalizer.NormalizeKey(row.Lemma);
                    if (!Guard.IsTemporalOrMeasureLemma(normLemma))
                        allNounsTemporal = false;
                }
            }
        }

        if (!sawNonPlaceholder)
            return true;
        if (!isCarrier && allNumerals)
            return true;
        if (sawVocativeOnlyNoun && !sawNoun)
            return true;
        if (sawNoun && allNounsTemporal && bodyLen >= 2)
            return true;

        return false;
    }

    private static bool HasLinkingVerbReading(TokenView carrierWord)
    {
        foreach (ReadingView r in carrierWord.Readings)
        {
            if (r.Category != Category.Verb) continue;
            string lemma = BulgarianTextNormalizer.NormalizeKey(r.Lemma);
            if (TextPredicateLinkAnalyzer.CopularLemma(lemma) ||
                lemma is "ставам" or "стана" or "оставам" or "остана" or "изглеждам" or "оказвам" or "окажа")
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasCompetingSerialVerbAlternative(List<ReadingSupport>[] supportsByPos, int carrierPos)
    {
        ReadingSupport[] carrierVerbs = supportsByPos[carrierPos]
            .Where(s => s.Kind is SupportKind.FiniteVerb or SupportKind.ImperativeVerb)
            .ToArray();
        if (carrierVerbs.Length == 0) return false;

        for (int pos = 0; pos < supportsByPos.Length; pos++)
        {
            if (pos == carrierPos || Math.Abs(pos - carrierPos) != 1) continue;
            // Prenominal adjectives with verb homographs ('нови книги') modify an adjacent noun rather than serializing verbs.
            if (supportsByPos[pos].Any(s => s.Kind == SupportKind.Adjective) &&
                pos + 1 < supportsByPos.Length && supportsByPos[pos + 1].Any(s => s.Kind == SupportKind.Noun))
            {
                continue;
            }
            foreach (ReadingSupport other in supportsByPos[pos])
            {
                if (other.Kind is not (SupportKind.FiniteVerb or SupportKind.ImperativeVerb)) continue;
                if (carrierVerbs.Any(cv => cv.Person == other.Person && cv.Number == other.Number))
                    return true;
            }
        }
        return false;
    }

    private static bool IsSubjectPronounReading(
        TokenView word,
        ReadingView row,
        out MorphologicalPerson person,
        out MorphologicalNumber number)
    {
        if (Guard.LicensedSubject(word.CanonicalText, row))
        {
            BulgarianFunctionWords.TryGetPersonalSubjectPronoun(word.CanonicalText, out PersonalSubjectPronounSpec spec);
            person = spec.Person;
            number = spec.Number;
            return true;
        }

        MorphologicalFeatures f = row.Features;
        if (row.Category == Category.Pronoun &&
            row.SourcePersonalPronoun &&
            BulgarianFunctionWords.TryGetPersonalSubjectPronoun(word.CanonicalText, out PersonalSubjectPronounSpec fallbackSpec) &&
            StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(row.Lemma), word.CanonicalText) &&
            f is
            {
                PartOfSpeech: MorphologicalPartOfSpeech.PersonalPronoun,
                HasConflictingFeatures: false,
                HasUnrecognizedFeatures: false,
                Case: null,
                IsShortForm: not true,
                Person: not null,
                Number: MorphologicalNumber.Singular or MorphologicalNumber.Plural
            } &&
            f.Person == fallbackSpec.Person &&
            f.Number == fallbackSpec.Number)
        {
            person = f.Person.Value;
            number = f.Number.Value;
            return true;
        }

        person = default;
        number = default;
        return false;
    }

    private static string ProjectMismatchAuthorityLane(
        SentenceClauseMap map,
        int bodyStart,
        int bodyLen,
        int carrierPos,
        NominalConstituent[] subjConstituents,
        CancellationToken ct)
    {
        IReadOnlyList<TokenView> words = map.Words;
        if (map.Clauses.Count != 1 || bodyStart != 0 || bodyLen != words.Count)
            return ClauseFusionAnalyzer.AgreementLane;

        string text = map.Analysis.Text;
        SentenceSpan sentence = map.Sentence;
        if (!IsWhitespaceSpan(text, sentence.Start, words[0].Token.Start))
            return ClauseFusionAnalyzer.AgreementLane;

        bool consecutiveAll = true;
        for (int i = 0; i + 1 < words.Count; i++)
        {
            if (words[i + 1].OriginalIndex != words[i].OriginalIndex + 1 ||
                !IsWhitespaceSpan(text, words[i].Token.Start + words[i].Token.Length, words[i + 1].Token.Start, nonEmpty: true))
            {
                consecutiveAll = false;
                break;
            }
        }

        if (consecutiveAll && carrierPos == bodyLen - 1 && HasClosedTerminatorTail(text, sentence, words[^1]))
        {
            TokenView pred = words[carrierPos];
            bool cleanClosedVerb = pred.Readings.All(r =>
                !r.Features.HasConflictingFeatures &&
                (r.Category != Category.Verb ||
                 !r.Features.HasUnrecognizedFeatures &&
                 r.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative &&
                 Guard.IsPersonalPredicate(r.Features)));
            if (cleanClosedVerb)
            {
                if (bodyLen == 2 && subjConstituents.All(c => c.IsPronoun && c.StartPos == 0))
                {
                    TokenView w0 = words[0];
                    ReadingView[] pronRows = w0.Readings.Where(r =>
                        r.Category == Category.Pronoun && r.SourcePersonalPronoun &&
                        r.Features.Case is null && r.Features.IsShortForm != true &&
                        BulgarianTextNormalizer.NormalizeKey(r.Lemma) == w0.CanonicalText).ToArray();
                    if (pronRows.Length > 0 &&
                        pronRows.All(r => Guard.LicensedSubject(w0.CanonicalText, r)) &&
                        (!w0.Readings.Any(r => r.Category == Category.Noun) ||
                         ContextLanes.NominalMembers(words, 0, ct) is not null))
                    {
                        return ContextLanes.ExactAgreementLane;
                    }
                }
                else if (subjConstituents.Any(c =>
                             !c.IsPronoun && c.StartPos == 0 && c.EndPos == bodyLen - 2 &&
                             c.MasculineArticle == MasculineArticleKind.Full && c.HasZeroLocalViolation) &&
                         ContextLanes.NominalMembers(words, bodyLen - 2, ct) is not null)
                {
                    return ContextLanes.ExactAgreementLane;
                }
            }
        }

        if (bodyLen >= 3 && carrierPos >= 1 &&
            subjConstituents.All(c => c.IsPronoun && c.StartPos == 0 && c.EndPos == 0) &&
            words.All(IsPrefixReadyWord) &&
            words[0].Readings.All(r => Guard.LicensedSubject(words[0].CanonicalText, r)) &&
            words[carrierPos].Readings.All(r =>
                r.Category == Category.Verb && Guard.IsPersonalPredicate(r.Features) &&
                r.Features.VerbForm == MorphologicalVerbForm.Finite))
        {
            bool prefixBridged = true;
            for (int i = 0; i < carrierPos; i++)
            {
                if (words[i + 1].OriginalIndex != words[i].OriginalIndex + 1 ||
                    !IsWhitespaceSpan(text, words[i].Token.Start + words[i].Token.Length, words[i + 1].Token.Start, nonEmpty: true))
                {
                    prefixBridged = false;
                    break;
                }
            }
            bool pureIntervening = prefixBridged;
            for (int p = 1; pureIntervening && p < carrierPos; p++)
            {
                TokenView mid = words[p];
                bool pureNeg = mid.CanonicalText == "не" &&
                    mid.Readings.All(r => r.Category == Category.Particle && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle);
                bool pureAdv = mid.Readings.All(r =>
                    r.SourceAdverb && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb);
                if (!pureNeg && !pureAdv)
                    pureIntervening = false;
            }
            if (pureIntervening)
                return ContextLanes.ExactPronounPrefixLane;
        }

        return ClauseFusionAnalyzer.AgreementLane;

        static bool IsPrefixReadyWord(TokenView w) =>
            w.Complete && !w.UsesGraph && w.Readings.Count > 0 &&
            w.Readings.All(r => r.Category != Category.Unknown &&
                !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures);

        static bool IsWhitespaceSpan(string s, int start, int end, bool nonEmpty = false)
        {
            if (start > end || nonEmpty && start == end) return false;
            for (int i = start; i < end; i++)
                if (!char.IsWhiteSpace(s[i])) return false;
            return true;
        }

        static bool HasClosedTerminatorTail(string s, SentenceSpan span, TokenView last)
        {
            int from = last.Token.Start + last.Token.Length;
            ReadOnlySpan<char> tail = s.AsSpan(from, span.Start + span.Length - from).Trim();
            bool terminator = false;
            foreach (char ch in tail)
            {
                if (ch is '.' or '!' or '?' or '\u2026') terminator = true;
                else if (ch is not ('"' or '\'' or '\u00bb' or '\u201d' or '\u2019' or ')' or ']' or '}')) return false;
            }
            return terminator;
        }
    }

    private static (int? OwnerOrdinal, OwnerVerbValency Valency) ResolveVerbReadingValency(
        TokenView word,
        int completeReadingIndex,
        ReadingView row,
        VerbReadingAuthority? verbAuthority,
        VerbFrameCatalog? verbFrames,
        CancellationToken ct)
    {
        if (verbAuthority is not null)
            return verbAuthority(word, completeReadingIndex, ct);

        if (verbFrames is not null)
        {
            IReadOnlyList<VerbFrameLexemeEntry> entries = verbFrames.GetByLemma(row.Lemma);
            if (entries.Count > 0)
            {
                return entries.Any(e => e.NonReflexiveDirectObjectFrameRatio is > 0.0)
                    ? (null, OwnerVerbValency.AttestedTransitiveAny)
                    : (null, OwnerVerbValency.OpenUnattested);
            }
        }

        // Package-free synthetic unit-test fallback only (real BulgarianLanguageSession always supplies verbAuthority):
        // positive Transitive fixture tag maps to AttestedTransitiveAny; never manufactures ClosedIntransitive.
        return row.Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive
            ? (null, OwnerVerbValency.AttestedTransitiveAny)
            : (null, OwnerVerbValency.OpenUnattested);
    }

    private static MasculineArticleKind ClassifyMasculineArticle(string canonicalSurface, ReadingView row)
    {
        MorphologicalFeatures f = row.Features;
        if (f.Number != MorphologicalNumber.Singular || f.Gender != MorphologicalGender.Masculine)
            return MasculineArticleKind.None;
        if (Guard.IsShortMasculineDefiniteReading(canonicalSurface, row))
            return MasculineArticleKind.Short;
        if (f.Article is MorphologicalArticle.Full or MorphologicalArticle.Definite)
            return MasculineArticleKind.Full;
        return MasculineArticleKind.None;
    }

    private static bool IsArticled(MorphologicalArticle? article) =>
        article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short;
}
