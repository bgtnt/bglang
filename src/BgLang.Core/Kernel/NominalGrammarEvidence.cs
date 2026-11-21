using BgLang.Contracts;
using Guard = BgLang.Core.ContextEvidenceGuards;

namespace BgLang.Core;

internal static class NominalGrammarEvidence
{
    internal static TextGrammarMatch Explain(NominalGrammarRules.AttributeMatch match, IRuntimeLexicon lexicon,
        Func<string, BulgarianStressLookup.ExactInventory> acquire, bool selectedSubject,
        PredicativeGrammarEvidence.Frame? frame, CancellationToken ct,
        ClauseFusionAnalyzer.ArticleExpectationKind? articleRole = null)
    {
        var binding = match.Binding;
        if (match.Reason == "numeral_count_attribute_mismatch" && binding.Participants.Count == 2)
        {
            var adjMember = binding.Participants[0];
            var nounMember = binding.Head;
            var countConflicts = new[]
            {
                new TextGrammarConflict(adjMember.Word.OriginalIndex,
                    NominalGrammarRules.References(adjMember),
                    NominalGrammarRules.References(nounMember))
            };
            List<TextGrammarRepair> countRepairs = [];
            foreach (var alt in GrammarRepairForms.Find(adjMember.Word, adjMember.RoleReadings,
                         TextGrammarConstraint.NumberGender, lexicon, acquire, ct))
            {
                if (alt.Readings.All(r => r.Features is
                    {
                        PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
                        Number: MorphologicalNumber.Plural,
                        Article: MorphologicalArticle.Indefinite
                    } && Guard.HasNominalAxes(r.Features)))
                {
                    countRepairs.Add(NominalGrammarRules.Repair(adjMember.Word, alt.Surface));
                }
            }
            var orderedRepairs = Array.AsReadOnly(countRepairs
                .OrderBy(r => r.Edits[0].Start)
                .ThenBy(r => r.Edits[0].Replacement, StringComparer.Ordinal)
                .ToArray());
            return NominalGrammarRules.Match(NominalGrammarRules.AttributeId, binding, TextGrammarAssessment.Mismatch,
                Array.AsReadOnly(countConflicts), orderedRepairs,
                orderedRepairs.Count > 0 ? null : "no_verified_single_token_repair");
        }
        if (match.Reason == "competing_nominal_construction")
            return NominalGrammarRules.Match(NominalGrammarRules.AttributeId, binding, TextGrammarAssessment.Ambiguous,
                match.Phrase is { } branch ? Array.AsReadOnly(Conflicts(binding, branch)) : [], [], match.Reason);
        if (match.Phrase is not { } phrase)
            return NominalGrammarRules.Match(NominalGrammarRules.AttributeId, binding,
                TextGrammarAssessment.Unassessed, [], [], match.Reason);

        bool compatible = phrase.Heads.Any(head => head.Attributes.All(rows => rows.Length > 0));
        if (compatible)
        {
            bool all = FullyAgrees(binding, phrase);
            return NominalGrammarRules.Match(NominalGrammarRules.AttributeId, binding,
                all ? TextGrammarAssessment.Compatible : TextGrammarAssessment.Ambiguous, [], []);
        }

        var conflicts = Conflicts(binding, phrase);
        bool articlePlaced = ArticlePlaced(binding);
        IReadOnlyList<TextGrammarRepair> repairs = articlePlaced && conflicts.Length > 0
            ? FindRepairs(match, conflicts, lexicon, acquire, selectedSubject, frame, ct, articleRole) : [];
        return NominalGrammarRules.Match(NominalGrammarRules.AttributeId, binding, TextGrammarAssessment.Mismatch,
            Array.AsReadOnly(conflicts), repairs,
            repairs.Count > 0 ? null : !articlePlaced ? "article_placement_unresolved" : "no_verified_single_token_repair");
    }

    internal static bool ArticlePlaced(NominalStructuralProjection.Binding binding)
    {
        for (int index = 1; index < binding.Participants.Count; index++)
        {
            var member = binding.Participants[index];
            bool modifierSlot = index < binding.Participants.Count - 1;
            bool ok = member.Word.Readings.Where((row, rowIdx) =>
                member.RoleReadings.Contains(rowIdx) ||
                !(row.Category == ContextLanes.Category.Verb &&
                  row.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative)).All(row =>
                Guard.IsSourcePlaceholder(member.Word, row) ||
                Guard.IsUnwrittenCapitalizedReading(member.Word, row) ||
                Guard.IsSuppressedPresentActiveNounHomograph(member.Word, row) ||
                modifierSlot && Guard.IsSuppressedSubstantivizedAdjectiveNounHomograph(member.Word, row, ContextLanes.Category.Adjective) ||
                Guard.IsSyncreticVocativeCompanion(member.Word, row) ||
                row.Features.Article == MorphologicalArticle.Indefinite ||
                row.Features.Article is null &&
                    (Guard.IsMeasuredPassiveAttribute(row.Features) || Guard.IsMeasuredPresentActiveAttribute(row.Features)) ||
                Guard.TryGetOrdinalAxes(row, out var ord) && ord.Article == MorphologicalArticle.Indefinite);
            if (!ok) return false;
        }
        return true;
    }

    internal static bool FullyAgrees(NominalStructuralProjection.Binding binding, ContextLanes.NominalPhrase phrase) =>
        phrase.Heads.All(head => head.Attributes.Select((rows, index) =>
            rows.Length == binding.Participants[index].RoleReadings.Count).All(value => value));

    private static TextGrammarConflict[] Conflicts(NominalStructuralProjection.Binding binding, ContextLanes.NominalPhrase phrase)
    {
        var expected = NominalGrammarRules.References(binding.Head, phrase.NounMembers);
        return phrase.AttributeMembers.Select((rows, index) => (Rows: rows, Index: index))
            .Where(item => !phrase.AnyAttributeMatch[item.Index]).Select(item =>
            {
                var member = binding.Participants[item.Index];
                var actual = member.Word.UsesGraph ? Array.Empty<TextReadingReference>() : NominalGrammarRules.References(member, item.Rows);
                return new TextGrammarConflict(member.Word.OriginalIndex, actual, expected);
            }).ToArray();
    }

    private static IReadOnlyList<TextGrammarRepair> FindRepairs(NominalGrammarRules.AttributeMatch match,
        TextGrammarConflict[] conflicts, IRuntimeLexicon lexicon, Func<string, BulgarianStressLookup.ExactInventory> acquire,
        bool selectedSubject, PredicativeGrammarEvidence.Frame? frame, CancellationToken ct,
        ClauseFusionAnalyzer.ArticleExpectationKind? articleRole)
    {
        var binding = match.Binding;
        bool linked = frame is { Competing: false, Controller.Phrase: { } phrase } && phrase.Start == binding.Start;
        List<TextGrammarRepair> result = [];
        HashSet<string> preferredSubjectForms = new(StringComparer.Ordinal);
        var editable = conflicts.Select(conflict => conflict.TokenIndex).Append(binding.Head.Word.OriginalIndex).ToHashSet();
        for (int index = 0; index < binding.Participants.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var member = binding.Participants[index];
            var word = member.Word;
            if (word.UsesGraph || !editable.Contains(word.OriginalIndex))
                continue;
            bool initial = index == 0 && binding.Participants[0].WordIndex == 0;
            var activeRows = word.Readings.Where((row, rowIdx) => !Guard.IsSourcePlaceholder(word, row) &&
                !Guard.IsUnwrittenCapitalizedReading(word, row, initial) &&
                !Guard.IsSuppressedPresentActiveNounHomograph(word, row) &&
                !(index < binding.Participants.Count - 1 &&
                  Guard.IsSuppressedSubstantivizedAdjectiveNounHomograph(word, row, ContextLanes.Category.Adjective)) &&
                !Guard.IsSyncreticVocativeCompanion(word, row) &&
                (member.RoleReadings.Contains(rowIdx) ||
                 !(row.Category == ContextLanes.Category.Verb &&
                   row.Features.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative))).ToArray();
            if (activeRows.Length == 0 ||
                activeRows.Any(row => row.Features.PartOfSpeech is not (MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective) &&
                    !(index < binding.Participants.Count - 1 &&
                      (Guard.IsMeasuredPassiveAttribute(row.Features) ||
                       Guard.IsMeasuredPresentActiveAttribute(row.Features) ||
                       Guard.TryGetOrdinalAxes(row, out _) ||
                       index == 0 && Guard.TryGetDeterminerAxes(word, row, out _)))))
                continue;
            foreach (var alternative in GrammarRepairForms.Find(word, member.RoleReadings, TextGrammarConstraint.NumberGender,
                lexicon, acquire, ct))
            {
                bool changesHeadNumber = index == binding.Participants.Count - 1 && alternative.Readings.Any(replacement =>
                    activeRows.Any(original => original.Features.Number != replacement.Features.Number));
                if (selectedSubject && !linked && changesHeadNumber ||
                    match.Governed && alternative.IntroducedFullArticle == true ||
                    articleRole == ClauseFusionAnalyzer.ArticleExpectationKind.DirectObjectShort && alternative.IntroducedFullArticle == true ||
                    (selectedSubject || linked) && alternative.IntroducedFullArticle == false) continue;
                var updated = GrammarRepairForms.Replace(binding, index, alternative);
                var repaired = linked ? null : NominalStructuralProjection.Measure(updated, ct);
                if (linked ? !PredicativeGrammarEvidence.RepairsController(frame!, updated, ct) :
                    repaired is null ||
                    !repaired.Heads.Any(head => head.Attributes.All(rows => rows.Length > 0))) continue;
                List<string> qualifications = [];
                if (alternative.IntroducedFullArticle is not null && !match.Governed && !selectedSubject && !linked)
                    qualifications.Add("masculine_article_form_requires_role");
                if (changesHeadNumber && match.HasFinite && !match.Governed && !linked)
                    qualifications.Add("clause_relation_unverified");
                if (index == 0 && alternative.IntroducedFullArticle == true && !match.Governed &&
                    !selectedSubject && !linked && articleRole == ClauseFusionAnalyzer.ArticleExpectationKind.SubjectFull &&
                    repaired is not null && FullyAgrees(updated, repaired))
                    preferredSubjectForms.Add(alternative.Surface);
                result.Add(NominalGrammarRules.Repair(word, alternative.Surface) with { Qualifications = qualifications.AsReadOnly() });
            }
        }
        return Array.AsReadOnly(result.OrderBy(repair => repair.Edits[0].Start)
            .ThenBy(repair => preferredSubjectForms.Contains(repair.Edits[0].Replacement) ? 0 : 1)
            .ThenBy(repair => repair.Edits[0].Replacement, StringComparer.Ordinal).ToArray());
    }
}
