using BgLang.Contracts;

namespace BgLang.Core;

// Exact local quantity advice. No subject role, general animacy, or sense is inferred from Count.
internal static class TextCountConstructionAnalyzer
{
    private enum Denotation { Nonperson, Person }
    private sealed record Noun(string Lemma, string Count, string Plural, Denotation Denotation);
    private sealed record Numeral(string Lemma, string Paradigm, string Ordinary, string Personal,
        bool IsPersonal, bool IsDefinite);

    internal static IReadOnlyList<TextDiagnostic> Calculate(TextAnalysis analysis,
        MorphologyEvidenceProjection.View morphology,
        Func<string, IReadOnlyList<TextReadingSnapshot>> exactLookup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(exactLookup);
        // The shared quotation owner may narrow this later. Until then suppress the entire quoted document,
        // including continuations after a sentence boundary; do not install a second quote parser here.
        if (morphology.UnavailableReason is not null || analysis.Text.AsSpan().ContainsAny("\"'„“”«»‘’‚`")) return [];
        List<TextDiagnostic> output = [];
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            var words = NominalStructuralProjection.SentenceWords(analysis, sentence, morphology, ct);
            for (int i = 0; i + 1 < words.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                ContextLanes.TokenView numeral = words[i];
                Numeral? form = NumeralForm(numeral.CanonicalText);
                if (form is null || !Exact(numeral) || UnsupportedLeft(i)) continue;
                int head = i + 1;
                List<int[]> modifierMembers = [];
                bool modifierUsesFiniteContext = false;
                while (head < words.Count && Exact(words[head]) && Adjacent(head - 1, head))
                {
                    int[]? members = QuantityModifierMembers(words[head], i > 0, out bool usesContext, ct);
                    if (members is null) break;
                    modifierMembers.Add(members);
                    modifierUsesFiniteContext |= usesContext;
                    head++;
                }
                if (head >= words.Count || !Adjacent(head - 1, head) || !Exact(words[head])) continue;
                ContextLanes.TokenView noun = words[head];
                Noun? lexical = ReviewedNoun(noun.Readings[0].Lemma);
                if (lexical is null || !NounRows(noun.Readings, lexical, out string? paradigm)) continue;
                // Companion rows are considered only inside this independently bound local quantity frame.
                int[]? numeralMembers = NumeralMembers(numeral.Readings, form, numeral.CanonicalText, numeral.Token.Text,
                    i > 0, out bool finiteContextUsed, ct);
                if (numeralMembers is null) continue;
                finiteContextUsed |= modifierUsesFiniteContext;
                MorphologicalNumber expected = lexical.Denotation == Denotation.Person
                    ? MorphologicalNumber.Plural : MorphologicalNumber.Count;
                string wantedNumeral = lexical.Denotation == Denotation.Person ? form.Personal : form.Ordinary;
                string wantedNoun = expected == MorphologicalNumber.Count ? lexical.Count : lexical.Plural;
                TextReadingReference[] supports = words.Skip(i).Take(head - i + 1).SelectMany((w, slot) =>
                    (slot == 0 ? numeralMembers.AsEnumerable() : slot < head - i
                        ? modifierMembers[slot - 1].AsEnumerable() : Enumerable.Range(0, w.Readings.Count))
                        .Select(r => new TextReadingReference(w.OriginalIndex, r))).ToArray();
                // Both obligations are independent. C04 must retain both targets and both projections.
                if (numeral.CanonicalText != wantedNumeral && CounterpartNumeral(wantedNumeral))
                    Add(numeral, "personal_numeral_selection_mismatch", "grammar.count.reviewed_numeral_selection.v1", wantedNumeral);
                if (!noun.Readings.Any(r => r.Features.Number == expected && r.Features.Article == MorphologicalArticle.Indefinite) &&
                    CounterpartNoun(wantedNoun))
                    Add(noun, "quantified_noun_form_mismatch", "grammar.count.reviewed_noun_form.v1", wantedNoun);

                bool CounterpartNumeral(string key)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = exactLookup(key) ?? throw new ArgumentException("Exact counterpart callback returned null.");
                    ct.ThrowIfCancellationRequested();
                    Numeral? target = NumeralForm(key);
                    if (target is null || rows.Count == 0 || rows.Any(r => Key(r.Raw.Form) != key || r.Features != r.Raw.Features)) return false;
                    int[]? targetMembers = NumeralMembers(rows.Select(r => ContextReadingProjection.Create(r, key).Lane).ToArray(), target, key,
                        CaseLike(numeral.Token.Text, key), i > 0, out bool targetUsesFiniteContext, ct);
                    if (targetMembers is null) return false;
                    finiteContextUsed |= targetUsesFiniteContext;
                    return true;
                }
                bool CounterpartNoun(string key)
                {
                    ct.ThrowIfCancellationRequested();
                    var rows = exactLookup(key) ?? throw new ArgumentException("Exact counterpart callback returned null.");
                    ct.ThrowIfCancellationRequested();
                    var lanes = rows.Select(r => ContextReadingProjection.Create(r, key).Lane).ToArray();
                    return rows.Count > 0 && rows.All(r => Key(r.Raw.Form) == key && r.Features == r.Raw.Features) &&
                        NounRows(lanes, lexical, out string? targetParadigm) && targetParadigm == paradigm &&
                        rows.Any(r => r.Features.Number == expected && r.Features.Article == MorphologicalArticle.Indefinite);
                }
                void Add(ContextLanes.TokenView target, string code, string rule, string replacement)
                {
                    // Cite the original complete governor only when a real finite companion needed it.
                    IEnumerable<TextReadingReference> proof = finiteContextUsed
                        ? Enumerable.Range(0, words[i - 1].Readings.Count).Select(r => new TextReadingReference(words[i - 1].OriginalIndex, r)).Concat(supports)
                        : supports;
                    output.Add(new(code, target.Token.Start, target.Token.Length, rule)
                        { ProjectedText = CaseLike(target.Token.Text, replacement), Readings = Array.AsReadOnly(proof.ToArray()) });
                }
                bool Adjacent(int left, int right) => Whitespace(analysis.Text,
                    words[left].Token.Start + words[left].Token.Length, words[right].Token.Start);
                bool UnsupportedLeft(int index)
                {
                    // Supported left boundaries are NP start or an adjacent complete finite predicate.
                    // All original left rows must qualify; a nominal/label competitor cannot be discarded.
                    int previousIndex = numeral.OriginalIndex - 1;
                    while (previousIndex >= 0 && analysis.Tokens[previousIndex].Kind == TokenKind.Whitespace) previousIndex--;
                    if (previousIndex < 0 || analysis.Tokens[previousIndex].Start < sentence.Start) return index != 0;
                    TokenAnalysis previous = analysis.Tokens[previousIndex];
                    if (previous.Text.Any(char.IsDigit) || previous.Kind != TokenKind.Word || index == 0) return true;
                    ContextLanes.TokenView left = words[index - 1];
                    return !Exact(left) || !Whitespace(analysis.Text, previous.Start + previous.Length, numeral.Token.Start) ||
                        left.Readings.Any(r => r.Features.HasUnrecognizedFeatures ||
                            !ContextEvidenceGuards.IsPersonalPredicate(r.Features) ||
                            r.Features.VerbForm != MorphologicalVerbForm.Finite);
                }
            }
        }
        ct.ThrowIfCancellationRequested();
        return Array.AsReadOnly(output.OrderBy(d => d.Start).ThenBy(d => d.Code, StringComparer.Ordinal).ToArray());
    }

    // Reviewed lexical scope: BERON стол/компютър and IBE consultation ученици, mapped by the oracle.
    // The full inflection is supplied by complete package rows. Meaning strings and suffixes are not classifiers.
    private static Noun? ReviewedNoun(string lemma) => Key(lemma) switch
    {
        "стол" => new("стол", "стола", "столове", Denotation.Nonperson),
        "компютър" => new("компютър", "компютъра", "компютри", Denotation.Nonperson),
        "ученик" => new("ученик", "ученика", "ученици", Denotation.Person),
        _ => null
    };

    private static bool NounRows(IReadOnlyList<ContextLanes.ReadingView> rows, Noun noun, out string? paradigm)
    {
        paradigm = rows.Count == 0 ? null : rows[0].Features.ParadigmId;
        string? identity = paradigm;
        return identity is not null && rows.Count > 0 && rows.All(r => Key(r.Lemma) == noun.Lemma &&
            !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures &&
            r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun && r.Features.ParadigmId == identity &&
            r.Features.Gender == MorphologicalGender.Masculine &&
            r.Features.Number is MorphologicalNumber.Singular or MorphologicalNumber.Plural or MorphologicalNumber.Count &&
            (r.Features.Article == MorphologicalArticle.Indefinite ||
                // The accepted source retains raw Членуван as Definite for masculine short/count homographs.
                // Admit only the reviewed same count surface, never a plural definite noun or a full article.
                r.Features.Number == MorphologicalNumber.Singular &&
                r.Features.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite &&
                r.Reading is { } raw && Key(raw.Form) == noun.Count));
    }

    private static Numeral? NumeralForm(string key)
    {
        // Simple 2–6 only. Compound alternatives are deliberately not interpreted by this lane.
        (string Lemma, string Paradigm, string Ordinary, string Personal) tuple = key switch
        {
            "два" or "две" or "двама" or "двата" or "двете" or "двамата" => ("два", "132", "два", "двама"),
            "три" or "трима" or "трите" or "тримата" => ("три", "133", "три", "трима"),
            "четири" or "четирима" or "четирите" or "четиримата" => ("четири", "134", "четири", "четирима"),
            "пет" or "петима" or "петте" or "петимата" => ("пет", "134", "пет", "петима"),
            "шест" or "шестима" or "шестте" or "шестимата" => ("шест", "134", "шест", "шестима"),
            _ => default
        };
        if (tuple.Lemma is null) return null;
        bool personal = key == tuple.Personal || key == tuple.Personal + "та";
        bool definite = key is "двата" or "двете" || key == tuple.Ordinary + "те" || key == tuple.Personal + "та";
        string ordinarySuffix = tuple.Lemma == "два" ? "та" : "те";
        return new(tuple.Lemma, tuple.Paradigm, tuple.Ordinary + (definite ? ordinarySuffix : ""),
            tuple.Personal + (definite ? "та" : ""), personal, definite);
    }

    private static int[]? NumeralMembers(IReadOnlyList<ContextLanes.ReadingView> rows, Numeral numeral,
        string surface, string spelling, bool finiteContext, out bool usesFiniteContext, CancellationToken ct)
    {
        usesFiniteContext = false;
        List<int> members = [];
        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ContextLanes.ReadingView row = rows[i];
            if (NumeralRow(row, numeral)) { members.Add(i); continue; }
            if (ReviewedTriFinite(row, surface))
            {
                if (!finiteContext) return null;
                usesFiniteContext = true;
                continue;
            }
            MorphologicalFeatures f = row.Features;
            if (Key(row.Lemma) != surface || f.HasConflictingFeatures) return null;
            // Exact reviewed source identities, not arbitrary unknown/name alternatives.
            // These remain in the inventory and never become numeral proof.
            if (surface is "двама" or "трима" && row.SourceGenericUnknown &&
                f.PartOfSpeech == MorphologicalPartOfSpeech.Unknown && f.RawTag == "unknown:500") continue;
            if (surface == "три" && spelling.Any(char.IsLetter) &&
                spelling.All(c => !char.IsLetter(c) || char.IsLower(c)) && f.RawTag == "Място:195" &&
                f.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun && f.NameKind == MorphologicalNameKind.PlaceName &&
                !f.HasUnrecognizedFeatures) continue;
            return null;
        }
        return members.Count == 0 ? null : members.ToArray();
    }

    private static int[]? QuantityModifierMembers(ContextLanes.TokenView word, bool finiteContext,
        out bool usesFiniteContext, CancellationToken ct)
    {
        usesFiniteContext = false;
        List<int> members = [];
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ContextLanes.ReadingView row = word.Readings[i];
            MorphologicalFeatures f = row.Features;
            if (f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && ContextEvidenceGuards.HasNominalAxes(f) &&
                f.Number == MorphologicalNumber.Plural && f.Article == MorphologicalArticle.Indefinite)
            { members.Add(i); continue; }
            if (finiteContext && word.CanonicalText == "нови" && Key(row.Lemma) == "нови" &&
                word.Token.Text.All(c => !char.IsLetter(c) || char.IsLower(c)) && f.RawTag == "Място:195" &&
                f.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun && f.NameKind == MorphologicalNameKind.PlaceName &&
                !f.HasUnrecognizedFeatures && !f.HasConflictingFeatures)
            { usesFiniteContext = true; continue; }
            return null;
        }
        if (members.Count == 0 || usesFiniteContext && members.Any(i => Key(word.Readings[i].Lemma) != "нов" ||
            word.Readings[i].Features.RawTag != "Прилагателно:76,Мъжки,мн.ч.")) return null;
        return members.ToArray();
    }

    private static bool ReviewedTriFinite(ContextLanes.ReadingView row, string surface)
    {
        MorphologicalFeatures f = row.Features;
        return surface == "три" && Key(row.Lemma) == "трия" &&
            !f.HasConflictingFeatures && !f.HasUnrecognizedFeatures && f.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
            f.ParadigmId == "163ti" && f.Number == MorphologicalNumber.Singular && f.Tense == MorphologicalTense.Aorist &&
            f.VerbForm == MorphologicalVerbForm.Finite &&
            (f.Person == MorphologicalPerson.Second && f.RawTag == "Глагол:163ti,2л.,Минало_свършено" ||
             f.Person == MorphologicalPerson.Third && f.RawTag == "Глагол:163ti,3л.,Минало_свършено");
    }

    private static bool NumeralRow(ContextLanes.ReadingView r, Numeral numeral)
    {
        MorphologicalFeatures f = r.Features;
        if (Key(r.Lemma) != numeral.Lemma || f.PartOfSpeech != MorphologicalPartOfSpeech.Numeral ||
            f.ParadigmId != numeral.Paradigm || f.HasConflictingFeatures || f.RawTag is null || f.Gender is null ||
            numeral.IsPersonal && f.Gender != MorphologicalGender.Masculine) return false;
        string[] atoms = f.RawTag.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Consume only this source's explicit personal/definite atoms; Count never means personal.
        return atoms[0].Equals("Бройно_Числително:" + numeral.Paradigm, StringComparison.OrdinalIgnoreCase) &&
            atoms.Skip(1).All(a => a is "Мъжки" or "Женски" or "Среден" or "мн.ч." or "Мъжколична" or "Членуван") &&
            atoms.Contains("Мъжколична", StringComparer.Ordinal) == numeral.IsPersonal &&
            atoms.Contains("Членуван", StringComparer.Ordinal) == numeral.IsDefinite;
    }

    private static bool Exact(ContextLanes.TokenView word) => word.Complete && !word.UsesGraph && word.Readings.Count > 0 &&
        word.Readings.All(r => r.Reading is { } raw && Key(raw.Form) == word.CanonicalText && raw.Features == r.Features);
    private static bool Whitespace(string text, int start, int end)
    {
        if (end <= start) return false;
        for (int i = start; i < end; i++) if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
    private static string Key(string text) => BulgarianTextNormalizer.NormalizeKey(text);
    private static string CaseLike(string original, string replacement) => original.All(c => !char.IsLetter(c) || char.IsUpper(c))
        ? replacement.ToUpperInvariant() : char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;
}
