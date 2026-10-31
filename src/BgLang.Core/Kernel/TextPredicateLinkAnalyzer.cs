using BgLang.Contracts;
using Guard = BgLang.Core.ContextEvidenceGuards;

namespace BgLang.Core;

/// <summary>Local reading tuples, separate from the legacy first-predicate selection.</summary>
internal static class TextPredicateLinkAnalyzer
{
    internal const string Code = "unseparated_finite_predications";
    private const string Rule = "grammar.closed_pronoun_two_present_predications.v1";
    private sealed record Word(int Index, TokenAnalysis Token, MorphologyEvidenceProjection.Rows Rows);

    internal static (TextPredicateLinkEvidence Evidence, IReadOnlyList<TextDiagnostic> Diagnostics) Calculate(
        TextAnalysis analysis, MorphologyEvidenceProjection.View morphology, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (morphology.UnavailableReason is { } unavailable)
            return (new("local-predicate-links.v1", false, unavailable, []), []);
        List<TextPredicateSentence> sentences = [];
        List<TextDiagnostic> diagnostics = [];
        var quoted = Quotes(analysis.Text, ct);
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            if (sentence.Start < 0 || sentence.Length < 0 || (long)sentence.Start + sentence.Length > analysis.Text.Length)
                throw new ArgumentException("Predicate sentence is outside the original text.");
            int end = sentence.Start + sentence.Length;
            Word[] words = morphology.OrderedTokens.Where(w => analysis.Tokens[w.Index].Start >= sentence.Start &&
                analysis.Tokens[w.Index].Start + analysis.Tokens[w.Index].Length <= end)
                .Select(w => new Word(w.Index, analysis.Tokens[w.Index], w.Rows)).ToArray();
            List<string> reasons = [];
            bool quote = quoted.Any(q => q.Start < end && q.End > sentence.Start);
            if (quote) reasons.Add("quoted_scope_unsupported");
            foreach (Word word in words)
            {
                ct.ThrowIfCancellationRequested();
                if (word.Rows.Count == 0) reasons.Add("morphology_unavailable:token=" + word.Index);
                if (word.Rows.GraphUnavailableReason is { } why) reasons.Add(why + ":token=" + word.Index);
                if (word.Rows.Lanes.Any(r => r.SourceGenericUnknown || r.Features.HasUnrecognizedFeatures || r.Features.HasConflictingFeatures))
                    reasons.Add("unresolved_morphology:token=" + word.Index);
            }
            var relevant = words.Where(w => w.Rows.Lanes.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb) ||
                w.Rows.Canonical == "ще" && w.Rows.Lanes.Any(FutureMarker)).ToArray();
            List<TextPredicateLink> links = [];
            HashSet<(int Token, int Row)> linked = [];
            if (!quote)
                for (int i = 0; i < words.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int j = i + 1; j < words.Length && j <= i + 3; j++)
                    {
                        Word left = words[i], right = words[j];
                        string? kind = null;
                        var markers = new List<(Word Word, int Row)>();
                        if (j == i + 1)
                        {
                            string bridge = analysis.Text[(left.Token.Start + left.Token.Length)..right.Token.Start];
                            if (bridge.Trim(' ', '\t') == ",") kind = "comma_separation";
                            else if (Horizontal(bridge) && left.Rows.Canonical == "ще") kind = "future_marker";
                            else if (Horizontal(bridge)) kind = "auxiliary_participle";
                        }
                        else if (BridgesHorizontal(analysis.Text, words, i, j))
                        {
                            if (j == i + 2 && words[i + 1].Rows.Canonical is "и" or "да")
                            {
                                Word marker = words[i + 1];
                                kind = marker.Rows.Canonical == "и" ? "coordinating_conjunction" : "da_boundary";
                                AddMarker(marker);
                            }
                            else if (j == i + 3 && words[i + 1].Rows.Canonical == "и" && words[i + 2].Rows.Canonical == "да")
                            {
                                kind = "coordinated_da_boundary";
                                AddMarker(words[i + 1]); AddMarker(words[i + 2]);
                            }
                        }
                        if (kind is null || (j > i + 1 && markers.Select(m => m.Word.Index).Distinct().Count() != j - i - 1)) continue;
                        // Each emitted tuple cites real rows. Marker alternatives are not silently selected.
                        for (int a = 0; a < left.Rows.Count; a++)
                            for (int b = 0; b < right.Rows.Count; b++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var l = left.Rows.Lanes[a]; var r = right.Rows.Lanes[b];
                                bool accepted = kind switch
                                {
                                    "future_marker" => FutureMarker(l) && Finite(r) && r.Features.Tense == MorphologicalTense.Present,
                                    "auxiliary_participle" => Finite(l) && CopularLemma(l.Lemma) &&
                                        r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Verb, VerbForm: MorphologicalVerbForm.Participle,
                                            HasConflictingFeatures: false, HasUnrecognizedFeatures: false } &&
                                        l.Features.Number == r.Features.Number,
                                    _ => Finite(l) && Finite(r)
                                };
                                if (!accepted) continue;
                                if (markers.Count == 0)
                                {
                                    var punctuation = kind == "comma_separation" ? analysis.Tokens.Select((t, n) => (t, n))
                                        .Where(x => x.t.Kind == TokenKind.Punctuation && x.t.Text == "," &&
                                            x.t.Start >= left.Token.Start + left.Token.Length && x.t.Start < right.Token.Start)
                                        .Select(x => new TextSyntaxSupport(x.n, "observed_comma", [])).ToArray() : [];
                                    Add(punctuation);
                                }
                                else if (j == i + 2)
                                    foreach (var marker in markers) Add([Support(marker.Word, marker.Row)]);
                                else
                                    foreach (var first in markers.Where(m => m.Word.Index == words[i + 1].Index))
                                        foreach (var second in markers.Where(m => m.Word.Index == words[i + 2].Index))
                                            Add([Support(first.Word, first.Row), Support(second.Word, second.Row)]);
                                void Add(IReadOnlyList<TextSyntaxSupport> supports)
                                {
                                    links.Add(new(kind, Support(left, a), Support(right, b), Freeze(supports)));
                                    linked.Add((left.Index, a)); linked.Add((right.Index, b));
                                }
                            }
                        void AddMarker(Word marker)
                        {
                            for (int row = 0; row < marker.Rows.Count; row++)
                            {
                                var reading = marker.Rows.Lanes[row];
                                if (!reading.Features.HasConflictingFeatures && !reading.Features.HasUnrecognizedFeatures &&
                                    (marker.Rows.Canonical == "и" && reading.SourceConjunction ||
                                     marker.Rows.Canonical == "да" && (reading.SourceConjunction || reading.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle)))
                                    markers.Add((marker, row));
                            }
                        }
                    }
                }
            bool conflict = !quote && ClosedConflict(analysis.Text, sentence, words, ct);
            if (conflict)
            {
                Word last = words[2];
                var exact = words.SelectMany(w => Enumerable.Range(0, w.Rows.Count).Where(_ => !w.Rows.UsesGraph)
                    .Select(r => new TextReadingReference(w.Index, r))).ToArray();
                var graph = words.SelectMany(w => Enumerable.Range(0, w.Rows.Count).Where(_ => w.Rows.UsesGraph)
                    .Select(r => GraphReference(w, r))).ToArray();
                diagnostics.Add(new(Code, last.Token.Start, last.Token.Length, Rule)
                {
                    Readings = Freeze(exact), GraphReadings = graph.Length == 0 ? null : Freeze(graph)
                });
            }
            else reasons.Add("closed_conflict_check_not_applicable");
            List<TextPredicateOccurrence> occurrences = [];
            foreach (Word word in relevant)
            {
                List<TextPredicateReading> readings = [];
                for (int row = 0; row < word.Rows.Count; row++)
                {
                    ct.ThrowIfCancellationRequested();
                    var reading = word.Rows.Lanes[row];
                    string kind = Kind(reading);
                    string coverage = conflict ? "conflict_support" : linked.Contains((word.Index, row)) ? "local_link_candidate" :
                        kind == "other" ? "competing_alternative" : kind == "finite" && relevant.Length == 1 ? "isolated_finite_candidate" : "uncovered";
                    if (coverage == "uncovered") reasons.Add("predicate_reading_uncovered:token=" + word.Index + ":row=" + row);
                    readings.Add(new(kind, coverage, Auxiliary(reading.Lemma),
                        word.Rows.UsesGraph ? null : new TextReadingReference(word.Index, row),
                        word.Rows.UsesGraph ? GraphReference(word, row) : null));
                }
                occurrences.Add(new(word.Index, word.Token.Start, word.Token.Length, Freeze(readings)));
            }
            sentences.Add(new(sentence.Start, sentence.Length, Freeze(occurrences), Freeze(links),
                Freeze(reasons.Distinct(StringComparer.Ordinal)), conflict));
        }
        return (new("local-predicate-links.v1", true, null, Freeze(sentences)), Freeze(diagnostics));

        TextSyntaxSupport Support(Word word, int row)
        {
            if (!word.Rows.UsesGraph) return new(word.Index, "original_complete_reading", [new(word.Index, row)]);
            TextGraphReadingReference reference = GraphReference(word, row);
            _ = MorphologyEvidenceProjection.RequireGraphSupport(word.Token, reference, word.Index, 0, ct);
            return new(word.Index, "licensed_exact_root_graph_cell", []) { GraphReadings = [reference] };
        }
    }

    private static bool ClosedConflict(string text, SentenceSpan sentence, Word[] words, CancellationToken ct)
    {
        if (words.Length != 3 || words.Any(w => w.Rows.Count == 0 || w.Rows.GraphUnavailableReason is not null) ||
            !Horizontal(text[sentence.Start..words[0].Token.Start], allowEmpty: true) ||
            !BridgesHorizontal(text, words, 0, 2) ||
            text[(words[2].Token.Start + words[2].Token.Length)..(sentence.Start + sentence.Length)].Trim(' ', '\t') != ".") return false;
        Word subject = words[0];
        if (!subject.Rows.Lanes.All(r => Guard.LicensedSubject(subject.Rows.Canonical, r))) return false;
        var tuple = subject.Rows.Lanes[0].Features;
        foreach (Word word in words.Skip(1))
            foreach (var row in word.Rows.Lanes)
            {
                ct.ThrowIfCancellationRequested();
                // Positive topology: two measured lexical predications of the overt subject.
                // BERON85 supplies separation authority only after these exhaustive guards.
                if (!Finite(row) || row.Features.Tense != MorphologicalTense.Present || Auxiliary(row.Lemma) ||
                    !Guard.PersonNumberTupleMatches((tuple.Person, tuple.Number), (row.Features.Person, row.Features.Number))) return false;
            }
        // Repetition may have a discourse reading, outside this declarative slice.
        var firstLemmas = words[1].Rows.Lanes.Select(r => BulgarianTextNormalizer.NormalizeKey(r.Lemma)).ToHashSet(StringComparer.Ordinal);
        return !words[2].Rows.Lanes.Any(r => firstLemmas.Contains(BulgarianTextNormalizer.NormalizeKey(r.Lemma)));
    }

    private static bool Finite(ContextLanes.ReadingView row) =>
        row.Category == ContextLanes.Category.Verb && !row.SourceGenericUnknown && !row.Features.HasUnrecognizedFeatures &&
        Guard.IsPersonalPredicate(row.Features) && row.Features.VerbForm == MorphologicalVerbForm.Finite;
    private static string Kind(ContextLanes.ReadingView row) => row.Features.PartOfSpeech != MorphologicalPartOfSpeech.Verb ? "other" :
        Finite(row) ? "finite" : row.Features.HasConflictingFeatures || row.Features.HasUnrecognizedFeatures ? "unmeasured_verb" : row.Features.VerbForm switch
        {
            MorphologicalVerbForm.Imperative => "imperative", MorphologicalVerbForm.Participle => "participle",
            MorphologicalVerbForm.Converb => "converb", _ => "unmeasured_verb"
        };
    // Candidates, never selected lexical senses. These source lemmas can head compound/copular forms.
    private static bool Auxiliary(string lemma) => BulgarianTextNormalizer.NormalizeKey(lemma) is "съм" or "бъда" or "ща" or "имам" or "нямам";
    private static bool CopularLemma(string lemma) => BulgarianTextNormalizer.NormalizeKey(lemma) is "съм" or "бъда";
    private static bool FutureMarker(ContextLanes.ReadingView row) =>
        !row.Features.HasConflictingFeatures && !row.Features.HasUnrecognizedFeatures && row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle;
    private static TextGraphReadingReference GraphReference(Word word, int row) =>
        new(word.Index, word.Rows.Graph[row].CandidateIndex, word.Rows.Graph[row].FullTagIndex);
    private static bool BridgesHorizontal(string text, Word[] words, int first, int last)
    {
        for (int i = first; i < last; i++)
            if (!Horizontal(text[(words[i].Token.Start + words[i].Token.Length)..words[i + 1].Token.Start])) return false;
        return true;
    }
    private static bool Horizontal(string value, bool allowEmpty = false) =>
        (allowEmpty || value.Length > 0) && value.All(c => c is ' ' or '\t');
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> rows) => Array.AsReadOnly(rows.ToArray());
    private static List<(int Start, int End)> Quotes(string text, CancellationToken ct)
    {
        List<(int, int)> spans = [];
        Stack<char> closing = new(); int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            char c = text[i];
            if (closing.Count > 0 && c == closing.Peek())
            {
                closing.Pop();
                if (closing.Count == 0) spans.Add((start, i + 1));
                continue;
            }
            char close = c switch
            {
                '„' => '“', '“' => '”', '«' => '»', '‘' => '’', '‚' => '‘',
                _ => SentenceBoundaryDetector.IsSentenceQuote(c) ? c : '\0'
            };
            if (close == '\0') continue;
            // Nested/open or unsupported unmatched wrappers keep the outer scope protected.
            if (closing.Count == 0) start = i;
            closing.Push(close);
        }
        if (closing.Count > 0) spans.Add((start, text.Length));
        return spans;
    }
}
