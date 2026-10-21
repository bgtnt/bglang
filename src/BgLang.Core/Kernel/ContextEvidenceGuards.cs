// Typed guards shared by the bounded contextual lanes.
// Callers must provide complete exact inventories and retain every original row.
using System.Collections.Generic;
using System.Threading;
using BgLang.Contracts;

namespace BgLang.Core;

internal static class ContextEvidenceGuards
{
    internal enum Agreement { Unmeasured, Compatible, Mismatch }

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
                reading.Number != nounTuple.Number ||
                reading.Number == MorphologicalNumber.Singular && reading.Gender != nounTuple.Gender)
                continue;
            var tuple = (reading.Number, reading.Gender);
            if (selectedTuple is { } previous && previous != tuple) return [];
            selectedTuple = tuple;
            selected.Add(i);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return selected.ToArray();
    }



    private static bool ExplicitNominalTuple(MorphologicalFeatures f) =>
        !f.HasConflictingFeatures && IsNumber(f.Number) &&
        (f.Number == MorphologicalNumber.Plural || f.Gender is
            MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter);
    private static bool IsNumber(MorphologicalNumber? number) =>
        number is MorphologicalNumber.Singular or MorphologicalNumber.Plural;
}
