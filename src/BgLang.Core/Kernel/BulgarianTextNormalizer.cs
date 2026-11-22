using System.Text;

namespace BgLang.Core;

internal static class BulgarianTextNormalizer
{
    internal static string NormalizeKey(string surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        string trimmed = surface.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        string withoutStress = NormalizeLetters(trimmed, removeStress: true);
        string normalized = BulgarianAbbreviationLexicon.TryNormalizeExact(withoutStress.AsSpan(), out string abbreviation)
            ? abbreviation
            : withoutStress;
        foreach (char character in normalized)
            if (!BulgarianOrthography.IsLowercaseLetterOrStressedVowel(character))
                return normalized.ToLowerInvariant();
        return normalized;
    }

    internal static string NormalizeLetters(string value, bool removeStress)
    {
        if (value.AsSpan().IndexOfAny("\u0300\u0301\u0306") < 0)
            return value;

        StringBuilder result = new(value.Length);
        bool previousVowel = false;
        for (int offset = 0; offset < value.Length;)
        {
            char character = BulgarianOrthography.ReadLetter(value, offset, out int length);
            offset += length;
            if (removeStress && previousVowel && character is '\u0300' or '\u0301')
                continue;

            result.Append(character);
            previousVowel = BulgarianOrthography.IsVowel(character);
        }

        return result.ToString();
    }
}
