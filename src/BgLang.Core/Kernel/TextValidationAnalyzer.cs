using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Source-bound advice over complete exact acquisition; never a grammatical verdict.</summary>
internal static class TextValidationAnalyzer
{
    internal const string Version = "bounded-validation.v1";
    private const string ArticleCode = "full_article_expected_for_subject_candidate";
    private const string IntensifierCode = "contextual_morphology_reading_evidence";
    private const string CommaCode = "missing_comma_before_subordinate_conjunction";
    private const string FragmentCode = "clause_missing_predicate";

    // These are the six joint source lanes. The PREP-only seventh lane cannot license a subject.
    private static bool AcceptedLane(string code) => code is
        "accepted_authority_lane=typed_first_second_pronoun_ambiguous_predicate.v1" or
        "accepted_authority_lane=closed_third_person_pronoun_nominal_verb_predicate.v1" or
        "accepted_authority_lane=closed_ambiguous_personal_pronoun_decisive_predicate.v1" or
        "accepted_authority_lane=closed_negative_particle_postverbal_full_article_subject.v1" or
        "accepted_authority_lane=closed_particle_compatible_postverbal_full_article_subject.v1" or
        "accepted_authority_lane=closed_postverbal_full_article_subject.v1";

    // exactLookup returns the complete canonical-first/support-fallback route, never display32.
    // suppressedSpans is the existing technical/notation authority, not a second detector.
    internal static TextValidationEvidence Analyze(
        TextAnalysis analysis, TextContextEvidence context, TextSyntaxEvidence syntax,
        TextContextOptions options, CancellationToken cancellationToken,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? exactLookup = null,
        IReadOnlyList<SentenceSpan>? suppressedSpans = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(syntax);
        List<string> unavailable = ["article.intransitive_animate:typed_Animacy_and_Transitivity_unavailable"];
        List<TextValidationDiagnostic> diagnostics = [];
        List<TextValidationDiagnostic>? spacing = options.IncludeSpacingDiagnostics ? [] : null;
        List<TextValidationDiagnostic>? commas = options.IncludeSubordinateCommaDiagnostics ? [] : null;
        List<TextValidationDiagnostic>? fragments = options.IncludeFragmentDiagnostics ? [] : null;
        if (spacing is not null)
            AddSpacing(analysis.Text, spacing, cancellationToken);
        if (string.IsNullOrWhiteSpace(analysis.Text))
            diagnostics.Add(new("empty_text", 0, analysis.Text.Length,
                "compact_compound_shape;empty_trimmed_source;partial_advice", Freeze<TextReadingReference>([])));
        if (analysis.ReadingInventory is null)
        {
            unavailable.Add("complete_reading_inventory_unavailable");
            return Result(false, "complete_reading_inventory_unavailable");
        }

        Dictionary<int, Word> words = Bind(analysis, cancellationToken);
        ValidateReferences(context, syntax, words, cancellationToken);
        if (!context.Available) unavailable.Add("article.context:" + (context.UnavailableReason ?? "unavailable"));
        if (!syntax.Available) unavailable.Add("article.syntax:" + (syntax.UnavailableReason ?? "unavailable"));
        if (exactLookup is null) unavailable.Add("article.coordinated:exact_counterpart_lookup_unavailable");
        if (suppressedSpans is not null)
            foreach (SentenceSpan span in suppressedSpans) RequireSpan(analysis.Text, span.Start, span.Length);

        // Preserve old deduplicated Diagnostics verbatim. Only project the two existing article facts.
        foreach (TextDiagnostic old in analysis.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (old.Code is ArticleCode or "full_short_article_check_not_ready")
            {
                RequireSpan(analysis.Text, old.Start, old.Length);
                Word[] supports = words.Values.Where(w => Overlap(old.Start, old.Length, w.Token.Start, w.Token.Length)).ToArray();
                diagnostics.Add(new(old.Code, old.Start, old.Length,
                    "existing_diagnostic=" + old.RuleId + ";bounded_article_advice", Supports(supports)));
            }
        }
        foreach (Word word in words.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (word.Token.Text.IndexOfAny(['-', '/', '.']) >= 0)
                diagnostics.AddRange(AnalyzeCompoundShape(analysis.Text, word.Token.Start, word.Token.Length, cancellationToken));
        }
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSpan(analysis.Text, sentence.Start, sentence.Length);
            Word[] sentenceWords = words.Values.Where(w => Within(sentence.Start, sentence.Length, w.Token.Start, w.Token.Length)).ToArray();
            if (syntax.Available && exactLookup is not null)
                AddCoordinatedArticle(analysis.Text, sentence, sentenceWords, context, syntax, exactLookup, diagnostics, cancellationToken);
            AddIntensifier(analysis.Text, sentenceWords, suppressedSpans, diagnostics, unavailable, cancellationToken);
            if (commas is not null)
                AddCommas(analysis.Text, sentence, sentenceWords, commas, unavailable, cancellationToken);
            if (fragments is not null)
            {
                if (!syntax.Available)
                    AddUnavailable(unavailable, "fragment:syntax_unavailable");
                else AddFragment(sentence, sentenceWords, syntax, fragments, unavailable, cancellationToken);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Result(true, null);

        TextValidationEvidence Result(bool available, string? reason) => new(Version, available, reason,
            Freeze(diagnostics), spacing is null ? null : Freeze(spacing),
            commas is null ? null : Freeze(commas), fragments is null ? null : Freeze(fragments), Freeze(unavailable));
    }

    private sealed record Word(int Index, TokenAnalysis Token, IReadOnlyList<TextReadingSnapshot> Rows);

    private static Dictionary<int, Word> Bind(TextAnalysis analysis, CancellationToken ct)
    {
        TextReadingInventory inventory = analysis.ReadingInventory!;
        Dictionary<int, Word> result = [];
        int occurrenceIndex = 0;
        for (int index = 0; index < analysis.Tokens.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[index];
            RequireSpan(analysis.Text, token.Start, token.Length);
            if (!analysis.Text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text.AsSpan()))
                throw new ArgumentException("Token does not match raw UTF-16 source.", nameof(analysis));
            if (token.Kind != TokenKind.Word) continue;
            if (occurrenceIndex >= inventory.Occurrences.Count)
                throw new ArgumentException("Missing complete Word occurrence.", nameof(analysis));
            TextReadingOccurrence occurrence = inventory.Occurrences[occurrenceIndex++];
            if (occurrence.TokenIndex != index || occurrence.Start != token.Start ||
                occurrence.Length != token.Length || !string.Equals(occurrence.Text, token.Text, StringComparison.Ordinal))
                throw new ArgumentException("Complete occurrence does not match the original Word token.", nameof(analysis));
            result.Add(index, new(index, token, inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings));
        }
        if (occurrenceIndex != inventory.Occurrences.Count)
            throw new ArgumentException("Unbound complete occurrence.", nameof(analysis));
        return result;
    }

    private static void ValidateReferences(TextContextEvidence context, TextSyntaxEvidence syntax,
        Dictionary<int, Word> words, CancellationToken ct)
    {
        void Check(IEnumerable<TextReadingReference> refs, int owner)
        {
            foreach (TextReadingReference reference in refs)
            {
                ct.ThrowIfCancellationRequested();
                if (reference.TokenIndex != owner || !words.TryGetValue(reference.TokenIndex, out Word? word) ||
                    (uint)reference.CompleteReadingIndex >= (uint)word.Rows.Count)
                    throw new ArgumentException("Context/syntax support is outside the complete original inventory.");
            }
        }
        foreach (TextContextSentence sentence in context.Sentences)
        {
            foreach (TextContextRoleSelection role in sentence.LocalRoles.Concat(sentence.SelectedRoles)) Check(role.Readings, role.TokenIndex);
            foreach (TextContextCandidate candidate in sentence.Candidates)
                foreach (TextContextRoleSelection role in candidate.Roles) Check(role.Readings, role.TokenIndex);
        }
        foreach (TextSyntaxSentence sentence in syntax.Sentences)
            foreach (TextSyntaxCandidate candidate in sentence.Candidates)
                foreach (TextSyntaxSupport support in candidate.Targets.Concat(candidate.Context)) Check(support.Readings, support.TokenIndex);
    }

    private static void AddCoordinatedArticle(string text, SentenceSpan sentence, Word[] words,
        TextContextEvidence context, TextSyntaxEvidence syntax,
        Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        List<TextValidationDiagnostic> output, CancellationToken ct)
    {
        if (words.Length < 4 || !Article(words[0], MorphologicalArticle.Short)) return;
        TextSyntaxSentence[] syntaxSentences = syntax.Sentences.Where(s => s.Start == sentence.Start && s.Length == sentence.Length).ToArray();
        if (syntaxSentences.Length != 1) return;
        TextSyntaxCandidate[] predicates = syntaxSentences[0].Candidates.Where(c => c.Role == TextSyntaxRole.Predicate).ToArray();
        if (predicates.Length != 1 || predicates[0].Targets.Count != 1 || predicates[0].Targets[0].Readings.Count == 0) return;
        int predicateIndex = predicates[0].Targets[0].TokenIndex;
        Word? predicate = words.SingleOrDefault(w => w.Index == predicateIndex);
        if (predicate is null || !All(predicate, f => f.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
            !f.HasConflictingFeatures && f.Person == MorphologicalPerson.Third && f.Number == MorphologicalNumber.Plural) ||
            predicate.Rows.Any(r => Key(r.Raw.Lemma) == "съм")) return;
        TextContextSentence[] selections = context.Available ? context.Sentences.Where(s => s.Start == sentence.Start && s.Length == sentence.Length &&
            s.Complete && (s.Status is TextContextSelectionStatus.Selected or TextContextSelectionStatus.Partial) &&
            s.SelectedPredicateTokenIndex == predicateIndex && s.EvidenceCodes.Any(AcceptedLane) &&
            s.SelectedRoles.Any(r => r.Role == TextContextRole.Predicate && r.TokenIndex == predicateIndex && r.Readings.Count > 0)).ToArray() : [];
        TextContextRoleSelection[] anchors = selections.Length == 1 ? selections[0].SelectedRoles.Where(r => r.Role == TextContextRole.Subject &&
            r.Start > words[0].Token.Start && r.Start + r.Length <= predicate.Token.Start && r.Readings.Count > 0 &&
            r.EvidenceCodes.Any(c => c is "preverbal_nominal_reading" or "postverbal_nominal_reading")).ToArray() : [];
        string anchorBasis = "accepted_context_subject_anchor";
        if (anchors.Length == 0)
        {
            if (syntaxSentences[0].Route != "SourceScan") return;
            // A basic source-scan score is not authority. This exact structural proof is the replacement.
            TextSyntaxCandidate[] basic = syntaxSentences[0].Candidates.Where(c => c.Role == TextSyntaxRole.Subject &&
                c.RuleId == "BasicSubject" && c.Targets.Count == 1 &&
                c.Targets[0].Basis is "ExactCategory" or "TypedFeatures").ToArray();
            if (basic.Length != 1) return;
            TextSyntaxSupport support = basic[0].Targets[0];
            Word? target = words.SingleOrDefault(w => w.Index == support.TokenIndex);
            if (target is null || support.Readings.Count == 0 || target.Token.Start <= words[0].Token.Start ||
                target.Token.Start + target.Token.Length > predicate.Token.Start ||
                basic[0].Start != target.Token.Start || basic[0].Length != target.Token.Length ||
                !All(target, f => !f.HasConflictingFeatures && f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                    f.Gender == MorphologicalGender.Masculine && f.Number == MorphologicalNumber.Singular && f.Article == MorphologicalArticle.Full)) return;
            anchors = [new(TextContextRole.Subject, target.Index, target.Token.Start, target.Token.Length, support.Readings, [])];
            anchorBasis = "strict_basic_subject_full_article";
        }
        if (anchors.Length != 1) return;
        TextContextRoleSelection anchor = anchors[0];
        Word[] anchorWords = words.Where(w => Within(anchor.Start, anchor.Length, w.Token.Start, w.Token.Length)).ToArray();
        if (anchorWords.Length == 0 || !anchorWords.Any(w => w.Index == anchor.TokenIndex) ||
            !Article(anchorWords[0], MorphologicalArticle.Full) || !Head(anchorWords)) return;
        Word[] before = words.Where(w => w.Token.Start < anchor.Start).ToArray();
        if (before.Length < 2) return;
        Word coordinator = before[^1];
        Word[] first = before[..^1];
        if (Key(coordinator.Token.Text) != "и" || !All(coordinator, f => f.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction) ||
            !Head(first) || !WhitespaceChain(text, first) || !WhitespaceChain(text, anchorWords) ||
            !Bridge(text, first[^1].Token.Start + first[^1].Token.Length, coordinator.Token.Start) ||
            !Bridge(text, coordinator.Token.Start + coordinator.Token.Length, anchorWords[0].Token.Start) ||
            !Bridge(text, anchorWords[^1].Token.Start + anchorWords[^1].Token.Length, predicate.Token.Start)) return;
        int coordinatedEnd = anchorWords[^1].Token.Start + anchorWords[^1].Token.Length;
        if (syntaxSentences[0].Candidates.Any(c => c.Role == TextSyntaxRole.Object &&
            Overlap(c.Start, c.Length, first[0].Token.Start, coordinatedEnd - first[0].Token.Start))) return;
        ct.ThrowIfCancellationRequested();
        string? proposal = FullCounterpart(first[0], lookup, ct);
        if (proposal is null || output.Any(d => d.Code == ArticleCode && d.Start == first[0].Token.Start && d.Length == first[0].Token.Length)) return;
        output.Add(new(ArticleCode, first[0].Token.Start, first[0].Token.Length,
            "coordinated_subject;" + anchorBasis + ";complete_all_verb_third_plural;known_full_form=" + proposal +
            ";candidate_only;no_automatic_rewrite", Supports(first.Concat([coordinator]).Concat(anchorWords).Append(predicate))));
    }

    private static bool Article(Word word, MorphologicalArticle article)
    {
        string surface = Key(word.Token.Text);
        bool shape = article == MorphologicalArticle.Short
            ? surface.EndsWith("а", StringComparison.Ordinal) || surface.EndsWith("я", StringComparison.Ordinal)
            : surface.EndsWith("ът", StringComparison.Ordinal) || surface.EndsWith("ят", StringComparison.Ordinal);
        if (!shape || !All(word, f => !f.HasConflictingFeatures && f.Gender == MorphologicalGender.Masculine &&
            f.Number == MorphologicalNumber.Singular && f.Article == article &&
            f.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective)) return false;
        MorphologicalPartOfSpeech pos = word.Rows[0].Features.PartOfSpeech;
        return word.Rows.All(r => r.Features.PartOfSpeech == pos) &&
            word.Rows.Select(r => Key(r.Raw.Lemma)).Distinct(StringComparer.Ordinal).Count() == 1 &&
            Key(word.Rows[0].Raw.Lemma).Length > 0 && word.Rows[0].Features.ParadigmId is not null &&
            word.Rows.Select(r => r.Features.ParadigmId).Distinct(StringComparer.Ordinal).Count() == 1;
    }

    private static bool Head(Word[] phrase)
    {
        if (phrase.Length == 0) return false;
        if (phrase[0].Rows[0].Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun) return phrase.Length == 1;
        return phrase.Length > 1 && All(phrase[^1], f => !f.HasConflictingFeatures &&
            f.PartOfSpeech == MorphologicalPartOfSpeech.Noun && f.Gender == MorphologicalGender.Masculine &&
            f.Number == MorphologicalNumber.Singular && f.Article == MorphologicalArticle.Indefinite) &&
            phrase.Skip(1).SkipLast(1).All(w => All(w, f => !f.HasConflictingFeatures &&
                f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && f.Gender == MorphologicalGender.Masculine &&
                f.Number == MorphologicalNumber.Singular));
    }

    private static string? FullCounterpart(Word word, Func<string, IReadOnlyList<TextReadingSnapshot>> lookup, CancellationToken ct)
    {
        string surface = Key(word.Token.Text), lemma = Key(word.Rows[0].Raw.Lemma);
        MorphologicalPartOfSpeech pos = word.Rows[0].Features.PartOfSpeech;
        string proposal;
        if (pos == MorphologicalPartOfSpeech.Adjective && surface.EndsWith("ия", StringComparison.Ordinal)) proposal = surface + "т";
        else if (pos == MorphologicalPartOfSpeech.Noun && (surface == lemma + "а" || surface == lemma + "я"))
            proposal = lemma + (surface.EndsWith("я", StringComparison.Ordinal) ? "ят" : "ът");
        else return null;
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TextReadingSnapshot> rows = lookup(proposal) ?? throw new ArgumentException("Exact callback returned null.");
        ct.ThrowIfCancellationRequested();
        if (rows.Count == 0 || rows.Any(r => Key(r.Raw.Form) != proposal || Key(r.Raw.Lemma) != lemma ||
            r.Features.HasConflictingFeatures || r.Features.PartOfSpeech != pos || r.Features.ParadigmId != word.Rows[0].Features.ParadigmId ||
            r.Features.Gender != MorphologicalGender.Masculine || r.Features.Number != MorphologicalNumber.Singular ||
            r.Features.Article != MorphologicalArticle.Full)) return null;
        return word.Token.Text.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? proposal.ToUpperInvariant() :
            char.IsUpper(word.Token.Text[0]) ? char.ToUpperInvariant(proposal[0]) + proposal[1..] : proposal;
    }

    private static void AddIntensifier(string text, Word[] words, IReadOnlyList<SentenceSpan>? suppressed,
        List<TextValidationDiagnostic> output, List<string> unavailable, CancellationToken ct)
    {
        for (int index = 0; index + 1 < words.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            Word word = words[index], head = words[index + 1];
            if (Key(word.Token.Text) is not ("един-единствен" or "една-единствена" or "едно-единствено") ||
                !head.Rows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun) ||
                !Bridge(text, word.Token.Start + word.Token.Length, head.Token.Start)) continue;
            string suppressionBasis = "technical_notation_suppression_applied";
            if (suppressed is not null)
            {
                if (suppressed.Any(s => Overlap(s.Start, s.Length, word.Token.Start, word.Token.Length) ||
                    Overlap(s.Start, s.Length, head.Token.Start, head.Token.Length))) continue;
            }
            else
            {
                // Current tokenizer already merges every known abbreviation into one Word.
                if (BulgarianAbbreviationLexicon.TryNormalizeExact(word.Token.Text.AsSpan(), out _) ||
                    BulgarianAbbreviationLexicon.TryNormalizeExact(head.Token.Text.AsSpan(), out _)) continue;
                // Source technical runs permit Cyrillic only as 1/2-character suffix tokens;
                // standalone identifiers require ASCII. The reviewed pair itself is not eligible.
                if (!SourceProvenNonTechnicalCyrillicHead(head.Token.Text, ct))
                {
                    AddUnavailable(unavailable, "intensifier:technical_notation_authority_unavailable_for_head_shape");
                    continue;
                }
                suppressionBasis = "source_proven_nontechnical_cyrillic_head;current_abbreviation_lexicon";
            }
            output.Add(new(IntensifierCode, word.Token.Start, word.Token.Length,
                "reviewed_single_word_intensifier_boundary;exact_following_noun;" + suppressionBasis + ";candidate_only;not_dictionary_truth",
                Supports([head])));
        }
    }

    private static bool SourceProvenNonTechnicalCyrillicHead(string text, CancellationToken ct)
    {
        // >2 is the complement of the source detector's 1/2-character Cyrillic suffix rule.
        if (text.Length <= 2) return false;
        foreach (char ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (!Bulgarian(ch)) return false;
        }
        return true;
    }

    private static void AddCommas(string text, SentenceSpan sentence, Word[] words,
        List<TextValidationDiagnostic> output, List<string> unavailable, CancellationToken ct)
    {
        for (int index = 1; index < words.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            Word trigger = words[index];
            if (Key(trigger.Token.Text) is not ("че" or "защото" or "когато" or "който" or "която" or "което" or "които") ||
                HasSeparatorBefore(text, sentence.Start, trigger.Token.Start)) continue;
            bool relative = trigger.Rows.Any(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun);
            bool marker = trigger.Rows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction);
            if (!relative && !marker) { AddUnavailable(unavailable, "subordinate_comma:trigger_authority_unavailable"); continue; }
            Word[] before = words[..index].Where(HasVerb).ToArray();
            Word[] after = words[(index + 1)..].Where(HasVerb).ToArray();
            if (after.Length == 0 || (!relative && before.Length == 0)) continue;
            output.Add(new(CommaCode, trigger.Token.Start, trigger.Token.Length,
                "subordinate_marker;complete_any_verb_topology;source_separator_absent;candidate_only;not_grammar_verdict",
                Supports(before.Concat([trigger]).Concat(after))));
        }
    }

    private static void AddFragment(SentenceSpan sentence, Word[] words, TextSyntaxEvidence syntax,
        List<TextValidationDiagnostic> output, List<string> unavailable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (words.Length == 0 || words.Any(HasVerb)) return;
        if (words.Any(w => w.Rows.Count == 0 || w.Rows.Any(r => r.Features.HasConflictingFeatures ||
            r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Unknown)))
        { AddUnavailable(unavailable, "fragment:predicate_absence_unproven"); return; }
        TextSyntaxSentence[] sentences = syntax.Sentences.Where(s => s.Start == sentence.Start && s.Length == sentence.Length).ToArray();
        if (sentences.Length != 1) { AddUnavailable(unavailable, "fragment:syntax_sentence_unavailable"); return; }
        if (sentences[0].Candidates.Any(c => c.Role == TextSyntaxRole.Predicate)) return;
        if (!words.Any(w => w.Rows.Any(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun)) ||
            words.Any(w => w.Rows.Any(r => r.Features.PartOfSpeech is not (MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun or
                MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Numeral or MorphologicalPartOfSpeech.Pronoun or
                MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.Preposition or MorphologicalPartOfSpeech.Conjunction)))) return;
        output.Add(new(FragmentCode, sentence.Start, sentence.Length,
            "nominal_fragment_candidate;complete_known_morphology;no_predicate_evidence;title_may_be_valid;no_automatic_rewrite",
            Supports(words)));
    }

    private static void AddSpacing(string text, List<TextValidationDiagnostic> output, CancellationToken ct)
    {
        for (int index = 0; index < text.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            char mark = text[index];
            if (mark is not (',' or ';' or ':' or '.' or '!' or '?')) continue;
            if (index > 0 && char.IsWhiteSpace(text[index - 1]))
                output.Add(new("space_before_punctuation", index, 1, "punctuation_spacing;raw_occurrence;partial_advice", Freeze<TextReadingReference>([])));
            if ((mark is ',' or ';' or ':') && index + 1 < text.Length && Bulgarian(text[index + 1]))
                output.Add(new("missing_space_after_punctuation", index, 1, "punctuation_spacing;raw_occurrence;partial_advice", Freeze<TextReadingReference>([])));
        }
    }

    /// <summary>Exact partial compact-compound span rule; a sentence is not implicitly one compound.</summary>
    internal static IReadOnlyList<TextValidationDiagnostic> AnalyzeCompoundShape(string text, int start, int length, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); RequireSpan(text, start, length);
        int first = start, end = start + length - 1;
        while (first <= end && char.IsWhiteSpace(text[first])) first++;
        while (end >= first && char.IsWhiteSpace(text[end])) end--;
        List<TextValidationDiagnostic> result = [];
        void Add(string code)
        {
            if (!result.Any(d => d.Code == code)) result.Add(new(code, start, length,
                "compact_compound_shape;explicit_source_span;partial_advice;not_dictionary_truth", Freeze<TextReadingReference>([])));
        }
        if (first > end) { Add("empty_text"); return Freeze(result); }
        char last = '\0', previous = ' ';
        for (int index = first; index <= end; index++)
        {
            ct.ThrowIfCancellationRequested(); char ch = text[index];
            if (Bulgarian(ch))
            {
                if (index > first && Bulgarian(last) && char.IsWhiteSpace(previous)) Add("word_phrase_internal_space_after_letter_run");
                last = ch;
            }
            else if (ch == '.') { if (last != '-' && !Bulgarian(last)) Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            else if (ch == '-') { if (!Bulgarian(last)) Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            else if (ch == '/') { if (last != '.' && !Bulgarian(last)) Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            else if (!char.IsWhiteSpace(ch)) { Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            previous = ch;
        }
        return Freeze(result);
    }

    private static bool HasSeparatorBefore(string text, int sentenceStart, int wordStart)
    {
        for (int index = wordStart - 1; index >= sentenceStart; index--)
        {
            char ch = text[index];
            if (char.IsWhiteSpace(ch) || ch is '"' or '\'' or '\u00AB' or '\u00BB' or '\u2018' or '\u2019' or '\u201A' or '\u201C' or '\u201D' or '\u201E') continue;
            return ch is ',' or ';' or ':' or '.' or '!' or '?' or '\u2013' or '\u2014' or '(' or '[';
        }
        return true;
    }
    private static bool HasVerb(Word word) => word.Rows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb);
    private static bool All(Word word, Func<MorphologicalFeatures, bool> predicate) => word.Rows.Count > 0 && word.Rows.All(r => predicate(r.Features));
    private static bool WhitespaceChain(string text, Word[] words) => words.Zip(words.Skip(1)).All(p => Bridge(text, p.First.Token.Start + p.First.Token.Length, p.Second.Token.Start));
    private static bool Bridge(string text, int start, int end)
    {
        if (end <= start) return false;
        for (int index = start; index < end; index++) if (!char.IsWhiteSpace(text[index])) return false;
        return true;
    }
    private static bool Bulgarian(char ch) => ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ѝ' or 'Ѝ';
    private static string Key(string value) => BulgarianTextNormalizer.NormalizeKey(value);
    private static bool Within(int start, int length, int innerStart, int innerLength) => innerStart >= start && (long)innerStart + innerLength <= (long)start + length;
    private static bool Overlap(int start, int length, int otherStart, int otherLength) => start < (long)otherStart + otherLength && otherStart < (long)start + length;
    private static void RequireSpan(string text, int start, int length)
    {
        if (start < 0 || length < 0 || start > text.Length - length) throw new ArgumentException("Invalid UTF-16 source span.");
    }
    private static IReadOnlyList<TextReadingReference> Supports(IEnumerable<Word> words) => Freeze(words
        .DistinctBy(w => w.Index).SelectMany(w => Enumerable.Range(0, w.Rows.Count).Select(index => new TextReadingReference(w.Index, index))));
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> items) => Array.AsReadOnly(items.ToArray());
    private static void AddUnavailable(List<string> output, string reason) { if (!output.Contains(reason, StringComparer.Ordinal)) output.Add(reason); }
}
