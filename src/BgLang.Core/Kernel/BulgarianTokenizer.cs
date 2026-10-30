using BgLang.Contracts;

namespace BgLang.Core;

internal sealed class BulgarianTokenizer
{
    internal IReadOnlyList<TokenAnalysis> Tokenize(
        string text,
        Func<string, WordAnalysis> analyzeWord,
        bool includeWhitespace = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(analyzeWord);

        List<TokenAnalysis> tokens = [];
        ReadOnlySpan<char> span = text.AsSpan();
        int index = 0;
        while (index < span.Length)
        {
            char character = span[index];
            if (char.IsWhiteSpace(character))
            {
                int start = index;
                index = ReadWhitespace(span, index + 1);
                if (includeWhitespace)
                    tokens.Add(Create(span, start, index, TokenKind.Whitespace, null));
                continue;
            }

            if (char.IsLetter(character))
            {
                if (BulgarianAbbreviationLexicon.TryMatch(span, index, out string abbreviation, out int nextIndex))
                {
                    tokens.Add(new TokenAnalysis(
                        new string(span[index..nextIndex]),
                        TokenKind.Word,
                        index,
                        nextIndex - index,
                        analyzeWord(abbreviation)));
                    index = nextIndex;
                    continue;
                }

                int start = index;
                index = ReadWord(span, index + 1);
                string word = new(span[start..index]);
                tokens.Add(new TokenAnalysis(word, TokenKind.Word, start, index - start, analyzeWord(word)));
                continue;
            }

            if (char.IsDigit(character))
            {
                int start = index;
                index = ReadDigits(span, index + 1);
                tokens.Add(Create(span, start, index, TokenKind.Number, null));
                continue;
            }

            if (character == '/')
            {
                tokens.Add(Create(span, index, index + 1, TokenKind.Punctuation, null));
                index++;
                continue;
            }

            if (character == '-')
            {
                tokens.Add(Create(span, index, index + 1, TokenKind.Punctuation, null));
                index++;
                continue;
            }

            TokenKind kind = char.IsPunctuation(character) ? TokenKind.Punctuation : TokenKind.Symbol;
            int end = index + 1;
            // Keep a Unicode scalar intact while retaining UTF-16 source offsets.
            if (end < span.Length && char.IsSurrogatePair(character, span[end]))
                end++;
            tokens.Add(Create(span, index, end, kind, null));
            index = end;
        }

        return tokens;
    }

    private static int ReadWhitespace(ReadOnlySpan<char> span, int index)
    {
        while (index < span.Length && char.IsWhiteSpace(span[index]))
            index++;
        return index;
    }

    internal static bool IsWordContinuation(ReadOnlySpan<char> span, int index) =>
        char.IsLetter(span[index]) || IsAttachedBreve(span, index) ||
        IsAttachedStressMark(span, index) || IsInternalWordHyphen(span, index);

    private static int ReadWord(ReadOnlySpan<char> span, int index)
    {
        while (index < span.Length)
        {
            if (IsWordContinuation(span, index))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static bool IsInternalWordHyphen(ReadOnlySpan<char> span, int index) =>
        span[index] == '-' &&
        index > 0 &&
        index + 1 < span.Length &&
        (char.IsLetter(span[index - 1]) || EndsCanonicalPair(span, index)) &&
        char.IsLetter(span[index + 1]);

    private static bool EndsCanonicalPair(ReadOnlySpan<char> span, int end) =>
        end >= 2 && BulgarianOrthography.ReadLetter(span, end - 2, out int length) != span[end - 2] && length == 2;

    private static bool IsAttachedBreve(ReadOnlySpan<char> span, int index) =>
        span[index] == '\u0306' && index > 0 && span[index - 1] is 'и' or 'И';

    private static bool IsAttachedStressMark(ReadOnlySpan<char> span, int index) =>
        span[index] is '\u0300' or '\u0301' &&
        index > 0 &&
        (char.IsLetter(span[index - 1]) || span[index - 1] is '\u0300' or '\u0301' || IsAttachedBreve(span, index - 1));

    private static int ReadDigits(ReadOnlySpan<char> span, int index)
    {
        while (index < span.Length)
        {
            if (char.IsDigit(span[index]) || IsInternalDecimalSeparator(span, index))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static bool IsInternalDecimalSeparator(ReadOnlySpan<char> span, int index) =>
        span[index] is '.' or ',' &&
        index > 0 &&
        index + 1 < span.Length &&
        char.IsDigit(span[index - 1]) &&
        char.IsDigit(span[index + 1]);

    private static TokenAnalysis Create(
        ReadOnlySpan<char> span,
        int start,
        int end,
        TokenKind kind,
        WordAnalysis? analysis) =>
        new(new string(span[start..end]), kind, start, end - start, analysis);
}
