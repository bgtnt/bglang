// Typed guards shared by the bounded contextual lanes.
// Callers must provide complete exact inventories and retain every original row.
using System.Collections.Generic;
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

    internal static bool AttributeAgreement(MorphologicalFeatures attribute, MorphologicalFeatures noun) =>
        attribute.PartOfSpeech == MorphologicalPartOfSpeech.Adjective &&
        noun.PartOfSpeech == MorphologicalPartOfSpeech.Noun && HasNominalAxes(attribute) && HasNominalAxes(noun) &&
        NominalTupleMatches((attribute.Number, attribute.Gender), (noun.Number, noun.Gender));

    private static bool ExplicitNominalTuple(MorphologicalFeatures f) =>
        !f.HasConflictingFeatures && IsNumber(f.Number) &&
        (f.Number == MorphologicalNumber.Plural || f.Gender is
            MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter);
    // These eight exact canonical subject cells license omitted Case/Short markers.
    // Other null-feature pronouns are not inferred to be nominative or long forms.
    internal static bool LicensedSubject(string canonical, ContextLanes.ReadingView row)
    {
        var license = canonical switch
        {
            "аз" => (90, MorphologicalPerson.First, MorphologicalNumber.Singular),
            "ти" => (91, MorphologicalPerson.Second, MorphologicalNumber.Singular),
            "той" => (92, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            "тя" => (93, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            "то" => (94, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            "ние" => (95, MorphologicalPerson.First, MorphologicalNumber.Plural),
            "вие" => (96, MorphologicalPerson.Second, MorphologicalNumber.Plural),
            "те" => (97, MorphologicalPerson.Third, MorphologicalNumber.Plural),
            _ => default
        };
        MorphologicalFeatures f = row.Features;
        return license.Item1 != 0 && row.Category == ContextLanes.Category.Pronoun && row.SourcePersonalPronoun &&
            f.PartOfSpeech == MorphologicalPartOfSpeech.PersonalPronoun && !f.HasConflictingFeatures && !f.HasUnrecognizedFeatures &&
            f.Person == license.Item2 && f.Number == license.Item3 && f.IsShortForm != true &&
            f.Case is null &&
            StringComparer.Ordinal.Equals(BulgarianTextNormalizer.NormalizeKey(row.Lemma), canonical) &&
            StringComparer.Ordinal.Equals(f.RawTag, "Лично_местоимение:" + license.Item1.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsNumber(MorphologicalNumber? number) =>
        number is MorphologicalNumber.Singular or MorphologicalNumber.Plural;
}
