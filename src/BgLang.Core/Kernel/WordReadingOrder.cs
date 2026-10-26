using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>One deterministic ordering for bounded reading selection across package backends.</summary>
internal static class WordReadingOrder
{
    internal static IComparer<WordReading> Comparer { get; } =
        System.Collections.Generic.Comparer<WordReading>.Create(Compare);

    internal static int Compare(WordReading left, WordReading right)
        => LexicalIdentity.CompareReadings(left, right);
}
