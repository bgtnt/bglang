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

internal enum VerbalComplexKind
{
    SyntheticFinite,
    Future,
    DaConstruction,
    FutureInPast,
    PersonalModalDa,
    AnalyticPerfect,
    FuturePerfect,
    Conditional,
    CopularPredicative
}

internal readonly record struct CliticCluster(
    int FirstWordIndex,
    int LastWordIndex,
    int NegationWordIndex,
    int InterrogativeLiWordIndex,
    int ReflexiveWordIndex,
    bool HasReflexiveSe,
    bool PostposedSe,
    int DativeCliticWordIndex,
    int AccusativeCliticWordIndex,
    int[] AccusativeCliticReadings)
{
    internal static CliticCluster Empty => new(
        FirstWordIndex: -1,
        LastWordIndex: -1,
        NegationWordIndex: -1,
        InterrogativeLiWordIndex: -1,
        ReflexiveWordIndex: -1,
        HasReflexiveSe: false,
        PostposedSe: false,
        DativeCliticWordIndex: -1,
        AccusativeCliticWordIndex: -1,
        AccusativeCliticReadings: []);
}

internal enum VerbalDependentKind
{
    DaFiniteComplement,
    ActiveParticiple,
    AuxiliaryByl,
    PassiveOrAdjectivalPredicative
}

internal readonly record struct VerbalDependent(
    VerbalDependentKind Kind,
    int WordIndex,
    int[] Readings);

internal enum VerbalInternalMismatchKind
{
    ParticleOrAuxVerbForm,
    ModalDaPersonNumberOrTransitivity,
    CliticClusterCaseOrOrder
}

internal sealed record VerbalInternalMismatch(
    VerbalInternalMismatchKind Kind,
    int TargetSlotIndex,
    int[] TargetReadings,
    int PartnerSlotIndex,
    int[] PartnerReadings);

internal sealed record VerbalComplex(
    VerbalComplexKind Kind,
    int ClauseId,
    int FirstWordIndex,
    int FiniteCarrierWordIndex,
    int LexicalHeadWordIndex,
    int LastWordIndex,
    int DaParticleWordIndex,
    int FutureParticleWordIndex,
    int[] CarrierFiniteReadings,
    int[] LexicalReadings,
    CliticCluster PreCarrierClitics,
    CliticCluster PostDaOrAuxClitics,
    VerbalDependent[] Dependents,
    bool CopularCarrier,
    bool ImpersonalModal,
    bool InvariantCarrier,
    int[] BridgeWordIndices)
{
    internal bool HasFutureMarker =>
        FutureParticleWordIndex >= 0 ||
        Kind is VerbalComplexKind.Future or VerbalComplexKind.FuturePerfect;

    internal bool HasReflexiveSe =>
        PreCarrierClitics.HasReflexiveSe || (!HasDaComplement && PostDaOrAuxClitics.HasReflexiveSe);

    internal bool PostposedSe => PreCarrierClitics.PostposedSe;

    internal int CarrierAccusativeCliticWordIndex =>
        PreCarrierClitics.AccusativeCliticWordIndex >= 0
            ? PreCarrierClitics.AccusativeCliticWordIndex
            : !HasDaComplement ? PostDaOrAuxClitics.AccusativeCliticWordIndex : -1;

    internal int[] CarrierAccusativeCliticReadings =>
        PreCarrierClitics.AccusativeCliticWordIndex >= 0
            ? PreCarrierClitics.AccusativeCliticReadings
            : !HasDaComplement ? PostDaOrAuxClitics.AccusativeCliticReadings : [];

    internal bool HasDaComplement =>
        DaParticleWordIndex >= 0 && Dependents.Any(d => d.Kind == VerbalDependentKind.DaFiniteComplement);

    internal bool HasActiveParticipleTail =>
        Dependents.Any(d => d.Kind == VerbalDependentKind.ActiveParticiple);

    internal bool AllowsPerfectivePresent(SentenceClauseMap map, ClauseSpan clause)
    {
        if (HasFutureMarker || DaParticleWordIndex >= 0 || clause.AllowsPerfectivePresent)
            return true;
        int firstPos = Array.IndexOf(clause.WordIndices, FirstWordIndex);
        return firstPos > 0 &&
               map.Slots[clause.WordIndices[firstPos - 1]].Word.CanonicalText is "да" or "нека";
    }
}

/// <summary>
/// Single linear O(N) recognizer and internal well-formedness checker for Bulgarian verbal complexes
/// and clitic clusters over immutable complete-inventory reading ordinals.
/// </summary>
internal static class VerbalComplexBinder
{
    // Surface licensing only; each caller still proves the copular reading and local dependent.
    internal static bool IsConditionalAuxiliary(string canonical, string? previousCanonical) =>
        canonical is "бих" or "би" or "бихме" or "бихте" or "биха" &&
        !(canonical == "би" && previousCanonical == "може");

    /// <summary>
    /// Binds the primary predicative verbal complex of a segmented ClauseSpan, preserving exact matrix predicate
    /// anchoring (Carrier token for copular/modal/analytic constructions; DaConstruction for PrepositionalSubordinator).
    /// </summary>
    internal static VerbalComplex? BindClauseMainComplex(
        SentenceClauseMap map,
        ClauseSpan clause,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int[] idx = clause.WordIndices;
        int bodyStart = clause.BodyStartPos;
        if (bodyStart >= idx.Length) return null;

        WordSlot[] slots = map.Slots;

        // Prepositional subordinator with "да" ("Без аз да знам", "Преди той изобщо да разбере"):
        // bind the "да" + [clitics] + Vfin complex directly when bodyStart == 1 so raised subjects before "да" participate in clause roles.
        if (clause.Boundary.Kind == ClauseBoundaryKind.PrepositionalSubordinator &&
            bodyStart == 1 &&
            clause.Boundary.IntroducerLastWordIndex >= 0 &&
            slots[clause.Boundary.IntroducerLastWordIndex].Word.CanonicalText == "да")
        {
            int daPos = Array.IndexOf(idx, clause.Boundary.IntroducerLastWordIndex);
            if (daPos >= bodyStart && TryBindDaClauseMain(map, clause.ClauseId, idx, daPos, ct) is { } daMain)
                return daMain;
        }

        for (int p = bodyStart; p < idx.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot s = slots[idx[p]];
            if (s.Quoted) continue;
            if (s.Word.CanonicalText == "да") break;

            int[] finite = SentenceClauseMap.FiniteReadings(s);
            if (finite.Length == 0) continue;

            // Skip clitic/particle tokens that retained a secondary verb reading before an adjacent real verb or active -л participle.
            if (s.Word.CanonicalText is "ще" or "си" or "ми" && p + 1 < idx.Length &&
                (SentenceClauseMap.HasFiniteReading(slots[idx[p + 1]]) ||
                 s.Word.CanonicalText == "ще" && HasUpcomingActiveParticipleInClause(map, idx, p + 1, ct)))
                continue;

            // Scan pre-carrier clitic/negation/future cluster within [bodyStart .. p - 1].
            CliticCluster preClitics = BindPreVerbalCliticsInClause(
                map, idx, bodyStart, p, out int firstWord, out int futureWord, ct);
            if (futureWord >= 0)
            {
                finite = finite.Where(r => s.Word.Readings[r].Features.Tense == MorphologicalTense.Present).ToArray();
                if (finite.Length == 0) return null;
            }

            int carrierWord = idx[p];
            int lastWord = carrierWord;
            int lexicalWord = carrierWord;
            int[] lexicalReadings = finite;
            int daWord = -1;
            CliticCluster postClitics = CliticCluster.Empty;
            List<VerbalDependent> dependents = [];
            List<int> bridges = [];

            bool copular = s.Active.Any(r => TextPredicateLinkAnalyzer.CopularLemma(s.Word.Readings[r].Lemma));
            string canon = s.Word.CanonicalText;
            bool isConditionalAux = copular && IsConditionalAuxiliary(canon,
                p > 0 ? slots[idx[p - 1]].Word.CanonicalText : null);
            bool isFutPastAux = canon is "щях" or "щеше" or "щяхме" or "щяхте" or "щяха";
            bool isPersonalNyama = canon is "нямам" or "нямаш" or "нямаме" or "нямате" or "нямат";
            bool isPersonalMoga = canon is "мога" or "можеш" or "можем" or "можете" or "могат" or
                "можех" or "можехме" or "можехте" or "можеха";
            bool isInvariantNyama = canon is "няма" or "нямаше" && preClitics.AccusativeCliticWordIndex < 0;
            bool isSledvaModal = canon is "следва" or "следваше" && !preClitics.HasReflexiveSe &&
                preClitics.AccusativeCliticWordIndex < 0 && IsFollowedByModalDa(map, idx, p, ct);
            bool isEpistemicMozheModal = canon is "може" or "можеше" && futureWord < 0 &&
                !preClitics.HasReflexiveSe && preClitics.AccusativeCliticWordIndex < 0 &&
                IsFollowedByModalDa(map, idx, p, ct, allowModalInversionI: true);
            bool isInvariantModal = isSledvaModal || isEpistemicMozheModal ||
                canon is "трябва" or "трябваше" or "бива" or "биваше" && preClitics.AccusativeCliticWordIndex < 0;

            VerbalComplexKind kind = futureWord >= 0
                ? VerbalComplexKind.Future
                : VerbalComplexKind.SyntheticFinite;
            bool impersonalModal = IsImpersonalModalCanon(canon) || isSledvaModal;
            bool invariantCarrier = impersonalModal || isEpistemicMozheModal ||
                (isInvariantNyama && IsFollowedByModalDa(map, idx, p, ct));

            // 1. Check inverted ActiveParticiple immediately preceding CopularCarrier ("Писали съм", "Прочел е").
            if (copular && !isConditionalAux && futureWord < 0 && p > bodyStart &&
                idx[p - 1] == carrierWord - 1 && map.AreSpaceBridged(idx[p - 1], carrierWord, ct) &&
                TryGetUnambiguousActiveParticipleReadings(slots[idx[p - 1]].Word, idx[p - 1] == 0, out int[] invPartRows))
            {
                firstWord = Math.Min(firstWord, idx[p - 1]);
                lexicalWord = idx[p - 1];
                lexicalReadings = invPartRows;
                dependents.Add(new(VerbalDependentKind.ActiveParticiple, idx[p - 1], invPartRows));
                kind = VerbalComplexKind.AnalyticPerfect;
            }
            // 2. Check Copular / FuturePerfect / Conditional + [clitics/ли] + ActiveParticiple / Predicative.
            else if (copular)
            {
                if (TryBindCopularTailInClause(map, idx, p, isConditionalAux, futureWord >= 0,
                        out VerbalComplexKind copKind, out CliticCluster auxPostClitics,
                        out List<VerbalDependent> copDeps, out List<int> copBridges, out int copLastWord, ct))
                {
                    kind = copKind;
                    postClitics = auxPostClitics;
                    dependents.AddRange(copDeps);
                    bridges.AddRange(copBridges);
                    VerbalDependent tailDep = copDeps[^1];
                    lexicalWord = tailDep.WordIndex;
                    lexicalReadings = tailDep.Readings;
                    int tailPosInClause = Array.IndexOf(idx, tailDep.WordIndex);
                    if (canon == "би" && slots[tailDep.WordIndex].Word.CanonicalText is "трябвало" or "следвало" or "могло" ||
                        canon is "е" or "беше" or "бе" &&
                        slots[tailDep.WordIndex].Word.CanonicalText is "трябвало" or "следвало" or "бивало" &&
                        tailPosInClause >= 0 && IsFollowedByModalDa(map, idx, tailPosInClause, ct))
                    {
                        impersonalModal = true;
                        invariantCarrier = true;
                    }
                    // Keep LastWord compatible with adjacent participle expectation in ClauseFusionAnalyzer when adjacent,
                    // or full analytic group when bridged by clitics/ли.
                    if (copKind is VerbalComplexKind.AnalyticPerfect or VerbalComplexKind.FuturePerfect or VerbalComplexKind.Conditional ||
                        copLastWord == carrierWord + 1 && IsParticipleOnly(slots[copLastWord]))
                    {
                        lastWord = copLastWord;
                    }
                }
                else if (p + 1 < idx.Length && idx[p + 1] == carrierWord + 1 &&
                         map.AreSpaceBridged(carrierWord, idx[p + 1], ct) &&
                         IsParticipleOnly(slots[idx[p + 1]]))
                {
                    lastWord = idx[p + 1];
                    lexicalWord = idx[p + 1];
                    lexicalReadings = slots[idx[p + 1]].Active;
                    dependents.Add(new(VerbalDependentKind.PassiveOrAdjectivalPredicative, idx[p + 1], lexicalReadings));
                    kind = VerbalComplexKind.CopularPredicative;
                }
            }
            // 3. Check clause-initial postposed reflexive "се" ("Налага се", "Проведе се срещи").
            else if (p == 0 && bodyStart == 0 && !preClitics.HasReflexiveSe && !s.Quoted &&
                     p + 1 < idx.Length && !slots[idx[p + 1]].Quoted &&
                     idx[p + 1] == carrierWord + 1 && map.AreSpaceBridged(carrierWord, idx[p + 1], ct) &&
                     slots[idx[p + 1]].Word.CanonicalText == "се" &&
                     slots[idx[p + 1]].Active.Length > 0 &&
                     slots[idx[p + 1]].Active.All(r => slots[idx[p + 1]].Word.Readings[r].Features is
                         { PartOfSpeech: MorphologicalPartOfSpeech.Particle, HasUnrecognizedFeatures: false, HasConflictingFeatures: false }))
            {
                lastWord = idx[p + 1];
                preClitics = preClitics with
                {
                    ReflexiveWordIndex = idx[p + 1],
                    HasReflexiveSe = true,
                    PostposedSe = true
                };
            }
            // 4. Check Future ("няма да"), FutureInPast ("щях [ли] да", "нямаше да"), and PersonalModalDa ("нямам* да", "мога* да", "трябва/бива [ли] да").
            else if (isFutPastAux || isPersonalNyama || isPersonalMoga || isInvariantNyama || isInvariantModal)
            {
                if (TryBindAuxOrModalDaTailInClause(
                        map, idx, p, isFutPastAux, isInvariantNyama, isInvariantModal,
                        out VerbalComplexKind modalKind, out int boundDaWord, out CliticCluster boundDaClitics,
                        out VerbalDependent subDep, out List<int> modalBridges, ct))
                {
                    kind = modalKind;
                    daWord = boundDaWord;
                    postClitics = boundDaClitics;
                    dependents.Add(subDep);
                    bridges.AddRange(modalBridges);
                    lexicalWord = subDep.WordIndex;
                    lexicalReadings = subDep.Readings;
                    invariantCarrier |= isInvariantNyama || isInvariantModal;
                    // Preserve LastWordIndex = carrierWord for postverbal domain / "Трябва" matrix predicate compatibility,
                    // while exposing Dependents[0] (subDep) and DaParticleWordIndex directly to consumers!
                }
            }

            return new VerbalComplex(
                Kind: kind,
                ClauseId: clause.ClauseId,
                FirstWordIndex: firstWord,
                FiniteCarrierWordIndex: carrierWord,
                LexicalHeadWordIndex: lexicalWord,
                LastWordIndex: lastWord,
                DaParticleWordIndex: daWord,
                FutureParticleWordIndex: futureWord,
                CarrierFiniteReadings: finite,
                LexicalReadings: lexicalReadings,
                PreCarrierClitics: preClitics,
                PostDaOrAuxClitics: postClitics,
                Dependents: dependents.ToArray(),
                CopularCarrier: copular,
                ImpersonalModal: impersonalModal,
                InvariantCarrier: invariantCarrier,
                BridgeWordIndices: bridges.ToArray());
        }

        return null;
    }

    /// <summary>
    /// Performs all internal verbal-complex and clitic-cluster well-formedness checks in a single linear pass,
    /// reproducing every Package A guard (epistemic "ще да", separate disallowImperfect vs disallowAoristAndActiveParticiple,
    /// embedded polar "ли" questions, modal/future-in-past person-number & transitivity checks, Tobler-Mussafia & clitic order,
    /// and closed finite-free converb / positive-imperative-clitic / bare-passive-clitic intrusions).
    /// </summary>
    internal static IReadOnlyList<VerbalInternalMismatch> CheckInternalWellFormedness(
        SentenceClauseMap map,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(map);
        ct.ThrowIfCancellationRequested();
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        if (slots.Length == 0 || map.HasUnexpectedSymbol)
            return [];

        List<VerbalInternalMismatch> mismatches = [];

        // A. Clitic-cluster anomalies:
        // A1. Sentence-initial enclitic violation (Tobler-Mussafia: "*Ме чета...", "*Го пишеш...", "*Му гледа...", hardened "<BOS> Я/Ни/Ми + Vfin").
        if (slots.Length >= 2 && !peripheral[0] && !slots[0].Quoted && SentenceClauseMap.IsExactWord(slots[0]))
        {
            string c0 = slots[0].Word.CanonicalText;
            if (SentenceClauseMap.LeadingDialogueOrSpace(map.Analysis.Text, map.Sentence.Start, slots[0].Word.Token.Start) &&
                c0 is "ме" or "го" or "му" or "ги" or "им" or "ѝ")
            {
                for (int j = 1; j < slots.Length && j <= 3; j++)
                {
                    if (peripheral[j] || slots[j].Quoted || !SentenceClauseMap.IsExactWord(slots[j]) ||
                        !map.AreSpaceBridged(j - 1, j, ct))
                        break;
                    if (SentenceClauseMap.HasFiniteReading(slots[j]) ||
                        slots[j].Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier))
                    {
                        AddPronounCaseAnomaly(0, slots[0].Word, j, slots[j].Word, mismatches);
                        break;
                    }
                    if (!BulgarianFunctionWords.HasAny(slots[j].Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge))
                        break;
                }
            }
            else if (c0 is "я" or "ни" or "ми" &&
                     map.Analysis.Text.AsSpan(map.Sentence.Start, slots[0].Word.Token.Start - map.Sentence.Start).Trim().IsEmpty &&
                     !peripheral[1] && !slots[1].Quoted && SentenceClauseMap.IsExactWord(slots[1]) &&
                     map.AreSpaceBridged(0, 1, ct))
            {
                ReadOnlySpan<char> sentSpan = map.Analysis.Text.AsSpan(map.Sentence.Start, map.Sentence.Length);
                ReadOnlySpan<char> trimmedSent = sentSpan.TrimEnd();
                bool declarativePeriod = trimmedSent.EndsWith(".", StringComparison.Ordinal) &&
                    !slots.Any(s => s.Word.CanonicalText == "ли");
                int[] raw1 = SentenceClauseMap.NonPlaceholderReadings(slots[1].Word, sentenceInitial: false);
                bool pureVerb1 = raw1.Length > 0 && raw1.All(r => slots[1].Word.Readings[r].Features is
                    { PartOfSpeech: MorphologicalPartOfSpeech.Verb, HasConflictingFeatures: false });
                int[] fin1 = pureVerb1 ? SentenceClauseMap.AllFinitePersonalReadings(slots[1].Word, ct) : [];
                bool hasImp1 = slots[1].Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative);
                bool nonCopular1 = fin1.Length > 0 && !hasImp1 &&
                    !slots[1].Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier);

                if (declarativePeriod && c0 == "ми")
                {
                    if (slots[1].Word.CanonicalText is "съм" or "си" or "е" or "сме" or "сте" or "са" &&
                        slots[1].Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier))
                    {
                        AddPronounCaseAnomaly(0, slots[0].Word, 1, slots[1].Word, mismatches);
                    }
                }
                else if (declarativePeriod && nonCopular1)
                {
                    bool invalidInitialHomographClitic = c0 switch
                    {
                        "я" => fin1.All(r => slots[1].Word.Readings[r].Features.Person == MorphologicalPerson.First &&
                                             slots[1].Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive),
                        "ни" => !sentSpan.Contains(',') &&
                                !slots.Skip(1).Any(s => s.Word.CanonicalText is "ни" or "нито") &&
                                fin1.All(r => slots[1].Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                                              !IsLinkingOrSemiCopularLemma(slots[1].Word.Readings[r].Lemma)),
                        _ => false
                    };
                    if (invalidInitialHomographClitic)
                    {
                        AddPronounCaseAnomaly(0, slots[0].Word, 1, slots[1].Word, mismatches);
                    }
                }
            }
        }

        // A2. Reversed or double pronominal clitics before finite verb ("*я му даде", "*го я даде"), guarding clause-initial/imperative "я".
        for (int i = 0; i < slots.Length - 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot slot = slots[i], next = slots[i + 1];
            if (peripheral[i] || peripheral[i + 1] || slot.Quoted || next.Quoted ||
                !SentenceClauseMap.IsExactWord(slot) || !SentenceClauseMap.IsExactWord(next) ||
                !map.AreSpaceBridged(i, i + 1, ct))
                continue;

            string c1 = slot.Word.CanonicalText;
            string c2 = next.Word.CanonicalText;
            if (c1 != "те" &&
                BulgarianFunctionWords.HasAny(c1, FunctionWordClasses.PureAccusativeClitic) &&
                BulgarianFunctionWords.HasAny(c2, FunctionWordClasses.ShortPronominalClitic))
            {
                bool clauseInitialYa = c1 == "я" &&
                    (i == 0 || !map.AreSpaceBridged(i - 1, i, ct) ||
                     SentenceClauseMap.IsCoordinatingConjunction(slots[i - 1]) ||
                     slots[i - 1].Word.CanonicalText is "ами" or "абе" or "е" or "та");
                if (!clauseInitialYa && !peripheral[i + 2] && !slots[i + 2].Quoted &&
                    SentenceClauseMap.IsExactWord(slots[i + 2]) &&
                    map.AreSpaceBridged(i + 1, i + 2, ct) &&
                    SentenceClauseMap.HasFiniteReading(slots[i + 2]) &&
                    !(c1 == "я" && slots[i + 2].Word.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Imperative)))
                {
                    AddPronounCaseAnomaly(i, slot.Word, i + 2, slots[i + 2].Word, mismatches);
                }
            }
        }

        // B. Particle / Auxiliary / Modal internal verb-form & agreement checks:
        for (int i = 0; i < slots.Length - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[i] || slots[i].Quoted || !SentenceClauseMap.IsExactWord(slots[i]))
                continue;
            string head = slots[i].Word.CanonicalText;

            // B1. Particles "ще" and "да" before non-Present / participle / imperative / converb forms.
            if (head is "ще" or "да")
            {
                // Epistemic "ще да е + -л" ("ще да" delegates check to "да").
                if (head == "ще" && i + 1 < slots.Length && slots[i + 1].Word.CanonicalText == "да")
                    continue;

                bool disallowImperfect = head == "ще";
                bool disallowAoristAndActiveParticiple = head == "ще";
                if (head == "да")
                {
                    if (!SentenceClauseMap.HasConcessiveIntroducerBeforeDa(slots, i) &&
                        !HasObliqueRelativeConcessiveBeforeDa(slots, i))
                    {
                        bool purposeOrNegPrep = false;
                        if (map.TryGetPrepositionalSubordinatorForIntroducer(i, out int prepIdx) &&
                            slots[prepIdx].Word.CanonicalText is "без" or "за" or "преди" or "вместо")
                        {
                            purposeOrNegPrep = true;
                        }

                        if (i > 0 && !peripheral[i - 1] && !slots[i - 1].Quoted &&
                            SentenceClauseMap.IsExactWord(slots[i - 1]) && map.AreSpaceBridged(i - 1, i, ct))
                        {
                            WordSlot prev = slots[i - 1];
                            string prevCanon = prev.Word.CanonicalText;
                            purposeOrNegPrep |= prevCanon is "без" or "за" or "преди" or "вместо" &&
                                SentenceClauseMap.IsActivePreposition(prev.Word, prev.Active);
                            int[] prevFinite = SentenceClauseMap.FiniteReadings(prev);
                            bool epistemicMozhe = prevCanon == "може" &&
                                (i - 1 == 0 || !map.AreSpaceBridged(i - 2, i - 1, ct) ||
                                 SentenceClauseMap.IsCoordinatingConjunction(slots[i - 2]) ||
                                 SentenceClauseMap.IsClauseIntroducer(slots[i - 2].Word, slots[i - 2].Active));
                            bool epistemicImperfectModal = prevCanon is "може" or "можеше";
                            int[] prevNouns = SentenceClauseMap.NominalHeadReadings(prev);
                            bool initialDefiniteSubject = prevNouns.Length > 0 &&
                                (i - 1 == 0 && prevNouns.All(r => prev.Word.Readings[r].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full) ||
                                 i - 2 == 0 && map.AreSpaceBridged(0, 1, ct) && SentenceClauseMap.IsExactWord(slots[0]) &&
                                 slots[0].Active.Length > 0 &&
                                 slots[0].Active.All(r => slots[0].Word.Readings[r].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full));

                            disallowImperfect = purposeOrNegPrep || prevFinite.Length > 0 && !epistemicImperfectModal;
                            disallowAoristAndActiveParticiple = purposeOrNegPrep || prevFinite.Length > 0 && !epistemicMozhe || initialDefiniteSubject;
                        }
                        else if (purposeOrNegPrep)
                        {
                            disallowImperfect = true;
                            disallowAoristAndActiveParticiple = true;
                        }
                    }
                }

                int badPos = -1;
                int[] badReadings = [];
                for (int j = i + 1; j < slots.Length && j <= i + 4; j++)
                {
                    if (peripheral[j] || slots[j].Quoted || !SentenceClauseMap.IsExactWord(slots[j]) ||
                        !map.AreSpaceBridged(j - 1, j, ct))
                        break;
                    string cj = slots[j].Word.CanonicalText;
                    if (cj is "би" or "бих" or "бихме" or "бихте" or "биха" && head == "да") break;
                    if (cj == "може" && j + 1 < slots.Length && slots[j + 1].Word.CanonicalText == "би") break;

                    if (TryGetInvalidParticleVerbReadings(slots[j].Word, disallowImperfect, disallowAoristAndActiveParticiple, out badReadings))
                    {
                        badPos = j;
                        break;
                    }
                    if (!BulgarianFunctionWords.HasAny(cj, FunctionWordClasses.PreVerbalCliticBridge) ||
                        SentenceClauseMap.HasFiniteReading(slots[j]))
                        break;
                }

                if (badPos >= 0)
                {
                    AddParticleOrAuxMismatch(i, slots[i].Word, badPos, slots[badPos].Word, badReadings, mismatches);
                }
                continue;
            }

            // B2. Analytic auxiliary ("съм/е/сме/сте/са", "бях/беше/бе/...", "бих/би/...") + finite / imperative / converb intrusion or V2d [Copula + Aorist+MascSgShort/Count + от].
            if (head is "съм" or "е" or "сме" or "сте" or "са" or
                "бях" or "беше" or "бе" or "бяхме" or "бяхте" or "бяха" or
                "бих" or "би" or "бихме" or "бихте" or "биха")
            {
                if (head == "би" && i > 0 && slots[i - 1].Word.CanonicalText is "може" or "не")
                    continue;

                for (int j = i + 1; j < slots.Length && j <= i + 3; j++)
                {
                    if (peripheral[j] || slots[j].Quoted || !SentenceClauseMap.IsExactWord(slots[j]) ||
                        !map.AreSpaceBridged(j - 1, j, ct))
                        break;
                    string cj = slots[j].Word.CanonicalText;
                    if (cj == "може" && j + 1 < slots.Length && slots[j + 1].Word.CanonicalText == "би")
                        break;
                    if (BulgarianFunctionWords.HasAny(cj,
                            FunctionWordClasses.PronominalVerbalClitic | FunctionWordClasses.ReflexiveAccusativeSe | FunctionWordClasses.InterrogativeParticle))
                        continue;
                    // Guard embedded polar "ли" questions ("Въпросът е има ли смисъл.").
                    if (FollowedByInterrogativeLi(map, j, ct))
                        break;

                    int[] realRows = SentenceClauseMap.NonPlaceholderReadings(slots[j].Word, sentenceInitial: false);
                    if (head != "бе" && realRows.Length > 0 && realRows.All(r =>
                        {
                            ReadingView row = slots[j].Word.Readings[r];
                            MorphologicalFeatures f = row.Features;
                            return f is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, HasConflictingFeatures: false } &&
                                   f.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative or MorphologicalVerbForm.Converb &&
                                   !TextPredicateLinkAnalyzer.CopularLemma(row.Lemma);
                        }))
                    {
                        AddParticleOrAuxMismatch(i, slots[i].Word, j, slots[j].Word, realRows, mismatches);
                    }
                    else if (head is "е" or "са" or "беше" or "бяха" or "бе" &&
                             j + 1 < slots.Length && !peripheral[j + 1] && !slots[j + 1].Quoted &&
                             slots[j + 1].Word.CanonicalText == "от" &&
                             map.AreSpaceBridged(j, j + 1, ct))
                    {
                        bool hasAorist = realRows.Any(r => slots[j].Word.Readings[r].Features is
                            { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Finite, Tense: MorphologicalTense.Aorist });
                        bool hasParticipleOrPresent = realRows.Any(r => slots[j].Word.Readings[r].Features is
                            { VerbForm: MorphologicalVerbForm.Participle } or { Tense: MorphologicalTense.Present });
                        int[] nonVerbRows = realRows.Where(r => slots[j].Word.Readings[r].Features.PartOfSpeech != MorphologicalPartOfSpeech.Verb).ToArray();
                        bool nonVerbOnlyMascShortOrCount = nonVerbRows.Length > 0 && nonVerbRows.All(r =>
                            slots[j].Word.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine } nf &&
                            (nf.Number == MorphologicalNumber.Count ||
                             nf.Number == MorphologicalNumber.Singular && nf.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite));
                        if (hasAorist && !hasParticipleOrPresent && nonVerbOnlyMascShortOrCount)
                        {
                            int[] verbRows = realRows
                                .Where(r => slots[j].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb)
                                .Order()
                                .ToArray();
                            AddParticleOrAuxMismatch(i, slots[i].Word, j, slots[j].Word, verbRows, mismatches);
                        }
                    }
                    break;
                }
                continue;
            }

            // B3. Future-in-the-past ("щях/щеше/щяхме/щяхте/щяха"), personal "нямам*/бивам* да", or pro-drop personal "мога/можеш/можем/можете/могат/можех*" + [ли] + "да" + Vpres.
            bool isFutPastAux = head is "щях" or "щеше" or "щяхме" or "щяхте" or "щяха";
            bool isPersonalNyama = head is "нямам" or "нямаш" or "нямаме" or "нямате" or "нямат";
            bool isPersonalBiva = head is "бивам" or "биваш" or "биваме" or "бивате" or "биват" or
                "бивах" or "бивахме" or "бивахте" or "биваха";
            bool isPersonalMoga = head is "мога" or "можеш" or "можем" or "можете" or "могат" or
                "можех" or "можехме" or "можехте" or "можеха";
            if (isFutPastAux || isPersonalNyama || isPersonalBiva || isPersonalMoga)
            {
                if (isPersonalMoga && HasOvertClauseSubjectBeforeModal(map, slots, peripheral, i, ct))
                    continue;
                int daIdx = i + 1;
                if ((isFutPastAux || isPersonalMoga || isPersonalBiva) && daIdx < slots.Length && slots[daIdx].Word.CanonicalText == "ли" &&
                    map.AreSpaceBridged(i, daIdx, ct))
                    daIdx++;
                if (daIdx >= slots.Length - 1 || peripheral[daIdx] || slots[daIdx].Quoted ||
                    slots[daIdx].Word.CanonicalText != "да" ||
                    !map.AreSpaceBridged(daIdx - 1, daIdx, ct))
                    continue;

                int subIdx = -1;
                for (int j = daIdx + 1; j < slots.Length && j <= daIdx + 4; j++)
                {
                    if (peripheral[j] || slots[j].Quoted || !SentenceClauseMap.IsExactWord(slots[j]) ||
                        !map.AreSpaceBridged(j - 1, j, ct))
                        break;
                    if (slots[j].Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier))
                        break;
                    if (BulgarianFunctionWords.HasAny(slots[j].Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge) &&
                        !SentenceClauseMap.HasFiniteReading(slots[j]))
                        continue;
                    subIdx = j;
                    break;
                }
                if (subIdx < 0) continue;

                TokenView auxWord = slots[i].Word;
                TokenView subWord = slots[subIdx].Word;
                int[] auxFinite = SentenceClauseMap.AllFinitePersonalReadings(auxWord, ct);
                int[] subPres = SentenceClauseMap.AllFinitePersonalReadings(subWord, ct)
                    .Where(r => subWord.Readings[r].Features.Tense == MorphologicalTense.Present)
                    .ToArray();
                if (auxFinite.Length == 0 || subPres.Length == 0) continue;

                bool pairAgrees = auxFinite.Any(ar => subPres.Any(sr => Guard.PersonNumberTupleMatches(
                    (auxWord.Readings[ar].Features.Person, auxWord.Readings[ar].Features.Number),
                    (subWord.Readings[sr].Features.Person, subWord.Readings[sr].Features.Number))));
                bool subStrictlyIntransitive = subPres.All(r =>
                    subWord.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
                    !IsLinkingOrSemiCopularLemma(subWord.Readings[r].Lemma));

                if (!pairAgrees || isPersonalBiva || isPersonalNyama && subStrictlyIntransitive)
                {
                    bool targetAux = isPersonalNyama || isPersonalBiva;
                    if (!targetAux && i > 0 && !peripheral[i - 1] && map.AreSpaceBridged(i - 1, i, ct))
                    {
                        int[] prevNouns = SentenceClauseMap.NominalHeadReadings(slots[i - 1]);
                        if (prevNouns.Length > 0)
                        {
                            MorphologicalNumber? subjNum = slots[i - 1].Word.Readings[prevNouns[0]].Features.Number;
                            if (subjNum is not null &&
                                subPres.Any(sr => Guard.PersonNumberTupleMatches((MorphologicalPerson.Third, subjNum),
                                    (subWord.Readings[sr].Features.Person, subWord.Readings[sr].Features.Number))) &&
                                !auxFinite.Any(ar => Guard.PersonNumberTupleMatches((MorphologicalPerson.Third, subjNum),
                                    (auxWord.Readings[ar].Features.Person, auxWord.Readings[ar].Features.Number))))
                            {
                                targetAux = true;
                            }
                        }
                    }

                    int targetIdx = targetAux ? i : subIdx;
                    int[] targetRows = targetAux ? auxFinite : subPres;
                    int partnerIdx = targetAux ? subIdx : i;
                    int[] partnerRows = targetAux ? subPres : auxFinite;
                    mismatches.Add(new VerbalInternalMismatch(
                        VerbalInternalMismatchKind.ModalDaPersonNumberOrTransitivity,
                        targetIdx,
                        targetRows,
                        partnerIdx,
                        partnerRows));
                }
            }
        }

        return mismatches;
    }

    internal static bool IsImpersonalModalCanon(string canon) =>
        canon is "трябва" or "трябваше" or "бива" or "биваше" or "личи" or "вали" or "валеше" or "гърми" or "съмва";

    internal static bool IsLinkingOrSemiCopularLemma(string lemma) =>
        BulgarianTextNormalizer.NormalizeKey(lemma) is "съм" or "бъда" or "бивам" or "стана" or "ставам" or
            "изглеждам" or "окажа" or "оказвам" or "наричам" or "нарека" or "казвам" or "кажа" or
            "зова" or "именувам" or "явявам" or "явя";

    internal static bool IsActiveLexicalParticiple(WordSlot slot) =>
        !slot.Word.UsesGraph && slot.Active.Length > 0 &&
        slot.Active.All(r => slot.Word.Readings[r].Features is
        {
            VerbForm: MorphologicalVerbForm.Participle,
            ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive
        } && !TextPredicateLinkAnalyzer.CopularLemma(slot.Word.Readings[r].Lemma));

    internal static bool TryGetUnambiguousActiveParticipleReadings(
        TokenView word, bool sentenceInitial, out int[] readings, bool allowSubstantivizedParticiple = false)
    {
        readings = [];
        if (word.UsesGraph || !word.Complete || word.Readings.Count == 0) return false;
        bool hasNonPlaceholder = word.Readings.Any(r => !Guard.IsSourcePlaceholder(word, r));
        List<int> rows = [];
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ReadingView row = word.Readings[i];
            if (hasNonPlaceholder && Guard.IsSourcePlaceholder(word, row) ||
                Guard.IsUnwrittenCapitalizedReading(word, row, sentenceInitial) ||
                Guard.IsSyncreticVocativeCompanion(word, row))
                continue;
            MorphologicalFeatures f = row.Features;
            if (allowSubstantivizedParticiple &&
                f is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, ParadigmId: "5" } &&
                BulgarianTextNormalizer.NormalizeKey(row.Lemma).EndsWith('л'))
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
        readings = rows.ToArray();
        return true;
    }

    internal static bool IsVerbalClitic(WordSlot slot, WordSlot? prev, out int[] accReadings)
    {
        accReadings = [];
        string c = slot.Word.CanonicalText;
        if (!BulgarianFunctionWords.HasAny(c, FunctionWordClasses.PronominalVerbalClitic))
            return false;
        if (c is "те" or "ти" && (prev is null || NominativePronounReadings(slot, prev).Length > 0 &&
            !BulgarianFunctionWords.HasAny(prev.Word.CanonicalText, FunctionWordClasses.VerbalPrefixParticle)))
            return false;
        accReadings = slot.Active.Where(r => Guard.IsAccusativeCliticReading(slot.Word.Readings[r])).ToArray();
        return true;
    }

    internal static int[] NominativePronounReadings(WordSlot slot, WordSlot? prev)
    {
        if (slot.Word.UsesGraph) return [];
        string c = slot.Word.CanonicalText;
        if (c == "никой")
        {
            return slot.Active.Where(r => slot.Word.Readings[r].Category == Category.Pronoun &&
                slot.Word.Readings[r].Features.Case is null).ToArray();
        }
        if (c == "те" && prev is not null && BulgarianFunctionWords.HasAny(prev.Word.CanonicalText, FunctionWordClasses.VerbalPrefixParticle))
            return [];
        if (c == "ти" && prev is not null && (SentenceClauseMap.HasNominalHeadReading(prev) || SentenceClauseMap.HasFiniteReading(prev) ||
            BulgarianFunctionWords.HasAny(prev.Word.CanonicalText, FunctionWordClasses.VerbalPrefixParticle)))
            return [];
        int[] licensed = slot.Active.Where(r => Guard.LicensedSubject(c, slot.Word.Readings[r])).ToArray();
        return licensed.Length > 0 && slot.Active.All(r => Guard.LicensedSubject(c, slot.Word.Readings[r]) ||
            c == "те" && (slot.Word.Readings[r].Category == Category.Particle || Guard.IsAccusativeCliticReading(slot.Word.Readings[r])) ||
            c == "ти" && (slot.Word.Readings[r].Category == Category.Particle || slot.Word.Readings[r].Features.IsShortForm == true) ||
            c == "вие" && prev is not null && SentenceClauseMap.HasFiniteReading(prev) &&
            slot.Word.Readings[r].Category == Category.Verb && SentenceClauseMap.IsCleanOrBenignLegacyVerb(slot.Word.Readings[r]))
            ? licensed : [];
    }

    private static VerbalComplex? TryBindDaClauseMain(
        SentenceClauseMap map,
        int clauseId,
        int[] idx,
        int daPos,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        int daWord = idx[daPos];
        int negBeforeDa = -1;
        int firstWord = daWord;
        if (daPos > 0 && slots[idx[daPos - 1]].Word.CanonicalText == "не" &&
            map.AreSpaceBridged(idx[daPos - 1], daWord, ct))
        {
            negBeforeDa = idx[daPos - 1];
            firstWord = negBeforeDa;
        }

        int verbPos = -1;
        for (int p = daPos + 1; p < idx.Length && p <= daPos + 4; p++)
        {
            if (!map.AreSpaceBridged(idx[p - 1], idx[p], ct)) break;
            WordSlot cand = slots[idx[p]];
            if (SentenceClauseMap.HasFiniteReading(cand) &&
                !(cand.Word.CanonicalText is "си" or "ми" && p + 1 < idx.Length &&
                  SentenceClauseMap.HasFiniteReading(slots[idx[p + 1]])))
            {
                verbPos = p;
                break;
            }
            if (!BulgarianFunctionWords.HasAny(cand.Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge))
                break;
        }
        if (verbPos < 0) return null;

        CliticCluster postDaClitics = BindPreVerbalCliticsInClause(
            map, idx, daPos + 1, verbPos, out _, out _, ct);
        if (negBeforeDa >= 0 && postDaClitics.NegationWordIndex < 0)
            postDaClitics = postDaClitics with { NegationWordIndex = negBeforeDa };

        WordSlot verbSlot = slots[idx[verbPos]];
        int[] finite = SentenceClauseMap.FiniteReadings(verbSlot);
        bool copular = verbSlot.Active.Any(r => TextPredicateLinkAnalyzer.CopularLemma(verbSlot.Word.Readings[r].Lemma));
        int lastWord = idx[verbPos];
        int lexicalWord = idx[verbPos];
        int[] lexicalReadings = finite;
        List<VerbalDependent> dependents = [];
        List<int> bridges = [];

        if (copular && TryBindCopularTailInClause(
                map, idx, verbPos, isConditionalAux: false, hasFutureMarker: false,
                out _, out _, out List<VerbalDependent> copDeps, out List<int> copBridges, out int copLast, ct))
        {
            dependents.AddRange(copDeps);
            bridges.AddRange(copBridges);
            lexicalWord = copDeps[^1].WordIndex;
            lexicalReadings = copDeps[^1].Readings;
            lastWord = copLast;
        }

        bool impersonal = IsImpersonalModalCanon(verbSlot.Word.CanonicalText);
        return new VerbalComplex(
            Kind: VerbalComplexKind.DaConstruction,
            ClauseId: clauseId,
            FirstWordIndex: firstWord,
            FiniteCarrierWordIndex: idx[verbPos],
            LexicalHeadWordIndex: lexicalWord,
            LastWordIndex: lastWord,
            DaParticleWordIndex: daWord,
            FutureParticleWordIndex: -1,
            CarrierFiniteReadings: finite,
            LexicalReadings: lexicalReadings,
            PreCarrierClitics: CliticCluster.Empty,
            PostDaOrAuxClitics: postDaClitics,
            Dependents: dependents.ToArray(),
            CopularCarrier: copular,
            ImpersonalModal: impersonal,
            InvariantCarrier: impersonal,
            BridgeWordIndices: bridges.ToArray());
    }

    private static CliticCluster BindPreVerbalCliticsInClause(
        SentenceClauseMap map,
        int[] idx,
        int minPos,
        int verbPos,
        out int firstWordIndex,
        out int futureWordIndex,
        CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        firstWordIndex = idx[verbPos];
        futureWordIndex = -1;

        int cliticFirst = -1, cliticLast = -1;
        int negWord = -1, liWord = -1, reflWord = -1, datWord = -1, accWord = -1;
        bool hasSe = false;
        int[] accReadings = [];

        for (int q = verbPos - 1; q >= minPos; q--)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot pre = slots[idx[q]];
            if (pre.Quoted || !map.AreSpaceBridged(idx[q], idx[q + 1], ct)) break;
            string c = pre.Word.CanonicalText;
            if (c == "се")
            {
                hasSe = true;
                reflWord = idx[q];
                firstWordIndex = idx[q];
                cliticFirst = idx[q];
                if (cliticLast < 0) cliticLast = idx[q];
                continue;
            }
            if (c == "ще")
            {
                futureWordIndex = idx[q];
                firstWordIndex = idx[q];
                continue;
            }
            if (c == "не")
            {
                negWord = idx[q];
                firstWordIndex = idx[q];
                cliticFirst = idx[q];
                if (cliticLast < 0) cliticLast = idx[q];
                continue;
            }
            if (c == "ли" && q > minPos && slots[idx[q - 1]].Word.CanonicalText == "не" &&
                map.AreSpaceBridged(idx[q - 1], idx[q], ct))
            {
                liWord = idx[q];
                firstWordIndex = idx[q];
                cliticFirst = idx[q];
                if (cliticLast < 0) cliticLast = idx[q];
                continue;
            }
            WordSlot? prev = q > minPos ? slots[idx[q - 1]] : null;
            if (IsVerbalClitic(pre, prev, out int[] acc))
            {
                if (c == "си" && reflWord < 0)
                    reflWord = idx[q];
                else if (acc.Length > 0 && accWord < 0)
                {
                    accWord = idx[q];
                    accReadings = acc;
                }
                else if (BulgarianFunctionWords.HasAny(c, FunctionWordClasses.ShortDativeClitic) && datWord < 0)
                {
                    datWord = idx[q];
                }
                firstWordIndex = idx[q];
                cliticFirst = idx[q];
                if (cliticLast < 0) cliticLast = idx[q];
                continue;
            }
            break;
        }

        return new CliticCluster(
            FirstWordIndex: cliticFirst,
            LastWordIndex: cliticLast,
            NegationWordIndex: negWord,
            InterrogativeLiWordIndex: liWord,
            ReflexiveWordIndex: reflWord,
            HasReflexiveSe: hasSe,
            PostposedSe: false,
            DativeCliticWordIndex: datWord,
            AccusativeCliticWordIndex: accWord,
            AccusativeCliticReadings: accReadings);
    }

    private static bool TryBindCopularTailInClause(
        SentenceClauseMap map,
        int[] idx,
        int carrierPos,
        bool isConditionalAux,
        bool hasFutureMarker,
        out VerbalComplexKind kind,
        out CliticCluster postClitics,
        out List<VerbalDependent> dependents,
        out List<int> bridges,
        out int lastWordIndex,
        CancellationToken ct)
    {
        kind = VerbalComplexKind.SyntheticFinite;
        postClitics = CliticCluster.Empty;
        dependents = [];
        bridges = [];
        lastWordIndex = idx[carrierPos];

        WordSlot[] slots = map.Slots;
        int cursor = carrierPos + 1;
        int cliticFirst = -1, cliticLast = -1, liWord = -1, reflWord = -1, datWord = -1, accWord = -1;
        bool hasSe = false;
        int[] accReadings = [];

        while (cursor < idx.Length && cursor <= carrierPos + 3 &&
               idx[cursor] == idx[cursor - 1] + 1 &&
               !slots[idx[cursor]].Quoted &&
               map.AreSpaceBridged(idx[cursor - 1], idx[cursor], ct))
        {
            WordSlot cand = slots[idx[cursor]];
            string cc = cand.Word.CanonicalText;
            if (cc == "ли")
            {
                liWord = idx[cursor];
                if (cliticFirst < 0) cliticFirst = idx[cursor];
                cliticLast = idx[cursor];
                bridges.Add(idx[cursor]);
                cursor++;
                continue;
            }
            if (cc is "се" or "си" ||
                BulgarianFunctionWords.HasAny(cc, FunctionWordClasses.PronominalVerbalClitic))
            {
                if (TryGetUnambiguousActiveParticipleReadings(cand.Word, sentenceInitial: false, out _))
                    break;
                if (cc == "се") { hasSe = true; reflWord = idx[cursor]; }
                else if (cc == "си" && reflWord < 0) reflWord = idx[cursor];
                else if (IsVerbalClitic(cand, slots[idx[cursor - 1]], out int[] acc) && acc.Length > 0 && accWord < 0)
                {
                    accWord = idx[cursor];
                    accReadings = acc;
                }
                else if (datWord < 0)
                {
                    datWord = idx[cursor];
                }
                if (cliticFirst < 0) cliticFirst = idx[cursor];
                cliticLast = idx[cursor];
                bridges.Add(idx[cursor]);
                cursor++;
                continue;
            }
            break;
        }

        postClitics = new CliticCluster(
            cliticFirst, cliticLast, -1, liWord, reflWord, hasSe, false,
            datWord, accWord, accReadings);

        if (cursor >= idx.Length || idx[cursor] != idx[cursor - 1] + 1 ||
            slots[idx[cursor]].Quoted ||
            !map.AreSpaceBridged(idx[cursor - 1], idx[cursor], ct))
            return false;

        WordSlot target = slots[idx[cursor]];

        // Check "бил/била/било/били" auxiliary participle chain first ("е бил разбрал", "сте били поканена").
        if (TryGetBylParticipleReadings(target.Word, out int[] bylRows))
        {
            dependents.Add(new(VerbalDependentKind.AuxiliaryByl, idx[cursor], bylRows));
            lastWordIndex = idx[cursor];
            if (cursor + 1 < idx.Length && idx[cursor + 1] == idx[cursor] + 1 &&
                !slots[idx[cursor + 1]].Quoted &&
                map.AreHorizontalWordBridged(idx[cursor], idx[cursor + 1], ct))
            {
                WordSlot afterByl = slots[idx[cursor + 1]];
                if (TryGetUnambiguousActiveParticipleReadings(afterByl.Word, sentenceInitial: false, out int[] nextActRows, allowSubstantivizedParticiple: true))
                {
                    dependents.Add(new(VerbalDependentKind.ActiveParticiple, idx[cursor + 1], nextActRows));
                    lastWordIndex = idx[cursor + 1];
                }
                else if (TryGetPassiveOrAdjectivalReadings(afterByl.Word, out int[] nextPassRows))
                {
                    dependents.Add(new(VerbalDependentKind.PassiveOrAdjectivalPredicative, idx[cursor + 1], nextPassRows));
                    lastWordIndex = idx[cursor + 1];
                }
            }
            kind = isConditionalAux
                ? VerbalComplexKind.Conditional
                : hasFutureMarker
                    ? VerbalComplexKind.FuturePerfect
                    : VerbalComplexKind.AnalyticPerfect;
            return true;
        }

        // Unambiguous lexical active -л participle ("предала", "решил", "навъдили", "завършили").
        if (TryGetUnambiguousActiveParticipleReadings(target.Word, sentenceInitial: false, out int[] partRows, allowSubstantivizedParticiple: true))
        {
            dependents.Add(new(VerbalDependentKind.ActiveParticiple, idx[cursor], partRows));
            lastWordIndex = idx[cursor];
            kind = isConditionalAux
                ? VerbalComplexKind.Conditional
                : hasFutureMarker
                    ? VerbalComplexKind.FuturePerfect
                    : VerbalComplexKind.AnalyticPerfect;
            return true;
        }

        if (isConditionalAux) return false;

        // CopularPredicative across ExactAdverb* bridges ("е много хубава", "е вече поканен").
        int advCursor = cursor;
        while (advCursor < idx.Length && idx[advCursor] == idx[advCursor - 1] + 1 &&
               !slots[idx[advCursor]].Quoted &&
               map.AreSpaceBridged(idx[advCursor - 1], idx[advCursor], ct) &&
               IsExactPureAdverb(slots[idx[advCursor]].Word))
        {
            bridges.Add(idx[advCursor]);
            advCursor++;
        }

        if (advCursor < idx.Length && idx[advCursor] == idx[advCursor - 1] + 1 &&
            !slots[idx[advCursor]].Quoted &&
            map.AreSpaceBridged(idx[advCursor - 1], idx[advCursor], ct) &&
            TryGetPassiveOrAdjectivalReadings(slots[idx[advCursor]].Word, out int[] predRows))
        {
            dependents.Add(new(VerbalDependentKind.PassiveOrAdjectivalPredicative, idx[advCursor], predRows));
            lastWordIndex = idx[advCursor];
            kind = VerbalComplexKind.CopularPredicative;
            return true;
        }

        return false;
    }

    private static bool TryBindAuxOrModalDaTailInClause(
        SentenceClauseMap map,
        int[] idx,
        int auxPos,
        bool isFutPastAux,
        bool isInvariantNyama,
        bool isInvariantModal,
        out VerbalComplexKind kind,
        out int daWordIndex,
        out CliticCluster postDaClitics,
        out VerbalDependent subDependent,
        out List<int> bridges,
        CancellationToken ct)
    {
        kind = VerbalComplexKind.SyntheticFinite;
        daWordIndex = -1;
        postDaClitics = CliticCluster.Empty;
        subDependent = default;
        bridges = [];

        WordSlot[] slots = map.Slots;
        int cursor = auxPos + 1;
        if (cursor < idx.Length && idx[cursor] == idx[cursor - 1] + 1 &&
            map.AreSpaceBridged(idx[cursor - 1], idx[cursor], ct) &&
            slots[idx[cursor]].Word.CanonicalText == "ли")
        {
            bridges.Add(idx[cursor]);
            cursor++;
        }
        if (isInvariantModal && cursor < idx.Length && idx[cursor] == idx[cursor - 1] + 1 &&
            map.AreSpaceBridged(idx[cursor - 1], idx[cursor], ct) &&
            slots[idx[cursor]].Word.CanonicalText == "и")
        {
            bridges.Add(idx[cursor]);
            cursor++;
        }
        if (cursor >= idx.Length || idx[cursor] != idx[cursor - 1] + 1 ||
            slots[idx[cursor]].Quoted ||
            !map.AreSpaceBridged(idx[cursor - 1], idx[cursor], ct) ||
            slots[idx[cursor]].Word.CanonicalText != "да")
            return false;

        int daPos = cursor;
        daWordIndex = idx[daPos];

        int subPos = -1;
        for (int p = daPos + 1; p < idx.Length && p <= daPos + 4; p++)
        {
            if (idx[p] != idx[p - 1] + 1 || slots[idx[p]].Quoted || !map.AreSpaceBridged(idx[p - 1], idx[p], ct))
                break;
            WordSlot cand = slots[idx[p]];
            if (cand.Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier))
                return false;
            if (BulgarianFunctionWords.HasAny(cand.Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge) &&
                !SentenceClauseMap.HasFiniteReading(cand))
                continue;
            subPos = p;
            break;
        }
        if (subPos < 0 || !SentenceClauseMap.IsExactWord(slots[idx[subPos]]))
            return false;

        int[] subFinite = SentenceClauseMap.AllFinitePersonalReadings(slots[idx[subPos]].Word, ct);
        if (subFinite.Length == 0) return false;

        postDaClitics = BindPreVerbalCliticsInClause(map, idx, daPos + 1, subPos, out _, out _, ct);
        subDependent = new VerbalDependent(VerbalDependentKind.DaFiniteComplement, idx[subPos], subFinite);
        kind = isInvariantNyama
            ? slots[idx[auxPos]].Word.CanonicalText == "няма"
                ? VerbalComplexKind.Future
                : VerbalComplexKind.FutureInPast
            : isFutPastAux
                ? VerbalComplexKind.FutureInPast
                : VerbalComplexKind.PersonalModalDa;
        return true;
    }

    private static bool IsFollowedByModalDa(
        SentenceClauseMap map, int[] idx, int auxPos, CancellationToken ct, bool allowModalInversionI = false)
    {
        int c = auxPos + 1;
        if (c < idx.Length && idx[c] == idx[c - 1] + 1 && map.AreSpaceBridged(idx[c - 1], idx[c], ct) &&
            map.Slots[idx[c]].Word.CanonicalText == "ли")
            c++;
        if (allowModalInversionI && c < idx.Length && idx[c] == idx[c - 1] + 1 && map.AreSpaceBridged(idx[c - 1], idx[c], ct) &&
            map.Slots[idx[c]].Word.CanonicalText == "и")
            c++;
        return c < idx.Length && idx[c] == idx[c - 1] + 1 && map.AreSpaceBridged(idx[c - 1], idx[c], ct) &&
               map.Slots[idx[c]].Word.CanonicalText == "да";
    }

    private static bool HasUpcomingActiveParticipleInClause(
        SentenceClauseMap map, int[] idx, int startPos, CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        for (int p = startPos; p < idx.Length && p <= startPos + 3; p++)
        {
            if (!map.AreSpaceBridged(idx[p - 1], idx[p], ct)) return false;
            WordSlot s = slots[idx[p]];
            if (TryGetUnambiguousActiveParticipleReadings(s.Word, idx[p] == 0, out _)) return true;
            if (s.Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) ||
                SentenceClauseMap.HasFiniteReading(s) ||
                !BulgarianFunctionWords.HasAny(s.Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge))
                return false;
        }
        return false;
    }

    private static bool IsParticipleOnly(WordSlot slot) =>
        !slot.Word.UsesGraph && slot.Active.Length > 0 &&
        slot.Active.All(r => slot.Word.Readings[r].Features.VerbForm == MorphologicalVerbForm.Participle);

    internal static bool IsExactPureAdverb(TokenView word) =>
        !word.UsesGraph && word.Complete && word.Readings.Count > 0 &&
        word.Readings.All(r => r.SourceAdverb &&
            r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adverb, HasUnrecognizedFeatures: false });

    private static bool TryGetBylParticipleReadings(TokenView word, out int[] rows)
    {
        rows = [];
        if (word.UsesGraph || !word.Complete || word.Readings.Count == 0) return false;
        int[] matches = Enumerable.Range(0, word.Readings.Count)
            .Where(i => word.Readings[i].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    HasConflictingFeatures: false
                } && TextPredicateLinkAnalyzer.CopularLemma(word.Readings[i].Lemma))
            .ToArray();
        if (matches.Length == 0) return false;
        rows = matches;
        return true;
    }

    private static bool TryGetPassiveOrAdjectivalReadings(TokenView word, out int[] rows)
    {
        rows = [];
        if (word.UsesGraph || !word.Complete || word.Readings.Count == 0) return false;
        int[] matches = Enumerable.Range(0, word.Readings.Count)
            .Where(i =>
            {
                ReadingView row = word.Readings[i];
                return row.Category == Category.Adjective &&
                       row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
                       Guard.HasNominalAxes(row.Features) &&
                       row.Features.Article == MorphologicalArticle.Indefinite ||
                       Guard.IsMeasuredPassiveAttribute(row.Features) &&
                       row.Features.Article is null or MorphologicalArticle.Indefinite;
            })
            .ToArray();
        if (matches.Length == 0) return false;
        rows = matches;
        return true;
    }

    private static bool FollowedByInterrogativeLi(SentenceClauseMap map, int verbIndex, CancellationToken ct)
    {
        WordSlot[] slots = map.Slots;
        bool[] peripheral = map.Peripheral;
        for (int k = verbIndex + 1; k < slots.Length && k <= verbIndex + 2; k++)
        {
            if (peripheral[k] || slots[k].Quoted || !map.AreSpaceBridged(k - 1, k, ct))
                return false;
            string ck = slots[k].Word.CanonicalText;
            if (ck == "ли") return true;
            if (!BulgarianFunctionWords.HasAny(ck,
                    FunctionWordClasses.PronominalVerbalClitic | FunctionWordClasses.ReflexiveAccusativeSe))
                return false;
        }
        return false;
    }

    private static bool HasOvertClauseSubjectBeforeModal(
        SentenceClauseMap map,
        WordSlot[] slots,
        bool[] peripheral,
        int modalSlotIndex,
        CancellationToken ct)
    {
        ClauseSpan? clause = map.Clauses.FirstOrDefault(c => c.WordIndices.Contains(modalSlotIndex));
        if (clause is null) return false;
        int[] idx = clause.WordIndices;
        int modalPos = Array.IndexOf(idx, modalSlotIndex);
        if (modalPos <= clause.BodyStartPos) return false;

        bool inPrep = false;
        for (int p = clause.BodyStartPos; p < modalPos; p++)
        {
            int wi = idx[p];
            if (peripheral[wi] || slots[wi].Quoted) continue;
            if (p > clause.BodyStartPos && map.HasCommaBetween(idx[p - 1], wi))
            {
                inPrep = false;
                continue;
            }
            WordSlot s = slots[wi];
            if (SentenceClauseMap.IsActivePreposition(s.Word, s.Active))
            {
                inPrep = true;
                continue;
            }
            if (inPrep)
            {
                if (SentenceClauseMap.HasNominalHeadReading(s) &&
                    !(p + 1 < modalPos && map.AreSpaceBridged(wi, idx[p + 1], ct) &&
                      BulgarianFunctionWords.HasAny(slots[idx[p + 1]].Word.CanonicalText, FunctionWordClasses.PossessiveOrDativeClitic)))
                {
                    inPrep = false;
                }
                continue;
            }
            WordSlot? prev = p > clause.BodyStartPos && map.AreSpaceBridged(idx[p - 1], wi, ct) ? slots[idx[p - 1]] : null;
            if (NominativePronounReadings(s, prev).Length > 0)
                return true;
            if (SentenceClauseMap.HasNominalHeadReading(s))
                return true;
        }
        return false;
    }

    private static bool HasObliqueRelativeConcessiveBeforeDa(WordSlot[] slots, int daIndex)
    {
        for (int k = Math.Max(0, daIndex - 4); k < daIndex; k++)
            if (slots[k].Word.CanonicalText is "кого" or "когото" or "кому" or "комуто")
                return true;
        return false;
    }

    private static bool TryGetInvalidParticleVerbReadings(
        TokenView word,
        bool disallowImperfect,
        bool disallowAoristAndActiveParticiple,
        out int[] readings)
    {
        readings = [];
        int[] realRows = SentenceClauseMap.NonPlaceholderReadings(word, sentenceInitial: false);
        if (realRows.Length == 0) return false;

        List<int> verbRows = [];
        foreach (int r in realRows)
        {
            ReadingView row = word.Readings[r];
            MorphologicalFeatures f = row.Features;
            if (f is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
                    Article: MorphologicalArticle.Indefinite,
                    HasConflictingFeatures: false
                } && Guard.HasNominalAxes(f))
            {
                continue;
            }
            if (f.PartOfSpeech != MorphologicalPartOfSpeech.Verb ||
                f.HasConflictingFeatures ||
                f is { VerbForm: MorphologicalVerbForm.Finite, Tense: MorphologicalTense.Present })
                return false;

            bool alwaysInvalidAfterParticle =
                f.VerbForm is MorphologicalVerbForm.Imperative or MorphologicalVerbForm.Converb ||
                f is
                {
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastPassive,
                    Article: null or MorphologicalArticle.Indefinite
                };
            bool invalidImperfect = disallowImperfect &&
                f is { VerbForm: MorphologicalVerbForm.Finite, Tense: MorphologicalTense.Imperfect } &&
                SentenceClauseMap.IsCleanOrBenignLegacyVerb(row);
            bool invalidAoristOrActiveParticiple = disallowAoristAndActiveParticiple &&
                (f is { VerbForm: MorphologicalVerbForm.Finite, Tense: MorphologicalTense.Aorist } &&
                 SentenceClauseMap.IsCleanOrBenignLegacyVerb(row) ||
                 f is
                 {
                     VerbForm: MorphologicalVerbForm.Participle,
                     ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                     Article: null or MorphologicalArticle.Indefinite
                 } && SentenceClauseMap.IsCleanOrBenignLegacyVerb(row));

            if (!alwaysInvalidAfterParticle && !invalidImperfect && !invalidAoristOrActiveParticiple)
                return false;
            verbRows.Add(r);
        }

        if (verbRows.Count == 0) return false;
        // Require pure verb rows unless every verb row is a bare PastPassive accompanied only by its syncretic indefinite adjective.
        if (verbRows.Count < realRows.Length &&
            !verbRows.All(r => word.Readings[r].Features is
            {
                VerbForm: MorphologicalVerbForm.Participle,
                ParticipleKind: MorphologicalParticipleKind.PastPassive,
                Article: null or MorphologicalArticle.Indefinite
            }))
            return false;

        readings = verbRows.ToArray();
        return true;
    }

    private static void AddParticleOrAuxMismatch(
        int contextSlotIndex,
        TokenView contextWord,
        int badVerbSlotIndex,
        TokenView badVerbWord,
        int[] badVerbReadings,
        List<VerbalInternalMismatch> mismatches)
    {
        int[] ctxReadings = Enumerable.Range(0, contextWord.Readings.Count)
            .Where(r => !Guard.IsSourcePlaceholder(contextWord, contextWord.Readings[r]))
            .ToArray();
        if (ctxReadings.Length == 0 || badVerbReadings.Length == 0) return;

        mismatches.Add(new VerbalInternalMismatch(
            VerbalInternalMismatchKind.ParticleOrAuxVerbForm,
            TargetSlotIndex: badVerbSlotIndex,
            TargetReadings: badVerbReadings,
            PartnerSlotIndex: contextSlotIndex,
            PartnerReadings: ctxReadings));
    }

    private static void AddPronounCaseAnomaly(
        int pronSlotIndex,
        TokenView pronWord,
        int partnerSlotIndex,
        TokenView partnerWord,
        List<VerbalInternalMismatch> mismatches)
    {
        int[] pronReadings = Enumerable.Range(0, pronWord.Readings.Count)
            .Where(r => !Guard.IsSourcePlaceholder(pronWord, pronWord.Readings[r]))
            .ToArray();
        int[] partnerReadings = Enumerable.Range(0, partnerWord.Readings.Count)
            .Where(r => !Guard.IsSourcePlaceholder(partnerWord, partnerWord.Readings[r]))
            .ToArray();
        if (pronReadings.Length == 0 || partnerReadings.Length == 0) return;

        mismatches.Add(new VerbalInternalMismatch(
            VerbalInternalMismatchKind.CliticClusterCaseOrOrder,
            TargetSlotIndex: pronSlotIndex,
            TargetReadings: pronReadings,
            PartnerSlotIndex: partnerSlotIndex,
            PartnerReadings: partnerReadings));
    }
}
