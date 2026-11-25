using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Source-bound advice over complete exact acquisition; never a grammatical verdict.</summary>
internal static class TextValidationAnalyzer
{
    internal const string Version = "bounded-validation.v2";
    private const string ArticleCode = "full_article_expected_for_subject_candidate";
    private const string PrepositionalArticleCode = "short_article_expected_after_preposition_candidate";
    private const string PrepositionalArticleRule = NominalGrammarRules.ShortArticleId;
    private const string LocalArticleCode = "article_prepositional_group_locally_assessed";
    private const string ArticlePlacementCode = "nominal_article_on_noninitial_member";
    private const string ArticlePlacementRule = NominalGrammarRules.PrepositionalPlacementId;
    private const string InitialNominalArticleRule = NominalGrammarRules.InitialPlacementId;
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
        "accepted_authority_lane=closed_postverbal_full_article_subject.v1" or
        "accepted_authority_lane=" + ClauseFusionAnalyzer.SelectionLane;

    // exactLookup returns the complete canonical-first/support-fallback route, never display32.
    // suppressedSpans is the existing technical/notation authority, not a second detector.
    internal static TextValidationEvidence Analyze(
        TextAnalysis analysis, TextContextEvidence context, TextSyntaxEvidence syntax,
        TextContextOptions options, CancellationToken cancellationToken,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? exactLookup = null,
        IReadOnlyList<SentenceSpan>? suppressedSpans = null,
        IReadOnlyList<TextValidationDiagnostic>? localArticleFacts = null,
        ClauseFusionAnalyzer.DirectObjectFrame? directObjectFrame = null,
        SimpleClauseAgreementResolver.VerbReadingAuthority? verbAuthority = null,
        VerbFrameCatalog? verbFrames = null,
        double postverbalDirectObjectMaxRatio = VerbFrameCatalog.DefaultPostverbalDirectObjectMaxRatio)
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
        ValidateReferences(analysis, context, syntax, words, options, cancellationToken,
            directObjectFrame, exactLookup, verbAuthority, verbFrames, postverbalDirectObjectMaxRatio);
        if (!context.Available) unavailable.Add("article.context:" + (context.UnavailableReason ?? "unavailable"));
        if (!syntax.Available) unavailable.Add("article.syntax:" + (syntax.UnavailableReason ?? "unavailable"));
        if (exactLookup is null) unavailable.Add("article.coordinated:exact_counterpart_lookup_unavailable");
        if (suppressedSpans is not null)
            foreach (SentenceSpan span in suppressedSpans) RequireSpan(analysis.Text, span.Start, span.Length);

        // Project source-bound article diagnostics into validation evidence.
        foreach (TextDiagnostic old in analysis.Diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (old.Code is ArticleCode or PrepositionalArticleCode or ArticlePlacementCode or "full_short_article_check_not_ready")
            {
                RequireSpan(analysis.Text, old.Start, old.Length);
                bool exactArticle = old.Code is PrepositionalArticleCode or ArticlePlacementCode ||
                    old.Code == ArticleCode && old.RuleId == ClauseFusionAnalyzer.SubjectArticleRule;
                if (exactArticle)
                {
                    bool expectedShape = old.Code == PrepositionalArticleCode
                        ? old.RuleId == PrepositionalArticleRule && !string.IsNullOrEmpty(old.ProjectedText)
                        : old.Code == ArticleCode
                            ? old.RuleId == ClauseFusionAnalyzer.SubjectArticleRule && !string.IsNullOrEmpty(old.ProjectedText)
                            : old.RuleId is ArticlePlacementRule or InitialNominalArticleRule && old.ProjectedText is null;
                    if (!expectedShape || old.Readings is not { Count: > 0 } || old.GraphReadings is { Count: > 0 })
                        throw new ArgumentException("Unbound prepositional article diagnostic.");
                    foreach (TextReadingReference reference in old.Readings)
                        if (!words.TryGetValue(reference.TokenIndex, out Word? owner) ||
                            (uint)reference.CompleteReadingIndex >= (uint)owner.Rows.Count ||
                            old.Code == ArticleCode && !Overlap(old.Start, old.Length, owner.Token.Start, owner.Token.Length) ||
                            !analysis.Sentences.Any(s => Within(s.Start, s.Length, old.Start, old.Length) &&
                                Within(s.Start, s.Length, owner.Token.Start, owner.Token.Length)))
                            throw new ArgumentException("Foreign prepositional article support.");
                }
                Word[] supports = words.Values.Where(w => Overlap(old.Start, old.Length, w.Token.Start, w.Token.Length)).ToArray();
                diagnostics.Add(new(old.Code, old.Start, old.Length,
                    "existing_diagnostic=" + old.RuleId + ";bounded_article_advice",
                    exactArticle ? old.Readings! : Supports(supports)));
            }
        }
        foreach (Word word in words.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (word.Token.Text.IndexOfAny(['-', '/', '.']) >= 0)
                diagnostics.AddRange(AnalyzeCompoundShape(analysis.Text, word.Token.Start, word.Token.Length, cancellationToken));
        }
        List<(int Start, int End)>? commaQuotes = null;
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireSpan(analysis.Text, sentence.Start, sentence.Length);
            Word[] sentenceWords = words.Values.Where(w => Within(sentence.Start, sentence.Length, w.Token.Start, w.Token.Length)).ToArray();
            if (syntax.Available && exactLookup is not null)
                AddCoordinatedArticle(analysis.Text, sentence, sentenceWords, context, syntax, exactLookup, diagnostics, cancellationToken);
            AddIntensifier(analysis.Text, sentenceWords, suppressedSpans, diagnostics, unavailable, cancellationToken);
            if (commas is not null)
                AddCommas(analysis.Text, sentence, sentenceWords, commas, unavailable, ref commaQuotes, cancellationToken);
            if (fragments is not null)
            {
                if (!syntax.Available)
                    AddUnavailable(unavailable, "fragment:syntax_unavailable");
                else AddFragment(sentence, sentenceWords, syntax, fragments, unavailable, cancellationToken);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        AddLocalArticleFacts(analysis, words, localArticleFacts, diagnostics, cancellationToken);
        return Result(true, null);

        TextValidationEvidence Result(bool available, string? reason) => new(Version, available, reason,
            Freeze(diagnostics), spacing is null ? null : Freeze(spacing),
            commas is null ? null : Freeze(commas), fragments is null ? null : Freeze(fragments), Freeze(unavailable));
    }

    internal static (TextAnalysis Analysis, IReadOnlyList<TextValidationDiagnostic> LocalFacts) ApplyPrepositionalArticles(
        TextAnalysis analysis, Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        CancellationToken ct, MorphologyEvidenceProjection.View? morphology = null,
        List<TextGrammarMatch>? grammar = null,
        IReadOnlyDictionary<(int Start, int Length), IReadOnlyList<ClauseFusionAnalyzer.ArticleTarget>>? precomputedArticleTargets = null)
    {
        ct.ThrowIfCancellationRequested();
        if (analysis.ReadingInventory is null) return (analysis, []);
        morphology ??= MorphologyEvidenceProjection.Create(analysis, ct);
        if (morphology.UnavailableReason is not null) return (analysis, []);

        Dictionary<int, Word> bound = Bind(analysis, ct);
        var quoted = TextPredicateLinkAnalyzer.QuoteSpans(analysis.Text, ct, protectBackticks: true);
        var protectedSpans = TextPredicateLinkAnalyzer.QuoteSpans(analysis.Text, ct, protectBackticks: true, protectBrackets: true);
        bool Quoted(int start, int end)
        {
            int quote = quoted.BinarySearch((start, 0));
            if (quote < 0) quote = ~quote;
            return quote > 0 && quoted[quote - 1].End > start || quote < quoted.Count && quoted[quote].Start < end;
        }

        HashSet<(int Start, int Length)> evaluated = [];
        List<TextDiagnostic> added = [];
        List<TextValidationDiagnostic> facts = [];
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            RequireSpan(analysis.Text, sentence.Start, sentence.Length);
            var words = NominalStructuralProjection.SentenceWords(analysis, sentence, morphology, ct);
            for (int head = words.Count - 1; head > 0; head--)
            {
                ct.ThrowIfCancellationRequested();
                var binding = NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, head, ct,
                    allowArticledDeterminer: true);
                if (binding is null) continue;
                var first = binding.Participants[0];
                // Do not rebind an interior homograph as a shorter group when the maximal spine abstains.
                head = first.WordIndex;
                if (head == 0)
                {
                    if (!Quoted(binding.Start, binding.Start + binding.Length) &&
                        InitialNominalGroup(analysis.Text, sentence, words, binding, ct))
                        AddArticlePlacement(analysis, words, binding, binding.Participants.Select(member => bound[member.Word.OriginalIndex]).ToArray(),
                            [], InitialNominalArticleRule, added, ct, grammar);
                    continue;
                }
                var preposition = words[head - 1];
                if (first.Word.OriginalIndex != preposition.OriginalIndex + 1 ||
                    !Bridge(analysis.Text, preposition.Token.Start + preposition.Token.Length, binding.Start) ||
                    !preposition.Complete || preposition.UsesGraph)
                    continue;
                if (PrepositionArticleReadings(bound[preposition.OriginalIndex], ct) is not { Count: > 0 } prepReadings)
                {
                    if (!Quoted(preposition.Token.Start, binding.Start + binding.Length))
                    {
                        if (ClauseInitialPossessiveGroup(analysis.Text, sentence, words, preposition, binding, ct))
                            AddArticlePlacement(analysis, words, binding,
                                binding.Participants.Select(member => bound[member.Word.OriginalIndex]).ToArray(),
                                [], InitialNominalArticleRule, added, ct, grammar);
                        else if (PostverbalObjectNominalGroup(analysis.Text, words, head - 1, bound[preposition.OriginalIndex], binding, ct, out var verbAnchors))
                            AddArticlePlacement(analysis, words, binding,
                                binding.Participants.Select(member => bound[member.Word.OriginalIndex]).ToArray(),
                                verbAnchors, InitialNominalArticleRule, added, ct, grammar);
                    }
                    continue;
                }

                int start = preposition.Token.Start, end = binding.Start + binding.Length;
                if (Quoted(start, end)) continue;
                Word[] phrase = binding.Participants.Select(member => bound[member.Word.OriginalIndex]).ToArray();
                AddArticlePlacement(analysis, words, binding, phrase, prepReadings, ArticlePlacementRule, added, ct, grammar);
                var headMember = binding.Head;
                bool leaderHead = ContextualNominalAgreement.HasLeaderCue(binding, ct) &&
                    headMember.RoleReadings.Count > 0 &&
                    headMember.Word.Readings.Where((_, index) => !headMember.RoleReadings.Contains(index)).All(row =>
                        row.Features.NameKind == MorphologicalNameKind.PlaceName &&
                        ContextEvidenceGuards.IsUnwrittenCapitalizedReading(headMember.Word, row));
                bool IsExcludedPlaceReading(ProperNameCandidate candidate) =>
                    leaderHead && candidate.TokenIndex == headMember.Word.OriginalIndex &&
                    candidate is { Lane: ProperNameLane.Exact, Kinds: ProperNameKinds.Place, ContextSupports.Count: 0, ExactSupports.Count: > 0 } &&
                    candidate.ExactSupports.All(r => r.TokenIndex == candidate.TokenIndex &&
                        (uint)r.CompleteReadingIndex < (uint)headMember.Word.Readings.Count &&
                        !headMember.RoleReadings.Contains(r.CompleteReadingIndex));
                if (analysis.ProperNames is { } names &&
                    (names.Candidates.Any(n => Overlap(start, end - start, n.Start, n.Length) && !IsExcludedPlaceReading(n)) ||
                     names.Groups.Any(n => Overlap(start, end - start, n.Start, n.Length)) ||
                     binding.Participants.Any(member => names.DeferredTokenIndices.Contains(member.Word.OriginalIndex) ||
                         names.UnresolvedContextTokens?.Any(token => token.TokenIndex == member.Word.OriginalIndex) == true)))
                    continue;

                Word carrier = phrase[0];
                bool upperFrame = phrase.All(word => word.Token.Text.All(c => !char.IsLetter(c) || char.IsUpper(c)));
                if (!upperFrame && phrase.Any(word => char.IsUpper(word.Token.Text[0]))) continue;
                // The filtered copy validates article rows; supports still use original reading indices.
                Word[] articlePhrase = phrase;
                if (leaderHead)
                {
                    articlePhrase = phrase.ToArray();
                    articlePhrase[^1] = phrase[^1] with
                    { Rows = headMember.RoleReadings.Select(index => phrase[^1].Rows[index]).ToArray() };
                }
                if (binding.Bridges.Any(bridge => bridge.Kind == NominalStructuralProjection.BridgeKind.ConjunctionAnd) ||
                    binding.Participants.Any(member => !member.Word.Complete || member.Word.UsesGraph ||
                        member.RoleReadings.Count != (leaderHead && member == headMember
                            ? articlePhrase[^1].Rows.Count : member.Word.Readings.Count)) ||
                    articlePhrase.Any(word => !ExactArticleRows(word))) continue;
                if (phrase.Length == 1)
                {
                    if (!All(carrier, f => f.PartOfSpeech == MorphologicalPartOfSpeech.Noun)) continue;
                }
                // Only the first member can carry the group's article. Another definite
                // member can signal a different local structure even when the axes agree.
                else if (!All(carrier, f => f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                             ContextEvidenceGuards.IsMeasuredPassiveAttribute(f)) ||
                         articlePhrase.Skip(1).Any(word => !All(word, f => f.Article == MorphologicalArticle.Indefinite ||
                             f.Article is null && ContextEvidenceGuards.IsMeasuredPassiveAttribute(f))) ||
                         NominalStructuralProjection.Measure(binding, ct) is not { } measured ||
                         measured.Heads.Any(group => group.Attributes.Where((rows, index) =>
                             rows.Length != binding.Participants[index].RoleReadings.Count).Any()))
                    continue;

                KnownArticlePair? pair = ArticlePair(carrier, lookup, ct);
                if (pair is null || !evaluated.Add((carrier.Token.Start, carrier.Token.Length))) continue;
                var supports = Freeze(prepReadings.Concat(leaderHead
                    ? Supports(phrase.SkipLast(1)).Concat(NominalGrammarRules.References(headMember)) : Supports(phrase)));
                if (grammar is not null)
                {
                    var source = binding.Participants[0].Word;
                    grammar.Add(NominalGrammarRules.Match(PrepositionalArticleRule, binding,
                        pair.SourceIsFull ? TextGrammarAssessment.Mismatch : TextGrammarAssessment.Compatible,
                        pair.SourceIsFull ? [new(carrier.Index, Freeze(supports.Where(r => r.TokenIndex == carrier.Index)), prepReadings)] : [],
                        pair.SourceIsFull ? [NominalGrammarRules.Repair(source, MatchArticleCase(carrier.Token.Text, pair.Short))] : [],
                        controllers: [preposition.OriginalIndex], dependents: [carrier.Index]));
                }
                if (pair.SourceIsFull)
                {
                    if (!analysis.Diagnostics.Any(d => d.Code == PrepositionalArticleCode &&
                        d.Start == carrier.Token.Start && d.Length == carrier.Token.Length))
                        added.Add(new(PrepositionalArticleCode, carrier.Token.Start, carrier.Token.Length, PrepositionalArticleRule)
                        { ProjectedText = MatchArticleCase(carrier.Token.Text, pair.Short), Readings = supports });
                }
                else
                    facts.Add(new(LocalArticleCode, carrier.Token.Start, carrier.Token.Length,
                        "independent_preposition;raw_article=" + pair.RawArticle + ";pair_verified_short_form=" + pair.Short +
                        ";known_full_form=" + pair.Full +
                        ";known_short_form=" + pair.Short + ";exact_inverse_full_counterpart_roundtrip;count_alternatives_retained=" + pair.CountAlternatives +
                        ";local_article_only;not_sentence_verdict", supports));
            }
            ApplyClauseArticles(analysis, sentence, words, bound, protectedSpans, Quoted, lookup, evaluated, added, grammar, ct, precomputedArticleTargets);
        }
        if (evaluated.Count == 0 && added.Count == 0) return (analysis, []);
        return (analysis with { Diagnostics = Freeze(analysis.Diagnostics.Where(d =>
            d.Code != "full_short_article_check_not_ready" || !evaluated.Contains((d.Start, d.Length)))
            .Concat(added).OrderBy(d => d.Start).ThenBy(d => d.Length).ThenBy(d => d.Code, StringComparer.Ordinal)) }, Freeze(facts));
    }

    private static void ApplyClauseArticles(
        TextAnalysis analysis,
        SentenceSpan sentence,
        IReadOnlyList<ContextLanes.TokenView> words,
        Dictionary<int, Word> bound,
        IReadOnlyList<(int Start, int End)> protectedSpans,
        Func<int, int, bool> quoted,
        Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        HashSet<(int Start, int Length)> evaluated,
        List<TextDiagnostic> added,
        List<TextGrammarMatch>? grammar,
        CancellationToken ct,
        IReadOnlyDictionary<(int Start, int Length), IReadOnlyList<ClauseFusionAnalyzer.ArticleTarget>>? precomputedArticleTargets = null)
    {
        IReadOnlyList<ClauseFusionAnalyzer.ArticleTarget> targets =
            precomputedArticleTargets is not null && precomputedArticleTargets.TryGetValue((sentence.Start, sentence.Length), out var cached)
                ? cached
                : ClauseFusionAnalyzer.CollectArticleTargets(analysis, sentence, words, protectedSpans, ct, lookup);
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (target.PhraseWordIndices.Length == 0) continue;
            Word[] phrase = target.PhraseWordIndices.Select(wi => bound[words[wi].OriginalIndex]).ToArray();
            Word carrier = phrase[0];
            int start = carrier.Token.Start, end = phrase[^1].Token.Start + phrase[^1].Token.Length;
            if (quoted(start, end)) continue;
            static bool UnwrittenNonCarrierName(Word word, TextReadingSnapshot row) =>
                row.Features.NameKind is not null && word.Token.Text.Length > 0 && char.IsLower(word.Token.Text[0]) &&
                row.Raw.Form.Length > 0 && char.IsUpper(row.Raw.Form[0]) && Key(row.Raw.Form) == Key(word.Token.Text);
            static bool ExactNonCarrierMemberRows(Word word)
            {
                bool sawClean = false;
                foreach (TextReadingSnapshot row in word.Rows)
                {
                    if (UnwrittenNonCarrierName(word, row)) continue;
                    if (row.Features != row.Raw.Features || !CleanArticleFeatures(row.Features) ||
                        Key(row.Raw.Form) != Key(word.Token.Text) ||
                        !(row.Features.Article == MorphologicalArticle.Indefinite ||
                          row.Features.Article is null && ContextEvidenceGuards.IsMeasuredPassiveAttribute(row.Features)))
                        return false;
                    sawClean = true;
                }
                return sawClean;
            }

            int sentenceFirstTok = words.Count > 0 ? words[0].OriginalIndex : -1;
            bool allowInitialCap = carrier.Index == sentenceFirstTok;
            if (analysis.ProperNames is { } names &&
                (names.Candidates.Any(n => Overlap(start, end - start, n.Start, n.Length) &&
                     !(n.Lane == ProperNameLane.Exact && n.ExactSupports.Count > 0 && n.ContextSupports.Count == 0 &&
                       phrase.Skip(1).FirstOrDefault(w => w.Index == n.TokenIndex) is { } nonCarrier &&
                       n.ExactSupports.All(r => r.TokenIndex == nonCarrier.Index &&
                           (uint)r.CompleteReadingIndex < (uint)nonCarrier.Rows.Count &&
                           UnwrittenNonCarrierName(nonCarrier, nonCarrier.Rows[r.CompleteReadingIndex])))) ||
                 names.Groups.Any(n => Overlap(start, end - start, n.Start, n.Length)) ||
                 phrase.Any(w => !(allowInitialCap && w.Index == sentenceFirstTok &&
                     names.UnresolvedContextTokens?.Any(token => token.TokenIndex == w.Index &&
                         token.Reason == "sentence-initial-context-insufficient") == true) &&
                     (names.DeferredTokenIndices.Contains(w.Index) ||
                      names.UnresolvedContextTokens?.Any(token => token.TokenIndex == w.Index) == true))))
                continue;

            bool upperFrame = phrase.All(word => word.Token.Text.All(c => !char.IsLetter(c) || char.IsUpper(c)));
            if (!upperFrame && (allowInitialCap ? phrase.Skip(1) : phrase).Any(word => char.IsUpper(word.Token.Text[0])))
                continue;
            if (!ExactArticleRows(carrier)) continue;
            if (phrase.Length == 1)
            {
                if (!All(carrier, f => f.PartOfSpeech == MorphologicalPartOfSpeech.Noun)) continue;
            }
            else if (!All(carrier, f => f.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                         ContextEvidenceGuards.IsMeasuredPassiveAttribute(f)) ||
                     phrase.Skip(1).Any(word => !ExactNonCarrierMemberRows(word)))
                continue;

            bool allowDualParadigms = target.Kind == ClauseFusionAnalyzer.ArticleExpectationKind.SubjectFull;
            KnownArticlePair? pair = ArticlePair(carrier, lookup, ct, allowDualParadigms);
            if (pair is null) continue;

            if (target.Kind == ClauseFusionAnalyzer.ArticleExpectationKind.SubjectFull && !pair.SourceIsFull)
            {
                evaluated.Add((carrier.Token.Start, carrier.Token.Length));
                string replacement = MatchArticleCase(carrier.Token.Text, pair.Full);
                if (grammar is not null && target.AnchorReadings.Length > 0 && bound.TryGetValue(target.AnchorTokenIndex, out Word? anchorWord))
                {
                    int matchStart = Math.Min(start, anchorWord.Token.Start);
                    int matchEnd = Math.Max(end, anchorWord.Token.Start + anchorWord.Token.Length);
                    var singularCarrier = carrier.Rows
                        .Select((row, idx) => (row, idx))
                        .Where(item => item.row.Features.Number == MorphologicalNumber.Singular)
                        .Select(item => new TextReadingReference(carrier.Index, item.idx))
                        .ToArray();
                    var actualRefs = singularCarrier.Length > 0 ? Freeze(singularCarrier) : Supports([carrier]);
                    var anchorRefs = Freeze(target.AnchorReadings.Select(r => new TextReadingReference(target.AnchorTokenIndex, r)));
                    grammar.Add(new(NominalGrammarRules.SubjectFullArticleId, matchStart, matchEnd - matchStart,
                        Freeze([target.AnchorTokenIndex]), Freeze(phrase.Select(w => w.Index)),
                        TextGrammarAssessment.Mismatch,
                        Freeze([new TextGrammarConflict(carrier.Index, actualRefs, anchorRefs)]),
                        Freeze([NominalGrammarRules.Repair(words[target.PhraseWordIndices[0]], replacement)]),
                        null));
                }
                if (!analysis.Diagnostics.Any(d => d.Code == ArticleCode && d.Start == carrier.Token.Start && d.Length == carrier.Token.Length) &&
                    !added.Any(d => d.Code == ArticleCode && d.Start == carrier.Token.Start && d.Length == carrier.Token.Length))
                {
                    added.Add(new(ArticleCode, carrier.Token.Start, carrier.Token.Length, ClauseFusionAnalyzer.SubjectArticleRule)
                    {
                        ProjectedText = replacement,
                        Readings = Supports([carrier])
                    });
                }
            }
            else if (target.Kind == ClauseFusionAnalyzer.ArticleExpectationKind.DirectObjectShort && pair.SourceIsFull && target.AnchorReadings.Length > 0)
            {
                evaluated.Add((carrier.Token.Start, carrier.Token.Length));
                string replacement = MatchArticleCase(carrier.Token.Text, pair.Short);
                var phraseRefs = Supports([carrier]).Concat(phrase.Skip(1).SelectMany(w => w.Rows
                    .Select((row, idx) => (row, idx))
                    .Where(item => !UnwrittenNonCarrierName(w, item.row))
                    .Select(item => new TextReadingReference(w.Index, item.idx))));
                var supports = Freeze(target.AnchorReadings.Select(r => new TextReadingReference(target.AnchorTokenIndex, r))
                    .Concat(phraseRefs)
                    .Distinct()
                    .OrderBy(r => r.TokenIndex)
                    .ThenBy(r => r.CompleteReadingIndex));
                if (grammar is not null && bound.TryGetValue(target.AnchorTokenIndex, out Word? objAnchor))
                {
                    int matchStart = Math.Min(start, objAnchor.Token.Start);
                    int matchEnd = Math.Max(end, objAnchor.Token.Start + objAnchor.Token.Length);
                    var anchorRefs = Freeze(target.AnchorReadings.Select(r => new TextReadingReference(target.AnchorTokenIndex, r)));
                    grammar.Add(new(PrepositionalArticleRule, matchStart, matchEnd - matchStart,
                        Freeze([target.AnchorTokenIndex]), Freeze(phrase.Select(w => w.Index)),
                        TextGrammarAssessment.Mismatch,
                        Freeze([new TextGrammarConflict(carrier.Index, Supports([carrier]), anchorRefs)]),
                        Freeze([NominalGrammarRules.Repair(words[target.PhraseWordIndices[0]], replacement)]),
                        null));
                }
                if (!analysis.Diagnostics.Any(d => d.Code == PrepositionalArticleCode && d.Start == carrier.Token.Start && d.Length == carrier.Token.Length) &&
                    !added.Any(d => d.Code == PrepositionalArticleCode && d.Start == carrier.Token.Start && d.Length == carrier.Token.Length))
                {
                    added.Add(new(PrepositionalArticleCode, carrier.Token.Start, carrier.Token.Length, PrepositionalArticleRule)
                    {
                        ProjectedText = replacement,
                        Readings = supports
                    });
                }
            }
        }
    }

    private static bool HasOnlyUnwrittenPlaceHomographs(
        NominalStructuralProjection.Member member, bool allowSentenceInitialTitleCase) =>
        member.RoleReadings.Count > 0 &&
        member.Word.Readings.Where((row, idx) => !member.RoleReadings.Contains(idx))
            .All(row => ContextEvidenceGuards.IsSuppressedPresentActiveNounHomograph(member.Word, row) ||
                ContextEvidenceGuards.IsSuppressedDeterminerHomograph(member.Word, row) ||
                ContextEvidenceGuards.IsSyncreticVocativeCompanion(member.Word, row) ||
                ContextEvidenceGuards.IsUnwrittenCapitalizedReading(
                    member.Word, row, sentenceInitial: allowSentenceInitialTitleCase && member.WordIndex == 0) &&
                row.Features.NameKind == MorphologicalNameKind.PlaceName);

    private static bool ClauseInitialPossessiveGroup(string text, SentenceSpan sentence, IReadOnlyList<ContextLanes.TokenView> words,
        ContextLanes.TokenView introducer, NominalStructuralProjection.Binding binding, CancellationToken ct)
    {
        if (introducer.CanonicalText is not ("и" or "а" or "но" or "че") ||
            binding.Participants.Count < 2 ||
            !binding.Participants[0].RoleReadings.All(ri =>
                ContextEvidenceGuards.TryGetDeterminerAxes(binding.Participants[0].Word, binding.Participants[0].Word.Readings[ri], out _) &&
                ContextEvidenceGuards.GetDeterminerArticle(binding.Participants[0].Word.Readings[ri].Features) is
                    MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
            return false;
        return InitialNominalGroupCore(text, sentence, words, binding, requireSentenceStart: false, ct);
    }

    private static bool InitialNominalGroup(string text, SentenceSpan sentence, IReadOnlyList<ContextLanes.TokenView> words,
        NominalStructuralProjection.Binding binding, CancellationToken ct) =>
        InitialNominalGroupCore(text, sentence, words, binding, requireSentenceStart: true, ct);

    private static bool InitialNominalGroupCore(string text, SentenceSpan sentence, IReadOnlyList<ContextLanes.TokenView> words,
        NominalStructuralProjection.Binding binding, bool requireSentenceStart, CancellationToken ct)
    {
        if (binding.Participants.Count < 2 ||
            requireSentenceStart && !text.AsSpan(sentence.Start, binding.Start - sentence.Start).Trim().IsEmpty ||
            binding.Bridges.Any(bridge => bridge.Kind != NominalStructuralProjection.BridgeKind.Whitespace) ||
            binding.Participants.Any(member => !member.Word.Complete || member.Word.UsesGraph ||
                member.RoleReadings.Count != member.Word.Readings.Count &&
                !HasOnlyUnwrittenPlaceHomographs(member, allowSentenceInitialTitleCase: requireSentenceStart)) ||
            !binding.Participants.Skip(1).Any(member => member.Word.Readings.Any(r =>
                r.Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))) return false;
        int after = binding.Head.WordIndex + 1, end = binding.Start + binding.Length;
        if (after == words.Count)
        {
            ReadOnlySpan<char> tail = text.AsSpan(end, sentence.Start + sentence.Length - end).Trim();
            if (tail.Length != 1 || !SentenceBoundaryDetector.IsSentenceTerminator(tail[0])) return false;
        }
        else
        {
            int verbAt = after;
            int prevEnd = end;
            int prevOrig = binding.Head.Word.OriginalIndex;
            bool sawPreVerbalSe = false;
            for (int step = 0; step < 3 && verbAt + 1 < words.Count &&
                 words[verbAt].CanonicalText is "не" or "ще" or "се" &&
                 words[verbAt].OriginalIndex == prevOrig + 1 && Bridge(text, prevEnd, words[verbAt].Token.Start) &&
                 words[verbAt].Complete && !words[verbAt].UsesGraph; step++)
            {
                if (words[verbAt].CanonicalText == "се") sawPreVerbalSe = true;
                prevEnd = words[verbAt].Token.Start + words[verbAt].Token.Length;
                prevOrig = words[verbAt].OriginalIndex;
                verbAt++;
            }
            ContextLanes.TokenView finite = words[verbAt];
            if (finite.OriginalIndex != prevOrig + 1 ||
                !Bridge(text, prevEnd, finite.Token.Start) || !finite.Complete || finite.UsesGraph ||
                finite.Readings.Count == 0)
                return false;

            bool copularAux = (finite.CanonicalText is "е" or "са" or "бе" or "беше" or "бяха" or "съм" or "си" or "сме" or "сте" or "бъде" or "бъдат") &&
                finite.Readings.Any(TextPredicateLinkAnalyzer.CopularCarrier);
            bool modalDa = finite.CanonicalText is "няма" or "нямаше" or "може" or "трябва" or "щеше" or "щяха" &&
                verbAt + 2 < words.Count && words[verbAt + 1].CanonicalText == "да" &&
                words[verbAt + 1].OriginalIndex == finite.OriginalIndex + 1 &&
                Bridge(text, finite.Token.Start + finite.Token.Length, words[verbAt + 1].Token.Start) &&
                HasModalDaIntransitiveOrReflexiveTail(text, words, verbAt + 1);
            bool ordinaryFinite = finite.Readings.Any(r => r.Category == ContextLanes.Category.Verb &&
                    r.Features.VerbForm == MorphologicalVerbForm.Finite && ContextEvidenceGuards.IsPersonalPredicate(r.Features)) &&
                finite.Readings.All(r => r.Category == ContextLanes.Category.Verb &&
                    !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures &&
                    (r.Features.VerbForm == MorphologicalVerbForm.Imperative ||
                     r.Features.VerbForm == MorphologicalVerbForm.Finite && ContextEvidenceGuards.IsPersonalPredicate(r.Features)));
            if (!(ordinaryFinite || copularAux || modalDa))
                return false;
            if (copularAux && !sawPreVerbalSe && HasTransitiveActiveParticipleWithoutObject(text, words, verbAt))
                return false;
        }
        // The case must still support a coherent nominal group if the article is ignored.
        return CoherentPlacementGroup(words, binding, ct);
    }

    private static bool HasModalDaIntransitiveOrReflexiveTail(string text, IReadOnlyList<ContextLanes.TokenView> words, int daIdx)
    {
        int cur = daIdx + 1;
        int prevEnd = words[daIdx].Token.Start + words[daIdx].Token.Length;
        int prevOrig = words[daIdx].OriginalIndex;
        bool sawSe = false;
        while (cur < words.Count && words[cur].CanonicalText is "не" or "се" or "си" &&
               words[cur].OriginalIndex == prevOrig + 1 && Bridge(text, prevEnd, words[cur].Token.Start))
        {
            if (words[cur].CanonicalText == "се") sawSe = true;
            prevEnd = words[cur].Token.Start + words[cur].Token.Length;
            prevOrig = words[cur].OriginalIndex;
            cur++;
        }
        if (cur >= words.Count || words[cur].OriginalIndex != prevOrig + 1 ||
            !Bridge(text, prevEnd, words[cur].Token.Start) || !words[cur].Complete || words[cur].UsesGraph)
            return false;
        var v = words[cur];
        return v.Readings.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Finite && ContextEvidenceGuards.IsPersonalPredicate(r.Features)) &&
            (sawSe || v.Readings.Where(r => r.Features.VerbForm == MorphologicalVerbForm.Finite)
                .All(r => r.Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive));
    }

    private static bool HasTransitiveActiveParticipleWithoutObject(string text, IReadOnlyList<ContextLanes.TokenView> words, int auxIdx)
    {
        int cur = auxIdx + 1;
        int prevEnd = words[auxIdx].Token.Start + words[auxIdx].Token.Length;
        int prevOrig = words[auxIdx].OriginalIndex;
        while (cur < words.Count && words[cur].OriginalIndex == prevOrig + 1 && Bridge(text, prevEnd, words[cur].Token.Start) &&
               (words[cur].CanonicalText is "ли" or "вече" or "все" or "още" ||
                words[cur].Readings.All(r => r.SourceAdverb && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb)))
        {
            prevEnd = words[cur].Token.Start + words[cur].Token.Length;
            prevOrig = words[cur].OriginalIndex;
            cur++;
        }
        if (cur >= words.Count || words[cur].OriginalIndex != prevOrig + 1 || !Bridge(text, prevEnd, words[cur].Token.Start))
            return false;
        bool transActive = words[cur].Readings.Any(r => r.Features is
            {
                PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                VerbForm: MorphologicalVerbForm.Participle,
                ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                DeclaredTransitivity: MorphologicalVerbTransitivity.Transitive
            });
        if (!transActive) return false;
        return cur + 1 >= words.Count || ContextEvidenceGuards.IsExactAdposition(words[cur + 1]) ||
            words[cur + 1].Readings.All(r => r.SourceAdverb && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb);
    }

    private static bool PostverbalObjectNominalGroup(string text, IReadOnlyList<ContextLanes.TokenView> words,
        int verbWordIndex, Word rawVerb, NominalStructuralProjection.Binding binding, CancellationToken ct,
        out IReadOnlyList<TextReadingReference> verbAnchors)
    {
        verbAnchors = [];
        if (binding.Participants.Count < 2 ||
            binding.Bridges.Any(b => b.Kind != NominalStructuralProjection.BridgeKind.Whitespace) ||
            binding.Participants.Any(member => !member.Word.Complete || member.Word.UsesGraph ||
                member.RoleReadings.Count != member.Word.Readings.Count &&
                !HasOnlyUnwrittenPlaceHomographs(member, allowSentenceInitialTitleCase: false)) ||
            !binding.Participants.Skip(1).Any(member => member.Word.Readings.Any(r =>
                r.Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short)))
            return false;

        var firstMod = binding.Participants[0];
        if (binding.Participants.Count == 2 &&
            firstMod.Word.Readings.Any(r => ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) &&
                r.Features.Article is null or MorphologicalArticle.Indefinite))
            return false;

        var head = binding.Head;
        bool headStrictlyFullMasc = head.RoleReadings.All(r =>
                head.Word.Readings[r].Features is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular }) &&
            head.RoleReadings.Any(r => head.Word.Readings[r].Features.Article == MorphologicalArticle.Full) &&
            !head.RoleReadings.Any(r => head.Word.Readings[r].Features.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite);
        bool firstLacksFull = !firstMod.RoleReadings.Any(r => firstMod.Word.Readings[r].Features.Article == MorphologicalArticle.Full);
        if (headStrictlyFullMasc && firstLacksFull)
            return false;

        static bool ExcludedObjectControlLemma(string lemmaKey) =>
            lemmaKey is "правя" or "направя" or "смятам" or "сметна" or "считам" or
                "наричам" or "нарека" or "избирам" or "избера" or "обявявам" or "обявя" or
                "назначавам" or "назнача" or "оставям" or "оставя" or "приемам" or "приема" or
                "определям" or "определя" or "казвам" or "кажа" or
                "виждам" or "видя" or "намирам" or "намеря" or "заварвам" or "заваря" or
                "държа" or "задържам" or "задържа";

        ContextLanes.TokenView verb = words[verbWordIndex];
        var finite = verb.Readings.Where(r => r.Category == ContextLanes.Category.Verb &&
            r.Features.VerbForm == MorphologicalVerbForm.Finite && ContextEvidenceGuards.IsPersonalPredicate(r.Features)).ToArray();
        if (finite.Length > 0 &&
            verb.Readings.All(r => r.Category == ContextLanes.Category.Verb &&
                !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures &&
                (r.Features.VerbForm == MorphologicalVerbForm.Imperative ||
                 r.Features.VerbForm == MorphologicalVerbForm.Finite && ContextEvidenceGuards.IsPersonalPredicate(r.Features))) &&
            finite.All(r => r.Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive &&
                !ExcludedObjectControlLemma(Key(r.Lemma))) &&
            finite.All(vf => vf.Features.Person is MorphologicalPerson.First or MorphologicalPerson.Second ||
                HasUngovernedAgreeingPreverbalSubject(text, words, verbWordIndex, vf.Features)))
        {
            if (!CoherentPlacementGroup(words, binding, ct)) return false;
            verbAnchors = Supports([rawVerb]);
            return true;
        }

        // Compound transitive VerbalComplex ("са завършили новия моста"): active -л participle preceded by auxiliary съм/бях/бих.
        int[] activePartRows = Enumerable.Range(0, verb.Readings.Count)
            .Where(i => verb.Readings[i].Features is
                {
                    PartOfSpeech: MorphologicalPartOfSpeech.Verb,
                    VerbForm: MorphologicalVerbForm.Participle,
                    ParticipleKind: MorphologicalParticipleKind.PastAoristActive or MorphologicalParticipleKind.PastImperfectActive,
                    DeclaredTransitivity: MorphologicalVerbTransitivity.Transitive,
                    HasConflictingFeatures: false
                } && !ExcludedObjectControlLemma(Key(verb.Readings[i].Lemma)))
            .ToArray();
        if (activePartRows.Length > 0 && verbWordIndex >= 1 &&
            verb.Readings.Where((r, idx) => !activePartRows.Contains(idx)).All(r =>
                ContextEvidenceGuards.IsSourcePlaceholder(verb, r) ||
                ContextEvidenceGuards.IsUnwrittenCapitalizedReading(verb, r) ||
                r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective))
        {
            int auxIdx = verbWordIndex - 1;
            int nextStart = verb.Token.Start;
            int nextOrig = verb.OriginalIndex;
            bool sawSe = false;
            while (auxIdx >= 0 && words[auxIdx].OriginalIndex + 1 == nextOrig &&
                   Bridge(text, words[auxIdx].Token.Start + words[auxIdx].Token.Length, nextStart) &&
                   (BulgarianFunctionWords.HasAny(words[auxIdx].CanonicalText,
                        FunctionWordClasses.InterrogativeParticle | FunctionWordClasses.PossessiveOrDativeClitic) ||
                    words[auxIdx].CanonicalText is "вече" or "все" or "още" ||
                    words[auxIdx].Readings.All(r => r.SourceAdverb && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb)))
            {
                nextStart = words[auxIdx].Token.Start;
                nextOrig = words[auxIdx].OriginalIndex;
                auxIdx--;
            }
            if (auxIdx >= 0 && words[auxIdx].OriginalIndex + 1 == nextOrig &&
                Bridge(text, words[auxIdx].Token.Start + words[auxIdx].Token.Length, nextStart) &&
                words[auxIdx].Complete && !words[auxIdx].UsesGraph)
            {
                var auxRows = words[auxIdx].Readings.Where(TextPredicateLinkAnalyzer.CopularCarrier).ToArray();
                int subjLimit = auxIdx;
                while (subjLimit > 0 && words[subjLimit - 1].OriginalIndex + 1 == words[subjLimit].OriginalIndex &&
                       Bridge(text, words[subjLimit - 1].Token.Start + words[subjLimit - 1].Token.Length, words[subjLimit].Token.Start) &&
                       words[subjLimit - 1].CanonicalText != "да" &&
                       BulgarianFunctionWords.HasAny(words[subjLimit - 1].CanonicalText,
                           FunctionWordClasses.VerbalPrefixOrReflexive | FunctionWordClasses.ShortDativeClitic))
                {
                    if (words[subjLimit - 1].CanonicalText == "се") sawSe = true;
                    subjLimit--;
                }
                if (!sawSe && auxRows.Length > 0 &&
                    auxRows.All(af => af.Features.Person is MorphologicalPerson.First or MorphologicalPerson.Second ||
                        HasUngovernedAgreeingPreverbalSubject(text, words, subjLimit, af.Features)) &&
                    CoherentPlacementGroup(words, binding, ct))
                {
                    verbAnchors = Freeze(activePartRows.Select(i => new TextReadingReference(rawVerb.Index, i)));
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsPrecededByCountTrigger(IReadOnlyList<ContextLanes.TokenView> words, int firstWordIndex) =>
        firstWordIndex > 0 &&
        (words[firstWordIndex - 1].Token.Text.All(char.IsDigit) ||
         words[firstWordIndex - 1].CanonicalText is "няколко" or "колко" or "толкова" ||
         words[firstWordIndex - 1].Readings.Any(r =>
             r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral &&
             !ContextEvidenceGuards.TryGetOrdinalAxes(r, out _)));

    private static bool IsStrictlySingularModifierWord(ContextLanes.TokenView word, bool sentenceInitial)
    {
        var active = word.Readings.Where(r => !ContextEvidenceGuards.IsSourcePlaceholder(word, r) &&
            !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(word, r, sentenceInitial) &&
            !ContextEvidenceGuards.IsSuppressedPresentActiveNounHomograph(word, r) &&
            !ContextEvidenceGuards.IsSuppressedDeterminerHomograph(word, r)).ToArray();
        return active.Length > 0 && active.All(r =>
            r.Features.Number == MorphologicalNumber.Singular ||
            ContextEvidenceGuards.TryGetDeterminerAxes(word, r, out var det) && det.Number == MorphologicalNumber.Singular ||
            ContextEvidenceGuards.TryGetOrdinalAxes(r, out var ord) && ord.Number == MorphologicalNumber.Singular);
    }

    private static bool CoherentPlacementGroup(IReadOnlyList<ContextLanes.TokenView> words,
        NominalStructuralProjection.Binding binding, CancellationToken ct)
    {
        NominalStructuralProjection.Binding checkBinding = binding;
        int firstIdx = binding.Participants[0].WordIndex;
        var firstWord = binding.Participants[0].Word;
        var headMember = binding.Head;
        var headWord = headMember.Word;
        bool singularFirst = !IsPrecededByCountTrigger(words, firstIdx) &&
            IsStrictlySingularModifierWord(firstWord, firstIdx == 0);
        if (singularFirst &&
            headWord.Readings.Any(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Short or MorphologicalArticle.Definite }) &&
            headWord.Readings.Any(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite }))
        {
            var sgRows = headWord.Readings.Where(r => !(r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite })).ToArray();
            var updatedHead = new NominalStructuralProjection.Member(
                headMember.WordIndex,
                headWord with { Readings = Array.AsReadOnly(sgRows) },
                Array.AsReadOnly(Enumerable.Range(0, sgRows.Length).ToArray()));
            var updatedParticipants = binding.Participants.Take(binding.Participants.Count - 1).Append(updatedHead).ToArray();
            checkBinding = binding with { Participants = Array.AsReadOnly(updatedParticipants) };
        }
        return NominalStructuralProjection.Measure(checkBinding, ct) is not null;
    }

    private static bool HasUngovernedAgreeingPreverbalSubject(
        string text, IReadOnlyList<ContextLanes.TokenView> words, int verbWordIndex, MorphologicalFeatures verb)
    {
        bool insidePp = false;
        bool shortMascPrefix = false;
        bool found = false;
        for (int k = 0; k < verbWordIndex; k++)
        {
            ContextLanes.TokenView w = words[k];
            if (k > 0 && (w.OriginalIndex != words[k - 1].OriginalIndex + 1 ||
                          !Bridge(text, words[k - 1].Token.Start + words[k - 1].Token.Length, w.Token.Start)))
            {
                insidePp = false;
                shortMascPrefix = false;
                if (text.AsSpan(words[k - 1].Token.Start + words[k - 1].Token.Length,
                        w.Token.Start - (words[k - 1].Token.Start + words[k - 1].Token.Length)).IndexOfAny(",;:") >= 0)
                    found = false;
            }
            if (!w.Complete || w.UsesGraph || w.Readings.Count == 0) continue;
            string canon = w.CanonicalText;
            if (canon is not ("и" or "пък") &&
                BulgarianFunctionWords.HasAny(canon,
                    FunctionWordClasses.DaParticle | FunctionWordClasses.ClauseCoordinatingConjunction |
                    FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.RelativeSubjectPronoun) ||
                canon is "когото" or "където")
            {
                insidePp = false;
                shortMascPrefix = false;
                found = false;
                continue;
            }
            if (ContextEvidenceGuards.IsExactAdposition(w))
            {
                insidePp = true;
                shortMascPrefix = false;
                continue;
            }
            var active = w.Readings.Where(r => !ContextEvidenceGuards.IsSourcePlaceholder(w, r) &&
                !ContextEvidenceGuards.IsUnwrittenCapitalizedReading(w, r, k == 0)).ToArray();
            if (active.Length == 0) continue;
            if (insidePp)
            {
                if (active.All(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or
                        MorphologicalPartOfSpeech.ProperNoun or MorphologicalPartOfSpeech.PersonalPronoun or
                        MorphologicalPartOfSpeech.Pronoun &&
                    r.Features.IsShortForm != true && !ContextEvidenceGuards.TryGetDeterminerAxes(w, r, out _)))
                {
                    insidePp = false;
                }
                continue;
            }
            if (active.All(r => (r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                    ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features)) &&
                r.Features is { Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Short or MorphologicalArticle.Definite }))
            {
                shortMascPrefix = true;
                continue;
            }
            if (active.Any(r => ContextEvidenceGuards.LicensedSubject(canon, r) &&
                    ContextEvidenceGuards.PersonNumberTupleMatches((r.Features.Person, r.Features.Number), (verb.Person, verb.Number))) &&
                active.All(r => ContextEvidenceGuards.LicensedSubject(canon, r) ||
                    canon == "те" && (r.Category == ContextLanes.Category.Particle && !r.Features.HasUnrecognizedFeatures || ContextEvidenceGuards.IsAccusativeCliticReading(r)) ||
                    canon == "ти" && !r.Features.HasUnrecognizedFeatures && (r.Category == ContextLanes.Category.Particle || r.Features.IsShortForm == true) ||
                    canon == "вие" && r.Category == ContextLanes.Category.Verb))
            {
                found = true;
                continue;
            }
            if (!shortMascPrefix && active.All(r => r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, HasConflictingFeatures: false } nf &&
                    !(r.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) == true ||
                      r.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) == true) &&
                    !ContextEvidenceGuards.IsTemporalOrMeasureLemma(Key(r.Lemma)) &&
                    !(nf.Gender == MorphologicalGender.Masculine && nf.Number == MorphologicalNumber.Singular &&
                      nf.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite) &&
                    verb.Person == MorphologicalPerson.Third &&
                    (nf.Number == MorphologicalNumber.Plural) == (verb.Number == MorphologicalNumber.Plural)))
            {
                found = true;
                continue;
            }
            if (!active.All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective ||
                    ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) ||
                    ContextEvidenceGuards.TryGetDeterminerAxes(w, r, out _)))
                shortMascPrefix = false;
        }
        return found;
    }

    private static void AddArticlePlacement(TextAnalysis analysis, IReadOnlyList<ContextLanes.TokenView> words,
        NominalStructuralProjection.Binding binding, Word[] phrase, IReadOnlyList<TextReadingReference> anchors,
        string rule, List<TextDiagnostic> output, CancellationToken ct, List<TextGrammarMatch>? grammar)
    {
        if (phrase.Length < 2 || binding.Bridges.Any(b => b.Kind != NominalStructuralProjection.BridgeKind.Whitespace)) return;
        bool sentenceInitialPhrase = binding.Participants[0].WordIndex == 0;
        // A case-folded place-name homograph is still in the inventory. Only its original uppercase
        // spelling can support a name inside a prepositional group; at sentence start (index 0) an
        // initial-capitalized adjective with a pure PlaceName homograph ("Нова малката книга") is also title-cased by position.
        bool UnwrittenPlaceName(Word word, TextReadingSnapshot row) =>
            row.Features.NameKind == MorphologicalNameKind.PlaceName &&
            row.Raw.Form.Length > 0 && char.IsUpper(row.Raw.Form[0]) && Key(row.Raw.Form) == Key(word.Token.Text) &&
            (char.IsLower(word.Token.Text[0]) ||
             sentenceInitialPhrase && rule == InitialNominalArticleRule && word.Index == phrase[0].Index &&
             word.Token.Text.Length > 1 && char.IsUpper(word.Token.Text[0]) && char.IsLower(word.Token.Text[1]) &&
             word.Rows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
                 ContextEvidenceGuards.HasNominalAxes(r.Features)));

        if (analysis.ProperNames is { } names &&
            (names.Groups.Any(n => Overlap(binding.Start, binding.Length, n.Start, n.Length)) ||
             phrase.Any(w => !(sentenceInitialPhrase && rule == InitialNominalArticleRule && w.Index == phrase[0].Index &&
                 names.UnresolvedContextTokens?.Any(n => n.TokenIndex == w.Index &&
                     n.Reason == "sentence-initial-context-insufficient") == true &&
                 w.Rows.Any(r => UnwrittenPlaceName(w, r))) &&
                 (names.DeferredTokenIndices.Contains(w.Index) ||
                  names.UnresolvedContextTokens?.Any(n => n.TokenIndex == w.Index) == true)) ||
             names.Candidates.Any(n => Overlap(binding.Start, binding.Length, n.Start, n.Length) &&
                 (n.Lane != ProperNameLane.Exact || n.ExactSupports.Count == 0 || n.ContextSupports.Count > 0 ||
                  phrase.FirstOrDefault(w => w.Index == n.TokenIndex) is not { } word ||
                  n.ExactSupports.Any(r => r.TokenIndex != word.Index || (uint)r.CompleteReadingIndex >= (uint)word.Rows.Count ||
                      !UnwrittenPlaceName(word, word.Rows[r.CompleteReadingIndex])))))) return;

        // Guard G2-CP1: "от + [DefinitePassiveParticiple] + [DefiniteNoun]" ("различни от посочените[,] зоните се определят...")
        // can straddle an uncommaed participial/comparative PP boundary before a definite clause subject.
        if (rule == ArticlePlacementRule)
        {
            bool upperFrame = phrase.All(w => w.Token.Text.All(c => !char.IsLetter(c) || char.IsUpper(c)));
            if (!upperFrame && phrase.Any(w => char.IsUpper(w.Token.Text[0])))
                return;
            if (binding.Participants[0].WordIndex > 0 &&
                words[binding.Participants[0].WordIndex - 1].CanonicalText == "от" &&
                binding.Participants[0].Word.Readings.Any(r => ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) &&
                    r.Features.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
                return;
            if (!CoherentPlacementGroup(words, binding, ct))
                return;
        }

        int firstWordIdx = binding.Participants[0].WordIndex;
        bool singularFirst = !IsPrecededByCountTrigger(words, firstWordIdx) &&
            IsStrictlySingularModifierWord(binding.Participants[0].Word, sentenceInitialPhrase);
        Word headWord = phrase[^1];
        bool skipHeadMascCountCompanion = singularFirst &&
            headWord.Rows.Any(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Short or MorphologicalArticle.Definite }) &&
            headWord.Rows.Any(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite });

        List<TextReadingReference> supports = [.. anchors];
        List<Word> misplaced = [];
        for (int member = 0; member < phrase.Length; member++)
        {
            Word word = phrase[member];
            var bound = binding.Participants[member].Word;
            if (!bound.Complete || bound.UsesGraph) return;
            if (member == 0 && ContextEvidenceGuards.DeterminerAllowsArticledHead(bound)) return;
            bool? definite = null;
            for (int i = 0; i < word.Rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                TextReadingSnapshot row = word.Rows[i];
                if (UnwrittenPlaceName(word, row) ||
                    ContextEvidenceGuards.IsSuppressedPresentActiveNounHomograph(bound, bound.Readings[i]) ||
                    ContextEvidenceGuards.IsSuppressedDeterminerHomograph(bound, bound.Readings[i]) ||
                    ContextEvidenceGuards.IsSyncreticVocativeCompanion(bound, bound.Readings[i]))
                    continue;
                MorphologicalFeatures f = row.Features;
                if (member == phrase.Length - 1 && skipHeadMascCountCompanion && f is
                    { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite })
                    continue;
                bool determiner = member == 0 && ContextEvidenceGuards.TryGetDeterminerAxes(bound, bound.Readings[i], out _);
                bool ordinal = ContextEvidenceGuards.TryGetOrdinalAxes(bound.Readings[i], out var ordAxes) && member < phrase.Length - 1;
                bool passive = ContextEvidenceGuards.IsMeasuredPassiveAttribute(f);
                bool presentActive = ContextEvidenceGuards.IsMeasuredPresentActiveAttribute(f);
                if (f != row.Raw.Features || Key(row.Raw.Form) != Key(word.Token.Text) || f.HasConflictingFeatures || f.NameKind is not null)
                    return;
                if (!(determiner || ordinal))
                {
                    if (!CleanArticleFeatures(f) || !ContextEvidenceGuards.HasNominalAxes(f) || (member == phrase.Length - 1
                            ? f.PartOfSpeech != MorphologicalPartOfSpeech.Noun
                            : f.PartOfSpeech != MorphologicalPartOfSpeech.Adjective && !passive && !presentActive))
                        return;
                }
                bool? article = determiner
                    ? ContextEvidenceGuards.GetDeterminerArticle(f) is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short ||
                      Key(row.Raw.Lemma) is "този" or "онзи"
                    : ordinal
                        ? ordAxes.Article is MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short
                            ? true
                            : ordAxes.Article == MorphologicalArticle.Indefinite ? false : null
                        : f.Article switch
                        {
                            MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short => true,
                            MorphologicalArticle.Indefinite => false,
                            null when passive || presentActive => false,
                            _ => null
                        };
                if (article is null || definite is not null && definite != article) return;
                definite = article;
                supports.Add(new(word.Index, i));
            }
            if (definite is null) return;
            if (member > 0 && definite.Value) misplaced.Add(word);
        }
        if (misplaced.Count == 0 || NominalStructuralProjection.AssessPartition(analysis.Text, words,
            binding.Participants[0].WordIndex, binding.Head.WordIndex, ct) != NominalStructuralProjection.Partition.None)
            return;
        var proof = Freeze(supports);
        if (grammar is not null)
        {
            var first = Freeze(proof.Where(r => r.TokenIndex == phrase[0].Index));
            var conflicts = Freeze(misplaced.Select(word =>
                new TextGrammarConflict(word.Index, Freeze(proof.Where(r => r.TokenIndex == word.Index)), first)));
            grammar.Add(NominalGrammarRules.Match(rule, binding, TextGrammarAssessment.Mismatch, conflicts, [],
                "multi_token_article_repair_not_measured", controllers: Freeze(anchors.Select(r => r.TokenIndex)
                    .Append(phrase[0].Index).Distinct()), dependents: Freeze(phrase.Skip(1).Select(word => word.Index))));
        }
        foreach (Word word in misplaced)
        {
            ct.ThrowIfCancellationRequested();
            if (!analysis.Diagnostics.Any(d => d.Code == ArticlePlacementCode &&
                d.Start == word.Token.Start && d.Length == word.Token.Length))
                output.Add(new(ArticlePlacementCode, word.Token.Start, word.Token.Length, rule) { Readings = proof });
        }
    }

    private static bool CleanArticleFeatures(MorphologicalFeatures f) =>
        !f.HasConflictingFeatures && !f.HasUnrecognizedFeatures && f.NameKind is null;

    private static bool ExactArticleRows(Word word) => word.Rows.Count > 0 && word.Rows.All(row =>
        row.Features == row.Raw.Features && CleanArticleFeatures(row.Features) && Key(row.Raw.Form) == Key(word.Token.Text));

    private static IReadOnlyList<TextReadingReference> PrepositionArticleReadings(Word word, CancellationToken ct)
    {
        if (!ExactArticleRows(word)) return [];
        string surfaceKey = Key(word.Token.Text);
        List<TextReadingReference> supports = [];
        for (int index = 0; index < word.Rows.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var row = word.Rows[index];
            if (row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition ||
                surfaceKey == "относно" && row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb && Key(row.Raw.Lemma) == "относно")
                supports.Add(new(word.Index, index));
            // A bare particle reading does not bind this immediate common nominal spine.
            // A different lexical owner or a competing nominal/conjunction use remains open.
            else if (row.Features.PartOfSpeech != MorphologicalPartOfSpeech.Particle ||
                     Key(row.Raw.Lemma) != surfaceKey) return [];
        }
        return supports;
    }

    internal static bool? KnownFullArticleForm(TokenAnalysis token, IReadOnlyList<TextReadingSnapshot> rows,
        Func<string, IReadOnlyList<TextReadingSnapshot>> lookup, CancellationToken ct) =>
        ArticlePair(new(-1, token, rows, true), lookup, ct)?.SourceIsFull;

    private sealed record KnownArticlePair(string Full, string Short, bool SourceIsFull, MorphologicalArticle RawArticle, int CountAlternatives);
    private static KnownArticlePair? ArticlePair(Word word, Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        CancellationToken ct, bool allowSameLemmaParadigmVariants = false)
    {
        TextReadingSnapshot[] singular = word.Rows.Where(r => CleanArticleFeatures(r.Features) && r.Features == r.Raw.Features &&
            (r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective ||
             ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features)) &&
            r.Features.Number == MorphologicalNumber.Singular && r.Features.Gender == MorphologicalGender.Masculine &&
            r.Features.ParadigmId is not null && r.Features.Article is MorphologicalArticle.Full or MorphologicalArticle.Short or MorphologicalArticle.Definite).ToArray();
        if (singular.Length == 0) return null;
        TextReadingSnapshot anchor = singular[0];
        bool passiveAnchor = ContextEvidenceGuards.IsMeasuredPassiveAttribute(anchor.Features);
        string surface = Key(word.Token.Text);
        bool sourceIsFull = anchor.Features.Article == MorphologicalArticle.Full ||
            passiveAnchor && anchor.Features.Article == MorphologicalArticle.Definite &&
            surface.EndsWith("ият", StringComparison.Ordinal);
        MorphologicalFeatures NormalizeAxes(MorphologicalFeatures f) => allowSameLemmaParadigmVariants
            ? f with { RawTag = null, Article = null, ParadigmId = null }
            : f with { RawTag = null, Article = null };
        MorphologicalFeatures axes = NormalizeAxes(anchor.Features);
        bool SameAxes(TextReadingSnapshot r) => NormalizeAxes(r.Features) == axes;
        bool CountAlternative(TextReadingSnapshot r) => anchor.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
            r.Features.Number == MorphologicalNumber.Count && r.Features.Article == MorphologicalArticle.Indefinite &&
            NormalizeAxes(r.Features with { Number = anchor.Features.Number }) == axes;
        bool Identity(TextReadingSnapshot r) => CleanArticleFeatures(r.Features) && r.Features == r.Raw.Features &&
            SameArticleOwner(r.Raw, anchor.Raw, allowSameLemmaParadigmVariants);
        foreach (TextReadingSnapshot row in word.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!Identity(row) || !(SameAxes(row) && row.Features.Article == anchor.Features.Article ||
                !sourceIsFull && CountAlternative(row))) return null;
        }
        string full, shortForm;
        if (sourceIsFull)
        {
            full = surface;
            if ((anchor.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective || passiveAnchor) &&
                surface.EndsWith("ият", StringComparison.Ordinal)) shortForm = surface[..^1];
            else if (anchor.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun && surface.EndsWith("ът", StringComparison.Ordinal)) shortForm = surface[..^2] + "а";
            else if (anchor.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun && surface.EndsWith("ят", StringComparison.Ordinal)) shortForm = surface[..^1];
            else return null;
        }
        else
        {
            shortForm = surface;
            full = Key(FullCounterpart(word, lookup, ct, allowSameLemmaParadigmVariants) ?? string.Empty);
            if (full.Length == 0) return null;
        }
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TextReadingSnapshot> fullRows = lookup(full) ?? throw new ArgumentException("Exact callback returned null.");
        IReadOnlyList<TextReadingSnapshot> shortRows = lookup(shortForm) ?? throw new ArgumentException("Exact callback returned null.");
        ct.ThrowIfCancellationRequested();
        if (full == shortForm || fullRows.Count == 0 || shortRows.Count == 0 ||
            fullRows.Any(r => Key(r.Raw.Form) != full || !Identity(r) || !SameAxes(r) ||
                !(r.Features.Article == MorphologicalArticle.Full || passiveAnchor && r.Features.Article == MorphologicalArticle.Definite)) ||
            !shortRows.Any(r => SameAxes(r) && r.Features.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite) ||
            shortRows.Any(r => Key(r.Raw.Form) != shortForm || !Identity(r) ||
                !(SameAxes(r) && r.Features.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite || CountAlternative(r)))) return null;
        Word inverse = word with { Token = word.Token with { Text = shortForm, Length = shortForm.Length }, Rows = shortRows };
        if (Key(FullCounterpart(inverse, lookup, ct, allowSameLemmaParadigmVariants) ?? string.Empty) != full) return null;
        var original = sourceIsFull ? fullRows : shortRows;
        var expectedAuthority = word.Rows.Select(r => ArticleAuthority(r.Raw, allowSameLemmaParadigmVariants)).ToHashSet();
        if (!expectedAuthority.SetEquals(original.Select(r => ArticleAuthority(r.Raw, allowSameLemmaParadigmVariants)))) return null;
        return new(full, shortForm, sourceIsFull, anchor.Features.Article!.Value,
            word.Rows.Count(r => r.Features.Number == MorphologicalNumber.Count));
    }
    private static bool SameArticleOwner(WordReading a, WordReading b, bool allowSameLemmaParadigmVariants = false) =>
        Key(a.Lemma) == Key(b.Lemma) &&
        a.Features.PartOfSpeech == b.Features.PartOfSpeech &&
        string.Equals(a.Source, b.Source, StringComparison.Ordinal) &&
        (allowSameLemmaParadigmVariants && a.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun
            ? a.Features.ParadigmId is not null && b.Features.ParadigmId is not null
            : a.Features.ParadigmId is not null && a.Features.ParadigmId == b.Features.ParadigmId &&
              a.LegacyTypeId == b.LegacyTypeId && a.LegacyBaseId == b.LegacyBaseId);
    private static (string Form, string Lemma, string? Tag, int? Type, uint? Base, uint? Word, string Source) ArticleAuthority(
        WordReading r, bool allowSameLemmaParadigmVariants = false) =>
        allowSameLemmaParadigmVariants
            ? (Key(r.Form), Key(r.Lemma), null, null, null, null, r.Source)
            : (Key(r.Form), Key(r.Lemma), r.PartOfSpeech, r.LegacyTypeId, r.LegacyBaseId, r.LegacyWordId, r.Source);
    internal static string MatchArticleCase(string source, string form) => source.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? form.ToUpperInvariant() :
        char.IsUpper(source[0]) ? char.ToUpperInvariant(form[0]) + form[1..] : form;

    private static void AddLocalArticleFacts(TextAnalysis analysis, Dictionary<int, Word> words,
        IReadOnlyList<TextValidationDiagnostic>? facts, List<TextValidationDiagnostic> output, CancellationToken ct)
    {
        if (facts is null) return;
        foreach (TextValidationDiagnostic fact in facts)
        {
            ct.ThrowIfCancellationRequested();
            RequireSpan(analysis.Text, fact.Start, fact.Length);
            Word? carrier = words.Values.SingleOrDefault(w => w.Token.Start == fact.Start && w.Token.Length == fact.Length);
            if (carrier is null || fact.Code != LocalArticleCode || !fact.Basis.StartsWith("independent_preposition;raw_article=", StringComparison.Ordinal) ||
                fact.Supports.Count == 0 || !carrier.Rows.Any(r => r.Features.Number == MorphologicalNumber.Singular &&
                    r.Features.Article is MorphologicalArticle.Short or MorphologicalArticle.Definite) ||
                carrier.Rows.Any(r => r.Features.Article == MorphologicalArticle.Full) ||
                !Enumerable.Range(0, carrier.Rows.Count).All(i => fact.Supports.Contains(new TextReadingReference(carrier.Index, i))) ||
                analysis.Diagnostics.Any(d => d.Start == fact.Start && d.Length == fact.Length &&
                    d.Code is PrepositionalArticleCode or "full_short_article_check_not_ready"))
                throw new ArgumentException("Unbound local article fact.");
            foreach (TextReadingReference reference in fact.Supports)
                if (!words.TryGetValue(reference.TokenIndex, out Word? owner) || (uint)reference.CompleteReadingIndex >= (uint)owner.Rows.Count ||
                    !analysis.Sentences.Any(s => Within(s.Start, s.Length, fact.Start, fact.Length) && Within(s.Start, s.Length, owner.Token.Start, owner.Token.Length)))
                    throw new ArgumentException("Foreign local article support.");
            if (!output.Any(d => d.Code == fact.Code && d.Start == fact.Start && d.Length == fact.Length)) output.Add(fact);
        }
    }

    private sealed record Word(int Index, TokenAnalysis Token, IReadOnlyList<TextReadingSnapshot> Rows, bool CanonicalSource);

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
            TextReadingSet set = inventory.Sets[occurrence.ReadingSetIndex];
            result.Add(index, new(index, token, set.ResolvedReadings, set.CanonicalReadings.Count > 0));
        }
        if (occurrenceIndex != inventory.Occurrences.Count)
            throw new ArgumentException("Unbound complete occurrence.", nameof(analysis));
        return result;
    }

    // Bind has checked every raw token/occurrence. A represented opaque tail
    // still needs its own lookup snapshot rather than an absent or foreign set.
    private static void RequireOriginalRoleSource(TextAnalysis analysis, string basis, CancellationToken ct)
    {
        TextReadingInventory inventory = analysis.ReadingInventory!;
        int index = 0;
        bool[] used = new bool[inventory.Sets.Count];
        var original = new BulgarianTokenizer().Tokenize(analysis.Text, lookup =>
        {
            ct.ThrowIfCancellationRequested();
            if (index >= inventory.Occurrences.Count)
                throw new ArgumentException(basis + " needs the original Word occurrences.");
            var occurrence = inventory.Occurrences[index++];
            var display = analysis.Tokens[occurrence.TokenIndex].Analysis;
            var set = inventory.Sets[occurrence.ReadingSetIndex];
            if (display is null || set.LookupText != lookup || display.Word != lookup ||
                set.CanonicalKey != BulgarianTextNormalizer.NormalizeKey(lookup) ||
                set.SupportKey is { } support && (!BulgarianStressLookup.TryGetSupportKey(lookup, set.CanonicalKey, out string valid) || support != valid) ||
                display.NormalizedWord != (set.SupportReadings is { Count: > 0 } ? set.SupportKey : set.CanonicalKey) ||
                display.Readings.Count != set.DisplayedReadingCount ||
                !display.Readings.SequenceEqual(set.ResolvedReadings.Take(set.DisplayedReadingCount).Select(r => r.Raw)))
                throw new ArgumentException(basis + " needs the original lookup snapshot for every Word.");
            used[occurrence.ReadingSetIndex] = true;
            return display;
        });
        ct.ThrowIfCancellationRequested();
        if (index != inventory.Occurrences.Count || !original.SequenceEqual(analysis.Tokens))
            throw new ArgumentException(basis + " needs the original tokenization.");
        if (used.Contains(false)) throw new ArgumentException(basis + " has an unused lookup snapshot.");
    }

    private static void ValidateReferences(TextAnalysis analysis, TextContextEvidence context, TextSyntaxEvidence syntax,
        Dictionary<int, Word> words, TextContextOptions options, CancellationToken ct,
        ClauseFusionAnalyzer.DirectObjectFrame? directObjectFrame,
        Func<string, IReadOnlyList<TextReadingSnapshot>>? exactLookup = null,
        SimpleClauseAgreementResolver.VerbReadingAuthority? verbAuthority = null,
        VerbFrameCatalog? verbFrames = null,
        double postverbalDirectObjectMaxRatio = VerbFrameCatalog.DefaultPostverbalDirectObjectMaxRatio)
    {
        bool includeOpenNominal = options.IncludeGrammarEvidence;
        var graphFeatures = new Dictionary<TextGraphReadingReference, MorphologicalFeatures>();
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
        void CheckGraph(IReadOnlyList<TextGraphReadingReference>? refs, int owner, int start, int length,
            IReadOnlyList<TextReadingReference> exact, TextContextRole? role = null)
        {
            if (refs is null || refs.Count == 0) return;
            if (exact.Count != 0 || !words.TryGetValue(owner, out Word? word) ||
                !Within(start, length, word.Token.Start, word.Token.Length))
                throw new ArgumentException("Graph support has mixed exact authority or a foreign sentence span.");
            foreach (TextGraphReadingReference reference in refs)
            {
                ct.ThrowIfCancellationRequested();
                if (!graphFeatures.TryGetValue(reference, out MorphologicalFeatures features))
                {
                    features = MorphologyEvidenceProjection.RequireGraphSupport(word.Token, reference, owner, word.Rows.Count, ct);
                    graphFeatures.Add(reference, features);
                }
                if (reference.TokenIndex != owner || role == TextContextRole.Predicate &&
                    (!ContextEvidenceGuards.IsPersonalPredicate(features) || features.VerbForm != MorphologicalVerbForm.Finite) ||
                    role == TextContextRole.Attribute && (features.PartOfSpeech != MorphologicalPartOfSpeech.Adjective ||
                        !ContextEvidenceGuards.HasNominalAxes(features)) ||
                    role == TextContextRole.Subject && (features.Number is null ||
                        features.PartOfSpeech is not (MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun or
                            MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.Pronoun) ||
                        features.PartOfSpeech is MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.Pronoun && features.Person is null))
                    throw new ArgumentException("Graph support does not license the requested typed role.");
            }
        }
        MorphologicalFeatures[] Features(IReadOnlyList<TextReadingReference> exact, IReadOnlyList<TextGraphReadingReference>? graph) =>
            graph is { Count: > 0 } ? graph.Select(r => graphFeatures[r]).ToArray() :
                exact.Select(r => words[r.TokenIndex].Rows[r.CompleteReadingIndex].Features).ToArray();
        void AttributeHead(TextContextRoleSelection attribute, TextContextRoleSelection head, bool compatible, bool phraseInitial = false)
        {
            MorphologicalFeatures[] a = Features(attribute.Readings, attribute.GraphReadings), n = Features(head.Readings, head.GraphReadings);
            if (a.Length == 0 || n.Length == 0 || a.Any(f => f.PartOfSpeech != MorphologicalPartOfSpeech.Adjective || !ContextEvidenceGuards.HasNominalAxes(f)) ||
                n.Any(f => f.PartOfSpeech != MorphologicalPartOfSpeech.Noun || !ContextEvidenceGuards.HasNominalAxes(f)))
                throw new ArgumentException("Attribute needs explicit morphology and its actual noun anchor.");
            if (compatible)
            {
                foreach (MorphologicalFeatures noun in n)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ContextEvidenceGuards.NominalTupleMatches((noun.Number, noun.Gender), (n[0].Number, n[0].Gender)))
                        throw new ArgumentException("Nominal supports must share one coherent head tuple.");
                }
                string attrSurface = Key(words[attribute.TokenIndex].Token.Text);
                bool epiceneHead = head.GraphReadings is not { Count: > 0 } && head.Readings.Count > 0 &&
                    head.Readings.All(r => ContextEvidenceGuards.IsEpiceneHumanNounLemma(words[r.TokenIndex].Rows[r.CompleteReadingIndex].Raw.Lemma));
                foreach (MorphologicalFeatures adj in a)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ContextEvidenceGuards.AttributeAgreement(adj, n[0]) &&
                        !(epiceneHead && n[0] is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine } &&
                          ContextEvidenceGuards.AttributeAgreement(adj, n[0] with { Gender = MorphologicalGender.Feminine })) &&
                        !(phraseInitial && ContextEvidenceGuards.IsDefinitePluralQuantifierShape(attrSurface, adj, n[0])))
                        throw new ArgumentException("Accepted attribute disagrees with noun anchor.");
                }
            }
        }
        void NominalRoles(TextContextSentence sentence, IReadOnlyList<TextContextRoleSelection> roles, bool compatible, bool finiteCompatible = false)
        {
            Word[] original = words.Values.Where(w => Within(sentence.Start, sentence.Length, w.Token.Start, w.Token.Length)).OrderBy(w => w.Token.Start).ToArray();
            if (!analysis.Sentences.Any(s => s.Start == sentence.Start && s.Length == sentence.Length) || original.Length < 3 || roles.Count != original.Length)
                throw new ArgumentException("Nominal roles do not cover one original sentence.");
            TextContextRoleSelection[] heads = roles.Where(r => r.Role == TextContextRole.Subject).ToArray();
            TextContextRoleSelection[] predicates = roles.Where(r => r.Role == TextContextRole.Predicate).ToArray();
            if (heads.Length != 1 || predicates.Length != 1 || heads[0].TokenIndex != original[^2].Index || predicates[0].TokenIndex != original[^1].Index)
                throw new ArgumentException("Foreign nominal head or predicate occurrence.");
            MorphologicalFeatures[] predicateFeatures = Features(predicates[0].Readings, predicates[0].GraphReadings);
            if (predicateFeatures.Length == 0 || predicateFeatures.Any(f => f.HasUnrecognizedFeatures ||
                !ContextEvidenceGuards.IsPersonalPredicate(f) || f.VerbForm != MorphologicalVerbForm.Finite))
                throw new ArgumentException("Nominal phrase predicate needs original finite support.");
            if (finiteCompatible)
            {
                MorphologicalFeatures[] nounFeatures = Features(heads[0].Readings, heads[0].GraphReadings);
                if (nounFeatures.Length == 0 || predicateFeatures.Any(f => nounFeatures.Any(n =>
                    !ContextEvidenceGuards.PersonNumberTupleMatches((MorphologicalPerson.Third, n.Number), (f.Person, f.Number)))))
                    throw new ArgumentException("Compatible nominal phrase needs one measured finite relation.");
            }
            var byToken = new Dictionary<int, TextContextRoleSelection>();
            foreach (TextContextRoleSelection role in roles)
            { ct.ThrowIfCancellationRequested(); if (!byToken.TryAdd(role.TokenIndex, role)) throw new ArgumentException("Duplicate nominal token role."); }
            for (int i = 0; i < original.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!byToken.TryGetValue(original[i].Index, out TextContextRoleSelection? at) ||
                    at.Start != original[i].Token.Start || at.Length != original[i].Token.Length ||
                    i < original.Length - 2 && at.Role != TextContextRole.Attribute)
                    throw new ArgumentException("Nominal phrase roles must retain every original token span.");
                if (i < original.Length - 2) AttributeHead(at, heads[0], compatible, phraseInitial: i == 0);
            }
        }
        void RequireAllGraphCells(TextContextRoleSelection role, TextContextSentence sentence)
        {
            if (role.GraphReadings is not { Count: > 0 }) return;
            WordAnalysis word = words[role.TokenIndex].Token.Analysis!;
            List<TextGraphReadingReference> expected = [];
            for (int c = 0; c < word.GraphCandidates.Count; c++)
            {
                ct.ThrowIfCancellationRequested();
                WordDerivation path = word.GraphCandidates[c];
                if (path.IsConditional) continue;
                if (path.FullTags is not { Count: > 0 }) throw new ArgumentException("Selected graph scope has an unbound path.");
                for (int t = 0; t < path.FullTags.Count; t++) { ct.ThrowIfCancellationRequested(); expected.Add(new(role.TokenIndex, c, t)); }
            }
            CheckGraph(expected, role.TokenIndex, sentence.Start, sentence.Length, role.Readings, role.Role);
            if (!expected.SequenceEqual(role.GraphReadings)) throw new ArgumentException("Selected nominal role excludes graph alternatives.");
        }
        var nominalAttributes = new Dictionary<int, List<(TextContextRoleSelection Attribute, TextContextRoleSelection Head, bool Compatible, bool PhraseInitial)>>();
        foreach (TextContextSentence sentence in context.Sentences)
        {
            foreach (TextContextRoleSelection role in sentence.LocalRoles.Concat(sentence.SelectedRoles)
                .Concat(sentence.Candidates.SelectMany(candidate => candidate.Roles)))
            {
                Check(role.Readings, role.TokenIndex);
                if (role.GraphReadings is { Count: > 0 } && (!words.TryGetValue(role.TokenIndex, out Word? word) ||
                    role.Start != word.Token.Start || role.Length != word.Token.Length))
                    throw new ArgumentException("Graph role is not the original token span.");
                CheckGraph(role.GraphReadings, role.TokenIndex, sentence.Start, sentence.Length, role.Readings, role.Role);
            }
            foreach (TextContextCandidate candidate in sentence.Candidates)
            {
                if (candidate.AuthorityLane == ContextLanes.ProductiveNominalLane)
                {
                    bool compatible = !candidate.EvidenceCodes.Contains(ContextLanes.ProductiveAttributeMismatch, StringComparer.Ordinal);
                    NominalRoles(sentence, candidate.Roles, compatible,
                        candidate.EvidenceCodes.Contains("subject_predicate_agreement_compatible", StringComparer.Ordinal));
                    if (candidate.Selectable) foreach (TextContextRoleSelection role in candidate.Roles) RequireAllGraphCells(role, sentence);
                    TextContextRoleSelection head = candidate.Roles.Single(r => r.Role == TextContextRole.Subject);
                    int firstTokenIndex = candidate.Roles.Min(r => r.TokenIndex);
                    foreach (TextContextRoleSelection attribute in candidate.Roles.Where(r => r.Role == TextContextRole.Attribute))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!nominalAttributes.TryGetValue(attribute.TokenIndex, out var bindings))
                            nominalAttributes.Add(attribute.TokenIndex, bindings = []);
                        bindings.Add((attribute, head, compatible, attribute.TokenIndex == firstTokenIndex));
                    }
                }
                else if (candidate.Roles.Any(r => r.Role == TextContextRole.Attribute && r.GraphReadings is { Count: > 0 }))
                    throw new ArgumentException("Graph Attribute needs its explicit nominal head topology.");
            }
            if (sentence.EvidenceCodes.Contains("accepted_authority_lane=" + ContextLanes.ProductiveNominalLane, StringComparer.Ordinal))
            {
                NominalRoles(sentence, sentence.SelectedRoles, true, true);
                foreach (TextContextRoleSelection role in sentence.SelectedRoles) RequireAllGraphCells(role, sentence);
            }
            else if (sentence.SelectedRoles.Any(r => r.Role == TextContextRole.Attribute && r.GraphReadings is { Count: > 0 }))
                throw new ArgumentException("Selected graph Attribute lacks nominal authority.");
            if (sentence.LocalRoles.Any(r => r.Role == TextContextRole.Attribute && r.GraphReadings is { Count: > 0 }))
                throw new ArgumentException("Local graph Attribute lacks its noun anchor.");
        }
        bool SameSupport(TextContextRoleSelection role, TextSyntaxSupport support) => role.TokenIndex == support.TokenIndex &&
            role.Readings.SequenceEqual(support.Readings) && (role.GraphReadings ?? []).SequenceEqual(support.GraphReadings ?? []);
        foreach (TextSyntaxSentence sentence in syntax.Sentences)
        {
            foreach (TextSyntaxCandidate candidate in sentence.Candidates)
            {
                foreach (TextSyntaxSupport support in candidate.Targets.Concat(candidate.Context))
                {
                    Check(support.Readings, support.TokenIndex);
                    if (support.GraphReadings is { Count: > 0 } && support.Basis != "LicensedGraphMorphology")
                        throw new ArgumentException("Graph syntax support cannot claim exact category authority.");
                    if (support.Basis == "LicensedGraphMorphology" && support.GraphReadings is not { Count: > 0 })
                        throw new ArgumentException("Graph syntax basis needs its original graph references.");
                    CheckGraph(support.GraphReadings, support.TokenIndex, sentence.Start, sentence.Length, support.Readings);
                }
                if (candidate.Role != TextSyntaxRole.Attribute) continue;
                bool graphRelation = candidate.Targets.Concat(candidate.Context).Any(s => s.GraphReadings is { Count: > 0 });
                if (candidate.RuleId != "ProductiveNominalPhraseAttribute")
                { if (graphRelation) throw new ArgumentException("Graph Attribute needs its bound nominal rule."); continue; }
                if (candidate.Targets.Count != 1 || candidate.Context.Count != 1)
                    throw new ArgumentException("Attribute needs one target and one noun anchor.");
                TextSyntaxSupport target = candidate.Targets[0], anchor = candidate.Context[0];
                if (!words.TryGetValue(target.TokenIndex, out Word? attribute) || candidate.Start != attribute.Token.Start || candidate.Length != attribute.Token.Length)
                    throw new ArgumentException("Attribute target has a stale original span.");
                bool found = false;
                if (nominalAttributes.TryGetValue(target.TokenIndex, out var bindings))
                    foreach (var bound in bindings)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!SameSupport(bound.Attribute, target) || !SameSupport(bound.Head, anchor)) continue;
                        AttributeHead(bound.Attribute, bound.Head, bound.Compatible, bound.PhraseInitial); found = true; break;
                    }
                if (!found) throw new ArgumentException("Syntax Attribute does not bind the actual same-candidate noun head.");
            }
        }
        MorphologyEvidenceProjection.View? originalRelationMorphology = null;
        static bool ReboundLane(string? lane) =>
            lane is ClauseFusionAnalyzer.SelectionLane or ClauseFusionAnalyzer.AgreementLane
                or ClauseFusionAnalyzer.SharedSubjectCoordinationRule
                or ContextLanes.ExactAgreementLane or LocalClauseRoleLinker.Lane ||
            ContextLanes.IsPronounPrefixLane(lane);
        var claimedRoles = context.Sentences.SelectMany(sentence => sentence.Candidates
            .Where(candidate => ReboundLane(candidate.AuthorityLane))
            .Select(candidate => (Sentence: sentence, Candidate: candidate))).ToArray();
        bool claimsSelectedFusion = context.Sentences.Any(s =>
            s.EvidenceCodes.Contains("accepted_authority_lane=" + ClauseFusionAnalyzer.SelectionLane, StringComparer.Ordinal));
        if (claimedRoles.Length > 0 || claimsSelectedFusion || analysis.Diagnostics.Any(d =>
                d.Code != ContextLanes.ProductiveAttributeMismatch && ReboundLane(d.RuleId) ||
                d.RuleId == TextContextAnalyzer.DeicticTemporalTenseRule ||
                d.Code == "deictic_temporal_tense_mismatch"))
        {
            // Rebind to original snapshots, not to claimed candidate roles or display readings.
            int previousEnd = 0;
            foreach (SentenceSpan sentence in analysis.Sentences)
            {
                ct.ThrowIfCancellationRequested();
                if (sentence.Start < previousEnd) throw new ArgumentException("Exact agreement sentences must be ordered original spans.");
                RequireSpan(analysis.Text, sentence.Start, sentence.Length);
                previousEnd = sentence.Start + sentence.Length;
            }
            if (HasClaim(ContextLanes.SubordinatePronounPrefixLane) || HasClaim(LocalClauseRoleLinker.Lane))
            {
                string basis = HasClaim(ContextLanes.SubordinatePronounPrefixLane) ? "Subordinate prefix" : "Local clause role";
                RequireOriginalRoleSource(analysis, basis, ct);
                if (!SentenceBoundaryDetector.Split(analysis.Text, analysis.Tokens, analysis.ReadingInventory).SequenceEqual(analysis.Sentences))
                    throw new ArgumentException(basis + " needs the original sentence edges.");
            }

            bool HasClaim(string lane) => claimedRoles.Any(c => c.Candidate.AuthorityLane == lane) ||
                analysis.Diagnostics.Any(d => d.RuleId == lane);
            originalRelationMorphology = MorphologyEvidenceProjection.Create(analysis, ct);
            var (measured, _) = TextContextAnalyzer.Calculate(analysis, options, ct, originalRelationMorphology,
                verbFrames: verbFrames, postverbalDirectObjectMaxRatio: postverbalDirectObjectMaxRatio,
                directObjectFrame: directObjectFrame, lookup: exactLookup, verbAuthority: verbAuthority);
            var bySpan = measured.Sentences.ToLookup(sentence => (sentence.Start, sentence.Length));
            foreach (var claim in claimedRoles)
            {
                ct.ThrowIfCancellationRequested();
                if (!bySpan[(claim.Sentence.Start, claim.Sentence.Length)].SelectMany(s => s.Candidates)
                    .Any(expected => SameRoleCandidate(claim.Candidate, expected)))
                    throw new ArgumentException(claim.Candidate.AuthorityLane is LocalClauseRoleLinker.Lane
                        or ClauseFusionAnalyzer.AgreementLane or ClauseFusionAnalyzer.SelectionLane or ContextLanes.ExactAgreementLane
                        ? "Exact role candidate does not bind its complete original relation."
                        : "Pronoun prefix candidate does not bind its complete original relation.");
            }
            foreach (TextContextSentence sentence in context.Sentences)
            {
                ct.ThrowIfCancellationRequested();
                bool claimsSelection = sentence.EvidenceCodes.Contains(
                    "accepted_authority_lane=" + ClauseFusionAnalyzer.SelectionLane, StringComparer.Ordinal) ||
                    sentence.Candidates.Any(c => c.AuthorityLane == ClauseFusionAnalyzer.SelectionLane);
                if (!claimsSelection) continue;
                TextContextSentence? expectedSentence = bySpan[(sentence.Start, sentence.Length)].FirstOrDefault();
                if (expectedSentence is null ||
                    sentence.Status != expectedSentence.Status ||
                    sentence.SelectedPredicateTokenIndex != expectedSentence.SelectedPredicateTokenIndex ||
                    sentence.SelectedRoles.Count != expectedSentence.SelectedRoles.Count ||
                    !sentence.SelectedRoles.Zip(expectedSentence.SelectedRoles).All(pair => SameRole(pair.First, pair.Second)) ||
                    !expectedSentence.Candidates.Where(c => c.AuthorityLane == ClauseFusionAnalyzer.SelectionLane)
                        .All(exp => sentence.Candidates.Any(act => SameRoleCandidate(act, exp))))
                    throw new ArgumentException("Selected clause role proof does not bind its complete original relation.");
            }
            TextDiagnostic[] exactAgreement = analysis.Diagnostics.Where(d =>
                d.Code != ContextLanes.ProductiveAttributeMismatch && ReboundLane(d.RuleId) ||
                d.RuleId == TextContextAnalyzer.DeicticTemporalTenseRule ||
                d.Code == "deictic_temporal_tense_mismatch" ||
                d.Code == ContextLanes.ProductiveGraphMismatch &&
                !PredicativeGrammarEvidence.IsDiagnostic(d) &&
                d.GraphReadings is null && d.Readings is not null && claimedRoles.Any(claim =>
                    claim.Candidate.Roles.Any(role => role.Role == TextContextRole.Predicate && role.Start == d.Start && role.Length == d.Length))).ToArray();
            IReadOnlyList<TextDiagnostic> expectedAgreement = TextContextAnalyzer.GraphAgreementDiagnostics(measured, ct, analysis.Diagnostics);
            foreach (TextDiagnostic diagnostic in exactAgreement)
            {
                ct.ThrowIfCancellationRequested();
                if (!expectedAgreement.Any(expected => SameDiagnostic(expected, diagnostic)))
                    throw new ArgumentException("Exact agreement does not bind its original subject/predicate relation.");
            }
        }
        static bool SameRole(TextContextRoleSelection first, TextContextRoleSelection second) =>
            first.Role == second.Role && first.TokenIndex == second.TokenIndex &&
            first.Start == second.Start && first.Length == second.Length &&
            first.GraphReadings is null && second.GraphReadings is null &&
            first.Readings.SequenceEqual(second.Readings) && first.EvidenceCodes.SequenceEqual(second.EvidenceCodes);
        bool SameRoleCandidate(TextContextCandidate actual, TextContextCandidate expected) =>
            actual.AuthorityLane == expected.AuthorityLane &&
            actual.PredicateTokenIndex == expected.PredicateTokenIndex && actual.Selectable == expected.Selectable &&
            actual.Score == expected.Score && actual.CanonicalKey == expected.CanonicalKey &&
            actual.EvidenceCodes.SequenceEqual(expected.EvidenceCodes) && actual.Roles.Count == expected.Roles.Count &&
            actual.Roles.Zip(expected.Roles).All(pair => SameRole(pair.First, pair.Second));
        static bool SameDiagnostic(TextDiagnostic expected, TextDiagnostic diagnostic) =>
            expected.Code == diagnostic.Code && expected.RuleId == diagnostic.RuleId &&
            expected.Start == diagnostic.Start && expected.Length == diagnostic.Length && expected.ProjectedText == diagnostic.ProjectedText &&
            (expected.Readings is null) == (diagnostic.Readings is null) &&
            (expected.GraphReadings is null) == (diagnostic.GraphReadings is null) &&
            (expected.Readings ?? []).SequenceEqual(diagnostic.Readings ?? []) &&
            (expected.GraphReadings ?? []).SequenceEqual(diagnostic.GraphReadings ?? []);
        IReadOnlyList<PredicativeGrammarEvidence.Frame>? predicativeFrames = null;
        TextDiagnostic[] predicativeDiagnostics = analysis.Diagnostics.Where(PredicativeGrammarEvidence.IsDiagnostic).ToArray();
        if (predicativeDiagnostics.Length > 0)
        {
            RequireOriginalRoleSource(analysis, "Predicative agreement", ct);
            if (!SentenceBoundaryDetector.Split(analysis.Text, analysis.Tokens, analysis.ReadingInventory).SequenceEqual(analysis.Sentences))
                throw new ArgumentException("Predicative agreement needs the original sentence edges.");
            predicativeFrames = PredicativeGrammarEvidence.Bind(analysis,
                originalRelationMorphology ??= MorphologyEvidenceProjection.Create(analysis, ct), includeNominalReview: true, ct);
            var expected = predicativeFrames.SelectMany(PredicativeGrammarEvidence.Diagnostics).ToArray();
            foreach (var diagnostic in predicativeDiagnostics)
            {
                ct.ThrowIfCancellationRequested();
                if (!expected.Any(row => SameDiagnostic(row, diagnostic)))
                    throw new ArgumentException("Predicative agreement does not bind its original local relation.");
            }
        }
        TextDiagnostic[] attributeDiagnostics = analysis.Diagnostics.Where(d =>
            d.Code == ContextLanes.ProductiveAttributeMismatch ||
            d.RuleId == TextContextAnalyzer.LocalAttributeAgreementRule).ToArray();
        if (attributeDiagnostics.Length > 0)
        {
            TextDiagnostic[] oldAttributes = TextContextAnalyzer.GraphAgreementDiagnostics(context, ct)
                .Where(d => d.Code == ContextLanes.ProductiveAttributeMismatch).ToArray();
            var oldTargets = oldAttributes.Select(d => (d.Code, d.Start, d.Length)).ToHashSet();
            TextDiagnostic[] expectedAttributes = oldAttributes.Concat(
                TextContextAnalyzer.LocalAttributeAgreementDiagnostics(analysis, ct,
                    originalRelationMorphology ??= MorphologyEvidenceProjection.Create(analysis, ct), includeOpenNominal: includeOpenNominal,
                    predicativeFrames: predicativeFrames).Where(d =>
                    !oldTargets.Contains((d.Code, d.Start, d.Length)))).ToArray();
            foreach (TextDiagnostic diagnostic in attributeDiagnostics)
            {
                ct.ThrowIfCancellationRequested();
                if (!expectedAttributes.Any(expected => SameDiagnostic(expected, diagnostic)))
                    throw new ArgumentException("Attribute mismatch does not bind its original attribute/head relation.");
                if (ReboundLane(diagnostic.RuleId) && diagnostic.GraphReadings is null)
                {
                    RequireSpan(analysis.Text, diagnostic.Start, diagnostic.Length);
                    SentenceSpan? sentence = analysis.Sentences.SingleOrDefault(s => Within(s.Start, s.Length, diagnostic.Start, diagnostic.Length));
                    if (sentence is null || diagnostic.Readings is not { Count: > 0 } ||
                        !diagnostic.Readings.Any(reference => words.TryGetValue(reference.TokenIndex, out Word? word) &&
                            diagnostic.Start == word.Token.Start && diagnostic.Length == word.Token.Length))
                        throw new ArgumentException("Exact attribute diagnostic does not cite its target Word span.");
                    foreach (TextReadingReference reference in diagnostic.Readings)
                    {
                        Check([reference], reference.TokenIndex);
                        if (!Within(sentence.Start, sentence.Length, words[reference.TokenIndex].Token.Start, words[reference.TokenIndex].Token.Length))
                            throw new ArgumentException("Exact attribute diagnostic support belongs to another sentence.");
                    }
                }
            }
        }
        foreach (TextDiagnostic diagnostic in analysis.Diagnostics.Where(d => d.GraphReadings is { Count: > 0 }))
        {
            RequireSpan(analysis.Text, diagnostic.Start, diagnostic.Length);
            SentenceSpan? sentence = analysis.Sentences.SingleOrDefault(s => Within(s.Start, s.Length, diagnostic.Start, diagnostic.Length));
            if (diagnostic.Code == ContextLanes.ProductiveAttributeMismatch &&
                diagnostic.RuleId != TextContextAnalyzer.LocalAttributeAgreementRule &&
                !TextContextAnalyzer.GraphAgreementDiagnostics(context, ct).Any(d =>
                d.Code == diagnostic.Code && d.RuleId == diagnostic.RuleId && d.Start == diagnostic.Start && d.Length == diagnostic.Length &&
                (d.Readings ?? []).SequenceEqual(diagnostic.Readings ?? []) && (d.GraphReadings ?? []).SequenceEqual(diagnostic.GraphReadings ?? [])))
                throw new ArgumentException("Attribute mismatch does not bind its original attribute/head relation.");
            if (sentence is null || !diagnostic.GraphReadings!.Any(reference => words.TryGetValue(reference.TokenIndex, out Word? word) &&
                diagnostic.Start == word.Token.Start && diagnostic.Length == word.Token.Length))
                throw new ArgumentException("Graph diagnostic is not an original graph-supported Word span.");
            foreach (TextReadingReference reference in diagnostic.Readings ?? [])
            {
                Check([reference], reference.TokenIndex);
                Word word = words[reference.TokenIndex];
                if (!Within(sentence.Start, sentence.Length, word.Token.Start, word.Token.Length))
                    throw new ArgumentException("Graph diagnostic exact support belongs to another sentence.");
            }
            foreach (TextGraphReadingReference reference in diagnostic.GraphReadings!)
            {
                if (!words.TryGetValue(reference.TokenIndex, out Word? word) ||
                    !Within(sentence.Start, sentence.Length, word.Token.Start, word.Token.Length))
                    throw new ArgumentException("Graph diagnostic support belongs to another sentence.");
                MorphologyEvidenceProjection.RequireGraphSupport(word.Token, reference, reference.TokenIndex, word.Rows.Count, ct);
            }
        }
    }

    private static void AddCoordinatedArticle(string text, SentenceSpan sentence, Word[] words,
        TextContextEvidence context, TextSyntaxEvidence syntax,
        Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        List<TextValidationDiagnostic> output, CancellationToken ct)
    {
        if (words.Length < 4 || !Article(words[0], MorphologicalArticle.Short)) return;
        TextSyntaxSentence[] syntaxSentences = syntax.Sentences.Where(s => s.Start == sentence.Start && s.Length == sentence.Length).ToArray();
        if (syntaxSentences.Length != 1) return;
        TextSyntaxCandidate[] predicates = syntaxSentences[0].Candidates.Where(c => c.Role == TextSyntaxRole.Predicate &&
            c.RuleId != nameof(SyntaxRoleCalculator.Rule.CoordinatedPredicate)).ToArray();
        if (predicates.Length != 1 || predicates[0].Targets.Count != 1 || predicates[0].Targets[0].Readings.Count == 0 ||
            predicates[0].Targets[0].GraphReadings is { Count: > 0 }) return;
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
            r.GraphReadings is not { Count: > 0 } &&
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
            if (target is null || support.Readings.Count == 0 || support.GraphReadings is { Count: > 0 } || target.Token.Start <= words[0].Token.Start ||
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

    private static string? FullCounterpart(Word word, Func<string, IReadOnlyList<TextReadingSnapshot>> lookup,
        CancellationToken ct, bool allowSameLemmaParadigmVariants = false)
    {
        string surface = Key(word.Token.Text), lemma = Key(word.Rows[0].Raw.Lemma);
        MorphologicalPartOfSpeech pos = word.Rows[0].Features.PartOfSpeech;
        bool passive = ContextEvidenceGuards.IsMeasuredPassiveAttribute(word.Rows[0].Features);
        string proposal;
        if ((pos == MorphologicalPartOfSpeech.Adjective || passive) && surface.EndsWith("ия", StringComparison.Ordinal)) proposal = surface + "т";
        else if (pos == MorphologicalPartOfSpeech.Noun && surface.Length > 1 && surface != lemma &&
                 (surface.EndsWith("а", StringComparison.Ordinal) || surface.EndsWith("я", StringComparison.Ordinal)))
            proposal = surface[..^1] + (surface.EndsWith("я", StringComparison.Ordinal) ? "ят" : "ът");
        else return null;
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<TextReadingSnapshot> rows = lookup(proposal) ?? throw new ArgumentException("Exact callback returned null.");
        ct.ThrowIfCancellationRequested();
        HashSet<string?> allowedParadigms = allowSameLemmaParadigmVariants
            ? word.Rows.Select(w => w.Features.ParadigmId).ToHashSet(StringComparer.Ordinal)
            : [word.Rows[0].Features.ParadigmId];
        if (rows.Count == 0 || rows.Any(r => Key(r.Raw.Form) != proposal || Key(r.Raw.Lemma) != lemma ||
            r.Features.HasConflictingFeatures || r.Features.PartOfSpeech != pos || !allowedParadigms.Contains(r.Features.ParadigmId) ||
            r.Features.Gender != MorphologicalGender.Masculine || r.Features.Number != MorphologicalNumber.Singular ||
            !(r.Features.Article == MorphologicalArticle.Full ||
              passive && ContextEvidenceGuards.IsMeasuredPassiveAttribute(r.Features) && r.Features.Article == MorphologicalArticle.Definite))) return null;
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
        List<TextValidationDiagnostic> output, List<string> unavailable, ref List<(int Start, int End)>? quotes, CancellationToken ct)
    {
        for (int index = 1; index < words.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            Word trigger = words[index];
            string key = Key(trigger.Token.Text);
            if (!BulgarianFunctionWords.HasAny(key, FunctionWordClasses.CommaSubordinateTrigger)) continue;
            bool reviewedMarker = key is "понеже" or "докато";
            if (reviewedMarker && (!CompleteCommaRows(trigger, MorphologicalPartOfSpeech.Conjunction) ||
                index + 1 == words.Length ||
                !Bridge(text, words[index - 1].Token.Start + words[index - 1].Token.Length, trigger.Token.Start) ||
                !Bridge(text, trigger.Token.Start + trigger.Token.Length, words[index + 1].Token.Start)))
            { AddUnavailable(unavailable, "subordinate_comma:reviewed_marker_authority_unavailable"); continue; }
            bool relative = !reviewedMarker && key != "ако" && trigger.Rows.Any(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun);
            bool marker = reviewedMarker || trigger.Rows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction);
            if (!relative && !marker) { AddUnavailable(unavailable, "subordinate_comma:trigger_authority_unavailable"); continue; }
            int boundary = index;
            int compound = key == "че" ? CompoundCheStart(text, words, index) : index;
            int compoundQualifier = compound == index - 2 ? CommaQualifierStart(text, words, compound) : -1;
            if (compound < index)
            {
                var previous = words[index - 1].Token;
                var firstWord = words[compound].Token;
                int lead = compoundQualifier >= 0 ? compoundQualifier : compound;
                var leadWord = words[lead].Token;
                bool atBoundary = HasSeparatorBefore(text, sentence.Start, leadWord.Start) ||
                    lead > 0 && Key(words[lead - 1].Token.Text) is "и" or "или" or "но" or "а" &&
                    Bridge(text, words[lead - 1].Token.Start + words[lead - 1].Token.Length, leadWord.Start);
                // Independently used as an adverb in e.g. „толкова независимо, че …“.
                bool possibleAdverb = !atBoundary && Key(previous.Text) == "независимо";
                int end = previous.Start + previous.Length;
                ReadOnlySpan<char> gap = text.AsSpan(end, trigger.Token.Start - end);
                if (gap.Trim().SequenceEqual(",") && gap.IndexOfAny('\r', '\n', '\u2029') < 0)
                {
                    int matrix = !atBoundary && compound == index - 2
                        ? EmbeddedCompoundMatrix(text, words, compound, compoundQualifier, ct) : -1;
                    if (atBoundary || compound == index - 1 && !possibleAdverb || matrix >= 0)
                    {
                        quotes ??= TextPredicateLinkAnalyzer.QuoteSpans(text, ct, protectBackticks: true);
                        if (!quotes.Any(q => q.Start <= trigger.Token.Start && q.End > firstWord.Start))
                        {
                            Word? follower = AddInternalComma(text, words, compound, index, end + gap.IndexOf(','), output, ct, matrix);
                            if (matrix >= 0 && compoundQualifier < 0 && follower is not null)
                                output.Add(new(CommaCode, firstWord.Start, trigger.Token.Start + trigger.Token.Length - firstWord.Start,
                                    "subordinate_marker;complete_any_verb_topology;source_separator_absent;candidate_only;not_grammar_verdict",
                                    Supports(words[matrix..(index + 1)].Append(follower))));
                        }
                    }
                    continue;
                }
                if (atBoundary && Bridge(text, end, trigger.Token.Start)) continue;
                if (possibleAdverb) compound = index;
            }
            if (Bridge(text, words[index - 1].Token.Start + words[index - 1].Token.Length, trigger.Token.Start))
            {
                if (key == "ако" && Key(words[index - 1].Token.Text) == "освен") boundary--;
                else if (relative && All(words[index - 1], f => f.PartOfSpeech == MorphologicalPartOfSpeech.Preposition) ||
                    key == "че" && Key(words[index - 1].Token.Text) == "така") boundary--;
                else boundary = compound;
            }
            Word first = words[boundary];
            // Keep the existing rule90 cells local to reviewed markers and conditional compounds;
            // adversative само че and unrelated markers retain their previous behavior.
            if ((key == "ако" || reviewedMarker || compound == index - 2) &&
                CommaQualifierStart(text, words, boundary) >= 0) continue;
            if (HasSeparatorBefore(text, sentence.Start, first.Token.Start) || boundary > 0 &&
                Key(words[boundary - 1].Token.Text) is "и" or "или" &&
                Bridge(text, words[boundary - 1].Token.Start + words[boundary - 1].Token.Length, first.Token.Start)) continue;
            if (reviewedMarker && boundary > 0 && Key(words[boundary - 1].Token.Text) is "а" or "но" or "че" &&
                Bridge(text, words[boundary - 1].Token.Start + words[boundary - 1].Token.Length, first.Token.Start)) continue;
            Word[] before = words[..boundary].Where(HasVerb).ToArray();
            Word[] after = words[(index + 1)..].Where(HasVerb).ToArray();
            if (after.Length == 0 || (!relative && before.Length == 0)) continue;
            if (reviewedMarker && before.Concat(after).Any(word => !CompleteCommaRows(word, MorphologicalPartOfSpeech.Verb)))
            { AddUnavailable(unavailable, "subordinate_comma:reviewed_verb_authority_unavailable"); continue; }
            Word[] introducer = key == "ако" ? words[boundary..(index + 1)] : [trigger];
            output.Add(new(CommaCode, first.Token.Start, trigger.Token.Start + trigger.Token.Length - first.Token.Start,
                reviewedMarker ?
                    "reviewed_literal_subordinate_marker;possible_finite_verb_topology;complete_context_rows_retained;source_separator_absent;candidate_only;not_grammar_verdict" :
                    "subordinate_marker;complete_any_verb_topology;source_separator_absent;candidate_only;not_grammar_verdict",
                Supports(before.Concat(introducer).Concat(after))));
        }
    }

    private static int CompoundCheStart(string text, Word[] words, int index)
    {
        string prefix = Key(words[index - 1].Token.Text);
        if (prefix is "въпреки" or "макар" or "независимо") return index - 1;
        if (index < 2 || prefix is not ("случай" or "условие")) return index;
        var first = words[index - 2].Token;
        return Key(first.Text) == (prefix == "случай" ? "в" : "при") &&
            Bridge(text, first.Start + first.Length, words[index - 1].Token.Start) ? index - 2 : index;
    }

    private static int CommaQualifierStart(string text, Word[] words, int boundary)
    {
        if (boundary == 0 || !Bridge(text, words[boundary - 1].Token.Start + words[boundary - 1].Token.Length,
                words[boundary].Token.Start)) return -1;
        string qualifier = Key(words[boundary - 1].Token.Text);
        if (qualifier is "не" or "даже" or "дори" or "дълго" or "едва" or "единствено" or "именно" or
            "много" or "малко" or "особено" or "поне" or "само" or "тъкмо" or "чак") return boundary - 1;
        return qualifier == "скоро" && boundary >= 2 && Key(words[boundary - 2].Token.Text) == "твърде" &&
            Bridge(text, words[boundary - 2].Token.Start + words[boundary - 2].Token.Length, words[boundary - 1].Token.Start)
            ? boundary - 2 : -1;
    }

    private static int EmbeddedCompoundMatrix(string text, Word[] words, int first, int qualifier, CancellationToken ct)
    {
        // An unmodified в случай че / при условие че after a connected finite clause
        // has two punctuation defects. Modified literal nouns retain the ordinary че boundary.
        // The dictionary's ordinary conjunction/particle ambiguity of че remains in both candidates.
        if (!CompleteCommaRows(words[first], MorphologicalPartOfSpeech.Preposition) ||
            !CompleteCommaRows(words[first + 1], MorphologicalPartOfSpeech.Noun) ||
            !ExactCommaRows(words[first + 2]) ||
            !words[first + 2].Rows.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Conjunction) ||
            !All(words[first + 2], f => f.PartOfSpeech is MorphologicalPartOfSpeech.Conjunction or MorphologicalPartOfSpeech.Particle)) return -1;
        // Rule90 is lexical: preserve every original qualifier reading rather than
        // requiring its adjective/noun homographs to become adverb readings.
        for (int index = (qualifier >= 0 ? qualifier : first) - 1; index >= 0; index--)
        {
            ct.ThrowIfCancellationRequested();
            Word word = words[index];
            if (!Bridge(text, word.Token.Start + word.Token.Length, words[index + 1].Token.Start) ||
                !ExactCommaRows(word)) return -1;
            if (CompleteCommaRows(word, MorphologicalPartOfSpeech.Verb)) return index;
            if (!All(word, f => f.PartOfSpeech is MorphologicalPartOfSpeech.Adverb or MorphologicalPartOfSpeech.Particle)) return -1;
        }
        return -1;
    }

    private static Word? AddInternalComma(string text, Word[] words, int first, int marker, int comma,
        List<TextValidationDiagnostic> output, CancellationToken ct, int matrix = -1)
    {
        for (int i = first; i <= marker; i++)
            if (!ExactCommaRows(words[i])) return null;
        for (int i = marker + 1; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var previous = words[i - 1].Token;
            if (!Bridge(text, previous.Start + previous.Length, words[i].Token.Start)) return null;
            if (!words[i].Rows.Any(r => r.Features.VerbForm == MorphologicalVerbForm.Finite &&
                r.Features.Tense is not null && ContextEvidenceGuards.IsPersonalPredicate(r.Features)) ||
                !ExactCommaRows(words[i])) continue;
            output.Add(new("spurious_comma_inside_compound_conjunction", comma, 1,
                "compound_conjunction;internal_comma;finite_verb_reading;candidate_only;not_grammar_verdict;no_automatic_rewrite",
                Supports(words[(matrix >= 0 ? matrix : first)..(marker + 1)].Append(words[i]))));
            return words[i];
        }
        return null;
    }

    private static bool ExactCommaRows(Word word) => word.CanonicalSource &&
        word.Rows.Count > 0 && word.Rows.All(row => row.Features == row.Raw.Features && !row.Features.HasConflictingFeatures &&
            !row.Features.HasUnrecognizedFeatures && Key(row.Raw.Form) == Key(word.Token.Text));

    private static bool CompleteCommaRows(Word word, MorphologicalPartOfSpeech category) => ExactCommaRows(word) &&
        word.Rows.All(row => (row.Features.PartOfSpeech == category || category == MorphologicalPartOfSpeech.Conjunction &&
                row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb) &&
            (category != MorphologicalPartOfSpeech.Verb ||
                row.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative)) &&
        (category != MorphologicalPartOfSpeech.Verb || word.Rows.Any(row =>
            row.Features.VerbForm == MorphologicalVerbForm.Finite && row.Features.Person is not null &&
            row.Features.Number is not null && row.Features.Tense is not null));

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
        ReadOnlySpan<char> source = text.AsSpan(first, end - first + 1);
        if (BulgarianAbbreviationLexicon.TryNormalizeExact(source, out _)) return [];
        char last = '\0', previous = ' ';
        for (int index = first; index <= end; index++)
        {
            ct.ThrowIfCancellationRequested(); char ch = text[index];
            bool attachedMark = (ch is '\u0300' or '\u0301' or '\u0306') &&
                Bulgarian(last) && BulgarianTokenizer.IsWordContinuation(source, index - first);
            if (Bulgarian(ch))
            {
                if (index > first && Bulgarian(last) && char.IsWhiteSpace(previous)) Add("word_phrase_internal_space_after_letter_run");
                last = ch;
            }
            else if (ch == '.') { if (last != '-' && !Bulgarian(last)) Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            else if (ch == '-') { if (!Bulgarian(last)) Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            else if (ch == '/') { if (last != '.' && !Bulgarian(last)) Add("invalid_word_phrase_compact_separator_order"); last = ch; }
            else if (!char.IsWhiteSpace(ch) && !attachedMark) { Add("invalid_word_phrase_compact_separator_order"); last = ch; }
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
    private static bool Bulgarian(char ch) => ch is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ѝ' or 'Ѝ' or 'ѐ' or 'Ѐ';
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
