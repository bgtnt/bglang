using BgLang.Contracts;
using Guard = BgLang.Core.ContextEvidenceGuards;
using Role = BgLang.Core.ContextOverlayRole;
using Word = BgLang.Core.ContextLanes.TokenView;

namespace BgLang.Core;

// Binds one finite frame to original readings. A role hypothesis never enters contextual selection.
internal static class LocalClauseRoleLinker
{
    internal const string Lane = "local_finite_clause_roles.v1";
    internal const string Conditional = "subject_agreement_if_subject";

    internal static ContextOverlayCandidate? Calculate(TextAnalysis analysis, SentenceSpan sentence,
        IReadOnlyList<Word> words, Func<IReadOnlyList<ContextOverlayRoleSelection>, string> sourceKey,
        CancellationToken ct)
    {
        if (words.Count < 2 || !analysis.Text.AsSpan(sentence.Start, words[0].Token.Start - sentence.Start).Trim().IsEmpty)
            return null;

        if (CollectFiniteFrame(analysis, words, ct) is not { } frame || frame.Predicate < 1) return null;
        var (predicate, end, literal) = frame;
        Word verb = words[predicate];
        bool transitive = verb.Readings.All(r => r.Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Transitive);
        bool intransitive = verb.Readings.All(r => r.Features.DeclaredTransitivity == MorphologicalVerbTransitivity.Intransitive);
        bool copularRival = verb.Readings.Any(r => TextPredicateLinkAnalyzer.CopularLemma(r.Lemma));

        int head = predicate - 1;
        int[] clitic = transitive && Exact(words[head])
            ? Indices(words[head]).Where(i => Guard.IsAccusativeCliticReading(words[head].Readings[i])).ToArray() : [];
        if (clitic.Length > 0) head--;
        if (head < 0 || !Exact(words[head])) return null;

        Word nominal = words[head];
        bool pronoun = nominal.Readings.All(r => Guard.LicensedSubject(nominal.CanonicalText, r));
        var binding = pronoun ? null : Phrase(head);
        int first = binding?.Participants[0].WordIndex ?? head;
        int prep = first > 0 && Guard.IsExactAdposition(words[first - 1]) && Gap(first - 1, first) ? first - 1 : -1;
        var governedMembers = prep == 0 ? Governed(1, head) : null;
        var prefixMembers = first > 1 && prep < 0 && Guard.IsExactAdposition(words[0])
            ? Governed(1, first - 1) : null;
        bool governed = governedMembers is not null;
        bool prefix = prefixMembers is not null;
        if (first != 0 && !governed && !prefix) return null;
        if (!governed && !pronoun && binding is null) return null;
        bool coherent = binding is null || Coherent(binding);
        if (binding is not null && NominalStructuralProjection.AssessPartition(analysis.Text, words, first, head, ct) !=
            NominalStructuralProjection.Partition.None) return null;

        bool article = binding is not null && binding.Participants[0].Word.Readings.All(r => r.Features is
            { Article: MorphologicalArticle.Full, Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine });
        bool resumed = coherent && clitic.Length > 0 && !pronoun && !article && nominal.Readings.Any(n =>
            Guard.HasNominalAxes(n.Features) && clitic.Any(i =>
            {
                var f = words[predicate - 1].Readings[i].Features;
                return f.Person == MorphologicalPerson.Third &&
                    Guard.NominalTupleMatches((n.Features.Number, n.Features.Gender), (f.Number, f.Gender));
            }));
        bool strictClitic = clitic.Length == 0 ||
            Guard.AccusativeCliticReadings(words[predicate - 1], ct).Length == clitic.Length;
        bool closedTail = AdverbialTail(predicate + 1, end);
        // Full article also occurs in predicative and vocative constructions. The
        // local article alone cannot establish subjecthood independently of agreement.
        bool licensed = pronoun && prefix && literal && strictClitic;
        string basis = governed ? "locally_governed_nominal" : resumed ? "resumed_nominal_object_candidate" :
            licensed ? "canonical_subject_after_governed_prefix" : !coherent ? "nominal_members_not_all_compatible" :
            article ? "full_article_subject_or_predicative" : transitive ? "possible_direct_object_role" :
            "nominal_clause_role_unresolved";
        // Existing initial pronoun routes already bind the same relation.
        if (!governed && pronoun && !licensed) return null;
        if (!literal && (governed || resumed || transitive || article)) return null;
        if (!licensed && !governed && !resumed && !closedTail) return null;

        bool conflict = coherent && !governed && !resumed && nominal.Readings.All(n =>
            n.Features.Number is MorphologicalNumber.Singular or MorphologicalNumber.Plural &&
            verb.Readings.All(v => !Guard.PersonNumberTupleMatches(
                (pronoun ? n.Features.Person : MorphologicalPerson.Third, n.Features.Number),
                (v.Features.Person, v.Features.Number))));
        if (!licensed && !governed && !resumed && !conflict && coherent) return null;

        List<ContextOverlayRoleSelection> roles =
        [
            Evidence(Role.Predicate, predicate, Indices(verb), "complete_exact_finite_readings"),
            Evidence(licensed ? Role.Subject : Role.NominalContext, head, governedMembers?[^1].RoleReadings ?? Indices(nominal), basis)
        ];
        if (binding is not null)
            foreach (var member in binding.Participants.Take(binding.Participants.Count - 1))
                roles.Add(Evidence(Role.Attribute, member.WordIndex, member.RoleReadings, "nominal_member_candidate"));
        if (governed || prefix)
        {
            int[] prepositions = Indices(words[0]).Where(i => words[0].Readings[i].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition).ToArray();
            roles.Add(Evidence(Role.Adposition, 0, prepositions, "exact_preposition_governor"));
            if (prefixMembers is not null)
                foreach (var member in prefixMembers)
                    roles.Add(Evidence(Role.NominalContext, member.WordIndex, member.RoleReadings, "governed_prefix"));
        }
        if (clitic.Length > 0)
            roles.Add(Evidence(Role.NominalContext, predicate - 1, clitic, "accusative_clitic_witness"));

        List<string> evidence = [basis];
        if (licensed && conflict) evidence.Add(ContextLanes.ProductiveGraphMismatch);
        else if (conflict && intransitive && !copularRival) evidence.Add(Conditional);
        return new(verb.OriginalIndex, false, Lane, 0, sourceKey(roles), roles, evidence);

        ContextOverlayRoleSelection Evidence(Role role, int i, IReadOnlyList<int> readings, string basis) =>
            new(role, words[i].OriginalIndex, words[i].Token.Start, words[i].Token.Length, readings, [basis]);

        NominalStructuralProjection.Binding? Phrase(int last) =>
            NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, last, ct, exactContiguous: true);

        bool Coherent(NominalStructuralProjection.Binding phrase) =>
            NominalStructuralProjection.Measure(phrase, ct) is { } measured &&
            measured.Heads.All(head => head.Attributes.Select((readings, i) =>
                readings.Length == phrase.Participants[i].RoleReadings.Count).All(equal => equal));

        IReadOnlyList<NominalStructuralProjection.Member>? Governed(int start, int last)
        {
            for (int i = start - 1; i < last; i++)
                if (!Gap(i, i + 1)) return null;
            Word word = words[last];
            if (last == start && Exact(word))
            {
                int[] oblique = Guard.ObliquePronounReadings(word, ct);
                if (oblique.Length > 0) return [new(last, word, oblique)];
                if (word.Readings.All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                    !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures))
                {
                    int[] nouns = Indices(word).Where(i => Guard.HasNominalAxes(word.Readings[i].Features)).ToArray();
                    if (nouns.Length > 0) return [new(last, word, nouns)];
                }
            }
            return Phrase(last) is { } phrase && phrase.Participants[0].WordIndex == start && Coherent(phrase)
                ? phrase.Participants : null;
        }

        bool AdverbialTail(int start, int last)
        {
            while (start < last && Exact(words[start]) && words[start].Readings.All(r =>
                r.SourceAdverb && !r.Features.HasConflictingFeatures && !r.Features.HasUnrecognizedFeatures))
            {
                ct.ThrowIfCancellationRequested();
                if (!Gap(start - 1, start)) return false;
                start++;
            }
            return start == last || start + 1 < last && Gap(start - 1, start) &&
                Guard.IsExactAdposition(words[start]) && Governed(start + 1, last - 1) is not null;
        }

        bool Gap(int left, int right) => Connected(analysis, words[left], words[right], symbols: false, ct);

    }

    private static (int Predicate, int End, bool Literal)? CollectFiniteFrame(
        TextAnalysis analysis, IReadOnlyList<Word> words, CancellationToken ct)
    {
        int predicate = -1, end = 0;
        bool literal = true;
        for (; end < words.Count; end++)
        {
            ct.ThrowIfCancellationRequested();
            Word word = words[end];
            if (end > 0)
            {
                bool space = Connected(analysis, words[end - 1], word, symbols: false, ct);
                if (!space && !Connected(analysis, words[end - 1], word, symbols: true, ct)) break;
                if (predicate < 0) literal &= space;
            }
            if (!word.Readings.Any(TextPredicateLinkAnalyzer.Finite)) continue;
            if (predicate >= 0 || !Exact(word) || !word.Readings.All(TextPredicateLinkAnalyzer.Finite)) return null;
            predicate = end;
        }
        return predicate < 0 ? null : (predicate, end, literal);
    }

    internal static bool Connected(TextAnalysis analysis, Word left, Word right, bool symbols, CancellationToken ct)
    {
        int from = left.Token.Start + left.Token.Length;
        int to = right.Token.Start;
        if (from >= to) return false;
        for (int i = left.OriginalIndex + 1; i < right.OriginalIndex; i++)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[i];
            if (!symbols || token.Kind != TokenKind.Symbol || token.Start < from ||
                token.Start + token.Length > to || !analysis.Text.AsSpan(from, token.Start - from).Trim().IsEmpty)
                return false;
            from = token.Start + token.Length;
        }
        return analysis.Text.AsSpan(from, to - from).Trim().IsEmpty;
    }

    private static bool Exact(Word word) => word.Complete && !word.UsesGraph && word.Readings.Count > 0;

    private static int[] Indices(Word word) => Enumerable.Range(0, word.Readings.Count).ToArray();

}
