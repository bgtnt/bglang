using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Projects existing complete morphology and local NP evidence; it does not discover a second parse or enumerate sentence reading products.</summary>
public static class TextConstructionProjector
{
    public const string Version = "local-construction-projection.v2";

    public static TextConstructionProjection Project(TextAnalysis source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = source.ReadingInventory;
        Dictionary<int, TextReadingSet> readings = [];
        if (inventory is not null)
        foreach (var occurrence in inventory.Occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)occurrence.TokenIndex >= (uint)source.Tokens.Count ||
                (uint)occurrence.ReadingSetIndex >= (uint)inventory.Sets.Count) throw Invalid();
            var token = source.Tokens[occurrence.TokenIndex];
            if (token.Kind != TokenKind.Word || token.Start != occurrence.Start || token.Length != occurrence.Length ||
                token.Text != occurrence.Text || !readings.TryAdd(occurrence.TokenIndex, inventory.Sets[occurrence.ReadingSetIndex])) throw Invalid();
        }
        int previousEnd = 0;
        foreach (var token in source.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Start < previousEnd || token.Length <= 0 || (long)token.Start + token.Length > source.Text.Length ||
                !source.Text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text)) throw Invalid();
            previousEnd = token.Start + token.Length;
        }
        List<ConstructionSentence> sentences = [];
        List<SentenceSpan> uncovered = [];
        previousEnd = 0;
        foreach (var sentence in source.Sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = checked(sentence.Start + sentence.Length);
            if (sentence.Start < previousEnd || sentence.Length < 0 || end > source.Text.Length) throw Invalid();
            if (HasMaterial(previousEnd, sentence.Start)) uncovered.Add(new(previousEnd, sentence.Start - previousEnd));
            previousEnd = end;
            var issues = new SortedSet<string>(StringComparer.Ordinal) { "clause_links_not_assessed", "valency_not_assessed" };
            if (inventory is null) issues.Add("complete_reading_inventory_unavailable");
            if (source.NominalConstructions is not { Available: true }) issues.Add("nominal_projection_unavailable");
            List<ConstructionSlot> slots = [];
            var phrases = (source.NominalConstructions is { Available: true } nominal ? nominal.Phrases : [])
                .Where(p => p.Start >= sentence.Start && (long)p.Start + p.Length <= end).OrderBy(p => p.Start).ToArray();
            int phraseIndex = 0, cursor = sentence.Start;
            for (int index = 0; index < source.Tokens.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = source.Tokens[index];
                if (token.Start < sentence.Start || token.Start >= end) continue;
                if (token.Start + token.Length > end) throw Invalid();
                if (token.Start < cursor) continue;
                AddGap(cursor, token.Start);
                while (phraseIndex < phrases.Length && phrases[phraseIndex].Start < token.Start) phraseIndex++;
                if (phraseIndex < phrases.Length && phrases[phraseIndex].Start == token.Start &&
                    Eligible(phrases[phraseIndex], out var references, out bool ambiguous, out bool nonNominal))
                {
                    var phrase = phrases[phraseIndex++];
                    int phraseEnd = phrase.Start + phrase.Length;
                    var members = Enumerable.Range(index, source.Tokens.Count - index)
                        .TakeWhile(i => source.Tokens[i].Start < phraseEnd).ToArray();
                    if (members.Any(i => source.Tokens[i].Start + source.Tokens[i].Length > phraseEnd)) throw Invalid();
                    var links = phrase.ConnectorTokenIndices.Select(i => source.Tokens[i].Text == "," ? "modifier_enumeration" : "modifier_coordination").Distinct(StringComparer.Ordinal).ToArray();
                    if (ambiguous) issues.Add("nominal_alternatives_factored_not_selected");
                    if (nonNominal)
                    {
                        issues.Add("nominal_non_np_alternatives_factored");
                        issues.Add("non_np_branch_not_indexed_by_coarse_signature");
                    }
                    slots.Add(new("NP", phrase.Start, phrase.Length, Array.AsReadOnly(members),
                        Array.AsReadOnly(references), Array.AsReadOnly(new[] { "NP" }), phrase, ambiguous, Array.AsReadOnly(links))
                    {
                        HasNonNominalAlternatives = nonNominal,
                        FactoredTokens = Array.AsReadOnly(members.Where(i => source.Tokens[i].Kind == TokenKind.Word)
                            .Select(i => new ConstructionTokenAlternatives(i,
                                Array.AsReadOnly(Resolved(readings[i]).Select(Symbol).ToArray()))).ToArray())
                    });
                    cursor = phraseEnd;
                    continue;
                }
                if (token.Kind != TokenKind.Whitespace)
                {
                    var refs = References(index);
                    var alternatives = refs.Select(r => Symbol(Resolved(readings[index])[r.CompleteReadingIndex]))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    if (token.Kind != TokenKind.Word) alternatives = [token.Kind == TokenKind.Punctuation ? "P:" + token.Text : token.Kind.ToString().ToUpperInvariant()];
                    if (alternatives.Length == 0) { alternatives = ["UNK"]; issues.Add("word_without_complete_exact_readings"); }
                    bool ambiguousWord = refs.Length > 1;
                    if (alternatives.Contains("ADJ:full", StringComparer.Ordinal)) issues.Add("nominalized_adjective_not_assessed");
                    foreach (var r in refs)
                    {
                        var f = Resolved(readings[index])[r.CompleteReadingIndex].Features;
                        if (f.HasUnrecognizedFeatures || f.HasConflictingFeatures || f.PartOfSpeech == MorphologicalPartOfSpeech.Unknown) issues.Add("unprojected_or_conflicting_morphology");
                    }
                    string symbol = alternatives.Length == 1 ? alternatives[0] : "{" + string.Join('|', alternatives) + "}";
                    slots.Add(new(symbol, token.Start, token.Length, Array.AsReadOnly(new[] { index }),
                        Array.AsReadOnly(refs), Array.AsReadOnly(alternatives), null, ambiguousWord, Array.Empty<string>()));
                }
                cursor = token.Start + token.Length;
            }
            AddGap(cursor, end);
            sentences.Add(new(sentence.Start, sentence.Length, slots.AsReadOnly(),
                Array.AsReadOnly(new[] { "BOS" }.Concat(slots.Select(s => s.Symbol)).Append("EOS").ToArray()),
                slots.Any(s => s.HasAmbiguity), Array.AsReadOnly(issues.ToArray())));

            void AddGap(int start, int stop)
            {
                if (stop < start) throw Invalid();
                bool material = false;
                for (int at = start; at < stop; at++) { cancellationToken.ThrowIfCancellationRequested(); material |= !char.IsWhiteSpace(source.Text[at]); }
                if (!material) return;
                issues.Add("uncovered_nonwhitespace_material");
                slots.Add(new("GAP", start, stop - start, Array.Empty<int>(), Array.Empty<TextReadingReference>(),
                    Array.AsReadOnly(new[] { "GAP" }), null, true, Array.Empty<string>()));
            }
        }
        if (HasMaterial(previousEnd, source.Text.Length)) uncovered.Add(new(previousEnd, source.Text.Length - previousEnd));
        return new(Version, source, sentences.AsReadOnly(), uncovered.AsReadOnly());

        TextReadingReference[] References(int tokenIndex) => readings.TryGetValue(tokenIndex, out var set)
            ? Enumerable.Range(0, Resolved(set).Count).Select(i => new TextReadingReference(tokenIndex, i)).ToArray() : [];

        bool Eligible(TextNominalPhrase phrase, out TextReadingReference[] references, out bool ambiguous, out bool nonNominal)
        {
            references = []; ambiguous = false; nonNominal = false;
            if (phrase.Length <= 0 || phrase.Assessment != NominalAgreementAssessment.Compatible ||
                (uint)phrase.HeadTokenIndex >= (uint)source.Tokens.Count) return false;
            int[] participants = phrase.ModifierTokenIndices.Append(phrase.HeadTokenIndex).ToArray();
            if (participants.Distinct().Count() != participants.Length) throw Invalid();
            foreach (int i in participants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)i >= (uint)source.Tokens.Count || source.Tokens[i].Kind != TokenKind.Word ||
                    source.Tokens[i].Start < phrase.Start || source.Tokens[i].Start + source.Tokens[i].Length > phrase.Start + phrase.Length) throw Invalid();
                if (!readings.TryGetValue(i, out var set) || Resolved(set).Count == 0) return false;
                ambiguous |= Resolved(set).Count > 1;
                foreach (var row in Resolved(set))
                {
                    var f = row.Features;
                    bool nominalRole = i == phrase.HeadTokenIndex
                        ? f.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun
                        : f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                            f.PartOfSpeech == MorphologicalPartOfSpeech.Verb && f.VerbForm == MorphologicalVerbForm.Participle;
                    nonNominal |= !nominalRole; // Preserve the viable raw branch, rather than selecting the NP.
                }
            }
            // Reuse a complete existing coherent tuple. Never synthesize a tuple from feature unions.
            bool coherent = false;
            foreach (var head in phrase.HeadSupports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (head.Reading is not { } h || h.TokenIndex != phrase.HeadTokenIndex || head.GraphReading is not null) continue;
                Require(h);
                if (head.Members.Count != phrase.ModifierTokenIndices.Count ||
                    !head.Members.Select(m => m.TokenIndex).Order().SequenceEqual(phrase.ModifierTokenIndices.Order())) continue;
                bool all = true;
                foreach (var member in head.Members)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!phrase.ModifierTokenIndices.Contains(member.TokenIndex) || member.Assessment != NominalAgreementAssessment.Compatible ||
                        member.Readings.Count == 0 || member.GraphReadings.Count != 0 || member.ReplacementReadings is not null) { all = false; break; }
                    foreach (var r in member.Readings) { if (r.TokenIndex != member.TokenIndex) throw Invalid(); Require(r); }
                }
                coherent |= all;
            }
            if (!coherent) return false;
            foreach (int connector in phrase.ConnectorTokenIndices)
                if ((uint)connector >= (uint)source.Tokens.Count) throw Invalid();
            var allowed = participants.Concat(phrase.ConnectorTokenIndices).ToHashSet();
            int covered = phrase.Start;
            for (int i = 0; i < source.Tokens.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var t = source.Tokens[i];
                if (t.Start < phrase.Start || t.Start >= phrase.Start + phrase.Length) continue;
                if (HasMaterial(covered, t.Start) || t.Start + t.Length > phrase.Start + phrase.Length) return false;
                if (t.Kind != TokenKind.Whitespace && !allowed.Contains(i)) return false;
                if (t.Kind == TokenKind.Word)
                {
                    if (!readings.TryGetValue(i, out var tokenSet) || Resolved(tokenSet).Count == 0) return false;
                    ambiguous |= Resolved(tokenSet).Count > 1;
                    if (!participants.Contains(i)) nonNominal |= Resolved(tokenSet).Any(r => r.Features.PartOfSpeech != MorphologicalPartOfSpeech.Conjunction);
                }
                covered = t.Start + t.Length;
            }
            if (HasMaterial(covered, phrase.Start + phrase.Length)) return false;
            references = participants.Concat(phrase.ConnectorTokenIndices).Distinct().Order()
                .SelectMany(References).ToArray();
            return true;
        }
        bool HasMaterial(int start, int stop)
        {
            for (int at = start; at < stop; at++) { cancellationToken.ThrowIfCancellationRequested(); if (!char.IsWhiteSpace(source.Text[at])) return true; }
            return false;
        }
        void Require(TextReadingReference r)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!readings.TryGetValue(r.TokenIndex, out var set) || (uint)r.CompleteReadingIndex >= (uint)Resolved(set).Count) throw Invalid();
        }
    }

    private static IReadOnlyList<TextReadingSnapshot> Resolved(TextReadingSet set) => set.SupportReadings ?? set.CanonicalReadings;
    private static ArgumentException Invalid() => new("Construction evidence is not aligned with the original source/inventory.");

    private static string Symbol(TextReadingSnapshot row)
    {
        var f = row.Features;
        string lemma = BulgarianTextNormalizer.NormalizeKey(row.Raw.Lemma);
        if (lemma == "не" && f.PartOfSpeech == MorphologicalPartOfSpeech.Particle) return "NEG";
        if (lemma == "да" && f.PartOfSpeech is MorphologicalPartOfSpeech.Particle or MorphologicalPartOfSpeech.Conjunction) return "LINK:da";
        if (f.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction)
            return lemma switch { "и" or "или" or "а" or "но" => "LINK:coord:" + lemma,
                "че" or "ако" or "когато" or "защото" or "понеже" or "докато" => "LINK:sub:" + lemma, _ => "LINK:" + lemma };
        return f.PartOfSpeech switch
        {
            MorphologicalPartOfSpeech.Verb => "V:" + (f.VerbForm?.ToString() ?? "unassessed") + (lemma is "съм" or "бъда" ? ":be" : ""),
            MorphologicalPartOfSpeech.PersonalPronoun => "PRON:" + (f.IsShortForm == true ? "clitic:" : "") + (f.Case?.ToString() ?? "unmarked"),
            MorphologicalPartOfSpeech.Pronoun => "PRON:other",
            MorphologicalPartOfSpeech.Preposition => "PREP:" + lemma,
            MorphologicalPartOfSpeech.Adjective => "ADJ" + (f.Article == MorphologicalArticle.Full ? ":full" : ""),
            MorphologicalPartOfSpeech.Noun => "N", MorphologicalPartOfSpeech.ProperNoun => "PROPN",
            MorphologicalPartOfSpeech.Numeral => "NUM", MorphologicalPartOfSpeech.Adverb => "ADV",
            MorphologicalPartOfSpeech.Unknown => "UNK", _ => f.PartOfSpeech.ToString().ToUpperInvariant()
        };
    }
}
