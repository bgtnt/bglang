using BgLang.Contracts;

namespace BgLang.Core;

internal static class ProperNameAnalyzer
{
    // ExactReadings is the COMPLETE request-local inventory, never display32.
    // Facts come from shared typed projection and the pinned context predicates.
    // UnknownContextReady leaves the undecided graph/BaseEvidence policy explicit.
    internal sealed record TokenFacts(TokenAnalysis Token, int SentenceIndex,
        bool ExactInventoryComplete, IReadOnlyList<TextReadingSnapshot> ExactReadings,
        bool ContextTitleCase, bool ContextLowercase, bool PersonTitle,
        bool CurrentTitleGuardSatisfied, bool UnknownContextReady);

    internal static ProperNameEvidence Analyze(string text, IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences, TextReadingInventory inventory, bool includeUnknownContext,
        CancellationToken cancellationToken)
    {
        TokenFacts[] facts = new TokenFacts[tokens.Count];
        int occurrenceIndex = 0, sentenceIndex = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[i];
            while (sentenceIndex < sentences.Count &&
                token.Start >= sentences[sentenceIndex].Start + sentences[sentenceIndex].Length) sentenceIndex++;
            int sentence = sentenceIndex < sentences.Count && token.Start >= sentences[sentenceIndex].Start &&
                token.Start + token.Length <= sentences[sentenceIndex].Start + sentences[sentenceIndex].Length
                ? sentenceIndex : -1;
            IReadOnlyList<TextReadingSnapshot> readings = [];
            if (token.Kind == TokenKind.Word)
            {
                if (occurrenceIndex >= inventory.Occurrences.Count || inventory.Occurrences[occurrenceIndex].TokenIndex != i)
                    throw new InvalidOperationException("A proper-name input has no complete Word inventory.");
                readings = inventory.Sets[inventory.Occurrences[occurrenceIndex++].ReadingSetIndex].ResolvedReadings;
                if (sentence < 0) throw new InvalidOperationException("A Word token has no source sentence span.");
            }
            bool titleGuard = i > 0 && ProperNameEvidenceAnalyzer.IsPersonTitle(tokens[i - 1]) &&
                token.Analysis is { IsKnown: false, BaseEvidence: null } &&
                ProperNameEvidenceAnalyzer.IsBulgarianTitleCase(token.Text) &&
                ProperNameEvidenceAnalyzer.HasOnlyWhitespaceBetween(text, tokens[i - 1], token);
            facts[i] = new(token, sentence, true, readings, IsContextTitleCase(token.Text),
                IsBulgarianLowercaseWord(token.Text), ProperNameEvidenceAnalyzer.IsPersonTitle(token),
                titleGuard, token.Analysis is { IsKnown: false, BaseEvidence: null });
        }
        if (occurrenceIndex != inventory.Occurrences.Count)
            throw new InvalidOperationException("The proper-name inventory has extra Word occurrences.");
        return Calculate(text, facts, includeUnknownContext, groupingEnabled: true,
            sourceInputComplete: true, cancellationToken);
    }

    internal static ProperNameEvidence Calculate(string text, IReadOnlyList<TokenFacts> tokens,
        bool unknownContextEnabled, bool groupingEnabled, bool sourceInputComplete,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var closed = unknownContextEnabled ? ClosedAnchors(text, tokens, cancellationToken)
            : new Dictionary<int, TextReadingReference[]>();
        List<ProperNameCandidate> singles = [];
        List<ProperNameUnresolved> unresolved = [];
        List<int> deferred = [];
        bool complete = sourceInputComplete;
        for (int i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenFacts current = tokens[i];
            if (current.Token.Kind != TokenKind.Word || !current.Token.Text.Any(char.IsLetter))
                continue;
            if (!current.ExactInventoryComplete)
            {
                deferred.Add(i);
                complete = false;
                continue;
            }

            List<TextReadingReference> supports = [];
            ProperNameKinds kinds = ProperNameKinds.Unspecified;
            bool kindKnown = true;
            for (int r = 0; r < current.ExactReadings.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TextReadingSnapshot reading = current.ExactReadings[r];
                if (reading.Features.PartOfSpeech != MorphologicalPartOfSpeech.ProperNoun)
                    continue;
                supports.Add(new(i, r));
                kinds |= reading.Features.NameKind switch
                {
                    MorphologicalNameKind.GivenName or MorphologicalNameKind.FamilyName => ProperNameKinds.Person,
                    MorphologicalNameKind.PlaceName => ProperNameKinds.Place,
                    _ => ProperNameKinds.Unspecified
                };
                kindKnown &= reading.Features.NameKind is not null;
            }
            if (supports.Count > 0)
            {
                singles.Add(Create(i, current, ProperNameLane.Exact, kinds, kindKnown, supports, []));
                complete &= kindKnown;
                continue;
            }
            if (current.ExactReadings.Count > 0 || !unknownContextEnabled || !current.ContextTitleCase)
                continue;
            if (!current.UnknownContextReady)
            {
                deferred.Add(i);
                complete = false;
                continue;
            }

            if (closed.TryGetValue(i, out TextReadingReference[]? anchors))
                singles.Add(Create(i, current, ProperNameLane.ClosedClause, ProperNameKinds.Unspecified, true, [], anchors));
            else if (current.CurrentTitleGuardSatisfied)
                singles.Add(Create(i, current, ProperNameLane.Title, ProperNameKinds.Person, true, [], []));
            else if (i > 0 && SameSentence(tokens, i - 1, i) &&
                tokens[i - 1].Token.Kind == TokenKind.Word &&
                tokens[i - 1].Token.Text.Any(char.IsLetter) && !tokens[i - 1].PersonTitle)
                singles.Add(Create(i, current, ProperNameLane.MidSentence, ProperNameKinds.Unspecified, true, [], []));
            else
            {
                TextReadingReference[] surname = SurnameAnchors(tokens, i, cancellationToken);
                if (surname.Length > 0)
                    singles.Add(Create(i, current, ProperNameLane.Surname, ProperNameKinds.Person, true, [], surname));
                else
                    unresolved.Add(new(i, i == 0 || Terminal(tokens[i - 1].Token)
                        ? "sentence-initial-context-insufficient"
                        : "punctuation-separated-context-insufficient"));
            }
        }
        List<ProperNameGroup> groups = groupingEnabled ? GroupRuns(text, tokens, singles, complete, cancellationToken) : [];
        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(singles.ToArray()), Array.AsReadOnly(groups.ToArray()),
            unknownContextEnabled ? Array.AsReadOnly(unresolved.ToArray()) : null,
            Array.AsReadOnly(deferred.ToArray()), complete);
    }

    private static ProperNameCandidate Create(int index, TokenFacts token, ProperNameLane lane, ProperNameKinds kinds,
        bool kindKnown, IReadOnlyList<TextReadingReference> exact, IReadOnlyList<TextReadingReference> context) =>
        new(index, token.Token.Start, token.Token.Length, lane, kinds, kindKnown,
            Array.AsReadOnly(exact.ToArray()), Array.AsReadOnly(context.ToArray()));

    private static Dictionary<int, TextReadingReference[]> ClosedAnchors(string text,
        IReadOnlyList<TokenFacts> tokens, CancellationToken cancellationToken)
    {
        Dictionary<int, TextReadingReference[]> result = [];
        for (int i = 0; i + 3 < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenFacts first = tokens[i], second = tokens[i + 1], predicate = tokens[i + 2];
            if (i > 0 && !Terminal(tokens[i - 1].Token) || !Terminal(tokens[i + 3].Token) ||
                !EmptyUnknown(first) || !EmptyUnknown(second) ||
                !first.ContextTitleCase || !second.ContextTitleCase ||
                predicate.Token.Kind != TokenKind.Word || !predicate.ContextLowercase ||
                !predicate.ExactInventoryComplete || predicate.ExactReadings.Count == 0 ||
                !SameSentence(tokens, i, i + 3) ||
                !WhitespaceBridge(text, first.Token, second.Token, requireGap: true) ||
                !WhitespaceBridge(text, second.Token, predicate.Token, requireGap: true) ||
                !WhitespaceBridge(text, predicate.Token, tokens[i + 3].Token, requireGap: false))
                continue;
            List<TextReadingReference> anchors = [];
            bool allSingularFinite = true;
            for (int r = 0; r < predicate.ExactReadings.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MorphologicalFeatures f = predicate.ExactReadings[r].Features;
                if (f.HasConflictingFeatures || f.PartOfSpeech != MorphologicalPartOfSpeech.Verb ||
                    f.VerbForm != MorphologicalVerbForm.Finite ||
                    f.Person is not (MorphologicalPerson.First or MorphologicalPerson.Second or MorphologicalPerson.Third) ||
                    f.Number != MorphologicalNumber.Singular ||
                    f.Tense is not (MorphologicalTense.Past or MorphologicalTense.Aorist or
                        MorphologicalTense.Imperfect or MorphologicalTense.Present or MorphologicalTense.Future))
                {
                    allSingularFinite = false;
                    break;
                }
                if (f.Person == MorphologicalPerson.Third)
                    anchors.Add(new(i + 2, r));
            }
            if (!allSingularFinite || anchors.Count == 0)
                continue;
            result.Add(i, anchors.ToArray());
            result.Add(i + 1, anchors.ToArray());
        }
        return result;
    }

    private static TextReadingReference[] SurnameAnchors(IReadOnlyList<TokenFacts> tokens, int index,
        CancellationToken cancellationToken)
    {
        int next = index + 1;
        if (next >= tokens.Count || !SameSentence(tokens, index, next) ||
            tokens[next].Token.Kind != TokenKind.Word || !tokens[next].Token.Text.Any(char.IsLetter) ||
            !tokens[next].ExactInventoryComplete)
            return [];
        List<TextReadingReference> refs = [];
        for (int r = 0; r < tokens[next].ExactReadings.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextReadingSnapshot reading = tokens[next].ExactReadings[r];
            if (reading.Features.PartOfSpeech != MorphologicalPartOfSpeech.ProperNoun)
                continue;
            if (reading.Features.NameKind is null or MorphologicalNameKind.PlaceName)
                return [];
            if (reading.Features.NameKind == MorphologicalNameKind.FamilyName)
                refs.Add(new(next, r));
        }
        return refs.ToArray();
    }

    private static List<ProperNameGroup> GroupRuns(string text, IReadOnlyList<TokenFacts> tokens,
        IReadOnlyList<ProperNameCandidate> singles, bool complete, CancellationToken cancellationToken)
    {
        List<ProperNameGroup> groups = [];
        int runStart = 0;
        ProperNameKinds kinds = ProperNameKinds.Unspecified;
        for (int i = 0; i <= singles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool continues = i > runStart && i < singles.Count &&
                singles[i - 1].GroupingKindKnown && singles[i].GroupingKindKnown &&
                singles[i].TokenIndex == singles[i - 1].TokenIndex + 1 &&
                SameSentence(tokens, singles[i - 1].TokenIndex, singles[i].TokenIndex) &&
                WhitespaceBridge(text, tokens[singles[i - 1].TokenIndex].Token,
                    tokens[singles[i].TokenIndex].Token, requireGap: true) &&
                (kinds | singles[i].Kinds) != (ProperNameKinds.Person | ProperNameKinds.Place);
            if (!continues)
            {
                if (i - runStart > 1)
                    groups.Add(new(runStart, i - runStart, singles[runStart].Start,
                        singles[i - 1].Start + singles[i - 1].Length - singles[runStart].Start, kinds, complete));
                runStart = i;
                kinds = ProperNameKinds.Unspecified;
            }
            if (i < singles.Count)
                kinds |= singles[i].Kinds;
        }
        return groups;
    }

    private static bool EmptyUnknown(TokenFacts token) => token.Token.Kind == TokenKind.Word &&
        token.ExactInventoryComplete && token.ExactReadings.Count == 0 && token.UnknownContextReady;
    private static bool SameSentence(IReadOnlyList<TokenFacts> tokens, int left, int right) =>
        tokens[left].SentenceIndex >= 0 && tokens[left].SentenceIndex == tokens[right].SentenceIndex;
    private static bool Terminal(TokenAnalysis token) =>
        token.Kind == TokenKind.Punctuation && token.Text is "." or "!" or "?" or "…";
    private static bool WhitespaceBridge(string text, TokenAnalysis left, TokenAnalysis right, bool requireGap)
    {
        int start = left.Start + left.Length, length = right.Start - start;
        if (start < 0 || length < (requireGap ? 1 : 0) || start > text.Length || length > text.Length - start)
            return false;
        foreach (char value in text.AsSpan(start, length))
            if (!char.IsWhiteSpace(value)) return false;
        return true;
    }
    private static bool IsContextTitleCase(string surface)
    {
        bool firstLetterSeen = false;
        bool lowercaseLetterSeen = false;
        bool expectUppercase = true;
        foreach (char character in surface)
        {
            if (character is '\u0300' or '\u0301')
                continue;
            if (character == '-')
            {
                if (!firstLetterSeen)
                    return false;
                expectUppercase = true;
                continue;
            }
            if (!char.IsLetter(character) || !IsCyrillic(character))
                return false;

            if (expectUppercase)
            {
                if (!char.IsUpper(character))
                    return false;
                expectUppercase = false;
                firstLetterSeen = true;
            }
            else
            {
                if (!char.IsLower(character))
                    return false;
                lowercaseLetterSeen = true;
            }
        }

        return firstLetterSeen && lowercaseLetterSeen && !expectUppercase;
    }

    private static bool IsBulgarianLowercaseWord(string surface)
    {
        bool letterSeen = false;
        foreach (char character in surface)
        {
            if (character is '\u0300' or '\u0301')
                continue;
            if (!char.IsLetter(character) || !IsCyrillic(character) || !char.IsLower(character))
                return false;
            letterSeen = true;
        }

        return letterSeen;
    }

    private static bool IsCyrillic(char character) =>
        character is >= '\u0400' and <= '\u052f';

}
