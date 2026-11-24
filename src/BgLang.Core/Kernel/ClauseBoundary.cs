using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using BgLang.Contracts;
using Category = BgLang.Core.ContextLanes.Category;
using Guard = BgLang.Core.ContextEvidenceGuards;
using ReadingView = BgLang.Core.ContextLanes.ReadingView;
using TokenView = BgLang.Core.ContextLanes.TokenView;

namespace BgLang.Core;

internal enum ClauseBoundaryKind
{
    SentenceInitial,
    ParentheticalRelative,
    ParentheticalCourtesy,
    Subordinator,
    PrepositionalSubordinator,
    PreposedSubordinatorComma,
    Coordinator,
    BareParticipleSegment,
    IndependentPunctuation,
    ReportingSpeech
}

internal readonly record struct ClauseBoundary(
    ClauseBoundaryKind Kind,
    int PrepositionWordIndex,
    int IntroducerFirstWordIndex,
    int IntroducerLastWordIndex,
    int CommaTokenIndex,
    bool HasConcessiveIntroducer)
{
    internal static ClauseBoundary Initial(bool hasConcessive = false) =>
        new(ClauseBoundaryKind.SentenceInitial, -1, -1, -1, -1, hasConcessive);
}

internal sealed record WordSlot(
    int Index,
    TokenView Word,
    int[] Active,
    bool Quoted,
    bool ComparativeGraphModifier,
    (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article)? HyphenatedCompoundNominal = null,
    (MorphologicalNumber Number, MorphologicalGender? Gender)? DerivedPersonName = null,
    bool PersonTitlePrefix = false,
    int RequestVerbWordIndex = -1);

internal sealed record ClauseSpan(
    int ClauseId,
    ClauseBoundary Boundary,
    int[] WordIndices,
    int BodyStartPos,
    bool Matrix,
    bool AllowsPerfectivePresent);

internal readonly record struct BareParticipleClauseSegment(
    int[] WordIndices,
    bool IntroducedByConjunction);

/// <summary>
/// Single-pass per-sentence slot pruning, adjacency/comma cache, parenthetical extraction,
/// prepositional subordinator binding ("Без [NP/Adv]* да/че ..."), and sequential clause segmentation.
/// Operates strictly over immutable complete-inventory reading indices with zero public surface delta.
/// </summary>
internal sealed class SentenceClauseMap
{
    private readonly bool[] _spaceNext;
    private readonly bool[] _clauseBridgeNext;
    private readonly bool[] _horizontalBridgeNext;
    private readonly int[] _commaTokenBefore;
    private readonly ClauseBoundary[] _prepSubordinatorsByPrep;
    private readonly int[] _prepSubordinatorByIntroducer;

    private SentenceClauseMap(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        WordSlot[] slots,
        bool[] peripheral,
        bool allExactOrDegree,
        bool hasUnexpectedSymbol,
        bool hasTerminalPunctuation,
        bool[] spaceNext,
        bool[] clauseBridgeNext,
        bool[] horizontalBridgeNext,
        int[] commaTokenBefore,
        ClauseBoundary[] prepSubordinatorsByPrep,
        int[] prepSubordinatorByIntroducer,
        IReadOnlyList<ClauseSpan> clauses,
        IReadOnlyList<BareParticipleClauseSegment> bareParticipleSegments,
        IReadOnlyList<(int Start, int End)> protectedSpans)
    {
        Analysis = analysis;
        Sentence = sentence;
        Words = words;
        Slots = slots;
        Peripheral = peripheral;
        AllExactOrDegree = allExactOrDegree;
        HasUnexpectedSymbol = hasUnexpectedSymbol;
        HasTerminalPunctuation = hasTerminalPunctuation;
        _spaceNext = spaceNext;
        _clauseBridgeNext = clauseBridgeNext;
        _horizontalBridgeNext = horizontalBridgeNext;
        _commaTokenBefore = commaTokenBefore;
        _prepSubordinatorsByPrep = prepSubordinatorsByPrep;
        _prepSubordinatorByIntroducer = prepSubordinatorByIntroducer;
        Clauses = clauses;
        BareParticipleSegments = bareParticipleSegments;
        ProtectedSpans = protectedSpans;
    }

    internal TextAnalysis Analysis { get; }
    internal SentenceSpan Sentence { get; }
    internal IReadOnlyList<TokenView> Words { get; }
    internal WordSlot[] Slots { get; }
    internal bool[] Peripheral { get; }
    internal bool AllExactOrDegree { get; }
    internal bool HasUnexpectedSymbol { get; }
    internal bool HasTerminalPunctuation { get; }
    internal IReadOnlyList<ClauseSpan> Clauses { get; }
    internal IReadOnlyList<BareParticipleClauseSegment> BareParticipleSegments { get; }
    internal IReadOnlyList<(int Start, int End)> ProtectedSpans { get; }

    internal static SentenceClauseMap Create(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        CancellationToken ct,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(words);
        ct.ThrowIfCancellationRequested();

        int count = words.Count;
        if (count == 0)
        {
            return new SentenceClauseMap(
                analysis, sentence, words, [], [],
                allExactOrDegree: true,
                hasUnexpectedSymbol: false,
                hasTerminalPunctuation: ComputeTerminalPunctuation(analysis, sentence),
                spaceNext: [],
                clauseBridgeNext: [],
                horizontalBridgeNext: [],
                commaTokenBefore: [],
                prepSubordinatorsByPrep: [],
                prepSubordinatorByIntroducer: [],
                clauses: [],
                bareParticipleSegments: [],
                protectedSpans: protectedSpans);
        }

        bool[] peripheral = Guard.MarkPeripheralWords(analysis.Text, sentence, words, ct);
        bool QuotedWord(TokenView w) =>
            protectedSpans.Any(s => s.Start < w.Token.Start + w.Token.Length && w.Token.Start < s.End);
        bool[] spaceNext = new bool[Math.Max(0, count - 1)];
        bool[] clauseBridgeNext = new bool[Math.Max(0, count - 1)];
        bool[] horizontalBridgeNext = new bool[Math.Max(0, count - 1)];
        for (int i = 0; i + 1 < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            spaceNext[i] = LocalClauseRoleLinker.Connected(analysis, words[i], words[i + 1], symbols: false, ct);
            clauseBridgeNext[i] = spaceNext[i] || Guard.WordOrPictographBridge(analysis, words[i], words[i + 1], ct, horizontalOnly: false);
            horizontalBridgeNext[i] = Guard.WordOrPictographBridge(analysis, words[i], words[i + 1], ct, horizontalOnly: true);
        }

        (MorphologicalNumber Number, MorphologicalGender? Gender)?[]? derivedPersonNames = null;
        for (int wi = 0; wi < count; wi++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[wi] || QuotedWord(words[wi])) continue;
            if (ProperNameAnalyzer.TryResolveContextualSurname(analysis, sentence, words, wi, out var surnameAxes, lookup, ct))
            {
                derivedPersonNames ??= new (MorphologicalNumber, MorphologicalGender?)?[count];
                derivedPersonNames[wi] = surnameAxes;
            }
        }

        bool allExactOrDegree = true;
        (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article)?[]? hyphenCompounds = null;
        bool[]? personTitles = null;
        for (int wi = 0; wi < count; wi++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[wi] || QuotedWord(words[wi])) continue;
            TokenView w = words[wi];
            if (!w.UsesGraph && w.Readings.Count == 0 && lookup is not null &&
                TryResolveHyphenatedNominalCompound(w, IsSentenceInitialWord(analysis, sentence, wi, w), lookup, out var compAxes, ct))
            {
                hyphenCompounds ??= new (MorphologicalNumber, MorphologicalGender?, MorphologicalArticle)?[count];
                hyphenCompounds[wi] = compAxes;
                continue;
            }
            if (ProperNameEvidenceAnalyzer.IsPersonTitle(w.Token) &&
                wi + 1 < count && spaceNext[wi] && !peripheral[wi + 1] && !QuotedWord(words[wi + 1]))
            {
                TokenView nextW = words[wi + 1];
                if (derivedPersonNames?[wi + 1] is not null ||
                    ProperNameEvidenceAnalyzer.IsBulgarianTitleCase(nextW.Token.Text) &&
                    nextW.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun &&
                        r.Features.NameKind != MorphologicalNameKind.PlaceName))
                {
                    (personTitles ??= new bool[count])[wi] = true;
                    continue;
                }
            }
            if (derivedPersonNames?[wi] is not null)
                continue;
            if (w.UsesGraph && !IsDegreeGraphModifier(w) ||
                !w.UsesGraph && (!w.Complete || w.Readings.Count == 0))
            {
                allExactOrDegree = false;
                break;
            }
        }

        int[] commaTokenBefore = ComputeCommaTokensBefore(analysis, sentence, words, ct);
        WordSlot[] slots = FilterSlots(analysis, sentence, words, protectedSpans, spaceNext, clauseBridgeNext, hyphenCompounds, derivedPersonNames, personTitles, ct);

        ClauseBoundary[] prepByPrep = new ClauseBoundary[count];
        int[] prepByIntroducer = new int[count];
        Array.Fill(prepByIntroducer, -1);
        ScanPrepositionalSubordinators(slots, peripheral, spaceNext, commaTokenBefore, prepByPrep, prepByIntroducer, ct);

        bool hasUnexpectedSymbol = Guard.HasUnexpectedNonPictographSymbol(
            analysis, slots[0].Word.OriginalIndex, slots[^1].Word.OriginalIndex, protectedSpans);
        bool hasTerminalPunctuation = ComputeTerminalPunctuation(analysis, sentence);

        IReadOnlyList<ClauseSpan> clauses = allExactOrDegree
            ? SegmentClauses(analysis, sentence, words, slots, peripheral, spaceNext, commaTokenBefore, prepByPrep, ct)
            : [];

        IReadOnlyList<BareParticipleClauseSegment> bareSegments =
            SegmentBareParticipleClauses(analysis, words, slots, peripheral, horizontalBridgeNext, ct);

        return new SentenceClauseMap(
            analysis,
            sentence,
            words,
            slots,
            peripheral,
            allExactOrDegree,
            hasUnexpectedSymbol,
            hasTerminalPunctuation,
            spaceNext,
            clauseBridgeNext,
            horizontalBridgeNext,
            commaTokenBefore,
            prepByPrep,
            prepByIntroducer,
            clauses,
            bareSegments,
            protectedSpans);
    }

    internal bool AreSpaceBridged(int leftWordIndex, int rightWordIndex, CancellationToken ct = default)
    {
        if (leftWordIndex < 0 || rightWordIndex <= leftWordIndex || rightWordIndex >= Words.Count)
            return false;
        if (rightWordIndex == leftWordIndex + 1)
            return _spaceNext[leftWordIndex];
        return LocalClauseRoleLinker.Connected(Analysis, Words[leftWordIndex], Words[rightWordIndex], symbols: false, ct);
    }

    internal bool AreClauseWordBridged(int leftWordIndex, int rightWordIndex, CancellationToken ct = default)
    {
        if (leftWordIndex < 0 || rightWordIndex <= leftWordIndex || rightWordIndex >= Words.Count)
            return false;
        if (rightWordIndex == leftWordIndex + 1)
            return _clauseBridgeNext[leftWordIndex];
        return Guard.WordOrPictographBridge(Analysis, Words[leftWordIndex], Words[rightWordIndex], ct, horizontalOnly: false);
    }

    internal bool AreHorizontalWordBridged(int leftWordIndex, int rightWordIndex, CancellationToken ct = default)
    {
        if (leftWordIndex < 0 || rightWordIndex <= leftWordIndex || rightWordIndex >= Words.Count)
            return false;
        if (rightWordIndex == leftWordIndex + 1)
            return _horizontalBridgeNext[leftWordIndex];
        return Guard.WordOrPictographBridge(Analysis, Words[leftWordIndex], Words[rightWordIndex], ct, horizontalOnly: true);
    }

    internal bool HasCommaBetween(int leftWordIndex, int rightWordIndex)
    {
        if (leftWordIndex < 0 || rightWordIndex <= leftWordIndex || rightWordIndex >= Words.Count)
            return false;
        if (rightWordIndex == leftWordIndex + 1)
            return _commaTokenBefore[rightWordIndex] >= 0;
        int from = Words[leftWordIndex].Token.Start + Words[leftWordIndex].Token.Length;
        int to = Words[rightWordIndex].Token.Start;
        return to > from && Analysis.Text.AsSpan(from, to - from).Contains(',');
    }

    internal int CommaTokenBefore(int wordIndex) =>
        (uint)wordIndex < (uint)_commaTokenBefore.Length ? _commaTokenBefore[wordIndex] : -1;

    internal bool HasDirectCommaBetween(int[] clauseWordIndices, int leftPos, int rightPos)
    {
        for (int p = leftPos; p < rightPos; p++)
            if (clauseWordIndices[p + 1] == clauseWordIndices[p] + 1 &&
                HasCommaBetween(clauseWordIndices[p], clauseWordIndices[p + 1]))
                return true;
        return false;
    }

    // Bridge only an entire, already identified parenthetical at both original comma boundaries.
    internal bool HasDelimitedParentheticalBetween(int leftWordIndex, int rightWordIndex, CancellationToken ct = default)
    {
        if (!AllExactOrDegree || leftWordIndex < 0 || rightWordIndex <= leftWordIndex + 1 ||
            rightWordIndex >= Slots.Length || Slots[leftWordIndex].Quoted || Slots[rightWordIndex].Quoted ||
            !IsExactWord(Slots[leftWordIndex]) || !IsExactWord(Slots[rightWordIndex]))
            return false;
        foreach (ClauseSpan clause in Clauses)
        {
            ct.ThrowIfCancellationRequested();
            int[] indices = clause.WordIndices;
            if (clause.Boundary.Kind is not (ClauseBoundaryKind.ParentheticalRelative or ClauseBoundaryKind.ParentheticalCourtesy) ||
                indices.Length != rightWordIndex - leftWordIndex - 1 ||
                indices[0] != leftWordIndex + 1 || indices[^1] != rightWordIndex - 1)
                continue;
            for (int p = 0; p < indices.Length; p++)
            {
                ct.ThrowIfCancellationRequested();
                if (indices[p] != leftWordIndex + p + 1 || Slots[indices[p]].Quoted || !IsExactWord(Slots[indices[p]]))
                    return false;
            }
            return HasOnlyBoundaryPunctuation(Analysis, Words[leftWordIndex], Words[leftWordIndex + 1], ',') &&
                HasOnlyBoundaryPunctuation(Analysis, Words[rightWordIndex - 1], Words[rightWordIndex], ',');
        }
        return false;
    }

    internal bool IsPrepositionalSubordinatorPrep(int prepWordIndex) =>
        (uint)prepWordIndex < (uint)_prepSubordinatorsByPrep.Length &&
        _prepSubordinatorsByPrep[prepWordIndex].Kind == ClauseBoundaryKind.PrepositionalSubordinator;

    internal bool TryGetPrepositionalSubordinatorBoundary(int prepWordIndex, out ClauseBoundary boundary)
    {
        if ((uint)prepWordIndex < (uint)_prepSubordinatorsByPrep.Length &&
            _prepSubordinatorsByPrep[prepWordIndex].Kind == ClauseBoundaryKind.PrepositionalSubordinator)
        {
            boundary = _prepSubordinatorsByPrep[prepWordIndex];
            return true;
        }
        boundary = default;
        return false;
    }

    internal bool TryGetPrepositionalSubordinatorForIntroducer(int introducerWordIndex, out int prepWordIndex)
    {
        if ((uint)introducerWordIndex < (uint)_prepSubordinatorByIntroducer.Length &&
            _prepSubordinatorByIntroducer[introducerWordIndex] >= 0)
        {
            prepWordIndex = _prepSubordinatorByIntroducer[introducerWordIndex];
            return true;
        }
        prepWordIndex = -1;
        return false;
    }

    internal static bool IsDegreeGraphModifier(TokenView word) =>
        word.UsesGraph &&
        word.CanonicalText is not ("по-малко" or "най-малко" or "по-много" or "най-много") &&
        (word.CanonicalText.StartsWith("по-", StringComparison.Ordinal) ||
         word.CanonicalText.StartsWith("най-", StringComparison.Ordinal));

    internal static bool IsExactWord(WordSlot slot) =>
        !slot.Word.UsesGraph && slot.Word.Complete && slot.Word.Readings.Count > 0;

    internal static bool LeadingDialogueOrSpace(string text, int sentenceStart, int firstWordStart) =>
        sentenceStart >= 0 && firstWordStart >= sentenceStart && firstWordStart <= text.Length &&
        Guard.IsCleanTriviaOrPictographSpan(text.AsSpan(sentenceStart, firstWordStart - sentenceStart));

    internal static bool IsSentenceInitialWord(TextAnalysis analysis, SentenceSpan sentence, int wordIndex, TokenView word) =>
        wordIndex == 0 && LeadingDialogueOrSpace(analysis.Text, sentence.Start, word.Token.Start);

    internal static int[] NonPlaceholderReadings(TokenView word, bool sentenceInitial)
    {
        bool hasReal = word.Readings.Any(r => !Guard.IsSourcePlaceholder(word, r));
        List<int> list = [];
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ReadingView row = word.Readings[i];
            if (hasReal && Guard.IsSourcePlaceholder(word, row) ||
                Guard.IsUnwrittenCapitalizedReading(word, row, sentenceInitial) ||
                Guard.IsSuppressedPresentActiveNounHomograph(word, row) ||
                Guard.IsSyncreticVocativeCompanion(word, row))
                continue;
            list.Add(i);
        }
        return list.ToArray();
    }

    internal static bool IsCleanOrBenignLegacyVerb(ReadingView row)
    {
        MorphologicalFeatures f = row.Features;
        if (f.HasConflictingFeatures || f.VerbForm is null) return false;
        if (!f.HasUnrecognizedFeatures) return true;
        if (row.Reading is not { PartOfSpeech: { } rawTag } || f != MorphologicalFeatures.FromTag(rawTag))
            return false;
        string stripped = rawTag
            .Replace(",+си", "", StringComparison.Ordinal)
            .Replace(",+се", "", StringComparison.Ordinal)
            .Replace(";+си", "", StringComparison.Ordinal)
            .Replace(";+се", "", StringComparison.Ordinal);
        return stripped != rawTag && !MorphologicalFeatures.FromTag(stripped).HasUnrecognizedFeatures;
    }

    internal static int[] FiniteReadings(WordSlot slot)
    {
        if (slot.Word.UsesGraph) return [];
        return slot.Active.Where(r =>
        {
            ReadingView row = slot.Word.Readings[r];
            MorphologicalFeatures f = row.Features;
            return f.VerbForm == MorphologicalVerbForm.Finite && Guard.IsPersonalPredicate(f) && IsCleanOrBenignLegacyVerb(row);
        }).ToArray();
    }

    internal static int[] AllFinitePersonalReadings(TokenView word, CancellationToken ct)
    {
        List<int> list = [];
        for (int r = 0; r < word.Readings.Count; r++)
        {
            ct.ThrowIfCancellationRequested();
            ReadingView row = word.Readings[r];
            MorphologicalFeatures f = row.Features;
            if (f.VerbForm == MorphologicalVerbForm.Finite && Guard.IsPersonalPredicate(f) && IsCleanOrBenignLegacyVerb(row))
                list.Add(r);
        }
        return list.ToArray();
    }

    internal static bool HasFiniteReading(WordSlot slot) => FiniteReadings(slot).Length > 0;

    internal static bool HasPureImperativeReading(WordSlot slot)
    {
        if (!IsExactWord(slot) || slot.Active.Length == 0) return false;
        foreach (int index in slot.Active)
        {
            var reading = slot.Word.Readings[index];
            if (reading.Features is not { VerbForm: MorphologicalVerbForm.Imperative, Person: MorphologicalPerson.Second } ||
                !Guard.IsPersonalPredicate(reading.Features) || !IsCleanOrBenignLegacyVerb(reading)) return false;
        }
        return true;
    }

    private static bool RangeHasClausePredicate(WordSlot[] slots, int[] indices, int fromPos, int toPos, bool includeImperatives)
    {
        for (int i = fromPos; i <= toPos; i++)
        {
            WordSlot word = slots[indices[i]];
            if (!word.Quoted && (HasFiniteReading(word) || includeImperatives && HasPureImperativeReading(word))) return true;
        }
        return false;
    }

    internal static bool IsActivePreposition(TokenView word, IReadOnlyList<int> active)
    {
        if (word.UsesGraph || active.Count == 0) return false;
        return active.Any(r => word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition) &&
               active.All(r => word.Readings[r].Features.PartOfSpeech is MorphologicalPartOfSpeech.Preposition or MorphologicalPartOfSpeech.Particle ||
                               word.CanonicalText == "като" && word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction);
    }

    internal static bool IsRelativeWord(string canonical) =>
        BulgarianFunctionWords.HasAny(canonical, FunctionWordClasses.RelativeWord);

    internal static bool IsClauseIntroducer(TokenView word, IReadOnlyList<int> active) =>
        BulgarianFunctionWords.HasAny(word.CanonicalText, FunctionWordClasses.ClauseIntroducer);

    internal static bool IsCoordinatingConjunction(WordSlot slot) => IsCoordinatingConjunction(slot.Word);

    private static bool IsCoordinatingConjunction(TokenView word) =>
        BulgarianFunctionWords.HasAny(word.CanonicalText, FunctionWordClasses.ClauseCoordinatingConjunction) &&
        !word.Readings.Any(r => r.Category == Category.Noun && !Guard.IsSourcePlaceholder(word, r));

    internal static int[] NominalHeadReadings(WordSlot slot)
    {
        if (slot.Word.UsesGraph) return [];
        int[] common = slot.Active.Where(r =>
        {
            MorphologicalFeatures f = slot.Word.Readings[r].Features;
            return f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                   (Guard.HasNominalAxes(f) || f.Number == MorphologicalNumber.Count && f.Gender is not null);
        }).ToArray();
        if (common.Length > 0) return common;
        return slot.Active.Where(r => slot.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun &&
            !slot.Word.Readings[r].Features.HasConflictingFeatures).ToArray();
    }

    internal static bool HasNominalHeadReading(WordSlot slot) =>
        slot.HyphenatedCompoundNominal is not null || slot.DerivedPersonName is not null || NominalHeadReadings(slot).Length > 0;

    internal static bool IsNominalModifierOrDeterminer(WordSlot slot) =>
        !slot.Word.UsesGraph && slot.Active.Any(r =>
        {
            ReadingView row = slot.Word.Readings[r];
            return row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(row.Features) ||
                   Guard.IsMeasuredPassiveAttribute(row.Features) ||
                   Guard.IsMeasuredPresentActiveAttribute(row.Features) ||
                   Guard.TryGetOrdinalAxes(row, out _) ||
                   Guard.TryGetDeterminerAxes(slot.Word, row, out _);
        });

    internal static bool HasPureNominal(TokenView w, IReadOnlyList<int> active) =>
        !w.UsesGraph && active.Count > 0 && active.All(r => w.Readings[r].Category is Category.Noun or Category.Adjective);

    internal static bool HasConcessiveIntroducerBeforeDa(WordSlot[] slots, int daIndex)
    {
        for (int k = Math.Max(0, daIndex - 4); k < daIndex; k++)
        {
            if (IsConcessiveOrOptativeCue(slots[k].Word.CanonicalText))
                return true;
        }
        return false;
    }

    internal static bool IsConcessiveOrOptativeCue(string canonical) =>
        canonical is "макар" or "въпреки" or "дори" or "даже" or "ако" or "сякаш" or "нека" or "дано" ||
        canonical.StartsWith("как", StringComparison.Ordinal) ||
        canonical.StartsWith("колк", StringComparison.Ordinal) ||
        canonical.StartsWith("къде", StringComparison.Ordinal) ||
        canonical.StartsWith("кой", StringComparison.Ordinal) ||
        canonical.StartsWith("коя", StringComparison.Ordinal) ||
        canonical.StartsWith("кое", StringComparison.Ordinal) ||
        canonical.StartsWith("кои", StringComparison.Ordinal) ||
        canonical.StartsWith("чий", StringComparison.Ordinal) ||
        canonical.StartsWith("чия", StringComparison.Ordinal) ||
        canonical.StartsWith("чие", StringComparison.Ordinal) ||
        canonical.StartsWith("чии", StringComparison.Ordinal);

    private static int[] ComputeCommaTokensBefore(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        CancellationToken ct)
    {
        int[] commaTokens = new int[words.Count];
        Array.Fill(commaTokens, -1);
        for (int i = 0; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int leftTok = i == 0 ? -1 : words[i - 1].OriginalIndex;
            int rightTok = words[i].OriginalIndex;
            for (int t = rightTok - 1; t > leftTok && t >= 0; t--)
            {
                TokenAnalysis tok = analysis.Tokens[t];
                if (tok.Start < sentence.Start) break;
                if (tok.Kind == TokenKind.Punctuation && tok.Text.Contains(',', StringComparison.Ordinal))
                {
                    commaTokens[i] = t;
                    break;
                }
            }
        }
        return commaTokens;
    }

    private static WordSlot[] FilterSlots(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        bool[] spaceNext,
        bool[] clauseBridgeNext,
        (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article)?[]? hyphenCompounds,
        (MorphologicalNumber Number, MorphologicalGender? Gender)?[]? derivedPersonNames,
        bool[]? personTitles,
        CancellationToken ct)
    {
        var rawActive = new List<int>[words.Count];
        bool[] quoted = new bool[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            TokenView w = words[i];
            int wStart = w.Token.Start, wEnd = wStart + w.Token.Length;
            quoted[i] = protectedSpans.Any(s => s.Start < wEnd && wStart < s.End);
            if (w.UsesGraph || !w.Complete || w.Readings.Count == 0)
            {
                rawActive[i] = [];
                continue;
            }
            bool sentenceInitial = i == 0 && LeadingDialogueOrSpace(analysis.Text, sentence.Start, wStart);
            bool hasReal = w.Readings.Any(r => !Guard.IsSourcePlaceholder(w, r));
            List<int> keep = [];
            for (int r = 0; r < w.Readings.Count; r++)
            {
                ct.ThrowIfCancellationRequested();
                ReadingView row = w.Readings[r];
                if (hasReal && Guard.IsSourcePlaceholder(w, row)) continue;
                if (Guard.IsUnwrittenCapitalizedReading(w, row, sentenceInitial)) continue;
                if (Guard.IsSuppressedPresentActiveNounHomograph(w, row)) continue;
                if (Guard.IsSyncreticVocativeCompanion(w, row)) continue;
                keep.Add(r);
            }
            if (keep.Count == 0)
                for (int r = 0; r < w.Readings.Count; r++) { ct.ThrowIfCancellationRequested(); keep.Add(r); }
            rawActive[i] = keep;
        }

        for (int i = 0; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            TokenView w = words[i];
            if (w.UsesGraph || rawActive[i].Count <= 1) continue;
            string c = w.CanonicalText;
            bool prevAdjacent = i > 0 && spaceNext[i - 1];
            bool nextAdjacent = i + 1 < words.Count && spaceNext[i];
            bool nextClauseBridge = i + 1 < words.Count && clauseBridgeNext[i];

            if (c == "ще" && HasUpcomingVerb(words, rawActive, spaceNext, i + 1))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Particle);

            else if (c == "ми" &&
                     (prevAdjacent && HasNominalOrDegree(words[i - 1], rawActive[i - 1]) &&
                          SentenceHasAnotherVerb(words, rawActive, quoted, i) ||
                      prevAdjacent && IsPurePersonalVerb(words[i - 1], rawActive[i - 1]) ||
                      nextAdjacent && (HasUpcomingVerb(words, rawActive, spaceNext, i + 1) ||
                                       words[i + 1].CanonicalText is "да" or "се")))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category != Category.Verb);

            else if (c == "си" &&
                     (prevAdjacent && HasPureNominal(words[i - 1], rawActive[i - 1]) ||
                      prevAdjacent && IsPurePersonalVerb(words[i - 1], rawActive[i - 1]) ||
                      nextAdjacent && HasUpcomingVerb(words, rawActive, spaceNext, i + 1)))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category != Category.Verb);

            else if (BulgarianFunctionWords.HasAny(c, FunctionWordClasses.AdpositionHomographPrunable) &&
                     nextAdjacent && IsAdpositionComplementAfterHomograph(words, rawActive, quoted, spaceNext, clauseBridgeNext, i))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition);

            else if (c == "хиляди" && nextAdjacent && HasPluralNoun(words[i + 1], rawActive[i + 1]))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category != Category.Verb);

            else if (c == "хора" && !(prevAdjacent && (IsActivePreposition(words[i - 1], rawActive[i - 1]) ||
                     HasSingularModifierForHora(words[i - 1], rawActive[i - 1]))))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Noun &&
                    w.Readings[r].Features.Number == MorphologicalNumber.Plural);

            else if (nextAdjacent &&
                     !IsAfterModalOrFutureParticle(words, spaceNext, i) &&
                     (!HasVerbReading(w, rawActive[i]) ||
                      !(i > 0 && prevAdjacent &&
                        (HasNounReading(words[i - 1], rawActive[i - 1]) ||
                         BulgarianFunctionWords.TryGetPersonalSubjectPronoun(words[i - 1].CanonicalText, out _))) ||
                      SentenceHasAnotherVerb(words, rawActive, quoted, i)) &&
                     HasAgreeingNounPhraseAhead(words, rawActive, spaceNext, clauseBridgeNext, i))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Adjective ||
                    Guard.IsMeasuredPassiveAttribute(w.Readings[r].Features) ||
                    Guard.IsMeasuredPresentActiveAttribute(w.Readings[r].Features) ||
                    Guard.TryGetOrdinalAxes(w.Readings[r], out _));

            else if (prevAdjacent && words[i - 1].CanonicalText == "се" &&
                     HasVerbReading(w, rawActive[i]) &&
                     !SentenceHasAnotherVerb(words, rawActive, quoted, i))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Verb);

            else if (prevAdjacent &&
                     HasVerbReading(w, rawActive[i]) &&
                     HasAgreeingIndefiniteNounAfterModifier(words[i - 1], rawActive[i - 1], w, rawActive[i],
                         allowArticledModifier: SentenceHasAnotherVerb(words, rawActive, quoted, i)))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Noun);

            else if (!HasVerbReading(w, rawActive[i]) &&
                     HasCleanUnarticledActiveParticiple(w, rawActive[i]) &&
                     HasIndefiniteNounReading(w, rawActive[i]) &&
                     IsPrecededByFirstOrSecondSubjectPronoun(words, spaceNext, i) &&
                     !SentenceHasAnotherVerb(words, rawActive, quoted, i) &&
                     nextAdjacent && StartsNominalMaterial(words, rawActive, i + 1))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Verb);

            else if (!(nextAdjacent && HasNounReading(words[i + 1], rawActive[i + 1])) &&
                     rawActive[i].Any(r => w.Readings[r].Category == Category.Noun &&
                         w.Readings[r].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short) &&
                     rawActive[i].All(r => w.Readings[r].Category is Category.Noun or Category.Adjective))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Noun);

            if (nextAdjacent && IsActivePreposition(words[i + 1], rawActive[i + 1]) &&
                rawActive[i].Any(r => w.Readings[r].Category == Category.Noun &&
                    w.Readings[r].Features.Article == MorphologicalArticle.Definite) &&
                !HasCoordinatedAccusativeFinitePrefix(words, rawActive, quoted, spaceNext, i, ct) &&
                (i == 0 || !prevAdjacent || words[i - 1].CanonicalText != "вие") &&
                (!HasVerbReading(w, rawActive[i]) ||
                 i == 0 || !prevAdjacent ||
                 IsActivePreposition(words[i - 1], rawActive[i - 1]) ||
                 IsPureAdnominalModifier(words[i - 1], rawActive[i - 1]) ||
                 SentenceHasAnotherVerb(words, rawActive, quoted, i)))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Noun);
            else if (prevAdjacent &&
                     rawActive[i].Any(r => w.Readings[r].Category == Category.Noun &&
                         w.Readings[r].Features is { Article: MorphologicalArticle.Definite, Number: MorphologicalNumber.Plural }) &&
                     rawActive[i].Any(r => w.Readings[r].Category == Category.Verb &&
                         w.Readings[r].Features is { VerbForm: MorphologicalVerbForm.Finite, Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Plural }) &&
                     !SentenceHasAnotherVerb(words, rawActive, quoted, i) &&
                     !(nextAdjacent && words[i + 1].CanonicalText == "на") &&
                     IsPrecededBySubjectOrCliticChain(words, rawActive, spaceNext, i))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Verb);
            else if (nextClauseBridge && words[i + 1].CanonicalText != "да" &&
                     rawActive[i].Any(r => w.Readings[r].Category == Category.Noun &&
                         w.Readings[r].Features is { Article: MorphologicalArticle.Definite, Number: MorphologicalNumber.Plural }) &&
                     rawActive[i].Where(r => w.Readings[r].Category == Category.Verb).All(r =>
                         w.Readings[r].Features is { Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Plural } ||
                         w.Readings[r].Features.VerbForm == MorphologicalVerbForm.Imperative) &&
                     (i == 0 || !prevAdjacent || words[i - 1].CanonicalText != "вие" &&
                      !BulgarianFunctionWords.HasAny(words[i - 1].CanonicalText, FunctionWordClasses.VerbalPrefixOrReflexive)) &&
                     HasUpcomingClauseVerb(words, rawActive, clauseBridgeNext, i + 1))
                PruneIfNonEmpty(rawActive[i], r => w.Readings[r].Category == Category.Noun);

            if (c == "вие" && nextClauseBridge && words[i + 1].CanonicalText != "да" &&
                (i == 0 || !prevAdjacent ||
                 !BulgarianFunctionWords.HasAny(words[i - 1].CanonicalText, FunctionWordClasses.VerbalPrefixOrReflexive) &&
                 !HasSingularThirdSubjectBefore(words[i - 1], rawActive[i - 1])) &&
                HasUpcomingClauseVerb(words, rawActive, clauseBridgeNext, i + 1))
                PruneIfNonEmpty(rawActive[i], r => Guard.LicensedSubject("вие", w.Readings[r]));
        }

        for (int i = 0; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsActivePreposition(words[i], rawActive[i])) continue;
            for (int j = i + 1; j < words.Count && spaceNext[j - 1]; j++)
            {
                TokenView w = words[j];
                if (w.UsesGraph) continue;
                if (j == i + 1 && BulgarianFunctionWords.IsNominalFocusParticle(w.CanonicalText))
                    continue;
                if (IsActivePreposition(w, rawActive[j]) || IsClauseIntroducer(w, rawActive[j])) break;
                bool hasNominal = rawActive[j].Any(r => w.Readings[r].Category is Category.Noun or Category.Adjective or Category.Number ||
                    Guard.TryGetDeterminerAxes(w, w.Readings[r], out _) ||
                    Guard.TryGetOrdinalAxes(w.Readings[r], out _) ||
                    Guard.IsMeasuredPresentActiveAttribute(w.Readings[r].Features));
                if (!hasNominal) break;
                PruneIfNonEmpty(rawActive[j], r => w.Readings[r].Category != Category.Verb ||
                    Guard.IsMeasuredPassiveAttribute(w.Readings[r].Features) ||
                    Guard.IsMeasuredPresentActiveAttribute(w.Readings[r].Features));
                if (rawActive[j].All(r => w.Readings[r].Category == Category.Noun))
                    break;
            }
        }

        var result = new WordSlot[words.Count];
        for (int i = 0; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int governor = -1;
            var active = rawActive[i];
            if (words[i].CanonicalText is "те" or "ти" or "ви" && RequestCliticBinding(i) is { } binding &&
                active.Any(r => IsRequestClitic(words[i].Readings[r], binding.Case)))
            {
                active.RemoveAll(r => !IsRequestClitic(words[i].Readings[r], binding.Case));
                governor = binding.Verb;
            }
            result[i] = new(i, words[i], active.ToArray(), quoted[i], IsDegreeGraphModifier(words[i]),
                hyphenCompounds?[i], derivedPersonNames?[i], personTitles?[i] == true, governor);
        }
        return result;

        (int Verb, MorphologicalCase Case)? RequestCliticBinding(int at)
        {
            if (quoted[at] || at == 0 || !spaceNext[at - 1] || quoted[at - 1]) return null;
            TokenView previous = words[at - 1];
            bool adjacent = at + 1 < words.Count && spaceNext[at];
            bool reflexive = adjacent && !quoted[at + 1] && IsRequestReflexive(words[at + 1], rawActive[at + 1]);
            if (IsRequestVerb(previous, rawActive[at - 1]))
            {
                if (reflexive) return (at - 1, MorphologicalCase.Dative);
                return !adjacent || words[at + 1].CanonicalText is "за" or "да"
                    ? (at - 1, MorphologicalCase.Accusative) : null;
            }

            // An object clitic leans on the preceding word. A nominal host can also take a possessive.
            int verb = at + (reflexive ? 2 : 1);
            if (!adjacent || verb >= words.Count || !spaceNext[verb - 1] || quoted[verb] ||
                !IsRequestVerb(words[verb], rawActive[verb]) ||
                !(IsRequestAdverb(previous, rawActive[at - 1]) ||
                  rawActive[at - 1].Any(r => Guard.LicensedSubject(previous.CanonicalText, previous.Readings[r]) ||
                      previous.CanonicalText == "не" && previous.Readings[r].Category == Category.Particle)))
                return null;
            return (verb, reflexive ? MorphologicalCase.Dative : MorphologicalCase.Accusative);
        }
    }

    private static void PruneIfNonEmpty(List<int> active, Func<int, bool> predicate)
    {
        if (active.Any(predicate))
            active.RemoveAll(r => !predicate(r));
    }

    /// <summary>
    /// Recognizes first-class PrepositionalSubordinator spans ("Prep + [RaisedSubjectNP | Pronoun | Coord | Adv | Neg]* + да/че + VerbGroup")
    /// with zero intervening non-"вие" finite verb, retiring ClauseFusionAnalyzer.FollowedByPrepositionalClauseSubordinator and its 5-word limit.
    /// </summary>
    private static void ScanPrepositionalSubordinators(
        WordSlot[] slots,
        bool[] peripheral,
        bool[] spaceNext,
        int[] commaTokenBefore,
        ClauseBoundary[] prepByPrep,
        int[] prepByIntroducer,
        CancellationToken ct)
    {
        int coreStart = 0;
        while (coreStart < slots.Length && peripheral[coreStart]) coreStart++;

        for (int i = 0; i + 2 < slots.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (peripheral[i] || slots[i].Quoted || !IsExactWord(slots[i])) continue;
            string prep = slots[i].Word.CanonicalText;

            // Only subordinating clause prepositions can head a PrepositionalSubordinator;
            // true matrix prepositions (при, с, на, в, от, до, към, пред, над, под, между, срещу, etc.) NEVER do.
            if (prep is not ("без" or "преди" or "вместо" or "освен" or "за" or "въпреки"))
                continue;

            bool activePrep = IsActivePreposition(slots[i].Word, slots[i].Active);
            bool homographSubPrep = !activePrep && prep is "преди" or "вместо" or "освен" or "въпреки" &&
                slots[i].Active.Any(r =>
                    slots[i].Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition);
            if (!activePrep && !homographSubPrep) continue;

            if (peripheral[i + 1] || slots[i + 1].Quoted || !spaceNext[i]) continue;
            string firstAfterPrep = slots[i + 1].Word.CanonicalText;

            // Never excuse an accusative/dative verbal clitic after a preposition ("*Без ме да питат", "*За я да вземе").
            if (BulgarianFunctionWords.HasAny(firstAfterPrep, FunctionWordClasses.ShortPronominalClitic) &&
                !BulgarianFunctionWords.TryGetPersonalSubjectPronoun(firstAfterPrep, out _))
                continue;

            bool atClauseStart = i == coreStart || commaTokenBefore[i] >= 0 ||
                i > 0 && IsCoordinatingConjunction(slots[i - 1]);
            bool startsWithNomPronoun = BulgarianFunctionWords.TryGetPersonalSubjectPronoun(firstAfterPrep, out _);

            int subIdx = -1;
            int nominalChunks = 0;
            bool inModifierChain = false;
            bool modifierArticledNonDet = false;
            bool lastWasNounHead = false;
            bool lastWasNomPronoun = false;
            bool prevWasAdv = false;
            bool coordNomPronPending = false;
            bool coordAdvPending = false;

            for (int k = i + 1; k < slots.Length; k++)
            {
                if (peripheral[k] || slots[k].Quoted || !spaceNext[k - 1])
                    break;
                WordSlot sk = slots[k];
                string ck = sk.Word.CanonicalText;
                if (ck == "да" && prep != "въпреки")
                {
                    if (!coordNomPronPending && !coordAdvPending &&
                        HasUpcomingDaPredicate(slots, peripheral, spaceNext, k + 1))
                        subIdx = k;
                    break;
                }
                if (ck == "че" && k == i + 1 && prep is "освен" or "вместо" or "въпреки")
                {
                    if (k + 1 < slots.Length && !peripheral[k + 1] && !slots[k + 1].Quoted && spaceNext[k])
                        subIdx = k;
                    break;
                }
                if (ck != "вие" && HasFiniteReading(sk))
                    break;
                if (IsClauseIntroducer(sk.Word, sk.Active) || IsActivePreposition(sk.Word, sk.Active))
                    break;

                bool isNomPron = BulgarianFunctionWords.TryGetPersonalSubjectPronoun(ck, out _) || ck == "никой";
                if (isNomPron)
                {
                    if (coordAdvPending) { nominalChunks = 2; break; }
                    if (inModifierChain) { nominalChunks++; inModifierChain = false; modifierArticledNonDet = false; }
                    if (!coordNomPronPending) nominalChunks++;
                    coordNomPronPending = false;
                    lastWasNomPronoun = true;
                    lastWasNounHead = false;
                    prevWasAdv = false;
                    if (nominalChunks > 1) break;
                    continue;
                }

                if (ck is "и" or "или" or "нито")
                {
                    if (lastWasNomPronoun) { coordNomPronPending = true; lastWasNomPronoun = false; continue; }
                    if (prevWasAdv) { coordAdvPending = true; prevWasAdv = false; continue; }
                    nominalChunks = 2;
                    break;
                }

                if (IsPreDaAdverbOrNegation(slots, peripheral, spaceNext, k))
                {
                    if (coordNomPronPending) { nominalChunks = 2; break; }
                    coordAdvPending = false;
                    prevWasAdv = true;
                    lastWasNounHead = false;
                    lastWasNomPronoun = false;
                    continue;
                }

                if (coordNomPronPending || coordAdvPending)
                {
                    nominalChunks = 2;
                    break;
                }

                if (lastWasNounHead && BulgarianFunctionWords.HasAny(ck, FunctionWordClasses.PossessiveOrDativeClitic))
                {
                    lastWasNounHead = false;
                    continue;
                }

                bool isArticled = !sk.Word.UsesGraph && sk.Active.Length > 0 &&
                    sk.Active.All(r => sk.Word.Readings[r].Features.Article is
                        MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short);
                bool isNumeral = !sk.Word.UsesGraph && sk.Active.Length > 0 &&
                    sk.Active.All(r => sk.Word.Readings[r].Category == Category.Number ||
                        sk.Word.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral);
                bool isColloquialDem = ck is "тоя" or "тая" or "туй" or "тия" or "оня" or "оная" or "онуй" or "ония" or "тез" or "онез";
                bool hasNounHead = HasNominalHeadReading(sk);
                bool canModify = (sk.ComparativeGraphModifier || IsNominalModifierOrDeterminer(sk) || isNumeral || isColloquialDem) &&
                    (!hasNounHead || k + 1 < slots.Length && spaceNext[k] && HasNominalHeadReading(slots[k + 1]) &&
                     !slots[k + 1].Active.Any(r => slots[k + 1].Word.Readings[r].Features.Article is
                         MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short));

                if (inModifierChain && modifierArticledNonDet && isArticled)
                {
                    nominalChunks++;
                    inModifierChain = false;
                    modifierArticledNonDet = false;
                }

                if (canModify)
                {
                    inModifierChain = true;
                    if (isArticled && !Guard.DeterminerAllowsArticledHead(sk.Word))
                        modifierArticledNonDet = true;
                    lastWasNounHead = false;
                    lastWasNomPronoun = false;
                    prevWasAdv = false;
                    if (nominalChunks >= 1) { nominalChunks = 2; break; }
                    continue;
                }

                if (hasNounHead)
                {
                    nominalChunks++;
                    inModifierChain = false;
                    modifierArticledNonDet = false;
                    lastWasNounHead = true;
                    lastWasNomPronoun = false;
                    prevWasAdv = false;
                    if (nominalChunks > 1) break;
                    continue;
                }

                nominalChunks = 2;
                break;
            }

            if (inModifierChain) nominalChunks++;
            if (subIdx < 0 || nominalChunks > 1) continue;

            if (nominalChunks > 0)
            {
                // "за [NP] да": only a nominative personal pronoun ("за тя спокойно да може да работи") proves a raised
                // subject; "за + NounNP + да" ("За приятелите да работя", "книга за учителя да чете") is a governed PP.
                if (prep == "за" && !startsWithNomPronoun)
                    continue;
                // "без/преди/вместо/освен [NounNP] да": mid-clause without a nominative pronoun ("Тръгнах без чантата да пазарувам")
                // is an ordinary matrix PP + purpose "да"-clause, not a prepositional subordinator.
                if (prep is "без" or "преди" or "вместо" or "освен" && !startsWithNomPronoun && !atClauseStart)
                    continue;
            }

            var boundary = new ClauseBoundary(
                ClauseBoundaryKind.PrepositionalSubordinator,
                PrepositionWordIndex: i,
                IntroducerFirstWordIndex: i,
                IntroducerLastWordIndex: subIdx,
                CommaTokenIndex: commaTokenBefore[i],
                HasConcessiveIntroducer: HasConcessiveIntroducerBeforeDa(slots, subIdx));

            prepByPrep[i] = boundary;
            if (prepByIntroducer[subIdx] < 0)
                prepByIntroducer[subIdx] = i;
        }
    }

    private static bool HasUpcomingDaPredicate(
        WordSlot[] slots, bool[] peripheral, bool[] spaceNext, int start)
    {
        for (int j = start; j < slots.Length && j <= start + 4; j++)
        {
            if (peripheral[j] || slots[j].Quoted || !spaceNext[j - 1]) return false;
            if (HasFiniteReading(slots[j]) || slots[j].Word.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) ||
                slots[j].Word.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb))
                return true;
            if (!BulgarianFunctionWords.HasAny(slots[j].Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge))
                return false;
        }
        return false;
    }

    private static bool IsExactAdverbOrNegation(WordSlot slot) =>
        slot.Word.CanonicalText is "не" or "дори" or "даже" or "изобщо" or "още" or "вече" or "само" or "едва" ||
        !slot.Word.UsesGraph && slot.Active.Length > 0 &&
        slot.Active.All(r => IsCleanSourceAdverb(slot.Word.Readings[r]));

    private static bool IsCleanSourceAdverb(ReadingView reading) =>
        reading.SourceAdverb &&
        reading.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adverb, HasUnrecognizedFeatures: false };

    private static bool IsBareNeuterSingularAdjective(ReadingView reading) =>
        reading.Features is
        {
            PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
            Gender: MorphologicalGender.Neuter,
            Number: MorphologicalNumber.Singular,
            Article: MorphologicalArticle.Indefinite,
            HasConflictingFeatures: false,
            HasUnrecognizedFeatures: false
        };

    private static bool IsPreDaAdverbOrNegation(WordSlot[] slots, bool[]? peripheral, bool[] spaceNext, int k)
    {
        WordSlot slot = slots[k];
        if (IsExactAdverbOrNegation(slot))
            return true;
        if (slot.Word.UsesGraph || slot.Active.Length == 0 ||
            !slot.Active.Any(r => IsCleanSourceAdverb(slot.Word.Readings[r])) ||
            !slot.Active.All(r => IsCleanSourceAdverb(slot.Word.Readings[r]) || IsBareNeuterSingularAdjective(slot.Word.Readings[r])))
            return false;

        for (int m = k + 1; m < slots.Length && (peripheral is null || !peripheral[m]) && !slots[m].Quoted && spaceNext[m - 1]; m++)
        {
            string cm = slots[m].Word.CanonicalText;
            if (cm == "да")
                return true;
            if (slots[m].Active.Any(r => slots[m].Word.Readings[r].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                    Gender: MorphologicalGender.Neuter,
                    Number: MorphologicalNumber.Singular,
                    Article: MorphologicalArticle.Indefinite
                }))
                return false;
            if (cm is "и" or "или" or "нито" || slots[m].ComparativeGraphModifier || IsExactAdverbOrNegation(slots[m]) ||
                slots[m].Active.Any(r => IsBareNeuterSingularAdjective(slots[m].Word.Readings[r])))
                continue;
            break;
        }
        return true;
    }

    private static bool IsRequestVerb(TokenView word, IReadOnlyList<int> active) =>
        !word.UsesGraph && word.Complete && word.CanonicalText == "моля" && active.Any(r => word.Readings[r].Features is
            { VerbForm: MorphologicalVerbForm.Finite, Person: MorphologicalPerson.First, Number: MorphologicalNumber.Singular });

    private static bool IsRequestReflexive(TokenView word, IReadOnlyList<int> active) =>
        !word.UsesGraph && word.Complete && word.CanonicalText == "се" &&
        active.Any(r => word.Readings[r].Category == Category.Particle);

    private static bool IsRequestAdverb(TokenView word, IReadOnlyList<int> active) =>
        !word.UsesGraph && word.Complete && word.CanonicalText != "не" &&
        active.Any(r => IsCleanSourceAdverb(word.Readings[r])) &&
        (BulgarianFunctionWords.IsNominalFocusParticle(word.CanonicalText) || !active.Any(r => word.Readings[r].Category == Category.Noun)) &&
        !active.Any(r => word.Readings[r].Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative);

    private static bool IsRequestClitic(ReadingView reading, MorphologicalCase grammaticalCase) =>
        reading.Features.Case == grammaticalCase && Guard.IsShortPersonalCliticReading(reading);

    private static bool IsAntecedentOnlyRelativeTail(
        TextAnalysis analysis, WordSlot[] slots, int[] indices, int relativePos, bool hasPrefixPredicate,
        bool[] spaceNext, CancellationToken ct)
    {
        int relativeWord = relativePos;
        if (IsActivePreposition(slots[indices[relativePos]].Word, slots[indices[relativePos]].Active) &&
            relativePos + 1 < indices.Length)
            relativeWord++;
        if (!IsRelativeWord(slots[indices[relativeWord]].Word.CanonicalText) ||
            !HasOnlyBoundaryPunctuation(analysis, slots[indices[relativePos - 1]].Word, slots[indices[relativePos]].Word, ','))
            return false;
        for (int p = 0; p < indices.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            if (slots[indices[p]].Quoted || !IsExactWord(slots[indices[p]]) ||
                p > 0 && (indices[p] != indices[p - 1] + 1 || p != relativePos && !spaceNext[indices[p - 1]]))
                return false;
        }

        WordSlot first = slots[indices[0]];
        int[] subject = relativePos == 1
            ? first.Active.Where(r => Guard.LicensedSubject(first.Word.CanonicalText, first.Word.Readings[r])).ToArray() : [];
        if (!hasPrefixPredicate)
        {
            if (subject.Length > 0 || relativePos == 1 && HasNominalHeadReading(first)) return true;
            TokenView[] prefix = indices[..relativePos].Select(i => slots[i].Word).ToArray();
            ContextLanes.NominalPhrase? nominal = ContextLanes.NominalMembers(prefix, prefix.Length - 1, ct,
                allowAdnominalReadings: true, sentenceInitialFirst: true);
            return nominal is not null && nominal.Heads.Any(h => h.Attributes.All(rows => rows.Length > 0));
        }

        // The sole finite homograph of a licensed antecedent (Вие/вия) is not an independent matrix witness.
        if (subject.Length == 0 || !first.Active.All(r => Guard.LicensedSubject(first.Word.CanonicalText, first.Word.Readings[r]) ||
                first.Word.Readings[r].Features.VerbForm == MorphologicalVerbForm.Finite &&
                Guard.IsPersonalPredicate(first.Word.Readings[r].Features) && IsCleanOrBenignLegacyVerb(first.Word.Readings[r])) ||
            !BulgarianFunctionWords.TryGetRelativeSubjectAxes(slots[indices[relativeWord]].Word.CanonicalText, out var number, out _))
            return false;
        return subject.Any(r => first.Word.Readings[r].Features.Number == number &&
            indices[(relativeWord + 1)..].Any(i => FiniteReadings(slots[i]).Any(v => Guard.PersonNumberTupleMatches(
                (first.Word.Readings[r].Features.Person, first.Word.Readings[r].Features.Number),
                (slots[i].Word.Readings[v].Features.Person, slots[i].Word.Readings[v].Features.Number)))));
    }

    private static List<ClauseSpan> SegmentClauses(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        WordSlot[] slots,
        bool[] peripheral,
        bool[] spaceNext,
        int[] commaTokenBefore,
        ClauseBoundary[] prepByPrep,
        CancellationToken ct)
    {
        bool[] extracted = (bool[])peripheral.Clone();
        List<(int[] Indices, ClauseBoundary Boundary)> parentheticals = [];

        // 1. Extract comma-bounded parenthetical relative clauses (", която ... ," / ", в която ... ,").
        for (int i = 1; i < slots.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (extracted[i] || slots[i].Quoted) continue;
            int relStart = i;
            int prepIdx = -1;
            int relWordIdx = i;
            if (IsActivePreposition(slots[i].Word, slots[i].Active) && i + 1 < slots.Length &&
                !slots[i + 1].Quoted &&
                IsRelativeWord(slots[i + 1].Word.CanonicalText))
            {
                relStart = i;
                prepIdx = i;
                relWordIdx = i + 1;
            }
            else if (!IsRelativeWord(slots[i].Word.CanonicalText))
                continue;
            else if (i > 1 && IsActivePreposition(slots[i - 1].Word, slots[i - 1].Active) &&
                     HasCommaBetweenRaw(analysis, slots[i - 2].Word, slots[i - 1].Word))
                continue;

            if (!HasCommaBetweenRaw(analysis, slots[relStart - 1].Word, slots[relStart].Word)) continue;
            int closeAfter = -1;
            bool sawFinite = false;
            bool sawNonFiniteVerb = false;
            bool sawInternalComma = false;
            for (int j = relStart + 1; j < slots.Length - 1; j++)
            {
                if (HasCommaBetweenRaw(analysis, slots[j - 1].Word, slots[j].Word))
                    sawInternalComma = true;
                if (slots[j].Quoted) continue;
                if (HasFiniteReading(slots[j])) sawFinite = true;
                else if (!slots[j].Word.UsesGraph &&
                         slots[j].Word.Readings.Any(r => r.Category == Category.Verb && !Guard.IsSourcePlaceholder(slots[j].Word, r)))
                    sawNonFiniteVerb = true;
                bool closesFiniteFreeBeforePredicate = !sawFinite && !sawInternalComma && sawNonFiniteVerb &&
                    (HasFiniteReading(slots[j + 1]) ||
                     BulgarianFunctionWords.HasAny(slots[j + 1].Word.CanonicalText, FunctionWordClasses.VerbalPrefixOrReflexive));
                if ((sawFinite || closesFiniteFreeBeforePredicate) &&
                    !slots[j + 1].Quoted && HasCommaBetweenRaw(analysis, slots[j].Word, slots[j + 1].Word))
                {
                    bool trailingFinite = false;
                    for (int k = j + 1; k < slots.Length; k++)
                        if (!extracted[k] && !slots[k].Quoted && HasFiniteReading(slots[k])) { trailingFinite = true; break; }
                    if (!trailingFinite && HasNominalHeadReading(slots[j]) &&
                        (IsNominalModifierOrDeterminer(slots[j + 1]) || HasNominalHeadReading(slots[j + 1])))
                        continue;
                    closeAfter = j;
                    break;
                }
            }
            if (closeAfter < 0) continue;

            bool outerPredicate = false;
            for (int k = 0; k < slots.Length; k++)
                if ((k < relStart || k > closeAfter) && !extracted[k] && !slots[k].Quoted &&
                    (HasFiniteReading(slots[k]) || HasPureImperativeReading(slots[k])))
                { outerPredicate = true; break; }
            if (!outerPredicate) continue;

            int[] relIndices = Enumerable.Range(relStart, closeAfter - relStart + 1).ToArray();
            for (int k = relStart; k <= closeAfter; k++) extracted[k] = true;
            var relBoundary = new ClauseBoundary(
                ClauseBoundaryKind.ParentheticalRelative,
                PrepositionWordIndex: prepIdx,
                IntroducerFirstWordIndex: relStart,
                IntroducerLastWordIndex: relWordIdx,
                CommaTokenIndex: commaTokenBefore[relStart],
                HasConcessiveIntroducer: IsConcessiveOrOptativeCue(slots[relWordIdx].Word.CanonicalText));
            parentheticals.Add((relIndices, relBoundary));
            i = closeAfter;
        }

        // 1b. Mask comma-bounded finite-free participial appositives (", написана от ...,") immediately before matrix predicates.
        for (int i = 1; i < slots.Length - 1; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (extracted[i] || slots[i].Quoted || extracted[i - 1] || slots[i - 1].Quoted) continue;
            if (!HasNominalHeadReading(slots[i - 1]) ||
                !HasCommaBetweenRaw(analysis, slots[i - 1].Word, slots[i].Word) ||
                !IsParticipialAppositiveOpener(slots[i - 1], slots[i]) ||
                i + 1 < slots.Length && spaceNext[i] &&
                HasAgreeingIndefiniteNounAfterModifier(slots[i].Word, slots[i].Active.ToList(), slots[i + 1].Word, slots[i + 1].Active.ToList(), allowArticledModifier: false))
                continue;
            int closeAfter = -1;
            for (int j = i; j < slots.Length - 1; j++)
            {
                if (extracted[j] || slots[j].Quoted || HasFiniteReading(slots[j])) break;
                if (!slots[j + 1].Quoted && HasCommaBetweenRaw(analysis, slots[j].Word, slots[j + 1].Word))
                {
                    if (HasFiniteReading(slots[j + 1]) ||
                        BulgarianFunctionWords.HasAny(slots[j + 1].Word.CanonicalText, FunctionWordClasses.VerbalPrefixOrReflexive))
                        closeAfter = j;
                    break;
                }
            }
            if (closeAfter < 0) continue;
            for (int k = i; k <= closeAfter; k++) extracted[k] = true;
            i = closeAfter;
        }

        // Set aside the request formula only when the adjacent clause licenses a command.
        // Bind its original words to a separate clause without changing their lexical inventories.
        for (int i = 0; i < slots.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot request = slots[i];
            if (extracted[i] || request.Quoted || !IsRequestVerb(request.Word, request.Active))
                continue;
            int first = i, last = i;
            int preClitic = i - 1;
            if (preClitic > 0 && CanJoin(preClitic, i) && IsRequestReflexive(words[preClitic], slots[preClitic].Active))
                preClitic--;
            if (preClitic > 0 && slots[preClitic].RequestVerbWordIndex == i &&
                CanJoin(preClitic - 1, preClitic) && CanJoin(preClitic, preClitic + 1) &&
                IsRequestAdverb(words[preClitic - 1], slots[preClitic - 1].Active))
                first = preClitic - 1;
            else if (CanJoin(i, i + 1) && slots[i + 1].RequestVerbWordIndex == i)
                last = CanJoin(i + 1, i + 2) && IsRequestReflexive(words[i + 2], slots[i + 2].Active) ? i + 2 : i + 1;
            if (first > 0 && !HasOnlyBoundaryPunctuation(analysis, words[first - 1], words[first], ',') ||
                last + 1 < slots.Length && !HasOnlyBoundaryPunctuation(analysis, words[last], words[last + 1], ','))
                continue;
            int previousStart = first - 1;
            while (previousStart > 0 && spaceNext[previousStart - 1]) previousStart--;
            if (!HasCommandOnset(last + 1, slots.Length) && !HasCommandOnset(previousStart, first)) continue;
            int[] formula = Enumerable.Range(first, last - first + 1).ToArray();
            foreach (int index in formula) extracted[index] = true;
            parentheticals.Add((formula, new(ClauseBoundaryKind.ParentheticalCourtesy, -1, -1, -1,
                commaTokenBefore[first > 0 ? first : last + 1], false)));
        }

        bool CanJoin(int left, int right) => right < slots.Length && spaceNext[left] &&
            !extracted[left] && !extracted[right] && !slots[left].Quoted && !slots[right].Quoted;

        bool HasCommandOnset(int first, int end)
        {
            for (int p = first; p >= 0 && p < end; p++)
            {
                ct.ThrowIfCancellationRequested();
                WordSlot word = slots[p];
                if (extracted[p] || word.Quoted || !IsExactWord(word) || p > first && !spaceNext[p - 1]) break;
                if (word.Active.Any(r => word.Word.Readings[r].Features is
                    { VerbForm: MorphologicalVerbForm.Imperative, Person: MorphologicalPerson.Second } &&
                    IsCleanOrBenignLegacyVerb(word.Word.Readings[r])))
                    return word.Active.All(r => word.Word.Readings[r].Category == Category.Verb) &&
                        !(p + 1 < end && spaceNext[p] && slots[p + 1].Word.CanonicalText == "ли");
                if (!(p == first && word.Word.CanonicalText is "ти" or "вие") &&
                    !BulgarianFunctionWords.HasAny(word.Word.CanonicalText, FunctionWordClasses.PreVerbalCliticBridge)) break;
            }
            return false;
        }

        int[] remaining = Enumerable.Range(0, slots.Length).Where(idx => !extracted[idx]).ToArray();
        if (remaining.Length == 0)
        {
            List<ClauseSpan> onlySub = [];
            for (int p = 0; p < parentheticals.Count; p++)
            {
                var (relIdx, relBound) = parentheticals[p];
                onlySub.Add(BuildClauseSpan(p, relBound, relIdx, matrix: false, slots, spaceNext));
            }
            return onlySub;
        }

        // 2. Split remaining sequence at explicit connectors and independent punctuation boundaries.
        List<(int StartPos, int EndPos, bool SubordinatorStart, ClauseBoundary Boundary)> rawSegments = [];
        int segStart = 0;
        // A sentence coordinator can precede a preposed subordinate clause ("Но щом ..., той ...").
        // Keep it inside that clause while recognizing the comma that returns to the matrix clause.
        int initialPos = remaining.Length > 2 && IsCoordinatingConjunction(slots[remaining[0]]) &&
            !slots[remaining[1]].Quoted && remaining[1] == remaining[0] + 1 && spaceNext[remaining[0]] ? 1 : 0;
        bool initiallySubordinate = IsPreposedSubordinator(slots, remaining[initialPos],
            remaining.Length > initialPos + 1 ? remaining[initialPos + 1] : -1) ||
            prepByPrep[remaining[initialPos]].Kind == ClauseBoundaryKind.PrepositionalSubordinator;
        ClauseBoundary currentBoundary = InitialSegmentBoundary(slots, remaining, prepByPrep,
            initiallySubordinate ? initialPos : 0);
        bool antecedentOnlyRelative = false;

        for (int p = 1; p < remaining.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            int prevIdx = remaining[p - 1], curIdx = remaining[p];
            WordSlot cur = slots[curIdx];
            if (cur.Quoted) continue;
            bool commaBefore = HasCommaBetweenRaw(analysis, slots[prevIdx].Word, cur.Word);
            bool leftHasVerb = RangeHasClausePredicate(slots, remaining, segStart, p - 1, includeImperatives: true);
            bool rightHasVerb = RangeHasClausePredicate(slots, remaining, p, remaining.Length - 1, includeImperatives: true);
            bool relativeFragment = segStart == 0 && commaBefore && rightHasVerb &&
                IsAntecedentOnlyRelativeTail(analysis, slots, remaining, p, leftHasVerb, spaceNext, ct);
            if ((!leftHasVerb && !relativeFragment) || !rightHasVerb) continue;

            bool prepSubBreak = prepByPrep[curIdx].Kind == ClauseBoundaryKind.PrepositionalSubordinator &&
                (commaBefore || cur.Word.CanonicalText is "без" or "за" or "преди" or "вместо" or "освен" &&
                 prepByPrep[curIdx].IntroducerLastWordIndex == curIdx + 1);
            bool multiWordSub = cur.Word.CanonicalText is "след" or "тъй" or "макар" or "въпреки" &&
                p + 1 < remaining.Length && slots[remaining[p + 1]].Word.CanonicalText is "като" or "че";
            bool subBreak = IsClauseIntroducer(cur.Word, cur.Active) || multiWordSub;
            bool coordBreak = IsCoordinatingConjunction(cur) &&
                (commaBefore || SegmentedVerbAllowsCoordSplit(analysis, slots, remaining, p, prepByPrep) &&
                    !ContinuesCoordinatedNominal(analysis, sentence, words, slots, remaining, p, spaceNext, ct));
            bool preposedSubComma = segStart == 0 && commaBefore && initiallySubordinate;
            bool independentPunctuation = curIdx == prevIdx + 1 &&
                (HasOnlyBoundaryPunctuation(analysis, slots[prevIdx].Word, cur.Word, ';') ||
                 commaBefore && HasOnlyBoundaryPunctuation(analysis, slots[prevIdx].Word, cur.Word, ',') &&
                 HasNominativeOnlyPostposedPronoun(analysis, sentence, slots, remaining, p, spaceNext));

            if (prepSubBreak || subBreak || coordBreak || preposedSubComma || independentPunctuation || relativeFragment)
            {
                antecedentOnlyRelative |= relativeFragment;
                bool startedSub = antecedentOnlyRelative || segStart == 0 && initiallySubordinate;
                rawSegments.Add((segStart, p - 1, startedSub, currentBoundary));
                segStart = p;

                int commaTok = commaBefore ? commaTokenBefore[curIdx] : -1;
                if (prepSubBreak)
                {
                    currentBoundary = prepByPrep[curIdx] with { CommaTokenIndex = commaTok };
                }
                else if (subBreak || relativeFragment)
                {
                    int introEnd = multiWordSub || relativeFragment && !IsRelativeWord(cur.Word.CanonicalText)
                        ? remaining[p + 1] : curIdx;
                    currentBoundary = new ClauseBoundary(
                        ClauseBoundaryKind.Subordinator,
                        PrepositionWordIndex: -1,
                        IntroducerFirstWordIndex: curIdx,
                        IntroducerLastWordIndex: introEnd,
                        CommaTokenIndex: commaTok,
                        HasConcessiveIntroducer: IsConcessiveOrOptativeCue(cur.Word.CanonicalText));
                }
                else if (coordBreak)
                {
                    currentBoundary = new ClauseBoundary(
                        ClauseBoundaryKind.Coordinator,
                        PrepositionWordIndex: -1,
                        IntroducerFirstWordIndex: curIdx,
                        IntroducerLastWordIndex: curIdx,
                        CommaTokenIndex: commaTok,
                        HasConcessiveIntroducer: false);
                }
                else if (independentPunctuation)
                {
                    currentBoundary = new ClauseBoundary(
                        ClauseBoundaryKind.IndependentPunctuation, -1, -1, -1, commaTok, false);
                }
                else
                {
                    currentBoundary = new ClauseBoundary(
                        ClauseBoundaryKind.PreposedSubordinatorComma,
                        PrepositionWordIndex: -1,
                        IntroducerFirstWordIndex: -1,
                        IntroducerLastWordIndex: -1,
                        CommaTokenIndex: commaTok,
                        HasConcessiveIntroducer: IsConcessiveOrOptativeCue(cur.Word.CanonicalText));
                }
            }
        }

        bool lastStartedSub = antecedentOnlyRelative || segStart == 0 && initiallySubordinate;
        rawSegments.Add((segStart, remaining.Length - 1, lastStartedSub, currentBoundary));

        int matrixSeg = rawSegments.FindIndex(s => !s.SubordinatorStart);
        if (matrixSeg < 0 && !antecedentOnlyRelative) matrixSeg = 0;

        List<ClauseSpan> ordered = [];
        int nextClauseId = 0;
        for (int s = 0; s < rawSegments.Count; s++)
        {
            var seg = rawSegments[s];
            int[] slice = remaining[seg.StartPos..(seg.EndPos + 1)];
            ordered.Add(BuildClauseSpan(nextClauseId++, seg.Boundary, slice, matrix: s == matrixSeg, slots, spaceNext));
        }
        foreach (var (relIdx, relBound) in parentheticals)
        {
            ordered.Add(BuildClauseSpan(nextClauseId++, relBound, relIdx, matrix: false, slots, spaceNext));
        }
        return ordered;
    }

    // Keep the sentence map and its existing fallback roles intact when a speaker cannot be proved.
    internal ClauseSpan? FindDelimitedReportingTail(ClauseSpan clause, CancellationToken ct)
    {
        int[] idx = clause.WordIndices;
        if (!clause.Matrix || clause.BodyStartPos != 0 || idx.Length < 3) return null;
        ReadOnlySpan<char> leading = Analysis.Text.AsSpan(Sentence.Start,
            Slots[idx[0]].Word.Token.Start - Sentence.Start).Trim();
        bool dialogue = leading.Length == 1 && leading[0] is '-' or '\u2013' or '\u2014';
        if (!Slots[idx[0]].Quoted && !dialogue) return null;
        for (int p = 1; p + 1 < idx.Length; p++)
        {
            ct.ThrowIfCancellationRequested();
            WordSlot previous = Slots[idx[p - 1]], current = Slots[idx[p]];
            if (current.Quoted || idx[p] != idx[p - 1] + 1 ||
                !(previous.Quoted || dialogue &&
                    (HasOnlyBoundaryPunctuation(Analysis, previous.Word, current.Word, '-') ||
                     HasOnlyBoundaryPunctuation(Analysis, previous.Word, current.Word, '\u2013') ||
                     HasOnlyBoundaryPunctuation(Analysis, previous.Word, current.Word, '\u2014'))))
                continue;
            if (HasDelimitedFiniteSpeech(Analysis, Sentence, Slots, idx, 0, p, ProtectedSpans, ct) &&
                IsReportingPredicate(current, ct))
                return new ClauseSpan(clause.ClauseId,
                    new(ClauseBoundaryKind.ReportingSpeech, -1, -1, -1, -1, false), idx[p..], 0, false, false);
        }
        return null;
    }

    // A positive speech construction license; general transitivity does not identify a speaker.
    internal static bool IsReportingPredicate(WordSlot slot, CancellationToken ct)
    {
        if (slot.Quoted || !IsExactWord(slot)) return false;
        int[] finite = AllFinitePersonalReadings(slot.Word, ct);
        return finite.Length > 0 && finite.All(r =>
            BulgarianTextNormalizer.NormalizeKey(slot.Word.Readings[r].Lemma) is
                "кажа" or "казвам" or "съобщя" or "съобщавам" or
                "заявя" or "заявявам" or "обявя" or "обявявам");
    }

    private static bool HasDelimitedFiniteSpeech(
        TextAnalysis analysis, SentenceSpan sentence, WordSlot[] slots, int[] indices,
        int fromPos, int predicatePos, IReadOnlyList<(int Start, int End)> protectedSpans, CancellationToken ct)
    {
        if (fromPos != 0 || predicatePos <= fromPos) return false;
        TokenView first = slots[indices[fromPos]].Word;
        TokenView last = slots[indices[predicatePos - 1]].Word;
        TokenView predicate = slots[indices[predicatePos]].Word;
        bool quoted = false;
        foreach (var span in protectedSpans)
        {
            ct.ThrowIfCancellationRequested();
            if (span.Start < sentence.Start || span.Start > first.Token.Start ||
                span.End < last.Token.Start + last.Token.Length || span.End > predicate.Token.Start ||
                !analysis.Text.AsSpan(sentence.Start, span.Start - sentence.Start).Trim().IsEmpty)
                continue;
            char open = analysis.Text[span.Start], close = analysis.Text[span.End - 1];
            bool paired = (open, close) is ('"', '"') or ('„', '“') or ('“', '”') or ('«', '»') or ('‘', '’');
            ReadOnlySpan<char> delimiter = analysis.Text.AsSpan(span.End, predicate.Token.Start - span.End).Trim();
            if (paired && delimiter.Length == 1 && delimiter[0] is ',' or '-' or '\u2013' or '\u2014' &&
                Enumerable.Range(fromPos, predicatePos - fromPos).All(p => slots[indices[p]].Quoted))
                quoted = true;
        }
        if (!quoted)
        {
            ReadOnlySpan<char> leading = analysis.Text.AsSpan(sentence.Start, first.Token.Start - sentence.Start).Trim();
            int end = last.Token.Start + last.Token.Length;
            ReadOnlySpan<char> delimiter = analysis.Text.AsSpan(end, predicate.Token.Start - end);
            ReadOnlySpan<char> trimmed = delimiter.Trim();
            if (leading.Length != 1 || leading[0] is not ('-' or '\u2013' or '\u2014') ||
                first.Token.Start == 0 || !char.IsWhiteSpace(analysis.Text[first.Token.Start - 1]) ||
                trimmed.Length != 1 || trimmed[0] is not ('-' or '\u2013' or '\u2014') ||
                delimiter.IsEmpty || !char.IsWhiteSpace(delimiter[0]) || !char.IsWhiteSpace(delimiter[^1]) ||
                Enumerable.Range(fromPos, predicatePos - fromPos).Any(p => slots[indices[p]].Quoted))
                return false;
        }
        // A finite homograph inside a quoted title ("Неговите работи") is not a finite-content witness.
        return Enumerable.Range(fromPos, predicatePos - fromPos).Any(p =>
        {
            ct.ThrowIfCancellationRequested();
            TokenView word = slots[indices[p]].Word;
            int[] real = NonPlaceholderReadings(word, p == fromPos);
            return !word.UsesGraph && word.Complete && real.Length > 0 && real.All(r =>
                word.Readings[r].Features.VerbForm == MorphologicalVerbForm.Finite &&
                Guard.IsPersonalPredicate(word.Readings[r].Features) && IsCleanOrBenignLegacyVerb(word.Readings[r]));
        });
    }

    private static bool HasOnlyBoundaryPunctuation(TextAnalysis analysis, TokenView left, TokenView right, char punctuation)
    {
        bool found = false;
        int from = left.Token.Start + left.Token.Length;
        foreach (char ch in analysis.Text.AsSpan(from, right.Token.Start - from))
        {
            if (ch == punctuation && !found) found = true;
            else if (!char.IsWhiteSpace(ch)) return false;
        }
        return found;
    }

    private static bool HasNominativeOnlyPostposedPronoun(
        TextAnalysis analysis, SentenceSpan sentence, WordSlot[] slots, int[] indices, int predicatePos, bool[] spaceNext)
    {
        WordSlot predicate = slots[indices[predicatePos]];
        if (!IsExactWord(predicate) || FiniteReadings(predicate).Length == 0)
            return false;
        bool hasNonverbalAlternative = predicate.Active.Any(r => predicate.Word.Readings[r].Features.VerbForm is not
            (MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative));
        if (hasNonverbalAlternative &&
            (predicatePos + 2 != indices.Length ||
             predicate.Word.Readings.Any(r => r.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) == true) ||
             predicate.Active.Any(r =>
             {
                 ReadingView row = predicate.Word.Readings[r];
                 MorphologicalFeatures f = row.Features;
                 return f.VerbForm is not (MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative) &&
                     !(f is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false,
                              HasUnrecognizedFeatures: false } &&
                       (f.Number == MorphologicalNumber.Count || Guard.IsShortMasculineDefiniteReading(predicate.Word.CanonicalText, row)));
             })))
            return false;
        for (int p = predicatePos + 1; p < indices.Length; p++)
        {
            int previous = indices[p - 1], current = indices[p];
            WordSlot slot = slots[current];
            if (current != previous + 1 || !spaceNext[previous] || slot.Quoted || !IsExactWord(slot)) return false;
            if (VerbalComplexBinder.IsExactPureAdverb(slot.Word)) continue;
            // Complete inventories veto ти/те/то when dative, accusative or discourse readings remain.
            if (p != indices.Length - 1 || !slot.Word.Readings.All(r => Guard.LicensedSubject(slot.Word.CanonicalText, r)))
                return false;
            int from = slot.Word.Token.Start + slot.Word.Token.Length;
            ReadOnlySpan<char> tail = analysis.Text.AsSpan(from, sentence.Start + sentence.Length - from).Trim();
            if (tail.IsEmpty) return false;
            foreach (char ch in tail)
                if (ch is not ('.' or '!' or '?' or '\u2026')) return false;
            return true;
        }
        return false;
    }

    private static ClauseBoundary InitialSegmentBoundary(
        WordSlot[] slots, int[] remaining, ClauseBoundary[] prepByPrep, int firstPos)
    {
        int first = remaining[firstPos];
        if (prepByPrep[first].Kind == ClauseBoundaryKind.PrepositionalSubordinator)
            return prepByPrep[first];

        int second = remaining.Length > firstPos + 1 ? remaining[firstPos + 1] : -1;
        if (IsPreposedSubordinator(slots, first, second))
        {
            bool twoWord = slots[first].Word.CanonicalText is "след" or "преди" or "тъй" or "макар" or "въпреки" &&
                second >= 0 && slots[second].Word.CanonicalText is "като" or "да" or "че";
            return new ClauseBoundary(
                ClauseBoundaryKind.Subordinator,
                PrepositionWordIndex: -1,
                IntroducerFirstWordIndex: first,
                IntroducerLastWordIndex: twoWord ? second : first,
                CommaTokenIndex: -1,
                HasConcessiveIntroducer: IsConcessiveOrOptativeCue(slots[first].Word.CanonicalText));
        }

        return ClauseBoundary.Initial(IsConcessiveOrOptativeCue(slots[first].Word.CanonicalText));
    }

    private static ClauseSpan BuildClauseSpan(
        int clauseId,
        ClauseBoundary boundary,
        int[] idx,
        bool matrix,
        WordSlot[] slots,
        bool[] spaceNext)
    {
        int bodyStart = 0;
        if (idx.Length > 0)
        {
            if (boundary.Kind == ClauseBoundaryKind.PrepositionalSubordinator &&
                idx[0] == boundary.PrepositionWordIndex)
            {
                // Advance past leading adverbs/negation so "без дори да..." exposes bodyStart = 1 while
                // "Без дори шапката да изляза, не смея." keeps "Без" at bodyStart = 0 so "шапката" stays governed.
                int k = 1;
                while (k < idx.Length && idx[k] != boundary.IntroducerLastWordIndex &&
                       IsPreDaAdverbOrNegation(slots, null, spaceNext, idx[k]))
                    k++;
                bool exposeRaisedBody = k < idx.Length &&
                    (idx[k] == boundary.IntroducerLastWordIndex ||
                     BulgarianFunctionWords.TryGetPersonalSubjectPronoun(slots[idx[k]].Word.CanonicalText, out _) ||
                     slots[idx[k]].Word.CanonicalText == "никой" ||
                     slots[idx[k]].Active.Length > 0 &&
                     slots[idx[k]].Active.All(r => slots[idx[k]].Word.Readings[r].Features.Article == MorphologicalArticle.Full));
                bodyStart = exposeRaisedBody ? 1 : 0;
            }
            else if (boundary.Kind == ClauseBoundaryKind.ParentheticalRelative &&
                     boundary.PrepositionWordIndex >= 0 && idx.Length >= 2 && idx[0] == boundary.PrepositionWordIndex)
            {
                // Keep ", в която ..." intact from pos 0 so MarkGovernedPhrases marks "в която" as PP, matching legacy behavior.
                bodyStart = 0;
            }
            else if (boundary.Kind == ClauseBoundaryKind.Subordinator &&
                     Array.IndexOf(idx, boundary.IntroducerLastWordIndex) is int introducerPos && introducerPos >= 0)
            {
                bodyStart = introducerPos + 1;
            }
            else if (IsCoordinatingConjunction(slots[idx[0]]) || IsClauseIntroducer(slots[idx[0]].Word, slots[idx[0]].Active))
            {
                bodyStart = idx.Length > 2 && slots[idx[0]].Word.CanonicalText is "макар" or "въпреки" &&
                    slots[idx[1]].Word.CanonicalText == "че" ? 2 : 1;
            }
            else if (idx.Length > 2 && slots[idx[0]].Word.CanonicalText is "след" or "тъй" or "макар" or "въпреки" &&
                     slots[idx[1]].Word.CanonicalText is "като" or "че")
            {
                bodyStart = 2;
            }
            else if (idx.Length > 2 && idx[0] == 0 && slots[idx[0]].Word.CanonicalText == "разбира" &&
                     slots[idx[1]].Word.CanonicalText == "се" &&
                     idx[1] == idx[0] + 1 && spaceNext[idx[0]] &&
                     RangeHasClausePredicate(slots, idx, 2, idx.Length - 1, includeImperatives: false))
            {
                bodyStart = 2;
            }
        }

        bool allowsPerfPres = ComputeClauseAllowsPerfectivePresent(slots, idx, boundary);
        return new ClauseSpan(clauseId, boundary, idx, bodyStart, matrix, allowsPerfPres);
    }

    private static bool ComputeClauseAllowsPerfectivePresent(
        WordSlot[] slots, int[] clauseIdx, ClauseBoundary boundary)
    {
        if (boundary.HasConcessiveIntroducer)
            return true;
        if (boundary.Kind == ClauseBoundaryKind.PrepositionalSubordinator &&
            boundary.IntroducerLastWordIndex >= 0 &&
            slots[boundary.IntroducerLastWordIndex].Word.CanonicalText == "да")
            return true;
        if (clauseIdx.Length > 0 &&
            BulgarianFunctionWords.HasAny(slots[clauseIdx[0]].Word.CanonicalText, FunctionWordClasses.PerfectivePresentSubordinator))
            return true;
        if (clauseIdx.Length > 1 &&
            slots[clauseIdx[0]].Word.CanonicalText is "след" or "преди" &&
            slots[clauseIdx[1]].Word.CanonicalText is "като" or "да")
            return true;
        return false;
    }

    private static List<BareParticipleClauseSegment> SegmentBareParticipleClauses(
        TextAnalysis analysis,
        IReadOnlyList<TokenView> words,
        WordSlot[] slots,
        bool[] peripheral,
        bool[] horizontalBridgeNext,
        CancellationToken ct)
    {
        const FunctionWordClasses ClauseBreakWords =
            FunctionWordClasses.ClauseCoordinatingConjunction |
            FunctionWordClasses.CoreSubordinatingConjunction |
            FunctionWordClasses.CommaSubordinateTrigger |
            FunctionWordClasses.ClauseIntroducer |
            FunctionWordClasses.RelativeWord;

        List<int> core = [];
        for (int i = 0; i < words.Count; i++)
            if (!peripheral[i] && !slots[i].Quoted) core.Add(i);
        if (core.Count < 2) return [];

        List<BareParticipleClauseSegment> segments = [];
        int segStart = 0;
        bool nextIntroByConj = false;
        for (int i = 0; i < core.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int wi = core[i];
            string c = words[wi].CanonicalText;
            bool isCoord = c is "и" or "или";
            bool isBreakWord = isCoord
                ? RangeHasVerbOrParticiple(words, core, segStart, i - 1)
                : BulgarianFunctionWords.HasAny(c, ClauseBreakWords) ||
                  c is "та" or "дето" or "както" or "сякаш" or "дали";
            if (isBreakWord)
            {
                if (i > segStart)
                    segments.Add(new(core.GetRange(segStart, i - segStart).ToArray(), nextIntroByConj));
                segStart = i + 1;
                nextIntroByConj = true;
                continue;
            }
            if (i > segStart)
            {
                int prevWi = core[i - 1];
                bool connected = wi == prevWi + 1
                    ? horizontalBridgeNext[prevWi]
                    : Guard.WordOrPictographBridge(analysis, words[prevWi], words[wi], ct, horizontalOnly: true);
                if (!connected)
                {
                    segments.Add(new(core.GetRange(segStart, i - segStart).ToArray(), nextIntroByConj));
                    segStart = i;
                    nextIntroByConj = false;
                }
            }
        }
        if (segStart < core.Count)
            segments.Add(new(core.GetRange(segStart, core.Count - segStart).ToArray(), nextIntroByConj));
        return segments;
    }

    private static bool RangeHasVerbOrParticiple(IReadOnlyList<TokenView> words, List<int> core, int from, int to)
    {
        for (int i = from; i <= to; i++)
        {
            TokenView w = words[core[i]];
            if (!w.UsesGraph && w.Readings.Any(r =>
                    TextPredicateLinkAnalyzer.CopularCarrier(r) ||
                    r.Features is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                        VerbForm: MorphologicalVerbForm.Participle,
                        ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                        HasConflictingFeatures: false
                    } ||
                    r.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative &&
                    Guard.IsPersonalPredicate(r.Features)))
                return true;
        }
        return false;
    }

    private static bool IsPreposedSubordinator(WordSlot[] slots, int firstIdx, int secondIdx)
    {
        string c = slots[firstIdx].Word.CanonicalText;
        if (BulgarianFunctionWords.HasAny(c, FunctionWordClasses.SingleWordPreposedSubordinator))
            return true;
        return c is "след" or "преди" or "тъй" && secondIdx >= 0 &&
               slots[secondIdx].Word.CanonicalText is "като" or "да";
    }

    private static bool ContinuesCoordinatedNominal(
        TextAnalysis analysis, SentenceSpan sentence, IReadOnlyList<TokenView> words, WordSlot[] slots,
        int[] remaining, int conjPos, bool[] spaceNext, CancellationToken ct)
    {
        if (conjPos < 2 || !IsActivePreposition(slots[remaining[conjPos - 2]].Word, slots[remaining[conjPos - 2]].Active))
            return false;
        WordSlot left = slots[remaining[conjPos - 1]];
        if (!HasNominalHeadReading(left) && Guard.ObliquePronounReadings(left.Word, ct).Length == 0)
            return false;
        int headPos = conjPos + 1;
        while (headPos < remaining.Length && !HasFiniteReading(slots[remaining[headPos]])) headPos++;
        if (headPos >= remaining.Length || RangeHasClausePredicate(slots, remaining, headPos + 1, remaining.Length - 1, includeImperatives: false))
            return false;
        for (int p = conjPos - 2; p <= headPos; p++)
        {
            if (slots[remaining[p]].Quoted || p > conjPos - 2 &&
                (remaining[p] != remaining[p - 1] + 1 || !spaceNext[remaining[p - 1]]))
                return false;
        }
        NominalStructuralProjection.Binding? binding = NominalStructuralProjection.BindHead(
            analysis.Text, sentence, analysis.Tokens, words, remaining[headPos], ct, allowPossessiveCliticBridge: true);
        if (binding is null || binding.Participants.Count < 2 || binding.Participants[0].WordIndex != remaining[conjPos + 1] ||
            NominalStructuralProjection.Measure(binding, ct) is not { } phrase)
            return false;
        var coherent = phrase.Heads.Where(h => h.Attributes.All(rows => rows.Length > 0)).ToArray();
        return coherent.Length > 0 && coherent.All(h => Guard.FiniteReadingsConflictWithNumber(binding.Head.Word, h.Number));
    }

    private static bool SegmentedVerbAllowsCoordSplit(
        TextAnalysis analysis, WordSlot[] slots, int[] remaining, int conjPos, ClauseBoundary[] prepByPrep)
    {
        if (conjPos + 1 >= remaining.Length) return false;
        WordSlot prev = slots[remaining[conjPos - 1]], next = slots[remaining[conjPos + 1]];
        if (HasPureNominal(prev.Word, prev.Active) && HasPureNominal(next.Word, next.Active) &&
            !HasFiniteReading(next) && conjPos + 2 < remaining.Length &&
            IsActivePreposition(slots[remaining[conjPos + 2]].Word, slots[remaining[conjPos + 2]].Active))
            return false;

        if ((HasNominalHeadReading(prev) || IsNominalModifierOrDeterminer(prev)) &&
            (HasNominalHeadReading(next) || IsNominalModifierOrDeterminer(next)) &&
            !HasFiniteReading(prev) && !HasFiniteReading(next))
        {
            int vPos = -1, laterBreakPos = -1;
            for (int q = conjPos + 2; q < remaining.Length; q++)
            {
                WordSlot s = slots[remaining[q]];
                if (HasFiniteReading(s))
                {
                    vPos = q;
                    break;
                }
                if (laterBreakPos < 0 && (IsCoordinatingConjunction(s) || IsClauseIntroducer(s.Word, s.Active) ||
                    prepByPrep[remaining[q]].Kind == ClauseBoundaryKind.PrepositionalSubordinator))
                    laterBreakPos = q;
            }
            if (laterBreakPos >= 0 && vPos > laterBreakPos &&
                (prepByPrep[remaining[laterBreakPos]].Kind != ClauseBoundaryKind.PrepositionalSubordinator ||
                 !HasFiniteAfterComma(analysis, slots, remaining, vPos)))
            {
                bool prevGoverned = conjPos >= 2 &&
                    IsActivePreposition(slots[remaining[conjPos - 2]].Word, slots[remaining[conjPos - 2]].Active);
                bool hasSecondSubjectNominal = IsCoordinatingConjunction(slots[remaining[laterBreakPos]]) &&
                    Enumerable.Range(laterBreakPos + 1, vPos - laterBreakPos - 1)
                        .Any(k => HasNominalHeadReading(slots[remaining[k]]));
                if (prevGoverned || !hasSecondSubjectNominal)
                    return false;
            }
        }
        return true;
    }

    private static bool HasFiniteAfterComma(TextAnalysis analysis, WordSlot[] slots, int[] remaining, int firstPos)
    {
        for (int p = remaining.Length - 1; p > firstPos; p--)
            if (!slots[remaining[p]].Quoted && HasFiniteReading(slots[remaining[p]]))
                return HasCommaBetweenRaw(analysis, slots[remaining[firstPos]].Word, slots[remaining[p]].Word);
        return false;
    }

    private static bool HasCommaBetweenRaw(TextAnalysis analysis, TokenView left, TokenView right)
    {
        int from = left.Token.Start + left.Token.Length, to = right.Token.Start;
        return to > from && analysis.Text.AsSpan(from, to - from).Contains(',');
    }

    private static bool HasUpcomingVerb(
        IReadOnlyList<TokenView> words, List<int>[] active, bool[] spaceNext, int start)
    {
        for (int j = start; j < words.Count && j <= start + 3; j++)
        {
            if (!spaceNext[j - 1]) return false;
            // A clitic's secondary finite homograph cannot prove its own host
            // ("Ти си ми приятел" still has the copular carrier "си").
            if (words[j].CanonicalText is not ("ми" or "си") && HasVerbReading(words[j], active[j])) return true;
            if (!BulgarianFunctionWords.HasAny(words[j].CanonicalText, FunctionWordClasses.PreVerbalCliticBridge))
                return false;
        }
        return false;
    }

    private static bool HasUpcomingClauseVerb(
        IReadOnlyList<TokenView> words, List<int>[] active, bool[] clauseBridgeNext, int start)
    {
        for (int j = start; j < words.Count && j <= start + 4; j++)
        {
            if (!clauseBridgeNext[j - 1]) return false;
            if (HasVerbReading(words[j], active[j]) && words[j].CanonicalText != "ще") return true;
            bool pureAdverb = !words[j].UsesGraph && active[j].Count > 0 &&
                active[j].All(r => words[j].Readings[r].SourceAdverb &&
                    words[j].Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adverb, HasUnrecognizedFeatures: false });
            if (!pureAdverb && !BulgarianFunctionWords.HasAny(words[j].CanonicalText, FunctionWordClasses.PreVerbalClauseBridge))
                return false;
        }
        return false;
    }

    private static bool HasSingularThirdSubjectBefore(TokenView prev, List<int> prevActive) =>
        !prev.UsesGraph && prevActive.Any(r =>
            prev.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Singular } ||
            prev.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun ||
            BulgarianFunctionWords.IsThirdSingularSubjectPronoun(prev.CanonicalText));

    private static bool StartsNominalMaterial(IReadOnlyList<TokenView> words, List<int>[] active, int index)
    {
        if (index >= words.Count) return false;
        TokenView w = words[index];
        if (w.UsesGraph) return IsDegreeGraphModifier(w);
        return active[index].Any(r => w.Readings[r].Category is Category.Noun or Category.Adjective or Category.Number ||
            Guard.TryGetDeterminerAxes(w, w.Readings[r], out _));
    }

    private static bool IsAdpositionComplementAfterHomograph(
        IReadOnlyList<TokenView> words,
        List<int>[] rawActive,
        bool[] quoted,
        bool[] spaceNext,
        bool[] clauseBridgeNext,
        int prepIndex)
    {
        int next = prepIndex + 1;
        if (next >= words.Count) return false;
        // Preserve a noun reading licensed by the preceding governor or modifier ("за под", "избрах под", "нов под").
        if (words[prepIndex].CanonicalText == "под" && prepIndex > 0 && spaceNext[prepIndex - 1] &&
            (IsActivePreposition(words[prepIndex - 1], rawActive[prepIndex - 1]) ||
             HasVerbReading(words[prepIndex - 1], rawActive[prepIndex - 1]) ||
             HasAgreeingIndefiniteNounAfterModifier(words[prepIndex - 1], rawActive[prepIndex - 1],
                words[prepIndex], rawActive[prepIndex], allowArticledModifier: true)))
            return false;
        if (StartsNominalMaterial(words, rawActive, next)) return true;

        string nc = words[next].CanonicalText;
        if (BulgarianFunctionWords.HasAny(nc, FunctionWordClasses.FullObliquePronoun)) return true;
        if (nc == "когото" ||
            BulgarianFunctionWords.HasAny(nc, FunctionWordClasses.RelativeSubjectPronoun) ||
            nc is "чийто" or "чиято" or "чието" or "чиито")
            return true;

        if (BulgarianFunctionWords.TryGetPersonalSubjectPronoun(nc, out _) && nc != "то")
        {
            if (words[prepIndex].CanonicalText == "край" && prepIndex > 0 && spaceNext[prepIndex - 1] &&
                rawActive[prepIndex - 1].Any(r =>
                    words[prepIndex - 1].Readings[r].Category is Category.Adjective or Category.Number ||
                    Guard.TryGetDeterminerAxes(words[prepIndex - 1], words[prepIndex - 1].Readings[r], out _) ||
                    Guard.TryGetOrdinalAxes(words[prepIndex - 1].Readings[r], out _)))
                return false;

            if (nc == "вие" && !SentenceHasAnotherVerb(words, rawActive, quoted, next))
                return false;

            for (int k = next + 1; k < words.Count && clauseBridgeNext[k - 1]; k++)
            {
                string ck = words[k].CanonicalText;
                if (ck != "да" && BulgarianFunctionWords.HasAny(ck,
                        FunctionWordClasses.ClauseCoordinatingConjunction | FunctionWordClasses.ClauseIntroducer))
                    break;
                if (ck == "да" ||
                    HasVerbReading(words[k], rawActive[k]) ||
                    words[k].Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) ||
                    !words[k].UsesGraph && rawActive[k].Any(r => words[k].Readings[r].Features is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                        VerbForm: MorphologicalVerbForm.Participle,
                        ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive
                    }))
                    return false;
            }
            return true;
        }

        return false;
    }

    private static bool HasNominalOrDegree(TokenView w, List<int> active) =>
        IsDegreeGraphModifier(w) ||
        !w.UsesGraph && active.Any(r => w.Readings[r].Category is Category.Noun or Category.Adjective);

    private static bool HasVerbReading(TokenView w, List<int> active) =>
        !w.UsesGraph && active.Any(r => w.Readings[r].Category == Category.Verb &&
            w.Readings[r].Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative);

    private static bool IsPurePersonalVerb(TokenView word, List<int> active) =>
        !word.UsesGraph && active.Count > 0 && active.All(r =>
            Guard.IsPersonalPredicate(word.Readings[r].Features) && IsCleanOrBenignLegacyVerb(word.Readings[r]));

    private static bool HasNounReading(TokenView w, List<int> active) =>
        !w.UsesGraph && active.Any(r => w.Readings[r].Category == Category.Noun);

    private static bool HasPluralNoun(TokenView w, List<int> active) =>
        !w.UsesGraph && active.Any(r => w.Readings[r].Category == Category.Noun &&
            w.Readings[r].Features.Number == MorphologicalNumber.Plural);

    private static bool HasSingularModifierForHora(TokenView w, List<int> active) =>
        !w.UsesGraph &&
        w.CanonicalText is not ("малко" or "немалко" or "малкото" or "немалкото") &&
        active.Any(r =>
            w.Readings[r].Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Number: MorphologicalNumber.Singular } ||
            Guard.TryGetDeterminerAxes(w, w.Readings[r], out var axes) && axes.Number == MorphologicalNumber.Singular);

    private static bool IsAfterModalOrFutureParticle(
        IReadOnlyList<TokenView> words, bool[] spaceNext, int index)
    {
        for (int k = index - 1; k >= 0 && k >= index - 3; k--)
        {
            if (!spaceNext[k]) return false;
            string ck = words[k].CanonicalText;
            if (ck is "да" or "ще") return true;
            if (!BulgarianFunctionWords.HasAny(ck, FunctionWordClasses.PreVerbalCliticBridge))
                return false;
        }
        return false;
    }

    private static bool SentenceHasAnotherVerb(
        IReadOnlyList<TokenView> words, List<int>[] active, bool[] quoted, int excludeIndex)
    {
        for (int k = 0; k < words.Count; k++)
        {
            if (k == excludeIndex || quoted[k] || words[k].UsesGraph) continue;
            if (HasVerbReading(words[k], active[k]) ||
                active[k].Any(r => TextPredicateLinkAnalyzer.CopularCarrier(words[k].Readings[r])))
                return true;
        }
        return false;
    }

    private static bool IsPureAdnominalModifier(TokenView w, List<int> active) =>
        !w.UsesGraph && active.Count > 0 && active.All(r =>
            w.Readings[r].Category == Category.Adjective ||
            Guard.IsMeasuredPassiveAttribute(w.Readings[r].Features) ||
            Guard.IsMeasuredPresentActiveAttribute(w.Readings[r].Features) ||
            Guard.TryGetOrdinalAxes(w.Readings[r], out _));

    private static bool HasAgreeingIndefiniteNounAfterModifier(
        TokenView mod, List<int> modActive, TokenView cand, List<int> candActive, bool allowArticledModifier)
    {
        if (!IsPureAdnominalModifier(mod, modActive) || cand.UsesGraph) return false;
        return modActive.Any(mr =>
        {
            MorphologicalFeatures mf = mod.Readings[mr].Features;
            if (!allowArticledModifier &&
                (mf.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
                 Guard.TryGetOrdinalAxes(mod.Readings[mr], out var ord) &&
                 ord.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
                return false;
            return candActive.Any(nr =>
                cand.Readings[nr].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Noun,
                    Article: MorphologicalArticle.Indefinite
                } nf && Guard.ModifierAgreement(mod, mod.Readings[mr], cand.Readings[nr],
                    includeAdnominalReadings: true, phraseInitial: true));
        });
    }

    private static bool HasCleanUnarticledActiveParticiple(TokenView w, List<int> active) =>
        !w.UsesGraph && active.Any(r => w.Readings[r].Features is
        {
            PartOfSpeech: MorphologicalPartOfSpeech.Verb,
            VerbForm: MorphologicalVerbForm.Participle,
            ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
            Article: null or MorphologicalArticle.Indefinite,
            HasConflictingFeatures: false
        } && IsCleanOrBenignLegacyVerb(w.Readings[r]) && !TextPredicateLinkAnalyzer.CopularLemma(w.Readings[r].Lemma));

    private static bool HasIndefiniteNounReading(TokenView w, List<int> active) =>
        !w.UsesGraph && active.Any(r => w.Readings[r].Features is
            { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Indefinite });

    private static bool IsPrecededByFirstOrSecondSubjectPronoun(
        IReadOnlyList<TokenView> words, bool[] spaceNext, int index)
    {
        for (int k = index - 1; k >= 0 && k >= index - 4; k--)
        {
            if (!spaceNext[k]) return false;
            string ck = words[k].CanonicalText;
            if (BulgarianFunctionWords.IsFirstOrSecondSubjectPronoun(ck, out _))
            {
                if (ck == "ти" && k > 0 && spaceNext[k - 1] &&
                    !BulgarianFunctionWords.HasAny(words[k - 1].CanonicalText,
                        FunctionWordClasses.ClauseCoordinatingConjunction | FunctionWordClasses.ClauseIntroducer))
                    return false;
                return true;
            }
            if (!BulgarianFunctionWords.HasAny(ck, FunctionWordClasses.PreVerbalCliticBridge))
                return false;
        }
        return false;
    }

    private static bool IsArticledFeatures(MorphologicalFeatures f) =>
        f.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short;

    private static bool HasAgreeingNounNext(TokenView mod, List<int> modActive, TokenView noun, List<int> nounActive)
    {
        if (mod.UsesGraph || noun.UsesGraph) return false;
        return modActive.Any(mr => (mod.Readings[mr].Category == Category.Adjective ||
                Guard.IsMeasuredPassiveAttribute(mod.Readings[mr].Features)) &&
            Guard.HasNominalAxes(mod.Readings[mr].Features) &&
            nounActive.Any(nr => noun.Readings[nr].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                !(mod.Readings[mr].Features.Article == MorphologicalArticle.Indefinite &&
                  IsArticledFeatures(noun.Readings[nr].Features)) &&
                Guard.AttributeAgreement(mod.Readings[mr].Features, noun.Readings[nr].Features, includePassiveParticiples: true)));
    }

    private static bool HasAgreeingNounPhraseAhead(
        IReadOnlyList<TokenView> words,
        List<int>[] active,
        bool[] spaceNext,
        bool[] clauseBridgeNext,
        int index)
    {
        if (index + 1 >= words.Count) return false;
        if (HasAgreeingNounNext(words[index], active[index], words[index + 1], active[index + 1]))
            return true;
        bool firstOnlyIndefinite = active[index]
            .Where(r => words[index].Readings[r].Category == Category.Adjective ||
                        Guard.IsMeasuredPassiveAttribute(words[index].Readings[r].Features))
            .All(r => words[index].Readings[r].Features.Article == MorphologicalArticle.Indefinite);
        if (firstOnlyIndefinite && !words[index + 1].UsesGraph && active[index + 1].Count > 0 &&
            active[index + 1].All(r => IsArticledFeatures(words[index + 1].Readings[r].Features)))
            return false;
        for (int nounIdx = index + 2; nounIdx < words.Count && nounIdx <= index + 3; nounIdx++)
        {
            if (!spaceNext[nounIdx - 1]) break;
            int prevIdx = nounIdx - 1;
            if (words[prevIdx].UsesGraph || active[prevIdx].Count == 0 ||
                !active[prevIdx].All(r => words[prevIdx].Readings[r].Category == Category.Adjective ||
                    Guard.IsMeasuredPassiveAttribute(words[prevIdx].Readings[r].Features)))
                break;
            if (HasAgreeingNounNext(words[index], active[index], words[nounIdx], active[nounIdx]) &&
                Enumerable.Range(index + 1, nounIdx - index - 1).All(j =>
                    HasAgreeingNounNext(words[j], active[j], words[nounIdx], active[nounIdx])) &&
                HasUpcomingClauseVerb(words, active, clauseBridgeNext, nounIdx + 1))
                return true;
        }
        return false;
    }

    private static bool ComputeTerminalPunctuation(TextAnalysis analysis, SentenceSpan sentence)
    {
        if (sentence.Start < 0 || sentence.Length < 0 || (long)sentence.Start + sentence.Length > analysis.Text.Length)
            return false;
        ReadOnlySpan<char> span = analysis.Text.AsSpan(sentence.Start, sentence.Length);
        while (!span.IsEmpty && Rune.DecodeLastFromUtf16(span, out Rune rune, out int consumed) == OperationStatus.Done)
        {
            span = span[..^consumed];
            if (Rune.IsWhiteSpace(rune) || Guard.IsDecorativePictographScalar(rune) ||
                rune.Value is '"' or '\'' or '\u00bb' or '\u201d' or '\u201c' or '\u2019' or ')' or ']' or '}')
                continue;
            return rune.Value is '.' or '!' or '?' or '\u2026';
        }
        return false;
    }

    private static bool IsPrecededBySubjectOrCliticChain(
        IReadOnlyList<TokenView> words,
        List<int>[] active,
        bool[] spaceNext,
        int index)
    {
        int k = index - 1;
        while (k >= 0 && spaceNext[k] &&
               (words[k].CanonicalText is "не" or "се" or "си" or "го" or "я" or "ги" or "ми" or "ти" or "му" or "ѝ" or "ни" or "ви" or "им"))
            k--;
        if (k < 0 || !spaceNext[k]) return false;
        TokenView subj = words[k];
        if (BulgarianFunctionWords.TryGetPersonalSubjectPronoun(subj.CanonicalText, out _))
            return true;
        return !subj.UsesGraph && active[k].Count > 0 &&
               active[k].All(r => subj.Readings[r].Features is
               {
                   Gender: MorphologicalGender.Masculine,
                   Number: MorphologicalNumber.Singular,
                   Article: MorphologicalArticle.Full
               });
    }

    private static bool HasCoordinatedAccusativeFinitePrefix(
        IReadOnlyList<TokenView> words, List<int>[] active, bool[] quoted, bool[] spaceNext,
        int index, CancellationToken ct)
    {
        if (index < 2 || !spaceNext[index - 2] || !spaceNext[index - 1] ||
            quoted[index - 2] || quoted[index - 1] || quoted[index])
            return false;
        TokenView coordinator = words[index - 2];
        TokenView host = words[index];
        // Preserve both host readings; a complete short accusative rules out a nominal prefix.
        return !coordinator.UsesGraph && coordinator.Complete && IsCoordinatingConjunction(coordinator) &&
            active[index - 2].Any(r => coordinator.Readings[r].Features.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction) &&
            Guard.AccusativeCliticReadings(words[index - 1], ct).Length > 0 &&
            !host.UsesGraph && host.Complete && active[index].Any(r =>
                host.Readings[r].Features.VerbForm == MorphologicalVerbForm.Finite &&
                Guard.IsPersonalPredicate(host.Readings[r].Features) && IsCleanOrBenignLegacyVerb(host.Readings[r]));
    }

    private static bool IsParticipialAppositiveOpener(WordSlot nounSlot, WordSlot openerSlot)
    {
        if (nounSlot.Word.UsesGraph || openerSlot.Word.UsesGraph || openerSlot.Active.Length == 0)
            return false;
        int[] headRows = NominalHeadReadings(nounSlot);
        if (headRows.Length == 0 && nounSlot.HyphenatedCompoundNominal is null)
            return false;
        return openerSlot.Active.All(r =>
        {
            MorphologicalFeatures f = openerSlot.Word.Readings[r].Features;
            return f.Article is null or MorphologicalArticle.Indefinite &&
                   (Guard.IsMeasuredPassiveAttribute(f) ||
                    Guard.IsMeasuredPresentActiveAttribute(f) ||
                    f is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                        VerbForm: MorphologicalVerbForm.Participle,
                        ParticipleKind: MorphologicalParticipleKind.PastAoristActive
                    });
        });
    }

    private static bool TryResolveHyphenatedNominalCompound(
        TokenView word,
        bool sentenceInitial,
        Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        out (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article) axes,
        CancellationToken ct)
    {
        axes = default;
        string canon = word.CanonicalText;
        string raw = word.Token.Text;
        int dash = canon.IndexOf('-');
        int rawDash = raw.IndexOf('-');
        if (dash < 2 || dash >= canon.Length - 2 || canon.IndexOf('-', dash + 1) >= 0 ||
            rawDash < 2 || rawDash >= raw.Length - 2 || raw.IndexOf('-', rawDash + 1) >= 0)
            return false;

        string part1 = canon[..dash];
        string part2 = canon[(dash + 1)..];
        if (part1 is "по" or "най") return false;

        for (int i = 0; i < raw.Length; i++)
        {
            if (i == rawDash) continue;
            char ch = raw[i];
            bool cyrillic = ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ѝ' or 'Ѝ' or '\u0300' or '\u0301';
            if (!cyrillic) return false;
        }
        for (int i = rawDash + 1; i < raw.Length; i++)
            if (char.IsUpper(raw[i])) return false;
        if (!sentenceInitial && char.IsUpper(raw[0])) return false;
        for (int i = 1; i < rawDash; i++)
            if (char.IsUpper(raw[i])) return false;

        if (!TryExtractPureCommonNounAxes(lookup(part1), part1, out var a1, ct) ||
            !TryExtractPureCommonNounAxes(lookup(part2), part2, out var a2, ct))
            return false;

        if (a2.Article == MorphologicalArticle.Indefinite && a1.Number == a2.Number)
        {
            axes = a1;
            return true;
        }
        if (a1 is { Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite } &&
            (a2.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
             a2.Number == MorphologicalNumber.Plural))
        {
            axes = a2;
            return true;
        }
        return false;
    }

    private static bool TryExtractPureCommonNounAxes(
        IReadOnlyList<TextReadingSnapshot>? rows,
        string key,
        out (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article) axes,
        CancellationToken ct)
    {
        axes = default;
        if (rows is null || rows.Count == 0) return false;
        MorphologicalNumber? num = null;
        MorphologicalGender? gen = null;
        MorphologicalArticle? art = null;
        bool sawNoun = false;
        foreach (TextReadingSnapshot snap in rows)
        {
            ct.ThrowIfCancellationRequested();
            MorphologicalFeatures f = snap.Features;
            if (f.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun)
            {
                if (snap.Raw.Form.Length > 0 && char.IsUpper(snap.Raw.Form[0])) continue;
                return false;
            }
            if (f.PartOfSpeech == MorphologicalPartOfSpeech.Verb) continue;
            if (f.PartOfSpeech != MorphologicalPartOfSpeech.Noun ||
                f.HasConflictingFeatures || f.HasUnrecognizedFeatures)
                return false;
            if (f.RawTag?.Contains("Звател", StringComparison.Ordinal) == true) continue;
            if (f.Number is not (MorphologicalNumber.Singular or MorphologicalNumber.Plural) ||
                f.Gender is null ||
                f.Article is not (MorphologicalArticle.Indefinite or MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
                continue;
            MorphologicalArticle normArt = f is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Definite }
                ? MorphologicalArticle.Short
                : f.Article.Value;
            if (!sawNoun)
            {
                num = f.Number.Value;
                gen = f.Gender.Value;
                art = normArt;
                sawNoun = true;
            }
            else if (num != f.Number.Value || gen != f.Gender.Value || art != normArt)
            {
                return false;
            }
        }
        if (!sawNoun) return false;
        axes = (num!.Value, gen, art!.Value);
        return true;
    }
}
