using BgLang.Contracts;

namespace BgLang.Core;

internal static class BulgarianPhoneticAnalyzer
{
    private static readonly HashSet<char> BulgarianVowels = new(
        ['а', 'ъ', 'о', 'у', 'е', 'и', 'ю', 'я', 'ѐ', 'ѝ'],
        EqualityComparer<char>.Default);

    internal static PhoneticEvidence Analyze(
        string text,
        IReadOnlyList<TokenAnalysis> tokens,
        IRuntimeLexicon? lexicon = null,
        WordFormationAnalyzer? wordFormation = null,
        IReadOnlyDictionary<string, int[]>? lexicalStress = null)
    {
        List<string> syllables = [];
        List<int> explicitStressPositions = [];
        List<int> inferredStressPositions = [];
        List<int> reviewedStressPositions = [];
        List<string> explicitRhymeTails = [];
        List<string> inferredRhymeTails = [];
        List<string> reviewedRhymeTails = [];
        List<int>? lexicalStressPositions = null;
        List<string>? lexicalRhymeTails = null;

        foreach (TokenAnalysis token in tokens)
        {
            if (token.Kind != TokenKind.Word)
                continue;

            string sourceText = text.Substring(token.Start, token.Length);
            syllables.AddRange(SplitWordIntoSyllables(sourceText));

            IReadOnlyList<int> explicitOffsets = FindExplicitStressOffsets(sourceText);
            if (explicitOffsets.Count > 0)
            {
                foreach (int offset in explicitOffsets)
                {
                    explicitStressPositions.Add(token.Start + offset);
                    if (TryCreateRhymeTail(sourceText, offset, out string rhymeTail))
                        explicitRhymeTails.Add(rhymeTail);
                }
            }

            if (lexicon is not null && wordFormation?.HasReviewedExactStress == true && TryGetVowelOffset(sourceText, 1, out _))
            {
                string normalized = BulgarianTextNormalizer.NormalizeKey(sourceText);
                IReadOnlyList<WordReading> completeReadings = lexicon.LookupExactNormalized(normalized, int.MaxValue);
                if (wordFormation.ResolveReviewedExactStressVowelOrdinal(normalized, completeReadings) is { } ordinal &&
                    TryGetVowelOffset(sourceText, ordinal, out int offset) &&
                    !explicitOffsets.Contains(offset) &&
                    TryCreateRhymeTail(sourceText, offset, out string rhymeTail))
                {
                    reviewedStressPositions.Add(token.Start + offset);
                    reviewedRhymeTails.Add(rhymeTail);
                }
            }

            if (lexicalStress is not null &&
                lexicalStress.TryGetValue(BulgarianTextNormalizer.NormalizeKey(sourceText), out int[]? ordinals))
            {
                foreach (int ordinal in ordinals)
                    if (TryGetVowelOffset(sourceText, ordinal, out int offset, minimumVowels: 1) &&
                        TryCreateRhymeTail(sourceText, offset, out string tail))
                    {
                        (lexicalStressPositions ??= []).Add(token.Start + offset);
                        (lexicalRhymeTails ??= []).Add(tail);
                    }
            }

            if (explicitOffsets.Count > 0)
                continue;

            if (FindSingleVowelOffset(sourceText) is { } inferredOffset)
            {
                inferredStressPositions.Add(token.Start + inferredOffset);
                if (TryCreateRhymeTail(sourceText, inferredOffset, out string rhymeTail))
                    inferredRhymeTails.Add(rhymeTail);
            }
        }

        return new PhoneticEvidence(
            syllables,
            explicitStressPositions,
            inferredStressPositions)
        {
            ExplicitRhymeTailCandidates = explicitRhymeTails,
            InferredSingleVowelRhymeTailCandidates = inferredRhymeTails,
            ReviewedExactStressPositions = reviewedStressPositions,
            ReviewedExactRhymeTailCandidates = reviewedRhymeTails,
            LexicalStressPositions = lexicalStressPositions is null ? Array.Empty<int>() : lexicalStressPositions,
            LexicalRhymeTailCandidates = lexicalRhymeTails is null ? Array.Empty<string>() : lexicalRhymeTails
        };
    }

    private static bool TryGetVowelOffset(string word, int ordinal, out int offset, int minimumVowels = 2)
    {
        offset = -1;
        int current = 0;
        for (int index = 0; index < word.Length; index++)
        {
            if (!IsBulgarianVowel(word[index]))
                continue;
            if (current++ == ordinal)
                offset = index;
        }
        return offset >= 0 && current >= minimumVowels;
    }

    private static IReadOnlyList<string> SplitWordIntoSyllables(string word)
    {
        List<string> syllables = [];
        int start = 0;
        int lastVowel = -1;

        for (int index = 0; index < word.Length; index++)
        {
            if (!IsBulgarianVowel(word[index]))
                continue;

            if (lastVowel >= 0)
            {
                int boundary = ChooseBoundary(word, lastVowel, index);
                if (boundary > start)
                {
                    syllables.Add(word[start..boundary]);
                    start = boundary;
                }
            }

            lastVowel = index;
        }

        if (start < word.Length)
            syllables.Add(word[start..]);

        return syllables.Count == 0 ? [word] : syllables;
    }

    private static int ChooseBoundary(string word, int previousVowel, int currentVowel)
    {
        List<int> soundOffsets = [];
        for (int offset = previousVowel + 1; offset < currentVowel; offset++)
        {
            if (!IsCombiningStress(word[offset]))
                soundOffsets.Add(offset);
        }

        if (soundOffsets.Count == 0)
            return currentVowel;
        if (soundOffsets.Count == 1)
            return soundOffsets[0];
        if (char.ToLowerInvariant(word[soundOffsets[0]]) == 'й')
            return soundOffsets[0] + 1;
        return soundOffsets[^2];
    }

    private static IReadOnlyList<int> FindExplicitStressOffsets(string word)
    {
        List<int> offsets = [];
        for (int index = 0; index < word.Length; index++)
        {
            char value = char.ToLowerInvariant(word[index]);
            if (value is 'ѐ' or 'ѝ')
            {
                offsets.Add(index);
                continue;
            }

            if (IsCombiningStress(word[index]) &&
                index > 0 &&
                IsBulgarianVowel(word[index - 1]))
            {
                offsets.Add(index - 1);
            }
        }

        return offsets.Distinct().Order().ToArray();
    }

    private static int? FindSingleVowelOffset(string word)
    {
        int? offset = null;
        for (int index = 0; index < word.Length; index++)
        {
            if (!IsBulgarianVowel(word[index]))
                continue;

            if (offset is not null)
                return null;
            offset = index;
        }

        return offset;
    }

    private static bool TryCreateRhymeTail(
        string word,
        int stressOffset,
        out string rhymeTail)
    {
        rhymeTail = string.Empty;
        if ((uint)stressOffset >= (uint)word.Length || !IsBulgarianVowel(word[stressOffset]))
            return false;

        for (int index = 0; index < word.Length; index++)
        {
            char value = word[index];
            if (IsCombiningStress(value))
            {
                char previous = index == 0 ? '\0' : char.ToLowerInvariant(word[index - 1]);
                if (!IsBulgarianVowel(previous) || previous is 'ѐ' or 'ѝ')
                    return false;
                continue;
            }

            char normalized = char.ToLowerInvariant(value);
            if (!BulgarianOrthography.IsLowercaseLetterOrStressedVowel(normalized))
                return false;
        }

        char[] buffer = new char[word.Length - stressOffset];
        int length = 0;
        for (int index = stressOffset; index < word.Length; index++)
        {
            if (IsCombiningStress(word[index]))
                continue;

            char value = char.ToLowerInvariant(word[index]) switch
            {
                'ѐ' => 'е',
                'ѝ' => 'и',
                var normalized => normalized
            };
            buffer[length++] = value;
        }

        rhymeTail = new string(buffer, 0, length);
        return rhymeTail.Length > 0;
    }

    private static bool IsBulgarianVowel(char character) =>
        BulgarianVowels.Contains(char.ToLowerInvariant(character));

    private static bool IsCombiningStress(char character) =>
        character is '\u0300' or '\u0301';
}
