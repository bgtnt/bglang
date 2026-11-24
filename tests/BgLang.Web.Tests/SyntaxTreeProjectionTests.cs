using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass]
public sealed class SyntaxTreeProjectionTests
{
    [TestMethod]
    public void Compact_word_metadata_preserves_undisplayed_complete_reading_ambiguity_and_exact_unknown_status()
    {
        TextAnalysis input = Text("пила пила щщ", "пила", "пила", "щщ");
        WordReading noun = Reading("пила", "пила", "Съществително:41,Женски");
        WordReading verb = Reading("пила", "пия", "Глагол:150,Минало_свършено_деятелно,Женски");
        input = input with
        {
            Tokens = input.Tokens.Select((token, index) => token with
                { Analysis = new(token.Text, token.Text, index == 2 ? [] : [noun], null) }).ToArray(),
            ReadingInventory = new(
                [new("пила", "пила", [Snapshot(noun), Snapshot(verb)], null, null, 1),
                 new("щщ", "щщ", [], null, null)],
                [new(0, "пила", 0, 4, 0), new(1, "пила", 5, 4, 0), new(2, "щщ", 10, 2, 1)])
        };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(input);

        CollectionAssert.AreEqual(new[] { "Noun", "Verb" }, tree.Tokens[0].Pos.ToArray());
        CollectionAssert.AreEqual(new[] { "пила", "пия" }, tree.Tokens[1].Lemmas.ToArray());
        Assert.AreEqual(2, tree.Tokens[0].Tags.Count);
        Assert.AreEqual(true, tree.Tokens[0].Known);
        Assert.AreEqual(false, tree.Tokens[2].Known);
        Assert.AreEqual(0, tree.Tokens[2].Pos.Count);
        Assert.AreEqual(0, tree.Suggestions.Count);
        string json = JsonSerializer.Serialize(tree, HttpContract.Json);
        Assert.IsFalse(json.Contains("legacyTypeId", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("canonicalReadings", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Diagnostic_edits_use_the_existing_Lab_allowlist_and_original_repeated_utf16_occurrence()
    {
        TextAnalysis input = Text("🙂 Непея Непея.", "🙂", "Непея", "Непея", ".");
        TokenAnalysis second = input.Tokens[2];
        input = input with { Diagnostics =
            [new("reviewed_word_formation_evidence", input.Tokens[1].Start, 5, "formation") { ProjectedText = "пея" },
             new("joined_verb_negation_should_be_separate", second.Start, second.Length, "negation")
                 { ProjectedText = "Не пея" }] };

        SyntaxTreeSuggestion suggestion = SyntaxTreeProjection.Build(input).Suggestions.Single();

        Assert.AreEqual("diagnostic", suggestion.Source);
        Assert.AreEqual("negation", suggestion.RuleId);
        Assert.IsFalse(suggestion.Conditional);
        Assert.AreEqual(9, suggestion.Start);
        CollectionAssert.AreEqual(new[] { 2 }, suggestion.TokenIndices.ToArray());
        Assert.AreEqual(new SyntaxTreeEdit(2, 9, 5, "Непея", "Не пея"), suggestion.Edits.Single());
        TextAnalysis splitMark = Text("а\u0301", "а") with { Diagnostics =
            [new("accent_sensitive_i_grave", 0, 1, "test") { ProjectedText = "ѝ" }] };
        Assert.AreEqual(0, SyntaxTreeProjection.Build(splitMark).Suggestions.Count,
            "An action must not detach an original combining mark.");
    }

    [TestMethod]
    public void Compound_conjunction_warning_keeps_the_original_comma_span_without_an_edit()
    {
        const string code = "spurious_comma_inside_compound_conjunction";
        TextAnalysis input = Text("🙂 Дойдох, за да чета, за, да пиша.",
            "🙂", "Дойдох", ",", "за", "да", "чета", ",", "за", ",", "да", "пиша", ".");
        TokenAnalysis comma = input.Tokens[8];
        TextValidationDiagnostic warning = new(code, comma.Start, comma.Length, "compound_conjunction", []);
        input = input with { Validation = new("validation.v1", true, null, [warning], null, [warning], null, []) };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(input);
        SyntaxTreeDiagnostic diagnostic = tree.Diagnostics.Single();

        Assert.AreEqual((code, input.Text.LastIndexOf(','), 1), (diagnostic.Code, diagnostic.Start, diagnostic.Length));
        Assert.AreEqual("Излишна запетая между частите на сложния съюз.", diagnostic.Message);
        Assert.IsNull(diagnostic.RuleId);
        SyntaxTreeToken target = tree.Tokens.Single(token => token.Start == diagnostic.Start && token.Length == diagnostic.Length);
        Assert.AreEqual((8, ",", "Punctuation"), (target.Index, target.Text, target.Kind));
        Assert.AreEqual(0, tree.Suggestions.Count, "Punctuation evidence must not become an edit action.");
        Assert.AreEqual("🙂 Дойдох, за да чета, за, да пиша.", input.Text);
    }

    [TestMethod]
    public void Spelling_options_keep_all_returned_variants_case_and_valid_previous_modifier_witnesses()
    {
        TextAnalysis input = Text("🙂 Нова Кнгиа. Нова Кнгиа.", "🙂", "Нова", "Кнгиа", ".", "Нова", "Кнгиа", ".");
        WordReading modifier = Reading("Нова", "нов", "Прилагателно:76,Женски");
        input = input with { Tokens = input.Tokens.Select((token, index) => index == 4
            ? token with { Analysis = new(token.Text, "нова", [modifier], null) } : token).ToArray() };
        TokenAnalysis typo = input.Tokens[5];
        input = input with { Spelling = new(2, 10,
            [new(5, typo.Text, typo.Start, typo.Length, "кнгиа", SpellingSearchDisposition.Completed, null,
                [new("книга", 1, [Reading("книга", "книга", "Съществително:41,Женски")]),
                 new("книги", 2, [Reading("книги", "книга", "Съществително:41,мн.ч.")])])
                { ContextualCandidates = [new(0, SpellingContextAssessment.Supported, [])
                    { PreviousModifierMatches = [new(0, new(4, 0))] }] }]) };

        SyntaxTreeSuggestion[] options = SyntaxTreeProjection.Build(input).Suggestions.ToArray();

        Assert.AreEqual(2, options.Length);
        CollectionAssert.AreEqual(new[] { "Книга", "Книги" }, options.Select(option => option.Edits.Single().Replacement).ToArray());
        Assert.AreEqual("Supported", options[0].Assessment);
        Assert.AreEqual("Unassessed", options[1].Assessment);
        CollectionAssert.AreEqual(new[] { 5, 4 }, options[0].TokenIndices.ToArray());
        Assert.IsTrue(options.All(option => option.Conditional && option.Start == typo.Start && option.Group == "spelling:5"));
        Assert.AreNotEqual(options[0].Id, options[1].Id);
    }

    [TestMethod]
    public void Nominal_options_require_one_coherent_head_and_replacement_reading_witness_for_supported_label()
    {
        TextAnalysis input = Text("Рисувам сипя река.", "Рисувам", "сипя", "река", ".");
        WordReading noun = Reading("река", "река", "Съществително:41,Женски");
        input = input with { Tokens = input.Tokens.Select((token, index) => index == 2
            ? token with { Analysis = new(token.Text, token.Text, [noun], null) } : token).ToArray() };
        TokenAnalysis edited = input.Tokens[1], head = input.Tokens[2];
        TextNominalPhrase phrase = new("NP", edited.Start, head.Start + head.Length - edited.Start, 2, [1], [],
            NominalAgreementAssessment.Compatible,
            [new(new(2, 0), null, [new(1, NominalAgreementAssessment.Compatible, [], []) { ReplacementReadings = [0] }])]);
        FuzzyMatch alternative = new("синя", 1, [Reading("синя", "син", "Прилагателно:79,Женски")]);
        input = input with { NominalConstructions = new("nominal.v1", true, null, [],
            [new(1, edited.Start, edited.Length, "finite+NP", true, 1, 10, false,
                [new(alternative, phrase), new(alternative with { Form = "сипа" }, phrase with
                    { HeadSupports = [new(new(2, 99), null, phrase.HeadSupports[0].Members)] }),
                 new(alternative with { Form = "сипя" }, phrase)])]) };

        SyntaxTreeSuggestion[] options = SyntaxTreeProjection.Build(input).Suggestions.ToArray();

        Assert.AreEqual(2, options.Length, "No-op branches remain possible without becoming edit actions.");
        Assert.AreEqual("Supported", options[0].Assessment);
        Assert.AreEqual("Unassessed", options[1].Assessment);
        Assert.IsTrue(options.All(option => option.Conditional && option.Group == "nominal:0"));
        CollectionAssert.AreEqual(new[] { 1, 2 }, options[0].TokenIndices.ToArray());
    }

    [TestMethod]
    public void Grammar_variants_preserve_atomic_multi_edit_sets_and_qualifications_and_reject_invalid_sets()
    {
        TextAnalysis input = Text("Малък ученика чете.", "Малък", "ученика", "чете", ".");
        input = input with { Tokens = input.Tokens.Select((token, index) => token.Kind == TokenKind.Word
            ? token with { Analysis = new(token.Text, token.Text,
                [Reading(token.Text, token.Text, index == 0 ? "Прилагателно:76,Мъжки" : "Съществително:1,Мъжки")], null) } : token).ToArray() };
        TextGrammarEdit first = new(0, input.Tokens[0].Start, input.Tokens[0].Length, "Малкият");
        TextGrammarEdit second = new(1, input.Tokens[1].Start, input.Tokens[1].Length, "ученик");
        input = input with { Grammar = new("grammar.v1",
            [new("article", "article_code", "NP", TextGrammarConstraint.ArticlePlacement, "Проверка за членуване.", "section", "https://ibl.bas.bg")],
            [new("article", 0, input.Tokens[1].Start + input.Tokens[1].Length, [1], [0], TextGrammarAssessment.Mismatch,
                [new(0, [new(0, 0)], [new(1, 0)])],
                [new([second, first]) { Qualifications = ["masculine_article_form_requires_role"] },
                 new([first, first]), new([]),
                 new([new(2, input.Tokens[2].Start, input.Tokens[2].Length, "четат")])], null)]) };

        SyntaxTreeSuggestion option = SyntaxTreeProjection.Build(input).Suggestions.Single();

        Assert.AreEqual("grammar", option.Source);
        Assert.AreEqual("article", option.RuleId);
        Assert.AreEqual(2, option.Edits.Count);
        Assert.AreEqual("Малкият", option.Edits[0].Replacement);
        Assert.AreEqual("ученик", option.Edits[1].Replacement);
        CollectionAssert.AreEqual(new[] { "masculine_article_form_requires_role" }, option.Qualifications.ToArray());
        Assert.IsTrue(option.Conditional);
    }

    [TestMethod]
    public void Selected_subject_is_paired_only_with_its_selected_predicate_and_validation_groups_share_spans()
    {
        TextAnalysis input = Text("Тя ще чете.", "Тя", "ще", "чете", ".");
        TextContextRoleSelection subject = new(TextContextRole.Subject, 0, 0, 2, [], []);
        TextContextRoleSelection predicate = new(TextContextRole.Predicate, 2, 6, 4, [], []);
        TextValidationDiagnostic spacing = new("spacing", 2, 1, "test", []);
        TextContextSentence sentence = new(0, input.Text.Length, true, TextContextSelectionStatus.Selected,
            2, null, [], [], [subject, predicate], []);
        input = input with
        {
            Context = new("context.v1", true, null, [sentence]),
            Validation = new("validation.v1", true, null, [spacing], [spacing], null, null, [])
        };
        var tree = SyntaxTreeProjection.Build(input);
        Assert.AreEqual("partial", tree.Status);
        Assert.AreEqual((2, 0, "selected_subject"), (tree.Edges.Single().From, tree.Edges.Single().To, tree.Edges.Single().Label));
        Assert.AreEqual("spacing", tree.Diagnostics.Single().Code);
        var ambiguous = input with { Context = input.Context with
            { Sentences = [sentence with { SelectedPredicateTokenIndex = null }] } };
        Assert.AreEqual(0, SyntaxTreeProjection.Build(ambiguous).Edges.Count);
    }

    [TestMethod]
    public void Unsupported_words_and_role_context_never_become_guessed_dependencies()
    {
        TextAnalysis analysis = Text("И то ще се превърне.", "И", "то", "ще", "се", "превърне", ".");
        analysis = analysis with
        {
            Syntax = new("syntax.v1", true, null,
            [new(0, analysis.Text.Length, "Calculated", "SourceScan", 4,
                [new(TextSyntaxRole.Predicate, "basic", 11, 8, [Exact(4)], []),
                 new(TextSyntaxRole.Subject, "subject", 2, 2, [Exact(1)], [Exact(4)])])], []),
            PredicateLinks = new("links.v1", true, null,
                [new(0, analysis.Text.Length, [], [], ["predicate_reading_uncovered:token=4:row=0"], false)])
        };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);

        Assert.AreEqual("partial", tree.Status);
        Assert.AreEqual(6, tree.Tokens.Count);
        Assert.AreEqual(0, tree.Edges.Count, "Role support context is not a governor/dependent contract.");
        Assert.AreEqual("unlinked", tree.Tokens[2].Status, "ще has no typed group membership here.");
        Assert.AreEqual("unlinked", tree.Tokens[3].Status, "се has no typed group membership here.");
        Assert.AreEqual("candidate", tree.Tokens[1].Roles.Single().Status);
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, tree.Sentences.Single().UnlinkedTokenIndices.ToArray());
        CollectionAssert.Contains(tree.Sentences[0].Reasons.ToArray(), "predicate_reading_uncovered:token=4:row=0");
    }

    [TestMethod]
    public void Bound_syntax_relations_preserve_both_predicates_and_each_actual_clitic_carrier()
    {
        TextAnalysis analysis = Text("🙂 Много ви моля, успокойте се и ми се доверете.",
            "🙂", "Много", "ви", "моля", ",", "успокойте", "се", "и", "ми", "се", "доверете", ".");
        TextSyntaxCandidate Candidate(TextSyntaxRole role, string rule, int target, int? carrier) =>
            new(role, rule, analysis.Tokens[target].Start, analysis.Tokens[target].Length,
                [new(target, "TypedFeatures", [new(target, 0)])], carrier is int index ? [Typed(index)] : []);
        TextSyntaxSupport Typed(int token) => new(token, "TypedFeatures", [new(token, 0)]);
        TextSyntaxCandidate[] candidates =
        [
            Candidate(TextSyntaxRole.Predicate, "BasicPredicate", 5, null),
            Candidate(TextSyntaxRole.Predicate, "CoordinatedPredicate", 10, 5) with
                { Context = [Typed(5), new(7, "ExactCategory", [new(7, 0)])] },
            Candidate(TextSyntaxRole.Object, "ImmediateClitic", 2, 3),
            Candidate(TextSyntaxRole.Object, "ImperativeObjectClitic", 8, 10),
            Candidate(TextSyntaxRole.ReflexiveClitic, "BoundReflexiveClitic", 6, 5),
            Candidate(TextSyntaxRole.ReflexiveClitic, "BoundReflexiveClitic", 9, 10)
        ];
        analysis = analysis with { Syntax = new("syntax.v2", true, null,
            [new(0, analysis.Text.Length, "Calculated", "SourceScan", 5, candidates)], []) };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);

        CollectionAssert.AreEqual(new[] { 5, 10 }, tree.Tokens.Where(token => token.Roles.Any(role =>
            role.Role == "Predicate")).Select(token => token.Index).ToArray());
        CollectionAssert.AreEqual(new[]
        {
            "10|5|coordinated_predicate", "7|10|coordinating_conjunction", "2|3|object_clitic", "8|10|object_clitic",
            "6|5|bound_reflexive_clitic", "9|10|bound_reflexive_clitic"
        }, tree.Edges.Select(edge => $"{edge.From}|{edge.To}|{edge.Label}").ToArray());
        Assert.IsTrue(tree.Edges.All(edge => edge.Source == "syntax" && edge.Status == "candidate" &&
            edge.Supports.Any(support => support.TokenIndex == edge.From) &&
            edge.Supports.Any(support => support.TokenIndex == edge.To)));
        CollectionAssert.AreEqual(new[] { 10, 5, 7 }, tree.Edges[0].Supports.Select(s => s.TokenIndex).ToArray());
        Assert.AreEqual(5, analysis.Syntax!.Sentences.Single().PredicateTokenIndex,
            "Projecting a secondary predicate must preserve the single main predicate index.");
        Assert.AreEqual("Calculated", analysis.Syntax.Sentences.Single().Status);
        Assert.AreEqual(36, tree.Tokens[9].Start, "The second се keeps its distinct original UTF-16 occurrence.");
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(tree, HttpContract.Json));
        JsonElement reflexive = wire.RootElement.GetProperty("tokens")[9].GetProperty("roles")[0];
        Assert.AreEqual("ReflexiveClitic", reflexive.GetProperty("role").GetString());
        Assert.AreEqual(10, reflexive.GetProperty("contextTokenIndices")[0].GetInt32());
        Assert.IsFalse(wire.RootElement.GetProperty("edges")[0].TryGetProperty("supports", out _));
        TextAnalysis carrierOnly = analysis with { Syntax = analysis.Syntax with { Sentences =
            [analysis.Syntax.Sentences.Single() with { Candidates = candidates.Select((candidate, index) => index == 1
                ? candidate with { Context = [Typed(5)] } : candidate).ToArray() }] } };
        SyntaxTreeResult carrierOnlyTree = SyntaxTreeProjection.Build(carrierOnly);
        Assert.AreEqual(1, carrierOnlyTree.Edges.Count(edge => edge.Label == "coordinated_predicate"));
        Assert.IsFalse(carrierOnlyTree.Edges.Any(edge => edge.Label == "coordinating_conjunction"));
    }

    [TestMethod]
    public void Unbound_surface_only_and_group_role_context_remain_evidence_without_directed_edges()
    {
        TextAnalysis analysis = Text("Тя ми се доверява.", "Тя", "ми", "се", "доверява", ".");
        TextSyntaxSupport typed = new(1, "TypedFeatures", [new(1, 0)]);
        TextSyntaxCandidate candidate = new(TextSyntaxRole.Object, "ImmediateClitic", 3, 2, [typed], [Exact(3)]);
        TextSyntaxCandidate[] unsupported =
        [
            candidate with { RuleId = "OtherClitic" },
            candidate with { Role = TextSyntaxRole.Subject },
            candidate with { Targets = [new(1, "SurfaceRule", [])] },
            candidate with { Targets = [typed with { Readings = [] }] },
            candidate with { Targets = [typed with { Basis = "ExactCategory" }] },
            candidate with { Targets = [typed, Exact(2)] },
            candidate with { Context = [Exact(0), Exact(3)] },
            candidate with { Role = TextSyntaxRole.ReflexiveClitic, RuleId = "ReflexiveClitic" },
            candidate with { Role = TextSyntaxRole.Predicate, RuleId = "CoordinatedPredicate", Context = [] },
            candidate with { Role = TextSyntaxRole.Predicate, RuleId = "CoordinatedPredicate", Context = [Exact(0), Exact(2), Exact(3)] },
            candidate with { Role = TextSyntaxRole.Predicate, RuleId = "CoordinatedPredicate", Context = [typed, new(2, "SurfaceRule", [])] }
        ];
        analysis = analysis with { Syntax = new("syntax.v2", true, null,
            [new(0, analysis.Text.Length, "Calculated", "SourceScan", 3, unsupported)], []) };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);

        Assert.AreEqual(0, tree.Edges.Count);
        Assert.AreEqual(unsupported.Length, tree.Tokens[1].Roles.Count,
            "Unsupported directed relations still preserve all original role candidates and their context.");
        Assert.IsTrue(tree.Tokens[1].Roles.Any(role => role.Context.Count > 1));
    }

    [TestMethod]
    public void Predicate_reading_tuples_and_marker_membership_remain_distinct_at_repeated_utf16_occurrences()
    {
        TextAnalysis analysis = Text("🙂 Аз съм се върнал. Аз съм се върнал.",
            "🙂", "Аз", "съм", "се", "върнал", ".", "Аз", "съм", "се", "върнал", ".");
        analysis = analysis with
        {
            Sentences = [new(0, 20), new(21, 17)],
            PredicateLinks = new("links.v1", true, null,
            [new(0, 20, [],
                [new("auxiliary_participle", Exact(2), Exact(4), [Exact(3)]),
                 new("auxiliary_participle", Exact(2, 1), Graph(4, 2, 3), [Exact(3, 1)])], [], false),
             new(21, 17, [], [new("auxiliary_participle", Exact(7), Exact(9), [Exact(8)])], [], false)])
        };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);

        SyntaxTreeEdge[] pairs = tree.Edges.Where(edge => edge.Label == "auxiliary_participle").ToArray();
        Assert.AreEqual(3, pairs.Length);
        Assert.AreEqual(2, pairs[0].From);
        Assert.AreEqual(4, pairs[0].To);
        Assert.AreNotEqual(pairs[0].Alternative, pairs[1].Alternative);
        Assert.AreEqual(1, pairs[1].Supports[0].Readings.Single().CompleteReadingIndex);
        Assert.AreEqual(new TextGraphReadingReference(4, 2, 3), pairs[1].Supports[1].GraphReadings!.Single());
        Assert.AreEqual(7, pairs[2].From);
        Assert.AreEqual(9, pairs[2].To);
        Assert.AreEqual("се", tree.Tokens[8].Text);
        Assert.AreEqual(28, tree.Tokens[8].Start);
        Assert.AreEqual(1, tree.Tokens[8].Sentence);
        SyntaxTreeEdge marker = tree.Edges.Single(edge => edge.To == 8);
        Assert.AreEqual("auxiliary_participle_marker", marker.Label);
        Assert.AreEqual(pairs[2].Alternative, marker.Alternative);
        Assert.IsFalse(tree.Tokens[8].Roles.Any(role => role.Role == nameof(TextSyntaxRole.Object)),
            "Membership in an auxiliary relation does not establish an Object role.");
        Assert.AreEqual(JsonSerializer.Serialize(tree, HttpContract.Json),
            JsonSerializer.Serialize(SyntaxTreeProjection.Build(analysis), HttpContract.Json));
        using JsonDocument wire = JsonDocument.Parse(JsonSerializer.Serialize(tree, HttpContract.Json));
        Assert.IsFalse(wire.RootElement.GetProperty("edges")[0].TryGetProperty("supports", out _),
            "Canonical reading evidence stays in the public API instead of repeating over the live connection.");
    }

    [TestMethod]
    public void Nominal_head_alternatives_preserve_joint_reading_support_and_do_not_select_a_winner()
    {
        TextAnalysis analysis = Text("Стара и порутена къща пустее.", "Стара", "и", "порутена", "къща", "пустее", ".");
        analysis = analysis with
        {
            NominalConstructions = new("nominal.v1", true, null,
            [new("NP", 0, 21, 3, [0, 2], [1], NominalAgreementAssessment.Compatible,
                [new(new(3, 0), null,
                    [new(0, NominalAgreementAssessment.Compatible, [new(0, 2)], []),
                     new(2, NominalAgreementAssessment.Compatible, [new(2, 3)], [])]),
                 new(new(3, 1), null,
                    [new(0, NominalAgreementAssessment.Compatible, [new(0, 4)], []),
                     new(2, NominalAgreementAssessment.Compatible, [new(2, 5)], [])])])
                { ConjunctionReadings = [new(1, 1)] }], [])
        };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);

        SyntaxTreeEdge[] old = tree.Edges.Where(edge => edge.To == 0).ToArray();
        Assert.AreEqual(2, old.Length);
        Assert.IsTrue(old.All(edge => edge.From == 3 && edge.Status == "compatible"));
        Assert.AreEqual(0, old[0].Supports[0].Readings.Single().CompleteReadingIndex);
        Assert.AreEqual(2, old[0].Supports[1].Readings.Single().CompleteReadingIndex);
        Assert.AreEqual(1, old[1].Supports[0].Readings.Single().CompleteReadingIndex);
        Assert.AreEqual(4, old[1].Supports[1].Readings.Single().CompleteReadingIndex);
        Assert.AreNotEqual(old[0].Alternative, old[1].Alternative);
        Assert.IsTrue(tree.Edges.Where(edge => edge.Label == "nominal_connector").All(edge => edge.To == 1));
        Assert.AreEqual("unlinked", tree.Tokens[4].Status, "No proximity link to the finite predicate is invented.");
    }

    [TestMethod]
    public void Grammar_conflicts_ambiguity_and_unassessed_scopes_are_separate_from_supported_alternatives()
    {
        TextAnalysis analysis = Text("Те е уморени.", "Те", "е", "уморени", ".");
        analysis = analysis with
        {
            Grammar = new("grammar.v1", [],
                [new("person.number", 0, 13, [0], [1], TextGrammarAssessment.Mismatch,
                    [new(1, [new(1, 0)], [new(0, 0)])], [], null),
                 new("copular", 0, 13, [0], [2], TextGrammarAssessment.Ambiguous, [], [], "competing_reading"),
                 new("unmeasured", 0, 13, [1], [2], TextGrammarAssessment.Unassessed, [], [], "features_unavailable")]),
            Diagnostics = [new("person_number", 3, 1, "person.number")]
        };

        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);

        CollectionAssert.AreEqual(new[] { "conflict", "ambiguous", "unassessed" },
            tree.Edges.Select(edge => edge.Status).ToArray());
        Assert.IsTrue(tree.Edges.All(edge => edge.Label == "grammar_constraint"));
        Assert.AreEqual("person.number", tree.Edges[0].RuleId);
        Assert.AreEqual(3, tree.Diagnostics.Single().Start);
        Assert.AreEqual(1, tree.Diagnostics.Single().Length);
        Assert.AreEqual("person_number", tree.Diagnostics.Single().Code);
        Assert.AreEqual("partial", tree.Status);
    }

    [TestMethod]
    public void Absent_evidence_is_unavailable_and_misaligned_tokens_fail_instead_of_reanchoring_repeated_text()
    {
        TextAnalysis analysis = Text("дума дума", "дума", "дума");
        SyntaxTreeResult tree = SyntaxTreeProjection.Build(analysis);
        Assert.AreEqual("unavailable", tree.Status);
        Assert.AreEqual(5, tree.Tokens[1].Start);
        Assert.IsTrue(tree.Tokens.All(token => token.Status == "unlinked"));
        analysis = analysis with { Tokens = [analysis.Tokens[0], analysis.Tokens[1] with { Start = 4 }] };
        Assert.Throws<InvalidOperationException>(() => SyntaxTreeProjection.Build(analysis));
    }

    private static TextSyntaxSupport Exact(int token, int reading = 0) =>
        new(token, "original_complete_reading", [new(token, reading)]);

    private static TextSyntaxSupport Graph(int token, int candidate, int tag) =>
        new(token, "licensed_exact_root_graph_cell", []) { GraphReadings = [new(token, candidate, tag)] };

    private static WordReading Reading(string form, string lemma, string tag) =>
        new(form, lemma, tag, 1, null, null, null, "projection-test");

    private static TextReadingSnapshot Snapshot(WordReading reading) => new(reading, reading.Features);

    private static TextAnalysis Text(string text, params string[] parts)
    {
        List<TokenAnalysis> tokens = [];
        int offset = 0;
        foreach (string part in parts)
        {
            int start = text.IndexOf(part, offset, StringComparison.Ordinal);
            Assert.IsTrue(start >= offset);
            tokens.Add(new(part, part is "." or "," ? TokenKind.Punctuation : part == "🙂" ? TokenKind.Symbol :
                TokenKind.Word, start, part.Length, null));
            offset = start + part.Length;
        }
        return new(text, tokens) { Sentences = [new(0, text.Length)] };
    }
}
