using System.Buffers;
using System.Text;
using BgLang.Contracts;
using Guard = BgLang.Core.ContextEvidenceGuards;
using Word = BgLang.Core.ContextLanes.TokenView;

namespace BgLang.Core;

internal static class PredicativeGrammarEvidence
{
    internal enum DependentKind { Adjective, PassiveParticiple, ActiveParticiple }

    internal sealed record ControllerBinding(Word Head, int[] Rows, bool Competing,
        NominalStructuralProjection.Binding? Phrase = null, IReadOnlyList<TextReadingReference>? Markers = null,
        Word? Address = null, bool AdverbBridgeOnlyCompeting = false);

    internal sealed record Dependent(Word Word, int[] Rows, DependentKind Kind, bool Competing);

    internal sealed record Frame(Word Finite, int[] FiniteRows, IReadOnlyList<Dependent> Dependents,
        ControllerBinding? Controller = null, IReadOnlyList<TextReadingReference>? Bridges = null,
        NominalStructuralProjection.Binding? CompetingTail = null)
    {
        internal bool Competing => Controller?.Competing == true || Dependents.Any(part => part.Competing);
        internal int GroupStart => Math.Min(Finite.Token.Start, Dependents[0].Word.Token.Start);
        internal int Start => Math.Min(Controller?.Address?.Token.Start ?? GroupStart,
            Math.Min(Controller?.Phrase?.Start ?? Controller?.Head.Token.Start ?? GroupStart, GroupStart));
        internal int End => Math.Max(Controller?.Address is { } address ? address.Token.Start + address.Token.Length : GroupStart,
            Math.Max(Finite.Token.Start + Finite.Token.Length,
                Dependents[^1].Word.Token.Start + Dependents[^1].Word.Token.Length));
    }

    private sealed record Relation(string Rule, string Code, Word Anchor, int[] AnchorRows, Word Target, int[] TargetRows,
        TextGrammarConstraint Constraint, Dependent Part, bool Competing, bool AllowsPolite = false, bool UsesController = false);

    internal static bool IsDiagnostic(TextDiagnostic diagnostic) =>
        diagnostic.RuleId != ClauseFusionAnalyzer.AgreementLane &&
        (diagnostic.Code is NominalGrammarRules.PredicativeCode or NominalGrammarRules.AuxiliaryParticipleCode ||
         diagnostic.RuleId is NominalGrammarRules.CopularFiniteId or NominalGrammarRules.CopularAdjectiveId or
             NominalGrammarRules.PassiveParticipleId or NominalGrammarRules.AnalyticFiniteId or
             NominalGrammarRules.ActiveControllerId or NominalGrammarRules.AuxiliaryParticipleId);

    internal static IReadOnlyList<Frame> Bind(TextAnalysis analysis, MorphologyEvidenceProjection.View morphology,
        bool includeNominalReview, CancellationToken ct, IReadOnlyList<SentenceClauseMap?>? sharedClauseMaps = null)
    {
        if (morphology.UnavailableReason is not null) return [];
        var protectedSpans = TextPredicateLinkAnalyzer.QuoteSpans(analysis.Text, ct, protectBackticks: true, protectBrackets: true);
        List<Frame> frames = [];
        for (int sIdx = 0; sIdx < analysis.Sentences.Count; sIdx++)
        {
            SentenceSpan sentence = analysis.Sentences[sIdx];
            ct.ThrowIfCancellationRequested();
            var words = NominalStructuralProjection.SentenceWords(analysis, sentence, morphology, ct);
            SentenceClauseMap clauseMap = sharedClauseMaps is not null && sIdx < sharedClauseMaps.Count && sharedClauseMaps[sIdx] is { } cachedMap
                ? cachedMap
                : SentenceClauseMap.Create(analysis, sentence, words, protectedSpans, ct);
            bool[] peripheral = clauseMap.Peripheral;
            int coreStart = 0;
            while (coreStart < words.Count && peripheral[coreStart]) coreStart++;
            int coreLast = words.Count - 1;
            while (coreLast >= coreStart && peripheral[coreLast]) coreLast--;
            int end = sentence.Start + sentence.Length;
            for (int i = coreStart; i + 1 <= coreLast; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (peripheral[i]) continue;
                Word left = words[i], right = words[i + 1];
                int rightEnd = right.Token.Start + right.Token.Length;
                if (Exact(left) && left.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier))
                {
                    int j = i + 1;
                    List<TextReadingReference>? postAuxBridges = null;
                    bool postAuxSe = false;
                    while (j <= coreLast && j - i <= 2 && !peripheral[j] && Exact(words[j]) && Horizontal(j - 1, j) &&
                        (IsAuxGroupBridgeToken(words[j]) || left.CanonicalText != "си" &&
                         (ExactAdverb(words[j]) || j + 1 <= coreLast && !peripheral[j + 1] && Horizontal(j, j + 1) &&
                          DisambiguatedPreDependentAdverb(words[j], words[j + 1], active: true))))
                    {
                        ct.ThrowIfCancellationRequested();
                        int[] bridgeRows = IsAuxGroupBridgeToken(words[j]) ? NonPlaceholderBridgeRows(words[j]) : Rows(words[j], IsCleanAdverbRow);
                        if (bridgeRows.Length == 0) break;
                        postAuxBridges ??= [];
                        postAuxBridges.AddRange(References(words[j], bridgeRows));
                        if (words[j].CanonicalText == "се") postAuxSe = true;
                        j++;
                    }
                    if (j <= coreLast && !peripheral[j] && Exact(words[j]) && Horizontal(j - 1, j) &&
                        !Protected(left.Token.Start, words[j].Token.Start + words[j].Token.Length))
                    {
                        Word depWord = words[j];
                        int groupStart = i;
                        bool hasFutureMarker = false;
                        bool preAuxSe = false;
                        List<TextReadingReference>? preAuxBridges = null;
                        int pre = i - 1;
                        List<(int Index, int[] Rows)>? pendingPreClitics = null;
                        while (pre >= coreStart && i - pre <= 3 && !peripheral[pre] && Exact(words[pre]) && Connected(pre, pre + 1) &&
                            IsPreAuxCliticToken(words[pre]))
                        {
                            int[] cliticRows = NonPlaceholderBridgeRows(words[pre]);
                            if (cliticRows.Length == 0) break;
                            pendingPreClitics ??= [];
                            pendingPreClitics.Insert(0, (pre, cliticRows));
                            pre--;
                        }
                        if (pre >= coreStart && !peripheral[pre] && Exact(words[pre]) && Connected(pre, pre + 1) &&
                            words[pre].CanonicalText == "ще" && FutureParticleReadings(words[pre]) is { Length: > 0 } shteRows &&
                            !Protected(words[pre].Token.Start, words[j].Token.Start + words[j].Token.Length))
                        {
                            hasFutureMarker = true;
                            groupStart = pre;
                            preAuxBridges = [.. References(words[pre], shteRows)];
                            if (pendingPreClitics is not null)
                                foreach (var (cIdx, cRows) in pendingPreClitics)
                                {
                                    preAuxBridges.AddRange(References(words[cIdx], cRows));
                                    if (words[cIdx].CanonicalText == "се") preAuxSe = true;
                                }
                        }

                        bool isImpersonalConditionalModal = left.CanonicalText == "би" &&
                            depWord.CanonicalText is "трябвало" or "следвало" or "могло";
                        bool isConditionalAux = !isImpersonalConditionalModal &&
                            left.CanonicalText is "бих" or "би" or "бихме" or "бихте" or "биха" &&
                            !(left.CanonicalText == "би" && i > coreStart && words[i - 1].CanonicalText is "може" or "не");

                        if (!isImpersonalConditionalModal &&
                            BindPair(left, depWord, active: true, analyticDisambiguated: isConditionalAux || hasFutureMarker) is { } active)
                        {
                            List<TextReadingReference>? allBridges = preAuxBridges;
                            if (postAuxBridges is not null)
                            {
                                allBridges ??= [];
                                allBridges.AddRange(postAuxBridges);
                            }
                            if (allBridges is not null)
                                active = active with { Bridges = allBridges.AsReadOnly() };
                            int activeEnd = depWord.Token.Start + depWord.Token.Length;
                            var firstActive = active.Dependents[0];
                            var competingTail = SubstantiveTail(j, left, active.FiniteRows, depWord, firstActive.Rows);
                            bool nominalRival = !isConditionalAux && HasNominalRival(j + 1, left, active.FiniteRows, depWord, firstActive.Rows, requireParticipleMatch: true);
                            if (competingTail is not null || nominalRival) active = active with
                            { Dependents = [firstActive with { Competing = true }], CompetingTail = competingTail };
                            var preController = Prefix(groupStart, activeEnd, allowClitic: !hasFutureMarker);
                            if (preController is { Competing: false } && !isConditionalAux && !nominalRival &&
                                !active.FiniteRows.Any(f => firstActive.Rows.Any(p =>
                                    left.Readings[f].Features.Number == depWord.Readings[p].Features.Number)) &&
                                HasNominalRival(j + 1, left, active.FiniteRows, depWord, firstActive.Rows, requireParticipleMatch: false))
                            {
                                preController = preController with { Competing = true };
                            }
                            var extended = Extend(active with { Controller = preController }, j + 1);
                            if (extended.Controller is null && includeNominalReview && !Protected(sentence.Start, end) &&
                                !extended.Competing && BindDependent(depWord, active: false) is null)
                                extended = extended with { Controller = ActiveNominalController(groupStart, j, preAuxSe || postAuxSe, extended) };
                            frames.Add(WithAddress(extended));
                        }
                    }
                }
                if (!peripheral[i + 1] && Exact(left) && Exact(right) && !Protected(left.Token.Start, rightEnd) && Horizontal(i, i + 1))
                {
                    if (BindPair(right, left, active: true) is { } reversed)
                        frames.Add(WithAddress(Extend(reversed with { Controller = Prefix(i, rightEnd, allowClitic: true) }, i + 2)));
                }
                if (Exact(left) && left.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) && Nominal(i) is { } nominal)
                    frames.Add(WithAddress(nominal));
            }
            if (includeNominalReview && coreStart <= coreLast)
                BindBareActiveParticiples(analysis, sentence, words, clauseMap, coreStart, coreLast, protectedSpans, frames, ct);

            Frame WithAddress(Frame frame)
            {
                if (frame.Controller is not { Address: null } subject || !SecondPlural(subject) || coreLast < coreStart) return frame;
                Word address = words[coreLast];
                if (address.Token.Start <= frame.End || !SingularVocative(address) ||
                    !CoreTerminalTail(analysis, sentence, words, coreLast, coreLast) ||
                    !CommaBefore(analysis, sentence, address, out int comma) ||
                    !analysis.Text.AsSpan(frame.End, comma - frame.End).Trim().IsEmpty ||
                    Protected(subject.Head.Token.Start, address.Token.Start + address.Token.Length)) return frame;
                return frame with { Controller = AddressSubject(subject, address) };
            }

            Frame? Nominal(int carrier)
            {
                int index = carrier + 1;
                List<TextReadingReference>? bridges = null;
                while (index <= coreLast && !peripheral[index] && Connected(index - 1, index) &&
                    (ExactAdverb(words[index]) || index + 1 <= coreLast && !peripheral[index + 1] && Connected(index, index + 1) &&
                     DisambiguatedPreDependentAdverb(words[index], words[index + 1], active: false)))
                {
                    ct.ThrowIfCancellationRequested();
                    bridges ??= [];
                    bridges.AddRange(References(words[index], Rows(words[index], IsCleanAdverbRow)));
                    index++;
                }
                if (index > coreLast || peripheral[index] || !Connected(index - 1, index) || !Exact(words[index]) ||
                    Protected(words[carrier].Token.Start, words[index].Token.Start + words[index].Token.Length) ||
                    BindPair(words[carrier], words[index], active: false) is not { } frame) return null;
                if (!HasAdjectivalTail(analysis, sentence, words, coreLast, index, words[carrier], frame.FiniteRows,
                        frame.Dependents[0].Word, frame.Dependents[0].Rows, ct, out var competingTail)) return null;
                if (competingTail is not null) frame = frame with { Dependents = [frame.Dependents[0] with { Competing = true }] };
                var subject = Prefix(carrier, words[index].Token.Start + words[index].Token.Length, allowClitic: false);
                if (subject is null && includeNominalReview && !Protected(sentence.Start, end))
                    subject = ClosedNominal(analysis, sentence, words, carrier,
                        frame.Dependents[0].Kind is DependentKind.Adjective or DependentKind.PassiveParticiple &&
                        bridges is null, peripheral, coreStart, ct);
                return subject is null && competingTail is null ? null : frame with
                { Controller = subject, Bridges = bridges?.AsReadOnly(), CompetingTail = competingTail };
            }

            ControllerBinding? ActiveNominalController(int groupStart, int firstDepIndex, bool hasAuxGroupSe, Frame extended)
            {
                int subjBoundary = groupStart;
                IReadOnlyList<TextReadingReference>? seRefs = null;
                if (groupStart >= coreStart + 1 && !peripheral[groupStart - 1] && Connected(groupStart - 1, groupStart) &&
                    words[groupStart - 1].CanonicalText == "се" &&
                    ReflexiveMarkerReadings(words[groupStart - 1]) is { Length: > 0 } seRows)
                {
                    subjBoundary = groupStart - 1;
                    seRefs = References(words[groupStart - 1], seRows);
                }
                if (ClosedNominal(analysis, sentence, words, subjBoundary, legacyAdjectival: true, peripheral, coreStart, ct) is not { Competing: false } subj)
                    return null;
                if (seRefs is not null)
                    subj = subj with
                    {
                        Markers = subj.Markers is { Count: > 0 } m
                            ? Array.AsReadOnly(m.Concat(seRefs).ToArray())
                            : seRefs
                    };
                bool hasSe = seRefs is not null || hasAuxGroupSe;

                Dependent firstPart = extended.Dependents[0];
                bool intransitiveOrCopular = firstPart.Rows.All(r =>
                    firstPart.Word.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive ||
                    TextPredicateLinkAnalyzer.CopularLemma(firstPart.Word.Readings[r].Lemma));
                bool fullArticleSingular = subj.Rows.All(r =>
                        subj.Head.Readings[r].Features.Number == MorphologicalNumber.Singular) &&
                    (subj.Phrase is null
                        ? subj.Rows.All(r => subj.Head.Readings[r].Features.Article == MorphologicalArticle.Full)
                        : subj.Phrase.Participants[0].RoleReadings.All(r =>
                            subj.Phrase.Participants[0].Word.Readings[r].Features is
                                { Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Full }));

                int lastDep = firstDepIndex + extended.Dependents.Count - 1;
                Dependent tailDep = extended.Dependents[^1];
                if (HasAdjectivalTail(analysis, sentence, words, coreLast, lastDep, extended.Finite, extended.FiniteRows,
                        tailDep.Word, tailDep.Rows, ct, out var tailCompeting))
                    return tailCompeting is null && (hasSe || intransitiveOrCopular || fullArticleSingular) ? subj : null;

                int advCursor = lastDep;
                while (advCursor + 1 <= coreLast && !peripheral[advCursor + 1] && Connected(advCursor, advCursor + 1) && ExactAdverb(words[advCursor + 1]))
                {
                    ct.ThrowIfCancellationRequested();
                    advCursor++;
                }
                int objStart = advCursor + 1;
                if (objStart > coreLast || peripheral[objStart] || !Connected(advCursor, objStart)) return null;
                int objEnd = objStart;
                while (objEnd + 1 <= coreLast && !peripheral[objEnd + 1] && Connected(objEnd, objEnd + 1) &&
                    words[objEnd].Readings.Any(r => OrdinaryAttribute(r) || Guard.TryGetDeterminerAxes(words[objEnd], r, out _)))
                {
                    ct.ThrowIfCancellationRequested();
                    objEnd++;
                }
                bool validObj = objEnd == objStart
                    ? ExactGovernedSingleNoun(words[objStart])
                    : NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, objEnd, ct, exactContiguous: true)
                        is { } objNp && objNp.Participants[0].WordIndex == objStart;
                if (!validObj || words[objStart].Readings.Any(r => r.Features.Article == MorphologicalArticle.Full) ||
                    !HasAdjectivalTail(analysis, sentence, words, coreLast, objEnd, extended.Finite, extended.FiniteRows,
                        tailDep.Word, tailDep.Rows, ct, out var afterObjCompeting) ||
                    afterObjCompeting is not null)
                    return null;

                bool obliqueShortObject = IsObliqueShortMasculineCarrier(words[objStart]);
                return hasSe || fullArticleSingular || obliqueShortObject ? subj : null;
            }

            Frame Extend(Frame frame, int next)
            {
                List<Dependent>? chain = null;
                var competingTail = frame.CompetingTail;
                Dependent previous = frame.Dependents[0];
                while (next <= coreLast && !peripheral[next] && previous.Kind == DependentKind.ActiveParticiple &&
                    previous.Rows.Any(row => TextPredicateLinkAnalyzer.CopularLemma(previous.Word.Readings[row].Lemma)))
                {
                    ct.ThrowIfCancellationRequested();
                    Word word = words[next], preceding = words[next - 1];
                    if (!Exact(word) || Protected(preceding.Token.Start, word.Token.Start + word.Token.Length) ||
                        !Horizontal(next - 1, next)) break;
                    var dependent = BindDependent(word, active: true);
                    NominalStructuralProjection.Binding? tail;
                    if (dependent is not null) tail = SubstantiveTail(next, frame.Finite, frame.FiniteRows, dependent.Word, dependent.Rows);
                    else if ((dependent = BindDependent(word, active: false)) is null ||
                        !HasAdjectivalTail(analysis, sentence, words, coreLast, next, frame.Finite, frame.FiniteRows,
                            dependent.Word, dependent.Rows, ct, out tail)) break;
                    competingTail ??= tail;
                    // The copular reading licenses the extension. A lexical active reading retains a competing path.
                    bool competing = tail is not null || dependent.Competing || previous.Competing || previous.Rows.Any(row =>
                        !TextPredicateLinkAnalyzer.CopularLemma(previous.Word.Readings[row].Lemma));
                    previous = dependent with { Competing = competing };
                    chain ??= [frame.Dependents[0]];
                    chain.Add(previous);
                    next++;
                }
                return chain is null ? frame : frame with { Dependents = chain.AsReadOnly(), CompetingTail = competingTail };
            }

            NominalStructuralProjection.Binding? SubstantiveTail(int dependent, Word finite, int[] finiteRows, Word depWord, int[] depRows) =>
                TryPrepositionalTail(analysis, sentence, words, coreLast, dependent, ct, out var phrase)
                    ? CompetingPrepositionalSplit(phrase, words[dependent + 1], finite, finiteRows, depWord, depRows, ct) : null;

            ControllerBinding? Prefix(int groupStart, int scopeEnd, bool allowClitic)
            {
                int prev = groupStart - 1;
                while (prev >= coreStart && peripheral[prev]) prev--;
                if (prev < coreStart || prev == groupStart - 1 && !Connected(prev, groupStart)) return null;
                if (Subject(prev, []) is { } direct) return direct;
                if (prev != groupStart - 1 || groupStart < coreStart + 2) return null;
                int prev2 = groupStart - 2;
                while (prev2 >= coreStart && peripheral[prev2]) prev2--;
                if (prev2 < coreStart || prev2 == groupStart - 2 && !Connected(prev2, groupStart - 1)) return null;
                Word marker = words[groupStart - 1];
                if (ExactDa(marker)) return Subject(prev2, References(marker, Rows(marker, TextPredicateLinkAnalyzer.DaMarker)), da: true);
                if (!allowClitic) return null;
                int[] markers = ReflexiveMarkerReadings(marker);
                if (markers.Length == 0) markers = Guard.AccusativeCliticReadings(marker, ct);
                return markers.Length == 0 ? null : Subject(prev2, References(marker, markers));

                ControllerBinding? Subject(int index, IReadOnlyList<TextReadingReference> markers, bool da = false)
                {
                    Word word = words[index];
                    if (!Exact(word)) return null;
                    int[] controllers = Rows(word, row => Guard.LicensedSubject(word.CanonicalText, row));
                    if (controllers.Length == 0) return null;
                    int scopeStart = word.Token.Start;
                    bool competing = word.CanonicalText != "те" && controllers.Length != word.Readings.Count;
                    var subject = new ControllerBinding(word, controllers, competing, Markers: markers);
                    if (index == coreStart)
                    {
                        if (coreStart == 0 && !analysis.Text.AsSpan(sentence.Start, scopeStart - sentence.Start).Trim().IsEmpty) return null;
                    }
                    else
                    {
                        Word connector = words[index - 1];
                        if (index == coreStart + 1 && SingularVocative(connector) && SecondPlural(subject) &&
                            CommaBefore(analysis, sentence, word, out int comma) &&
                            analysis.Text.AsSpan(connector.Token.Start + connector.Token.Length,
                                comma - connector.Token.Start - connector.Token.Length).Trim().IsEmpty &&
                            (coreStart > 0 || analysis.Text.AsSpan(sentence.Start, connector.Token.Start - sentence.Start).Trim().IsEmpty))
                        {
                            scopeStart = connector.Token.Start;
                            subject = AddressSubject(subject, connector);
                        }
                        else if (da && ExactFiniteBoundary(connector) && Connected(index - 1, index))
                        {
                            scopeStart = connector.Token.Start;
                            subject = subject with { Markers = Array.AsReadOnly(References(connector,
                                Rows(connector, TextPredicateLinkAnalyzer.Finite)).Concat(markers).ToArray()) };
                        }
                        else
                        {
                            var connectorRows = Guard.SubordinateConnectorReadings(connector, ct);
                            if (connectorRows.Length == 0 || !Connected(index - 1, index) ||
                                !CommaBefore(analysis, sentence, connector, out scopeStart)) return null;
                            subject = subject with { Markers = Array.AsReadOnly(References(connector, connectorRows).Concat(markers).ToArray()) };
                        }
                    }
                    return Protected(scopeStart, scopeEnd) ? null : subject;
                }
            }

            bool HasNominalRival(int next, Word finite, int[] finiteRows, Word participle, int[] participleRows, bool requireParticipleMatch)
            {
                // An active participle can also modify a following bare nominal in a copular complement.
                // A proved PP consumes its first nominal head. A later head still licenses the rival.
                bool governed = false, determined = false;
                for (int index = next; index <= coreLast; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (peripheral[index]) continue;
                    Word word = words[index];
                    if (Protected(word.Token.Start, word.Token.Start + word.Token.Length)) continue;
                    if (ExactFiniteBoundary(word) || ExactDa(word) && FollowingFinite(index)) break;
                    if (CommaBefore(analysis, sentence, word, out _) &&
                        (Guard.SubordinateConnectorReadings(word, ct).Length > 0 ||
                         Exact(word) && word.Readings.All(row => Guard.LicensedSubject(word.CanonicalText, row)) && FollowingFinite(index))) break;
                    if (Guard.IsExactAdposition(word)) { governed = true; continue; }
                    if (word.CanonicalText is "цял" or "цяла" or "цели" ||
                        word.Readings.Any(row => Guard.TryGetDeterminerAxes(word, row, out _)))
                    { determined = true; continue; }
                    if (!Exact(word) || !word.Readings.Any(CommonNoun) || IsVocative(word)) continue;
                    if (!governed && !determined && word.Readings.Any(row => CommonNoun(row) &&
                        row.Features.Article == MorphologicalArticle.Indefinite &&
                        finiteRows.Any(f => finite.Readings[f].Features.Number == row.Features.Number) &&
                        (!requireParticipleMatch || participleRows.Any(p => Guard.NominalTupleMatches(
                            (participle.Readings[p].Features.Number, participle.Readings[p].Features.Gender),
                            (row.Features.Number, row.Features.Gender)))))) return true;
                    governed = false;
                    determined = false;
                }
                return false;
            }

            bool FollowingFinite(int index) => index + 1 < words.Count && Connected(index, index + 1) &&
                Exact(words[index + 1]) && words[index + 1].Readings.Any(TextPredicateLinkAnalyzer.Finite);

            bool Connected(int left, int right) => LocalClauseRoleLinker.Connected(analysis, words[left], words[right], symbols: false, ct);
            bool Horizontal(int left, int right) => TextPredicateLinkAnalyzer.Horizontal(
                analysis.Text[(words[left].Token.Start + words[left].Token.Length)..words[right].Token.Start]);
        }
        return frames.AsReadOnly();

        bool Protected(int start, int end) => protectedSpans.Any(span => span.Start < end && span.End > start);
    }

    private static ControllerBinding? ClosedNominal(TextAnalysis analysis, SentenceSpan sentence, IReadOnlyList<Word> words,
        int carrier, bool legacyAdjectival, bool[] peripheral, int coreStart, CancellationToken ct)
    {
        int subjectEnd = carrier - 1;
        while (subjectEnd >= coreStart && peripheral[subjectEnd]) subjectEnd--;
        if (subjectEnd < coreStart ||
            coreStart == 0 && !analysis.Text.AsSpan(sentence.Start, words[0].Token.Start - sentence.Start).Trim().IsEmpty)
            return null;
        for (int i = coreStart; i <= subjectEnd; i++)
            if (peripheral[i] || !Exact(words[i])) return null;
        for (int i = coreStart; i < subjectEnd; i++)
            if (!LocalClauseRoleLinker.Connected(analysis, words[i], words[i + 1], symbols: false, ct)) return null;
        if (subjectEnd == carrier - 1 && !LocalClauseRoleLinker.Connected(analysis, words[subjectEnd], words[carrier], symbols: false, ct))
            return null;
        List<TextReadingReference>? markers = null;
        for (int adv = 0; adv < 2 && subjectEnd >= coreStart + 1 && ExactAdverb(words[subjectEnd]); adv++)
        {
            markers ??= [];
            markers.InsertRange(0, References(words[subjectEnd], Rows(words[subjectEnd], _ => true)));
            subjectEnd--;
        }
        int head = subjectEnd;
        if (subjectEnd >= coreStart + 2 && Guard.IsExactAdposition(words[coreStart + 1]))
        {
            int ppEnd = subjectEnd;
            if (ppEnd >= coreStart + 3 && PossessiveClitic(words[ppEnd])) ppEnd--;
            bool validPp = ppEnd == coreStart + 2 && ExactGovernedSingleNoun(words[coreStart + 2]) ||
                NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, ppEnd, ct, exactContiguous: true)
                    is { Participants.Count: > 0 } pp && pp.Participants[0].WordIndex == coreStart + 2;
            if (validPp)
            {
                head = coreStart;
                markers ??= [];
                for (int m = coreStart + 1; m <= subjectEnd; m++)
                    markers.InsertRange(m == coreStart + 1 ? 0 : markers.Count, References(words[m], Rows(words[m], _ => true)));
            }
        }
        Word controller = words[head];
        int[] controllers = Rows(controller, CommonNoun);
        if (controllers.Length == 0) return null;
        int activeControllerRows = controller.Readings.Count(row => !SourcePlaceholder(controller, row) &&
            !Guard.IsUnwrittenCapitalizedReading(controller, row, head == 0) &&
            !Guard.IsSuppressedPresentActiveNounHomograph(controller, row));
        bool competing = controllers.Length != activeControllerRows;
        bool adverbBridgeOnlyCompeting = false;
        NominalStructuralProjection.Binding? phrase = null;
        if (head == coreStart)
        {
            if (controllers.Any(r => controller.Readings[r].Features.Article is not
                (MorphologicalArticle.Definite or MorphologicalArticle.Full))) return null;
        }
        else
        {
            phrase = NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, head, ct);
            if (phrase is null || phrase.Participants.Count != head - coreStart + 1 ||
                phrase.Participants[0].WordIndex != coreStart ||
                phrase.Bridges.Any(bridge => bridge.Kind != NominalStructuralProjection.BridgeKind.Whitespace)) return null;
            var first = phrase.Participants[0];
            if (IsTemporalAdjunctPair(first.Word, controller)) return null;
            bool firstDefinite = first.RoleReadings.All(r => first.Word.Readings[r].Features.Article is
                MorphologicalArticle.Definite or MorphologicalArticle.Full ||
                DemonstrativeDeterminer(first.Word, first.Word.Readings[r]) ||
                Guard.TryGetOrdinalAxes(first.Word.Readings[r], out var ord) &&
                    ord.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full);
            if (!firstDefinite) return null;
            static bool AttributeModifier(ContextLanes.ReadingView row) =>
                OrdinaryAttribute(row) || Guard.IsMeasuredPassiveAttribute(row.Features) ||
                Guard.IsMeasuredPresentActiveAttribute(row.Features) || Guard.TryGetOrdinalAxes(row, out _);
            var modifiers = phrase.Participants.Take(head - coreStart).ToArray();
            if (modifiers.Any(member => !member.Word.Readings.Any(row => AttributeModifier(row) ||
                DemonstrativeDeterminer(member.Word, row)))) return null;
            bool hasAdjectiveModifier = modifiers.Any(member => member.Word.Readings.Any(AttributeModifier));
            bool otherCompeting = modifiers.Any(member => member.Word.Readings.Where(row => !SourcePlaceholder(member.Word, row) &&
                    !Guard.IsUnwrittenCapitalizedReading(member.Word, row, member.WordIndex == 0) &&
                    !Guard.IsSuppressedDeterminerHomograph(member.Word, row) &&
                    !Guard.IsSuppressedPresentActiveNounHomograph(member.Word, row))
                .Any(row => !AttributeModifier(row) && !DemonstrativeDeterminer(member.Word, row))) ||
                NominalStructuralProjection.AssessPartition(analysis.Text, words, coreStart, head, ct) !=
                    NominalStructuralProjection.Partition.None;
            adverbBridgeOnlyCompeting = !competing && !otherCompeting && !legacyAdjectival && hasAdjectiveModifier;
            competing |= otherCompeting || (!legacyAdjectival && hasAdjectiveModifier);
        }
        return new(controller, controllers, competing, phrase, markers?.AsReadOnly(), AdverbBridgeOnlyCompeting: adverbBridgeOnlyCompeting);
    }

    private static bool DemonstrativeDeterminer(Word word, ContextLanes.ReadingView row) =>
        Guard.TryGetDeterminerAxes(word, row, out _) &&
        BulgarianTextNormalizer.NormalizeKey(row.Lemma) is "този" or "онзи";

    private static bool PossessiveClitic(Word word) =>
        BulgarianFunctionWords.HasAny(word.CanonicalText, FunctionWordClasses.PossessiveOrDativeClitic);

    private static bool IsTemporalAdjunctPair(Word modifier, Word noun) =>
        modifier.CanonicalText is "всеки" or "всяка" or "всяко" or "цял" or "цяла" or "целия" or "цялата" or
            "миналата" or "последната" or "следващата" or "тази" &&
        noun.CanonicalText is "ден" or "нощ" or "вечер" or "сутрин" or "седмица" or "година" or "месец" or "зима" or "лято";

    private static Frame? BindPair(Word finite, Word dependent, bool active, bool analyticDisambiguated = false)
    {
        int[] carriers = Rows(finite, TextPredicateLinkAnalyzer.CopularCarrier);
        if (carriers.Length == 0) return null;
        if (BindDependent(dependent, active) is not { } part) return null;
        // The exact source placeholder remains in the original inventory.
        bool competing = part.Competing || (!analyticDisambiguated && finite.Readings.Any(row => !TextPredicateLinkAnalyzer.CopularCarrier(row) &&
            !SourcePlaceholder(finite, row)));
        return new(finite, carriers, [part with { Competing = competing }]);
    }

    private static bool IsPreAuxCliticToken(Word word) =>
        BulgarianFunctionWords.HasAny(word.CanonicalText,
            FunctionWordClasses.ReflexiveAccusativeSe | FunctionWordClasses.PronominalVerbalClitic);

    private static bool IsAuxGroupBridgeToken(Word word) =>
        IsPreAuxCliticToken(word) || word.CanonicalText == "ли";

    private static int[] NonPlaceholderBridgeRows(Word word) =>
        Rows(word, row => !SourcePlaceholder(word, row) && !row.Features.HasConflictingFeatures);

    private static int[] FutureParticleReadings(Word word) =>
        word.CanonicalText == "ще"
            ? Rows(word, row => !SourcePlaceholder(word, row) && !row.Features.HasConflictingFeatures &&
                !row.Features.HasUnrecognizedFeatures && row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle)
            : [];

    private static Dependent? BindDependent(Word word, bool active)
    {
        int[] rows = active ? Rows(word, ActiveParticiple) : Rows(word, Adjectival);
        if (rows.Length == 0) return null;
        var kind = active ? DependentKind.ActiveParticiple : rows.Any(row => Guard.IsMeasuredPassiveAttribute(word.Readings[row].Features))
            ? DependentKind.PassiveParticiple : DependentKind.Adjective;
        bool competing = word.Readings.Where((_, idx) => !rows.Contains(idx))
            .Any(row => !SourcePlaceholder(word, row) && !Guard.IsUnwrittenCapitalizedReading(word, row));
        return new(word, rows, kind, competing);
    }

    private static bool HasAdjectivalTail(TextAnalysis analysis, SentenceSpan sentence, IReadOnlyList<Word> words,
        int coreLast, int index, Word finite, int[] finiteRows, Word dependent, int[] dependentRows,
        CancellationToken ct, out NominalStructuralProjection.Binding? competing)
    {
        competing = null;
        int last = coreLast, cursor = index;
        while (cursor < last && LocalClauseRoleLinker.Connected(analysis, words[cursor], words[cursor + 1], symbols: false, ct) &&
            (ExactAdverb(words[cursor + 1]) || words[cursor + 1].CanonicalText == "още" && cursor + 2 <= last && ExactAdverb(words[cursor + 2])))
        {
            ct.ThrowIfCancellationRequested();
            cursor++;
        }
        if (cursor == last) return CoreTerminalTail(analysis, sentence, words, last, last);
        Word next = words[cursor + 1];
        if (CommaBefore(analysis, sentence, next, out _) && (cursor + 1 == last && IsVocative(next) &&
            CoreTerminalTail(analysis, sentence, words, last, last) ||
            Guard.SubordinateConnectorReadings(next, ct).Length > 0)) return true;
        if (cursor + 2 == last && LocalClauseRoleLinker.Connected(analysis, words[cursor], next, symbols: false, ct) &&
            LocalClauseRoleLinker.Connected(analysis, next, words[last], symbols: false, ct) &&
            IsTemporalAdjunctPair(next, words[last]) && CoreTerminalTail(analysis, sentence, words, last, last))
            return true;
        if (!TryPrepositionalTail(analysis, sentence, words, last, cursor, ct, out var phrase)) return false;
        competing = CompetingPrepositionalSplit(phrase, words[cursor + 1], finite, finiteRows, dependent, dependentRows, ct);
        return true;
    }

    private static bool TryPrepositionalTail(TextAnalysis analysis, SentenceSpan sentence, IReadOnlyList<Word> words,
        int coreLast, int index, CancellationToken ct, out NominalStructuralProjection.Binding? phrase)
    {
        phrase = null;
        int last = coreLast;
        if (index + 2 > last || !Guard.IsExactAdposition(words[index + 1]) || !CoreTerminalTail(analysis, sentence, words, last, last) ||
            !LocalClauseRoleLinker.Connected(analysis, words[index], words[index + 1], symbols: false, ct) ||
            !LocalClauseRoleLinker.Connected(analysis, words[index + 1], words[index + 2], symbols: false, ct)) return false;
        if (index + 2 == last && (Guard.ObliquePronounReadings(words[last], ct).Length > 0 || ExactGovernedSingleNoun(words[last])))
            return true;
        phrase = NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, last, ct, exactContiguous: true);
        return phrase?.Participants[0].WordIndex == index + 2;
    }

    private static bool CoreTerminalTail(
        TextAnalysis analysis, SentenceSpan sentence, IReadOnlyList<Word> words, int coreLast, int targetIndex) =>
        TerminalTail(analysis, sentence, targetIndex == coreLast ? words[^1] : words[targetIndex]);

    private static bool ExactGovernedSingleNoun(Word word)
    {
        if (!Exact(word)) return false;
        var active = word.Readings.Where(row => !SourcePlaceholder(word, row) &&
            !Guard.IsUnwrittenCapitalizedReading(word, row)).ToArray();
        return active.Length > 0 &&
            active.All(row => row.Category == ContextLanes.Category.Noun && row.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false, HasUnrecognizedFeatures: false }) &&
            active.Any(CommonNoun);
    }

    private static NominalStructuralProjection.Binding? CompetingPrepositionalSplit(
        NominalStructuralProjection.Binding? phrase, Word prepWord, Word finite, int[] finiteRows,
        Word dependent, int[] dependentRows, CancellationToken ct)
    {
        // A definite modifier can head the governed expression, leaving a bare predicative noun outside it.
        if (phrase is not { Participants.Count: > 1 }) return null;
        var first = phrase.Participants[0];
        if (!first.RoleReadings.Any(row => NominalStructuralProjection.IsSubstantiveModifier(first.Word.Readings[row].Features)))
            return null;
        bool presentOtSplit = prepWord.CanonicalText == "от" &&
            finiteRows.Any(f => finite.Readings[f].Features.Tense == MorphologicalTense.Present) &&
            dependentRows.Any(d => Guard.IsMeasuredPassiveAttribute(dependent.Readings[d].Features));
        if (!presentOtSplit && NominalStructuralProjection.Measure(phrase, ct) is { } measured &&
            NominalGrammarEvidence.FullyAgrees(phrase, measured))
            return null;
        return phrase.Head.RoleReadings.Any(row => phrase.Head.Word.Readings[row].Features is { Article: MorphologicalArticle.Indefinite } head &&
            finiteRows.Any(f => finite.Readings[f].Features.Number == head.Number) &&
            (presentOtSplit || dependentRows.Any(d => Guard.NominalTupleMatches(
                (dependent.Readings[d].Features.Number, dependent.Readings[d].Features.Gender),
                (head.Number, head.Gender)))))
            ? phrase : null;
    }

    private static bool CommaBefore(TextAnalysis analysis, SentenceSpan sentence, Word word, out int start)
    {
        start = -1;
        if (word.OriginalIndex == 0 || analysis.Tokens[word.OriginalIndex - 1] is not { Kind: TokenKind.Punctuation, Text: "," } comma ||
            comma.Start < sentence.Start || !analysis.Text.AsSpan(comma.Start + comma.Length, word.Token.Start - comma.Start - comma.Length)
                .Trim().IsEmpty) return false;
        start = comma.Start;
        return true;
    }

    private static bool IsCleanAdverbRow(ContextLanes.ReadingView row) => row.SourceAdverb &&
        row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adverb, HasConflictingFeatures: false, HasUnrecognizedFeatures: false };

    private static bool ExactAdverb(Word word) => Exact(word) && word.Readings.All(IsCleanAdverbRow);

    private static bool DisambiguatedPreDependentAdverb(Word word, Word next, bool active) =>
        Exact(word) && Exact(next) &&
        word.Readings.Any(IsCleanAdverbRow) &&
        word.Readings.All(row => IsCleanAdverbRow(row) || SourcePlaceholder(word, row) || Guard.IsUnwrittenCapitalizedReading(word, row) ||
            OrdinaryAttribute(row) && row.Features is
            {
                Gender: MorphologicalGender.Neuter,
                Number: MorphologicalNumber.Singular,
                Article: MorphologicalArticle.Indefinite,
                HasConflictingFeatures: false,
                HasUnrecognizedFeatures: false
            }) &&
        BindDependent(next, active) is not null &&
        (active || !next.Readings.Any(CommonNoun));

    private static bool IsVocative(Word word) => Exact(word) && word.Readings.All(row =>
        row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false, RawTag: { } tag } &&
        tag.Split(',', StringSplitOptions.TrimEntries).Contains("Звателна", StringComparer.Ordinal));

    private static bool SingularVocative(Word word) => IsVocative(word) && word.Readings.All(row =>
        row.Features is { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine or MorphologicalGender.Feminine } &&
        (!row.Features.HasUnrecognizedFeatures || VerifiedLegacyAtom(row.Features, atom => atom == "Звателна")));

    private static bool SecondPlural(ControllerBinding subject) => subject.Rows.All(row => subject.Head.Readings[row].Features is
        { PartOfSpeech: MorphologicalPartOfSpeech.PersonalPronoun, Person: MorphologicalPerson.Second, Number: MorphologicalNumber.Plural });

    private static ControllerBinding AddressSubject(ControllerBinding subject, Word address)
    {
        // The literal comma-marked address anchors this pronoun slot. A second lexical finite in the slot
        // cannot form another local predicate without a boundary; its raw reading remains in the inventory.
        bool competing = subject.Head.Readings.Any(row => !Guard.LicensedSubject(subject.Head.CanonicalText, row) &&
            (!TextPredicateLinkAnalyzer.Finite(row) || TextPredicateLinkAnalyzer.CopularCarrier(row)));
        return subject with { Address = address, Competing = competing };
    }

    private static bool ExactDa(Word word) => word.CanonicalText == "да" && Exact(word) &&
        word.Readings.Any(row => row.SourceConjunction) && word.Readings.All(TextPredicateLinkAnalyzer.DaMarker);

    private static bool ExactFiniteBoundary(Word word) => Exact(word) && word.Readings.Any(TextPredicateLinkAnalyzer.Finite) &&
        word.Readings.All(row => TextPredicateLinkAnalyzer.Finite(row) || SourcePlaceholder(word, row));

    private static bool TerminalTail(TextAnalysis analysis, SentenceSpan sentence, Word word)
    {
        var last = word.Token;
        foreach (char c in analysis.Text.AsSpan(last.Start + last.Length, sentence.Start + sentence.Length - last.Start - last.Length).Trim())
            if (!SentenceBoundaryDetector.IsSentenceTerminator(c)) return false;
        return true;
    }

    private static bool Exact(Word word) => word.Complete && !word.UsesGraph && word.Readings.Count > 0;

    private static bool SourcePlaceholder(Word word, ContextLanes.ReadingView row) =>
        row.SourceGenericUnknown && row.Lemma.Trim().ToLowerInvariant() == word.CanonicalText;

    private static bool CommonNoun(ContextLanes.ReadingView row) => row.Category == ContextLanes.Category.Noun &&
        row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun && Guard.HasNominalAxes(row.Features);

    private static bool IsObliqueShortMasculineCarrier(Word word)
    {
        if (!Exact(word)) return false;
        string surface = word.CanonicalText;
        bool sawShortDefinite = false;
        foreach (var row in word.Readings)
        {
            if (SourcePlaceholder(word, row) || Guard.IsUnwrittenCapitalizedReading(word, row)) continue;
            var f = row.Features;
            if (f.HasConflictingFeatures || f.HasUnrecognizedFeatures || f.Gender != MorphologicalGender.Masculine)
                return false;
            if (Guard.IsShortMasculineDefiniteReading(surface, row))
                sawShortDefinite = true;
            else if (f is not { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite })
                return false;
        }
        return sawShortDefinite;
    }

    private static bool OrdinaryAttribute(ContextLanes.ReadingView row) => row.Category == ContextLanes.Category.Adjective &&
        row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(row.Features);

    private static bool ActiveParticiple(ContextLanes.ReadingView row)
    {
        var features = row.Features;
        if (features is not { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle,
            HasConflictingFeatures: false, ParticipleKind: MorphologicalParticipleKind.PastAoristActive or
                MorphologicalParticipleKind.PastImperfectActive }) return false;
        if (TextPredicateLinkAnalyzer.Participle(row)) return true;
        // The Legacy reflexive signature is independent of person, number and gender. Keep the raw row and
        // admit it only when removing that one signature reparses to exactly the same typed evidence.
        return VerifiedLegacyAtom(features, atom => atom is "+се" or "+си");
    }

    private static bool VerifiedLegacyAtom(MorphologicalFeatures features, Func<string, bool> ignored)
    {
        if (features.RawTag is not { } rawTag) return false;
        string[] atoms = rawTag.Split(',', StringSplitOptions.TrimEntries);
        if (atoms.Count(ignored) != 1) return false;
        var parsed = MorphologicalFeatures.FromTag(string.Join(',', atoms.Where(atom => !ignored(atom))));
        return !parsed.HasUnrecognizedFeatures && (parsed with
            { RawTag = features.RawTag, HasUnrecognizedFeatures = true }) == features;
    }

    private static bool Adjectival(ContextLanes.ReadingView row) => OrdinaryAttribute(row) &&
        row.Features.Article == MorphologicalArticle.Indefinite || Guard.IsMeasuredPassiveAttribute(row.Features) &&
        row.Features.Article is null or MorphologicalArticle.Indefinite;

    private static int[] ReflexiveMarkerReadings(Word word)
    {
        if (!Exact(word) || word.CanonicalText is not ("се" or "си")) return [];
        bool Particle(ContextLanes.ReadingView row) => row.Features is
            { PartOfSpeech: MorphologicalPartOfSpeech.Particle, RawTag: "Частица:191", HasConflictingFeatures: false, HasUnrecognizedFeatures: false } &&
            BulgarianTextNormalizer.NormalizeKey(row.Lemma) == word.CanonicalText;
        // Before an independently acquired finite carrier, the short reflexive particle is licensed.
        // The same source spelling retains its short possessive and finite copular homographs.
        bool admitted = word.Readings.All(row => Particle(row) || word.CanonicalText == "си" &&
            (TextPredicateLinkAnalyzer.CopularCarrier(row) || row.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Pronoun, IsShortForm: true, HasConflictingFeatures: false, HasUnrecognizedFeatures: false }));
        return admitted ? Rows(word, Particle) : [];
    }

    private static IEnumerable<Relation> Relations(Frame frame)
    {
        var first = frame.Dependents[0];
        var subject = frame.Controller;
        bool finiteSubjectCompeting = subject is not null && subject.Competing &&
            !(subject.AdverbBridgeOnlyCompeting && frame.Finite.CanonicalText != "си");
        if (subject is not null && frame.FiniteRows.Length > 0)
            yield return new(first.Kind == DependentKind.ActiveParticiple ? NominalGrammarRules.AnalyticFiniteId : NominalGrammarRules.CopularFiniteId,
                ContextLanes.ProductiveGraphMismatch, subject.Head, subject.Rows, frame.Finite, frame.FiniteRows,
                TextGrammarConstraint.PersonNumber, first, first.Competing || finiteSubjectCompeting, UsesController: true);
        foreach (var part in frame.Dependents)
        {
            bool active = part.Kind == DependentKind.ActiveParticiple;
            if (active && frame.FiniteRows.Length > 0)
                yield return new(NominalGrammarRules.AuxiliaryParticipleId, NominalGrammarRules.AuxiliaryParticipleCode,
                    frame.Finite, frame.FiniteRows, part.Word, part.Rows, TextGrammarConstraint.NumberGender, part, part.Competing);
            if (subject is null) continue;
            var rule = part.Kind switch
            {
                DependentKind.ActiveParticiple => NominalGrammarRules.ActiveControllerId,
                DependentKind.PassiveParticiple => NominalGrammarRules.PassiveParticipleId,
                _ => NominalGrammarRules.CopularAdjectiveId
            };
            // A singular vocative can identify one member of a plural addressee. Its grammatical gender
            // can also differ from the person's gender. Measure its axes only as a conditional reading.
            Word? address = active ? null : subject.Address;
            yield return new(rule, NominalGrammarRules.PredicativeCode, address ?? subject.Head,
                address is null ? subject.Rows : Rows(address, _ => true), part.Word, part.Rows, TextGrammarConstraint.NumberGender,
                part, address is not null || part.Competing || subject.Competing, !active && address is null, true);
        }
    }

    internal static IEnumerable<TextDiagnostic> Diagnostics(Frame frame)
    {
        foreach (var relation in Relations(frame))
        {
            if (relation.Competing || Assessment(relation) != TextGrammarAssessment.Mismatch) continue;
            var supports = References(frame.Finite, frame.FiniteRows).Concat(DependentSupports());
            if (relation.UsesController && frame.Controller is { } subject)
            {
                supports = supports.Concat(subject.Phrase?.Participants.SelectMany(member => NominalGrammarRules.References(member)) ??
                    References(subject.Head, subject.Rows));
                if (subject.Markers is { Count: > 0 } markers) supports = supports.Concat(markers);
            }
            if (frame.Bridges is { Count: > 0 } bridges) supports = supports.Concat(bridges);
            yield return new(relation.Code, relation.Target.Token.Start, relation.Target.Token.Length, relation.Rule)
            {
                Readings = Array.AsReadOnly(supports.Distinct().OrderBy(row => row.TokenIndex).ThenBy(row => row.CompleteReadingIndex).ToArray())
            };

            IEnumerable<TextReadingReference> DependentSupports()
            {
                foreach (var part in frame.Dependents)
                {
                    foreach (var row in References(part.Word, part.Rows)) yield return row;
                    if (part == relation.Part) yield break;
                }
            }
        }
    }

    internal static IEnumerable<TextGrammarMatch> Explain(Frame frame, IRuntimeLexicon lexicon,
        Func<string, BulgarianStressLookup.ExactInventory> acquire, CancellationToken ct)
    {
        var relations = Relations(frame).Select(relation => (Relation: relation, Status: Assessment(relation))).ToArray();
        var repairs = relations.Any(row => !row.Relation.Competing && row.Status == TextGrammarAssessment.Mismatch)
            ? Repairs(frame, relations.Select(row => row.Relation).ToArray(), lexicon, acquire, ct) : [];
        foreach (var (relation, status) in relations)
        {
            ct.ThrowIfCancellationRequested();
            bool conditionalAddress = relation.Anchor == frame.Controller?.Address;
            IReadOnlyList<int> participants = relation.UsesController && frame.Controller?.Phrase is { } phrase
                ? Array.AsReadOnly(phrase.Participants.Select(member => member.Word.OriginalIndex).ToArray())
                : relation.UsesController && frame.Controller is { Address: { } address } subject && relation.Anchor == address
                    ? [subject.Head.OriginalIndex, address.OriginalIndex] : [relation.Anchor.OriginalIndex];
            var conflicts = status == TextGrammarAssessment.Mismatch
                ? new[] { new TextGrammarConflict(relation.Target.OriginalIndex, References(relation.Target, relation.TargetRows),
                    References(relation.Anchor, relation.AnchorRows)) } : [];
            bool polite = relation.AllowsPolite && Features(relation.Anchor, relation.AnchorRows)
                .Any(subject => Features(relation.Target, relation.TargetRows).Any(target => PoliteAdjective(subject, target)));
            int start = relation.UsesController ? frame.Start : frame.GroupStart;
            yield return new(relation.Rule, start, frame.End - start, participants, [relation.Target.OriginalIndex],
                relation.Competing ? TextGrammarAssessment.Ambiguous : status, Array.AsReadOnly(conflicts),
                !relation.Competing && status == TextGrammarAssessment.Mismatch ? repairs : [],
                conditionalAddress ? "address_number_gender_unresolved" : polite ? "polite_singular_address_possible" :
                relation.Competing ? relation.Part.Kind == DependentKind.Adjective
                    ? "competing_predicative_role" : "competing_predicate_group" : status == TextGrammarAssessment.Mismatch && repairs.Count == 0
                    ? "no_verified_single_token_repair" : null);
        }
    }

    private static IReadOnlyList<TextGrammarRepair> Repairs(Frame frame, Relation[] relations, IRuntimeLexicon lexicon,
        Func<string, BulgarianStressLookup.ExactInventory> acquire, CancellationToken ct)
    {
        List<TextGrammarRepair> repairs = [];
        if (frame.Controller is { } subject && Features(subject.Head, subject.Rows).All(f => f.PartOfSpeech == MorphologicalPartOfSpeech.Noun))
            Offer(subject.Head, subject.Rows, TextGrammarConstraint.NumberGender);
        if (frame.FiniteRows.Length > 0)
            Offer(frame.Finite, frame.FiniteRows, TextGrammarConstraint.PersonNumber);
        foreach (var part in frame.Dependents)
            Offer(part.Word, part.Rows, TextGrammarConstraint.NumberGender);
        return repairs.AsReadOnly();

        void Offer(Word word, int[] rows, TextGrammarConstraint axes)
        {
            foreach (var form in GrammarRepairForms.Find(word, rows, axes, lexicon, acquire, ct))
            {
                ct.ThrowIfCancellationRequested();
                bool editsHead = word == frame.Controller?.Head;
                bool unboundFinite = frame.FiniteRows.Length > 0 && word == frame.Finite && frame.Controller is null;
                if (editsHead && form.IntroducedFullArticle == false || unboundFinite && form.Readings.Any(replacement =>
                    rows.Any(row => word.Readings[row].Features.Person != replacement.Features.Person))) continue;
                var changed = form.Readings.Select(row => row.Features).ToArray();
                var phrase = frame.Controller?.Phrase;
                if (editsHead && phrase is not null)
                    phrase = GrammarRepairForms.Replace(phrase, phrase.Participants.Count - 1, form);
                if (!FrameAgrees(phrase, relations, word, changed, ct)) continue;
                var genderRelation = relations.FirstOrDefault(relation => relation.UsesController && relation.Target == word &&
                    relation.Constraint == TextGrammarConstraint.NumberGender);
                bool unmeasuredGender = (frame.FiniteRows.Length == 0 || word != frame.Finite) && !editsHead &&
                    changed.Any(f => f.Number == MorphologicalNumber.Singular) &&
                    (genderRelation is null || Features(genderRelation.Anchor, genderRelation.AnchorRows).Any(f => f.Gender is null));
                repairs.Add(NominalGrammarRules.Repair(word, form.Surface) with
                {
                    Qualifications = unboundFinite ? ["clause_relation_unverified"] : unmeasuredGender ? ["controller_gender_unmeasured"] : []
                });
            }
        }
    }

    internal static bool RepairsController(Frame frame, NominalStructuralProjection.Binding phrase, CancellationToken ct) =>
        FrameAgrees(phrase, Relations(frame), frame.Controller!.Head, phrase.Head.Word.Readings.Select(row => row.Features).ToArray(), ct);

    private static bool FrameAgrees(NominalStructuralProjection.Binding? phrase, IEnumerable<Relation> relations, Word word,
        MorphologicalFeatures[] changed, CancellationToken ct) =>
        relations.All(relation =>
            (relation.Competing || Assessment(relation) == TextGrammarAssessment.Ambiguous) &&
                relation.Anchor != word && relation.Target != word ||
            Assessment(relation, word, changed) == TextGrammarAssessment.Compatible) &&
        (phrase is null || NominalGrammarEvidence.ArticlePlaced(phrase) && NominalStructuralProjection.Measure(phrase, ct) is { } measured &&
            NominalGrammarEvidence.FullyAgrees(phrase, measured));

    // БЕРОН §24.1. The singular address alternative applies to adjectival and passive forms, while -л forms use plural.
    private static bool PoliteAdjective(MorphologicalFeatures subject, MorphologicalFeatures target) =>
        subject is { PartOfSpeech: MorphologicalPartOfSpeech.PersonalPronoun, Person: MorphologicalPerson.Second,
            Number: MorphologicalNumber.Plural } && target is { Number: MorphologicalNumber.Singular,
            Gender: MorphologicalGender.Masculine or MorphologicalGender.Feminine };

    private static TextGrammarAssessment Assessment(Relation relation, Word? edited = null, MorphologicalFeatures[]? replacement = null)
    {
        var controller = relation.Anchor == edited ? replacement! : Features(relation.Anchor, relation.AnchorRows);
        var dependent = relation.Target == edited ? replacement! : Features(relation.Target, relation.TargetRows);
        bool any = false, all = true, unmeasured = false;
        foreach (var subject in controller)
        foreach (var target in dependent)
        {
            if (subject.Number is null || target.Number is null || relation.Constraint == TextGrammarConstraint.NumberGender &&
                subject.Number == MorphologicalNumber.Singular && target.Number == MorphologicalNumber.Singular &&
                subject.Gender is not null && target.Gender is null)
            {
                unmeasured = true;
                continue;
            }
            bool agrees = relation.Constraint == TextGrammarConstraint.PersonNumber
                ? Guard.PersonNumberTupleMatches((subject.Person ?? MorphologicalPerson.Third, subject.Number), (target.Person, target.Number))
                : relation.AllowsPolite && PoliteAdjective(subject, target) || Guard.NominalTupleMatches(
                    (subject.Number, subject.Gender ?? target.Gender), (target.Number, target.Gender));
            any |= agrees;
            all &= agrees;
        }
        return unmeasured ? TextGrammarAssessment.Unassessed : all ? TextGrammarAssessment.Compatible :
            any ? TextGrammarAssessment.Ambiguous : TextGrammarAssessment.Mismatch;
    }

    private static int[] Rows(Word word, Func<ContextLanes.ReadingView, bool> predicate) =>
        Enumerable.Range(0, word.Readings.Count).Where(index => predicate(word.Readings[index])).ToArray();

    private static MorphologicalFeatures[] Features(Word word, int[] rows) => rows.Select(index => word.Readings[index].Features).ToArray();

    private static IReadOnlyList<TextReadingReference> References(Word word, int[] rows) =>
        Array.AsReadOnly(rows.Select(index => new TextReadingReference(word.OriginalIndex, index)).ToArray());

    private static void BindBareActiveParticiples(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<Word> words,
        SentenceClauseMap clauseMap,
        int coreStart,
        int coreLast,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        List<Frame> frames,
        CancellationToken ct)
    {
        if (!HasSentenceTerminator(analysis, sentence) ||
            Guard.HasUnexpectedNonPictographSymbol(analysis, words[coreStart].OriginalIndex, words[coreLast].OriginalIndex))
            return;

        foreach (BareParticipleClauseSegment segment in clauseMap.BareParticipleSegments)
        {
            int[] seg = segment.WordIndices;
            bool introByConj = segment.IntroducedByConjunction;
            ct.ThrowIfCancellationRequested();
            if (seg.Length < 2) continue;
            int spanStart = words[seg[0]].Token.Start;
            int spanEnd = words[seg[^1]].Token.Start + words[seg[^1]].Token.Length;
            if (protectedSpans.Any(s => s.Start < spanEnd && s.End > spanStart) ||
                frames.Any(f => f.Start < spanEnd && f.End > spanStart))
                continue;
            if (seg.Any(wi => words[wi].CanonicalText is "ще" or "да" or "би" or "бих" or "бихме" or "бихте" or "биха" or
                    "нека" or "дано" or "де" or "ли" ||
                    words[wi].CanonicalText.StartsWith("колк", StringComparison.Ordinal) ||
                    words[wi].CanonicalText.StartsWith("какв", StringComparison.Ordinal)))
                continue;

            int partPos = -1;
            int[] partRows = [];
            bool multipleParticiples = false;
            for (int p = 1; p < seg.Length; p++)
            {
                Word candidate = words[seg[p]];
                if (!TryGetBareParticipleRows(candidate, seg[p] == 0, p == 1 ? words[seg[0]] : null, out int[] rows))
                    continue;
                bool precededByNominativePronoun = p == 1 &&
                    BulgarianFunctionWords.TryGetPersonalSubjectPronoun(words[seg[0]].CanonicalText, out _);
                if (!precededByNominativePronoun && p + 1 < seg.Length &&
                    HasAgreeingIndefiniteNounNext(candidate, rows, words[seg[p + 1]]))
                    continue;
                if (partPos >= 0) { multipleParticiples = true; break; }
                partPos = p;
                partRows = rows;
            }
            if (multipleParticiples || partPos < 1) continue;

            bool hasFiniteOrAux = false;
            for (int k = 0; k < seg.Length; k++)
            {
                if (k == partPos) continue;
                Word w = words[seg[k]];
                if (!Exact(w)) { hasFiniteOrAux = true; break; }
                if (k == 0 && w.CanonicalText == "вие") continue;
                if (k > 0 && k < partPos && w.CanonicalText is "си" or "ми" &&
                    !(w.CanonicalText == "си" && k + 1 < seg.Length && words[seg[k + 1]].CanonicalText == "се"))
                    continue;
                if (w.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) ||
                    w.Readings.Any(r => r.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative &&
                        Guard.IsPersonalPredicate(r.Features)))
                {
                    hasFiniteOrAux = true;
                    break;
                }
            }
            if (hasFiniteOrAux) continue;

            int subjEndPos = partPos - 1;
            bool hasSe = false;
            Word? accCliticWord = null;
            int[] accCliticRows = [];
            List<TextReadingReference> markerRefs = [];
            bool invalidPreClitic = false;
            while (subjEndPos >= 1 && partPos - subjEndPos <= 2)
            {
                Word mw = words[seg[subjEndPos]];
                string mc = mw.CanonicalText;
                if (!BulgarianFunctionWords.HasAny(mc, FunctionWordClasses.PreVerbalCliticBridge))
                    break;
                int[] mRows = Enumerable.Range(0, mw.Readings.Count)
                    .Where(r => !SourcePlaceholder(mw, mw.Readings[r]))
                    .ToArray();
                if (mRows.Length == 0) { invalidPreClitic = true; break; }
                if (mc == "се") hasSe = true;
                int[] acc = Guard.AccusativeCliticReadings(mw, ct);
                if (acc.Length > 0 && accCliticWord is null) { accCliticWord = mw; accCliticRows = acc; }
                markerRefs.InsertRange(0, References(mw, mRows));
                subjEndPos--;
            }
            if (invalidPreClitic || subjEndPos < 0) continue;

            bool contiguous = true;
            for (int k = 1; k <= subjEndPos; k++)
                if (seg[k] != seg[k - 1] + 1) { contiguous = false; break; }
            if (!contiguous) continue;

            Word partWord = words[seg[partPos]];
            ControllerBinding? controller = TryBindBareSubjectController(
                analysis, sentence, words, seg, coreStart, introByConj, subjEndPos, partPos, partWord, partRows,
                hasSe, accCliticWord, accCliticRows, markerRefs, ct);
            if (controller is null) continue;

            frames.Add(new Frame(
                partWord,
                [],
                [new Dependent(partWord, partRows, DependentKind.ActiveParticiple, Competing: false)],
                Controller: controller));
        }
    }

    private static bool TryGetBareParticipleRows(Word word, bool sentenceInitial, Word? prevWord, out int[] partRows)
    {
        partRows = [];
        if (!Exact(word)) return false;
        bool hasNonPlaceholder = word.Readings.Any(r => !SourcePlaceholder(word, r));
        List<int> active = [];
        bool allActive = true, allActiveOrNoun = true;
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ContextLanes.ReadingView row = word.Readings[i];
            if (hasNonPlaceholder && SourcePlaceholder(word, row) ||
                Guard.IsUnwrittenCapitalizedReading(word, row, sentenceInitial))
                continue;
            bool isPart = ActiveParticiple(row) &&
                row.Features.Article is null or MorphologicalArticle.Indefinite &&
                !TextPredicateLinkAnalyzer.CopularLemma(row.Lemma);
            if (isPart) active.Add(i);
            else
            {
                allActive = false;
                if (!CommonNoun(row)) allActiveOrNoun = false;
            }
        }
        if (active.Count == 0) return false;
        if (!allActive &&
            !(allActiveOrNoun && prevWord is not null &&
              prevWord.CanonicalText != "вие" &&
              BulgarianFunctionWords.TryGetPersonalSubjectPronoun(prevWord.CanonicalText, out _)))
            return false;
        partRows = active.ToArray();
        return true;
    }

    private static bool HasAgreeingIndefiniteNounNext(Word partWord, int[] partRows, Word nextWord)
    {
        if (!Exact(nextWord)) return false;
        return nextWord.Readings.Any(nr => CommonNoun(nr) && nr.Features.Article == MorphologicalArticle.Indefinite &&
            partRows.Any(pr => Guard.NominalTupleMatches(
                (partWord.Readings[pr].Features.Number, partWord.Readings[pr].Features.Gender),
                (nr.Features.Number, nr.Features.Gender))));
    }

    private static ControllerBinding? TryBindBareSubjectController(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<Word> words,
        int[] seg,
        int coreStart,
        bool introducedByConjunction,
        int subjEndPos,
        int partPos,
        Word partWord,
        int[] partRows,
        bool hasSe,
        Word? accCliticWord,
        int[] accCliticRows,
        List<TextReadingReference> markerRefs,
        CancellationToken ct)
    {
        IReadOnlyList<TextReadingReference>? markers = markerRefs.Count > 0 ? markerRefs.AsReadOnly() : null;
        Word firstWord = words[seg[0]];
        Word headWord = words[seg[subjEndPos]];

        // 1. Single nominative personal pronoun (аз, ти, той, тя, то, ние, вие, те).
        if (subjEndPos == 0)
        {
            string pc = firstWord.CanonicalText;
            int[] pronRows = Rows(firstWord, row => Guard.LicensedSubject(pc, row));
            if (pronRows.Length > 0 &&
                firstWord.Readings.All(row => SourcePlaceholder(firstWord, row) || Guard.LicensedSubject(pc, row) ||
                    pc == "те" && (row.Category == ContextLanes.Category.Particle || Guard.IsAccusativeCliticReading(row)) ||
                    pc == "ти" && (row.Category == ContextLanes.Category.Particle || row.Features.IsShortForm == true) ||
                    pc == "вие" && row.Category == ContextLanes.Category.Verb))
            {
                return new ControllerBinding(firstWord, pronRows, Competing: false, Markers: markers);
            }
        }

        // 2. 3rd-person definite NP or unambiguous singular personal ProperNoun.
        NominalStructuralProjection.Binding? phrase = null;
        int[] headRows;
        bool hasFullArticle = false;
        if (subjEndPos == 0)
        {
            if (IsVocative(headWord) || IsShortMasculineCarrier(headWord) || IsTemporalMeasureNoun(headWord))
                return null;
            int[] common = Rows(headWord, CommonNoun);
            int nonTrivialCount = headWord.Readings.Count(row => !SourcePlaceholder(headWord, row) &&
                !Guard.IsUnwrittenCapitalizedReading(headWord, row, seg[0] == 0));
            if (common.Length > 0 && common.Length == nonTrivialCount &&
                common.All(r => headWord.Readings[r].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full))
            {
                headRows = common;
                hasFullArticle = common.All(r => headWord.Readings[r].Features.Article == MorphologicalArticle.Full);
            }
            else if (!IsPersonPlaceHomograph(analysis, headWord.OriginalIndex))
            {
                int[] proper = Rows(headWord, row => row.Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.ProperNoun,
                    NameKind: MorphologicalNameKind.GivenName or MorphologicalNameKind.FamilyName,
                    Number: MorphologicalNumber.Singular,
                    Gender: MorphologicalGender.Masculine or MorphologicalGender.Feminine,
                    HasConflictingFeatures: false,
                    HasUnrecognizedFeatures: false
                });
                if (proper.Length == 0 || proper.Length != nonTrivialCount) return null;
                headRows = proper;
            }
            else return null;
        }
        else
        {
            if (IsShortMasculineCarrier(firstWord) || IsTemporalAdjunctPair(firstWord, headWord) || IsTemporalMeasureNoun(headWord))
                return null;
            phrase = NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, seg[subjEndPos], ct);
            if (phrase is null || phrase.Participants.Count != subjEndPos + 1 ||
                phrase.Participants[0].WordIndex != seg[0] ||
                phrase.Bridges.Any(b => b.Kind != NominalStructuralProjection.BridgeKind.Whitespace))
                return null;
            var firstMember = phrase.Participants[0];
            if (!firstMember.RoleReadings.All(r => firstMember.Word.Readings[r].Features.Article is
                    MorphologicalArticle.Definite or MorphologicalArticle.Full ||
                    DemonstrativeDeterminer(firstMember.Word, firstMember.Word.Readings[r])))
                return null;
            if (NominalStructuralProjection.Measure(phrase, ct) is not { } measured ||
                !NominalGrammarEvidence.FullyAgrees(phrase, measured))
                return null;
            headRows = measured.NounMembers.ToArray();
            if (headRows.Length == 0) return null;
            hasFullArticle = firstMember.RoleReadings.All(r =>
                firstMember.Word.Readings[r].Features.Article == MorphologicalArticle.Full);
        }

        // Guard against clitic-resumed direct objects ("Хорото го гледали.").
        if (accCliticWord is not null && !hasFullArticle &&
            accCliticRows.Any(cr => accCliticWord.Readings[cr].Features is { Person: MorphologicalPerson.Third } cf &&
                headRows.Any(hr => Guard.NominalTupleMatches(
                    (headWord.Readings[hr].Features.Number, headWord.Readings[hr].Features.Gender),
                    (cf.Number, cf.Gender)))))
            return null;

        // Inspect postverbal domain for OVS postverbal subjects vs ungoverned objects.
        bool inGovPp = false;
        bool hasUngovernedObject = false;
        for (int p = partPos + 1; p < seg.Length; p++)
        {
            Word pw = words[seg[p]];
            if (pw.Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition))
            {
                inGovPp = true;
                continue;
            }
            if (inGovPp)
            {
                if (pw.Readings.Any(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun) ||
                    Guard.ObliquePronounReadings(pw, ct).Length > 0)
                    inGovPp = false;
                continue;
            }
            if (pw.Readings.Any(r => r.Features.Article == MorphologicalArticle.Full))
                return null;
            if (!hasFullArticle && !IsShortMasculineCarrier(pw) && PostverbalProperOrDefiniteSubjectMatches(pw, partWord, partRows))
                return null;
            if (pw.Readings.Any(CommonNoun) && !IsTemporalMeasureNoun(pw))
                hasUngovernedObject = true;
        }

        bool strictlyIntransitive = partRows.All(r =>
            partWord.Readings[r].Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive &&
            BulgarianTextNormalizer.NormalizeKey(partWord.Readings[r].Lemma) != "играя");
        bool intransitiveMotionOrState = partRows.All(r =>
            Guard.IsIntransitiveMotionOrStateLemma(BulgarianTextNormalizer.NormalizeKey(partWord.Readings[r].Lemma)));
        bool animateInanimateVerb = partRows.All(r =>
                Guard.IsAnimateSubjectInanimateVerbLemma(BulgarianTextNormalizer.NormalizeKey(partWord.Readings[r].Lemma))) &&
            IsAnimateHead(headWord, headRows);

        // Guard unintroduced post-comma participial appositions ("Избяга дяволът от тези, коранът взели за хаир."):
        // when a transitive participle phrase follows a comma without a conjunction and an antecedent before the comma agrees.
        if (seg[0] > coreStart && !introducedByConjunction && CommaBefore(analysis, sentence, firstWord, out _) &&
            !strictlyIntransitive && !intransitiveMotionOrState)
        {
            for (int k = coreStart; k < seg[0]; k++)
            {
                Word prev = words[k];
                if (prev.Readings.Any(nr =>
                        (CommonNoun(nr) || Guard.TryGetDeterminerAxes(prev, nr, out _) ||
                         nr.Features.PartOfSpeech is MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.ProperNoun) &&
                        partRows.Any(pr => Guard.NominalTupleMatches(
                            (partWord.Readings[pr].Features.Number, partWord.Readings[pr].Features.Gender),
                            (nr.Features.Number, nr.Features.Gender ?? partWord.Readings[pr].Features.Gender)))))
                    return null;
            }
        }

        if (!hasFullArticle && !hasSe && accCliticWord is null &&
            !strictlyIntransitive && !intransitiveMotionOrState && !animateInanimateVerb && !hasUngovernedObject)
            return null;

        return new ControllerBinding(headWord, headRows, Competing: false, Phrase: phrase, Markers: markers);
    }

    private static bool PostverbalProperOrDefiniteSubjectMatches(Word pw, Word partWord, int[] partRows)
    {
        foreach (ContextLanes.ReadingView r in pw.Readings)
        {
            if (SourcePlaceholder(pw, r) || Guard.IsUnwrittenCapitalizedReading(pw, r)) continue;
            MorphologicalFeatures rf = r.Features;
            MorphologicalNumber? num;
            MorphologicalGender? gen;
            if (rf is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Definite })
            {
                num = rf.Number;
                gen = rf.Gender;
            }
            else if (rf.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun && rf.Gender is not null)
            {
                num = rf.Number ?? MorphologicalNumber.Singular;
                if (rf.ParadigmId == "207")
                {
                    string c = pw.CanonicalText;
                    bool unisex = c is "валя" or "ваня" or "вася" or "гриша" or "дима" or "женя" or "миша" or "паша" or
                        "петя" or "саша" or "тоня" or "тони" or "ники" or "боби" or "роси" or "алекс" or "крис" or "дани" ||
                        c.EndsWith('и') && c is not ("георги" or "добри" or "методи" or "евгени" or "валери" or "анатоли" or "димитри" or "захари" or "арсени");
                    MorphologicalGender expected207 = (c.EndsWith('а') || c.EndsWith('я')) &&
                        c is not ("лука" or "тома" or "никола" or "илия" or "сава" or "никита" or "коста" or "мита" or "горица")
                        ? MorphologicalGender.Feminine
                        : MorphologicalGender.Masculine;
                    if (!unisex && rf.Gender != expected207) continue;
                }
                gen = rf.Gender;
            }
            else continue;

            if (partRows.Any(pr => Guard.NominalTupleMatches(
                    (partWord.Readings[pr].Features.Number, partWord.Readings[pr].Features.Gender),
                    (num, gen))))
                return true;
        }
        return false;
    }

    private static bool IsShortMasculineCarrier(Word word) =>
        word.Readings.Any(r => Guard.IsShortMasculineDefiniteReading(word.CanonicalText, r));

    private static bool IsTemporalMeasureNoun(Word word) =>
        word.Readings.Any(r => Guard.IsTemporalOrMeasureLemma(BulgarianTextNormalizer.NormalizeKey(r.Lemma)));

    private static bool IsAnimateHead(Word headWord, int[] headRows) =>
        headRows.Length > 0 && headRows.All(r => Guard.IsAnimateNominalReading(headWord.Readings[r]));

    private static bool IsPersonPlaceHomograph(TextAnalysis analysis, int tokenIndex) =>
        Guard.IsPersonPlaceHomograph(analysis, tokenIndex);

    private static bool HasSentenceTerminator(TextAnalysis analysis, SentenceSpan sentence)
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
            return SentenceBoundaryDetector.IsSentenceTerminator((char)rune.Value);
        }
        return false;
    }
}
