using System.Collections.Frozen;
using BgLang.Contracts;

namespace BgLang.Core;

[Flags]
internal enum FunctionWordClasses : ulong
{
    None = 0,

    // Atomic pronominal & reflexive clitics
    ShortDativeClitic = 1UL << 0,       // ми ти му ѝ ни ви им
    ShortAccusativeClitic = 1UL << 1,   // ме те го я ни ви ги
    ReflexiveAccusativeSe = 1UL << 2,   // се
    ReflexiveDativeSi = 1UL << 3,       // си

    // SyntaxRoleCalculator historical clitic subsets (intentionally distinct regarding ѝ / ти / ви / си)
    SyntaxDaShortClitic = 1UL << 4,     // ти си ми му ви им ни
    SyntaxPossessiveClitic = 1UL << 5,  // си ми му ви им ни
    SyntaxFutureClitic = 1UL << 6,      // го я ги ме те ни

    // Verbal particles & clitic-host exclusions
    DaParticle = 1UL << 7,              // да
    FutureParticle = 1UL << 8,          // ще
    NegativeParticle = 1UL << 9,        // не
    InterrogativeParticle = 1UL << 10,  // ли
    CliticNonHostExtra = 1UL << 11,     // и (unaccented dative/conjunction non-host in ReviewedCliticPolicy)

    // Prepositions (each analyzer's exact validated scope preserved explicitly)
    LegacyChainPreposition = 1UL << 12,         // на към с без от до пред след
    ReviewedComplementPreposition = 1UL << 13,  // без в във до за зад към между на над около от под пред през при с след сред със
    ReportComplementPreposition = 1UL << 14,    // върху покрай срещу
    SyntaxRolePreposition = 1UL << 15,          // без в във до за из към на над от по под пред през при с със след
    AdpositionHomographPrunable = 1UL << 16,    // преди край между срещу около под

    // Conjunctions, subordinators & relative introducers
    CoreCoordinatingConjunction = 1UL << 17,    // и или а но
    ClauseCoordinatingConjunction = 1UL << 18,  // и но а или обаче ала пък
    CoreSubordinatingConjunction = 1UL << 19,   // че ако когато защото понеже докато
    CommaSubordinateTrigger = 1UL << 20,        // че защото когато който която което които ако понеже докато
    RelativeWord = 1UL << 21,                   // който която което които чийто чиято чието чиито където откъдето
    ClauseIntroducer = 1UL << 22,               // че защото понеже когато ако докато дали щом както който която което които където макар въпреки
    SingleWordPreposedSubordinator = 1UL << 23, // когато ако докато защото понеже щом макар въпреки
    PerfectivePresentSubordinator = 1UL << 24,  // когато ако докато щом като
    PureAccusativeClitic = 1UL << 25,           // ме те го я ги (strict non-reflexive non-dative short accusatives)
    FullObliquePronoun = 1UL << 26,             // мен мене теб тебе него нея нас вас тях
    ClauseCaseGovernedPrepositionExtra = 1UL << 27, // заради поради освен вместо чрез край преди въпреки
    RelativeSubjectPronoun = 1UL << 28,         // който която което които (nominative-capable; excludes когото)

    // Algebraic composite masks (zero duplicate word lists)
    ShortPronominalClitic = ShortDativeClitic | ShortAccusativeClitic,
    PossessiveOrDativeClitic = ShortDativeClitic | ReflexiveDativeSi,
    PronominalVerbalClitic = ShortAccusativeClitic | ShortDativeClitic | ReflexiveDativeSi,
    VerbalPrefixParticle = DaParticle | FutureParticle | NegativeParticle,
    VerbalPrefixOrReflexive = VerbalPrefixParticle | ReflexiveAccusativeSe | ReflexiveDativeSi,
    PreVerbalCliticBridge = NegativeParticle | ReflexiveAccusativeSe | PronominalVerbalClitic,
    PreVerbalClauseBridge = FutureParticle | PreVerbalCliticBridge,
    CliticNonHost = DaParticle | CliticNonHostExtra | PreVerbalClauseBridge,
    ClauseCaseGovernedPreposition =
        LegacyChainPreposition | ReviewedComplementPreposition | ReportComplementPreposition |
        SyntaxRolePreposition | ClauseCaseGovernedPrepositionExtra
}

internal readonly record struct PersonalSubjectPronounSpec(
    int LegacyTagId,
    MorphologicalPerson Person,
    MorphologicalNumber Number);

/// <summary>
/// Single compiled source of closed-class Bulgarian function words and canonical personal subject pronouns.
/// Preserves multi-class membership (e.g. ни/ви as both dative and accusative clitics) and intentional analyzer scopes.
/// </summary>
internal static class BulgarianFunctionWords
{
    private const FunctionWordClasses F = default;

    private static readonly FrozenDictionary<string, FunctionWordClasses> ClassesByForm =
        new Dictionary<string, FunctionWordClasses>(StringComparer.Ordinal)
        {
            // Pronominal & reflexive clitics
            ["ми"] = FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.SyntaxDaShortClitic | FunctionWordClasses.SyntaxPossessiveClitic,
            ["ти"] = FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.SyntaxDaShortClitic,
            ["му"] = FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.SyntaxDaShortClitic | FunctionWordClasses.SyntaxPossessiveClitic,
            ["ѝ"] = FunctionWordClasses.ShortDativeClitic,
            ["ни"] = FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.ShortAccusativeClitic |
                     FunctionWordClasses.SyntaxDaShortClitic | FunctionWordClasses.SyntaxPossessiveClitic | FunctionWordClasses.SyntaxFutureClitic,
            ["ви"] = FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.ShortAccusativeClitic |
                     FunctionWordClasses.SyntaxDaShortClitic | FunctionWordClasses.SyntaxPossessiveClitic,
            ["им"] = FunctionWordClasses.ShortDativeClitic | FunctionWordClasses.SyntaxDaShortClitic | FunctionWordClasses.SyntaxPossessiveClitic,
            ["ме"] = FunctionWordClasses.ShortAccusativeClitic | FunctionWordClasses.SyntaxFutureClitic | FunctionWordClasses.PureAccusativeClitic,
            ["те"] = FunctionWordClasses.ShortAccusativeClitic | FunctionWordClasses.SyntaxFutureClitic | FunctionWordClasses.PureAccusativeClitic,
            ["го"] = FunctionWordClasses.ShortAccusativeClitic | FunctionWordClasses.SyntaxFutureClitic | FunctionWordClasses.PureAccusativeClitic,
            ["я"] = FunctionWordClasses.ShortAccusativeClitic | FunctionWordClasses.SyntaxFutureClitic | FunctionWordClasses.PureAccusativeClitic,
            ["ги"] = FunctionWordClasses.ShortAccusativeClitic | FunctionWordClasses.SyntaxFutureClitic | FunctionWordClasses.PureAccusativeClitic,
            ["се"] = FunctionWordClasses.ReflexiveAccusativeSe,
            ["си"] = FunctionWordClasses.ReflexiveDativeSi | FunctionWordClasses.SyntaxDaShortClitic | FunctionWordClasses.SyntaxPossessiveClitic,

            // Particles
            ["да"] = FunctionWordClasses.DaParticle,
            ["ще"] = FunctionWordClasses.FutureParticle,
            ["не"] = FunctionWordClasses.NegativeParticle,
            ["ли"] = FunctionWordClasses.InterrogativeParticle,

            // Coordinating conjunctions
            ["и"] = FunctionWordClasses.CliticNonHostExtra | FunctionWordClasses.CoreCoordinatingConjunction | FunctionWordClasses.ClauseCoordinatingConjunction,
            ["или"] = FunctionWordClasses.CoreCoordinatingConjunction | FunctionWordClasses.ClauseCoordinatingConjunction,
            ["а"] = FunctionWordClasses.CoreCoordinatingConjunction | FunctionWordClasses.ClauseCoordinatingConjunction,
            ["но"] = FunctionWordClasses.CoreCoordinatingConjunction | FunctionWordClasses.ClauseCoordinatingConjunction,
            ["обаче"] = FunctionWordClasses.ClauseCoordinatingConjunction,
            ["ала"] = FunctionWordClasses.ClauseCoordinatingConjunction,
            ["пък"] = FunctionWordClasses.ClauseCoordinatingConjunction,

            // Prepositions
            ["без"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["в"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["във"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["вместо"] = FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["въпреки"] = FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator | FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["върху"] = FunctionWordClasses.ReportComplementPreposition,
            ["до"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["за"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["зад"] = FunctionWordClasses.ReviewedComplementPreposition,
            ["заради"] = FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["из"] = FunctionWordClasses.SyntaxRolePreposition,
            ["край"] = FunctionWordClasses.AdpositionHomographPrunable | FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["към"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["между"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.AdpositionHomographPrunable,
            ["на"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["над"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["около"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.AdpositionHomographPrunable,
            ["освен"] = FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["от"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["по"] = FunctionWordClasses.SyntaxRolePreposition,
            ["под"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition |
                      FunctionWordClasses.AdpositionHomographPrunable,
            ["покрай"] = FunctionWordClasses.ReportComplementPreposition,
            ["поради"] = FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["пред"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["преди"] = FunctionWordClasses.AdpositionHomographPrunable | FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,
            ["през"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["при"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["с"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["след"] = FunctionWordClasses.LegacyChainPreposition | FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["сред"] = FunctionWordClasses.ReviewedComplementPreposition,
            ["срещу"] = FunctionWordClasses.ReportComplementPreposition | FunctionWordClasses.AdpositionHomographPrunable,
            ["със"] = FunctionWordClasses.ReviewedComplementPreposition | FunctionWordClasses.SyntaxRolePreposition,
            ["чрез"] = FunctionWordClasses.ClauseCaseGovernedPrepositionExtra,

            // Subordinators & relative words
            ["че"] = FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.CommaSubordinateTrigger | FunctionWordClasses.ClauseIntroducer,
            ["защото"] = FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.CommaSubordinateTrigger |
                         FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator,
            ["понеже"] = FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.CommaSubordinateTrigger |
                         FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator,
            ["когато"] = FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.CommaSubordinateTrigger |
                         FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator |
                         FunctionWordClasses.PerfectivePresentSubordinator,
            ["ако"] = FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.CommaSubordinateTrigger |
                      FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator |
                      FunctionWordClasses.PerfectivePresentSubordinator,
            ["докато"] = FunctionWordClasses.CoreSubordinatingConjunction | FunctionWordClasses.CommaSubordinateTrigger |
                         FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator |
                         FunctionWordClasses.PerfectivePresentSubordinator,
            ["дали"] = FunctionWordClasses.ClauseIntroducer,
            ["както"] = FunctionWordClasses.ClauseIntroducer,
            ["щом"] = FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator |
                      FunctionWordClasses.PerfectivePresentSubordinator,
            ["макар"] = FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.SingleWordPreposedSubordinator,
            ["като"] = FunctionWordClasses.PerfectivePresentSubordinator,
            ["който"] = FunctionWordClasses.CommaSubordinateTrigger | FunctionWordClasses.RelativeWord | FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.RelativeSubjectPronoun,
            ["която"] = FunctionWordClasses.CommaSubordinateTrigger | FunctionWordClasses.RelativeWord | FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.RelativeSubjectPronoun,
            ["което"] = FunctionWordClasses.CommaSubordinateTrigger | FunctionWordClasses.RelativeWord | FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.RelativeSubjectPronoun,
            ["които"] = FunctionWordClasses.CommaSubordinateTrigger | FunctionWordClasses.RelativeWord | FunctionWordClasses.ClauseIntroducer | FunctionWordClasses.RelativeSubjectPronoun,
            ["когото"] = FunctionWordClasses.CommaSubordinateTrigger | FunctionWordClasses.RelativeWord | FunctionWordClasses.ClauseIntroducer,
            ["чийто"] = FunctionWordClasses.RelativeWord,
            ["чиято"] = FunctionWordClasses.RelativeWord,
            ["чието"] = FunctionWordClasses.RelativeWord,
            ["чиито"] = FunctionWordClasses.RelativeWord,
            ["където"] = FunctionWordClasses.RelativeWord | FunctionWordClasses.ClauseIntroducer,
            ["откъдето"] = FunctionWordClasses.RelativeWord,

            // Full oblique personal pronouns (tagged unknown:500 in neutral lexicon)
            ["мен"] = FunctionWordClasses.FullObliquePronoun,
            ["мене"] = FunctionWordClasses.FullObliquePronoun,
            ["теб"] = FunctionWordClasses.FullObliquePronoun,
            ["тебе"] = FunctionWordClasses.FullObliquePronoun,
            ["него"] = FunctionWordClasses.FullObliquePronoun,
            ["нея"] = FunctionWordClasses.FullObliquePronoun,
            ["нас"] = FunctionWordClasses.FullObliquePronoun,
            ["вас"] = FunctionWordClasses.FullObliquePronoun,
            ["тях"] = FunctionWordClasses.FullObliquePronoun
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, (MorphologicalPerson Person, MorphologicalNumber Number)> FullObliquePronouns =
        new Dictionary<string, (MorphologicalPerson Person, MorphologicalNumber Number)>(StringComparer.Ordinal)
        {
            ["мен"] = (MorphologicalPerson.First, MorphologicalNumber.Singular),
            ["мене"] = (MorphologicalPerson.First, MorphologicalNumber.Singular),
            ["теб"] = (MorphologicalPerson.Second, MorphologicalNumber.Singular),
            ["тебе"] = (MorphologicalPerson.Second, MorphologicalNumber.Singular),
            ["него"] = (MorphologicalPerson.Third, MorphologicalNumber.Singular),
            ["нея"] = (MorphologicalPerson.Third, MorphologicalNumber.Singular),
            ["нас"] = (MorphologicalPerson.First, MorphologicalNumber.Plural),
            ["вас"] = (MorphologicalPerson.Second, MorphologicalNumber.Plural),
            ["тях"] = (MorphologicalPerson.Third, MorphologicalNumber.Plural)
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, PersonalSubjectPronounSpec> PersonalSubjects =
        new Dictionary<string, PersonalSubjectPronounSpec>(StringComparer.Ordinal)
        {
            ["аз"] = new(90, MorphologicalPerson.First, MorphologicalNumber.Singular),
            ["ти"] = new(91, MorphologicalPerson.Second, MorphologicalNumber.Singular),
            ["той"] = new(92, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            ["тя"] = new(93, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            ["то"] = new(94, MorphologicalPerson.Third, MorphologicalNumber.Singular),
            ["ние"] = new(95, MorphologicalPerson.First, MorphologicalNumber.Plural),
            ["вие"] = new(96, MorphologicalPerson.Second, MorphologicalNumber.Plural),
            ["те"] = new(97, MorphologicalPerson.Third, MorphologicalNumber.Plural)
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static FunctionWordClasses GetClasses(string normalizedForm) =>
        ClassesByForm.TryGetValue(normalizedForm, out FunctionWordClasses classes) ? classes : F;

    internal static bool HasAny(string normalizedForm, FunctionWordClasses mask) =>
        ClassesByForm.TryGetValue(normalizedForm, out FunctionWordClasses classes) && (classes & mask) != 0;

    internal static bool IsNominalFocusParticle(string normalizedForm) =>
        normalizedForm is "дори" or "даже" or "само" or "едва" or "почти" or "чак" or "тъкмо" or "именно";

    internal static bool TryGetPersonalSubjectPronoun(string normalizedForm, out PersonalSubjectPronounSpec spec) =>
        PersonalSubjects.TryGetValue(normalizedForm, out spec);

    internal static bool IsFirstOrSecondSubjectPronoun(string normalizedForm, out PersonalSubjectPronounSpec spec) =>
        PersonalSubjects.TryGetValue(normalizedForm, out spec) &&
        spec.Person is MorphologicalPerson.First or MorphologicalPerson.Second;

    internal static bool IsThirdSubjectPronoun(string normalizedForm) =>
        PersonalSubjects.TryGetValue(normalizedForm, out PersonalSubjectPronounSpec spec) &&
        spec.Person == MorphologicalPerson.Third;

    internal static bool IsThirdSingularSubjectPronoun(string normalizedForm) =>
        PersonalSubjects.TryGetValue(normalizedForm, out PersonalSubjectPronounSpec spec) &&
        spec is { Person: MorphologicalPerson.Third, Number: MorphologicalNumber.Singular };

    internal static bool TryGetFullObliquePronoun(
        string normalizedForm, out MorphologicalPerson person, out MorphologicalNumber number)
    {
        if (FullObliquePronouns.TryGetValue(normalizedForm, out var axes))
        {
            person = axes.Person;
            number = axes.Number;
            return true;
        }
        person = default;
        number = default;
        return false;
    }

    internal static bool TryGetRelativeSubjectAxes(
        string normalizedForm, out MorphologicalNumber number, out MorphologicalGender? gender)
    {
        switch (normalizedForm)
        {
            case "който":
                number = MorphologicalNumber.Singular;
                gender = MorphologicalGender.Masculine;
                return true;
            case "която":
                number = MorphologicalNumber.Singular;
                gender = MorphologicalGender.Feminine;
                return true;
            case "което":
                number = MorphologicalNumber.Singular;
                gender = MorphologicalGender.Neuter;
                return true;
            case "които":
                number = MorphologicalNumber.Plural;
                gender = null;
                return true;
            default:
                number = default;
                gender = null;
                return false;
        }
    }
}
