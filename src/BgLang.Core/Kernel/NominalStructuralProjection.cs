using BgLang.Contracts;

namespace BgLang.Core;

// Structural binding only. This neither selects a parse nor licenses a subject/object.
internal static class NominalStructuralProjection
{
    internal enum BridgeKind { Whitespace, Comma, ConjunctionAnd }
    internal enum Partition { None, Unassessed, Supported }
    internal sealed record Member(int WordIndex, ContextLanes.TokenView Word, IReadOnlyList<int> RoleReadings);
    internal sealed record Bridge(BridgeKind Kind, int Start, int Length, int? TokenIndex,
        ContextLanes.TokenView? Conjunction = null)
    {
        internal IReadOnlyList<int> WitnessReadings { get; init; } = [];
    }
    internal sealed record Binding(int Start, int Length, IReadOnlyList<Member> Participants,
        IReadOnlyList<Bridge> Bridges, IReadOnlyList<Member>? AdverbialModifiers = null)
    {
        internal Member Head => Participants[^1];
        internal IReadOnlyList<Member> Specifiers => AdverbialModifiers ?? [];
    }

    internal static IReadOnlyList<ContextLanes.TokenView> SentenceWords(TextAnalysis analysis, SentenceSpan sentence,
        MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        List<ContextLanes.TokenView> words = [];
        var ordered = morphology.OrderedTokens;
        int first = 0, last = ordered.Count;
        while (first < last)
        {
            ct.ThrowIfCancellationRequested();
            int middle = first + (last - first) / 2;
            if (analysis.Tokens[ordered[middle].Index].Start < sentence.Start) first = middle + 1;
            else last = middle;
        }
        long end = (long)sentence.Start + sentence.Length;
        for (int i = first; i < ordered.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = ordered[i];
            TokenAnalysis token = analysis.Tokens[entry.Index];
            if (token.Start >= end) break;
            if ((long)token.Start + token.Length > end) continue;
            var rows = entry.Rows;
            words.Add(new(entry.Index, token, rows.Canonical, rows.Lanes, rows.GraphUnavailableReason is null) { UsesGraph = rows.UsesGraph });
        }
        return words;
    }

    // One requested head: linear in the bound local core and supplied reading rows.
    // No all-head/all-boundary search, agreement test, article filter, or reading Cartesian product.
    internal static Binding? BindHead(string text, SentenceSpan sentence, IReadOnlyList<TokenAnalysis> sourceTokens,
        IReadOnlyList<ContextLanes.TokenView> words, int headWordIndex, CancellationToken ct, bool exactContiguous = false,
        bool allowDegreeGraph = false, bool allowPossessiveCliticBridge = false, bool allowArticledDeterminer = false)
    {
        ct.ThrowIfCancellationRequested();
        if (sentence.Start < 0 || sentence.Length < 0 || (long)sentence.Start + sentence.Length > text.Length)
            throw new ArgumentException("Nominal sentence is outside original UTF16 source.");
        if ((uint)headWordIndex >= (uint)words.Count) return null;
        Member? head = MemberAt(headWordIndex, ContextLanes.Category.Noun, MorphologicalPartOfSpeech.Noun);
        if (head is null) return null;
        List<Member> reversed = [head];
        List<Bridge> bridges = [];
        List<Member> adverbials = [];
        int current = headWordIndex;
        while (current > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (reversed.Count > 1 && Whitespace(words[current - 1], words[current]) &&
                AdverbialSpecifierAt(current - 1) is { } advSpec)
            {
                adverbials.Add(advSpec);
                current--;
                continue;
            }
            Member? previous = MemberAt(current - 1, ContextLanes.Category.Adjective, MorphologicalPartOfSpeech.Adjective);
            Bridge? bridge = previous is null ? null : LocalBridge(previous.Word, words[current], reversed.Count > 1);
            if (previous is not null && bridge is not null &&
                !((previous.RoleReadings.Any(ri => ContextEvidenceGuards.IsPresentActiveAttributeShape(previous.Word.Readings[ri].Features) &&
                       previous.Word.Readings[ri].Features.Article is null or MorphologicalArticle.Indefinite) ||
                   previous.Word.Readings.Any(r => ContextEvidenceGuards.IsPresentActiveAttributeShape(r.Features) &&
                       r.Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive) ||
                   previous.Word.UsesGraph && previous.RoleReadings.All(ri =>
                       previous.Word.Readings[ri].Features.Article is null or MorphologicalArticle.Indefinite)) &&
                  reversed.Any(m => m.RoleReadings.Any(ri => m.Word.Readings[ri].Features.Article is
                      MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))))
            {
                if (current < reversed[^1].WordIndex)
                    bridge = bridge with { Length = reversed[^1].Word.Token.Start - bridge.Start };
                reversed.Add(previous); bridges.Add(bridge); current--; continue;
            }
            // и only joins two actual possible modifiers, never modifier-to-head or noun-to-noun.
            if (!exactContiguous && reversed.Count > 1 && current > 1 && Conjunction(current - 1) is { } witnesses)
            {
                previous = MemberAt(current - 2, ContextLanes.Category.Adjective, MorphologicalPartOfSpeech.Adjective);
                ContextLanes.TokenView conjunction = words[current - 1];
                if (previous is not null && Whitespace(previous.Word, conjunction) && Whitespace(conjunction, words[current]))
                {
                    int from = previous.Word.Token.Start + previous.Word.Token.Length;
                    bridges.Add(new(BridgeKind.ConjunctionAnd, from, reversed[^1].Word.Token.Start - from,
                        conjunction.OriginalIndex, conjunction) { WitnessReadings = witnesses });
                    reversed.Add(previous); current -= 2; continue;
                }
            }
            if (allowPossessiveCliticBridge && current == reversed[^1].WordIndex && current >= 2 &&
                ContextEvidenceGuards.IsPossessiveBridgeClitic(words[current - 1].CanonicalText) &&
                words[current - 1].Complete && !words[current - 1].UsesGraph && words[current - 1].Readings.Count > 0 &&
                Whitespace(words[current - 1], words[current]) &&
                reversed.All(m => m.RoleReadings.Count > 0 && m.RoleReadings.All(ri =>
                    m.Word.Readings[ri].Features.Article == MorphologicalArticle.Indefinite ||
                    m.Word.Readings[ri].Features.Article is null &&
                        (ContextEvidenceGuards.IsMeasuredPassiveAttribute(m.Word.Readings[ri].Features) ||
                         ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(m.Word.Readings[ri].Features)) ||
                    ContextEvidenceGuards.TryGetOrdinalAxes(m.Word.Readings[ri], out var ord) && ord.Article == MorphologicalArticle.Indefinite)) &&
                !head.RoleReadings.Any(ri => IsVocativeMemberReading(head.Word, head.Word.Readings[ri])) &&
                !(words[current - 1].CanonicalText == "ти" && head.RoleReadings.Any(ri =>
                    ContextEvidenceGuards.IsTemporalOrMeasureLemma(BulgarianTextNormalizer.NormalizeKey(head.Word.Readings[ri].Lemma)))))
            {
                Member? cliticMod = MemberAt(current - 2, ContextLanes.Category.Adjective, MorphologicalPartOfSpeech.Adjective);
                if (cliticMod is not null && Whitespace(cliticMod.Word, words[current - 1]) &&
                    (!head.Word.Readings.Any(r => r.Category == ContextLanes.Category.Verb) ||
                     cliticMod.RoleReadings.All(ri => cliticMod.Word.Readings[ri].Features.Number is { } number &&
                         ContextEvidenceGuards.FiniteReadingsConflictWithNumber(head.Word, number)) &&
                     cliticMod.RoleReadings.Any(ri => head.RoleReadings.Any(hi => ContextEvidenceGuards.ModifierAgreement(
                         cliticMod.Word, cliticMod.Word.Readings[ri], head.Word.Readings[hi], includeAdnominalReadings: true)))) &&
                    cliticMod.RoleReadings.Count > 0 && cliticMod.RoleReadings.All(ri =>
                        cliticMod.Word.Readings[ri].Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
                        ContextEvidenceGuards.TryGetOrdinalAxes(cliticMod.Word.Readings[ri], out var ord) &&
                            ord.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
                {
                    int from = cliticMod.Word.Token.Start + cliticMod.Word.Token.Length;
                    bridges.Add(new(BridgeKind.Whitespace, from, words[current].Token.Start - from, words[current - 1].OriginalIndex));
                    reversed.Add(cliticMod); current -= 2; break;
                }
            }
            break;
        }
        if (current > 0 && DeterminerAt(current - 1) is { } determiner && Whitespace(determiner.Word, words[current]))
        {
            int from = determiner.Word.Token.Start + determiner.Word.Token.Length;
            bridges.Add(new(BridgeKind.Whitespace, from, reversed[^1].Word.Token.Start - from, null));
            reversed.Add(determiner);
        }
        reversed.Reverse(); bridges.Reverse();
        adverbials.Reverse();
        int start = reversed[0].Word.Token.Start;
        return new(start, head.Word.Token.Start + head.Word.Token.Length - start,
            reversed.AsReadOnly(), bridges.AsReadOnly(),
            adverbials.Count > 0 ? adverbials.AsReadOnly() : null);

        Member? AdverbialSpecifierAt(int index)
        {
            ContextLanes.TokenView word = words[index];
            RequireOriginal(word);
            bool precededByDefAdj = index >= 1 && Whitespace(words[index - 1], word) &&
                HasArticledModifierReading(words[index - 1]);
            if (!ContextEvidenceGuards.IsAdverbialSpecifierCandidate(word, index == 0, precededByDefAdj))
                return null;
            bool hasAdverbReading = word.Readings.Any(r =>
                (r.SourceAdverb || r.Category == ContextLanes.Category.Other) &&
                r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb);
            if (!hasAdverbReading && head.RoleReadings.Any(hri => word.Readings.Any(sr =>
                    ContextEvidenceGuards.ModifierAgreement(word, sr, head.Word.Readings[hri], includeAdnominalReadings: false))))
                return null;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            List<int> rows = [];
            for (int row = 0; row < word.Readings.Count; row++)
            {
                ct.ThrowIfCancellationRequested();
                var r = word.Readings[row];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, r) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, r, index == 0))
                    continue;
                rows.Add(row);
            }
            return rows.Count > 0 ? new(index, word, rows.AsReadOnly()) : null;
        }
        static bool HasArticledModifierReading(ContextLanes.TokenView w) =>
            w.Complete && !w.UsesGraph && w.Readings.Any(r =>
                (r.Category == ContextLanes.Category.Adjective &&
                 r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                 ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) ||
                 ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(r.Features)) &&
                r.Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short);

        Member? DeterminerAt(int index)
        {
            ContextLanes.TokenView word = words[index];
            RequireOriginal(word);
            if (!word.Complete || word.UsesGraph || word.Readings.Count == 0 ||
                index > 0 && Whitespace(words[index - 1], word) && words[index - 1].Readings.Any(r =>
                    r.Category is ContextLanes.Category.Adjective or ContextLanes.Category.Number ||
                    r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral))
                return null;
            if (!allowArticledDeterminer && !ContextEvidenceGuards.DeterminerAllowsArticledHead(word) &&
                reversed.Any(m => m.RoleReadings.Any(ri => m.Word.Readings[ri].Features.Article != MorphologicalArticle.Indefinite &&
                    !(m.Word.Readings[ri].Features.Article is null &&
                      (ContextEvidenceGuards.IsMeasuredPassiveAttribute(m.Word.Readings[ri].Features) ||
                       ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(m.Word.Readings[ri].Features))) &&
                    !(ContextEvidenceGuards.TryGetOrdinalAxes(m.Word.Readings[ri], out var ord) &&
                      ord.Article == MorphologicalArticle.Indefinite))))
                return null;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            List<int> possible = [];
            for (int row = 0; row < word.Readings.Count; row++)
            {
                ct.ThrowIfCancellationRequested();
                var reading = word.Readings[row];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, reading) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, reading, index == 0) ||
                    ContextEvidenceGuards.IsSuppressedDeterminerHomograph(word, reading))
                    continue;
                bool determiner = ContextEvidenceGuards.TryGetDeterminerAxes(word, reading, out _);
                if (exactContiguous && !determiner) return null;
                if (determiner) possible.Add(row);
            }
            return possible.Count > 0 ? new(index, word, possible.AsReadOnly()) : null;
        }
        Member? MemberAt(int index, ContextLanes.Category category, MorphologicalPartOfSpeech pos)
        {
            ContextLanes.TokenView word = words[index];
            RequireOriginal(word);
            bool degreeGraph = allowDegreeGraph && category == ContextLanes.Category.Adjective &&
                SentenceClauseMap.IsDegreeGraphModifier(word);
            if (exactContiguous && (!word.Complete || word.UsesGraph && !degreeGraph || word.Readings.Count == 0)) return null;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            bool skipPositionalVerbHomograph = exactContiguous && CanPrunePositionalVerbHomograph(index, category);
            List<int> possible = [];
            for (int row = 0; row < word.Readings.Count; row++)
            {
                ct.ThrowIfCancellationRequested();
                var reading = word.Readings[row];
                if (hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, reading) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, reading, index == 0) ||
                    ContextEvidenceGuards.IsSuppressedPresentActiveNounHomograph(word, reading) ||
                    ContextEvidenceGuards.IsSuppressedSubstantivizedAdjectiveNounHomograph(word, reading, category) ||
                    ContextEvidenceGuards.IsSyncreticVocativeCompanion(word, reading))
                    continue;
                if (skipPositionalVerbHomograph && reading.Category == ContextLanes.Category.Verb &&
                    reading.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative)
                    continue;
                bool ordinary = reading.Category == category && reading.Features.PartOfSpeech == pos;
                bool passive = category == ContextLanes.Category.Adjective && reading.Category == ContextLanes.Category.Verb &&
                    ContextEvidenceGuards.IsPassiveAttributeShape(reading.Features);
                bool presentActive = category == ContextLanes.Category.Adjective && reading.Category == ContextLanes.Category.Verb &&
                    ContextEvidenceGuards.IsPresentActiveAttributeShape(reading.Features);
                bool ordinal = category == ContextLanes.Category.Adjective &&
                    ContextEvidenceGuards.TryGetOrdinalAxes(reading, out _);
                // Competing or unmeasured words end the independent exact local suffix.
                if (exactContiguous && !(ordinary && ContextEvidenceGuards.HasNominalAxes(reading.Features) ||
                    passive && ContextEvidenceGuards.IsMeasuredPassiveAttribute(reading.Features) ||
                    presentActive && ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(reading.Features) ||
                    ordinal)) return null;
                if (ordinary || passive || presentActive || ordinal) possible.Add(row);
            }
            // Incomplete/conflicting/unmeasured evidence is retained if its typed role is possible.
            if (possible.Count > 0) return new(index, word, possible.AsReadOnly());
            // Unlicensed raw graph shape can describe topology, never a support reading.
            return !word.Complete && word.Readings.Count == 0 && PotentialGraph(word, category, pos)
                ? new(index, word, Array.Empty<int>()) : null;
        }
        bool CanPrunePositionalVerbHomograph(int index, ContextLanes.Category category)
        {
            ContextLanes.TokenView word = words[index];
            if (!word.Complete || word.UsesGraph || word.Readings.Count == 0) return false;
            if (category == ContextLanes.Category.Noun && index == headWordIndex)
            {
                bool hasCleanNoun = word.Readings.Any(r =>
                    r.Category == ContextLanes.Category.Noun &&
                    r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                    ContextEvidenceGuards.HasNominalAxes(r.Features) &&
                    !ContextEvidenceGuards.IsSyncreticVocativeCompanion(word, r));
                if (!hasCleanNoun) return false;
                if (index + 1 < words.Count && Whitespace(word, words[index + 1]) && IsUnambiguousFiniteVerbWord(words[index + 1]))
                    return true;
                int cur = index;
                while (cur > 0 && Whitespace(words[cur - 1], words[cur]) && HasAdnominalModifierReading(words[cur - 1]))
                    cur--;
                return cur < index && cur > 0 && Whitespace(words[cur - 1], words[cur]) &&
                    !(cur == index - 1 && words[cur].CanonicalText is "това" or "онова") &&
                    ContextEvidenceGuards.IsExactAdposition(words[cur - 1]);
            }
            if (category == ContextLanes.Category.Adjective && index < headWordIndex)
            {
                if (!HasAdnominalModifierReading(word)) return false;
                int cur = index;
                while (cur > 0 && Whitespace(words[cur - 1], words[cur]))
                {
                    if (ContextEvidenceGuards.IsExactAdposition(words[cur - 1]))
                        return true;
                    if (!HasAdnominalModifierReading(words[cur - 1]))
                        break;
                    cur--;
                }
            }
            return false;
        }
        bool HasAdnominalModifierReading(ContextLanes.TokenView word) =>
            word.Complete && !word.UsesGraph && word.Readings.Any(r =>
                r.Category == ContextLanes.Category.Adjective && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
                    ContextEvidenceGuards.HasNominalAxes(r.Features) ||
                ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) ||
                ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(r.Features) ||
                ContextEvidenceGuards.TryGetOrdinalAxes(r, out _) ||
                ContextEvidenceGuards.TryGetDeterminerAxes(word, r, out _));
        bool IsUnambiguousFiniteVerbWord(ContextLanes.TokenView word)
        {
            if (!word.Complete || word.UsesGraph || word.Readings.Count == 0) return false;
            bool hasReal = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            int count = 0;
            foreach (var r in word.Readings)
            {
                if (hasReal && ContextEvidenceGuards.IsSourcePlaceholder(word, r) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, r, sentenceInitial: false))
                    continue;
                if (r.Category != ContextLanes.Category.Verb ||
                    r.Features.VerbForm != MorphologicalVerbForm.Finite ||
                    !ContextEvidenceGuards.IsPersonalPredicate(r.Features))
                    return false;
                count++;
            }
            return count > 0;
        }
        bool PotentialGraph(ContextLanes.TokenView word, ContextLanes.Category category, MorphologicalPartOfSpeech pos)
        {
            if (word.Token.Analysis is not { } analysis || analysis.GraphCandidates.Count == 0) return false;
            string normalized = BulgarianTextNormalizer.NormalizeKey(word.Token.Text);
            if (analysis.NormalizedWord != normalized || BulgarianTextNormalizer.NormalizeKey(analysis.Word) != normalized)
                throw new ArgumentException("Potential graph word is not the original occurrence.");
            bool found = false;
            foreach (WordDerivation path in analysis.GraphCandidates)
            {
                ct.ThrowIfCancellationRequested();
                MorphologyEvidenceProjection.RequirePathWord(analysis, path);
                foreach (string tag in path.FullTags ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    MorphologicalFeatures feature = MorphologicalFeatures.FromTag(tag);
                    found |= feature.PartOfSpeech == pos || category == ContextLanes.Category.Adjective &&
                        (ContextEvidenceGuards.IsPassiveAttributeShape(feature) ||
                         ContextEvidenceGuards.IsPresentActiveAttributeShape(feature));
                }
            }
            return found;
        }
        IReadOnlyList<int>? Conjunction(int index)
        {
            ContextLanes.TokenView word = words[index];
            RequireOriginal(word);
            if (!word.Complete || word.UsesGraph || word.Readings.Count == 0 ||
                BulgarianTextNormalizer.NormalizeKey(word.Token.Text) != "и") return null;
            List<int> witnesses = [];
            for (int rowIndex = 0; rowIndex < word.Readings.Count; rowIndex++)
            {
                ct.ThrowIfCancellationRequested();
                ContextLanes.ReadingView row = word.Readings[rowIndex];
                if (row.SourceConjunction && row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction &&
                    !row.Features.HasConflictingFeatures && !row.Features.HasUnrecognizedFeatures &&
                    row.Features.RawTag == "Съюз:189" && BulgarianTextNormalizer.NormalizeKey(row.Lemma) == "и")
                    witnesses.Add(rowIndex);
            }
            return witnesses.Count == 0 ? null : witnesses.AsReadOnly();
        }
        Bridge? LocalBridge(ContextLanes.TokenView left, ContextLanes.TokenView right, bool betweenModifiers)
        {
            if (Whitespace(left, right))
                return new(BridgeKind.Whitespace, left.Token.Start + left.Token.Length,
                    right.Token.Start - left.Token.Start - left.Token.Length, null);
            if (exactContiguous || !betweenModifiers || right.OriginalIndex != left.OriginalIndex + 2) return null;
            int index = left.OriginalIndex + 1;
            TokenAnalysis comma = sourceTokens[index];
            int from = left.Token.Start + left.Token.Length;
            if (comma.Kind != TokenKind.Punctuation || comma.Text != "," || comma.Length != 1 ||
                comma.Start < from || comma.Start + comma.Length > right.Token.Start || text[comma.Start] != ',' ||
                !Space(from, comma.Start, false) || !Space(comma.Start + comma.Length, right.Token.Start, false)) return null;
            return new(BridgeKind.Comma, from, right.Token.Start - from, index);
        }
        bool Whitespace(ContextLanes.TokenView left, ContextLanes.TokenView right) =>
            right.OriginalIndex == left.OriginalIndex + 1 &&
            Space(left.Token.Start + left.Token.Length, right.Token.Start, true);
        bool Space(int from, int to, bool required)
        {
            if (from > to || required && from == to) return false;
            for (int at = from; at < to; at++)
            {
                ct.ThrowIfCancellationRequested();
                if (!char.IsWhiteSpace(text[at])) return false;
            }
            return true;
        }
        void RequireOriginal(ContextLanes.TokenView word)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = word.Token;
            if ((uint)word.OriginalIndex >= (uint)sourceTokens.Count ||
                !ReferenceEquals(token, sourceTokens[word.OriginalIndex]) || token.Kind != TokenKind.Word || token.Length <= 0 ||
                token.Start < sentence.Start || (long)token.Start + token.Length > (long)sentence.Start + sentence.Length ||
                !text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text))
                throw new ArgumentException("Nominal member is not the original Word occurrence.");
        }
    }

    private static bool IsVocativeMemberReading(ContextLanes.TokenView word, ContextLanes.ReadingView r) =>
        !ContextEvidenceGuards.IsSyncreticVocativeCompanion(word, r) &&
        (r.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) == true ||
         r.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) == true);

    private static bool NominalModifier(MorphologicalFeatures f) =>
        f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && ContextEvidenceGuards.HasNominalAxes(f) ||
        ContextEvidenceGuards.IsMeasuredPassiveAttribute(f) ||
        ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(f);

    internal static bool IsSubstantiveModifier(MorphologicalFeatures f) => NominalModifier(f) &&
        f.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short;

    private static bool TryGetSubstantiveFeatures(ContextLanes.TokenView word, ContextLanes.ReadingView row,
        out MorphologicalFeatures features)
    {
        if (IsSubstantiveModifier(row.Features))
        {
            features = row.Features;
            return true;
        }
        if (ContextEvidenceGuards.TryGetOrdinalAxes(row, out var ord) &&
            ord.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short)
        {
            features = new MorphologicalFeatures
            {
                PartOfSpeech = MorphologicalPartOfSpeech.Adjective,
                Gender = ord.Gender,
                Number = ord.Number,
                Article = ord.Article
            };
            return true;
        }
        if (!ContextEvidenceGuards.DeterminerAllowsArticledHead(word) &&
            ContextEvidenceGuards.TryGetDeterminerAxes(word, row, out var det) &&
            BulgarianTextNormalizer.NormalizeKey(row.Lemma) is "този" or "онзи")
        {
            features = new MorphologicalFeatures
            {
                PartOfSpeech = MorphologicalPartOfSpeech.Pronoun,
                Gender = det.Gender,
                Number = det.Number,
                Article = row.Features.Article ?? MorphologicalArticle.Definite
            };
            return true;
        }
        features = default!;
        return false;
    }

    // A definite adjective can head a separate nominal expression. Preserve a
    // compatible clause partition instead of treating the adjacency as one phrase.
    internal static Partition AssessPartition(string text, IReadOnlyList<ContextLanes.TokenView> words,
        int first, int head, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (first >= head || !Exact(words[first]) || !Exact(words[head])) return Partition.None;
        MorphologicalFeatures[] nouns = words[head].Readings.Select(row => row.Features).Where(f =>
            f.PartOfSpeech == MorphologicalPartOfSpeech.Noun && ContextEvidenceGuards.HasNominalAxes(f)).ToArray();
        if (nouns.Length == 0) return Partition.None;
        for (int member = first; member < head; member++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Exact(words[member]) || !Adjacent(member, member + 1)) return Partition.None;
        }

        // 1. Distributive plural modifier/determiner over coordinated indefinite NPs ("Моите брат и сестра...", "изписани решителност и желязна воля").
        if (head + 2 < words.Count && Adjacent(head, head + 1) && Adjacent(head + 1, head + 2) &&
            words[head + 1].CanonicalText == "и" && words[head + 1].Readings.Any(r => r.SourceConjunction) &&
            nouns.Any(n => n is { Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }) &&
            words[first].Readings.Any(r =>
                r.Features.Number == MorphologicalNumber.Plural && NominalModifier(r.Features) ||
                ContextEvidenceGuards.TryGetOrdinalAxes(r, out var ord) && ord.Number == MorphologicalNumber.Plural ||
                ContextEvidenceGuards.TryGetDeterminerAxes(words[first], r, out var d) && d.Number == MorphologicalNumber.Plural))
        {
            int c2 = head + 2;
            while (c2 + 1 < words.Count && Adjacent(c2, c2 + 1) && Exact(words[c2]) &&
                words[c2].Readings.All(r => ContextEvidenceGuards.IsSourcePlaceholder(words[c2], r) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(words[c2], r) ||
                    r.Features.Article is null or MorphologicalArticle.Indefinite &&
                    (NominalModifier(r.Features) && r.Features.PartOfSpeech != MorphologicalPartOfSpeech.Noun ||
                     ContextEvidenceGuards.TryGetOrdinalAxes(r, out _) ||
                     ContextEvidenceGuards.TryGetDeterminerAxes(words[c2], r, out _))))
            {
                c2++;
            }
            if (Exact(words[c2]) && words[c2].Readings.Any(r =>
                    r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Article: MorphologicalArticle.Indefinite } &&
                    ContextEvidenceGuards.HasNominalAxes(r.Features)))
                return Partition.Supported;
        }

        // 2. Adverbialized parenthetical idiom "като цяло" before non-neuter/plural noun ("Като цяло въпроси не възникнаха.").
        if (first == head - 1 && first >= 1 && Adjacent(first - 1, first) &&
            words[first - 1].CanonicalText == "като" && words[first].CanonicalText == "цяло" &&
            !nouns.Any(n => n is { Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular }))
            return Partition.Supported;

        // 2B. Standalone reciprocal PP ("един/една/едно/... + Prep + друг/друга/друго/други" before NP).
        if (first >= 2 && Adjacent(first - 2, first - 1) && Adjacent(first - 1, first) &&
            Exact(words[first - 2]) &&
            words[first - 2].CanonicalText is "един" or "една" or "едно" or "единия" or "единият" or "едната" or "едното" or "едни" or "едните" &&
            ContextEvidenceGuards.IsExactAdposition(words[first - 1]) &&
            words[first].CanonicalText is "друг" or "друга" or "друго" or "други")
            return Partition.Supported;

        // 2C. Standalone demonstrative PP ("Prep + това/онова" before non-Neuter.Sg NP).
        if (first >= 1 && Adjacent(first - 1, first) &&
            words[first].CanonicalText is "това" or "онова" &&
            ContextEvidenceGuards.IsExactAdposition(words[first - 1]) &&
            !nouns.Any(n => n is { Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular }))
            return Partition.Supported;

        // 2D. Interrogative-copular demonstrative ("какво/кой/... + Copula + това/онова" before non-Neuter.Sg NP).
        if (first >= 2 && Adjacent(first - 2, first - 1) && Adjacent(first - 1, first) &&
            words[first].CanonicalText is "това" or "онова" &&
            Exact(words[first - 1]) && words[first - 1].Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier) &&
            Exact(words[first - 2]) && words[first - 2].CanonicalText is "какво" or "кой" or "коя" or "кое" or "кои" or "що" &&
            !nouns.Any(n => n is { Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular }))
            return Partition.Supported;

        // 2E. Fixed coordinated determiner idiom "един и същи" before Masculine Singular noun (preserves "на същи музей").
        if (first >= 2 && Adjacent(first - 2, first - 1) && Adjacent(first - 1, first) &&
            words[first - 2].CanonicalText == "един" &&
            words[first - 1].CanonicalText == "и" && words[first - 1].Readings.Any(r => r.SourceConjunction) &&
            words[first].CanonicalText == "същи" &&
            nouns.Any(n => n is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular }))
            return Partition.Supported;

        // 2F. Postverbal bare -о manner adverb immediately before strictly articled NP ("обвърже правно Европейския съюз").
        if (first >= 1 && Adjacent(first - 1, first) &&
            words[first].CanonicalText.EndsWith('о') &&
            ContextEvidenceGuards.IsAdverbialSpecifierCandidate(words[first], sentenceInitial: false, precededByDefiniteAdjective: false) &&
            IsPersonalNonCopularFiniteVerbWord(words[first - 1]) &&
            IsStrictlyArticledNominalWord(words[first + 1]))
            return Partition.Supported;

        // 2G. Impersonal 3sg copular predicate before overt definite subject of да-clause ("е необходимо заявителите да ...").
        if (first >= 1 && head == first + 1 && head + 1 < words.Count &&
            Adjacent(first - 1, first) && Adjacent(head, head + 1) &&
            words[head + 1].CanonicalText == "да" &&
            Exact(words[first - 1]) &&
            words[first - 1].Readings.Any(r => TextPredicateLinkAnalyzer.CopularCarrier(r) &&
                r.Features is { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Singular }) &&
            words[first].CanonicalText.EndsWith('о') &&
            ContextEvidenceGuards.IsAdverbialSpecifierCandidate(words[first], sentenceInitial: false, precededByDefiniteAdjective: false) &&
            nouns.All(n => n.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
            return Partition.Supported;

        // 3. Substantivized neuter PP complement tucked inside "[ArticledModOrParticiple] [Prep] [NeutSgAdj] [HeadNoun]" ("пред подгизналата в червено стръмнина").
        if (first == head - 1 && first >= 2 && Adjacent(first - 2, first - 1) && Adjacent(first - 1, first) &&
            Exact(words[first - 1]) && words[first - 1].Readings.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition) &&
            Exact(words[first - 2]) &&
            words[first].Readings.Any(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }) &&
            !nouns.Any(n => n is { Gender: MorphologicalGender.Neuter, Number: MorphologicalNumber.Singular }) &&
            words[first - 2].Readings.Any(r =>
                r.Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short &&
                (NominalModifier(r.Features) || r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle }) &&
                nouns.Any(n => ContextEvidenceGuards.NominalTupleMatches((r.Features.Number, r.Features.Gender), (n.Number, n.Gender)))))
            return Partition.Supported;

        // 4. Compound loan classifier noun before agreeing outer head noun ("на една секс игра с нож").
        if (head + 1 < words.Count && Adjacent(head, head + 1) && Exact(words[head + 1]) &&
            ContextEvidenceGuards.IsCompoundClassifierLoanNoun(words[head].CanonicalText) &&
            nouns.All(n => n is
                { Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite,
                  Gender: MorphologicalGender.Masculine or MorphologicalGender.Neuter }))
        {
            ContextLanes.TokenView outer = words[head + 1];
            bool outerAllNoun = outer.Readings.All(r => r.Category == ContextLanes.Category.Noun ||
                ContextEvidenceGuards.IsSourcePlaceholder(outer, r) ||
                ContextEvidenceGuards.IsUnwrittenCapitalizedReading(outer, r));
            bool prepFramed = first >= 1 && Adjacent(first - 1, first) && ContextEvidenceGuards.IsExactAdposition(words[first - 1]) &&
                (head + 2 == words.Count || !Adjacent(head + 1, head + 2) || ContextEvidenceGuards.IsExactAdposition(words[head + 2]));
            if (outerAllNoun || prepFramed)
            {
                ContextLanes.ReadingView[] outerNounRows = outer.Readings.Where(r =>
                    !ContextEvidenceGuards.IsSourcePlaceholder(outer, r) &&
                    !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(outer, r) &&
                    !ContextEvidenceGuards.IsSyncreticVocativeCompanion(outer, r) &&
                    r.Category == ContextLanes.Category.Noun &&
                    r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                    ContextEvidenceGuards.HasNominalAxes(r.Features)).ToArray();
                if (outerNounRows.Length > 0)
                {
                    ContextLanes.TokenView outerView = outer with { Readings = Array.AsReadOnly(outerNounRows) };
                    ContextLanes.TokenView[] outerWords = [.. words.Skip(first).Take(head - first), outerView];
                    if (ContextLanes.NominalMembers(outerWords, outerWords.Length - 1, ct,
                            allowAdnominalReadings: true, sentenceInitialFirst: first == 0) is { } outerPhrase &&
                        outerPhrase.Heads.Any(h => h.Attributes.All(rows => rows.Length > 0)))
                        return Partition.Supported;
                }
            }
        }

        if (!words[first].Readings.Any(row => TryGetSubstantiveFeatures(words[first], row, out _))) return Partition.None;

        bool afterPreposition = first > 0 && Adjacent(first - 1, first) && ContextEvidenceGuards.IsExactAdposition(words[first - 1]);
        List<(ContextLanes.ReadingView Reading, MorphologicalFeatures[] Objects, bool ClearTail, bool OccupiedObjectTail, bool VerbPrecedes, bool OccupiedSubjectLeft, bool ReflexiveSe)> carriers = [];
        int leftBoundary = afterPreposition ? first - 1 : first;
        AddCarriers(leftBoundary, -1);
        AddCarriers(head, 1);
        if (carriers.Count == 0) return Partition.None;

        Partition result = Partition.None;
        foreach (var reading in words[first].Readings)
        {
            ct.ThrowIfCancellationRequested();
            if (!TryGetSubstantiveFeatures(words[first], reading, out var f)) continue;
            int leftEnd = first + 1;
            while (leftEnd < head && Continues(leftEnd, f))
            {
                ct.ThrowIfCancellationRequested();
                leftEnd++;
            }

            foreach (var noun in nouns)
            {
                ct.ThrowIfCancellationRequested();
                if (leftEnd == head && Observe(AssessRoles(f, noun))) return Partition.Supported;
                if (noun.Article != MorphologicalArticle.Indefinite) continue;
                // Scan the right suffix once for this actual head reading. The
                // first member carries any article, and all subsequent members stay bare.
                for (int split = head - 1; split > first; split--)
                {
                    ct.ThrowIfCancellationRequested();
                    if (split <= leftEnd)
                        foreach (var row in words[split].Readings)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (ModifierRowMatches(row, noun, out var splitFeatures) && Observe(AssessRoles(f, splitFeatures)))
                                return Partition.Supported;
                        }
                    if (!Continues(split, noun)) break;
                }
            }
        }
        return result;

        bool Observe(Partition candidate)
        {
            if (candidate == Partition.Unassessed) result = candidate;
            return candidate == Partition.Supported;
        }

        void AddCarriers(int boundary, int step)
        {
            MorphologicalFeatures[] objects = [];
            bool reflexiveSe = false;
            int previous = boundary;
            for (int at = boundary + step; (uint)at < (uint)words.Count; at += step)
            {
                ct.ThrowIfCancellationRequested();
                ContextLanes.TokenView word = words[at];
                if (!Exact(word) || !Adjacent(Math.Min(previous, at), Math.Max(previous, at))) return;
                var finite = word.Readings.Where(row => row.Features.VerbForm == MorphologicalVerbForm.Finite &&
                    ContextEvidenceGuards.IsPersonalPredicate(row.Features)).ToArray();
                if (finite.Length > 0)
                {
                    bool preverbalSe = step < 0 && at > 0 && Adjacent(at - 1, at) &&
                        words[at - 1].CanonicalText == "се" && Exact(words[at - 1]) &&
                        words[at - 1].Readings.All(row =>
                            row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle && Clean(row.Features));
                    int tail = Math.Max(at, head) + 1;
                    // Only a fully accounted adverbial tail proves an available
                    // object position. A later nominal or a PP leaves it unassessed,
                    // unless an ungoverned postverbal direct-object NP already occupies that slot.
                    bool clearTail = true;
                    for (int i = tail; i < words.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Adjacent(i - 1, i) || !AdverbBranch(words[i]))
                        {
                            clearTail = false;
                            break;
                        }
                    }
                    bool occupiedObjectTail = !clearTail && step > 0 && objects.Length == 0 && StartsDirectObjectPhrase(tail);
                    foreach (var row in finite)
                    {
                        bool occupiedSubjectLeft = step < 0 && HasPreverbalSubjectCarrier(at, row.Features);
                        carriers.Add((row, objects, clearTail, occupiedObjectTail, step < 0, occupiedSubjectLeft, reflexiveSe || preverbalSe));
                    }
                    return;
                }
                var clitic = word.Readings.Where(ContextEvidenceGuards.IsAccusativeCliticReading)
                    .Select(row => row.Features).ToArray();
                if (objects.Length == 0 && clitic.Length > 0)
                    objects = clitic;
                else if (word.CanonicalText == "се" && word.Readings.All(row =>
                    row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle && Clean(row.Features)))
                    reflexiveSe = true;
                else if (!AdverbBranch(word) && !word.Readings.All(row =>
                    row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle && Clean(row.Features))) return;
                previous = at;
            }
        }

        bool StartsDirectObjectPhrase(int start)
        {
            if (start >= words.Count || !Adjacent(start - 1, start)) return false;
            int at = start;
            if (words[at].CanonicalText is "много" or "многото" or "малко" or "немалко" or "повече" or "повечето" or "няколко" &&
                at + 1 < words.Count && Adjacent(at, at + 1))
                at++;
            else if (Exact(words[at]) && words[at].Readings.Any(r => ContextEvidenceGuards.TryGetDeterminerAxes(words[at], r, out _)) &&
                at + 1 < words.Count && Adjacent(at, at + 1))
                at++;
            while (at + 1 < words.Count && Adjacent(at, at + 1) && Exact(words[at]) &&
                words[at].Readings.All(r => ContextEvidenceGuards.IsSourcePlaceholder(words[at], r) ||
                    ContextEvidenceGuards.IsUnwrittenCapitalizedReading(words[at], r) || NominalModifier(r.Features) ||
                    ContextEvidenceGuards.TryGetOrdinalAxes(r, out _)))
                at++;
            if (!Exact(words[at]) || at == start && words[at].CanonicalText is "сутринта" or "вечерта" or "нощта") return false;
            var active = words[at].Readings.Where(r => !ContextEvidenceGuards.IsSourcePlaceholder(words[at], r) &&
                !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(words[at], r) &&
                !ContextEvidenceGuards.IsSyncreticVocativeCompanion(words[at], r)).ToArray();
            return active.Length > 0 && active.All(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false, HasUnrecognizedFeatures: false }) &&
                active.Any(r => ContextEvidenceGuards.HasNominalAxes(r.Features));
        }

        bool HasPreverbalSubjectCarrier(int verbIndex, MorphologicalFeatures verb)
        {
            int clauseStart = 0;
            for (int s = verbIndex - 1; s >= 0; s--)
            {
                int prevEnd = words[s].Token.Start + words[s].Token.Length;
                if (text.AsSpan(prevEnd, words[s + 1].Token.Start - prevEnd).IndexOfAny(",;:") >= 0)
                {
                    clauseStart = s + 1;
                    break;
                }
                string wCanon = words[s].CanonicalText;
                if (wCanon != "и" && BulgarianFunctionWords.HasAny(wCanon,
                        FunctionWordClasses.DaParticle | FunctionWordClasses.CoreCoordinatingConjunction |
                        FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.RelativeSubjectPronoun) ||
                    wCanon is "когото" or "където")
                {
                    clauseStart = s + 1;
                    break;
                }
            }

            bool insidePp = false;
            bool shortMascPrefix = false;
            for (int k = clauseStart; k < verbIndex; k++)
            {
                ContextLanes.TokenView w = words[k];
                if (k > clauseStart && !Adjacent(k - 1, k))
                {
                    insidePp = false;
                    shortMascPrefix = false;
                }
                if (!Exact(w)) continue;
                string canon = w.CanonicalText;
                if (ContextEvidenceGuards.IsExactAdposition(w))
                {
                    insidePp = true;
                    shortMascPrefix = false;
                    continue;
                }
                var active = w.Readings.Where(r => !ContextEvidenceGuards.IsSourcePlaceholder(w, r) &&
                    !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(w, r, k == 0) &&
                    !ContextEvidenceGuards.IsSyncreticVocativeCompanion(w, r)).ToArray();
                if (active.Length == 0) continue;
                if (insidePp)
                {
                    if (active.All(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or
                            MorphologicalPartOfSpeech.ProperNoun or MorphologicalPartOfSpeech.PersonalPronoun or
                            MorphologicalPartOfSpeech.Pronoun &&
                        r.Features.IsShortForm != true && !ContextEvidenceGuards.TryGetDeterminerAxes(w, r, out _)))
                    {
                        insidePp = false;
                    }
                    continue;
                }
                if (active.All(r => NominalModifier(r.Features) &&
                    r.Features is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Short or MorphologicalArticle.Definite }))
                {
                    shortMascPrefix = true;
                    continue;
                }
                if (active.Any(r => ContextEvidenceGuards.LicensedSubject(canon, r) &&
                        ContextEvidenceGuards.PersonNumberTupleMatches((r.Features.Person, r.Features.Number), (verb.Person, verb.Number))) &&
                    active.All(r => ContextEvidenceGuards.LicensedSubject(canon, r) ||
                        canon == "те" && (r.Category == ContextLanes.Category.Particle && !r.Features.HasUnrecognizedFeatures || ContextEvidenceGuards.IsAccusativeCliticReading(r)) ||
                        canon == "ти" && !r.Features.HasUnrecognizedFeatures && (r.Category == ContextLanes.Category.Particle || r.Features.IsShortForm == true) ||
                        canon == "вие" && r.Category == ContextLanes.Category.Verb) &&
                    (canon is not ("те" or "ти") || k == 0 || !Adjacent(k, verbIndex) || verb.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive))
                {
                    return true;
                }
                if (!shortMascPrefix && active.All(r => r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false } nf &&
                        !IsVocativeRow(r) && !IsTemporalOrMeasureLemma(r.Lemma) &&
                        !(nf.Gender == MorphologicalGender.Masculine && nf.Number == MorphologicalNumber.Singular &&
                          nf.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite) &&
                        AgreesWithSubject(nf, verb)))
                {
                    return true;
                }
                if (!active.All(r => NominalModifier(r.Features) || ContextEvidenceGuards.TryGetDeterminerAxes(w, r, out _)))
                    shortMascPrefix = false;
            }
            return false;
        }

        static bool IsVocativeRow(ContextLanes.ReadingView r) =>
            r.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) == true ||
            r.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) == true;

        static bool IsTemporalOrMeasureLemma(string? lemma) =>
            lemma is not null && ContextEvidenceGuards.IsTemporalOrMeasureLemma(BulgarianTextNormalizer.NormalizeKey(lemma));

        Partition AssessRoles(MorphologicalFeatures left, MorphologicalFeatures right)
        {
            Partition assessment = Partition.None;
            foreach (var carrier in carriers)
            {
                ct.ThrowIfCancellationRequested();
                MorphologicalFeatures verb = carrier.Reading.Features;
                bool hasExplicitPreverbalSubject = carrier.VerbPrecedes && carrier.OccupiedSubjectLeft;
                if (afterPreposition && !hasExplicitPreverbalSubject && CanBeObject(left) && AgreesWithSubject(right, verb))
                    return Partition.Supported;
                if (!TextPredicateLinkAnalyzer.Finite(carrier.Reading) ||
                    verb.DeclaredTransitivity != MorphologicalVerbTransitivity.Transitive ||
                    carrier.ReflexiveSe && carrier.Objects.Length == 0) continue;
                bool subjectAlreadyBound = hasExplicitPreverbalSubject ||
                    carrier.VerbPrecedes && verb.Person is MorphologicalPerson.First or MorphologicalPerson.Second;
                bool leftObject = !afterPreposition && !subjectAlreadyBound && CanBeObject(left) && AgreesWithSubject(right, verb);
                bool barePreverbalRight = !afterPreposition && !carrier.VerbPrecedes && first == 0 &&
                    right.Article == MorphologicalArticle.Indefinite && carrier.Objects.Length == 0;
                bool rightObject = !barePreverbalRight && CanBeObject(right) &&
                    (afterPreposition ? CanBeObject(left) : !subjectAlreadyBound && AgreesWithSubject(left, verb));
                if (!leftObject && !rightObject) continue;
                if (carrier.Objects.Length > 0)
                {
                    if (leftObject && Resumes(left) || rightObject && Resumes(right)) return Partition.Supported;
                }
                else if (carrier.ClearTail) return Partition.Supported;
                else if (!(carrier.OccupiedObjectTail && !afterPreposition && right.Article == MorphologicalArticle.Indefinite))
                    assessment = Partition.Unassessed;

                bool Resumes(MorphologicalFeatures candidate) => carrier.Objects.Any(clitic =>
                    clitic.Person == MorphologicalPerson.Third &&
                    ContextEvidenceGuards.NominalTupleMatches((clitic.Number, clitic.Gender), (candidate.Number, candidate.Gender)));
            }
            return assessment;
        }
        bool Continues(int index, MorphologicalFeatures headFeatures) => words[index].Readings.Any(row =>
            (row.Features.Article == MorphologicalArticle.Indefinite ||
             row.Features.Article is null &&
                 (ContextEvidenceGuards.IsMeasuredPassiveAttribute(row.Features) ||
                  ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(row.Features)) ||
             ContextEvidenceGuards.TryGetOrdinalAxes(row, out var ord) && ord.Article == MorphologicalArticle.Indefinite) &&
            ModifierRowMatches(row, headFeatures, out _));
        static bool ModifierRowMatches(ContextLanes.ReadingView row, MorphologicalFeatures headFeatures, out MorphologicalFeatures effective)
        {
            if (NominalModifier(row.Features) &&
                ContextEvidenceGuards.NominalTupleMatches((row.Features.Number, row.Features.Gender), (headFeatures.Number, headFeatures.Gender)))
            {
                effective = row.Features;
                return true;
            }
            if (ContextEvidenceGuards.TryGetOrdinalAxes(row, out var ord) &&
                ContextEvidenceGuards.NominalTupleMatches((ord.Number, ord.Gender), (headFeatures.Number, headFeatures.Gender)))
            {
                effective = new MorphologicalFeatures
                {
                    PartOfSpeech = MorphologicalPartOfSpeech.Adjective,
                    Gender = ord.Gender,
                    Number = ord.Number,
                    Article = ord.Article
                };
                return true;
            }
            effective = default!;
            return false;
        }
        static bool AdverbBranch(ContextLanes.TokenView word) => Exact(word) &&
            word.Readings.Any(row => row.SourceAdverb && row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb && Clean(row.Features));
        bool Adjacent(int left, int right)
        {
            var a = words[left]; var b = words[right];
            int end = a.Token.Start + a.Token.Length;
            return b.OriginalIndex == a.OriginalIndex + 1 && b.Token.Start > end &&
                text.AsSpan(end, b.Token.Start - end).Trim().IsEmpty;
        }
        static bool Exact(ContextLanes.TokenView word) => word.Complete && !word.UsesGraph && word.Readings.Count > 0;
        static bool Clean(MorphologicalFeatures features) => !features.HasConflictingFeatures && !features.HasUnrecognizedFeatures;
        static bool CanBeObject(MorphologicalFeatures f) => f.Article is MorphologicalArticle.Short or
            MorphologicalArticle.Definite or MorphologicalArticle.Indefinite;
        static bool AgreesWithSubject(MorphologicalFeatures nominal, MorphologicalFeatures verb) =>
            verb.Person == MorphologicalPerson.Third && verb.Number == nominal.Number &&
            (nominal.Article is MorphologicalArticle.Full or MorphologicalArticle.Indefinite ||
             nominal.PartOfSpeech == MorphologicalPartOfSpeech.Pronoun ||
             nominal.Article == MorphologicalArticle.Definite &&
                (nominal.Number != MorphologicalNumber.Singular || nominal.Gender != MorphologicalGender.Masculine));
        static bool IsPersonalNonCopularFiniteVerbWord(ContextLanes.TokenView word)
        {
            if (!Exact(word)) return false;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            ContextLanes.ReadingView[] active = word.Readings.Where(r =>
                !(hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, r)) &&
                !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, r)).ToArray();
            return active.Length > 0 && active.All(r =>
                r.Category == ContextLanes.Category.Verb &&
                !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures &&
                r.Features.VerbForm == MorphologicalVerbForm.Finite &&
                ContextEvidenceGuards.IsPersonalPredicate(r.Features) &&
                !TextPredicateLinkAnalyzer.CopularLemma(r.Lemma));
        }
        static bool IsStrictlyArticledNominalWord(ContextLanes.TokenView word)
        {
            if (!Exact(word)) return false;
            bool hasNonPlaceholder = word.Readings.Any(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r));
            ContextLanes.ReadingView[] active = word.Readings.Where(r =>
                !(hasNonPlaceholder && ContextEvidenceGuards.IsSourcePlaceholder(word, r)) &&
                !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, r) &&
                !ContextEvidenceGuards.IsSyncreticVocativeCompanion(word, r)).ToArray();
            return active.Length > 0 && active.All(r =>
                (r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Noun ||
                 ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) ||
                 ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(r.Features) ||
                 ContextEvidenceGuards.TryGetOrdinalAxes(r, out _) ||
                 ContextEvidenceGuards.TryGetDeterminerAxes(word, r, out _)) &&
                (r.Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
                 ContextEvidenceGuards.TryGetOrdinalAxes(r, out var ord) &&
                     ord.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short));
        }
    }

    // Uses all supplied alternatives, not RoleReadings alone. Incomplete evidence stays unassessed.
    // Rows at the edited target belong to the candidate branch and must map to ReplacementReadings.
    internal static ContextLanes.NominalPhrase? Measure(Binding binding, CancellationToken ct) =>
        Measure(binding, ct, allowAdnominalReadings: true);

    internal static ContextLanes.NominalPhrase? Measure(Binding binding, CancellationToken ct, bool allowAdnominalReadings) =>
        ContextLanes.NominalMembers(binding.Participants.Select(member => member.Word).ToArray(),
            binding.Participants.Count - 1, ct, allowAdnominalReadings,
            sentenceInitialFirst: binding.Participants.Count > 0 && binding.Participants[0].WordIndex == 0);
}
