using BgLang.Contracts;
using Rows = BgLang.Core.MorphologyEvidenceProjection.Rows;

namespace BgLang.Core;

// One consumer of the shared local nominal binding. It adds advice, never Context/Syntax selections.
internal static class TextNominalConstructionAnalyzer
{
    internal static TextNominalConstructionEvidence Calculate(TextAnalysis analysis,
        MorphologyEvidenceProjection.View morphology, SpellingCandidateCache search, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (morphology.UnavailableReason is { } reason) return new("nominal_constructions.v1", false, reason, [], []);
        List<TextNominalPhrase> phrases = [];
        List<NominalRepairReview> reviews = [];
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            List<ContextLanes.TokenView> words = [];
            foreach (var entry in morphology.OrderedTokens)
            {
                ct.ThrowIfCancellationRequested();
                TokenAnalysis token = analysis.Tokens[entry.Index];
                if (token.Start < sentence.Start || (long)token.Start + token.Length > (long)sentence.Start + sentence.Length) continue;
                Rows rows = entry.Rows;
                words.Add(new(entry.Index, token, rows.Canonical, rows.Lanes, rows.GraphUnavailableReason is null)
                    { UsesGraph = rows.UsesGraph });
            }
            // Emit maximal, nonoverlapping cores, not every nested homograph head.
            // Each bound member is visited once; all raw alternative readings remain in the response.
            List<TextNominalPhrase> sentencePhrases = [];
            for (int head = words.Count - 1; head >= 0; head--)
            {
                ct.ThrowIfCancellationRequested();
                if (NominalStructuralProjection.BindHead(analysis.Text, sentence, analysis.Tokens, words, head, ct) is { } binding)
                {
                    sentencePhrases.Add(Project(binding, null));
                    head = binding.Participants[0].WordIndex;
                }
            }
            sentencePhrases.Reverse();
            phrases.AddRange(sentencePhrases);
            // One deliberately local construction: finite, known finite, nominal tail, then sentence end.
            // The original can be valid with another interpretation. Absence of this pattern is not an error.
            if (words.Count < 3 || !PureFinite(words[0]) || !PureFinite(words[1]) ||
                !Whitespace(words[0].Token.Start + words[0].Token.Length, words[1].Token.Start) ||
                !TerminalTail(words[^1].Token.Start + words[^1].Token.Length, sentence.Start + sentence.Length)) continue;
            ContextLanes.TokenView edited = words[1];
            if (edited.Token.Analysis?.IsKnown != true || edited.CanonicalText.Length > 256 ||
                analysis.Tokens.Any(t => t.Start >= sentence.Start && t.Start < words[0].Token.Start &&
                    t.Kind is not (TokenKind.Symbol or TokenKind.Whitespace)) ||
                analysis.Tokens.Any(t => t.Start >= sentence.Start && t.Start < edited.Token.Start &&
                    t.Text.Any(c => c is '\u201e' or '\u201c' or '\u00ab' or '\u00bb' or '\"' or '\'' or '\u201d' or '`'))) continue;
            // Existing lexical pool and ordering. Search cannot establish unique intent or exhaust the package.
            IReadOnlyList<FuzzyMatch> pool = search.FindSimilarNormalized(edited.CanonicalText, 1,
                TextSpellingAnalyzer.DefaultLimit, ct);
            List<NominalRepairAlternative> alternatives = [];
            foreach (FuzzyMatch candidate in pool)
            {
                ct.ThrowIfCancellationRequested();
                TextNominalPhrase? phrase = null;
                string key = BulgarianTextNormalizer.NormalizeKey(candidate.Form);
                if (candidate.Distance > 0 && candidate.Readings.Count > 0 &&
                    candidate.Readings.All(r => StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(r.Form), key)) &&
                    SingleWord(candidate))
                {
                    var lanes = Array.AsReadOnly(candidate.Readings.Select(r => ContextReadingProjection.Create(
                        new(r, r.Features), key).Lane).ToArray());
                    var branch = words.ToArray();
                    // Only morphology is replaced; the licensed single Word preserves source token topology/spans.
                    branch[1] = edited with { CanonicalText = key, Readings = lanes, Complete = true, UsesGraph = false };
                    if (PureFinite(branch[0]) && NominalStructuralProjection.BindHead(analysis.Text, sentence,
                        analysis.Tokens, branch, branch.Length - 1, ct) is { } target &&
                        target.Participants[0].Word.OriginalIndex == edited.OriginalIndex &&
                        target.Participants.Count + target.Bridges.Count(b => b.Conjunction is not null) == branch.Length - 1)
                    {
                        TextNominalPhrase measured = Project(target, edited.OriginalIndex);
                        if (measured.Assessment == NominalAgreementAssessment.Compatible) phrase = measured;
                    }
                }
                alternatives.Add(new(candidate, phrase));
            }
            // Do not create an empty suggestion merely because Context or Syntax abstained.
            if (alternatives.Any(a => a.Phrase is not null)) reviews.Add(new(edited.OriginalIndex,
                edited.Token.Start, edited.Token.Length, "finite+NP", true, 1, TextSpellingAnalyzer.DefaultLimit,
                false, Array.AsReadOnly(alternatives.ToArray())));
        }
        ct.ThrowIfCancellationRequested();
        return new("nominal_constructions.v1", true, null, Array.AsReadOnly(phrases.ToArray()), Array.AsReadOnly(reviews.ToArray()));

        TextNominalPhrase Project(NominalStructuralProjection.Binding binding, int? replacement)
        {
            ct.ThrowIfCancellationRequested();
            ContextLanes.NominalPhrase? relation = NominalStructuralProjection.Measure(binding, ct);
            List<NominalHeadSupport> heads = [];
            if (relation is not null)
            foreach (ContextLanes.NominalHead group in relation.Heads)
            {
                ct.ThrowIfCancellationRequested();
                List<NominalMemberSupport> members = [];
                for (int a = 0; a + 1 < binding.Participants.Count; a++)
                {
                    ct.ThrowIfCancellationRequested();
                    int token = binding.Participants[a].Word.OriginalIndex;
                    int[] matches = group.Attributes[a];
                    NominalAgreementAssessment state = matches.Length > 0 ? NominalAgreementAssessment.Compatible : NominalAgreementAssessment.Mismatch;
                    members.Add(token == replacement
                        ? new(token, state, [], []) { ReplacementReadings = Array.AsReadOnly(matches) }
                        : new(token, state, Exact(token, matches), Graph(token, matches)));
                }
                int head = binding.Head.Word.OriginalIndex;
                foreach (int h in group.Members)
                {
                    ct.ThrowIfCancellationRequested();
                    heads.Add(new(morphology.ByToken[head].UsesGraph ? null : new TextReadingReference(head, h),
                        morphology.ByToken[head].UsesGraph ? new TextGraphReadingReference(head,
                            morphology.ByToken[head].Graph[h].CandidateIndex, morphology.ByToken[head].Graph[h].FullTagIndex) : null, Array.AsReadOnly(members.ToArray())));
                }
            }
            NominalAgreementAssessment assessment = relation is null ? NominalAgreementAssessment.Unassessed :
                heads.Any(h => h.Members.All(m => m.Assessment == NominalAgreementAssessment.Compatible))
                    ? NominalAgreementAssessment.Compatible : NominalAgreementAssessment.Mismatch;
            return new("NP", binding.Start, binding.Length, binding.Head.Word.OriginalIndex,
                Array.AsReadOnly(binding.Participants.SkipLast(1).Select(p => p.Word.OriginalIndex).ToArray()),
                Array.AsReadOnly(binding.Bridges.Where(b => b.TokenIndex.HasValue).Select(b => b.TokenIndex!.Value).ToArray()),
                assessment, Array.AsReadOnly(heads.ToArray()))
            {
                ConjunctionReadings = binding.Bridges.Any(b => b.Conjunction is not null)
                    ? Array.AsReadOnly(binding.Bridges.Where(b => b.Conjunction is not null)
                        .SelectMany(b => b.WitnessReadings.Select(r => new TextReadingReference(b.Conjunction!.OriginalIndex, r))).ToArray()) : null
            };
        }
        IReadOnlyList<TextReadingReference> Exact(int token, IEnumerable<int> indices) => morphology.ByToken[token].UsesGraph
            ? [] : Array.AsReadOnly(indices.Select(i => new TextReadingReference(token, i)).ToArray());
        IReadOnlyList<TextGraphReadingReference> Graph(int token, IEnumerable<int> indices)
        {
            Rows rows = morphology.ByToken[token];
            return !rows.UsesGraph ? [] : Array.AsReadOnly(indices.Select(i => new TextGraphReadingReference(token,
                rows.Graph[i].CandidateIndex, rows.Graph[i].FullTagIndex)).ToArray());
        }
        bool PureFinite(ContextLanes.TokenView word) => word.Complete && word.Readings.Count > 0 &&
            word.Readings.All(r => { ct.ThrowIfCancellationRequested(); return r.Category == ContextLanes.Category.Verb &&
                ContextEvidenceGuards.IsPersonalPredicate(r.Features) &&
                r.Features.VerbForm == MorphologicalVerbForm.Finite; });
        bool Whitespace(int start, int end)
        {
            if (start >= end) return false;
            for (int i = start; i < end; i++) { ct.ThrowIfCancellationRequested(); if (!char.IsWhiteSpace(analysis.Text[i])) return false; }
            return true;
        }
        bool TerminalTail(int start, int end)
        {
            for (int i = start; i < end; i++)
            { ct.ThrowIfCancellationRequested(); if (!char.IsWhiteSpace(analysis.Text[i]) && analysis.Text[i] is not ('.' or '!' or '?' or '\u2026')) return false; }
            return true;
        }
        bool SingleWord(FuzzyMatch match)
        {
            var tokens = new BulgarianTokenizer().Tokenize(match.Form, s => new(s, BulgarianTextNormalizer.NormalizeKey(s), [], null));
            return tokens.Count == 1 && tokens[0].Kind == TokenKind.Word && tokens[0].Start == 0 && tokens[0].Length == match.Form.Length;
        }
    }
}
