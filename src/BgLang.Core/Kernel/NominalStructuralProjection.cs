using BgLang.Contracts;

namespace BgLang.Core;

// Structural binding only. This neither selects a parse nor licenses a subject/object.
internal static class NominalStructuralProjection
{
    internal enum BridgeKind { Whitespace, Comma, ConjunctionAnd }
    internal sealed record Member(int WordIndex, ContextLanes.TokenView Word, IReadOnlyList<int> RoleReadings);
    internal sealed record Bridge(BridgeKind Kind, int Start, int Length, int? TokenIndex,
        ContextLanes.TokenView? Conjunction = null)
    {
        internal IReadOnlyList<int> WitnessReadings { get; init; } = [];
    }
    internal sealed record Binding(int Start, int Length, IReadOnlyList<Member> Participants, IReadOnlyList<Bridge> Bridges)
    {
        internal Member Head => Participants[^1];
    }

    internal static IReadOnlyList<ContextLanes.TokenView> SentenceWords(TextAnalysis analysis, SentenceSpan sentence,
        MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        List<ContextLanes.TokenView> words = [];
        foreach (var entry in morphology.OrderedTokens)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[entry.Index];
            if (token.Start < sentence.Start || (long)token.Start + token.Length > (long)sentence.Start + sentence.Length) continue;
            var rows = entry.Rows;
            words.Add(new(entry.Index, token, rows.Canonical, rows.Lanes, rows.GraphUnavailableReason is null) { UsesGraph = rows.UsesGraph });
        }
        return words;
    }

    // One requested head: linear in the bound local core and supplied reading rows.
    // No all-head/all-boundary search, agreement test, article filter, or reading Cartesian product.
    internal static Binding? BindHead(string text, SentenceSpan sentence, IReadOnlyList<TokenAnalysis> sourceTokens,
        IReadOnlyList<ContextLanes.TokenView> words, int headWordIndex, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sentence.Start < 0 || sentence.Length < 0 || (long)sentence.Start + sentence.Length > text.Length)
            throw new ArgumentException("Nominal sentence is outside original UTF16 source.");
        if ((uint)headWordIndex >= (uint)words.Count) return null;
        Member? head = MemberAt(headWordIndex, ContextLanes.Category.Noun, MorphologicalPartOfSpeech.Noun);
        if (head is null) return null;
        List<Member> reversed = [head];
        List<Bridge> bridges = [];
        int current = headWordIndex;
        while (current > 0)
        {
            ct.ThrowIfCancellationRequested();
            Member? previous = MemberAt(current - 1, ContextLanes.Category.Adjective, MorphologicalPartOfSpeech.Adjective);
            Bridge? bridge = previous is null ? null : LocalBridge(previous.Word, words[current], reversed.Count > 1);
            if (previous is not null && bridge is not null)
            {
                reversed.Add(previous); bridges.Add(bridge); current--; continue;
            }
            // и only joins two actual possible modifiers, never modifier-to-head or noun-to-noun.
            if (reversed.Count > 1 && current > 1 && Conjunction(current - 1) is { } witnesses)
            {
                previous = MemberAt(current - 2, ContextLanes.Category.Adjective, MorphologicalPartOfSpeech.Adjective);
                ContextLanes.TokenView conjunction = words[current - 1];
                if (previous is not null && Whitespace(previous.Word, conjunction) && Whitespace(conjunction, words[current]))
                {
                    int from = previous.Word.Token.Start + previous.Word.Token.Length;
                    bridges.Add(new(BridgeKind.ConjunctionAnd, from, words[current].Token.Start - from,
                        conjunction.OriginalIndex, conjunction) { WitnessReadings = witnesses });
                    reversed.Add(previous); current -= 2; continue;
                }
            }
            break;
        }
        reversed.Reverse(); bridges.Reverse();
        int start = reversed[0].Word.Token.Start;
        return new(start, head.Word.Token.Start + head.Word.Token.Length - start,
            reversed.AsReadOnly(), bridges.AsReadOnly());

        Member? MemberAt(int index, ContextLanes.Category category, MorphologicalPartOfSpeech pos)
        {
            ContextLanes.TokenView word = words[index];
            RequireOriginal(word);
            List<int> possible = [];
            for (int row = 0; row < word.Readings.Count; row++)
            {
                ct.ThrowIfCancellationRequested();
                if (word.Readings[row].Category == category && word.Readings[row].Features.PartOfSpeech == pos ||
                    category == ContextLanes.Category.Adjective && word.Readings[row].Category == ContextLanes.Category.Verb &&
                    ContextEvidenceGuards.IsPassiveAttributeShape(word.Readings[row].Features))
                    possible.Add(row);
            }
            // Incomplete/conflicting/unmeasured evidence is retained if its typed role is possible.
            if (possible.Count > 0) return new(index, word, possible.AsReadOnly());
            // Unlicensed raw graph shape can describe topology, never a support reading.
            return !word.Complete && word.Readings.Count == 0 && PotentialGraph(word, category, pos)
                ? new(index, word, Array.Empty<int>()) : null;
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
                if (BulgarianTextNormalizer.NormalizeKey(path.Word) != normalized)
                    throw new ArgumentException("Potential graph path belongs to a foreign word.");
                foreach (string tag in path.FullTags ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(tag)) continue;
                    MorphologicalFeatures feature = MorphologicalFeatures.FromTag(tag);
                    found |= feature.PartOfSpeech == pos || category == ContextLanes.Category.Adjective &&
                        ContextEvidenceGuards.IsPassiveAttributeShape(feature);
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
            if (!betweenModifiers || right.OriginalIndex != left.OriginalIndex + 2) return null;
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

    // Uses all supplied alternatives, not RoleReadings alone. Incomplete evidence stays unassessed.
    // Rows at the edited target belong to the candidate branch and must map to ReplacementReadings.
    internal static ContextLanes.NominalPhrase? Measure(Binding binding, CancellationToken ct) =>
        ContextLanes.NominalMembers(binding.Participants.Select(member => member.Word).ToArray(),
            binding.Participants.Count - 1, ct, allowAdnominalReadings: true);
}
