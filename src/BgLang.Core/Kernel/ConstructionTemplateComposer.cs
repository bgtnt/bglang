using System;
using System.Collections.Generic;
using System.Threading;
using BgLang.Contracts;

namespace BgLang.Core;

// Input is one complete, already validated owner snapshot; acquisition is the caller's responsibility.
internal static class ConstructionTemplateComposer
{
    private enum ParticipleShape
    {
        CopulaAfter, RenarrativeCopulaAfter, BilBefore,
        FuturePerfect, FuturePerfectInPast, PastPerfect, Conditional
    }

    internal static IReadOnlyList<WordConstructionCandidate> Compose(
        string? entryId, string lexemeKey, string ownerLemma, IReadOnlyList<TextReadingSnapshot> ownForms,
        CancellationToken cancellationToken, IReadOnlySet<WordReading>? exactAnchors = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lexemeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLemma);
        ArgumentNullException.ThrowIfNull(ownForms);
        cancellationToken.ThrowIfCancellationRequested();
        string lemmaKey = BulgarianTextNormalizer.NormalizeKey(ownerLemma);
        var candidates = new List<WordConstructionCandidate>();
        for (int ownFormIndex = 0; ownFormIndex < ownForms.Count; ownFormIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TextReadingSnapshot snapshot = ownForms[ownFormIndex];
            WordReading form = snapshot.Raw;
            ArgumentNullException.ThrowIfNull(form);
            if (exactAnchors is not null && !exactAnchors.Contains(form)) continue;
            if (!string.Equals(BulgarianTextNormalizer.NormalizeKey(form.Lemma), lemmaKey,
                    StringComparison.Ordinal)) continue;
            MorphologicalFeatures features = snapshot.Features; // One shared typed projection, no local tag parser.
            if (features.PartOfSpeech != MorphologicalPartOfSpeech.Verb || features.HasConflictingFeatures ||
                features.Number is not (MorphologicalNumber.Singular or MorphologicalNumber.Plural)) continue;
            bool plural = features.Number == MorphologicalNumber.Plural;
            string numberLabel = plural ? "мн.ч." : "ед.ч.";
            if (features.VerbForm == MorphologicalVerbForm.Finite && features.Tense == MorphologicalTense.Present &&
                features.Person is MorphologicalPerson.First or MorphologicalPerson.Second or MorphologicalPerson.Third)
            {
                MorphologicalPerson person = features.Person.Value;
                Emit("reflexive-present", "възвр.страд.форма", person, false, null, [], ["се"]);
                Finite("future-present", "бъд.вр.", false, ["ще"]);
                Finite("negative-future-present", "бъд.вр.", true, ["няма", "да"]);
                Finite("future-in-past-present", "бъд.вр. в мин.", false, [PastAuxiliary(person, plural, true), "да"]);
                Finite("negative-future-in-past-present", "бъд.вр. в мин.", true, ["нямаше", "да"]);
                RenarrativeDa("future-renarrative-da", "пр.накл., бъд.вр.", false);
                RenarrativeDa("negative-future-renarrative-da", "пр.накл., бъд.вр.", true);
                RenarrativeDa("future-in-past-renarrative-da", "пр.накл., бъд.вр. в мин.", false);
                RenarrativeDa("negative-future-in-past-renarrative-da", "пр.накл., бъд.вр. в мин.", true);
            }
            else if (features.VerbForm == MorphologicalVerbForm.Participle && features.Tense == MorphologicalTense.Past &&
                features.Article is not (MorphologicalArticle.Definite or MorphologicalArticle.Full) &&
                (plural || features.Gender is MorphologicalGender.Masculine or MorphologicalGender.Feminine or MorphologicalGender.Neuter))
            {
                if (features.ParticipleKind == MorphologicalParticipleKind.PastImperfectActive)
                    Participle("renarrative-present", "пр.накл., сег.вр.", ParticipleShape.RenarrativeCopulaAfter);
                else if (features.ParticipleKind == MorphologicalParticipleKind.PastAoristActive)
                {
                    Participle("past-indefinite", "мин.неопр.вр.", ParticipleShape.CopulaAfter);
                    Participle("renarrative-past-aorist", "пр.накл., мин.св.вр.", ParticipleShape.RenarrativeCopulaAfter);
                    Participle("renarrative-past-indefinite", "пр.накл., мин.неопр.вр.", ParticipleShape.BilBefore);
                    Participle("renarrative-past-perfect", "пр.накл., мин.пред.вр.", ParticipleShape.BilBefore);
                    Participle("future-perfect", "бъд.пред.вр.", ParticipleShape.FuturePerfect);
                    Participle("negative-future-perfect", "бъд.пред.вр.", ParticipleShape.FuturePerfect, true);
                    Participle("future-perfect-in-past", "бъд.пред.вр. в мин.", ParticipleShape.FuturePerfectInPast);
                    Participle("negative-future-perfect-in-past", "бъд.пред.вр. в мин.", ParticipleShape.FuturePerfectInPast, true);
                    Participle("past-perfect", "мин.пред.вр.", ParticipleShape.PastPerfect);
                    Participle("conditional", "условно наклонение", ParticipleShape.Conditional);
                }
            }

            void Emit(string family, string construction, MorphologicalPerson person, bool negative,
                string? genderLabel, string[] before, string[] after)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parts = new string[before.Length + 1 + after.Length];
                before.CopyTo(parts, 0);
                parts[before.Length] = form.Form;
                after.CopyTo(parts, before.Length + 1);
                string tag = string.Join(", ", construction, PersonLabel(person), numberLabel);
                if (genderLabel is not null) tag += ", " + genderLabel;
                if (negative) tag += ", отрицание";
                candidates.Add(new("typed-construction-candidate." + family + ".v1", entryId, lexemeKey,
                    ownFormIndex, snapshot, Array.AsReadOnly(parts), before.Length, tag));
            }

            void Finite(string family, string construction, bool negative, string[] before) =>
                Emit(family, construction, features.Person!.Value, negative, null, before, []);

            void RenarrativeDa(string family, string construction, bool negative)
            {
                MorphologicalPerson person = features.Person!.Value;
                string first = negative ? "нямало" : plural ? "щели" : "щял";
                string[] before = person == MorphologicalPerson.Third
                    ? [first, "да"] : [first, Copula(person, plural), "да"];
                Finite(family, construction, negative, before);
            }

            void Participle(string family, string construction, ParticipleShape shape, bool negative = false)
            {
                // Persons belong to the target construction; never infer them from participle boilerplate.
                foreach (MorphologicalPerson person in new[] { MorphologicalPerson.First, MorphologicalPerson.Second, MorphologicalPerson.Third })
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string copula = Copula(person, plural);
                    string[] reducedCopula = person == MorphologicalPerson.Third ? [] : [copula];
                    string[] before = [];
                    string[] after = [];
                    switch (shape)
                    {
                        case ParticipleShape.CopulaAfter: after = [copula]; break;
                        case ParticipleShape.RenarrativeCopulaAfter: after = reducedCopula; break;
                        case ParticipleShape.BilBefore:
                            string bil = plural ? "били" : features.Gender switch
                            {
                                MorphologicalGender.Feminine => "била",
                                MorphologicalGender.Neuter => "било",
                                MorphologicalGender.Masculine => "бил",
                                _ => throw new InvalidOperationException("Singular participle gender unavailable.")
                            };
                            before = person == MorphologicalPerson.Third ? [bil] : [bil, copula];
                            break;
                        case ParticipleShape.FuturePerfect:
                            before = negative ? ["няма", "да", copula] : ["ще", copula]; break;
                        case ParticipleShape.FuturePerfectInPast:
                            before = [negative ? "нямаше" : PastAuxiliary(person, plural, true), "да", copula]; break;
                        case ParticipleShape.PastPerfect: before = [PastAuxiliary(person, plural, false)]; break;
                        case ParticipleShape.Conditional: before = [ConditionalAuxiliary(person, plural)]; break;
                        default: throw new ArgumentOutOfRangeException(nameof(shape));
                    }
                    string? gender = plural ? null : features.Gender switch
                    {
                        MorphologicalGender.Masculine => "м.р.",
                        MorphologicalGender.Feminine => "ж.р.",
                        MorphologicalGender.Neuter => "ср.р.",
                        _ => throw new InvalidOperationException("Singular participle gender unavailable.")
                    };
                    Emit(family, construction, person, negative, gender, before, after);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(candidates.ToArray());
    }

    private static string PersonLabel(MorphologicalPerson person) => person switch
    {
        MorphologicalPerson.First => "1л.", MorphologicalPerson.Second => "2л.", MorphologicalPerson.Third => "3л.",
        _ => throw new ArgumentOutOfRangeException(nameof(person))
    };
    private static string Copula(MorphologicalPerson person, bool plural) => person switch
    {
        MorphologicalPerson.First => plural ? "сме" : "съм",
        MorphologicalPerson.Second => plural ? "сте" : "си",
        MorphologicalPerson.Third => plural ? "са" : "е",
        _ => throw new ArgumentOutOfRangeException(nameof(person))
    };
    private static string PastAuxiliary(MorphologicalPerson person, bool plural, bool future) => (person, plural, future) switch
    {
        (MorphologicalPerson.First, false, true) => "щях", (MorphologicalPerson.First, false, false) => "бях",
        (MorphologicalPerson.Second or MorphologicalPerson.Third, false, true) => "щеше",
        (MorphologicalPerson.Second or MorphologicalPerson.Third, false, false) => "беше",
        (MorphologicalPerson.First, true, true) => "щяхме", (MorphologicalPerson.First, true, false) => "бяхме",
        (MorphologicalPerson.Second, true, true) => "щяхте", (MorphologicalPerson.Second, true, false) => "бяхте",
        (MorphologicalPerson.Third, true, true) => "щяха", (MorphologicalPerson.Third, true, false) => "бяха",
        _ => throw new ArgumentOutOfRangeException(nameof(person))
    };
    private static string ConditionalAuxiliary(MorphologicalPerson person, bool plural) => (person, plural) switch
    {
        (MorphologicalPerson.First, false) => "бих",
        (MorphologicalPerson.Second or MorphologicalPerson.Third, false) => "би",
        (MorphologicalPerson.First, true) => "бихме", (MorphologicalPerson.Second, true) => "бихте",
        (MorphologicalPerson.Third, true) => "биха",
        _ => throw new ArgumentOutOfRangeException(nameof(person))
    };
}
