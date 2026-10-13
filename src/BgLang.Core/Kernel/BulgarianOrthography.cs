namespace BgLang.Core;

internal static class BulgarianOrthography
{
    internal static bool IsConsonant(char value) => value is
        'б' or 'в' or 'г' or 'д' or 'ж' or 'з' or 'й' or 'к' or 'л' or
        'м' or 'н' or 'п' or 'р' or 'с' or 'т' or 'ф' or 'х' or 'ц' or
        'ч' or 'ш' or 'щ';

    /// <summary>A lowercase alphabet letter, excluding the stressed vowels.</summary>
    internal static bool IsLowercaseLetter(char value) => value is >= 'а' and <= 'я' and not ('ы' or 'э');

    /// <summary>A lowercase alphabet letter or one of the stressed vowels 'ѐ' and 'ѝ'.</summary>
    internal static bool IsLowercaseLetterOrStressedVowel(char value) =>
        IsLowercaseLetter(value) || value is 'ѐ' or 'ѝ';
}
