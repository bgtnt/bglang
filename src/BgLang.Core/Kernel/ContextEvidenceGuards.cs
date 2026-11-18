// Typed guards shared by the bounded contextual lanes.
// Callers must provide complete exact inventories and retain every original row.
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using BgLang.Contracts;

namespace BgLang.Core;

internal static class ContextEvidenceGuards
{
    internal enum Agreement { Unmeasured, Compatible, Mismatch }

    // Compare already measured tuples only. Callers retain category, feature,
    // topology and completeness policy; equality alone does not establish support.
    internal static bool NominalTupleMatches(
        (MorphologicalNumber? Number, MorphologicalGender? Gender) left,
        (MorphologicalNumber? Number, MorphologicalGender? Gender) right) =>
        left.Number == right.Number &&
        (left.Number == MorphologicalNumber.Plural || left.Gender == right.Gender);

    internal static bool PersonNumberTupleMatches(
        (MorphologicalPerson? Person, MorphologicalNumber? Number) left,
        (MorphologicalPerson? Person, MorphologicalNumber? Number) right) =>
        left.Person == right.Person && left.Number == right.Number;

    internal static bool IsPersonalPredicate(MorphologicalFeatures f) =>
        !f.HasConflictingFeatures && f.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
        f.VerbForm is MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative &&
        f.Person is MorphologicalPerson.First or MorphologicalPerson.Second or MorphologicalPerson.Third &&
        IsNumber(f.Number);

    internal static bool IsExactAdposition(ContextLanes.TokenView word) =>
        word.Complete && !word.UsesGraph && word.Readings.Count > 0 &&
        word.Readings.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition) &&
        word.Readings.All(row => !row.Features.HasConflictingFeatures && !row.Features.HasUnrecognizedFeatures &&
            (row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition ||
             row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Particle &&
             BulgarianTextNormalizer.NormalizeKey(row.Lemma) == word.CanonicalText));

    internal static int[] SubordinateConnectorReadings(ContextLanes.TokenView word, CancellationToken ct)
    {
        string key = word.CanonicalText;
        if (key is not ("че" or "защото") || word.UsesGraph || !word.Complete || word.Readings.Count == 0) return [];
        List<int> supports = [];
        for (int index = 0; index < word.Readings.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var row = word.Readings[index];
            if (row.Category == ContextLanes.Category.Unknown || row.Features is
                { HasConflictingFeatures: true } or { HasUnrecognizedFeatures: true } ||
                row.Lemma.Trim().ToLowerInvariant() != key) return [];
            if (row.SourceConjunction && row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Conjunction, RawTag: "Съюз:189" } ||
                key == "защото" && row.SourceAdverb && row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adverb, RawTag: "Наречие:188" })
                supports.Add(index);
            else if (key != "че" || row.Category != ContextLanes.Category.Particle ||
                row.Features is not { PartOfSpeech: MorphologicalPartOfSpeech.Particle, RawTag: "Частица:191" }) return [];
        }
        return supports.ToArray();
    }

    internal static int[] AccusativeCliticReadings(ContextLanes.TokenView word, CancellationToken ct) =>
        CompletePersonalFormReadings(word, IsAccusativeCliticReading, ct);

    internal static int[] ObliquePronounReadings(ContextLanes.TokenView word, CancellationToken ct) =>
        CompletePersonalFormReadings(word, row => row.Features.Case is not null &&
            row.Features.IsShortForm != true && IsMeasuredPersonalForm(row), ct);

    private static int[] CompletePersonalFormReadings(ContextLanes.TokenView word,
        System.Func<ContextLanes.ReadingView, bool> licensed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!word.Complete || word.UsesGraph || word.Readings.Count == 0 ||
            word.Readings.Any(row => row.Features.HasConflictingFeatures)) return [];
        List<int> supports = [];
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ContextLanes.ReadingView row = word.Readings[i];
            if (row.SourceGenericUnknown && row.Lemma.Trim().ToLowerInvariant() == word.CanonicalText) continue;
            if (!licensed(row)) return [];
            supports.Add(i);
        }
        return supports.ToArray();
    }

    internal static bool IsAccusativeCliticReading(ContextLanes.ReadingView row) =>
        row.Features is { Case: MorphologicalCase.Accusative, IsShortForm: true } && IsMeasuredPersonalForm(row);

    private static bool IsMeasuredPersonalForm(ContextLanes.ReadingView row)
    {
        var features = row.Features;
        if (!row.SourcePersonalPronoun || features is not
            { PartOfSpeech: MorphologicalPartOfSpeech.PersonalPronoun, HasConflictingFeatures: false }) return false;
        if (!features.HasUnrecognizedFeatures) return true;

        // Some source personal forms carry one legacy numeric atom outside the typed axes.
        if (features.RawTag is not { } tag) return false;
        string[] atoms = tag.Split(',', StringSplitOptions.TrimEntries);
        if (atoms.Count(atom => atom == "4") != 1) return false;
        var parsed = MorphologicalFeatures.FromTag(string.Join(',', atoms.Where(atom => atom != "4")));
        return !parsed.HasUnrecognizedFeatures && !parsed.HasConflictingFeatures &&
            parsed == features with { RawTag = parsed.RawTag, HasUnrecognizedFeatures = false };
    }

    internal static bool IsPresentThirdSingular(MorphologicalFeatures f) =>
        IsPersonalPredicate(f) && f.VerbForm == MorphologicalVerbForm.Finite &&
        f.Person == MorphologicalPerson.Third && f.Number == MorphologicalNumber.Singular &&
        f.Tense == MorphologicalTense.Present;




    // Caller also proves exact ADJ+VERB target categories, all-PREP left context,
    // same sentence and literal whitespace bridges using original token indices.
    internal static int[] CompatibleAttributeMembers(IReadOnlyList<MorphologicalFeatures> attribute,
        IReadOnlyList<MorphologicalFeatures> noun, CancellationToken cancellationToken)
    {
        if (noun.Count == 0) return [];
        var nounTuple = (noun[0].Number, noun[0].Gender);
        foreach (MorphologicalFeatures reading in noun)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reading.PartOfSpeech != MorphologicalPartOfSpeech.Noun || !ExplicitNominalTuple(reading) ||
                (reading.Number, reading.Gender) != nounTuple)
                return [];
        }
        List<int> selected = [];
        (MorphologicalNumber? Number, MorphologicalGender? Gender)? selectedTuple = null;
        for (int i = 0; i < attribute.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MorphologicalFeatures reading = attribute[i];
            if (reading.PartOfSpeech != MorphologicalPartOfSpeech.Adjective || !ExplicitNominalTuple(reading) ||
                !NominalTupleMatches((reading.Number, reading.Gender), nounTuple))
                continue;
            var tuple = (reading.Number, reading.Gender);
            if (selectedTuple is { } previous && previous != tuple) return [];
            selectedTuple = tuple;
            selected.Add(i);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selected.ToArray();
    }



    internal static bool HasNominalAxes(MorphologicalFeatures f) =>
        !f.HasUnrecognizedFeatures && ExplicitNominalTuple(f);

    internal static bool IsSourcePlaceholder(ContextLanes.TokenView word, ContextLanes.ReadingView row) =>
        row.SourceGenericUnknown && !row.Features.HasConflictingFeatures &&
        row.Features.RawTag is { } tag && tag.StartsWith("unknown:", StringComparison.OrdinalIgnoreCase) &&
        row.Lemma.Trim().ToLowerInvariant() == word.CanonicalText;

    internal static bool IsUnwrittenCapitalizedReading(ContextLanes.TokenView word, ContextLanes.ReadingView row, bool sentenceInitial = false)
    {
        if (row.Reading is not { Form.Length: > 0 } raw || !char.IsUpper(raw.Form[0]) ||
            BulgarianTextNormalizer.NormalizeKey(raw.Form) != word.CanonicalText ||
            !(row.Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun ||
              row.Features.RawTag is { } tag && tag.StartsWith("Наименование:", StringComparison.Ordinal)))
            return false;
        string surface = word.Token.Text;
        if (surface.Length > 0 && char.IsLower(surface[0]))
            return word.Readings.Any(r => !IsSourcePlaceholder(word, r) &&
                !(r.Reading is { Form.Length: > 0 } other && char.IsUpper(other.Form[0]) &&
                  (r.Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun ||
                   r.Features.RawTag?.StartsWith("Наименование:", StringComparison.Ordinal) == true)));
        return sentenceInitial && surface.Length > 1 && char.IsUpper(surface[0]) && char.IsLower(surface[1]) &&
            row.Features.NameKind is not (MorphologicalNameKind.GivenName or MorphologicalNameKind.FamilyName) &&
            !word.Readings.Any(r => r.Features.NameKind is MorphologicalNameKind.GivenName or MorphologicalNameKind.FamilyName) &&
            word.Readings.Any(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective &&
                HasNominalAxes(r.Features) ||
                r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral && !r.Features.HasConflictingFeatures);
    }

    internal static bool TryGetDeterminerAxes(ContextLanes.TokenView word, ContextLanes.ReadingView row,
        out (MorphologicalNumber Number, MorphologicalGender? Gender) axes)
    {
        axes = default;
        if (word.CanonicalText is "всичко" or "нищо" or "нещо" or "що") return false;
        string lemma = BulgarianTextNormalizer.NormalizeKey(row.Lemma);
        MorphologicalFeatures f = row.Features;
        if (lemma == "един" &&
            f is { PartOfSpeech: MorphologicalPartOfSpeech.Numeral, HasConflictingFeatures: false, HasUnrecognizedFeatures: false })
        {
            if (f.Number == MorphologicalNumber.Plural) { axes = (MorphologicalNumber.Plural, null); return true; }
            if (f.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter)
            { axes = (MorphologicalNumber.Singular, f.Gender); return true; }
            return false;
        }
        if (lemma is "този" or "онзи" or "такъв" or "онакъв" or "всеки" or "всичкия" or "всички" or "какъв" or "чий" or "кой" or
                "някой" or "някакъв" or "никой" or "никакъв" or "мой" or "твой" or "негов" or "неин" or
                "наш" or "ваш" or "техен" or "свой" &&
            row.Category == ContextLanes.Category.Pronoun &&
            f is { PartOfSpeech: MorphologicalPartOfSpeech.Pronoun, HasConflictingFeatures: false, IsShortForm: not true } &&
            (f.Case is null || word.CanonicalText == "всеки" && f.Case == MorphologicalCase.PrepositionalDative) &&
            (!f.HasUnrecognizedFeatures || word.CanonicalText == "всеки" || HasOnlyPronounArticleAtom(f)))
        {
            if (f.Number == MorphologicalNumber.Plural) { axes = (MorphologicalNumber.Plural, null); return true; }
            if (f.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter)
            { axes = (MorphologicalNumber.Singular, f.Gender); return true; }
        }
        return false;
    }

    private static bool HasOnlyPronounArticleAtom(MorphologicalFeatures f)
    {
        if (!f.HasUnrecognizedFeatures || f.RawTag is not { } tag) return false;
        string[] atoms = tag.Split(',', StringSplitOptions.TrimEntries);
        if (!atoms.Any(IsPronounArticleAtom)) return false;
        var parsed = MorphologicalFeatures.FromTag(string.Join(',', atoms.Where(atom => !IsPronounArticleAtom(atom))));
        return !parsed.HasUnrecognizedFeatures && !parsed.HasConflictingFeatures &&
            parsed == f with { RawTag = parsed.RawTag, HasUnrecognizedFeatures = false };

        static bool IsPronounArticleAtom(string atom) => atom is "Пълен" or "Непълен" or "Членуван";
    }

    internal static bool TryGetGenderedNumeralReadings(ContextLanes.TokenView word, bool sentenceInitial,
        CancellationToken ct, out int[] numeralReadings, out bool masculineOnly)
    {
        numeralReadings = [];
        masculineOnly = false;
        if (!word.Complete || word.UsesGraph || word.Readings.Count == 0) return false;
        (string? expectedLemma, bool masc) = word.CanonicalText switch
        {
            "два" or "двата" => ("два", true),
            "две" or "двете" => ("два", false),
            "двама" or "двамата" => ("два", true),
            "трима" or "тримата" => ("три", true),
            "четирима" or "четиримата" => ("четири", true),
            "петима" or "петимата" => ("пет", true),
            "шестима" or "шестимата" => ("шест", true),
            "седмина" or "седмината" => ("седем", true),
            _ => (null, false)
        };
        if (expectedLemma is null) return false;
        bool hasNonPlaceholder = word.Readings.Any(r => !IsSourcePlaceholder(word, r));
        List<int> rows = [];
        for (int i = 0; i < word.Readings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ContextLanes.ReadingView row = word.Readings[i];
            if (hasNonPlaceholder && IsSourcePlaceholder(word, row) ||
                IsUnwrittenCapitalizedReading(word, row, sentenceInitial))
                continue;
            MorphologicalFeatures f = row.Features;
            if (f.HasConflictingFeatures || f.PartOfSpeech != MorphologicalPartOfSpeech.Numeral ||
                BulgarianTextNormalizer.NormalizeKey(row.Lemma) != expectedLemma ||
                (masc ? f.Gender != MorphologicalGender.Masculine
                      : f.Gender is not (MorphologicalGender.Feminine or MorphologicalGender.Neuter)))
                return false;
            if (f.HasUnrecognizedFeatures)
            {
                if (f.RawTag is not { } tag) return false;
                string[] atoms = tag.Split(',', StringSplitOptions.TrimEntries);
                var parsed = MorphologicalFeatures.FromTag(string.Join(',',
                    atoms.Where(a => a is not ("Мъжколична" or "Членуван"))));
                if (parsed.HasUnrecognizedFeatures || parsed.HasConflictingFeatures) return false;
            }
            rows.Add(i);
        }
        if (rows.Count == 0) return false;
        numeralReadings = rows.ToArray();
        masculineOnly = masc;
        return true;
    }

    internal static bool DeterminerAllowsArticledHead(ContextLanes.TokenView word) =>
        word.CanonicalText is "всичкият" or "всичкия" or "всичката" or "всичкото" or "всичките";

    internal static bool IsSuppressedDeterminerHomograph(ContextLanes.TokenView word, ContextLanes.ReadingView row) =>
        word.CanonicalText == "тези" && row.Category == ContextLanes.Category.Noun &&
        BulgarianTextNormalizer.NormalizeKey(row.Lemma) == "теза" &&
        word.Readings.Any(r => TryGetDeterminerAxes(word, r, out _));

    internal static bool IsSuppressedPresentActiveNounHomograph(ContextLanes.TokenView word, ContextLanes.ReadingView row) =>
        word.CanonicalText is "настоящата" or "настоящите" && row.Category == ContextLanes.Category.Noun &&
        BulgarianTextNormalizer.NormalizeKey(row.Lemma) == "настояще" &&
        row.Features.Number == MorphologicalNumber.Plural &&
        word.Readings.Any(r => r.Category == ContextLanes.Category.Adjective || IsMeasuredPresentActiveAttribute(r.Features));

    internal static bool IsSuppressedSubstantivizedAdjectiveNounHomograph(
        ContextLanes.TokenView word, ContextLanes.ReadingView row, ContextLanes.Category expectedCategory)
    {
        if (expectedCategory != ContextLanes.Category.Adjective ||
            row.Category != ContextLanes.Category.Noun ||
            row.Features.PartOfSpeech != MorphologicalPartOfSpeech.Noun ||
            row.Features.Article is not (MorphologicalArticle.Definite or MorphologicalArticle.Full or MorphologicalArticle.Short))
            return false;
        string nounLemma = BulgarianTextNormalizer.NormalizeKey(row.Lemma);
        if (nounLemma.Length < 4) return false;
        foreach (ContextLanes.ReadingView other in word.Readings)
        {
            if (ReferenceEquals(other, row)) continue;
            if (!(other.Category == ContextLanes.Category.Adjective &&
                  other.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && HasNominalAxes(other.Features) ||
                  IsMeasuredPassiveAttribute(other.Features) ||
                  IsMeasuredPresentActiveAttribute(other.Features)))
                continue;
            string adjLemma = BulgarianTextNormalizer.NormalizeKey(other.Lemma);
            if (adjLemma.Length < 4) continue;
            int minLen = Math.Min(nounLemma.Length, adjLemma.Length);
            int requiredPrefix = Math.Max(4, minLen - 2);
            if (nounLemma.AsSpan(0, requiredPrefix).SequenceEqual(adjLemma.AsSpan(0, requiredPrefix)))
                return true;
        }
        return false;
    }

    internal static bool IsAdverbialSpecifierCandidate(
        ContextLanes.TokenView word, bool sentenceInitial, bool precededByDefiniteAdjective)
    {
        if (!word.Complete || word.UsesGraph || word.Readings.Count == 0) return false;
        if (word.CanonicalText is "едно" or "друго" or "това" or "онова" or
            "мое" or "твое" or "свое" or "наше" or "ваше" or "тяхно" or
            "всяко" or "никое" or "което" or "какво" or "нещо" or "нищо" or "всичко" or
            "колко" or "няколко" or "толкова")
            return false;

        bool hasNonPlaceholder = word.Readings.Any(r => !IsSourcePlaceholder(word, r));
        ContextLanes.ReadingView[] active = word.Readings.Where(r =>
            !(hasNonPlaceholder && IsSourcePlaceholder(word, r)) &&
            !IsUnwrittenCapitalizedReading(word, r, sentenceInitial)).ToArray();
        if (active.Length == 0) return false;

        if (active.Any(r => (r.SourceAdverb || r.Category == ContextLanes.Category.Other) &&
                r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adverb, HasConflictingFeatures: false, HasUnrecognizedFeatures: false }) &&
            active.All(r => (r.SourceAdverb || r.Category == ContextLanes.Category.Other) && r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adverb ||
                r.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Number: MorphologicalNumber.Singular,
                    Gender: MorphologicalGender.Neuter, Article: MorphologicalArticle.Indefinite } && HasNominalAxes(r.Features)))
            return true;

        if (precededByDefiniteAdjective ||
            active.Any(r => TryGetDeterminerAxes(word, r, out _) || TryGetOrdinalAxes(r, out _) ||
                IsMeasuredPassiveAttribute(r.Features) || IsMeasuredPresentActiveAttribute(r.Features)))
            return false;

        string surface = word.Token.Text;
        if (surface.Length == 0) return false;

        if ((char.IsLower(surface[0]) || sentenceInitial && surface.Length > 1 && char.IsUpper(surface[0]) && char.IsLower(surface[1])) &&
            word.CanonicalText.EndsWith('о') &&
            active.All(r => r.Features is
                { PartOfSpeech: MorphologicalPartOfSpeech.Adjective, Number: MorphologicalNumber.Singular,
                  Gender: MorphologicalGender.Neuter, Article: MorphologicalArticle.Indefinite } && HasNominalAxes(r.Features)))
            return true;

        return char.IsLower(surface[0]) &&
            word.CanonicalText.EndsWith("ически", StringComparison.Ordinal) &&
            active.All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
                HasNominalAxes(r.Features) && r.Features.Article is null or MorphologicalArticle.Indefinite);
    }

    internal static bool IsCompoundClassifierLoanNoun(string canonical) =>
        canonical is "секс" or "бизнес" or "фитнес" or "джаз" or "рок" or "поп" or
            "шоу" or "арт" or "офис" or "интернет" or "видео" or "аудио" or "радио" or
            "фото" or "авто" or "тест" or "спорт" or "експерт" or "старт" or "блог" or
            "уеб" or "чат" or "кеш";

    internal static bool IsSyncreticVocativeCompanion(ContextLanes.TokenView word, ContextLanes.ReadingView row)
    {
        if (row.Features.PartOfSpeech != MorphologicalPartOfSpeech.Noun ||
            !row.Features.HasUnrecognizedFeatures ||
            row.Features.HasConflictingFeatures ||
            row.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) != true &&
            row.Reading?.PartOfSpeech?.Contains("Звател", StringComparison.Ordinal) != true)
            return false;
        string lemma = BulgarianTextNormalizer.NormalizeKey(row.Lemma);
        return lemma.Length > 0 && word.Readings.Any(other =>
            !ReferenceEquals(other, row) &&
            other.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
            HasNominalAxes(other.Features) &&
            other.Features.RawTag?.Contains("Звател", StringComparison.Ordinal) != true &&
            BulgarianTextNormalizer.NormalizeKey(other.Lemma) == lemma);
    }

    internal static bool IsEpiceneHumanNounLemma(string lemma) =>
        BulgarianTextNormalizer.NormalizeKey(lemma) is "колега" or "съдия" or "слуга" or "роднина" or "пияница";

    internal static bool IsPossessiveBridgeClitic(string canonical) =>
        canonical is "му" or "ѝ" or "си";

    // A partial typed passive shape remains structurally inspectable; only measured axes can support agreement.
    internal static bool IsPassiveAttributeShape(MorphologicalFeatures f) =>
        f.PartOfSpeech == MorphologicalPartOfSpeech.Verb && f.ParticipleKind == MorphologicalParticipleKind.PastPassive &&
        f.VerbForm is null or MorphologicalVerbForm.Participle;

    internal static bool IsMeasuredPassiveAttribute(MorphologicalFeatures f) =>
        IsPassiveAttributeShape(f) && f.VerbForm == MorphologicalVerbForm.Participle && HasNominalAxes(f);

    internal static bool IsPresentActiveAttributeShape(MorphologicalFeatures f) =>
        f.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
        f.VerbForm is null or MorphologicalVerbForm.Participle &&
        f.Tense == MorphologicalTense.Present &&
        f.ParticipleKind is null &&
        f.RawTag?.Contains("Сегашно_деятелно", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsMeasuredPresentActiveAttribute(MorphologicalFeatures f) =>
        IsPresentActiveAttributeShape(f) && f.VerbForm == MorphologicalVerbForm.Participle &&
        ExplicitNominalTuple(f) && (!f.HasUnrecognizedFeatures || HasOnlyPronounArticleAtom(f));

    internal static bool TryGetOrdinalAxes(ContextLanes.ReadingView row,
        out (MorphologicalNumber Number, MorphologicalGender? Gender, MorphologicalArticle Article) axes)
    {
        axes = default;
        MorphologicalFeatures f = row.Features;
        if (f.PartOfSpeech != MorphologicalPartOfSpeech.Numeral || f.HasConflictingFeatures ||
            f.RawTag is not { } tag || !tag.StartsWith("Редно_Числително:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (f.HasUnrecognizedFeatures && !HasOnlyPronounArticleAtom(f))
            return false;
        MorphologicalArticle article = f.Article ?? MorphologicalArticle.Indefinite;
        if (f.HasUnrecognizedFeatures)
        {
            string[] atoms = tag.Split(',', StringSplitOptions.TrimEntries);
            if (atoms.Contains("Пълен", StringComparer.Ordinal)) article = MorphologicalArticle.Full;
            else if (atoms.Contains("Непълен", StringComparer.Ordinal)) article = MorphologicalArticle.Short;
            else if (atoms.Contains("Членуван", StringComparer.Ordinal)) article = MorphologicalArticle.Definite;
        }
        if (f.Number == MorphologicalNumber.Plural)
        {
            axes = (MorphologicalNumber.Plural, null, article);
            return true;
        }
        if (f.Number is null or MorphologicalNumber.Singular &&
            f.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter)
        {
            axes = (MorphologicalNumber.Singular, f.Gender, article);
            return true;
        }
        return false;
    }

    internal static MorphologicalArticle GetDeterminerArticle(MorphologicalFeatures f)
    {
        if (f.Article is { } explicitArticle) return explicitArticle;
        if (f.RawTag is { } tag)
        {
            string[] atoms = tag.Split(',', StringSplitOptions.TrimEntries);
            if (atoms.Contains("Пълен", StringComparer.Ordinal)) return MorphologicalArticle.Full;
            if (atoms.Contains("Непълен", StringComparer.Ordinal)) return MorphologicalArticle.Short;
            if (atoms.Contains("Членуван", StringComparer.Ordinal)) return MorphologicalArticle.Definite;
        }
        return MorphologicalArticle.Indefinite;
    }

    internal static bool IsDefinitePluralQuantifierShape(
        string normalizedSurface, MorphologicalFeatures modifier, MorphologicalFeatures noun) =>
        normalizedSurface is "малкото" or "немалкото" &&
        modifier is
        {
            PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
            Number: MorphologicalNumber.Singular,
            Gender: MorphologicalGender.Neuter,
            Article: MorphologicalArticle.Definite,
            HasConflictingFeatures: false,
            HasUnrecognizedFeatures: false
        } &&
        noun is
        {
            PartOfSpeech: MorphologicalPartOfSpeech.Noun,
            Number: MorphologicalNumber.Plural,
            Article: MorphologicalArticle.Indefinite
        } &&
        HasNominalAxes(noun);

    internal static bool IsDefinitePluralQuantifierAdjective(
        ContextLanes.TokenView modifierWord, ContextLanes.ReadingView modifierRow) =>
        modifierWord.CanonicalText is "малкото" or "немалкото" &&
        BulgarianTextNormalizer.NormalizeKey(modifierRow.Lemma) is "малък" or "немалък" &&
        modifierRow.Features is
        {
            PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
            Number: MorphologicalNumber.Singular,
            Gender: MorphologicalGender.Neuter,
            Article: MorphologicalArticle.Definite,
            HasConflictingFeatures: false,
            HasUnrecognizedFeatures: false
        };

    internal static bool AttributeAgreement(MorphologicalFeatures attribute, MorphologicalFeatures noun) =>
        AttributeAgreement(attribute, noun, includePassiveParticiples: false);

    internal static bool AttributeAgreement(MorphologicalFeatures attribute, MorphologicalFeatures noun, bool includePassiveParticiples) =>
        (attribute.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && HasNominalAxes(attribute) ||
            includePassiveParticiples && (IsMeasuredPassiveAttribute(attribute) || IsMeasuredPresentActiveAttribute(attribute))) &&
        noun.PartOfSpeech == MorphologicalPartOfSpeech.Noun && HasNominalAxes(noun) &&
        NominalTupleMatches((attribute.Number, attribute.Gender), (noun.Number, noun.Gender));

    internal static bool ModifierAgreement(ContextLanes.TokenView modifierWord, ContextLanes.ReadingView modifierRow,
        MorphologicalFeatures noun, bool includeAdnominalReadings, bool phraseInitial = false) =>
        AttributeAgreement(modifierRow.Features, noun, includeAdnominalReadings) ||
        phraseInitial && IsDefinitePluralQuantifierAdjective(modifierWord, modifierRow) &&
            IsDefinitePluralQuantifierShape(modifierWord.CanonicalText, modifierRow.Features, noun) ||
        includeAdnominalReadings && noun.PartOfSpeech == MorphologicalPartOfSpeech.Noun && HasNominalAxes(noun) &&
        (TryGetDeterminerAxes(modifierWord, modifierRow, out var axes) &&
            NominalTupleMatches(axes, (noun.Number, noun.Gender)) ||
         TryGetOrdinalAxes(modifierRow, out var ordAxes) &&
            NominalTupleMatches((ordAxes.Number, ordAxes.Gender), (noun.Number, noun.Gender)));

    internal static bool ModifierAgreement(ContextLanes.TokenView modifierWord, ContextLanes.ReadingView modifierRow,
        ContextLanes.ReadingView nounRow, bool includeAdnominalReadings, bool phraseInitial = false) =>
        ModifierAgreement(modifierWord, modifierRow, nounRow.Features, includeAdnominalReadings, phraseInitial) ||
        includeAdnominalReadings &&
        nounRow.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine } &&
        HasNominalAxes(nounRow.Features) &&
        IsEpiceneHumanNounLemma(nounRow.Lemma) &&
        ModifierAgreement(modifierWord, modifierRow, nounRow.Features with { Gender = MorphologicalGender.Feminine }, includeAdnominalReadings, phraseInitial);

    private static bool ExplicitNominalTuple(MorphologicalFeatures f) =>
        !f.HasConflictingFeatures && IsNumber(f.Number) &&
        (f.Number == MorphologicalNumber.Plural || f.Gender is
            MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter);
    // These eight exact canonical subject cells license omitted Case/Short markers.
    // Other null-feature pronouns are not inferred to be nominative or long forms.
    internal static bool LicensedSubject(string canonical, ContextLanes.ReadingView row)
    {
        if (!BulgarianFunctionWords.TryGetPersonalSubjectPronoun(canonical, out PersonalSubjectPronounSpec license))
            return false;
        MorphologicalFeatures f = row.Features;
        return row.Category == ContextLanes.Category.Pronoun && row.SourcePersonalPronoun &&
            f.PartOfSpeech == MorphologicalPartOfSpeech.PersonalPronoun && !f.HasConflictingFeatures && !f.HasUnrecognizedFeatures &&
            f.Person == license.Person && f.Number == license.Number && f.IsShortForm != true &&
            f.Case is null &&
            StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(row.Lemma), canonical) &&
            StringComparer.Ordinal.Equals(f.RawTag, "Лично_местоимение:" + license.LegacyTagId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsNumber(MorphologicalNumber? number) =>
        number is MorphologicalNumber.Singular or MorphologicalNumber.Plural;

    // Peels peripheral interjections ("Ех, ...") and fixed discourse parentheticals (", разбира се", "може би")
    // without removing vocative addresses ("Госпожо, Вие...") or clause-linking conjunctions ("..., обаче тя...").
    internal static bool[] MarkPeripheralWords(
        string text, SentenceSpan sentence, IReadOnlyList<ContextLanes.TokenView> words, CancellationToken ct)
    {
        bool[] peripheral = new bool[words.Count];
        if (words.Count <= 1 || sentence.Start < 0 || sentence.Length < 0 ||
            (long)sentence.Start + sentence.Length > text.Length ||
            !CleanLeadingTrivia(text, sentence.Start, words[0].Token.Start))
            return peripheral;

        int marked = 0;
        bool changed = true;
        while (changed && marked < words.Count - 1)
        {
            ct.ThrowIfCancellationRequested();
            changed = false;
            for (int i = 0; i < words.Count; i++)
            {
                if (peripheral[i]) continue;
                int len = MatchPeripheralSpan(text, words, peripheral, i);
                if (len <= 0 || marked + len >= words.Count) continue;
                for (int k = 0; k < len; k++) peripheral[i + k] = true;
                marked += len;
                changed = true;
                break;
            }
        }
        return peripheral;

        static int MatchPeripheralSpan(
            string text, IReadOnlyList<ContextLanes.TokenView> words, bool[] peripheral, int i)
        {
            string c0 = words[i].CanonicalText;
            if (c0 == "може" && i + 1 < words.Count && !peripheral[i + 1] &&
                WordsHorizontal(text, words[i], words[i + 1]) && words[i + 1].CanonicalText == "би")
                return 2;

            bool leftEdge = AtLeftEdge(peripheral, i);
            bool commaBefore = HasPunctuationBefore(text, words, peripheral, i);

            if (IsPeripheralInterjection(words[i]))
            {
                bool commaAfter = HasPunctuationAfter(text, words, peripheral, i);
                bool rightEdge = AtRightEdge(peripheral, i);
                bool standaloneInterj = c0 is "ех" or "ах" or "ох" or "ух" or "абе" or "бре" or "уви";
                if (leftEdge && (commaAfter || standaloneInterj) || commaBefore && (commaAfter || rightEdge))
                    return 1;
            }

            int phraseLen = MatchDiscoursePhrase(text, words, peripheral, i);
            if (phraseLen > 0)
            {
                int end = i + phraseLen - 1;
                bool rightEdge = AtRightEdge(peripheral, end);
                bool commaAfter = HasPunctuationAfter(text, words, peripheral, end);
                if ((leftEdge || commaBefore) && (rightEdge || commaAfter) && (commaBefore || commaAfter))
                    return phraseLen;
            }

            return 0;
        }

        static bool IsPeripheralInterjection(ContextLanes.TokenView word)
        {
            if (word.CanonicalText is "ех" or "ах" or "ох" or "ух" or "абе" or "бре" or "уви" or "хайде")
                return true;
            if (word.UsesGraph || word.Readings.Count == 0) return false;
            bool sawInterj = false;
            foreach (var r in word.Readings)
            {
                if (IsSourcePlaceholder(word, r)) continue;
                if (r.Features.PartOfSpeech != MorphologicalPartOfSpeech.Interjection) return false;
                sawInterj = true;
            }
            return sawInterj;
        }

        static int MatchDiscoursePhrase(
            string text, IReadOnlyList<ContextLanes.TokenView> words, bool[] peripheral, int i)
        {
            bool Seq(params string[] tokens)
            {
                if (i + tokens.Length > words.Count) return false;
                for (int k = 0; k < tokens.Length; k++)
                {
                    if (peripheral[i + k] || words[i + k].CanonicalText != tokens[k]) return false;
                    if (k > 0 && !WordsHorizontal(text, words[i + k - 1], words[i + k])) return false;
                }
                return true;
            }

            if (Seq("така", "да", "се", "каже")) return 4;
            if (Seq("струва", "ми", "се")) return 3;
            if (Seq("разбира", "се") || Seq("честно", "казано") || Seq("общо", "взето") ||
                Seq("между", "другото") || Seq("за", "съжаление") || Seq("за", "щастие") ||
                Seq("по", "принцип") || Seq("в", "действителност") || Seq("без", "съмнение"))
                return 2;
            return words[i].CanonicalText is "всъщност" or "значи" or "наистина" or "например" or
                "следователно" or "впрочем" or "обаче" or "изглежда" or "вероятно" or "очевидно" or
                "естествено" or "накратко" or "нали" ? 1 : 0;
        }

        static bool AtLeftEdge(bool[] peripheral, int index)
        {
            for (int k = 0; k < index; k++) if (!peripheral[k]) return false;
            return true;
        }

        static bool AtRightEdge(bool[] peripheral, int index)
        {
            for (int k = index + 1; k < peripheral.Length; k++) if (!peripheral[k]) return false;
            return true;
        }

        static bool HasPunctuationBefore(
            string text, IReadOnlyList<ContextLanes.TokenView> words, bool[] peripheral, int index)
        {
            int prev = index - 1;
            while (prev >= 0 && peripheral[prev]) prev--;
            if (prev < 0 && index > 0) prev = index - 1;
            if (prev < 0) return false;
            int from = words[prev].Token.Start + words[prev].Token.Length;
            return text.AsSpan(from, words[index].Token.Start - from).IndexOfAny(",!\u2013\u2014-") >= 0;
        }

        static bool HasPunctuationAfter(
            string text, IReadOnlyList<ContextLanes.TokenView> words, bool[] peripheral, int index)
        {
            int next = index + 1;
            while (next < words.Count && peripheral[next]) next++;
            if (next >= words.Count && index + 1 < words.Count) next = index + 1;
            if (next >= words.Count) return false;
            int from = words[index].Token.Start + words[index].Token.Length;
            return text.AsSpan(from, words[next].Token.Start - from).IndexOfAny(",!\u2013\u2014-") >= 0;
        }

        static bool WordsHorizontal(string text, ContextLanes.TokenView left, ContextLanes.TokenView right)
        {
            int from = left.Token.Start + left.Token.Length;
            return right.Token.Start > from &&
                   TextPredicateLinkAnalyzer.Horizontal(text[from..right.Token.Start]);
        }

        static bool CleanLeadingTrivia(string text, int sentenceStart, int firstWordStart) =>
            sentenceStart >= 0 && firstWordStart >= sentenceStart && firstWordStart <= text.Length &&
            IsCleanTriviaOrPictographSpan(text.AsSpan(sentenceStart, firstWordStart - sentenceStart));
    }

    internal static bool IsShortMasculineSurface(
        string surface, string normalizedLemma, MorphologicalPartOfSpeech pos, bool adjectivalParticiple = false) =>
        pos == MorphologicalPartOfSpeech.Adjective || adjectivalParticiple
            ? surface.EndsWith("ия", StringComparison.Ordinal)
            : pos == MorphologicalPartOfSpeech.Noun &&
              normalizedLemma.Length > 0 &&
              !normalizedLemma.EndsWith('а') &&
              !normalizedLemma.EndsWith('я') &&
              !normalizedLemma.EndsWith('о') &&
              !normalizedLemma.EndsWith('е') &&
              (surface.EndsWith('а') || surface.EndsWith('я'));

    internal static bool IsShortMasculineDefiniteReading(string surface, ContextLanes.ReadingView row)
    {
        if (TryGetOrdinalAxes(row, out var ordAxes))
        {
            return ordAxes.Gender == MorphologicalGender.Masculine &&
                   ordAxes.Number == MorphologicalNumber.Singular &&
                   (ordAxes.Article == MorphologicalArticle.Short ||
                    ordAxes.Article == MorphologicalArticle.Definite && surface.EndsWith("ия", StringComparison.Ordinal));
        }
        MorphologicalFeatures f = row.Features;
        if (f.Gender != MorphologicalGender.Masculine || f.Number != MorphologicalNumber.Singular)
            return false;
        return f.Article == MorphologicalArticle.Short ||
               f.Article == MorphologicalArticle.Definite &&
               IsShortMasculineSurface(surface, BulgarianTextNormalizer.NormalizeKey(row.Lemma), f.PartOfSpeech,
                   IsMeasuredPassiveAttribute(f) || IsMeasuredPresentActiveAttribute(f));
    }

    internal static bool IsIntransitiveMotionOrStateLemma(string normalizedLemma) =>
        normalizedLemma is "изляза" or "вляза" or "дойда" or "отида" or "пристигна" or "говоря" or "работя" or "спя" or "живея" or
            "вървя" or "ходя" or "стоя" or "седя" or "лежа" or "падна" or "остана" or "закъснея" or "протестирам" or
            "плача" or "рухна" or "шумя" or "мълча" or "шепна" or "дишам" or "треперя" or "бързам";

    internal static bool IsAnimateSubjectInanimateVerbLemma(string normalizedLemma) =>
        normalizedLemma is "пея" or "танцувам" or "свиря" or "чета" or "играя";

    internal static bool IsAnimateNominalReading(ContextLanes.ReadingView row)
    {
        MorphologicalFeatures f = row.Features;
        if (f.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun)
            return f.NameKind != MorphologicalNameKind.PlaceName;
        return f.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
               (f.ParadigmId is "1a" or "7a" or "7b" or "8a" or "8c" or "8d" or "9b" or
                    "14a" or "14b" or "15a" or "15b" or "18a" or "19a" or "24" or "24a" or
                    "25" or "28b" or "31a" or "32a" or "33a" or "35a" or "41a" or "41b" or "42" or "43" ||
                BulgarianTextNormalizer.NormalizeKey(row.Lemma) is "хора" or "дете" or "момче" or "момиче");
    }

    internal static bool IsTemporalOrMeasureLemma(string normalizedLemma) =>
        normalizedLemma is "година" or "седмица" or "месец" or "ден" or "денонощие" or "нощ" or "вечер" or "сутрин" or "утро" or
            "обед" or "обяд" or "следобед" or "полунощ" or "привечер" or "зима" or "лято" or "пролет" or "есен" or
            "сезон" or "век" or "час" or "минута" or "секунда" or "миг" or "момент" or "период" or "срок" or
            "време" or "път" or "понеделник" or "вторник" or "сряда" or "четвъртък" or "петък" or "събота" or "неделя" or
            "километър" or "метър" or "миля" or "крачка" or "спирка" or "класа" or "смяна" or "ваканция" or "почивка";

    internal static bool IsPersonPlaceHomograph(TextAnalysis analysis, int tokenIndex) =>
        analysis.ProperNames?.Candidates.Any(c => c.TokenIndex == tokenIndex &&
            (c.Kinds & (ProperNameKinds.Person | ProperNameKinds.Place)) == (ProperNameKinds.Person | ProperNameKinds.Place)) == true;

    internal static bool WordOrPictographBridge(
        TextAnalysis analysis, ContextLanes.TokenView left, ContextLanes.TokenView right,
        CancellationToken ct, bool horizontalOnly = false)
    {
        if (LocalClauseRoleLinker.Connected(analysis, left, right, symbols: false, ct))
            return !horizontalOnly || TextPredicateLinkAnalyzer.Horizontal(
                analysis.Text[(left.Token.Start + left.Token.Length)..right.Token.Start]);
        if (right.OriginalIndex <= left.OriginalIndex + 1) return false;
        int from = left.Token.Start + left.Token.Length, to = right.Token.Start;
        if (from >= to) return false;
        bool sawSpace = false;
        for (int k = from; k < to; k++)
            if (analysis.Text[k] is ' ' or '\t') { sawSpace = true; break; }
        if (!sawSpace) return false;
        for (int i = left.OriginalIndex + 1; i < right.OriginalIndex; i++)
        {
            ct.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[i];
            if (!IsDecorativePictograph(token) || token.Start < from || token.Start + token.Length > to ||
                !TextPredicateLinkAnalyzer.Horizontal(analysis.Text[from..token.Start], allowEmpty: true))
                return false;
            from = token.Start + token.Length;
        }
        return TextPredicateLinkAnalyzer.Horizontal(analysis.Text[from..to], allowEmpty: true);
    }

    internal static bool HasUnexpectedNonPictographSymbol(
        TextAnalysis analysis, int firstTokenIndex, int lastTokenIndex,
        IReadOnlyList<(int Start, int End)>? ignoredSpans = null)
    {
        for (int t = firstTokenIndex; t <= lastTokenIndex; t++)
        {
            TokenAnalysis tok = analysis.Tokens[t];
            if (tok.Kind == TokenKind.Symbol && !IsDecorativePictograph(tok) &&
                !(ignoredSpans is not null && ignoredSpans.Any(s => s.Start < tok.Start + tok.Length && s.End > tok.Start)))
                return true;
        }
        return false;
    }

    internal static bool IsDecorativePictographScalar(Rune rune) =>
        (rune.Value is >= 0x2600 and <= 0x2BFF || rune.Value >= 0x1F000) &&
            Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol ||
        rune.Value is >= 0x1F3FB and <= 0x1F3FF or 0xFE0E or 0xFE0F or 0x200D;

    internal static bool IsDecorativePictograph(TokenAnalysis token) =>
        token.Kind == TokenKind.Symbol &&
        Rune.DecodeFromUtf16(token.Text, out Rune rune, out int consumed) == OperationStatus.Done &&
        consumed == token.Text.Length &&
        IsDecorativePictographScalar(rune);

    internal static bool IsCleanTriviaOrPictographSpan(ReadOnlySpan<char> span)
    {
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out Rune rune, out int consumed) != OperationStatus.Done)
                return false;
            span = span[consumed..];
            if (!Rune.IsWhiteSpace(rune) && rune.Value is not ('-' or '\u2013' or '\u2014') &&
                !IsDecorativePictographScalar(rune))
                return false;
        }
        return true;
    }
}
