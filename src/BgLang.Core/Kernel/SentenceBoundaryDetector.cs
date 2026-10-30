using BgLang.Contracts;

namespace BgLang.Core;

internal static class SentenceBoundaryDetector
{
    internal static IReadOnlyList<SentenceSpan> Split(
        string text,
        IReadOnlyList<TokenAnalysis> tokens)
    {
        List<SentenceSpan> sentences = [];
        int start = 0;

        for (int index = 0; index < tokens.Count; index++)
        {
            TokenAnalysis token = tokens[index];
            if (!IsSentenceEndingToken(tokens, index))
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

    private static bool IsSentenceEndingToken(IReadOnlyList<TokenAnalysis> tokens, int index)
    {
        TokenAnalysis token = tokens[index];
        if (token.Kind != TokenKind.Punctuation || !ContainsSentenceEnding(token.Text))
            return false;

        return !IsDecimalSeparator(tokens, index);
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
