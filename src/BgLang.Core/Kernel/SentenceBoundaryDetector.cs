using BgLang.Contracts;

namespace BgLang.Core;

internal static class SentenceBoundaryDetector
{
    internal static IReadOnlyList<SentenceSpan> Split(
        string text,
        IReadOnlyList<TokenAnalysis> tokens,
        TextReadingInventory? readingInventory = null)
    {
        TextReadingSet?[]? complete = null;
        if (readingInventory is not null)
        {
            complete = new TextReadingSet?[tokens.Count];
            foreach (TextReadingOccurrence occurrence in readingInventory.Occurrences)
                complete[occurrence.TokenIndex] = readingInventory.Sets[occurrence.ReadingSetIndex];
        }

        List<SentenceSpan> sentences = [];
        int start = 0;

        for (int index = 0; index < tokens.Count; index++)
        {
            TokenAnalysis token = tokens[index];
            if (!IsSentenceEndingToken(text, tokens, index, start, complete))
                continue;

            int end = token.Start + token.Length;
            while (end < text.Length && IsSentenceTerminator(text[end]))
                end++;
            while (end < text.Length && IsSentenceClosingWrapper(text[end]))
                end++;

            AddSentence(text, start, end, sentences);
            start = end;
            while (index + 1 < tokens.Count && tokens[index + 1].Start < end)
                index++;
        }

        AddSentence(text, start, text.Length, sentences);
        return sentences;
    }

    /// <summary>
    /// Groups word tokens per sentence in one forward walk. Tokens are emitted in ascending
    /// start order and sentences never overlap, so each token is visited a bounded number of times.
    /// </summary>
    internal static TokenAnalysis[][] GroupWordsBySentence(
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences)
    {
        var groups = new TokenAnalysis[sentences.Count][];
        List<TokenAnalysis> words = [];
        int cursor = 0;
        for (int index = 0; index < sentences.Count; index++)
        {
            SentenceSpan sentence = sentences[index];
            int end = sentence.Start + sentence.Length;
            while (cursor < tokens.Count && tokens[cursor].Start < sentence.Start)
                cursor++;

            words.Clear();
            for (int scan = cursor; scan < tokens.Count && tokens[scan].Start < end; scan++)
            {
                TokenAnalysis token = tokens[scan];
                if (token.Kind == TokenKind.Word && token.Start + token.Length <= end)
                    words.Add(token);
            }

            groups[index] = words.ToArray();
        }

        return groups;
    }

    private static bool IsSentenceEndingToken(string text, IReadOnlyList<TokenAnalysis> tokens,
        int index, int sentenceStart, TextReadingSet?[]? complete)
    {
        TokenAnalysis token = tokens[index];
        if (token.Kind == TokenKind.Word)
            return IsTerminalAbbreviation(text, tokens, index, complete);
        if (token.Kind != TokenKind.Punctuation || !ContainsSentenceEnding(token.Text))
            return false;

        return !IsDecimalSeparator(tokens, index) &&
            !IsAnchoredNameInitial(text, tokens, index, sentenceStart, complete);
    }

    private static bool IsTerminalAbbreviation(string text, IReadOnlyList<TokenAnalysis> tokens,
        int index, TextReadingSet?[]? complete)
    {
        TokenAnalysis token = tokens[index];
        if (!token.Text.EndsWith('.') ||
            !BulgarianAbbreviationLexicon.TryNormalizeExact(token.Text, out string canonical)) return false;
        if (canonical != "и др.")
        {
            if (canonical is not ("лв." or "кв.м." or "г.") || index == 0 ||
                tokens[index - 1].Kind != TokenKind.Number ||
                !WhitespaceBetween(text, tokens[index - 1], token)) return false;
        }
        int next = FollowingWord(text, tokens, index);
        if (next < 0 || !char.IsUpper(tokens[next].Text[0])) return false;
        if (ExactFinite(Readings(complete, next))) return true;

        // A single adverb-compatible prelude followed by an exact finite verb also starts a clause.
        IReadOnlyList<TextReadingSnapshot> prelude = Readings(complete, next);
        if (!prelude.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb) ||
            prelude.Any(r => r.Features.HasConflictingFeatures || r.Features.PartOfSpeech is not
                (MorphologicalPartOfSpeech.Adverb or MorphologicalPartOfSpeech.Adjective))) return false;
        int predicate = FollowingWord(text, tokens, next);
        if (predicate < 0) return false;
        if (ExactFinite(Readings(complete, predicate))) return true;
        // Only the numeric-unit lane admits an original canonical copula beside unknown shadows.
        return canonical != "и др." && complete?[next]?.CanonicalReadings.Count > 0 &&
            prelude.All(r => r.Features == r.Raw.Features && !r.Features.HasUnrecognizedFeatures &&
                string.Equals(r.Raw.Form, tokens[next].Text, StringComparison.OrdinalIgnoreCase)) &&
            ExactPresentCopula(tokens[predicate], complete?[predicate]);
    }

    private static bool IsAnchoredNameInitial(string text, IReadOnlyList<TokenAnalysis> tokens,
        int index, int sentenceStart, TextReadingSet?[]? complete)
    {
        if (tokens[index].Text != "." || index == 0) return false;
        TokenAnalysis initial = tokens[index - 1];
        if (initial.Kind != TokenKind.Word || initial.Start + initial.Length != tokens[index].Start) return false;
        if (initial.Text is not ("Хр" or "Ив") &&
            (initial.Text.Length != 1 || initial.Text[0] is < 'А' or > 'Я')) return false;
        // An initial begins this sentence. A preceding clause or letter label remains a real boundary.
        for (int i = sentenceStart; i < initial.Start; i++)
            if (!char.IsWhiteSpace(text[i]) && !IsSentenceQuote(text[i]) && text[i] is not ('(' or '[')) return false;
        int next = FollowingWord(text, tokens, index);
        if (next < 0 || !ProperNameEvidenceAnalyzer.IsBulgarianTitleCase(tokens[next].Text)) return false;
        IReadOnlyList<TextReadingSnapshot> names = Readings(complete, next);
        if (!names.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun &&
                r.Features.NameKind == MorphologicalNameKind.FamilyName)) return false;
        if (names.All(r => !r.Features.HasConflictingFeatures &&
                r.Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun &&
                r.Features.NameKind is MorphologicalNameKind.GivenName or MorphologicalNameKind.FamilyName)) return true;
        // A known multi-letter name abbreviation may retain clean adjective homographs.
        // A single-letter initial keeps the stricter all-person-name requirement above.
        return (initial.Text is "Хр" or "Ив") && complete?[next]?.CanonicalReadings.Count > 0 &&
            names.All(r => r.Features == r.Raw.Features && !r.Features.HasConflictingFeatures &&
                !r.Features.HasUnrecognizedFeatures &&
                string.Equals(r.Raw.Form, tokens[next].Text, StringComparison.OrdinalIgnoreCase) &&
                (r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                    r.Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun &&
                    r.Features.NameKind is MorphologicalNameKind.GivenName or MorphologicalNameKind.FamilyName));
    }

    private static IReadOnlyList<TextReadingSnapshot> Readings(TextReadingSet?[]? complete, int index) =>
        complete?[index]?.ResolvedReadings ?? [];

    private static bool ExactFinite(IReadOnlyList<TextReadingSnapshot> readings) =>
        readings.Count > 0 && readings.All(r => !r.Features.HasConflictingFeatures &&
            r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
            r.Features.VerbForm == MorphologicalVerbForm.Finite &&
            r.Features.Person is not null && r.Features.Number is not null && r.Features.Tense is not null);

    private static bool ExactPresentCopula(TokenAnalysis token, TextReadingSet? set)
    {
        if (token.Text != "е" || set is null || set.CanonicalReadings.Count == 0) return false;
        bool witnessed = false;
        foreach (TextReadingSnapshot reading in set.CanonicalReadings)
        {
            MorphologicalFeatures features = reading.Features;
            if (reading.Raw.Form != "е" || features != reading.Raw.Features || features.HasConflictingFeatures) return false;
            if (features.PartOfSpeech == MorphologicalPartOfSpeech.Unknown) continue;
            if (reading.Raw.Lemma != "съм" || features.HasUnrecognizedFeatures ||
                features.PartOfSpeech != MorphologicalPartOfSpeech.Verb ||
                features.VerbForm != MorphologicalVerbForm.Finite || features.Person != MorphologicalPerson.Third ||
                features.Number != MorphologicalNumber.Singular || features.Tense != MorphologicalTense.Present) return false;
            witnessed = true;
        }
        return witnessed;
    }
    private static int FollowingWord(string text, IReadOnlyList<TokenAnalysis> tokens, int index)
    {
        int end = tokens[index].Start + tokens[index].Length;
        if (end >= text.Length || !char.IsWhiteSpace(text[end])) return -1;
        int next = index + 1;
        while (next < tokens.Count && tokens[next].Kind == TokenKind.Whitespace) next++;
        if (next == tokens.Count || tokens[next].Kind != TokenKind.Word) return -1;
        // Quoted lexical verbs may be titles or mentioned words. Only an unquoted start licenses this lane.
        for (int i = end; i < tokens[next].Start; i++)
            if (!char.IsWhiteSpace(text[i])) return -1;
        return next;
    }

    private static bool WhitespaceBetween(string text, TokenAnalysis left, TokenAnalysis right)
    {
        int end = left.Start + left.Length;
        if (end >= right.Start) return false;
        for (int i = end; i < right.Start; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    private static bool ContainsSentenceEnding(string text)
    {
        foreach (char character in text)
        {
            if (IsSentenceTerminator(character))
                return true;
        }

        return false;
    }

    internal static bool IsSentenceTerminator(char character) =>
        character is '.' or '!' or '?' or '\u2026';

    internal static bool IsSentenceQuote(char character) =>
        character is '"' or '\'' or '«' or '»' or '‘' or '’' or '‚' or
            '“' or '”' or '„';

    internal static bool IsSentenceClosingWrapper(char character) =>
        IsSentenceQuote(character) || character is ')' or ']';

    private static bool IsDecimalSeparator(IReadOnlyList<TokenAnalysis> tokens, int index)
    {
        TokenAnalysis token = tokens[index];
        if (token.Text != ".")
            return false;

        TokenAnalysis? previous = index > 0 ? tokens[index - 1] : null;
        TokenAnalysis? next = index + 1 < tokens.Count ? tokens[index + 1] : null;
        return previous?.Kind == TokenKind.Number &&
            next?.Kind == TokenKind.Number &&
            previous.Start + previous.Length == token.Start &&
            next.Start == token.Start + token.Length;
    }

    private static void AddSentence(
        string text,
        int start,
        int end,
        List<SentenceSpan> sentences)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;

        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        if (end > start)
            sentences.Add(new SentenceSpan(start, end - start));
    }
}
