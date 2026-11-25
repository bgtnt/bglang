using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>The rules exposed by the executed nominal checks, in stable order.</summary>
internal static class NominalGrammarRules
{
    internal const string AttributeId = "local_exact_attribute_agreement.v1";
    internal const string PrepositionalPlacementId = "grammar.article.first_prepositional_nominal_member.v1";
    internal const string InitialPlacementId = "grammar.article.first_initial_nominal_member.v1";
    internal const string ShortArticleId = "grammar.short_article.exact_prepositional_group.v1";
    internal const string CopularFiniteId = "grammar.agreement.copular_finite.v1";
    internal const string CopularAdjectiveId = "grammar.agreement.copular_adjective.v1";
    internal const string PassiveParticipleId = "grammar.agreement.passive_participle.v1";
    internal const string AnalyticFiniteId = "grammar.agreement.analytic_finite.v1";
    internal const string ActiveControllerId = "grammar.agreement.active_participle_controller.v1";
    internal const string AuxiliaryParticipleId = "grammar.agreement.auxiliary_active_participle.v1";
    internal const string SubjectPredicateId = ClauseFusionAnalyzer.AgreementLane;
    internal const string SubjectFullArticleId = ClauseFusionAnalyzer.SubjectArticleRule;
    internal const string PredicativeCode = "productive_predicative_agreement_mismatch";
    internal const string AuxiliaryParticipleCode = "auxiliary_participle_agreement_mismatch";

    private const string CarrierExplanation = "В тази именна група членът се поставя на първото определение.";
    private const string CarrierSource = "Мониторинг на българския език, ИБЕ, § 7.3, с. 40";
    private const string CarrierUrl = "https://ibl.bas.bg/wp-content/uploads/2023/06/3_MONITORING_2018-2020_str.-1-751.pdf#page=40";
    private const string PredicativeSource = "П. Осенова, К. Симов, „Формална граматика на българския език“, § 5.1, с. 52";
    private const string PredicativeUrl = "https://bultreebank.org/wp-content/uploads/2017/04/FormalGrammarBG.pdf#page=52";
    private const string AnalyticSource = "П. Осенова, К. Симов, „Формална граматика на българския език“, § 4.2, с. 39";
    private const string AnalyticUrl = "https://bultreebank.org/wp-content/uploads/2017/04/FormalGrammarBG.pdf#page=39";
    internal static readonly IReadOnlyList<TextGrammarRule> Catalog = Array.AsReadOnly<TextGrammarRule>(
    [
        new(AttributeId, ContextLanes.ProductiveAttributeMismatch, "П+ С", TextGrammarConstraint.NumberGender,
            "Определението и съществителното трябва да съвпадат по число, а в единствено число и по род.",
            "И. Кунева, „Случаи на колебания при съгласуване“, с. 75",
            "https://www.balgarskiezik.eu/1-2015/0_0_7_ILIYANA%20KUNEVA_str.%2075-81.pdf"),
        new(PrepositionalPlacementId, "nominal_article_on_noninitial_member", "ПРЕДЛ П+ С", TextGrammarConstraint.ArticlePlacement,
            CarrierExplanation, CarrierSource, CarrierUrl),
        new(InitialPlacementId, "nominal_article_on_noninitial_member", "П+ С", TextGrammarConstraint.ArticlePlacement,
            CarrierExplanation, CarrierSource, CarrierUrl),
        new(ShortArticleId, "short_article_expected_after_preposition_candidate", "ПРЕДЛ ИГ", TextGrammarConstraint.MasculineArticle,
            "В тази предложна група членуваната форма в мъжки род, единствено число, изисква кратък член.",
            "БЕРОН § 17.6.2", "https://beron.mon.bg/rechnik/363"),
        new(CopularFiniteId, ContextLanes.ProductiveGraphMismatch, "ЯВЕН ПОДЛОГ КОПУЛА П", TextGrammarConstraint.PersonNumber,
            "Личната форма на глагола се съгласува с явния подлог по лице и число.",
            PredicativeSource, PredicativeUrl),
        new(CopularAdjectiveId, PredicativeCode, "ЯВЕН ПОДЛОГ КОПУЛА П", TextGrammarConstraint.NumberGender,
            "Сказуемното прилагателно се съгласува с подлога по число и по установим род. При учтива форма към едно лице прилагателното е в единствено число.",
            PredicativeSource, PredicativeUrl),
        new(PassiveParticipleId, PredicativeCode, "ЯВЕН ПОДЛОГ КОПУЛА СТРАДАТЕЛНО ПРИЧАСТИЕ", TextGrammarConstraint.NumberGender,
            "Страдателното причастие се съгласува по число и установим род. При учтива форма към едно лице причастието е в единствено число.",
            "БЕРОН § 24.1.2", "https://beron.mon.bg/rechnik/566"),
        new(AnalyticFiniteId, ContextLanes.ProductiveGraphMismatch, "ЯВЕН ПОДЛОГ СПОМАГАТЕЛЕН ГЛАГОЛ -Л ФОРМА", TextGrammarConstraint.PersonNumber,
            "Личната форма на спомагателния глагол се съгласува с явния подлог по лице и число.", AnalyticSource, AnalyticUrl),
        new(ActiveControllerId, PredicativeCode, "ЯВЕН ПОДЛОГ И -Л ФОРМА", TextGrammarConstraint.NumberGender,
            "Деятелното причастие на -л се съгласува с подлога по число и установим род. При учтива форма се използва множествено число.",
            "БЕРОН § 24.1.1", "https://beron.mon.bg/rechnik/566"),
        new(AuxiliaryParticipleId, AuxiliaryParticipleCode, "СПОМАГАТЕЛЕН ГЛАГОЛ И -Л ФОРМА", TextGrammarConstraint.NumberGender,
            "Личната форма на спомагателния глагол и причастието на -л в тази група трябва да съвпадат по число.", AnalyticSource, AnalyticUrl),
        new(SubjectPredicateId, ContextLanes.ProductiveGraphMismatch, "ИГ_ПОДЛОГ ГГ_СКАЗУЕМО", TextGrammarConstraint.PersonNumber,
            "Групата на подлога и групата на сказуемото трябва да се съгласуват по лице и число.",
            "П. Осенова, К. Симов, „Формална граматика на българския език“, § 5.2, с. 57–58",
            "https://bultreebank.org/wp-content/uploads/2017/04/FormalGrammarBG.pdf#page=57"),
        new(SubjectFullArticleId, "full_article_expected_for_subject_candidate", "ИГ_ПОДЛОГ ГГ_СКАЗУЕМО", TextGrammarConstraint.MasculineArticle,
            "Групата на подлога в мъжки род, единствено число, изисква пълен член на първия членуван член.",
            "БЕРОН § 17.6.1", "https://beron.mon.bg/rechnik/362"),
        new(ClauseFusionAnalyzer.SharedSubjectCoordinationRule, ContextLanes.ProductiveGraphMismatch,
            "ЯВЕН ПОДЛОГ ГГ И ГГ", TextGrammarConstraint.PersonNumber,
            "Ако координираните сказуеми имат общ подлог, всяка лична форма трябва да се съгласува с него по лице и число. Възможен е и отделен неизразен подлог.",
            "П. Осенова, К. Симов, „Формална граматика на българския език“, § 5.2, с. 57–58",
            "https://bultreebank.org/wp-content/uploads/2017/04/FormalGrammarBG.pdf#page=57")
    ]);

    internal sealed record AttributeMatch(NominalStructuralProjection.Binding Binding,
        ContextLanes.NominalPhrase? Phrase, string? Reason, bool Governed = false, bool HasFinite = false);

    internal static TextGrammarMatch Match(string rule, NominalStructuralProjection.Binding binding,
        TextGrammarAssessment assessment, IReadOnlyList<TextGrammarConflict> conflicts,
        IReadOnlyList<TextGrammarRepair> repairs, string? reason = null,
        IReadOnlyList<int>? controllers = null, IReadOnlyList<int>? dependents = null) =>
        new(rule, binding.Start, binding.Length,
            controllers ?? Array.AsReadOnly(new[] { binding.Head.Word.OriginalIndex }),
            dependents ?? Array.AsReadOnly(binding.Participants.Take(binding.Participants.Count - 1)
                .Select(member => member.Word.OriginalIndex).ToArray()), assessment, conflicts, repairs, reason);

    internal static IReadOnlyList<TextReadingReference> References(NominalStructuralProjection.Member member,
        IReadOnlyList<int>? rows = null) =>
        Array.AsReadOnly((rows ?? member.RoleReadings).Select(row => new TextReadingReference(member.Word.OriginalIndex, row)).ToArray());

    internal static TextGrammarRepair Repair(ContextLanes.TokenView word, string replacement) =>
        new(Array.AsReadOnly(new[] { new TextGrammarEdit(word.OriginalIndex, word.Token.Start, word.Token.Length, replacement) }));
}
