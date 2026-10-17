using System.Buffers;

namespace BgLang.Core;

internal static class BulgarianAbbreviationLexicon
{
    private static readonly AbbreviationEntry[] Entries =
    [
        new("т.нар."),
        new("пр.н.е."),
        new("кв. м.", "кв.м."),
        new("проф."),
        new("доц."),
        new("инж."),
        new("кв.м."),
        new("г-жа"),
        new("т.е."),
        new("т.н."),
        new("и др."),
        new("лв."),
        new("г-н"),
        new("д-р"),
        new("чл."),
        new("ал."),
        new("стр."),
        new("бр."),
        new("гр."),
        new("бул."),
        new("ул."),
        new("вж.")
    ];

    private static readonly SearchValues<char> FirstCharacters = BuildFirstCharacters();

    internal static bool TryMatch(ReadOnlySpan<char> text, int index, out string tokenText, out int nextIndex)
    {
        if ((uint)index >= (uint)text.Length ||
            !FirstCharacters.Contains(char.ToLowerInvariant(text[index])))
        {
            tokenText = string.Empty;
            nextIndex = index;
            return false;
        }

        foreach (AbbreviationEntry entry in Entries)
        {
            if (!Matches(text, index, entry.Surface.AsSpan()))
                continue;

            tokenText = entry.TokenText;
            nextIndex = index + entry.Surface.Length;
            return true;
        }

        tokenText = string.Empty;
        nextIndex = index;
        return false;
    }

    internal static bool TryNormalizeExact(ReadOnlySpan<char> surface, out string tokenText)
    {
        foreach (AbbreviationEntry entry in Entries)
        {
            if (surface.Length == entry.Surface.Length && Matches(surface, 0, entry.Surface.AsSpan()))
            {
                tokenText = entry.TokenText;
                return true;
            }
        }

        tokenText = string.Empty;
        return false;
    }

    private static bool Matches(ReadOnlySpan<char> text, int index, ReadOnlySpan<char> value)
    {
        if (index + value.Length > text.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (char.ToLowerInvariant(text[index + i]) != char.ToLowerInvariant(value[i]))
                return false;
        }

        // Letter-final abbreviations must end at the same boundary as an ordinary word.
        int end = index + value.Length;
        return end == text.Length || !char.IsLetter(value[^1]) ||
            !BulgarianTokenizer.IsWordContinuation(text, end);
    }

    private static SearchValues<char> BuildFirstCharacters()
    {
        Span<char> characters = stackalloc char[Entries.Length];
        for (int i = 0; i < Entries.Length; i++)
            characters[i] = char.ToLowerInvariant(Entries[i].Surface[0]);
        return SearchValues.Create(characters);
    }

    private readonly record struct AbbreviationEntry(string Surface, string TokenText)
    {
        internal AbbreviationEntry(string surface)
            : this(surface, surface)
        {
        }
    }
}
