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

        string withoutStress = RemoveExplicitCombiningStress(trimmed);
        string normalized = BulgarianAbbreviationLexicon.TryNormalizeExact(withoutStress.AsSpan(), out string abbreviation)
            ? abbreviation
            : withoutStress;
        foreach (char character in normalized)
            if (!BulgarianOrthography.IsLowercaseLetterOrStressedVowel(character))
                return normalized.ToLowerInvariant();
        return normalized;
    }

    private static string RemoveExplicitCombiningStress(string value)
    {
        int firstMark = value.AsSpan().IndexOfAny('\u0300', '\u0301');
        if (firstMark < 0)
            return value;

        if (value.Length == 2 && value[1] == '\u0300')
        {
            return char.ToLowerInvariant(value[0]) switch
            {
                'и' => "ѝ",
                'е' => "ѐ",
                _ => RemoveAttachedMarks(value)
            };
        }

        return RemoveAttachedMarks(value);
    }

    private static string RemoveAttachedMarks(string value)
    {
        StringBuilder result = new(value.Length);
        char previous = '\0';
        foreach (char character in value)
        {
            if (character is '\u0300' or '\u0301' && IsBulgarianVowel(previous))
                continue;

            result.Append(character);
            previous = character;
        }

        return result.ToString();
    }

    private static bool IsBulgarianVowel(char value) =>
        char.ToLowerInvariant(value) is 'а' or 'ъ' or 'о' or 'у' or 'е' or 'и' or 'ю' or 'я' or 'ѐ' or 'ѝ';
}
