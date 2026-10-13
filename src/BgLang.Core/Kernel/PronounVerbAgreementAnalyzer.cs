using BgLang.Contracts;

namespace BgLang.Core;

internal static class PronounVerbAgreementAnalyzer
{
    private const string DiagnosticCode = "pronoun_verb_agreement_mismatch";
    private const string RuleId = "pronoun_verb_agreement";

    internal static IReadOnlyList<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences,
        IRuntimeLexicon lexicon)
    {
        List<TextDiagnostic> diagnostics = [];
        Dictionary<string, IReadOnlyList<WordReading>> completeReadings = new(StringComparer.Ordinal);
        TokenAnalysis[][] sentenceWords = SentenceBoundaryDetector.GroupWordsBySentence(tokens, sentences);
        int tokenCursor = 0;
        for (int index = 0; index < sentenceWords.Length; index++)
        {
            SentenceSpan sentence = sentences[index];
            while (tokenCursor < tokens.Count && tokens[tokenCursor].Start < sentence.Start)
                tokenCursor++;
            int firstToken = tokenCursor;
            int sentenceEnd = sentence.Start + sentence.Length;
            while (tokenCursor < tokens.Count && tokens[tokenCursor].Start < sentenceEnd)
                tokenCursor++;
            if (!TrySelectBoundedClause(
                    sentenceWords[index],
                    tokens,
                    firstToken,
                    tokenCursor,
                    sentence,
                    lexicon,
                    completeReadings,
                    out AgreementFeatures subject,
                    out TokenAnalysis predicate) ||
                !TryGetUnambiguousFinitePresentAgreement(
                    predicate,
                    lexicon,
                    completeReadings,
                    out AgreementFeatures predicateFeatures) ||
                predicateFeatures == subject)
            {
                continue;
            }

            diagnostics.Add(new TextDiagnostic(
                DiagnosticCode,
                predicate.Start,
                predicate.Length,
                RuleId));
        }

        return diagnostics;
    }

    private static bool TrySelectBoundedClause(
        IReadOnlyList<TokenAnalysis> words,
        IReadOnlyList<TokenAnalysis> tokens,
        int firstToken,
        int endToken,
        SentenceSpan sentence,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> completeReadings,
        out AgreementFeatures subject,
        out TokenAnalysis predicate)
    {
        subject = default;
        predicate = null!;
        if (words.Count is < 2 or > 3 ||
            !TryGetSubjectAgreement(words[0], lexicon, completeReadings, out subject))
            return false;

        if (words.Count == 3 && !HasInterveningRole(words[1], lexicon, completeReadings))
            return false;

        predicate = words[^1];
        int sentenceEnd = sentence.Start + sentence.Length;
        int predicateStart = predicate.Start;
        int subjectEnd = words[0].Start + words[0].Length;
        for (int index = firstToken; index < endToken; index++)
        {
            TokenAnalysis token = tokens[index];
            if (token.Start >= subjectEnd && token.Start + token.Length <= predicateStart &&
                token.Start >= sentence.Start && token.Start + token.Length <= sentenceEnd &&
                token.Kind is TokenKind.Punctuation or TokenKind.Number)
                return false;
        }
        return true;
    }

    private static bool TryGetSubjectAgreement(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> completeReadings,
        out AgreementFeatures features)
    {
        string normalized = Normalized(token);
        (bool Supported, AgreementFeatures Features) subject = normalized switch
        {
            "аз" => (true, new AgreementFeatures(MorphologicalPerson.First, MorphologicalNumber.Singular)),
            "ти" => (true, new AgreementFeatures(MorphologicalPerson.Second, MorphologicalNumber.Singular)),
            "ние" => (true, new AgreementFeatures(MorphologicalPerson.First, MorphologicalNumber.Plural)),
            "вие" => (true, new AgreementFeatures(MorphologicalPerson.Second, MorphologicalNumber.Plural)),
            _ => default
        };
        features = subject.Features;
        if (!subject.Supported ||
            !TryGetCompleteReadings(token, lexicon, completeReadings, out IReadOnlyList<WordReading> readings))
            return false;

        AgreementFeatures? selected = null;
        foreach (WordReading reading in readings)
        {
            if (!IsPersonalPronoun(reading.PartOfSpeech))
                continue;
            if (!TryParsePersonalPronounAgreement(reading.PartOfSpeech, out AgreementFeatures candidate) ||
                candidate != subject.Features ||
                !string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), normalized, StringComparison.Ordinal))
                return false;
            selected = candidate;
        }

        return selected == subject.Features;
    }

    private static bool HasInterveningRole(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> completeReadings)
    {
        string requiredTag = Normalized(token) switch
        {
            "не" or "ще" or "ли" or "се" => "Частица:191",
            "го" => "Лично_местоимение:92,Винителен,4,Кратка",
            _ => string.Empty
        };
        if (requiredTag.Length == 0 ||
            !TryGetCompleteReadings(token, lexicon, completeReadings, out IReadOnlyList<WordReading> readings))
            return false;

        return readings.Any(reading => string.Equals(reading.PartOfSpeech, requiredTag, StringComparison.Ordinal));
    }

    private static bool TryGetUnambiguousFinitePresentAgreement(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> completeReadings,
        out AgreementFeatures features)
    {
        features = default;
        if (!TryGetCompleteReadings(token, lexicon, completeReadings, out IReadOnlyList<WordReading> readings))
            return false;

        AgreementFeatures? selected = null;
        foreach (WordReading reading in readings)
        {
            if (!IsVerb(reading.PartOfSpeech))
                continue;
            if (!TryParseFinitePresentAgreement(reading.PartOfSpeech, out AgreementFeatures candidate) ||
                selected is { } existing && existing != candidate)
            {
                return false;
            }

            selected = candidate;
        }

        if (selected is not { } resolved)
            return false;

        features = resolved;
        return true;
    }

    private static bool TryParseFinitePresentAgreement(
        string? fullTag,
        out AgreementFeatures features)
    {
        features = default;
        if (!IsVerb(fullTag))
            return false;
        MorphologicalFeatures projected = MorphologicalFeatures.FromTag(fullTag);
        if (projected.Tense != MorphologicalTense.Present ||
            projected.VerbForm != MorphologicalVerbForm.Finite ||
            projected.Person is not { } person ||
            projected.Number is not (MorphologicalNumber.Singular or MorphologicalNumber.Plural))
            return false;
        features = new AgreementFeatures(person, projected.Number.Value);
        return true;
    }

    private static bool TryParsePersonalPronounAgreement(string? fullTag, out AgreementFeatures features)
    {
        features = default;
        if (!IsPersonalPronoun(fullTag))
            return false;

        MorphologicalFeatures projected = MorphologicalFeatures.FromTag(fullTag);
        if (projected.Person is not { } person || projected.Number is not { } number)
            return false;
        features = new AgreementFeatures(person, number);
        return true;
    }

    private static bool IsPersonalPronoun(string? fullTag) =>
        !string.IsNullOrWhiteSpace(fullTag) &&
        fullTag.StartsWith("Лично_местоимение:", StringComparison.OrdinalIgnoreCase);

    private static bool IsVerb(string? fullTag) =>
        !string.IsNullOrWhiteSpace(fullTag) &&
        fullTag.StartsWith("Глагол:", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetCompleteReadings(
        TokenAnalysis token,
        IRuntimeLexicon lexicon,
        IDictionary<string, IReadOnlyList<WordReading>> memo,
        out IReadOnlyList<WordReading> readings)
    {
        string normalized = Normalized(token);
        if (!memo.TryGetValue(normalized, out readings!))
        {
            readings = lexicon.LookupExactNormalized(normalized, int.MaxValue);
            memo.Add(normalized, readings);
        }

        return readings.Count > 0;
    }

    /// <summary>The normalized surface already computed for the token, without recomputing it.</summary>
    private static string Normalized(TokenAnalysis token) =>
        token.Analysis?.NormalizedWord ?? BulgarianTextNormalizer.NormalizeKey(token.Text);

    private readonly record struct AgreementFeatures(MorphologicalPerson Person, MorphologicalNumber Number);
}
