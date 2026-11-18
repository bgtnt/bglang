using BgLang.Contracts;
using Guard = BgLang.Core.ContextEvidenceGuards;
using Word = BgLang.Core.ContextLanes.TokenView;

namespace BgLang.Core;

/// <summary>Exact owner-bound form acquisition shared by grammar repair checks.</summary>
internal static class GrammarRepairForms
{
    internal sealed record Alternative(string Surface, IReadOnlyList<ContextLanes.ReadingView> Readings,
        bool? IntroducedFullArticle);

    internal static NominalStructuralProjection.Binding Replace(NominalStructuralProjection.Binding binding,
        int index, Alternative alternative)
    {
        var members = binding.Participants.ToArray();
        var member = members[index];
        members[index] = member with
        {
            Word = member.Word with { Readings = alternative.Readings },
            RoleReadings = Array.AsReadOnly(Enumerable.Range(0, alternative.Readings.Count).ToArray())
        };
        return binding with { Participants = Array.AsReadOnly(members) };
    }

    internal static IEnumerable<Alternative> Find(Word word, IReadOnlyList<int> sourceRows, TextGrammarConstraint constraint,
        IRuntimeLexicon lexicon, Func<string, BulgarianStressLookup.ExactInventory> acquire, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sourceRows.Count == 0 || word.Token.Analysis is not { } display) yield break;
        var original = acquire(display.Word);
        var measured = sourceRows.Select(row => word.Readings[row]).ToArray();
        bool syncreticModifier = constraint == TextGrammarConstraint.NumberGender &&
            measured.All(r => IsAdnominalModifierReading(word, r));
        HashSet<int> owners = [];
        foreach (int index in sourceRows)
        {
            ct.ThrowIfCancellationRequested();
            if (word.Readings[index].Reading is not { } raw ||
                original.RequireOwnerOrdinal(index, raw, ct) is not int current ||
                !syncreticModifier && owners.Count > 0 && !owners.Contains(current))
                yield break;
            owners.Add(current);
        }

        HashSet<string> forms = new(StringComparer.Ordinal);
        string[] lemmas = syncreticModifier
            ? measured.Select(r => r.Lemma).Distinct(StringComparer.Ordinal).ToArray()
            : [measured[0].Lemma];
        foreach (string sourceLemma in lemmas)
        foreach (WordForm form in lexicon.GenerateForms(sourceLemma, int.MaxValue))
        {
            ct.ThrowIfCancellationRequested();
            string key = BulgarianTextNormalizer.NormalizeKey(form.Form);
            MorphologicalFeatures formFeatures = MorphologicalFeatures.FromTag(form.Tag);
            var candidateRow = measured[0] with
            {
                Category = MapCategory(formFeatures.PartOfSpeech),
                Lemma = sourceLemma,
                Features = formFeatures
            };
            var preWord = word with
            {
                Token = word.Token with { Text = form.Form, Length = form.Form.Length },
                CanonicalText = key,
                Readings = [candidateRow]
            };
            if (key == word.CanonicalText || !form.Form.All(char.IsLetter) ||
                !measured.Any(row => SameOutsideConstraint(word, row, preWord, candidateRow, constraint))) continue;
            string surface = TextValidationAnalyzer.MatchArticleCase(word.Token.Text, form.Form);
            if (!forms.Add(surface)) continue;
            var exact = acquire(surface);
            bool exactHasReal = exact.ResolvedReadings.Any(raw =>
                !(raw.PartOfSpeech == "unknown:500" && raw.Lemma.Trim().ToLowerInvariant() == key));
            List<ContextLanes.ReadingView> replacements = [];
            for (int index = 0; index < exact.ResolvedReadings.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                WordReading raw = exact.ResolvedReadings[index];
                if ((constraint == TextGrammarConstraint.PersonNumber || exactHasReal) &&
                    raw.PartOfSpeech == "unknown:500" && raw.Lemma.Trim().ToLowerInvariant() == key) continue;
                if (syncreticModifier && raw.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                    measured.Any(m => m.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
                        BulgarianTextNormalizer.NormalizeKey(m.Lemma) == BulgarianTextNormalizer.NormalizeKey(raw.Lemma)))
                    continue;
                var template = measured.FirstOrDefault(m =>
                    BulgarianTextNormalizer.NormalizeKey(m.Lemma) == BulgarianTextNormalizer.NormalizeKey(raw.Lemma)) ?? measured[0];
                var candidate = template with
                {
                    Category = MapCategory(raw.Features.PartOfSpeech),
                    Reading = raw,
                    Lemma = raw.Lemma,
                    Features = raw.Features
                };
                var candidateWord = word with
                {
                    Token = word.Token with { Text = surface, Length = surface.Length },
                    CanonicalText = key,
                    Readings = [.. measured, candidate]
                };
                if (Guard.IsUnwrittenCapitalizedReading(candidateWord, candidate, sentenceInitial: false) ||
                    Guard.IsSyncreticVocativeCompanion(candidateWord, candidate) ||
                    syncreticModifier &&
                        (Guard.IsSuppressedPresentActiveNounHomograph(candidateWord, candidate) ||
                         Guard.IsSuppressedSubstantivizedAdjectiveNounHomograph(candidateWord, candidate, ContextLanes.Category.Adjective)))
                    continue;
                if (!measured.Any(row => SameOutsideConstraint(word, row, candidateWord, candidate, constraint)) ||
                    exact.RequireOwnerOrdinal(index, raw, ct) is not int repOwner || !owners.Contains(repOwner))
                {
                    replacements.Clear();
                    break;
                }
                replacements.Add(candidate);
            }
            if (replacements.Count == 0) continue;
            var repWord = word with
            {
                Token = word.Token with { Text = surface, Length = surface.Length },
                CanonicalText = key,
                Readings = replacements.AsReadOnly()
            };
            bool? introducedFullArticle = null;
            if (replacements.Any(r => IsMasculineSingularArticled(repWord, r)) &&
                measured.All(row => !IsMasculineSingular(word, row)))
            {
                var token = word.Token with { Text = surface, Length = surface.Length };
                introducedFullArticle = TextValidationAnalyzer.KnownFullArticleForm(token, Snapshots(surface), Snapshots, ct);
                if (introducedFullArticle is null && syncreticModifier)
                {
                    string normSurface = BulgarianTextNormalizer.NormalizeKey(surface);
                    if ((normSurface.EndsWith("ият", StringComparison.Ordinal) || normSurface.EndsWith("ят", StringComparison.Ordinal)) &&
                        acquire(surface[..^1]).ResolvedReadings.Count > 0)
                        introducedFullArticle = true;
                    else if ((normSurface.EndsWith("ия", StringComparison.Ordinal) || normSurface.EndsWith("я", StringComparison.Ordinal)) &&
                             acquire(surface + "т").ResolvedReadings.Count > 0)
                        introducedFullArticle = false;
                }
                if (introducedFullArticle is null) continue;
            }
            yield return new(surface, replacements.AsReadOnly(), introducedFullArticle);
        }

        IReadOnlyList<TextReadingSnapshot> Snapshots(string surface) =>
            Array.AsReadOnly(acquire(surface).ResolvedReadings.Select(row => new TextReadingSnapshot(row, row.Features)).ToArray());
    }

    private static ContextLanes.Category MapCategory(MorphologicalPartOfSpeech? pos) => pos switch
    {
        MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun => ContextLanes.Category.Noun,
        MorphologicalPartOfSpeech.Adjective => ContextLanes.Category.Adjective,
        MorphologicalPartOfSpeech.Verb => ContextLanes.Category.Verb,
        MorphologicalPartOfSpeech.Numeral => ContextLanes.Category.Number,
        MorphologicalPartOfSpeech.Pronoun or MorphologicalPartOfSpeech.PersonalPronoun => ContextLanes.Category.Pronoun,
        _ => ContextLanes.Category.Unknown
    };

    private static bool IsAdnominalModifierReading(Word word, ContextLanes.ReadingView row) =>
        row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && Guard.HasNominalAxes(row.Features) ||
        Guard.IsMeasuredPassiveAttribute(row.Features) ||
        Guard.IsMeasuredPresentActiveAttribute(row.Features) ||
        Guard.TryGetOrdinalAxes(row, out _) ||
        Guard.TryGetDeterminerAxes(word, row, out _);

    private static bool SameOutsideConstraint(Word sourceWord, ContextLanes.ReadingView sourceRow,
        Word replacementWord, ContextLanes.ReadingView replacementRow, TextGrammarConstraint constraint)
    {
        MorphologicalFeatures source = sourceRow.Features;
        MorphologicalFeatures replacement = replacementRow.Features;
        if (constraint == TextGrammarConstraint.PersonNumber)
            return Guard.IsPersonalPredicate(replacement) && replacement.VerbForm == MorphologicalVerbForm.Finite &&
                !replacement.HasUnrecognizedFeatures && source == replacement with
                { RawTag = source.RawTag, Person = source.Person, Number = source.Number };

        if (constraint != TextGrammarConstraint.NumberGender)
            return false;

        if (Guard.TryGetOrdinalAxes(sourceRow, out var srcOrd) && Guard.TryGetOrdinalAxes(replacementRow, out var repOrd))
        {
            return source.ParadigmId == replacement.ParadigmId &&
                ArticleCompatible(srcOrd.Number == MorphologicalNumber.Singular && srcOrd.Gender == MorphologicalGender.Masculine, srcOrd.Article,
                    repOrd.Number == MorphologicalNumber.Singular && repOrd.Gender == MorphologicalGender.Masculine, repOrd.Article);
        }

        if (Guard.TryGetDeterminerAxes(sourceWord, sourceRow, out var srcDet) &&
            Guard.TryGetDeterminerAxes(replacementWord, replacementRow, out var repDet))
        {
            MorphologicalArticle srcArt = Guard.GetDeterminerArticle(source);
            MorphologicalArticle repArt = Guard.GetDeterminerArticle(replacement);
            return source.ParadigmId == replacement.ParadigmId &&
                BulgarianTextNormalizer.NormalizeKey(sourceRow.Lemma) == BulgarianTextNormalizer.NormalizeKey(replacementRow.Lemma) &&
                ArticleCompatible(srcDet.Number == MorphologicalNumber.Singular && srcDet.Gender == MorphologicalGender.Masculine, srcArt,
                    repDet.Number == MorphologicalNumber.Singular && repDet.Gender == MorphologicalGender.Masculine, repArt);
        }

        bool validAxes = Guard.HasNominalAxes(replacement) || Guard.IsMeasuredPresentActiveAttribute(replacement);
        return validAxes &&
            ArticleCompatible(MasculineSingular(source), source.Article, MasculineSingular(replacement), replacement.Article) &&
            source == replacement with
            {
                RawTag = source.RawTag,
                Gender = source.Gender,
                Number = source.Number,
                Article = source.Article,
                HasUnrecognizedFeatures = source.HasUnrecognizedFeatures
            };
    }

    private static bool ArticleCompatible(bool srcMascSg, MorphologicalArticle? srcArt, bool repMascSg, MorphologicalArticle? repArt) =>
        srcArt == repArt ||
        srcMascSg && IsMasculineArticle(srcArt) && !repMascSg && repArt == MorphologicalArticle.Definite ||
        repMascSg && IsMasculineArticle(repArt) && !srcMascSg && srcArt == MorphologicalArticle.Definite;

    private static bool IsMasculineSingular(Word word, ContextLanes.ReadingView row) =>
        MasculineSingular(row.Features) ||
        Guard.TryGetOrdinalAxes(row, out var ord) && ord is { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine } ||
        Guard.TryGetDeterminerAxes(word, row, out var det) && det is { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine };

    private static bool IsMasculineSingularArticled(Word word, ContextLanes.ReadingView row) =>
        MasculineSingular(row.Features) && IsMasculineArticle(row.Features.Article) ||
        Guard.TryGetOrdinalAxes(row, out var ord) && ord is { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine } && IsMasculineArticle(ord.Article) ||
        Guard.TryGetDeterminerAxes(word, row, out var det) && det is { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine } && IsMasculineArticle(Guard.GetDeterminerArticle(row.Features));

    private static bool MasculineSingular(MorphologicalFeatures features) =>
        features is { Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine };

    private static bool IsMasculineArticle(MorphologicalArticle? article) =>
        article is MorphologicalArticle.Full or MorphologicalArticle.Short or MorphologicalArticle.Definite;
}
