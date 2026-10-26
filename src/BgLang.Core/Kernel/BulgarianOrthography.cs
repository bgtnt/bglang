namespace BgLang.Core;

internal static class BulgarianOrthography
{
    // Read only Bulgarian canonical pairs, retaining the consumed raw UTF-16 width.
    // Unsupported marks and malformed surrogate code units remain observable.
    internal static char ReadLetter(ReadOnlySpan<char> text, int offset, out int length)
    {
        char value = text[offset];
        length = 1;
        if (offset + 1 >= text.Length)
            return value;
        char composed = (value, text[offset + 1]) switch
        {
            ('и', '\u0306') => 'й', ('И', '\u0306') => 'Й',
            ('и', '\u0300') => 'ѝ', ('И', '\u0300') => 'Ѝ',
            ('е', '\u0300') => 'ѐ', ('Е', '\u0300') => 'Ѐ',
            _ => value
        };
        if (composed != value)
            length = 2;
        return composed;
    }

    internal static bool IsWellFormedWord(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty)
            return false;
        bool acceptsStress = false;
        for (int index = 0; index < word.Length;)
        {
            char value = char.ToLowerInvariant(ReadLetter(word, index, out int rawLength));
            index += rawLength;
            if (value is '\u0300' or '\u0301')
            {
                if (!acceptsStress)
                    return false;
                acceptsStress = false;
                continue;
            }

            if (!IsLowercaseLetterOrStressedVowel(value))
                return false;
            acceptsStress = IsVowel(value) && value is not ('ѐ' or 'ѝ');
        }
        return true;
    }

    internal static bool IsVowel(char value) =>
        char.ToLowerInvariant(value) is 'а' or 'ъ' or 'о' or 'у' or 'е' or 'и' or 'ю' or 'я' or 'ѐ' or 'ѝ';

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
