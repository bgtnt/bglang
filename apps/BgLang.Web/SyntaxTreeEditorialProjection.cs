using System.Globalization;
using System.Text;
using BgLang.Contracts;

namespace BgLang.Web;

/// <summary>Compact word metadata and explicit edit proposals from one existing AnalyzeText result.</summary>
internal static class SyntaxTreeEditorialProjection
{
    // The existing Lab collectChecks replacement policy. Other projected evidence is not an edit instruction.
    private static readonly HashSet<string> ReplaceableCodes = new(StringComparer.Ordinal)
    {
        "spelling_unknown_word_unique_distance_one_candidate", "joined_verb_negation_should_be_separate",
        "accent_sensitive_i_grave", "mixed_script_ocr_known_candidate", "digit_shape_ocr_known_candidate"
    };

    internal static SyntaxTreeEditorialResult Build(TextAnalysis analysis)
    {
        IReadOnlyList<TextReadingSnapshot>[] readings = analysis.Tokens.Select(_ =>
            (IReadOnlyList<TextReadingSnapshot>)Array.Empty<TextReadingSnapshot>()).ToArray();
        bool[] acquired = new bool[readings.Length];
        string[][] lookupKeys = analysis.Tokens.Select(_ => Array.Empty<string>()).ToArray();
        foreach (TextReadingOccurrence occurrence in analysis.ReadingInventory?.Occurrences ?? [])
        {
            if ((uint)occurrence.TokenIndex >= (uint)analysis.Tokens.Count) continue;
            TokenAnalysis token = analysis.Tokens[occurrence.TokenIndex];
            if (token.Text != occurrence.Text || token.Start != occurrence.Start || token.Length != occurrence.Length) continue;
            TextReadingSet set = analysis.ReadingInventory!.Sets[occurrence.ReadingSetIndex];
            readings[occurrence.TokenIndex] = set.ResolvedReadings;
            lookupKeys[occurrence.TokenIndex] = set.SupportKey is { } support ? [set.CanonicalKey, support] : [set.CanonicalKey];
            acquired[occurrence.TokenIndex] = true;
        }
        for (int index = 0; index < readings.Length; index++)
            if (!acquired[index] && analysis.Tokens[index].Analysis is { } word)
            {
                readings[index] = word.Readings.Select(row => new TextReadingSnapshot(row, row.Features)).ToArray();
                lookupKeys[index] = [word.NormalizedWord];
                acquired[index] = true;
            }
        var metadata = new Dictionary<IReadOnlyList<TextReadingSnapshot>, SyntaxTreeWordInfo>(ReferenceEqualityComparer.Instance);
        SyntaxTreeWordInfo[] words = readings.Select((rows, index) =>
        {
            if (!acquired[index]) return new SyntaxTreeWordInfo(null, [], [], []);
            if (!metadata.TryGetValue(rows, out SyntaxTreeWordInfo? info))
                metadata.Add(rows, info = new(rows.Count > 0,
                    rows.Select(row => row.Features.PartOfSpeech.ToString()).Distinct(StringComparer.Ordinal).ToArray(),
                    rows.Select(row => row.Raw.Lemma).Distinct(StringComparer.Ordinal).ToArray(),
                    rows.Select(row => row.Raw.PartOfSpeech).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()));
            return info;
        }).ToArray();
        List<SyntaxTreeSuggestion> suggestions = [];
        List<SyntaxTreeDiagnostic> conflictWarnings = [];
        HashSet<int> boundaries = StringInfo.ParseCombiningCharacters(analysis.Text).Append(analysis.Text.Length).ToHashSet();
        var wordsBySpan = analysis.Tokens.Select((token, index) => (token, index)).Where(item => item.token.Kind == TokenKind.Word)
            .ToLookup(item => (item.token.Start, item.token.Length));

        for (int index = 0; index < analysis.Diagnostics.Count; index++)
        {
            TextDiagnostic diagnostic = analysis.Diagnostics[index];
            if (!ReplaceableCodes.Contains(diagnostic.Code) || string.IsNullOrEmpty(diagnostic.ProjectedText) ||
                !ValidSpan(diagnostic.Start, diagnostic.Length)) continue;
            string original = analysis.Text.Substring(diagnostic.Start, diagnostic.Length);
            string replacement = MatchCase(original, diagnostic.ProjectedText);
            if (replacement == original) continue;
            int? editedToken = wordsBySpan[(diagnostic.Start, diagnostic.Length)].Select(item => (int?)item.index).FirstOrDefault();
            int[] participants = (editedToken is { } edited ? new[] { edited } : [])
                .Concat((diagnostic.Readings ?? []).Select(row => row.TokenIndex))
                .Concat((diagnostic.GraphReadings ?? []).Select(row => row.TokenIndex))
                .Where(IsWord).Distinct().ToArray();
            bool conditional = diagnostic.Code == "spelling_unknown_word_unique_distance_one_candidate";
            suggestions.Add(new($"diagnostic:{index}", $"diagnostic:{index}", "diagnostic", diagnostic.RuleId,
                diagnostic.Code, diagnostic.Start, diagnostic.Length, participants, conditional, "projected", [],
                [new(editedToken, diagnostic.Start, diagnostic.Length, original, replacement)]));
        }

        if (analysis.Spelling is { } spelling)
            foreach (SpellingWordEvidence word in spelling.Words)
            {
                if (!BoundWord(word.TokenIndex, word.Start, word.Length) || analysis.Tokens[word.TokenIndex].Text != word.Text) continue;
                var contextual = (word.ContextualCandidates ?? []).ToLookup(row => row.AlternativeIndex);
                var ranks = (word.ContextualCandidates ?? []).Select((candidate, rank) => (candidate.AlternativeIndex, rank))
                    .ToLookup(item => item.AlternativeIndex);
                int[] order = Enumerable.Range(0, word.Alternatives.Count).OrderBy(index => word.Alternatives[index].Distance)
                    .ThenBy(index => ranks[index].Select(item => item.rank).FirstOrDefault(int.MaxValue))
                    .ThenBy(index => index).ToArray();
                foreach (int index in order)
                {
                    FuzzyMatch alternative = word.Alternatives[index];
                    if (alternative.Distance < 0 || string.IsNullOrEmpty(alternative.Form)) continue;
                    string replacement = MatchCase(word.Text, alternative.Form);
                    if (replacement == word.Text) continue;
                    SpellingContextCandidate? candidate = contextual[index].FirstOrDefault();
                    int[] context = candidate?.Assessment == SpellingContextAssessment.Supported
                        ? candidate.Matches.Where(match => match.FollowingNounReading.TokenIndex > word.TokenIndex &&
                                ValidReading(match.FollowingNounReading) && (uint)match.CandidateReadingIndex < (uint)alternative.Readings.Count)
                            .Select(match => match.FollowingNounReading.TokenIndex)
                            .Concat((candidate.PreviousModifierMatches ?? []).Where(match =>
                                    match.PreviousModifierReading.TokenIndex < word.TokenIndex && ValidReading(match.PreviousModifierReading) &&
                                    (uint)match.CandidateReadingIndex < (uint)alternative.Readings.Count)
                                .Select(match => match.PreviousModifierReading.TokenIndex)).Distinct().ToArray() : [];
                    string assessment = context.Length > 0 ? "Supported" :
                        candidate?.Assessment == SpellingContextAssessment.NoCompatiblePair ? "NoCompatiblePair" : "Unassessed";
                    suggestions.Add(new($"spelling:{word.TokenIndex}:{index}", $"spelling:{word.TokenIndex}", "spelling", null,
                        null, word.Start, word.Length, [word.TokenIndex, .. context], true, assessment, [],
                        [new(word.TokenIndex, word.Start, word.Length, word.Text, replacement)]) { Distance = alternative.Distance });
                }
            }

        if (analysis.NominalConstructions is { Available: true } nominal)
            for (int reviewIndex = 0; reviewIndex < nominal.Reviews.Count; reviewIndex++)
            {
                NominalRepairReview review = nominal.Reviews[reviewIndex];
                if (!review.NoChangeRetained || !BoundWord(review.TokenIndex, review.Start, review.Length) ||
                    review.Alternatives.Any(row => row.Alternative.Distance < 0 || string.IsNullOrEmpty(row.Alternative.Form))) continue;
                bool[] support = review.Alternatives.Select(row => row.Alternative.Distance > 0 && SupportedNominal(row, review)).ToArray();
                if (!support.Any(value => value)) continue;
                IEnumerable<int> order = Enumerable.Range(0, review.Alternatives.Count);
                if (review.ContextualOrder is { } proposed && proposed.Count == review.Alternatives.Count &&
                    proposed.Distinct().Count() == proposed.Count && proposed.All(i => (uint)i < (uint)review.Alternatives.Count) &&
                    proposed.Zip(proposed.Skip(1)).All(pair => review.Alternatives[pair.First].Alternative.Distance <=
                        review.Alternatives[pair.Second].Alternative.Distance)) order = proposed;
                foreach (int index in order)
                {
                    NominalRepairAlternative row = review.Alternatives[index];
                    if (row.Alternative.Distance == 0) continue;
                    TokenAnalysis token = analysis.Tokens[review.TokenIndex];
                    string replacement = MatchCase(token.Text, row.Alternative.Form);
                    if (replacement == token.Text) continue;
                    bool supported = support[index];
                    suggestions.Add(new($"nominal:{reviewIndex}:{index}", $"nominal:{reviewIndex}", "nominal", null,
                        null, review.Start, review.Length, supported ? [review.TokenIndex, row.Phrase!.HeadTokenIndex] : [review.TokenIndex],
                        true, supported ? "Supported" : "Unassessed", [],
                        [new(review.TokenIndex, review.Start, review.Length, token.Text, replacement)])
                        { Distance = row.Alternative.Distance });
                }
            }

        if (analysis.Grammar is { } grammar)
        {
            var rules = grammar.Rules.ToLookup(rule => rule.Id, StringComparer.Ordinal);
            for (int matchIndex = 0; matchIndex < grammar.Matches.Count; matchIndex++)
            {
                TextGrammarMatch match = grammar.Matches[matchIndex];
                TextGrammarRule? rule = rules[match.RuleId].FirstOrDefault();
                if (rule is null || match.Assessment is not (TextGrammarAssessment.Mismatch or TextGrammarAssessment.Ambiguous) ||
                    !ValidSpan(match.Start, match.Length) || !BoundConflicts(match)) continue;
                // The core reports this conflict even though it cannot choose which subject was intended.
                if (match.Assessment == TextGrammarAssessment.Ambiguous && match.Reason == "disjoint_subject_predicate_locus" &&
                    match.DependentTokenIndices.Count == 1 && match.ControllerTokenIndices.Distinct().Count() >= 2 &&
                    match.Conflicts.Count >= 2)
                {
                    TokenAnalysis predicate = analysis.Tokens[match.DependentTokenIndices[0]];
                    conflictWarnings.Add(new(predicate.Start, predicate.Length, match.Reason, rule.Id, rule.Explanation));
                }
                for (int repairIndex = 0; repairIndex < match.Repairs.Count; repairIndex++)
                {
                    TextGrammarRepair repair = match.Repairs[repairIndex];
                    TextGrammarEdit[] ordered = repair.Edits.OrderBy(edit => edit.Start).ToArray();
                    if (ordered.Length == 0 || ordered.Any(edit => !BoundWord(edit.TokenIndex, edit.Start, edit.Length) ||
                            string.IsNullOrEmpty(edit.Replacement) || edit.Start < match.Start ||
                            (long)edit.Start + edit.Length > (long)match.Start + match.Length) ||
                        ordered.Zip(ordered.Skip(1)).Any(pair => (long)pair.First.Start + pair.First.Length > pair.Second.Start)) continue;
                    SyntaxTreeEdit[] edits = ordered.Select(edit => new SyntaxTreeEdit(edit.TokenIndex, edit.Start, edit.Length,
                        analysis.Tokens[edit.TokenIndex].Text, edit.Replacement)).ToArray();
                    if (edits.All(edit => edit.Original == edit.Replacement)) continue;
                    suggestions.Add(new($"grammar:{matchIndex}:{repairIndex}", $"grammar:{matchIndex}", "grammar", match.RuleId,
                        rule.DiagnosticCode, match.Start, match.Length,
                        match.ControllerTokenIndices.Concat(match.DependentTokenIndices).Distinct().ToArray(), true,
                        match.Assessment.ToString(), repair.Qualifications, edits));
                }
            }
        }
        return new(words, suggestions, conflictWarnings);

        bool ValidSpan(int start, int length) => length > 0 && start >= 0 && start <= analysis.Text.Length - length &&
            boundaries.Contains(start) && boundaries.Contains(start + length);
        bool IsWord(int index) => (uint)index < (uint)analysis.Tokens.Count && analysis.Tokens[index].Kind == TokenKind.Word;
        bool BoundWord(int index, int start, int length) => IsWord(index) && ValidSpan(start, length) &&
            analysis.Tokens[index].Start == start && analysis.Tokens[index].Length == length &&
            analysis.Text.AsSpan(start, length).SequenceEqual(analysis.Tokens[index].Text.AsSpan());
        bool ValidReading(TextReadingReference reference) => IsWord(reference.TokenIndex) &&
            (uint)reference.CompleteReadingIndex < (uint)readings[reference.TokenIndex].Count;
        bool BoundConflicts(TextGrammarMatch match) => match.ControllerTokenIndices.Count > 0 && match.DependentTokenIndices.Count > 0 &&
            match.ControllerTokenIndices.All(index => IsWord(index) && BoundWord(index, analysis.Tokens[index].Start, analysis.Tokens[index].Length)) &&
            match.DependentTokenIndices.All(index => IsWord(index) && BoundWord(index, analysis.Tokens[index].Start, analysis.Tokens[index].Length) &&
                analysis.Tokens[index].Start >= match.Start && (long)analysis.Tokens[index].Start + analysis.Tokens[index].Length <= (long)match.Start + match.Length) &&
            match.Conflicts.Count > 0 && match.Conflicts.All(conflict => match.DependentTokenIndices.Contains(conflict.TokenIndex) &&
                conflict.Actual.Count > 0 && conflict.ExpectedFrom.Count > 0 &&
                conflict.Actual.All(row => row.TokenIndex == conflict.TokenIndex && ValidReading(row)) &&
                conflict.ExpectedFrom.All(row => match.ControllerTokenIndices.Contains(row.TokenIndex) && ValidReading(row)));
        bool SupportedNominal(NominalRepairAlternative row, NominalRepairReview review)
        {
            TextNominalPhrase? phrase = row.Phrase;
            if (phrase is not { Assessment: NominalAgreementAssessment.Compatible } || !ValidSpan(phrase.Start, phrase.Length) ||
                !IsWord(phrase.HeadTokenIndex) || phrase.Start > review.Start ||
                (long)review.Start + review.Length > (long)phrase.Start + phrase.Length) return false;
            TokenAnalysis headToken = analysis.Tokens[phrase.HeadTokenIndex];
            if (!BoundWord(phrase.HeadTokenIndex, headToken.Start, headToken.Length) || headToken.Start < phrase.Start ||
                (long)headToken.Start + headToken.Length > (long)phrase.Start + phrase.Length) return false;
            return phrase.HeadSupports.Any(head => SupportedHead(head, phrase.HeadTokenIndex) && head.Members.Count > 0 &&
                head.Members.All(member => member.Assessment == NominalAgreementAssessment.Compatible) &&
                head.Members.Any(member => member.TokenIndex == review.TokenIndex && member.ReplacementReadings is { } replacements &&
                    replacements.Any(index => (uint)index < (uint)row.Alternative.Readings.Count)));
        }
        bool SupportedHead(NominalHeadSupport head, int tokenIndex)
        {
            if (head.Reading is { } exact)
            {
                if (head.GraphReading is not null || exact.TokenIndex != tokenIndex || !ValidReading(exact)) return false;
                MorphologicalFeatures features = readings[tokenIndex][exact.CompleteReadingIndex].Features;
                return features.PartOfSpeech == MorphologicalPartOfSpeech.Noun && !features.HasConflictingFeatures &&
                    !features.HasUnrecognizedFeatures && (features.Number == MorphologicalNumber.Plural ||
                        features.Number == MorphologicalNumber.Singular && features.Gender is
                            MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter);
            }
            if (head.GraphReading is not { } graph || graph.TokenIndex != tokenIndex || readings[tokenIndex].Count != 0 ||
                analysis.Tokens[tokenIndex].Analysis is not { GraphSearchIncomplete: false, GraphExactRootCandidatesComplete: true } word ||
                (uint)graph.GraphCandidateIndex >= (uint)word.GraphCandidates.Count) return false;
            WordDerivation path = word.GraphCandidates[graph.GraphCandidateIndex];
            return lookupKeys[tokenIndex].Contains(path.Word, StringComparer.Ordinal) &&
                !path.IsConditional && path.ConditionalRootProgramId is null && path.ConditionalRootBaseFullTag is null &&
                path.ConditionalRootSupportCount is null && path.ConditionalSupportingLemmas is null &&
                path.UnmetExactSelfReadingRequirements is not { Count: > 0 } && !string.IsNullOrWhiteSpace(path.DerivedLemma) &&
                path.FullTags is { } tags && (uint)graph.FullTagIndex < (uint)tags.Count && !string.IsNullOrWhiteSpace(tags[graph.FullTagIndex]);
        }
    }

    private static string MatchCase(string original, string form)
    {
        Rune[]? source = Letters(original, false), candidate = Letters(form, true);
        if (source is not { Length: >= 2 } || candidate is not { Length: > 0 } || candidate.Any(rune => !Rune.IsLower(rune))) return form;
        bool caps = source.All(Rune.IsUpper), title = Rune.IsUpper(source[0]) && source.Skip(1).All(Rune.IsLower);
        if (!caps && !title) return form;
        StringBuilder result = new(); int index = 0;
        foreach (Rune rune in form.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                result.Append(rune.ToString());
            else result.Append((caps || index++ == 0 ? Rune.ToUpperInvariant(rune) : rune).ToString());
        }
        return result.ToString();

        static Rune[]? Letters(string text, bool cyrillic)
        {
            List<Rune> letters = [];
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (letters.Count > 0 && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
                if (!(Rune.IsUpper(rune) || Rune.IsLower(rune)) || cyrillic && rune.Value is not (>= 0x0400 and <= 0x052F)) return null;
                letters.Add(rune);
            }
            return letters.ToArray();
        }
    }
}

internal sealed record SyntaxTreeEditorialResult(IReadOnlyList<SyntaxTreeWordInfo> Words,
    IReadOnlyList<SyntaxTreeSuggestion> Suggestions, IReadOnlyList<SyntaxTreeDiagnostic> ConflictWarnings);
internal sealed record SyntaxTreeWordInfo(bool? Known, IReadOnlyList<string> Pos, IReadOnlyList<string> Lemmas,
    IReadOnlyList<string> Tags);
internal sealed record SyntaxTreeSuggestion(string Id, string Group, string Source, string? RuleId, string? Code,
    int Start, int Length, IReadOnlyList<int> TokenIndices, bool Conditional, string Assessment,
    IReadOnlyList<string> Qualifications, IReadOnlyList<SyntaxTreeEdit> Edits)
{
    public string Kind => Source;
    public int? Distance { get; init; }
}
internal sealed record SyntaxTreeEdit(int? TokenIndex, int Start, int Length, string Original, string Replacement);
