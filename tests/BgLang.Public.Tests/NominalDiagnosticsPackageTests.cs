using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class NominalDiagnosticsPackageTests
{
    private const string Attribute = "productive_attribute_agreement_mismatch";
    private const string Placement = "nominal_article_on_noninitial_member";
    private const string Predicate = "productive_morphology_agreement_mismatch";

    [TestMethod]
    public void Fronted_objects_and_implicit_person_subjects_do_not_prove_nominal_agreement_errors()
    {
        using var session = GraphStressAcceptance.Open();
        foreach (string text in new[]
        {
            "Малката книга четем.", "Книгата четат.", "Книгите четеш.",
            "Цялата нощ пътувам.", "Последната седмица пътуваме.", "Един час пътуваме.",
            "Ти чете.", "Малкият автобус пристига."
        })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Predicate), text);

        foreach ((string text, string target) in new[]
        {
            ("Децата играе.", "играе"), ("Котката спят.", "спят"),
            ("Малкият автобус пристигаме.", "пристигаме")
        })
        {
            var warning = session.AnalyzeText(text).Diagnostics.Single(d => d.Code == Predicate);
            Assert.AreEqual(target, text.Substring(warning.Start, warning.Length));
            Assert.IsNull(warning.ProjectedText);
        }
    }

    [TestMethod]
    public void Explicit_nominative_subject_agrees_across_one_typed_accusative_clitic()
    {
        using var session = GraphStressAcceptance.Open();
        foreach ((string text, string target) in new[]
        {
            ("Тя ги видяхте.", "видяхте"), ("Тя го видяхте.", "видяхте"),
            ("Тя ме видяхте.", "видяхте"), ("Аз ги поведохте.", "поведохте"),
            ("Тя ги видяхте на спирката.", "видяхте"), ("Аз ги поведохте натам", "поведохте")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text);
            TextDiagnostic warning = analysis.Diagnostics.Single(d => d.Code == Predicate);
            Assert.AreEqual(target, text.Substring(warning.Start, warning.Length), text);
            Assert.IsNotEmpty(warning.Readings!, text);
            Assert.IsTrue(analysis.Context!.Sentences.Single().Candidates.Any(c =>
                !c.Selectable && c.EvidenceCodes.Any(code => code.StartsWith("short_accusative_clitic=", StringComparison.Ordinal))), text);
        }
        foreach (string text in new[]
        {
            "Тя ги видя.", "Аз ги поведох.", "Тя я видяхте.", "Тя, ги видяхте.",
            "Тя ги видя на спирката.", "Аз ги поведох натам", "Тя ги 📚 видяхте натам",
            "Хорото го гледаха.", "Сутринта го гледаха.", "Тя ги пристигнахте."
        })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Predicate), text);
    }

    private const string ClauseRoles = "local_finite_clause_roles.v1";

    [TestMethod]
    public void Exact_clause_roles_anchor_agreement_across_governed_material()
    {
        using var session = GraphStressAcceptance.Open();
        foreach ((string text, string target, string basis) in new[]
        {
            ("С мен аз пътуваме.", "пътуваме", "canonical_subject_after_governed_prefix"),
            ("В парка тя ги видяхте.", "видяхте", "canonical_subject_after_governed_prefix")
        })
        {
            var analysis = session.AnalyzeText(text);
            var warning = analysis.Diagnostics.SingleOrDefault(d => d.Code == Predicate && d.RuleId == ClauseRoles);
            Assert.IsNotNull(warning, text);
            Assert.AreEqual(target, text.Substring(warning.Start, warning.Length), text);
            Assert.IsNull(warning.ProjectedText, text);
            Assert.IsNull(warning.GraphReadings, text);
            var candidate = analysis.Context!.Sentences.Single().Candidates.Single(c => c.AuthorityLane == ClauseRoles);
            Assert.IsFalse(candidate.Selectable, text);
            CollectionAssert.Contains(candidate.EvidenceCodes.ToArray(), basis, text);
            var relation = candidate.Roles.SelectMany(role => role.Readings).Distinct()
                .OrderBy(reading => reading.TokenIndex).ThenBy(reading => reading.CompleteReadingIndex).ToArray();
            CollectionAssert.AreEqual(relation, warning.Readings!.ToArray(), text);
        }

        foreach (string text in new[]
        {
            "С мен те пътуват.", "В парка тя ги видя.", "Малкият автобус пристигна с тях.", "Иван пътува с теб.", "Иван пътуваме с теб.",
            "„Иван пътуваме с теб.“", "Иван, пътуваме с теб.", "Тя ги 📚 видяхте натам", "Тя ги пристигнахте.",
            "Малката книга пътува с мен.", "Малката книга четем.", "На стария учителката помогна.",
            "Най-добрият отбор станахме.", "Петров станах след брака."
        })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Predicate), text);

        foreach ((string text, string target) in new[]
        {
            ("Малкият автобус пристигаме с тях.", "пристигаме"),
            ("Малката книга пътувам с мен.", "пътувам"),
            ("Малката книга пътувам с мен", "пътувам"),
            ("Малката книга пътуваме с мен.", "пътуваме")
        })
        {
            var analysis = session.AnalyzeText(text);
            Assert.IsFalse(analysis.Diagnostics.Any(d => d.RuleId == ClauseRoles), text);
            var fusionWarning = analysis.Diagnostics.Single(d => d.Code == Predicate);
            Assert.AreEqual("clause_fusion_agreement.v1", fusionWarning.RuleId, text);
            Assert.AreEqual(target, text.Substring(fusionWarning.Start, fusionWarning.Length), text);
            Assert.IsNull(fusionWarning.ProjectedText, text);
            Assert.IsNull(fusionWarning.GraphReadings, text);
            Assert.IsNotEmpty(fusionWarning.Readings!, text);
        }
    }

    [TestMethod]
    public void Clause_role_proof_stays_outside_multi_sentence_quotes()
    {
        using var session = GraphStressAcceptance.Open();
        var analysis = session.AnalyzeText("„Начало. В парка тя ги видяхте.“");
        Assert.IsTrue(analysis.Sentences.Count > 1);
        Assert.IsFalse(analysis.Context!.Sentences.SelectMany(sentence => sentence.Candidates)
            .Any(candidate => candidate.AuthorityLane == ClauseRoles));
        Assert.IsFalse(analysis.Diagnostics.Any(d => d.RuleId == ClauseRoles));
    }

    [TestMethod]
    public void Exact_person_and_place_homographs_do_not_license_agreement()
    {
        using var session = GraphStressAcceptance.Open();
        var analysis = session.AnalyzeText("Иван пътуваме с теб.");
        var name = analysis.ProperNames!.Candidates.Single(c => c.TokenIndex == 0);
        Assert.AreEqual(ProperNameKinds.Person | ProperNameKinds.Place, name.Kinds);
        Assert.HasCount(2, name.ExactSupports);
        Assert.IsFalse(analysis.Diagnostics.Any(d => d.Code == Predicate));
    }

    [TestMethod]
    public void Clause_alternatives_retain_original_support_without_categorical_agreement()
    {
        using var session = GraphStressAcceptance.Open();
        var r3ProDrop = session.AnalyzeText("Малката книга четем.");
        Assert.IsFalse(r3ProDrop.Diagnostics.Any(d => d.RuleId == ClauseRoles || d.Code == Predicate));
        Assert.IsFalse(r3ProDrop.Context!.Sentences.Single().Candidates.Any(c => c.AuthorityLane == ClauseRoles));
        Assert.AreEqual(TextContextSelectionStatus.Selected, r3ProDrop.Context.Sentences.Single().Status);
        Assert.HasCount(1, r3ProDrop.Context.Sentences.Single().SelectedRoles);
        Assert.AreEqual(TextContextRole.Predicate, r3ProDrop.Context.Sentences.Single().SelectedRoles[0].Role);

        foreach ((string text, string basis, string? expectedFusionTarget) in new[]
        {
            ("Хорото го гледаха.", "resumed_nominal_object_candidate", (string?)null),
            ("В парка играем.", "locally_governed_nominal", null),
            ("Малката книга 📚 пътуваме с мен.", "subject_agreement_if_subject", "пътуваме"),
            ("Малката книга + пътуваме с мен.", "subject_agreement_if_subject", null),
            ("Малката книга пътуваме с мен.", "subject_agreement_if_subject", "пътуваме"),
            ("Малкият автобус пристигаме с тях.", "full_article_subject_or_predicative", "пристигаме"),
            ("Последната седмица пътуваме.", "subject_agreement_if_subject", null)
        })
        {
            var analysis = session.AnalyzeText(text);
            Assert.IsFalse(analysis.Diagnostics.Any(d => d.RuleId == ClauseRoles), text);
            if (expectedFusionTarget is null)
            {
                Assert.IsFalse(analysis.Diagnostics.Any(d => d.Code == Predicate), text);
            }
            else
            {
                var fusionWarning = analysis.Diagnostics.Single(d => d.Code == Predicate);
                Assert.AreEqual("clause_fusion_agreement.v1", fusionWarning.RuleId, text);
                Assert.AreEqual(expectedFusionTarget, text.Substring(fusionWarning.Start, fusionWarning.Length), text);
                Assert.IsNotEmpty(fusionWarning.Readings!, text);
            }
            var candidate = analysis.Context!.Sentences.Single().Candidates.SingleOrDefault(c => c.AuthorityLane == ClauseRoles);
            Assert.IsNotNull(candidate, text);
            Assert.IsFalse(candidate.Selectable, text);
            CollectionAssert.Contains(candidate.EvidenceCodes.ToArray(), basis, text);
            Assert.IsTrue(candidate.Roles.Any(role => role.Role == TextContextRole.NominalContext && role.Readings.Count > 0), text);
            Assert.IsFalse(candidate.Roles.Any(role => role.Role == TextContextRole.Subject), text);
        }
    }

    [TestMethod]
    public void Grammar_catalog_lists_same_rule_and_all_verified_single_word_repairs()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        Assert.IsNull(session.AnalyzeText("Малките книга.").Grammar);
        var empty = session.AnalyzeText("", false, options, default).Grammar!;
        CollectionAssert.AreEqual(new[]
        {
            "local_exact_attribute_agreement.v1",
            "grammar.article.first_prepositional_nominal_member.v1",
            "grammar.article.first_initial_nominal_member.v1",
            "grammar.short_article.exact_prepositional_group.v1",
            "grammar.agreement.copular_finite.v1",
            "grammar.agreement.copular_adjective.v1",
            "grammar.agreement.passive_participle.v1",
            "grammar.agreement.analytic_finite.v1",
            "grammar.agreement.active_participle_controller.v1",
            "grammar.agreement.auxiliary_active_participle.v1",
            "clause_fusion_agreement.v1",
            "grammar.full_article.clause_subject.v1"
        }, empty.Rules.Select(rule => rule.Id).ToArray());
        Assert.IsEmpty(empty.Matches);

        const string agreement = "local_exact_attribute_agreement.v1";
        foreach ((string text, string adjective) in new[]
        {
            ("Малките книга.", "Малката"), ("Малките книга", "Малката"),
            ("Малките книга\n", "Малката"), ("🙂 Видях малките книга.", "малката")
        })
        {
            var analysis = session.AnalyzeText(text, false, options, default);
            var match = analysis.Grammar!.Matches.Single(row => row.RuleId == agreement);
            Assert.AreEqual(TextGrammarAssessment.Mismatch, match.Assessment, text);
            Assert.AreEqual(agreement, analysis.Diagnostics.Single(d => d.Code == Attribute).RuleId, text);
            Assert.IsNull(analysis.Diagnostics.Single(d => d.Code == Attribute).ProjectedText, text);
            var conflict = match.Conflicts.Single();
            Assert.AreEqual(analysis.Tokens[conflict.TokenIndex].Start, analysis.Diagnostics.Single(d => d.Code == Attribute).Start, text);
            Assert.IsTrue(conflict.Actual.All(row => row.TokenIndex == conflict.TokenIndex), text);
            Assert.IsTrue(conflict.ExpectedFrom.All(row => row.TokenIndex == match.ControllerTokenIndices.Single()), text);
            var edits = match.Repairs.Select(repair => repair.Edits.Single()).ToArray();
            CollectionAssert.AreEquivalent(new[] { adjective, "книги" }, edits.Select(edit => edit.Replacement).ToArray(), text);
            foreach (var edit in edits)
            {
                var original = analysis.Tokens[edit.TokenIndex];
                Assert.AreEqual((original.Start, original.Length), (edit.Start, edit.Length), text);
                string repaired = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
                var result = session.AnalyzeText(repaired, false, options, default);
                Assert.IsFalse(result.Diagnostics.Any(d => d.Code == Attribute), repaired);
                Assert.AreEqual(TextGrammarAssessment.Compatible,
                    result.Grammar!.Matches.Single(row => row.RuleId == agreement).Assessment, repaired);
            }
        }

        foreach ((string text, TextGrammarAssessment expected, string[] repairs) in new[]
        {
            ("Малката книга.", TextGrammarAssessment.Compatible, Array.Empty<string>()),
            ("Малките книги.", TextGrammarAssessment.Compatible, Array.Empty<string>()),
            ("Малките хубава книга.", TextGrammarAssessment.Mismatch, new[] { "Малката" }),
            ("Малките стара книга.", TextGrammarAssessment.Ambiguous, Array.Empty<string>()),
            ("Говоря с малката автобус.", TextGrammarAssessment.Ambiguous, Array.Empty<string>()),
            ("Седя до малката автобус.", TextGrammarAssessment.Ambiguous, Array.Empty<string>()),
            ("синята и бялата кола.", TextGrammarAssessment.Ambiguous, Array.Empty<string>()),
            ("Малките книга и тетрадка.", TextGrammarAssessment.Ambiguous, Array.Empty<string>()),
            ("Малките книгата.", TextGrammarAssessment.Mismatch, Array.Empty<string>())
        })
        {
            var match = session.AnalyzeText(text, false, options, default).Grammar!.Matches.Single(row => row.RuleId == agreement);
            Assert.AreEqual(expected, match.Assessment, text);
            CollectionAssert.AreEquivalent(repairs, match.Repairs.SelectMany(row => row.Edits).Select(edit => edit.Replacement).ToArray(), text);
        }
    }

    [TestMethod]
    public void Grammar_repairs_preserve_definiteness_across_gender_and_number()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        foreach ((string text, string[] alternatives, bool qualified) in new[]
        {
            ("Малкият книга.", new[] { "Малката" }, false),
            ("Малката автобус.", new[] { "Малкият", "Малкия" }, true),
            ("До малката автобус.", new[] { "малкия" }, false)
        })
        {
            var match = session.AnalyzeText(text, false, options, default).Grammar!.Matches
                .Single(row => row.RuleId == "local_exact_attribute_agreement.v1");
            CollectionAssert.AreEquivalent(alternatives,
                match.Repairs.Select(repair => repair.Edits.Single().Replacement).ToArray(), text);
            Assert.IsTrue(match.Repairs.All(repair =>
                repair.Qualifications.Contains("masculine_article_form_requires_role") == qualified), text);
        }
    }

    [TestMethod]
    public void Grammar_catalog_reports_exact_article_rules_and_available_repairs()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        foreach ((string text, string rule, string target) in new[]
        {
            ("Чорбанова четат на червена пейката", "grammar.article.first_prepositional_nominal_member.v1", "пейката"),
            ("Високата оградата падна.", "grammar.article.first_initial_nominal_member.v1", "оградата")
        })
        {
            var analysis = session.AnalyzeText(text, false, options, default);
            var match = analysis.Grammar!.Matches.Single(row => row.RuleId == rule);
            Assert.IsTrue(analysis.Grammar.Rules.Any(row => row.Id == rule && row.SourceSection.Length > 0));
            Assert.AreEqual(TextGrammarAssessment.Mismatch, match.Assessment);
            Assert.AreEqual(target, analysis.Tokens[match.Conflicts.Single().TokenIndex].Text);
            Assert.IsTrue(analysis.Diagnostics.Any(d => d.RuleId == match.RuleId));
            Assert.IsEmpty(match.Repairs);
            Assert.AreEqual("multi_token_article_repair_not_measured", match.Reason);
        }
        const string shortRule = "grammar.short_article.exact_prepositional_group.v1";
        var wrong = session.AnalyzeText("Говоря с добрият учител.", false, options, default);
        var shortForm = wrong.Grammar!.Matches.Single(row => row.RuleId == shortRule);
        Assert.AreEqual(TextGrammarAssessment.Mismatch, shortForm.Assessment);
        Assert.AreEqual("добрия", shortForm.Repairs.Single().Edits.Single().Replacement);
        var right = session.AnalyzeText("Говоря с добрия учител.", false, options, default);
        Assert.AreEqual(TextGrammarAssessment.Compatible,
            right.Grammar!.Matches.Single(row => row.RuleId == shortRule).Assessment);
        Assert.IsFalse(session.AnalyzeText("синята и бялата кола.", false, options, default).Grammar!.Matches.Any(row =>
            row.RuleId.StartsWith("grammar.article.", StringComparison.Ordinal) && row.Assessment == TextGrammarAssessment.Mismatch));
    }

    [TestMethod]
    public void Copular_catalog_binds_both_constraints_and_verifies_the_same_local_repair()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        const string finite = "grammar.agreement.copular_finite.v1";
        const string adjective = "grammar.agreement.copular_adjective.v1";
        foreach ((string text, string[] violations, string[] repairs) in new[]
        {
            ("Стаята е хубав.", new[] { adjective }, new[] { "хубава" }),
            ("Стаята е хубав", new[] { adjective }, new[] { "хубава" }),
            ("Стаята са хубава.", new[] { finite }, new[] { "е" }),
            ("Стаята са хубави.", new[] { finite, adjective }, Array.Empty<string>()),
            ("Книгата са хубави.", new[] { finite, adjective }, new[] { "Книгите" }),
            ("Стаята са хубав.", new[] { finite, adjective }, Array.Empty<string>()),
            ("Ние сме хубав.", new[] { adjective }, new[] { "хубави" })
        })
        {
            var analysis = session.AnalyzeText(text, false, options, default);
            var matches = analysis.Grammar!.Matches.Where(match => match.RuleId is finite or adjective).ToArray();
            Assert.HasCount(2, matches, text);
            var failures = matches.Where(match => match.Assessment == TextGrammarAssessment.Mismatch).ToArray();
            CollectionAssert.AreEquivalent(violations, failures.Select(match => match.RuleId).ToArray(), text);
            CollectionAssert.AreEquivalent(violations, analysis.Diagnostics.Where(d => d.RuleId is finite or adjective)
                .Select(d => d.RuleId).ToArray(), text);
            var edits = failures.SelectMany(match => match.Repairs).Select(repair => repair.Edits.Single()).Distinct().ToArray();
            CollectionAssert.AreEquivalent(repairs, edits.Select(edit => edit.Replacement).ToArray(), text);
            foreach (var edit in edits)
            {
                var token = analysis.Tokens[edit.TokenIndex];
                Assert.AreEqual((token.Start, token.Length), (edit.Start, edit.Length), text);
                string changed = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
                var revised = session.AnalyzeText(changed, false, options, default);
                var revisedMatches = revised.Grammar!.Matches.Where(match => match.RuleId is finite or adjective).ToArray();
                Assert.HasCount(2, revisedMatches, changed);
                Assert.IsTrue(revisedMatches.All(match => match.Assessment == TextGrammarAssessment.Compatible), changed);
            }
        }

        foreach (string text in new[]
        {
            "Стаята е хубава.", "Ние сме хубави.", "Той е учител.", "Стаята е на горния етаж.",
            "Стаята е голяма и светла.", "Стаята беше почистена.", "„Думи. Стаята е хубав.“"
        })
        {
            var analysis = session.AnalyzeText(text, false, options, default);
            Assert.IsFalse(analysis.Diagnostics.Any(d => d.RuleId is finite or adjective), text);
        }
        var person = session.AnalyzeText("Аз съм хубави.", false, options, default);
        var issue = person.Grammar!.Matches.Single(match => match.RuleId == adjective);
        Assert.AreEqual(TextGrammarAssessment.Mismatch, issue.Assessment);
        Assert.IsTrue(issue.Repairs.Any(repair => repair.Edits.Single().Replacement == "хубава" &&
            repair.Qualifications.Contains("controller_gender_unmeasured")));
    }

    [TestMethod]
    public void Analytic_groups_bind_before_agreement_and_warn_in_default_analysis()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        const string auxiliary = "auxiliary_participle_agreement_mismatch";
        const string subject = "productive_predicative_agreement_mismatch";
        foreach ((string text, string target, string[] codes, string? repair) in new[]
        {
            ("Винаги сме искала да изсвиря тази сонатина, харесвам я от дете.", "искала", new[] { auxiliary }, "съм"),
            ("Винаги съм искали да изсвиря тази сонатина, харесвам я от дете.", "искали", new[] { auxiliary }, "искала"),
            ("Той се е отървала от тез гризачи.", "отървала", new[] { subject }, (string?)null),
            ("Той се е отървали от тез гризачи.", "отървали", new[] { auxiliary, subject }, (string?)null),
            ("Той се е върнала вчера.", "върнала", new[] { subject }, "върнал"),
            ("Ние сме работила спокойно.", "работила", new[] { auxiliary, subject }, "работили"),
            ("Тя е поканен на срещата.", "поканен", new[] { subject }, "поканена"),
            ("Писали съм писмото.", "Писали", new[] { auxiliary }, "сме"),
            ("Той го е написала.", "написала", new[] { subject }, "написал"),
            ("Тя е писал, той работи.", "писал", new[] { subject }, "писала")
        })
        {
            var plain = session.AnalyzeText(text);
            var detailed = session.AnalyzeText(text, false, options, default);
            Assert.IsNull(plain.Grammar, text);
            var warnings = Warnings(plain);
            CollectionAssert.AreEquivalent(codes, warnings.Select(d => d.Code).ToArray(), text);
            Assert.IsTrue(warnings.All(d => text.Substring(d.Start, d.Length) == target && d.Readings is { Count: > 0 }), text);
            CollectionAssert.AreEqual(warnings.Select(d => (d.Code, d.RuleId, d.Start, d.Length)).ToArray(),
                Warnings(detailed).Select(d => (d.Code, d.RuleId, d.Start, d.Length)).ToArray(), text);
            var suggestions = detailed.Grammar!.Matches.SelectMany(match => match.Repairs).ToArray();
            if (repair is null)
            {
                Assert.IsEmpty(suggestions, text);
                continue;
            }
            var proposal = suggestions.First(item => item.Edits.Single().Replacement == repair);
            AssertPredicateRepairs(session, detailed, options);
            if (text.StartsWith("Писали", StringComparison.Ordinal))
                Assert.IsTrue(proposal.Qualifications.Contains("clause_relation_unverified"), text);
        }

        foreach (string text in new[] { "Той се е отървал от тез гризачи.", "Тя е поканена на срещата.",
            "Тя е поканен лекар.", "Тя е добра учителка.", "Цяла нощ беше студено.", "Думи: „Той е писала.“" })
            Assert.IsEmpty(Warnings(session.AnalyzeText(text)), text);
        var compatible = session.AnalyzeText("Ние сме работили спокойно.").PredicateLinks!;
        var incompatible = session.AnalyzeText("Ние сме работила спокойно.").PredicateLinks!;
        Assert.IsTrue(compatible.Sentences.SelectMany(s => s.Links).Any(link => link.Kind == "auxiliary_participle"));
        Assert.IsFalse(incompatible.Sentences.SelectMany(s => s.Links).Any(link => link.Kind == "auxiliary_participle"));

        static TextDiagnostic[] Warnings(TextAnalysis analysis) =>
            analysis.Diagnostics.Where(d => d.Code is auxiliary or subject).ToArray();
    }

    [TestMethod]
    public void Analytic_chain_measures_active_and_passive_forms_with_different_polite_policies()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        const string auxiliary = "grammar.agreement.auxiliary_active_participle.v1";
        const string passive = "grammar.agreement.passive_participle.v1";
        foreach ((string text, TextGrammarAssessment active) in new[]
        {
            ("Вие сте били поканена.", TextGrammarAssessment.Compatible),
            ("Вие сте бил поканена.", TextGrammarAssessment.Mismatch)
        })
        {
            var analyzed = session.AnalyzeText(text, false, options, default);
            var activeMatch = analyzed.Grammar!.Matches.Single(m => m.RuleId == auxiliary);
            Assert.AreEqual(active, activeMatch.Assessment, text);
            var passiveMatch = analyzed.Grammar.Matches.Single(m => m.RuleId == passive);
            Assert.AreEqual("polite_singular_address_possible", passiveMatch.Reason, text);
            Assert.IsEmpty(passiveMatch.Conflicts, text);
            Assert.IsFalse(analyzed.Diagnostics.Any(d => d.RuleId == passive), text);
        }
        var direct = session.AnalyzeText("Вие сте дошла, госпожо.", false, options, default);
        Assert.IsTrue(direct.Diagnostics.Any(d => d.RuleId == auxiliary && direct.Text.Substring(d.Start, d.Length) == "дошла"));
        var proposal = direct.Grammar!.Matches.Single(match => match.RuleId == auxiliary).Repairs.Single();
        Assert.AreEqual("дошли", proposal.Edits.Single().Replacement);
        AssertPredicateRepairs(session, direct, options);
    }

    [TestMethod]
    public void Predicative_scope_binds_exact_clause_and_right_frontier_before_agreement()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        const string subject = "productive_predicative_agreement_mismatch";
        foreach (string text in new[] { "Тя е добър, опитен лекар.", "Тя е добър за възрастта си специалист.",
            "Тя е преживял много човек.", "Тя е минал през много изпитания човек.", "Тя е поканен на срещата лекар.",
            "Тя каза, че той е готов.", "Тя каза, че той е писал.", "Тя е много добър човек.", "Цяла нощ беше буден." })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == subject), text);
        foreach ((string text, string target, string replacement) in new[]
        {
            ("Тя каза, че той е готова.", "готова", "готов"),
            ("Тя каза, че той е писала.", "писала", "писал"),
            ("Той е много хубава.", "хубава", "хубав"),
            ("Тя е поканен на срещата.", "поканен", "поканена"),
            ("Тя е вече поканен на срещата.", "поканен", "поканена"),
            ("Тя е поканен от млад лекар.", "поканен", "поканена")
        })
        {
            var plain = session.AnalyzeText(text);
            var diagnostic = plain.Diagnostics.Single(d => d.Code == subject);
            Assert.AreEqual(target, text.Substring(diagnostic.Start, diagnostic.Length), text);
            var detailed = session.AnalyzeText(text, false, options, default);
            var match = detailed.Grammar!.Matches.Single(row => row.RuleId == diagnostic.RuleId);
            Assert.IsTrue(match.Repairs.SelectMany(repair => repair.Edits).Any(row => row.Replacement == replacement), text);
            AssertPredicateRepairs(session, detailed, options);
        }
        foreach ((string text, string rule, string target) in new[]
        {
            ("Тя е поканен от младата лекар.", "passive_participle", "поканен"),
            ("Тя е поканен от младата лекарка.", "passive_participle", "поканен"),
            ("Тя е вече поканен от младата лекар.", "passive_participle", "поканен"),
            ("Тя е била поканен от младата лекар.", "passive_participle", "поканен"),
            ("Тя е преживял от малката човек.", "active_participle_controller", "преживял"),
            ("Тя е била преживял от малката човек.", "active_participle_controller", "преживял")
        })
        {
            Assert.IsEmpty(session.AnalyzeText(text).Diagnostics, text);
            var detailed = session.AnalyzeText(text, false, options, default);
            Assert.IsEmpty(detailed.Diagnostics, text);
            var ruleId = $"grammar.agreement.{rule}.v1";
            var match = detailed.Grammar!.Matches.Single(row => row.RuleId == ruleId &&
                detailed.Tokens[row.DependentTokenIndices.Single()].Text == target);
            Assert.AreEqual(TextGrammarAssessment.Ambiguous, match.Assessment, text);
            Assert.HasCount(1, match.Conflicts, text);
            var targetToken = detailed.Tokens[match.Conflicts.Single().TokenIndex];
            Assert.AreEqual((text.IndexOf(target, StringComparison.Ordinal), target.Length),
                (targetToken.Start, targetToken.Length), text);
            var pp = detailed.Grammar.Matches.Single(row => row.RuleId == "local_exact_attribute_agreement.v1");
            int ppStart = text.IndexOf("от ", StringComparison.Ordinal) + "от ".Length;
            Assert.AreEqual((ppStart, text.Length - ppStart - 1), (pp.Start, pp.Length), text);
            Assert.AreEqual(TextGrammarAssessment.Ambiguous, pp.Assessment, text);
            Assert.AreEqual("competing_nominal_construction", pp.Reason, text);
            Assert.IsTrue(detailed.Grammar.Matches.All(row => row.Repairs.Count == 0), text);
        }
        var coordinated = session.AnalyzeText("Едва бях проговорил, и вече с леките стъпки нагазих в невъзможното.",
            false, options, default);
        var firstClause = coordinated.Grammar!.Matches.Single(row => row.RuleId == "grammar.agreement.auxiliary_active_participle.v1");
        Assert.AreEqual(TextGrammarAssessment.Compatible, firstClause.Assessment);
        Assert.AreEqual("проговорил", coordinated.Tokens[firstClause.DependentTokenIndices.Single()].Text);

        var nominal = session.AnalyzeText("Малката книга е много хубав.", false, options, default);
        var uncertain = nominal.Grammar!.Matches.Single(m => m.RuleId == "grammar.agreement.copular_adjective.v1");
        Assert.AreEqual(TextGrammarAssessment.Ambiguous, uncertain.Assessment);
        Assert.IsEmpty(uncertain.Repairs);
    }

    [TestMethod]
    public void Exact_da_reflexive_and_address_witnesses_bind_their_local_roles()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        const string subject = "productive_predicative_agreement_mismatch";
        foreach ((string text, string target, string repair) in new[]
        {
            ("Аз искам тя да бъде спокоен.", "спокоен", "спокойна"),
            ("Тя си е взел тетрадката.", "взел", "взела")
        })
        {
            var plain = session.AnalyzeText(text);
            var warning = plain.Diagnostics.Single(d => d.Code == subject);
            Assert.AreEqual(target, text.Substring(warning.Start, warning.Length), text);
            var detailed = session.AnalyzeText(text, false, options, default);
            var proposal = detailed.Grammar!.Matches.Where(match => match.RuleId == warning.RuleId)
                .SelectMany(match => match.Repairs).Single(row => row.Edits.Single().Replacement == repair);
            Assert.IsEmpty(proposal.Qualifications, text);
            AssertPredicateRepairs(session, detailed, options);
        }
        foreach (string text in new[] { "Аз искам тя да бъде спокойна.", "Тя си е взела тетрадката." })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == subject), text);

        foreach ((string text, string rule, bool conflict) in new[]
        {
            ("Госпожо, Вие сте спокойни.", "copular_adjective", true),
            ("Госпожо, Вие сте спокойна.", "copular_adjective", false),
            ("Вие сте спокойни, госпожо.", "copular_adjective", true),
            ("Госпожо, Вие сте поканени.", "passive_participle", true),
            ("Госпожо, Вие сте поканена.", "passive_participle", false)
        })
        {
            var analyzed = session.AnalyzeText(text, false, options, default);
            var ruleId = $"grammar.agreement.{rule}.v1";
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.RuleId == ruleId), text);
            Assert.IsFalse(analyzed.Diagnostics.Any(d => d.RuleId == ruleId), text);
            var match = analyzed.Grammar!.Matches.Single(row => row.RuleId == ruleId);
            Assert.AreEqual(TextGrammarAssessment.Ambiguous, match.Assessment, text);
            Assert.AreEqual("address_number_gender_unresolved", match.Reason, text);
            Assert.AreEqual(conflict, match.Conflicts.Count > 0, text);
            Assert.IsEmpty(match.Repairs, text);
            CollectionAssert.AreEqual(new[] { "вие", "госпожо" },
                match.ControllerTokenIndices.Select(index => analyzed.Tokens[index].Text.ToLowerInvariant()).ToArray(), text);
        }
    }

    private static void AssertPredicateRepairs(BulgarianLanguageSession session, TextAnalysis source, TextContextOptions options)
    {
        var original = source.Grammar!.Matches.Where(match => match.RuleId.StartsWith("grammar.agreement.", StringComparison.Ordinal)).ToArray();
        foreach (var edit in original.SelectMany(match => match.Repairs).Select(repair => repair.Edits.Single()).Distinct())
        {
            var token = source.Tokens[edit.TokenIndex];
            Assert.AreEqual((token.Start, token.Length), (edit.Start, edit.Length), source.Text);
            string revised = source.Text[..edit.Start] + edit.Replacement + source.Text[(edit.Start + edit.Length)..];
            var after = session.AnalyzeText(revised, false, options, default).Grammar!.Matches;
            foreach (var match in original.Where(match => match.Start <= edit.Start &&
                edit.Start + edit.Length <= match.Start + match.Length &&
                match.Assessment is TextGrammarAssessment.Compatible or TextGrammarAssessment.Mismatch))
            {
                var retained = after.Single(candidate => candidate.RuleId == match.RuleId &&
                    candidate.ControllerTokenIndices.SequenceEqual(match.ControllerTokenIndices) &&
                    candidate.DependentTokenIndices.SequenceEqual(match.DependentTokenIndices));
                Assert.AreEqual(TextGrammarAssessment.Compatible, retained.Assessment, revised);
            }
        }
    }

    [TestMethod]
    public void Copular_controller_repairs_preserve_both_nominal_and_outgoing_agreement()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        const string nominal = "local_exact_attribute_agreement.v1";
        const string finite = "grammar.agreement.copular_finite.v1";
        const string adjective = "grammar.agreement.copular_adjective.v1";
        foreach ((string text, string[] rules, string[] repairs, int participants) in new[]
        {
            ("Малката книга е хубав.", new[] { adjective }, new[] { "хубава" }, 2),
            ("Малката хубава книга е хубав.", new[] { adjective }, new[] { "хубава" }, 3),
            ("Малките книга са хубави.", new[] { nominal, finite, adjective }, new[] { "книги" }, 2),
            ("Малките книги е хубава.", new[] { finite, adjective }, Array.Empty<string>(), 2),
            ("Малките книга е хубава.", new[] { nominal }, new[] { "Малката" }, 2)
        })
        {
            var analysis = session.AnalyzeText(text, false, options, default);
            var matches = Relevant(analysis);
            Assert.HasCount(3, matches, text);
            Assert.IsTrue(matches.Where(match => match.RuleId != nominal)
                .All(match => match.ControllerTokenIndices.Count == participants), text);
            var conflicts = matches.Where(match => match.Assessment == TextGrammarAssessment.Mismatch).ToArray();
            CollectionAssert.AreEquivalent(rules, conflicts.Select(match => match.RuleId).ToArray(), text);
            foreach (var conflict in conflicts)
                CollectionAssert.AreEquivalent(repairs, conflict.Repairs.Select(repair => repair.Edits.Single().Replacement).ToArray(), text);
            foreach (var edit in conflicts.SelectMany(match => match.Repairs).Select(repair => repair.Edits.Single()).Distinct())
            {
                string changed = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
                var verified = Relevant(session.AnalyzeText(changed, false, options, default));
                Assert.HasCount(3, verified, changed);
                Assert.IsTrue(verified.All(match => match.Assessment == TextGrammarAssessment.Compatible), changed);
            }
        }

        const string longer = "Малките книга пътува с мен.";
        var uncertain = Relevant(session.AnalyzeText(longer, false, options, default)).Single();
        Assert.IsTrue(uncertain.Repairs.Any(repair => repair.Edits.Single().Replacement == "книги" &&
            repair.Qualifications.Contains("clause_relation_unverified")));
        Assert.IsTrue(uncertain.Repairs.Any(repair => repair.Edits.Single().Replacement == "Малката" &&
            repair.Qualifications.Count == 0));
        foreach (string text in new[] { "Малката стая е хубав.", "Малките стаи са хубави.", "Вие сте щастлива.",
            "Вие сте щастлив.", "вие сте щастлива." })
        {
            var match = Relevant(session.AnalyzeText(text, false, options, default)).Single(row => row.RuleId == adjective);
            Assert.AreEqual(TextGrammarAssessment.Ambiguous, match.Assessment, text);
            Assert.IsEmpty(match.Repairs, text);
            if (text.StartsWith("вие", StringComparison.OrdinalIgnoreCase))
            {
                Assert.AreEqual("polite_singular_address_possible", match.Reason, text);
                Assert.IsEmpty(match.Conflicts, text);
            }
        }

        static TextGrammarMatch[] Relevant(TextAnalysis analysis) =>
            analysis.Grammar!.Matches.Where(match => match.RuleId is nominal or finite or adjective).ToArray();
    }

    [TestMethod]
    public void Local_attribute_agreement_is_measured_in_open_finite_text()
    {
        using var session = GraphStressAcceptance.Open();
        foreach ((string text, int start, int length) in new[]
        {
            ("Малките книга пътува с мен", 0, 7),
            ("📚. Малките книгата пътува с мен", 4, 7),
            ("Видяхме хубава дете", 8, 6)
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text);
            TextDiagnostic warning = analysis.Diagnostics.Single(d => d.Code == Attribute);
            Assert.AreEqual((start, length), (warning.Start, warning.Length), text);
            Assert.IsNull(warning.ProjectedText, text);
            Assert.IsNotEmpty(warning.Readings!, text);
        }

        foreach (string text in new[] { "Малките книги пътуват с мен", "Хубава дете", "Хубава 📚 дете расте" })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Attribute), text);
    }

    [TestMethod]
    public void Late_articles_in_exact_prepositional_groups_keep_original_offsets_and_readings()
    {
        using var session = GraphStressAcceptance.Open();
        foreach ((string text, string target) in new[]
        {
            ("Чорбанова четат на червена пейката", "пейката"),
            ("📚. Те седят на червената пейката.", "пейката"),
            ("Седят на дървен столът.", "столът"),
            ("Работят с нови инструментите.", "инструментите"),
            ("Говорим за синьо палтото.", "палтото")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text);
            TextDiagnostic[] placement = analysis.Diagnostics.Where(d => d.Code == Placement).ToArray();
            Assert.HasCount(1, placement, text);
            TextDiagnostic warning = placement[0];
            Assert.AreEqual((text.IndexOf(target, StringComparison.Ordinal), target.Length), (warning.Start, warning.Length), text);
            Assert.IsNull(warning.ProjectedText, text);
            Assert.IsNull(warning.GraphReadings, text);
            Assert.IsNotEmpty(warning.Readings!, text);
            Assert.IsTrue(analysis.Validation!.Diagnostics.Any(d => d.Code == Placement && d.Start == warning.Start &&
                d.Length == warning.Length && d.Supports.SequenceEqual(warning.Readings!)), text);
        }

        TextAnalysis unassessed = session.AnalyzeText("📚. Вървим до високата оградата.");
        Assert.IsFalse(unassessed.Diagnostics.Any(d => d.Code == Placement));
        var uncertain = unassessed.ReadingInventory!;
        var finite = uncertain.Occurrences.Single(o => o.Text == "Вървим");
        var valencies = uncertain.Sets[finite.ReadingSetIndex].ResolvedReadings
            .Select(row => row.Features.DeclaredTransitivity).ToArray();
        CollectionAssert.Contains(valencies, MorphologicalVerbTransitivity.Intransitive);
        CollectionAssert.Contains(valencies, MorphologicalVerbTransitivity.Transitive);

        var homograph = session.AnalyzeText("Четат на червена пейката");
        var inventory = homograph.ReadingInventory!;
        var adjective = inventory.Occurrences.Single(o => o.Text == "червена");
        var readings = inventory.Sets[adjective.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(readings.Any(r => r.Features.NameKind == MorphologicalNameKind.PlaceName));
        Assert.IsTrue(readings.Any(r => r.Features.ParticipleKind == MorphologicalParticipleKind.PastPassive));
        var bound = homograph.Diagnostics.Single(d => d.Code == Placement).Readings!;
        Assert.IsTrue(bound.Any(r => r.TokenIndex == adjective.TokenIndex &&
            readings[r.CompleteReadingIndex].Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective));
        Assert.IsFalse(bound.Any(r => r.TokenIndex == adjective.TokenIndex &&
            readings[r.CompleteReadingIndex].Features.PartOfSpeech == MorphologicalPartOfSpeech.ProperNoun));
    }

    [TestMethod]
    public void Initial_exact_nominal_groups_can_retain_the_same_local_article_constraint()
    {
        using var session = GraphStressAcceptance.Open();
        foreach ((string text, string target) in new[]
        {
            ("Светложълтото лаковото покритие.", "лаковото"),
            ("Светложълтото лаково покритието.", "покритието"),
            ("Високата оградата падна.", "оградата")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text);
            TextDiagnostic warning = analysis.Diagnostics.Single(d => d.Code == Placement);
            Assert.AreEqual(target, text.Substring(warning.Start, warning.Length), text);
            Assert.AreEqual("grammar.article.first_initial_nominal_member.v1", warning.RuleId);
            Assert.IsNotEmpty(warning.Readings!, text);
        }
        foreach (string text in new[]
        {
            "Светложълтото лаково покритие.", "Високата ограда падна.",
            "Беше червена пейката.", "Висока, оградата падна.", "„Високата оградата падна.“"
        })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Placement), text);
    }

    [TestMethod]
    public void Definite_substantive_and_separate_nominal_can_have_independent_roles()
    {
        using var session = GraphStressAcceptance.Open();
        foreach (string text in new[]
        {
            "На стария капитанът помогна.", "Стария капитанът видя.",
            "На високата учителят даде книга.", "Младата лекарят прегледа.",
            "За младия директорът се погрижи.", "На болния лекарят даде вода.",
            "На бялата лекарят сложи бинт.", "На високата докторът подаде чаша.",
            "На бедните хората помогнаха.", "На младата жената помогна.",
            "На възрастните съседите дадоха вода.", "На болната сестрата занесе лекарство.",
            "Старият капитана видя.", "Младият лекаря прегледа.", "Високият жената видя.",
            "На червената книгата сложих.",
            "На стария младият капитан помогна.", "На високата ниската жена помогна.",
            "На болния опитният лекар даде вода.", "Старият младия лекар видя.",
            "На осакатения капитанът помогна.", "Осакатения капитанът видя.",
            "Помогна на стария капитанът.", "Помогна на младата жената.",
            "Стария капитанът видя бързо.", "Стария капитанът видя сутринта.",
            "Малките детето ги вижда.", "Малките детето ги вижда", "Вижда ги малките детето.",
            "Високата учителката видя сутринта.", "Високата учителката видя в градината.",
            "Младата лекарят прегледа в болницата.", "Малката детето я вижда"
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text);
            Assert.IsFalse(analysis.Diagnostics.Any(d => d.Code is Placement or Attribute), text);
        }

        foreach (string text in new[]
        {
            "На дървен столът седяха.", "Високата оградата падна.",
            "Светложълтото лаковото покритие.", "Старият капитанът видя.",
            "На червената дървена пейката седяха."
        })
            Assert.IsTrue(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Placement), text);
        Assert.IsTrue(session.AnalyzeText("Малките детето го вижда.").Diagnostics.Any(d => d.Code == Attribute));
        Assert.IsFalse(session.AnalyzeText("Малката книга описва в детайли градината.").Diagnostics.Any(d => d.Code == Placement));
        foreach (string text in new[]
        {
            "Малката книгата описва в детайли градината.", "На бялото палтото зашиха копче.",
            "Новият бояджията боядисва стената."
        })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Placement), text);
    }

    [TestMethod]
    public void Exact_attribute_suffix_survives_an_earlier_competing_predicate_or_coordinator()
    {
        using var session = GraphStressAcceptance.Open();
        foreach ((string text, string target) in new[]
        {
            ("Искаха да си върнат златната кълбо на Алдур, откраднато от осакатения бог Торак.", "златната"),
            ("Оставя една тънка и къси следа подире си, като шлейф на младоженка.", "къси")
        })
        {
            var analysis = session.AnalyzeText(text);
            Assert.IsTrue(analysis.Diagnostics.Any(d => d.Code == Attribute &&
                text.Substring(d.Start, d.Length) == target), text);
        }
    }

    [TestMethod]
    public void Correct_articles_and_open_competing_structures_receive_no_placement_warning()
    {
        using var session = GraphStressAcceptance.Open();
        foreach (string text in new[]
        {
            "Четат на червената пейка.", "Четат на червена пейка.",
            "Работят с новите инструменти.", "Говорим за синьото палто.",
            "Седят на дървения стол.", "„Четат на червена пейката.“",
            "Седят на Червена пейката.", "Пием от синя чашата.",
            "Четат на червена, пейката.", "Четат на червена 📚 пейката."
        })
            Assert.IsFalse(session.AnalyzeText(text).Diagnostics.Any(d => d.Code == Placement), text);
    }

    [TestMethod]
    public void Wave1c_eliminates_clean_corpus_nominal_false_positives_while_preserving_errors_and_controls()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions fullOpts = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        foreach (TextContextOptions opts in new TextContextOptions[] { default, fullOpts })
        {
            foreach (string clean in new[]
            {
                // 8 dev1200 clean controls
                "Беше в малиново розов цвят и Рей моментално разбра, че ще й стои чудесно.",
                "Накрая бе събрал куража да я покани, може би под претекст да обсъдят някоя идеологически интересна тема.",
                "След това случаи на арести при нападение в Денвър и Канзас Сити; най-новите разкрития около конспиративната организация „Хора за свободата вътре в страната ни“.",
                "Клинично изолиран синдром (КИС) е първият неврологичен епизод при пациенти, причинен от възпаление или демиелинизация на нервната тъкан.",
                "Всички страни, за които се прилага един и същи размер на възстановяване при износ или такса, се считат за едно местоназначение.",
                "Председателят на Съвета е оправомощен да посочи лице, упълномощено да подпише споразумението, с което да обвърже правно Европейския съюз.",
                "За плащането на авансови суми, посочени в параграфи 2 и 3, е необходимо заявителите да представят доказателство , че е била депозирана гаранция, равна на 110% от сумата на аванса.",
                "Всеки документ или съобщение, което изменя или посочва конкретно и недвусмислено първоначалната фактура, се третира като фактура.",
                // 7 chitanka1784 clean controls + valid standalone subst-adj & agreeing controls
                "Беше като дълбоко синьо езеро насред скалите, останало след прилива, пълно с измамно бистра вода.",
                "Той придърпваше едно след друго тежки чекмеджета от потъмняло дърво.",
                "Езвалът не си представяше много ясно какво е това кучета, но все пак си представяше нещо.",
                "Играехме на една секс игра с нож.",
                "Блекуел явно още не беше бит и на гордото му лице бяха изписани решителност и желязна воля.",
                "Надебеляха, покриха се с плътна, леко напукана кора.",
                "Всяка частица на тази цистерна бе от млечно бялата стъклена материя, която звънтеше като камбана, щом човек я чукнеше с лопата.",
                "За това момче говорихме вчера.",
                "Операционните са готови."
            })
            {
                TextAnalysis actual = session.AnalyzeText(clean, false, opts, default);
                Assert.IsFalse(actual.Diagnostics.Any(d => d.Code is Attribute or Placement), clean);
            }

            foreach ((string errorSentence, string expectedTarget, string expectedCode) in new[]
            {
                ("Но защо искате да продадете вазата точно на същи музей?", "същи", Attribute),
                ("Стоях в санитарната палатка, докато опънатите тела едно след друго пристигнаха на операционните маса.", "операционните", Attribute),
                ("Стоях в санитарната палатка, докато опънатите тела едно след друго пристигнаха на операционния маса.", "операционния", Attribute),
                ("Аргир Наков Манасиев, наречен Гевгелийски, е български революционер, учител и общественик, деец на Вътрешната македоно-одрински революционна организация, гевгелийски околийски войвода, председател на Горноджумайското околийско ръководно тяло на Вътрешната македонска революционна организация.", "македоно-одрински", Attribute),
                ("От други страна, държавите-членки е следвало да се въздържат да прибягват до система на отговорност, ограничена до отделни членове на съветите.", "други", Attribute),
                ("Настоящото Решение се публикува в Раздел \"Европейски икономическа зона\" и в Приложението за ЕИЗ на Официален вестник на Европейския съюз.", "Европейски", Attribute),
                ("Да обсъдят някой идеологически интересна тема.", "някой", Attribute),
                ("Клинично изолирана синдром е първият неврологичен епизод.", "изолирана", Attribute),
                ("След тази случаи на арести.", "тази", Attribute),
                ("За тази случаи седяхме.", "тази", Attribute),
                ("Играехме на едно секс игра с нож.", "едно", Attribute),
                ("Беше червено колата.", "червено", Attribute),
                ("Колата беше червено.", "червено", "productive_predicative_agreement_mismatch"),
                ("На ново малкото бюро седяха.", "малкото", Placement)
            })
            {
                TextAnalysis err = session.AnalyzeText(errorSentence, false, opts, default);
                TextDiagnostic hit = err.Diagnostics.FirstOrDefault(d => d.Code == expectedCode &&
                    errorSentence.Substring(d.Start, d.Length) == expectedTarget)!;
                Assert.IsNotNull(hit, $"{errorSentence} expected {expectedCode}@{expectedTarget}; got=[{string.Join(',', err.Diagnostics.Select(d => $"{d.Code}@{errorSentence.Substring(d.Start, d.Length)}"))}]");
            }
        }
    }

    [TestMethod]
    public void Post_alpha1_independent_number_gender_and_article_placement_checks_coexist()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions fullOpts = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        foreach (TextContextOptions opts in new TextContextOptions[] { default, fullOpts })
        {
            foreach ((string sentence, string attrTarget, string artTarget) in new[]
            {
                ("Малките книгата пътува с мен.", "Малките", "книгата"),
                ("Влизам в малките стаята.", "малките", "стаята"),
                ("Седим на червените пейката.", "червените", "пейката")
            })
            {
                TextAnalysis res = session.AnalyzeText(sentence, false, opts, default);
                TextDiagnostic attr = res.Diagnostics.SingleOrDefault(d => d.Code == Attribute && sentence.Substring(d.Start, d.Length) == attrTarget)!;
                TextDiagnostic art = res.Diagnostics.SingleOrDefault(d => d.Code == Placement && sentence.Substring(d.Start, d.Length) == artTarget)!;
                Assert.IsNotNull(attr, $"{sentence}: expected {Attribute}@{attrTarget}");
                Assert.IsNotNull(art, $"{sentence}: expected {Placement}@{artTarget}");
                if (opts.IncludeGrammarEvidence)
                {
                    Assert.IsTrue(res.Grammar!.Matches.Any(m => m.Assessment == TextGrammarAssessment.Mismatch && m.RuleId == "local_exact_attribute_agreement.v1" && m.Reason == "article_placement_unresolved"), sentence);
                    Assert.IsTrue(res.Grammar.Matches.Any(m => m.Assessment == TextGrammarAssessment.Mismatch && m.RuleId.StartsWith("grammar.article.first_", StringComparison.Ordinal)), sentence);
                }
            }

            {
                const string attrOnly = "Малките книга пътува с мен.";
                TextAnalysis res = session.AnalyzeText(attrOnly, false, opts, default);
                Assert.IsTrue(res.Diagnostics.Any(d => d.Code == Attribute && attrOnly.Substring(d.Start, d.Length) == "Малките"));
                Assert.IsFalse(res.Diagnostics.Any(d => d.Code == Placement));
            }
            {
                const string artOnly = "Малката книгата пътува с мен.";
                TextAnalysis res = session.AnalyzeText(artOnly, false, opts, default);
                Assert.IsFalse(res.Diagnostics.Any(d => d.Code == Attribute));
                Assert.IsTrue(res.Diagnostics.Any(d => d.Code == Placement && artOnly.Substring(d.Start, d.Length) == "книгата"));
            }

            foreach (string clean in new[]
            {
                "Малката книга пътува с мен.",
                "Малките книги пътуват с мен.",
                "Новите стола са тук.",
                "Малките детето ги вижда.",
                "На бедните хората помогнаха.",
                "Малката книгата описва в детайли градината.",
                "Живея в старите къщата.",
                "Оставих при старите къщата.",
                "Седя на червените пейката."
            })
            {
                TextAnalysis ok = session.AnalyzeText(clean, false, opts, default);
                Assert.IsFalse(ok.Diagnostics.Any(d => d.Code is Attribute or Placement), clean);
            }
        }
    }
}
