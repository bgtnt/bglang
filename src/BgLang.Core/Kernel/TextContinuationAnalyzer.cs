using System.Buffers;
using System.Globalization;
using System.Text;
using BgLang.Contracts;
using Rows = BgLang.Core.MorphologyEvidenceProjection.Rows;

namespace BgLang.Core;

/// <summary>One context-selected continuation lane over the same original morphology and exact dictionary.</summary>
internal static class TextContinuationAnalyzer
{
    internal static void ValidateCaret(string text, int caret, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if ((uint)caret > (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(caret));
        int[] boundaries = StringInfo.ParseCombiningCharacters(text);
        ct.ThrowIfCancellationRequested();
        if (caret != text.Length && Array.BinarySearch(boundaries, caret) < 0)
            throw new ArgumentException("Caret must be an original grapheme boundary.", nameof(caret));
    }

    internal static TextContinuationEvidence Calculate(TextAnalysis analysis, MorphologyEvidenceProjection.View morphology,
        IRuntimeLexicon lexicon, TextContinuationRequest request, CancellationToken ct)
    {
        ValidateCaret(analysis.Text, request.Caret, ct);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);
        if (!Enum.IsDefined(request.Target)) throw new ArgumentOutOfRangeException(nameof(request.Target));
        (int start, int length, string prefix) = Replacement(analysis.Text, request.Caret, ct);
        TextContinuationTarget target = request.Target == TextContinuationTarget.Noun
            ? TextContinuationTarget.Noun : TextContinuationTarget.Finite;
        IReadOnlyList<TextContinuationExpectation> finite = [];
        IReadOnlyList<TextNominalContinuationExpectation>? nominal = target == TextContinuationTarget.Noun ? [] : null;
        TextContinuationEvidence Unavailable(string reason) =>
            new(false, reason, request.Caret, start, length, prefix, finite, [], false, false, 0)
            { Target = target, NominalExpectations = nominal };
        string ContextReason(string reason) => target == TextContinuationTarget.Noun
            ? "nominal_attribute_" + reason : "finite_subject_" + reason;
        if (morphology.UnavailableReason is { } unavailable) return Unavailable(unavailable);
        if (length > 0 && !analysis.Tokens.Any(token => token.Kind == TokenKind.Word &&
            token.Start == start && token.Length == length))
            return Unavailable(target == TextContinuationTarget.Noun ? "nominal_edit_span_unsupported" : "finite_edit_span_unsupported");
        var preceding = morphology.OrderedTokens.Where(entry =>
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[entry.Index];
            return token.Start + token.Length <= start;
        }).ToArray();
        if (preceding.Length == 0) return Unavailable(ContextReason("context_unavailable"));
        TokenAnalysis last = analysis.Tokens[preceding[^1].Index];
        SentenceSpan? sentence = analysis.Sentences.LastOrDefault(s =>
            s.Start <= last.Start && (long)s.Start + s.Length >= (long)last.Start + last.Length);
        if (sentence is null) return Unavailable(ContextReason("sentence_unavailable"));
        var completed = preceding.Where(entry => analysis.Tokens[entry.Index].Start >= sentence.Start).ToArray();
        int cursor = sentence.Start;
        var words = new List<ContextLanes.TokenView>();
        foreach (var entry in completed)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[entry.Index];
            if (!Space(analysis.Text, cursor, token.Start, words.Count > 0, ct))
                return Unavailable(ContextReason("topology_unsupported"));
            Rows rows = entry.Rows;
            if (rows.GraphUnavailableReason is { } reason) return Unavailable(reason);
            words.Add(new(entry.Index, token, rows.Canonical, rows.Lanes, true) { UsesGraph = rows.UsesGraph });
            cursor = token.Start + token.Length;
        }
        if (!Space(analysis.Text, cursor, start, true, ct)) return Unavailable(ContextReason("topology_unsupported"));

        // Auto resolves a licensed context before touching vocabulary. Empty/unavailable/error
        // candidate results and cancellation cannot change the selected interpretation lane.
        if (request.Target != TextContinuationTarget.Noun)
            finite = FiniteExpectations(words, morphology, ct).AsReadOnly();
        if (request.Target == TextContinuationTarget.Noun || request.Target == TextContinuationTarget.Auto && finite.Count == 0)
        {
            var attributes = NominalExpectations(words, morphology, ct);
            if (request.Target == TextContinuationTarget.Noun || attributes.Count > 0)
            {
                target = TextContinuationTarget.Noun;
                nominal = attributes.AsReadOnly();
                finite = [];
            }
        }
        int expectationCount = target == TextContinuationTarget.Noun ? nominal!.Count : finite.Count;
        if (expectationCount == 0) return Unavailable(target == TextContinuationTarget.Noun
            ? "nominal_coherent_attributes_unavailable" : "finite_coherent_subject_unavailable");
        bool Eligible(MorphologicalFeatures f) => target == TextContinuationTarget.Noun
            ? IsNominal(f, MorphologicalPartOfSpeech.Noun) && f.Article == MorphologicalArticle.Indefinite : IsFinite(f);
        bool Matches(MorphologicalFeatures f, int index) => target == TextContinuationTarget.Noun
            ? f.Number == nominal![index].Number &&
                (f.Number == MorphologicalNumber.Plural || f.Gender == nominal[index].Gender)
            : f.Person == finite[index].Person && f.Number == finite[index].Number;
        bool Compatible(WordReading reading)
        {
            ct.ThrowIfCancellationRequested();
            MorphologicalFeatures f = reading.Features;
            if (!Eligible(f)) return false;
            for (int e = 0; e < expectationCount; e++)
            {
                ct.ThrowIfCancellationRequested();
                if (Matches(f, e)) return true;
            }
            return false;
        }
        PrefixDiscoveryResult found = lexicon.DiscoverByPrefix(prefix, request.Limit, Compatible, ct);
        if (!found.Available) return Unavailable(target == TextContinuationTarget.Noun
            ? "nominal_candidate_source_unavailable" : "finite_candidate_source_unavailable");
        List<TextContinuationSuggestion> suggestions = [];
        foreach (PrefixDiscoveryForm form in found.Forms)
        {
            ct.ThrowIfCancellationRequested();
            List<TextContinuationMatch> matches = [];
            for (int i = 0; i < form.Readings.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                MorphologicalFeatures f = form.Readings[i].Features;
                if (!Eligible(f)) continue;
                int[] branches = Enumerable.Range(0, expectationCount).Where(e => Matches(f, e)).ToArray();
                if (branches.Length > 0) matches.Add(new(i, Array.AsReadOnly(branches)));
            }
            if (matches.Count == 0) throw new InvalidOperationException("Discovered form has no original " +
                (target == TextContinuationTarget.Noun ? "noun" : "finite") + " support.");
            suggestions.Add(new(form.BestCompatibleReading.Form, form.BestCompatibleReading.Score,
                form.Readings, matches.AsReadOnly()));
        }
        ct.ThrowIfCancellationRequested();
        return new(true, null, request.Caret, start, length, prefix, finite,
            suggestions.AsReadOnly(), found.Truncated, found.SearchComplete, found.ExaminedReadings)
        { Target = target, NominalExpectations = nominal };
    }

    private static List<TextContinuationExpectation> FiniteExpectations(List<ContextLanes.TokenView> words,
        MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        List<TextContinuationExpectation> expectations = [];
        if (words.Count == 1 && !words[0].UsesGraph)
        {
            int[] subject = Enumerable.Range(0, words[0].Readings.Count).Where(i =>
            {
                ct.ThrowIfCancellationRequested();
                return LicensedSubject(words[0].CanonicalText, words[0].Readings[i]);
            }).ToArray();
            if (subject.Length > 0)
            {
                MorphologicalFeatures f = words[0].Readings[subject[0]].Features;
                expectations.Add(new(f.Person!.Value, f.Number!.Value,
                    Array.AsReadOnly(new[] { Support(words[0], TextContextRole.Subject, subject, morphology, ct) })));
            }
        }
        if (expectations.Count == 0 && ContextLanes.NominalMembers(words, words.Count - 1, ct) is { } nominal)
        {
            foreach (ContextLanes.NominalHead head in nominal.Heads)
            {
                ct.ThrowIfCancellationRequested();
                if (head.Attributes.Any(members => members.Length == 0)) continue;
                List<TextContextRoleSelection> supports =
                    [Support(words[^1], TextContextRole.Subject, head.Members, morphology, ct)];
                for (int a = 0; a < head.Attributes.Length; a++)
                    supports.Add(Support(words[a], TextContextRole.Attribute, head.Attributes[a], morphology, ct));
                expectations.Add(new(MorphologicalPerson.Third, head.Number, supports.AsReadOnly()));
            }
        }
        return expectations;
    }

    private static List<TextNominalContinuationExpectation> NominalExpectations(List<ContextLanes.TokenView> words,
        MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        List<TextNominalContinuationExpectation> expectations = [];
        if (words.Count == 0) return expectations;
        var firstTuples = words[0].Readings.Where(row =>
        {
            ct.ThrowIfCancellationRequested();
            return row.Category == ContextLanes.Category.Adjective && IsNominal(row.Features, MorphologicalPartOfSpeech.Adjective);
        }).Select(row => (Number: row.Features.Number!.Value,
            Gender: row.Features.Number == MorphologicalNumber.Plural ? null : row.Features.Gender,
            Article: row.Features.Article!.Value)).Distinct().ToArray();
        foreach (var tuple in firstTuples)
        {
            ct.ThrowIfCancellationRequested();
            List<TextContextRoleSelection> attributes = [];
            for (int a = 0; a < words.Count; a++)
            {
                ct.ThrowIfCancellationRequested();
                ContextLanes.TokenView word = words[a];
                MorphologicalArticle article = a == 0 ? tuple.Article : MorphologicalArticle.Indefinite;
                int[] members = Enumerable.Range(0, word.Readings.Count).Where(i =>
                {
                    ct.ThrowIfCancellationRequested();
                    var row = word.Readings[i];
                    return row.Category == ContextLanes.Category.Adjective && IsNominal(row.Features, MorphologicalPartOfSpeech.Adjective) &&
                        row.Features.Number == tuple.Number && row.Features.Article == article &&
                        (tuple.Number == MorphologicalNumber.Plural || row.Features.Gender == tuple.Gender);
                }).ToArray();
                if (members.Length == 0) break;
                attributes.Add(Support(word, TextContextRole.Attribute, members, morphology, ct, "nominal_attribute_interpretation"));
            }
            if (attributes.Count == words.Count)
                expectations.Add(new(tuple.Number, tuple.Gender, tuple.Article, MorphologicalArticle.Indefinite, attributes.AsReadOnly()));
        }
        return expectations;
    }

    private static bool IsNominal(MorphologicalFeatures f, MorphologicalPartOfSpeech category) =>
        f.PartOfSpeech == category && ContextEvidenceGuards.HasNominalAxes(f) &&
        f.Case is null && f.IsShortForm is null && (f.Article is MorphologicalArticle.Indefinite or MorphologicalArticle.Definite ||
            f.Article is MorphologicalArticle.Full or MorphologicalArticle.Short &&
            f.Number == MorphologicalNumber.Singular && f.Gender == MorphologicalGender.Masculine);

    private static bool IsFinite(MorphologicalFeatures f) => !f.HasUnrecognizedFeatures &&
        f.VerbForm == MorphologicalVerbForm.Finite && ContextEvidenceGuards.IsPersonalPredicate(f);

    // These eight exact canonical subject cells license omitted Case/Short markers.
    // Other null-feature pronouns are not inferred to be nominative or long forms.
    private static bool LicensedSubject(string canonical, ContextLanes.ReadingView row)
    {
        var license = canonical switch
        {
            "аз" => (90, MorphologicalPerson.First, MorphologicalNumber.Singular),
            "ти" => (91, MorphologicalPerson.Second, MorphologicalNumber.Singular),
            "той" => (92, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            "тя" => (93, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            "то" => (94, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            "ние" => (95, MorphologicalPerson.First, MorphologicalNumber.Plural),
            "вие" => (96, MorphologicalPerson.Second, MorphologicalNumber.Plural),
            "те" => (97, MorphologicalPerson.Third, MorphologicalNumber.Plural),
            _ => default
        };
        MorphologicalFeatures f = row.Features;
        return license.Item1 != 0 && row.Category == ContextLanes.Category.Pronoun && row.SourcePersonalPronoun &&
            f.PartOfSpeech == MorphologicalPartOfSpeech.PersonalPronoun && !f.HasConflictingFeatures && !f.HasUnrecognizedFeatures &&
            f.Person == license.Item2 && f.Number == license.Item3 && f.IsShortForm != true &&
            f.Case is null &&
            StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(row.Lemma), canonical) &&
            StringComparer.Ordinal.Equals(f.RawTag, "Лично_местоимение:" + license.Item1.ToString(CultureInfo.InvariantCulture));
    }

    private static TextContextRoleSelection Support(ContextLanes.TokenView word, TextContextRole role,
        int[] members, MorphologyEvidenceProjection.View morphology, CancellationToken ct,
        string interpretation = "finite_subject_interpretation")
    {
        Rows rows = morphology.ByToken[word.OriginalIndex];
        List<TextReadingReference> exact = [];
        List<TextGraphReadingReference> graph = [];
        foreach (int i in members)
        {
            ct.ThrowIfCancellationRequested();
            if (rows.UsesGraph)
            {
                var cell = rows.Graph[i];
                graph.Add(new(word.OriginalIndex, cell.CandidateIndex, cell.FullTagIndex));
            }
            else exact.Add(new(word.OriginalIndex, i));
        }
        return new(role, word.OriginalIndex, word.Token.Start, word.Token.Length, exact.AsReadOnly(),
            Array.AsReadOnly(new[] { rows.UsesGraph ? "licensed_graph_morphology" : "complete_exact_reading", interpretation }))
        { GraphReadings = graph.Count == 0 ? null : graph.AsReadOnly() };
    }

    private static (int Start, int Length, string Prefix) Replacement(string text, int caret, CancellationToken ct)
    {
        int start = caret, end = caret;
        while (start > 0 && Rune.DecodeLastFromUtf16(text.AsSpan(0, start), out Rune previous, out int size) == OperationStatus.Done && WordPart(previous))
        { ct.ThrowIfCancellationRequested(); start -= size; }
        if (start == caret) return (caret, 0, string.Empty);
        while (end < text.Length && Rune.DecodeFromUtf16(text.AsSpan(end), out Rune next, out int size) == OperationStatus.Done && WordPart(next))
        { ct.ThrowIfCancellationRequested(); end += size; }
        return (start, end - start, text[start..caret]);
    }

    private static bool WordPart(Rune value) => Rune.IsLetter(value) || value.Value == '-' ||
        Rune.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool Space(string text, int start, int end, bool required, CancellationToken ct)
    {
        if (start > end || required && start == end) return false;
        for (int i = start; i < end; i++) { ct.ThrowIfCancellationRequested(); if (!char.IsWhiteSpace(text[i])) return false; }
        return true;
    }
}
