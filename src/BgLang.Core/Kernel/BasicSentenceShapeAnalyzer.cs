using BgLang.Contracts;

namespace BgLang.Core;

internal static class BasicSentenceShapeAnalyzer
{
    private const string EmptyTextCode = "empty_text";
    private const string MissingTerminalCode = "missing_terminal_punctuation";
    private const string SentenceInitialLowercaseCode = "sentence_initial_lowercase";
    private const string TerminalPunctuationRule = "terminal_punctuation";
    private const string SentenceInitialCapitalRule = "sentence_initial_capital";

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        string text,
        IReadOnlyList<SentenceSpan> sentences)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [new TextDiagnostic(EmptyTextCode, 0, 0, null)];

        List<TextDiagnostic> diagnostics = [];
        foreach (SentenceSpan sentence in sentences)
        {
            int initial = FindInitialCharacter(text, sentence);
            if (initial >= 0 && IsLowercaseCyrillic(text[initial]))
            {
                diagnostics.Add(new TextDiagnostic(
                    SentenceInitialLowercaseCode,
                    initial,
                    1,
                    SentenceInitialCapitalRule));
            }

            if (!HasTerminalPunctuation(text, sentence))
            {
                diagnostics.Add(new TextDiagnostic(
                    MissingTerminalCode,
                    sentence.Start + sentence.Length,
                    0,
                    TerminalPunctuationRule));
            }
        }

        return diagnostics;
    }

    private static int FindInitialCharacter(string text, SentenceSpan sentence)
    {
        int end = sentence.Start + sentence.Length;
        for (int index = sentence.Start; index < end; index++)
        {
            char character = text[index];
            if (char.IsWhiteSpace(character) || IsSentenceInitialPrefix(character))
                continue;

            return index;
        }

        return -1;
    }

    private static bool HasTerminalPunctuation(string text, SentenceSpan sentence)
    {
        for (int index = sentence.Start + sentence.Length - 1; index >= sentence.Start; index--)
        {
            char character = text[index];
            if (char.IsWhiteSpace(character) || SentenceBoundaryDetector.IsSentenceClosingWrapper(character))
                continue;

            return SentenceBoundaryDetector.IsSentenceTerminator(character);
        }

        return false;
    }

    private static bool IsSentenceInitialPrefix(char character) =>
        SentenceBoundaryDetector.IsSentenceQuote(character) ||
        character is '\u2013' or '\u2014' or '(' or '[';

    private static bool IsLowercaseCyrillic(char character) =>
        character is >= '\u0400' and <= '\u052F' && char.IsLower(character);
}
