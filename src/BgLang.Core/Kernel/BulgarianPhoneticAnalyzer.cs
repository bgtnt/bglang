using BgLang.Contracts;

namespace BgLang.Core;

internal static class BulgarianPhoneticAnalyzer
{
    internal static PhoneticEvidence Analyze(
        string text,
        IReadOnlyList<TokenAnalysis> tokens,
        IRuntimeLexicon? lexicon = null,
        WordFormationAnalyzer? wordFormation = null,
        IReadOnlyDictionary<string, int[]>? lexicalStress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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

            string? selectedKey = null;
            IReadOnlyList<WordReading>? exactReadings = null;
            if (lexicon is not null && wordFormation?.HasReviewedExactStress == true && TryGetVowelOffset(sourceText, 1, out _))
            {
                exactReadings = BulgarianStressLookup.LookupExact(lexicon, sourceText, int.MaxValue,
                    out string normalized, cancellationToken);
                selectedKey = normalized;
                cancellationToken.ThrowIfCancellationRequested();
                int? reviewedOrdinal = wordFormation.ResolveReviewedExactStressVowelOrdinal(normalized, exactReadings);
                cancellationToken.ThrowIfCancellationRequested();
                if (reviewedOrdinal is { } ordinal &&
                    TryGetVowelOffset(sourceText, ordinal, out int offset) &&
                    !explicitOffsets.Contains(offset) &&
                    TryCreateRhymeTail(sourceText, offset, out string rhymeTail))
                {
                    reviewedStressPositions.Add(token.Start + offset);
                    reviewedRhymeTails.Add(rhymeTail);
                }
            }

            if (lexicalStress is not null)
            {
                selectedKey ??= BulgarianTextNormalizer.NormalizeKey(sourceText);
                cancellationToken.ThrowIfCancellationRequested();
                bool found = lexicalStress.TryGetValue(selectedKey, out int[]? ordinals);
                cancellationToken.ThrowIfCancellationRequested();
                if (!found && exactReadings is null && lexicon is not null &&
                    BulgarianStressLookup.TryGetSupportKey(sourceText, selectedKey, out string support))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool supportFound = lexicalStress.TryGetValue(support, out int[]? supportOrdinals);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (supportFound)
                    {
                        IReadOnlyList<WordReading> supported = BulgarianStressLookup.LookupExact(lexicon, sourceText,
                            int.MaxValue, out string actualKey, cancellationToken);
                        if (actualKey == support && supported.Count > 0)
                        {
                            found = true;
                            ordinals = supportOrdinals;
                        }
                    }
                }
                if (found)
                    foreach (int ordinal in ordinals!)
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

        cancellationToken.ThrowIfCancellationRequested();
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
        for (int index = 0; index < word.Length;)
        {
            char value = BulgarianOrthography.ReadLetter(word, index, out int length);
            if (BulgarianOrthography.IsVowel(value) && current++ == ordinal)
                offset = index;
            index += length;
        }
        return offset >= 0 && current >= minimumVowels;
    }

    private static IReadOnlyList<string> SplitWordIntoSyllables(string word)
    {
        List<string> syllables = [];
        int start = 0;
        int lastVowel = -1;

        for (int index = 0; index < word.Length;)
        {
            char value = BulgarianOrthography.ReadLetter(word, index, out int length);
            int vowelOffset = index;
            index += length;
            if (!BulgarianOrthography.IsVowel(value))
                continue;

            if (lastVowel >= 0)
            {
                int boundary = ChooseBoundary(word, lastVowel, vowelOffset);
                if (boundary > start)
                {
                    syllables.Add(word[start..boundary]);
                    start = boundary;
                }
            }

            lastVowel = vowelOffset;
        }

        if (start < word.Length)
            syllables.Add(word[start..]);

        return syllables.Count == 0 ? [word] : syllables;
    }

    private static int ChooseBoundary(string word, int previousVowel, int currentVowel)
    {
        List<int> soundOffsets = [];
        BulgarianOrthography.ReadLetter(word, previousVowel, out int previousLength);
        for (int offset = previousVowel + previousLength; offset < currentVowel;)
        {
            char value = BulgarianOrthography.ReadLetter(word, offset, out int length);
            if (!IsCombiningStress(value))
                soundOffsets.Add(offset);
            offset += length;
        }

        if (soundOffsets.Count == 0)
            return currentVowel;
        if (soundOffsets.Count == 1)
            return soundOffsets[0];
        char first = BulgarianOrthography.ReadLetter(word, soundOffsets[0], out int firstLength);
        if (char.ToLowerInvariant(first) == 'й')
            return soundOffsets[0] + firstLength;
        return soundOffsets[^2];
    }

    private static IReadOnlyList<int> FindExplicitStressOffsets(string word)
    {
        List<int> offsets = [];
        int previousVowel = -1;
        for (int index = 0; index < word.Length;)
        {
            char value = char.ToLowerInvariant(BulgarianOrthography.ReadLetter(word, index, out int length));
            if (value is 'ѐ' or 'ѝ')
                offsets.Add(index);
            else if (IsCombiningStress(value) && previousVowel >= 0)
                offsets.Add(previousVowel);
            previousVowel = BulgarianOrthography.IsVowel(value) ? index : -1;
            index += length;
        }

        return offsets.Distinct().Order().ToArray();
    }

    private static int? FindSingleVowelOffset(string word)
    {
        int? offset = null;
        for (int index = 0; index < word.Length;)
        {
            char value = BulgarianOrthography.ReadLetter(word, index, out int length);
            if (BulgarianOrthography.IsVowel(value))
            {
                if (offset is not null)
                    return null;
                offset = index;
            }
            index += length;
        }

        return offset;
    }

    private static bool TryCreateRhymeTail(
        string word,
        int stressOffset,
        out string rhymeTail)
    {
        rhymeTail = string.Empty;
        if ((uint)stressOffset >= (uint)word.Length ||
            !BulgarianOrthography.IsVowel(BulgarianOrthography.ReadLetter(word, stressOffset, out _)))
            return false;

        if (!BulgarianOrthography.IsWellFormedWord(word))
            return false;

        char[] buffer = new char[word.Length - stressOffset];
        int length = 0;
        for (int index = stressOffset; index < word.Length;)
        {
            char letter = BulgarianOrthography.ReadLetter(word, index, out int rawLength);
            index += rawLength;
            if (IsCombiningStress(letter))
                continue;

            char value = char.ToLowerInvariant(letter) switch
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

    private static bool IsCombiningStress(char character) =>
        character is '\u0300' or '\u0301';
}
