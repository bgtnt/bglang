using System;
using System.Linq;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    private static readonly TextContextOptions NaturalSentenceOptions = new()
    {
        IncludeAdditionalRoleHeuristics = true,
        IncludeWordConstructions = true,
        IncludeNominalConstructions = true
    };

    [TestMethod]
    [DataRow("Свали шлема си с гребена и го размаха.", "BasicPrepositionalObject", "Свали", "с гребена")]
    [DataRow("После един от накичените свали шлема си с гребена и го размаха над главата си.", "BasicPrepositionalObject", "свали", "с гребена")]
    [DataRow("Свали шлема си с гребена и го размаха над главата си.", "BasicPrepositionalObject", "Свали", "с гребена")]
    [DataRow("Свали шлема си с гребена и го размаха над него.", "BasicPrepositionalObject", "Свали", "с гребена")]
    [DataRow("Той стоеше с портрета и го показа на детето.", "BasicPrepositionalObject", "стоеше", "с портрета")]
    [DataRow("Той не е висял на стената и класът не е правил стойка.", "BasicPrepositionalObject", "е", "на стената")]
    [DataRow("Учителят влезе и говори с децата.", "BasicPrepositionalObject", "влезе", null)]
    [DataRow("Учителят влезе и говори с децата.", "BasicObject", "влезе", null)]
    [DataRow("Каза, че пътува с влака.", "BasicPrepositionalObject", "Каза", null)]
    [DataRow("Тя подари книгата, която намери в парка, на детето.", "BasicPrepositionalObject", "подари", "на детето")]
    [DataRow("Работя с молив и гума.", "BasicPrepositionalObject", "Работя", "с молив и гума")]
    [DataRow("Тя работи с молив и гума на масата.", "BasicPrepositionalObject", "работи", "с молив и гума")]
    [DataRow("Той се възхищава на ръста и размаха на птицата.", "BasicPrepositionalObject", "възхищава", "на ръста и размаха")]
    [DataRow("Тя се интересува от него и неговите работи на масата.", "BasicPrepositionalObject", "интересува", "от него и неговите работи")]
    [DataRow("Тя се интересува от него и неговите работи.", "BasicPrepositionalObject", "интересува", "от него и неговите работи")]
    [DataRow("Тя се интересува от него и добрите му работи.", "BasicPrepositionalObject", "интересува", "от него и добрите му работи")]
    [DataRow("Тя се интересува от него и добрите му работи на масата.", "BasicPrepositionalObject", "интересува", "от него и добрите му работи")]
    [DataRow("Тя се интересува от него и неговите книги на масата.", "BasicPrepositionalObject", "интересува", "от него и неговите книги")]
    [DataRow("Тя се интересува от него и неговите деца.", "BasicPrepositionalObject", "интересува", "от него и неговите деца")]
    [DataRow("Всяка мина през залата.", "BasicPrepositionalObject", "мина", "през залата")]
    [DataRow("Една мина през залата.", "BasicPrepositionalObject", "мина", "през залата")]
    [DataRow("Нейната мина през залата.", "BasicPrepositionalObject", "мина", "през залата")]
    [DataRow("Добрата му мина през залата.", "BasicPrepositionalObject", "мина", "през залата")]
    [DataRow("Учителката каза, че всяка мина през залата.", "BasicPrepositionalObject", "каза", null)]
    [DataRow("Децата видяха всяка мина на пътя.", "BasicObject", "видяха", "всяка мина")]
    [DataRow("Учителката говори с момичетата и всяка мина през залата.", "BasicPrepositionalObject", "говори", "с момичетата")]
    [DataRow("Тя се интересува от него и неговите работи останаха неплатени.", "BasicPrepositionalObject", "интересува", "от него")]
    [DataRow("Тя се интересува от него и добрите му работи останаха неплатени.", "BasicPrepositionalObject", "интересува", "от него")]
    [DataRow("Тя се интересува от него и той работи на масата.", "BasicPrepositionalObject", "интересува", "от него")]
    [DataRow("Тя се интересува от него и работи на масата.", "BasicPrepositionalObject", "интересува", "от него")]
    [DataRow("Тя говори с него и нея за работата.", "BasicPrepositionalObject", "говори", "с него и нея")]
    [DataRow("Говоря с брат ми.", "BasicPrepositionalObject", "Говоря", "с брат ми")]
    [DataRow("Тя нарисува къща и дърво.", "BasicObject", "нарисува", "къща и дърво")]
    [DataRow("Учителят е спокоен и големите момчета, за да помогнат, пристигнаха.", "BasicComplement", "е", "спокоен")]
    [DataRow("Учителите са спокойни и големите момчета, за да помогнат, пристигнаха.", "BasicComplement", "са", "спокойни")]
    [DataRow("Учителят е спокоен и големите момчета пристигнаха, за да помогнат.", "BasicComplement", "е", "спокоен")]
    [DataRow("Тя работи с молив и го размаха над главата си, за да го видят.", "BasicPrepositionalObject", "работи", "с молив")]
    [DataRow("Учителят разглежда книги и списания, за да избере текст.", "BasicObject", "разглежда", "книги и списания")]
    public void Basic_object_extents_follow_their_predicate_clause(
        string text, string rule, string predicate, string? expected)
    {
        TextAnalysis analysis = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        TextSyntaxSentence sentence = analysis.Syntax!.Sentences.Single();
        int predicateIndex = sentence.PredicateTokenIndex!.Value;
        Assert.AreEqual(predicate, analysis.Tokens[predicateIndex].Text);
        var matches = sentence.Candidates.Where(c => c.RuleId == rule).ToArray();
        if (expected is null)
        {
            Assert.IsEmpty(matches);
            return;
        }
        Assert.HasCount(1, matches);
        TextSyntaxCandidate role = matches[0];
        Assert.AreEqual(text.IndexOf(expected, StringComparison.Ordinal), role.Start);
        Assert.AreEqual(expected, text.Substring(role.Start, role.Length));
        Assert.AreEqual(predicateIndex, role.Context.Single().TokenIndex);
        CollectionAssert.AreEqual(expected.Split(' '), role.Targets.Select(t => analysis.Tokens[t.TokenIndex].Text).ToArray());
        foreach (TextReadingOccurrence occurrence in analysis.ReadingInventory!.Occurrences)
            CollectionAssert.AreEqual(session.Lookup(occurrence.Text, LookupMode.Exact, 256).ToArray(),
                analysis.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Select(r => r.Raw).ToArray(), occurrence.Text);
    }

    [TestMethod]
    [DataRow("Той работеше с гребена и, след кратка пауза, го размаха над главата си.")]
    [DataRow("Той работеше с гребена и 😀 го размаха над главата си.")]
    [DataRow("Той работеше с гребена и „го размаха над главата си“.")]
    public void Interrupted_coordinator_clitic_prefix_does_not_supply_a_prepositional_object_extent(string text)
    {
        TextAnalysis analysis = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        foreach (TextSyntaxCandidate role in analysis.Syntax!.Sentences.Single().Candidates.Where(c => c.RuleId == "BasicPrepositionalObject"))
            Assert.IsFalse(role.Targets.Any(t => analysis.Tokens[t.TokenIndex].Text == "размаха"), text);
    }

    [TestMethod]
    public void Coordinated_accusative_host_preserves_the_complete_noun_and_finite_inventory()
    {
        const string text = "После един от накичените свали шлема си с гребена и го размаха над главата си.";
        TextAnalysis analysis = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        TextReadingInventory inventory = analysis.ReadingInventory!;
        TextReadingOccurrence occurrence = inventory.Occurrences.Single(o => o.Text == "размаха");
        TextReadingSet set = inventory.Sets[occurrence.ReadingSetIndex];
        CollectionAssert.AreEqual(new[]
        {
            "размах|Съществително:40a,Мъжки,Членуван",
            "размахам|Глагол:186tt,2л.,Минало_свършено",
            "размахам|Глагол:186tt,3л.,Минало_свършено",
            "размахам|Глагол:186tt,3л.,Сегашно"
        }, set.ResolvedReadings.Select(r => r.Raw.Lemma + "|" + r.Raw.PartOfSpeech).ToArray());
        Assert.AreEqual(4, set.DisplayedReadingCount);
        CollectionAssert.AreEqual(set.ResolvedReadings.Select(r => r.Raw).ToArray(), analysis.Tokens[occurrence.TokenIndex].Analysis!.Readings.ToArray());
        TextReadingOccurrence clitic = inventory.Occurrences.Single(o => o.Text == "го");
        Assert.IsTrue(inventory.Sets[clitic.ReadingSetIndex].ResolvedReadings.Any(r => r.Features is
            { Case: MorphologicalCase.Accusative, IsShortForm: true }));
        Assert.AreEqual(text, analysis.Text);
    }

    [TestMethod]
    [DataRow("Държавите-членки приемат необходимите законови подзаконови и административни разпоредби, за да се съобразят с настоящата директива преди 31 декември 1982 г. Те незабавно уведомяват Комисията за това.")]
    [DataRow("Държавите-членки приемат необходимите законови подзаконови и административни разпоредби, без да нарушават настоящата директива.")]
    public void Coordinated_object_modifiers_do_not_borrow_the_finite_of_a_licensed_subordinator(string text)
    {
        const string expected = "необходимите законови подзаконови и административни разпоредби";
        foreach (TextContextOptions options in new TextContextOptions[] { new() { IncludeAdditionalRoleHeuristics = true }, NaturalSentenceOptions })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextSyntaxSentence sentence = analysis.Syntax!.Sentences[0];
            TextSyntaxCandidate predicate = sentence.Candidates.Single(c => c.RuleId == "BasicPredicate");
            Assert.AreEqual("приемат", text.Substring(predicate.Start, predicate.Length));
            TextSyntaxCandidate obj = sentence.Candidates.Single(c => c.RuleId == "BasicObject");
            Assert.AreEqual(expected, text.Substring(obj.Start, obj.Length));
            Assert.AreEqual(text.IndexOf(expected, StringComparison.Ordinal), obj.Start);
            Assert.IsTrue(obj.Targets.Any(t => analysis.Tokens[t.TokenIndex].Text == "разпоредби"));
            Assert.IsFalse(sentence.Candidates.Any(c => c.RuleId == "BasicPrepositionalObject"),
                "The matrix object must not regain the subordinate clause's prepositional object.");
            if (options.IncludeNominalConstructions)
                Assert.IsTrue(analysis.NominalConstructions!.Phrases.Any(p => p.Assessment == NominalAgreementAssessment.Compatible &&
                    text.Substring(p.Start, p.Length) == expected));
            Assert.AreEqual(text, analysis.Text);
        }
    }

    [TestMethod]
    public void Legal_comma_nominal_core_keeps_its_complete_advice_and_separate_subordinate_scope()
    {
        const string text = "Държавите-членки приемат необходимите законови, подзаконови и административни разпоредби, за да се съобразят с настоящата директива преди 31 декември 1982 г. Те незабавно уведомяват Комисията за това.";
        foreach (TextContextOptions options in new TextContextOptions[] { new() { IncludeAdditionalRoleHeuristics = true }, NaturalSentenceOptions })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextSyntaxSentence sentence = analysis.Syntax!.Sentences[0];
            Assert.AreEqual("приемат", analysis.Tokens[sentence.PredicateTokenIndex!.Value].Text);
            Assert.IsFalse(sentence.Candidates.Any(c => c.RuleId == "BasicPrepositionalObject"));
            if (options.IncludeNominalConstructions)
            {
                TextNominalPhrase phrase = analysis.NominalConstructions!.Phrases.Single(p =>
                    text.Substring(p.Start, p.Length) == "необходимите законови, подзаконови и административни разпоредби");
                Assert.AreEqual(NominalAgreementAssessment.Compatible, phrase.Assessment);
                CollectionAssert.AreEqual(new[] { ",", "и" }, phrase.ConnectorTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray());
            }
        }
    }

    [TestMethod]
    [DataRow("Вчера сутринта в центъра на града се проведе протест.", "проведе", "протест", "протест")]
    [DataRow("Кметът излезе пред сградата на общината и говори с протестиращите.", "излезе", "Кметът", "Кметът")]
    [DataRow("Според организаторите протестът ще продължи и утре.", "продължи", "протестът", "протестът")]
    [DataRow("Майка ми приготви вечеря, а баща ми подреди масата.", "приготви", "Майка", "Майка")]
    [DataRow("Когато се прибрах, вече беше тъмно.", "беше", "", "")]
    [DataRow("Книгата, която ми подари, е много интересна.", "е", "Книгата", "Книгата")]
    [DataRow("Новият ресторант на ъгъла отвори врати миналата седмица.", "отвори", "Новият ресторант", "ресторант")]
    [DataRow("Красивата стара градина привлича много туристи през лятото.", "привлича", "Красивата стара градина", "градина")]
    [DataRow("Учениците от седми клас пишат контролна работа по математика.", "пишат", "Учениците", "Учениците")]
    [DataRow("Не знам дали ще успея да дойда навреме.", "знам", "", "")]
    [DataRow("Ако вали, ще останем вкъщи.", "останем", "", "")]
    [DataRow("Лекарят ми каза, че трябва да почивам повече.", "каза", "Лекарят", "Лекарят")]
    [DataRow("Старият мост над реката беше построен преди сто години.", "беше", "Старият мост", "мост")]
    [DataRow("Малкото момиче държеше голяма червена топка.", "държеше", "Малкото момиче", "момиче")]
    [DataRow("Колата на съседа ни е паркирана пред входа.", "е", "Колата", "Колата")]
    [DataRow("Цените на храните се повишиха значително през последната година.", "повишиха", "Цените", "Цените")]
    [DataRow("Кучето лаеше цяла нощ и никой не можа да заспи.", "лаеше", "Кучето", "Кучето")]
    [DataRow("Трябва да си купя нови обувки за зимата.", "Трябва", "", "")]
    [DataRow("След дъжда небето се проясни и излезе дъга.", "проясни", "небето", "небето")]
    [DataRow("Много хора предпочитат да пазаруват по интернет.", "предпочитат", "Много хора", "хора")]
    [DataRow("Тази година зимата дойде необичайно рано.", "дойде", "зимата", "зимата")]
    [DataRow("Думите му ме накараха да се замисля.", "накараха", "Думите", "Думите")]
    [DataRow("В залата останаха много празни места.", "останаха", "много празни места", "места")]
    [DataRow("Малкото книги пътуват с мен.", "пътуват", "Малкото книги", "книги")]
    [DataRow("Малкото нови книги пътуват с мен.", "пътуват", "Малкото нови книги", "книги")]
    [DataRow("Малкото хора пътуват с мен.", "пътуват", "Малкото хора", "хора")]
    [DataRow("Немалкото книги останаха тук.", "останаха", "Немалкото книги", "книги")]
    [DataRow("Многото книги пътуват с мен.", "пътуват", "Многото книги", "книги")]
    [DataRow("Повечето книги пътуват с мен.", "пътуват", "Повечето книги", "книги")]
    public void Natural_sentence_fusion_identifies_matrix_predicate_and_subject_without_mutating_inventory(
        string text, string expectedPredicate, string expectedSubjectSpan, string expectedSubjectHead)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        Assert.IsNotNull(actual.Validation);
        Assert.IsTrue(actual.Validation.Available);
        Assert.IsNotNull(actual.Syntax);
        Assert.IsTrue(actual.Syntax.Available);

        TextSyntaxSentence syntaxSentence = actual.Syntax.Sentences.Single();
        TextSyntaxCandidate predicate = syntaxSentence.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate);
        Assert.AreEqual(expectedPredicate, actual.Text.Substring(predicate.Start, predicate.Length), text);

        TextSyntaxCandidate[] subjects = syntaxSentence.Candidates.Where(c => c.Role == TextSyntaxRole.Subject).ToArray();
        if (expectedSubjectSpan.Length == 0)
        {
            Assert.IsEmpty(subjects, $"Expected no spurious subject on '{text}'.");
        }
        else
        {
            Assert.HasCount(1, subjects, text);
            Assert.AreEqual(expectedSubjectSpan, actual.Text.Substring(subjects[0].Start, subjects[0].Length), text);
            int headTok = subjects[0].Targets[^1].TokenIndex;
            Assert.AreEqual(expectedSubjectHead, actual.Tokens[headTok].Text, text);
        }

        // Verify complete ReadingInventory conservation against fresh Lookup(Exact) rows.
        Assert.IsNotNull(actual.ReadingInventory);
        foreach (TextReadingOccurrence occ in actual.ReadingInventory.Occurrences)
        {
            TextReadingSet set = actual.ReadingInventory.Sets[occ.ReadingSetIndex];
            CollectionAssert.AreEqual(
                set.ResolvedReadings.Take(set.DisplayedReadingCount).Select(r => r.Raw).ToArray(),
                actual.Tokens[occ.TokenIndex].Analysis!.Readings.ToArray(),
                "Display readings remain exact prefix of untouched ResolvedReadings.");
        }
    }

    [TestMethod]
    public void Postverbal_subject_for_transitive_tagged_verbs_requires_explicit_verb_frames_and_excludes_existential_imam()
    {
        // Existential/possessive "имам" has ~80% direct-object frames in BVFL and Transitive tag in the lexicon:
        // its postverbal NP is an object, never a subject.
        TextAnalysis existential = session.AnalyzeText("На масата има прясна салата.", false, NaturalSentenceOptions, default);
        TextSyntaxSentence exSyntax = existential.Syntax!.Sentences.Single();
        Assert.AreEqual("има", existential.Text.Substring(
            exSyntax.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate).Start,
            exSyntax.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate).Length));
        Assert.IsEmpty(exSyntax.Candidates.Where(c => c.Role == TextSyntaxRole.Subject).ToArray());

        // "падна" carries Intransitive in the lexicon (152it), so postverbal subject resolution succeeds without BVFL.
        TextAnalysis lexiconIntransitive = session.AnalyzeText("От клона падна червена ябълка.", false, NaturalSentenceOptions, default);
        TextSyntaxCandidate padnaSubj = lexiconIntransitive.Syntax!.Sentences.Single()
            .Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
        Assert.AreEqual("червена ябълка", lexiconIntransitive.Text.Substring(padnaSubj.Start, padnaSubj.Length));

        // Known limitation (tracked separately from Stage 5): "зряла" is tagged only as a PastActive -л participle
        // (Глагол:...,Минало_свършено_деятелно), whereas IsNominalModifierOrDeterminer currently admits Adjective
        // and PastPassive participles; postverbal subject resolves head "круша" without prenominal "зряла".
        TextAnalysis zryalaKrusha = session.AnalyzeText("От клона падна зряла круша.", false, NaturalSentenceOptions, default);
        TextSyntaxCandidate krushaSubj = zryalaKrusha.Syntax!.Sentences.Single()
            .Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
        Assert.AreEqual("круша", zryalaKrusha.Tokens[krushaSubj.Targets[^1].TokenIndex].Text);
        Assert.AreEqual("круша", zryalaKrusha.Text.Substring(krushaSubj.Start, krushaSubj.Length));

        // Known no-catalog behavior: "живея" carries Legacy Transitive in the lexicon (160ti).
        // Without an explicitly supplied VerbFrameCatalog, the runtime abstains on postverbal subject selection;
        // full recovery ("много диви животни") is verified with an explicit VerbFrameCatalog in BgLang.Tests.
        TextAnalysis zhiveyat = session.AnalyzeText("В гората живеят много диви животни.", false, NaturalSentenceOptions, default);
        TextSyntaxSentence zhSyntax = zhiveyat.Syntax!.Sentences.Single();
        Assert.AreEqual("живеят", zhiveyat.Text.Substring(
            zhSyntax.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate).Start,
            zhSyntax.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate).Length));
        TextSyntaxCandidate[] zhSubjects = zhSyntax.Candidates.Where(c => c.Role == TextSyntaxRole.Subject).ToArray();
        Assert.IsEmpty(zhSubjects);
    }

    [TestMethod]
    public void Selected_syntax_projects_actual_complete_package_supports_and_keeps_the_fusion_phrase()
    {
        using var language = GraphStressAcceptance.Open();
        foreach (bool enabled in new[] { false, true })
        {
            const string literal = "Работи учителят.";
            TextAnalysis actual = language.AnalyzeText(literal, false, new() { IncludeAdditionalRoleHeuristics = enabled }, default);
            Assert.AreEqual(literal, actual.Text);
            var context = actual.Context!.Sentences.Single();
            var syntax = actual.Syntax!.Sentences.Single();
            AssertInventory(actual);
            Console.WriteLine($"Package {language.Provenance.PackageId}; {literal}; additional={enabled}; route={syntax.Route}; " +
                System.Text.Json.JsonSerializer.Serialize(actual.ReadingInventory));
            if (context.SelectedPredicateTokenIndex.HasValue)
            {
                Assert.AreEqual("Selected", syntax.Route);
                Assert.AreEqual(context.SelectedPredicateTokenIndex, syntax.PredicateTokenIndex);
                AssertSelectedTarget(actual, TextContextRole.Predicate, TextSyntaxRole.Predicate);
                if (context.SelectedRoles.Any(role => role.Role == TextContextRole.Subject))
                    AssertSelectedTarget(actual, TextContextRole.Subject, TextSyntaxRole.Subject);
                else
                    Assert.IsFalse(syntax.Candidates.Any(role => role.Role == TextSyntaxRole.Subject));
            }
        }

        const string phrase = "Над езерото кръжат бели птици.";
        TextAnalysis fusion = language.AnalyzeText(phrase, true, NaturalSentenceOptions with
        {
            IncludeGrammarEvidence = true,
            IncludeSubordinateCommaDiagnostics = true,
            IncludeFragmentDiagnostics = true,
            IncludeSpacingDiagnostics = true
        }, default);
        AssertInventory(fusion);
        Assert.AreEqual("Selected", fusion.Syntax!.Sentences.Single().Route);
        var selected = fusion.Context!.Sentences.Single();
        Assert.IsTrue(selected.EvidenceCodes.Contains("clause_fusion_matrix_selected"));
        Assert.AreEqual(2, selected.SelectedPredicateTokenIndex);
        TextSyntaxCandidate predicate = AssertSelectedTarget(fusion, TextContextRole.Predicate, TextSyntaxRole.Predicate);
        TextSyntaxCandidate subject = AssertSelectedTarget(fusion, TextContextRole.Subject, TextSyntaxRole.Subject);
        Assert.AreEqual("кръжат", phrase.Substring(predicate.Start, predicate.Length));
        CollectionAssert.AreEqual(new[] { new TextReadingReference(2, 0) }, predicate.Targets.Single().Readings.ToArray());
        var inventory = fusion.ReadingInventory!;
        var predicateSet = inventory.Occurrences.Single(o => o.TokenIndex == 2).ReadingSetIndex;
        CollectionAssert.AreEqual(new[] { "Глагол:176aii,3л.,Сегашно,мн.ч.", "Глагол:178ctt,3л.,Сегашно,мн.ч." },
            inventory.Sets[predicateSet].ResolvedReadings.Select(r => r.Raw.PartOfSpeech).ToArray());
        Assert.AreEqual((19, 10), (subject.Start, subject.Length));
        Assert.AreEqual("бели птици", phrase.Substring(subject.Start, subject.Length));
        Assert.AreEqual(4, subject.Targets.Single().TokenIndex);
        Assert.AreEqual("птици", fusion.Tokens[4].Text);

        void AssertInventory(TextAnalysis actual)
        {
            var inventory = actual.ReadingInventory!;
            Assert.HasCount(actual.Tokens.Count(t => t.Kind == TokenKind.Word), inventory.Occurrences);
            foreach (var occurrence in inventory.Occurrences)
            {
                var token = actual.Tokens[occurrence.TokenIndex];
                Assert.AreEqual((token.Start, token.Length, token.Text), (occurrence.Start, occurrence.Length, occurrence.Text));
                CollectionAssert.AreEqual(language.Lookup(token.Text, LookupMode.Exact, 256).ToArray(),
                    inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Select(r => r.Raw).ToArray(), token.Text);
            }
        }

        static TextSyntaxCandidate AssertSelectedTarget(TextAnalysis analysis, TextContextRole contextRole, TextSyntaxRole syntaxRole)
        {
            var selected = analysis.Context!.Sentences.Single().SelectedRoles.Single(role => role.Role == contextRole);
            var projected = analysis.Syntax!.Sentences.Single().Candidates.Single(role => role.Role == syntaxRole);
            TextSyntaxSupport target = projected.Targets.Single();
            Assert.AreEqual(selected.TokenIndex, target.TokenIndex);
            Assert.IsNotEmpty(selected.Readings);
            CollectionAssert.AreEqual(selected.Readings.ToArray(), target.Readings.ToArray());
            Assert.IsNull(target.GraphReadings);
            var inventory = analysis.ReadingInventory!;
            var occurrence = inventory.Occurrences.Single(o => o.TokenIndex == target.TokenIndex);
            Assert.AreEqual((occurrence.Start, occurrence.Length), (selected.Start, selected.Length));
            int count = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Count;
            foreach (var reading in target.Readings)
            {
                Assert.AreEqual(target.TokenIndex, reading.TokenIndex);
                Assert.IsTrue(reading.CompleteReadingIndex >= 0 && reading.CompleteReadingIndex < count);
            }
            return projected;
        }
    }

    [TestMethod]
    public void Postverbal_agreement_preserves_original_subject_evidence_before_comparing_features()
    {
        using var language = GraphStressAcceptance.Open();
        const string agreement = "clause_fusion_agreement.v1";
        const string mismatch = "productive_morphology_agreement_mismatch";
        foreach ((string text, string verb, string subject, bool conflict) in new[]
        {
            ("В двора растат липи.", "растат", "липи", false),
            ("В двора расте липи.", "расте", "липи", true),
            ("Над езерото кръжи бели птици с дълги човки.", "кръжи", "птици", true),
            ("Над езерото кръжат бели птици с дълги човки.", "кръжат", "птици", false),
            ("В двора расте липи с широки корони.", "расте", "липи", true),
            ("В двора растат липи с широки корони.", "растат", "липи", false),
            ("Закъсняваме вие за срещата.", "Закъсняваме", "вие", true),
            ("В езерото плува големи шарени риби.", "плува", "риби", true),
            ("Усмихва се щастливите деца пред камерата.", "Усмихва", "деца", true),
            ("Проверява се новите документи в службата.", "Проверява", "документи", true)
        })
        {
            TextAnalysis analysis = language.AnalyzeText(text);
            TextContextCandidate relation = analysis.Context!.Sentences.Single().Candidates
                .Single(c => c.AuthorityLane == agreement && c.Roles.Any(r => r.Role == TextContextRole.Subject));
            Assert.IsFalse(relation.Selectable, text);
            var controller = relation.Roles.Single(r => r.Role == TextContextRole.Subject);
            var predicate = relation.Roles.Single(r => r.Role == TextContextRole.Predicate);
            Assert.AreEqual(subject, text.Substring(controller.Start, controller.Length), text);
            Assert.AreEqual(verb, text.Substring(predicate.Start, predicate.Length), text);
            foreach (var role in relation.Roles)
            {
                var inventory = analysis.ReadingInventory!;
                var occurrence = inventory.Occurrences.Single(o => o.TokenIndex == role.TokenIndex);
                Assert.IsNotEmpty(role.Readings, text);
                foreach (var reading in role.Readings)
                {
                    Assert.AreEqual(role.TokenIndex, reading.TokenIndex, text);
                    Assert.IsTrue(reading.CompleteReadingIndex >= 0, text);
                    Assert.IsLessThan(inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Count,
                        reading.CompleteReadingIndex, text);
                }
            }
            var warnings = analysis.Diagnostics.Where(d => d.Code == mismatch).ToArray();
            Assert.HasCount(conflict ? 1 : 0, warnings, text);
            if (conflict)
            {
                Assert.AreEqual(predicate.Start, warnings[0].Start, text);
                Assert.AreEqual(predicate.Length, warnings[0].Length, text);
                Assert.IsNull(warnings[0].ProjectedText, text);
            }
        }
    }

    [TestMethod]
    public void Opening_finite_and_se_keep_the_group_without_assigning_every_nominal_as_subject()
    {
        using var language = GraphStressAcceptance.Open();
        foreach ((string text, bool noOvertSubject) in new[]
        {
            ("Разхождаме се край езерото.", true),
            ("Работи се спокойно в кабинета.", true),
            ("Разхожда се цял ден.", true),
            ("Наричат се нашето бъдеще.", true),
            ("Наричат се бъдещето.", true),
            ("Появиха се майката с децата.", false),
            ("Отвори се другата врата.", false)
        })
        {
            TextAnalysis analysis = language.AnalyzeText(text);
            var sentence = analysis.Context!.Sentences.Single();
            var group = sentence.Candidates.Single(c => c.AuthorityLane == "clause_fusion_agreement.v1" &&
                c.Roles.Any(r => r.EvidenceCodes.Contains("postverbal_reflexive_group")));
            var marker = group.Roles.Single(r => r.EvidenceCodes.Contains("postverbal_reflexive_group"));
            Assert.AreEqual("се", text.Substring(marker.Start, marker.Length), text);
            Assert.IsFalse(group.Selectable, text);
            Assert.IsFalse(group.Roles.Any(r => r.Role == TextContextRole.Subject), text);
            if (noOvertSubject)
            {
                var contextRoles = sentence.SelectedRoles.Concat(sentence.Candidates.SelectMany(c => c.Roles));
                Assert.IsFalse(contextRoles.Any(r => r.Role == TextContextRole.Subject), text);
                Assert.IsFalse(analysis.Syntax!.Sentences.SelectMany(s => s.Candidates)
                    .Any(c => c.Role == TextSyntaxRole.Subject), text);
            }
            Assert.IsFalse(analysis.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"), text);
        }
    }

    [TestMethod]
    [DataRow("Хиляди хора излезе по улиците.", "излезе", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Кметът излязоха пред сградата на общината.", "излязоха", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Децата се върна от училище рано.", "върна", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Учениците от седми клас пише контролна работа.", "пише", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Цените на храните се повиши значително.", "повиши", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Студентите обсъжда проекта си в библиотеката.", "обсъжда", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Аз вчера ходи на кино с приятели.", "ходи", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Майка ми приготвиха вечеря.", "приготвиха", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Новата ресторант на ъгъла отвори врати.", "Новата", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Тя купи нов рокля за празника.", "нов", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Той живее в голям къща край морето.", "голям", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Видях едно красива картина в музея.", "едно", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Тези книга е много интересна.", "Тези", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Новата учител обясни урока.", "Новата", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Кмета излезе пред сградата на общината.", "Кмета", "full_article_expected_for_subject_candidate", "Кметът")]
    [DataRow("Лекаря ми каза да почивам повече.", "Лекаря", "full_article_expected_for_subject_candidate", "Лекарят")]
    [DataRow("Новия ресторант отвори врати миналата седмица.", "Новия", "full_article_expected_for_subject_candidate", "Новият")]
    [DataRow("Влака ще пристигне и утре.", "Влака", "full_article_expected_for_subject_candidate", "Влакът")]
    [DataRow("Директора на училището говори.", "Директора", "full_article_expected_for_subject_candidate", "Директорът")]
    [DataRow("Човека пее.", "Човека", "full_article_expected_for_subject_candidate", "Човекът")]
    [DataRow("Певеца пее.", "Певеца", "full_article_expected_for_subject_candidate", "Певецът")]
    [DataRow("Ученика чете.", "Ученика", "full_article_expected_for_subject_candidate", "Ученикът")]
    [DataRow("Писателя пише писмо.", "Писателя", "full_article_expected_for_subject_candidate", "Писателят")]
    [DataRow("Учителя видя ученика.", "Учителя", "full_article_expected_for_subject_candidate", "Учителят")]
    [DataRow("Учителя поздрави учениците.", "Учителя", "full_article_expected_for_subject_candidate", "Учителят")]
    [DataRow("Писателя пристига.", "Писателя", "full_article_expected_for_subject_candidate", "Писателят")]
    [DataRow("Човека бие тъпана.", "Човека", "full_article_expected_for_subject_candidate", "Човекът")]
    [DataRow("Ех, човека пее, разбира се.", "човека", "full_article_expected_for_subject_candidate", "човекът")]
    [DataRow("Човека пее, нали?", "Човека", "full_article_expected_for_subject_candidate", "Човекът")]
    [DataRow("Ех, малката книга пътувам с мен, разбира се.", "пътувам", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Може би малката книга пътувам с мен.", "пътувам", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Ех, детето е паднал, разбира се.", "паднал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Отборът спечели мачът с два гола.", "мачът", "short_article_expected_after_preposition_candidate", "мача")]
    [DataRow("Вчера видях учителят в парка.", "учителят", "short_article_expected_after_preposition_candidate", "учителя")]
    [DataRow("Книгата е много интересен.", "интересен", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Децата са уморен след дългия ден.", "уморен", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Музеят е отворена всеки ден.", "отворена", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Колата на съседа е паркиран пред входа.", "паркиран", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Те са доволен от резултата.", "доволен", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Децата на съседа са доволен от резултата.", "доволен", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Тя е дошъл вчера вечерта.", "дошъл", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Момчетата са играл футбол цял ден.", "играл", "auxiliary_participle_agreement_mismatch", null)]
    [DataRow("Ние сме видял този филм.", "видял", "auxiliary_participle_agreement_mismatch", null)]
    [DataRow("Моят сестра чете.", "Моят", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Нейният книга е на масата.", "Нейният", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Всичкият вода изтече.", "Всичкият", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Два книги са на масата.", "Два", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Две стола са тук.", "Две", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Двама книги паднаха.", "Двама", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Вие работя тук.", "работя", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Жените влезе в залата.", "влезе", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Мария и Иван дойде.", "дойде", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Малката книга пътувам с мен.", "пътувам", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Малката книга пътувам с мен", "пътувам", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Малката книга 📚 пътуваме с мен.", "пътуваме", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Жените 📚 влезе в залата.", "влезе", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Човека 📚 пее.", "Човека", "full_article_expected_for_subject_candidate", "Човекът")]
    [DataRow("Малкият автобус пристигаме с тях.", "пристигаме", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Малкото книги пътува с мен.", "пътува", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Многото книги пътува с мен.", "пътува", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Повечето книги пътува с мен.", "пътува", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Малкото книга пътува с мен.", "Малкото", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Малкото книгите пътуват с мен.", "Малкото", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Детето е паднал.", "паднал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Жената е дошъл навреме.", "дошъл", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Мъжът е дошла вчера.", "дошла", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Момичето се е върнал.", "върнал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Учителят е написала писмото.", "написала", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Жената е видял лекаря.", "видял", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Малката книга пътувал с мен.", "пътувал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Малката книга 📚 пътувал с мен.", "пътувал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Малката книга пътувал с мен. 📚", "пътувал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Малките книги пътувал с мен.", "пътувал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Той пила вода.", "пила", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Тя пил вода.", "пил", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Малкото дете плакал.", "плакал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Децата играл в двора.", "играл", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Аз пътували с него.", "пътували", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Вие пътувала с тях.", "пътувала", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Тя се усмихнал топло.", "усмихнал", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Старият майстор поправила прозореца.", "поправила", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Малката книга ще пътувал с мен.", "пътувал", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Малката книга ще пътувала с мен.", "пътувала", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Тя ще се върнала рано.", "върнала", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Малката книга да пътувал с мен.", "пътувал", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Тя иска да пътувала с мен.", "пътувала", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Той може да писал вчера.", "писал", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Малката книга пътува с аз.", "аз", "pronoun_case_governance_mismatch", null)]
    [DataRow("Малката книга пътува с той.", "той", "pronoun_case_governance_mismatch", null)]
    [DataRow("Той говори за тя.", "тя", "pronoun_case_governance_mismatch", null)]
    [DataRow("Без аз няма да тръгнат.", "аз", "pronoun_case_governance_mismatch", null)]
    [DataRow("Малката книга пътува с ме.", "ме", "pronoun_case_governance_mismatch", null)]
    [DataRow("Разчитам на му.", "му", "pronoun_case_governance_mismatch", null)]
    [DataRow("Мен пътувам с него.", "Мен", "pronoun_case_governance_mismatch", null)]
    [DataRow("Теб пътуваш с мен.", "Теб", "pronoun_case_governance_mismatch", null)]
    [DataRow("Него пътува с мен.", "Него", "pronoun_case_governance_mismatch", null)]
    [DataRow("Тях пътуват с мен.", "Тях", "pronoun_case_governance_mismatch", null)]
    [DataRow("Няколко книга са на масата.", "Няколко", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Няколко стол са в стаята.", "Няколко", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Няколко дете играят в двора.", "Няколко", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Майстора поправи счупения прозорец за един час.", "Майстора", "full_article_expected_for_subject_candidate", "Майсторът")]
    [DataRow("Майсторът поправи счупеният прозорец.", "счупеният", "short_article_expected_after_preposition_candidate", "счупения")]
    [DataRow("Спирам пред счупеният прозорец.", "счупеният", "short_article_expected_after_preposition_candidate", "счупения")]
    [DataRow("Нова малката книга е тук.", "малката", "nominal_article_on_noninitial_member", null)]
    [DataRow("Проведе се срещи в общината.", "Проведе", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Малката книга беше отворен.", "отворен", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Новата статия е написан добре.", "написан", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Учениците ще подготвеше проекта.", "подготвеше", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Учителят щеше да прегледал тетрадките.", "прегледал", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Тя излезе, без да кажеше дума.", "кажеше", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Малката книга ще пътувайки с мен.", "пътувайки", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Тя иска да написана статията.", "написана", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Авторката е написа нова книга.", "написа", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Охранителят беше види странна сянка.", "види", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Опитният техник би поправи повредата.", "поправи", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Аз щях да прегледа документите.", "прегледа", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Старият автобус нямам да пристигне навреме.", "нямам", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Старият автобус няма да пристигнат навреме.", "пристигнат", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Новият закон трябва да влязат в сила.", "влязат", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Инженерът ще е предала проекта навреме.", "предала", "productive_predicative_agreement_mismatch", null)]
    [DataRow("Комисията биха подкрепил проекта.", "подкрепил", "auxiliary_participle_agreement_mismatch", null)]
    [DataRow("Тази задача биха решил лесно.", "решил", "auxiliary_participle_agreement_mismatch", null)]
    [DataRow("Малката книга пътува с ѝ.", "ѝ", "pronoun_case_governance_mismatch", null)]
    [DataRow("Разчитам на я и работя.", "я", "pronoun_case_governance_mismatch", null)]
    [DataRow("Мъжът я му даде веднага.", "я", "pronoun_case_governance_mismatch", null)]
    [DataRow("Учителят го я даде веднага.", "го", "pronoun_case_governance_mismatch", null)]
    [DataRow("Ме чета интересна книга.", "Ме", "pronoun_case_governance_mismatch", null)]
    [DataRow("Вас чета интересна книга.", "Вас", "pronoun_case_governance_mismatch", null)]
    [DataRow("Старият майсторе поправи прозореца.", "майсторе", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Двама млади лекарю влязоха в кабинета.", "лекарю", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Тези директоре дойдоха.", "директоре", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Двама учителю влязоха.", "учителю", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Отивам при директоре веднага.", "директоре", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Отивам при уважаеми директоре.", "директоре", "productive_attribute_agreement_mismatch", null)]
    [DataRow("Малката книга пътувайки с мен.", "пътувайки", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Малката книга пътувайте с мен.", "пътувайте", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Учителят му я даден веднага.", "даден", "particle_participle_verb_form_mismatch", null)]
    [DataRow("Аз няма време за това.", "няма", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Ние нямаше билети за влака.", "нямаше", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Добрият майстор биха поправили старата ограда.", "биха", "productive_morphology_agreement_mismatch", null)]
    [DataRow("Добрият майстор биха поправили старата ограда.", "поправили", "productive_predicative_agreement_mismatch", null)]
    public void Natural_sentence_fusion_detects_core_grammar_mismatches_across_clause_patterns(
        string text, string expectedTarget, string expectedCode, string? expectedReplacement)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        Assert.IsNotNull(actual.Validation);
        Assert.IsTrue(actual.Validation.Available);

        int expectedStart = text.IndexOf(expectedTarget, StringComparison.Ordinal);
        Assert.IsTrue(expectedStart >= 0, $"Target '{expectedTarget}' not found in '{text}'.");

        TextDiagnostic? match = actual.Diagnostics.FirstOrDefault(d =>
            d.Code == expectedCode && d.Start == expectedStart && d.Length == expectedTarget.Length);
        Assert.IsNotNull(match,
            $"Expected {expectedCode} on '{expectedTarget}' in '{text}', got [{string.Join(", ", actual.Diagnostics.Select(d => $"{d.Code}@{actual.Text.Substring(d.Start, d.Length)}"))}].");

        if (expectedReplacement is not null)
            Assert.AreEqual(expectedReplacement, match.ProjectedText, text);
    }

    [TestMethod]
    [DataRow("Едно от децата играе.")]
    [DataRow("„Децата играе.“")]
    [DataRow("Апостола пристигна.")]
    [DataRow("Учителя го видях вчера.")]
    [DataRow("Два стола стоят до прозореца.")]
    [DataRow("Вчера сутринта заминаваме за морето с влака.")]
    [DataRow("Музеят е отворен всеки ден от девет до пет часа.")]
    [DataRow("Правителството прие новия бюджет след дълги спорове.")]
    [DataRow("Тя работи като учителка в малко селско училище.")]
    [DataRow("Отборът спечели мача с два гола разлика.")]
    [DataRow("Писмото е написал Иван.")]
    [DataRow("Книгата е прочел вчера.")]
    [DataRow("Колата си е купил вчера.")]
    [DataRow("Двама души чакат пред входа.")]
    [DataRow("Всичкият хляб е на масата.")]
    [DataRow("Кучето вие силно през нощта.")]
    [DataRow("Вие си цените времето.")]
    [DataRow("Три дни пътувам с влак.")]
    [DataRow("Малката книга четем с интерес.")]
    [DataRow("Човека бие.")]
    [DataRow("Коня храни.")]
    [DataRow("Коня язди.")]
    [DataRow("Коня ще храни.")]
    [DataRow("Коня ще язди.")]
    [DataRow("Човека ще бие.")]
    [DataRow("Конят храни.")]
    [DataRow("Химна пее.")]
    [DataRow("Романа чете.")]
    [DataRow("Писателя пише.")]
    [DataRow("Работника пише.")]
    [DataRow("Учителя видя Мария.")]
    [DataRow("Ученика поздрави директорката.")]
    [DataRow("Мача играе.")]
    [DataRow("Малкото книги пътуват с мен.")]
    [DataRow("Малкото нови книги пътуват с мен.")]
    [DataRow("Малкото хора пътуват с мен.")]
    [DataRow("Малко хора пътуват с мен.")]
    [DataRow("Немалкото книги останаха тук.")]
    [DataRow("Многото книги пътуват с мен.")]
    [DataRow("Повечето книги пътуват с мен.")]
    [DataRow("Малкото книги останаха.")]
    [DataRow("Малкото суперкниги останаха.")]
    [DataRow("Малката книга 📚 пътува с мен.")]
    [DataRow("Малката книга пътува с мен. 📚")]
    [DataRow("Малката книга пътувала с мен.")]
    [DataRow("Тя пила вода.")]
    [DataRow("До нея имало пила.")]
    [DataRow("Аз пътувал цяла нощ.")]
    [DataRow("Аз пътувала цяла нощ.")]
    [DataRow("Вие пътували до морето.")]
    [DataRow("Писмото написал Иван.")]
    [DataRow("Книгата прочела Мария.")]
    [DataRow("Вратата отворил бавно.")]
    [DataRow("Колкото и да се мъчили чакалите, не успели.")]
    [DataRow("Без аз да знам, те тръгнаха.")]
    [DataRow("Без аз дори да знам, те тръгнаха.")]
    [DataRow("Преди той изобщо да разбере, всички излязоха.")]
    [DataRow("Без аз и ти да знаем, работата приключи.")]
    [DataRow("Преди му го казах.")]
    [DataRow("Ти ще си прочел книгата до утре.")]
    [DataRow("Ти ще си се върнала навреме.")]
    [DataRow("Трябва да си разбрал.")]
    [DataRow("Може да си писал вчера.")]
    [DataRow("Нощната смяна си се изморил много.")]
    [DataRow("Ваканцията си се наспал добре.")]
    [DataRow("И всички сме на ти, ако е въпросът!")]
    [DataRow("В службата говорим на вие.")]
    [DataRow("Песента започва в ми минор.")]
    [DataRow("Него търся от сутринта.")]
    [DataRow("Мен ако питаш, всичко е наред.")]
    [DataRow("Лакоми балтийски чайки прелитаха ниско над сивите плътни води.")]
    [DataRow("Това определя и характерни черти от композиционния му стил, в който преобладават ясна структура, плътна музикална фактура и сложна полифония.")]
    [DataRow("Из окръга се бяха навъдили елени.")]
    [DataRow("Може би знае отговора.")]
    [DataRow("Това бяха може би най-ценните неща.")]
    [DataRow("Да не би да закъснее?")]
    [DataRow("Колкото и да се мъчеше, не успя.")]
    [DataRow("Може и да смятаха така.")]
    [DataRow("Макар краката му още да трепереха, той продължи.")]
    [DataRow("Я ми кажи истината!")]
    [DataRow("Скъпи приятелю, желая ти успех.")]
    [DataRow("Уважаеми директоре, заповядайте.")]
    [DataRow("Драги учителю, благодаря ти.")]
    [DataRow("Благодаря Ви, уважаеми директоре.")]
    [DataRow("Той може да знаеше отговора.")]
    [DataRow("Той трябва да е знаел истината.")]
    [DataRow("Човекът да знаеше какво го чака, нямаше да тръгне.")]
    [DataRow("Въпросът е има ли смисъл.")]
    [DataRow("Вие бихте казали същото.")]
    [DataRow("Вас търси младата учителка.")]
    [DataRow("Малката книга прочети утре!")]
    [DataRow("От а до я и обратно всичко е проверено.")]
    [DataRow("Аз няма да пристигна.")]
    [DataRow("Ти няма да сбъркаш.")]
    [DataRow("Ние няма да закъснеем.")]
    [DataRow("Вие няма да съжалявате.")]
    [DataRow("Аз трябва да тръгна.")]
    [DataRow("Аз трябваше да тръгна.")]
    [DataRow("Ние нямаше да успеем.")]
    [DataRow("Ние нямаше да закъснеем.")]
    [DataRow("Аз не биваше да тръгвам.")]
    [DataRow("Учителят и лекарят няма да закъснеят.")]
    [DataRow("Тя каза, че аз нямаше да пристигна.")]
    [DataRow("Аз няма ли да пристигна?")]
    [DataRow("Да бях знаел по-рано!")]
    [DataRow("Да бях там!")]
    [DataRow("Да беше казал истината!")]
    [DataRow("Да би мирно седяло, не би чудо видяло.")]
    [DataRow("Да са живи и здрави!")]
    [DataRow("Ще ми се да вярвам.")]
    [DataRow("Няма ли кой да помогне?")]
    [DataRow("Кучето вие силно.")]
    [DataRow("Той яде и пи.")]
    [DataRow("Тя ми се струва уморена.")]
    [DataRow("Въпросът е ще дойде ли навреме.")]
    [DataRow("Заминаха, без те дори изобщо спокойно и тихо да се обадят.")]
    [DataRow("Те би трябвало да дойдат навреме.")]
    [DataRow("Аз не биваше да тръгвам толкова рано.")]
    [DataRow("Без съмнение човекът да знаеше истината, щеше да каже.")]
    [DataRow("Без дори шапката да изляза, не смея.")]
    public void Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(string text)
    {
        static bool ActionableWarning(string code) => code is not
            ("full_short_article_check_not_ready" or
             "article_prepositional_group_locally_assessed" or
             "hyphenated_degree_morphology_evidence" or
             "reviewed_word_formation_evidence" or
             "style_repetition_evidence" or
             "contextual_morphology_reading_evidence" or
             "proper_name_exact_morphology_given_name_evidence");

        TextAnalysis actual = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        Assert.IsNotNull(actual.Validation);
        Assert.IsTrue(actual.Validation.Available);
        TextDiagnostic[] warnings = actual.Diagnostics.Where(d => ActionableWarning(d.Code)).ToArray();
        Assert.IsEmpty(warnings,
            $"Unexpected warning(s) on valid/protected sentence '{text}': {string.Join(", ", warnings.Select(w => $"{w.Code}@{actual.Text.Substring(w.Start, w.Length)}"))}.");
    }

    [TestMethod]
    public void Closed_nominal_active_participle_controllers_retain_compatible_matches_after_single_token_repair()
    {
        TextContextOptions options = new() { IncludeGrammarEvidence = true };
        foreach ((string text, string expectedTarget, string expectedRepair) in new[]
        {
            ("Детето е паднал.", "паднал", "паднало"),
            ("Жената е дошъл навреме.", "дошъл", "дошла"),
            ("Мъжът е дошла вчера.", "дошла", "дошъл"),
            ("Момичето се е върнал.", "върнал", "върнало"),
            ("Учителят е написала писмото.", "написала", "написал"),
            ("Жената е видял лекаря.", "видял", "видяла"),
            ("Малката книга пътувал с мен.", "пътувал", "пътувала"),
            ("Децата играл в двора.", "играл", "играли"),
            ("Старият майстор поправила прозореца.", "поправила", "поправил")
        })
        {
            TextAnalysis detailed = session.AnalyzeText(text, false, options, default);
            Assert.IsTrue(detailed.Validation is { Available: true }, text);
            TextDiagnostic diag = detailed.Diagnostics.Single(d => d.Code == "productive_predicative_agreement_mismatch");
            Assert.AreEqual(expectedTarget, text.Substring(diag.Start, diag.Length), text);

            TextGrammarMatch match = detailed.Grammar!.Matches.Single(m => m.RuleId == "grammar.agreement.active_participle_controller.v1");
            Assert.AreEqual(TextGrammarAssessment.Mismatch, match.Assessment, text);
            var edit = match.Repairs.Select(r => r.Edits.Single()).Single(e => e.Replacement == expectedRepair);
            string revised = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
            TextAnalysis after = session.AnalyzeText(revised, false, options, default);
            TextGrammarMatch retained = after.Grammar!.Matches.Single(m =>
                m.RuleId == match.RuleId &&
                m.ControllerTokenIndices.SequenceEqual(match.ControllerTokenIndices) &&
                m.DependentTokenIndices.SequenceEqual(match.DependentTokenIndices));
            Assert.AreEqual(TextGrammarAssessment.Compatible, retained.Assessment, revised);
        }

        TextAnalysis unmeasured = session.AnalyzeText("Аз пътували с него.", false, options, default);
        TextGrammarMatch unmeasuredMatch = unmeasured.Grammar!.Matches.Single(m =>
            m.RuleId == "grammar.agreement.active_participle_controller.v1");
        Assert.IsTrue(unmeasuredMatch.Repairs.Count >= 2);
        foreach (TextGrammarRepair repair in unmeasuredMatch.Repairs)
            CollectionAssert.Contains(repair.Qualifications.ToArray(), "controller_gender_unmeasured");
    }

    [TestMethod]
    public void Declarative_clause_rules_bind_full_constituent_groups_and_subject_article_repairs()
    {
        TextContextOptions options = new() { IncludeGrammarEvidence = true };

        TextAnalysis sv = session.AnalyzeText("Ех, малката книга пътувам с мен, разбира се.", false, options, default);
        TextGrammarMatch svMatch = sv.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1");
        Assert.AreEqual(TextGrammarAssessment.Mismatch, svMatch.Assessment);
        CollectionAssert.AreEqual(new[] { "малката", "книга" },
            svMatch.ControllerTokenIndices.Select(i => sv.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "пътувам" },
            svMatch.DependentTokenIndices.Select(i => sv.Tokens[i].Text).ToArray());

        TextAnalysis coord = session.AnalyzeText("Мария и Иван дойде.", false, options, default);
        TextGrammarMatch coordMatch = coord.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1");
        Assert.AreEqual("coordinated_subject_clause_mismatch", coordMatch.Reason);
        CollectionAssert.AreEqual(new[] { "Мария", "и", "Иван" },
            coordMatch.ControllerTokenIndices.Select(i => coord.Tokens[i].Text).ToArray());

        TextAnalysis art = session.AnalyzeText("Човека пее.", false, options, default);
        TextGrammarMatch artMatch = art.Grammar!.Matches.Single(m => m.RuleId == "grammar.full_article.clause_subject.v1");
        Assert.AreEqual(TextGrammarAssessment.Mismatch, artMatch.Assessment);
        CollectionAssert.AreEqual(new[] { "пее" },
            artMatch.ControllerTokenIndices.Select(i => art.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "Човека" },
            artMatch.DependentTokenIndices.Select(i => art.Tokens[i].Text).ToArray());
        Assert.AreEqual("Човекът", artMatch.Repairs.Single().Edits.Single().Replacement);

        foreach ((string text, string[] expectedController, string expectedDependent) in new[]
        {
            ("Децата играе.", new[] { "Децата" }, "играе"),
            ("Аз чете.", new[] { "Аз" }, "чете"),
            ("Аз ли четем?", new[] { "Аз" }, "четем"),
            ("Той четат.", new[] { "Той" }, "четат"),
            ("В стаята той го четат.", new[] { "той" }, "четат"),
            ("Малката книга 📚 пътуваме с мен.", new[] { "Малката", "книга" }, "пътуваме")
        })
        {
            TextAnalysis exactSv = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch exactSvMatch = exactSv.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1");
            Assert.AreEqual(TextGrammarAssessment.Mismatch, exactSvMatch.Assessment, text);
            CollectionAssert.AreEqual(expectedController,
                exactSvMatch.ControllerTokenIndices.Select(i => exactSv.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedDependent },
                exactSvMatch.DependentTokenIndices.Select(i => exactSv.Tokens[i].Text).ToArray(), text);
        }

        TextAnalysis pronDiag = session.AnalyzeText("Аз ли четем?", false, options, default);
        TextDiagnostic pronWarning = pronDiag.Diagnostics.Single(d => d.Code == "pronoun_verb_agreement_mismatch");
        Assert.AreEqual("pronoun_verb_agreement", pronWarning.RuleId);
        Assert.IsNull(pronWarning.Readings);

        foreach ((string text, string expectedRuleId) in new[]
        {
            ("Той са радостни.", "grammar.agreement.copular_finite.v1"),
            ("Той са паднали.", "grammar.agreement.analytic_finite.v1")
        })
        {
            TextAnalysis predSv = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch[] finiteMismatches = predSv.Grammar!.Matches
                .Where(m => m.Assessment == TextGrammarAssessment.Mismatch &&
                    m.RuleId is "clause_fusion_agreement.v1"
                        or "grammar.agreement.copular_finite.v1"
                        or "grammar.agreement.analytic_finite.v1")
                .ToArray();
            Assert.HasCount(1, finiteMismatches, text);
            Assert.AreEqual(expectedRuleId, finiteMismatches[0].RuleId, text);
        }

        foreach ((string text, string expectedNoun, string expectedNumeral) in new[]
        {
            ("Два книги са на масата.", "книги", "Два"),
            ("Две стола са тук.", "стола", "Две"),
            ("Двама книги паднаха.", "книги", "Двама")
        })
        {
            TextAnalysis num = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch numMatch = num.Grammar!.Matches.Single(m =>
                m.RuleId == "local_exact_attribute_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { expectedNoun },
                numMatch.ControllerTokenIndices.Select(i => num.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedNumeral },
                numMatch.DependentTokenIndices.Select(i => num.Tokens[i].Text).ToArray(), text);
        }

        TextAnalysis quantValid = session.AnalyzeText("Малкото книги пътуват с мен.", false, options, default);
        Assert.IsEmpty(quantValid.Diagnostics);
        TextGrammarMatch quantAttr = quantValid.Grammar!.Matches.Single(m => m.RuleId == "local_exact_attribute_agreement.v1");
        Assert.AreEqual(TextGrammarAssessment.Compatible, quantAttr.Assessment);
        CollectionAssert.AreEqual(new[] { "книги" },
            quantAttr.ControllerTokenIndices.Select(i => quantValid.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "Малкото" },
            quantAttr.DependentTokenIndices.Select(i => quantValid.Tokens[i].Text).ToArray());

        foreach ((string text, string[] expectedController, string expectedDependent) in new[]
        {
            ("Малкото книги пътува с мен.", new[] { "Малкото", "книги" }, "пътува"),
            ("Многото книги пътува с мен.", new[] { "Многото", "книги" }, "пътува"),
            ("Повечето книги пътува с мен.", new[] { "Повечето", "книги" }, "пътува")
        })
        {
            TextAnalysis quantSv = session.AnalyzeText(text, false, options, default);
            Assert.IsFalse(quantSv.Diagnostics.Any(d => d.Code == "productive_attribute_agreement_mismatch"), text);
            TextGrammarMatch quantSvMatch = quantSv.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1");
            Assert.AreEqual(TextGrammarAssessment.Mismatch, quantSvMatch.Assessment, text);
            CollectionAssert.AreEqual(expectedController,
                quantSvMatch.ControllerTokenIndices.Select(i => quantSv.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedDependent },
                quantSvMatch.DependentTokenIndices.Select(i => quantSv.Tokens[i].Text).ToArray(), text);
        }

        TextAnalysis quantSingMismatch = session.AnalyzeText("Малкото книга пътува с мен.", false, options, default);
        TextGrammarMatch quantSingAttr = quantSingMismatch.Grammar!.Matches.Single(m =>
            m.RuleId == "local_exact_attribute_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
        Assert.IsTrue(quantSingAttr.Repairs.SelectMany(r => r.Edits).Any(e => e.Replacement == "Малката"));

        // Non-decorative math/technical symbols still block clause-fusion agreement.
        TextAnalysis mathGap = session.AnalyzeText("Малката книга + пътуваме с мен.", false, options, default);
        Assert.IsFalse(mathGap.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"));

        // Postverbal subject selection on reflexive analytic past perfect ("се бяха навъдили елени").
        TextAnalysis analyticPost = session.AnalyzeText("Из окръга се бяха навъдили елени.", true, NaturalSentenceOptions, default);
        TextSyntaxCandidate postSubj = analyticPost.Syntax!.Sentences.Single().Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
        Assert.AreEqual("елени", analyticPost.Text.Substring(postSubj.Start, postSubj.Length));

        // Compound future-perfect subject <-> participle gender mismatch projects R4 (active_participle_controller), not R3.
        TextAnalysis futPerfSubj = session.AnalyzeText("Инженерът ще е предала проекта навреме.", false, options, default);
        TextGrammarMatch futPerfMatch = futPerfSubj.Grammar!.Matches.Single(m =>
            m.RuleId == "grammar.agreement.active_participle_controller.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
        CollectionAssert.AreEqual(new[] { "Инженерът" },
            futPerfMatch.ControllerTokenIndices.Select(i => futPerfSubj.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "предала" },
            futPerfMatch.DependentTokenIndices.Select(i => futPerfSubj.Tokens[i].Text).ToArray());
        Assert.IsFalse(futPerfSubj.Grammar.Matches.Any(m => m.RuleId == "clause_fusion_agreement.v1"));

        // Compound conditional auxiliary <-> participle mismatch projects R5 (auxiliary_active_participle) without binding fronted direct object as subject.
        TextAnalysis condObj = session.AnalyzeText("Тази задача биха решил лесно.", false, options, default);
        TextGrammarMatch condAuxMatch = condObj.Grammar!.Matches.Single(m =>
            m.RuleId == "grammar.agreement.auxiliary_active_participle.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
        CollectionAssert.AreEqual(new[] { "биха" },
            condAuxMatch.ControllerTokenIndices.Select(i => condObj.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "решил" },
            condAuxMatch.DependentTokenIndices.Select(i => condObj.Tokens[i].Text).ToArray());
        Assert.IsFalse(condObj.Context!.Sentences.Single().Candidates.Any(c =>
            c.AuthorityLane == "clause_fusion_agreement.v1" && c.Roles.Any(r => r.Role == TextContextRole.Subject)));
    }

    [TestMethod]
    [DataRow("Учителят ще подготвеше проекта.", "подготвеше", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "ще|подготвеше")]
    [DataRow("Учителят щеше да прегледал тетрадките.", "прегледал", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "да|прегледал")]
    [DataRow("Тя излезе, без да кажеше дума.", "кажеше", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "да|кажеше")]
    [DataRow("Малката книга ще пътувайки с мен.", "пътувайки", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "ще|пътувайки")]
    [DataRow("Тя иска да написана статията.", "написана", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "да|написана")]
    [DataRow("Авторката е написа нова книга.", "написа", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "е|написа")]
    [DataRow("Охранителят беше види странна сянка.", "види", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "беше|види")]
    [DataRow("Опитният техник би поправи повредата.", "поправи", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "би|поправи")]
    [DataRow("Малката книга пътувайки с мен.", "пътувайки", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "пътувайки")]
    [DataRow("Учителят му я даден веднага.", "даден", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "му|даден")]
    [DataRow("Малката книга пътува с ѝ.", "ѝ", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "с|ѝ")]
    [DataRow("Разчитам на я и работя.", "я", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "на|я")]
    [DataRow("Мъжът я му даде веднага.", "я", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "я|даде")]
    [DataRow("Учителят го я даде веднага.", "го", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "го|даде")]
    [DataRow("Ме чета интересна книга.", "Ме", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Ме|чета")]
    [DataRow("Вас чета интересна книга.", "Вас", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Вас|чета")]
    [DataRow("Тази задача биха решил лесно.", "решил", "auxiliary_participle_agreement_mismatch", "grammar.agreement.auxiliary_active_participle.v1", "биха|решил")]
    [DataRow("Новата статия е написан добре.", "написан", "productive_predicative_agreement_mismatch", "grammar.agreement.passive_participle.v1", "Новата|статия|е|написан")]
    [DataRow("Инженерът ще е предала проекта навреме.", "предала", "productive_predicative_agreement_mismatch", "grammar.agreement.active_participle_controller.v1", "Инженерът|ще|е|предала")]
    [DataRow("Старият майсторе поправи прозореца.", "майсторе", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Старият|майсторе")]
    [DataRow("Двама млади лекарю влязоха в кабинета.", "лекарю", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Двама|лекарю")]
    [DataRow("Тези директоре дойдоха.", "директоре", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Тези|директоре")]
    [DataRow("Двама учителю влязоха.", "учителю", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Двама|учителю")]
    [DataRow("Отивам при директоре веднага.", "директоре", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "при|директоре")]
    [DataRow("Отивам при уважаеми директоре.", "директоре", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "при|директоре")]
    [DataRow("Аз щях да прегледа документите.", "прегледа", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "щях|прегледа")]
    [DataRow("Старият автобус нямам да пристигне навреме.", "нямам", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "нямам|пристигне")]
    [DataRow("Старият автобус няма да пристигнат навреме.", "пристигнат", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Старият|автобус|пристигнат")]
    [DataRow("Новият закон трябва да влязат в сила.", "влязат", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Новият|закон|влязат")]
    [DataRow("Малката книга пътувайте с мен.", "пътувайте", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Малката|книга|пътувайте")]
    [DataRow("Отивам при той, за да попитам.", "той", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "при|той")]
    [DataRow("Инженерът се е уморила вчера.", "уморила", "productive_predicative_agreement_mismatch", "grammar.agreement.active_participle_controller.v1", "Инженерът|се|е|уморила")]
    [DataRow("Младата журналистка би написал отлична статия.", "написал", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Младата|журналистка|би|написал")]
    public void Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_a_families(
        string text, string expectedTarget, string expectedCode, string expectedRuleId, string expectedBoundTokensPipe)
    {
        static bool ActionableWarning(string code) => code is not
            ("full_short_article_check_not_ready" or
             "article_prepositional_group_locally_assessed" or
             "hyphenated_degree_morphology_evidence" or
             "reviewed_word_formation_evidence" or
             "style_repetition_evidence" or
             "contextual_morphology_reading_evidence" or
             "proper_name_exact_morphology_given_name_evidence");

        TextAnalysis actual = session.AnalyzeText(text, false, NaturalSentenceOptions, default);
        Assert.IsNotNull(actual.Validation);
        Assert.IsTrue(actual.Validation.Available);
        Assert.IsNotNull(actual.ReadingInventory);

        TextDiagnostic[] warnings = actual.Diagnostics.Where(d => ActionableWarning(d.Code)).ToArray();
        Assert.HasCount(1, warnings,
            $"Expected exactly 1 actionable warning on '{text}', got [{string.Join(", ", warnings.Select(w => $"{w.Code}@{actual.Text.Substring(w.Start, w.Length)}"))}].");

        TextDiagnostic diag = warnings[0];
        int expectedStart = text.IndexOf(expectedTarget, StringComparison.Ordinal);
        Assert.AreEqual(expectedCode, diag.Code, text);
        Assert.AreEqual(expectedRuleId, diag.RuleId, text);
        Assert.AreEqual(expectedStart, diag.Start, text);
        Assert.AreEqual(expectedTarget.Length, diag.Length, text);
        Assert.IsNull(diag.GraphReadings, text);
        Assert.IsNotNull(diag.Readings, text);
        Assert.IsTrue(diag.Readings.Count > 0, text);

        Dictionary<int, TextReadingOccurrence> occByToken = actual.ReadingInventory.Occurrences
            .ToDictionary(o => o.TokenIndex);
        for (int i = 0; i < diag.Readings.Count; i++)
        {
            TextReadingReference r = diag.Readings[i];
            Assert.IsTrue(occByToken.TryGetValue(r.TokenIndex, out TextReadingOccurrence occ),
                $"Unbound TokenIndex={r.TokenIndex} in '{text}'.");
            Assert.AreEqual(TokenKind.Word, actual.Tokens[r.TokenIndex].Kind, text);
            TextReadingSet set = actual.ReadingInventory.Sets[occ.ReadingSetIndex];
            Assert.IsTrue((uint)r.CompleteReadingIndex < (uint)set.ResolvedReadings.Count,
                $"Out-of-range CompleteReadingIndex={r.CompleteReadingIndex} (count={set.ResolvedReadings.Count}) on '{occ.Text}' in '{text}'.");
            if (i > 0)
            {
                TextReadingReference prev = diag.Readings[i - 1];
                Assert.IsTrue(prev.TokenIndex < r.TokenIndex ||
                    prev.TokenIndex == r.TokenIndex && prev.CompleteReadingIndex < r.CompleteReadingIndex,
                    $"Readings not strictly ordered/distinct at index {i} in '{text}'.");
            }
        }

        string[] actualBoundSurfaces = diag.Readings
            .Select(r => r.TokenIndex)
            .Distinct()
            .Select(ti => actual.Tokens[ti].Text)
            .ToArray();
        CollectionAssert.AreEqual(expectedBoundTokensPipe.Split('|'), actualBoundSurfaces,
            $"Bound token mismatch on '{text}': got [{string.Join("|", actualBoundSurfaces)}].");
    }

    // =========================================================================
    // Package B (B1–B6): Positive Single-Diagnostic + Exact Reading Binding
    // =========================================================================

    [TestMethod]
    // B1a: LocalAttributeAgreementDiagnostics (post-partition unlock & neuter head)
    [DataRow("Тя носи новия чанта до големия магазин.", "новия", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "новия|чанта")]
    [DataRow("Високият момче свири на китара пред блока.", "Високият", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "Високият|момче")]
    // B1b: ClauseFusionAnalyzer multi-modifier, verb-homograph modifier ("дълга"), and determiner+count ("Всички госта")
    [DataRow("В езерото плуват големи шарен риби.", "шарен", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "шарен|риби")]
    [DataRow("Мария написа дълга писмо на своята приятелка.", "дълга", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "дълга|писмо")]
    [DataRow("Всички госта седнаха на масата.", "Всички", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Всички|госта")]
    // B2: Neuter -о adjective / adverb homograph inside governed PP before non-neuter noun
    [DataRow("Той гледа стария филм в тихо стая.", "тихо", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "тихо|стая")]
    // B3a: Non-initial / double article placement (initial NP, postverbal object NP, sentence-initial & mid-sentence PlaceName homographs, prepositional NP)
    [DataRow("Старият майсторът поправи счупения прозорец.", "майсторът", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Старият|майсторът")]
    [DataRow("Старият майстор поправи счупения прозореца.", "прозореца", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "поправи|счупения|прозореца")]
    [DataRow("Нова малката гара се виждаше отдалеч.", "малката", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Нова|малката|гара")]
    [DataRow("Посетихме нова малката гара.", "малката", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Посетихме|нова|малката|гара")]
    [DataRow("Този наш стар приятел живее в съседния градът.", "градът", "nominal_article_on_noninitial_member", "grammar.article.first_prepositional_nominal_member.v1", "в|съседния|градът")]
    // B3b: Vocative intrusions (possessive determiner + vocative; uncommaed initial vocative + 3sg non-2ndPres verb)
    [DataRow("Неговият брате стана известен лекар.", "брате", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Неговият|брате")]
    [DataRow("Майсторе затвори прозореца преди силната буря.", "затвори", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Майсторе|затвори")]
    // B4: Masculine article (dual-paradigm prepositional, "относно", compound/modal direct object, clitic-resumed OVS object, phase/dual-paradigm/bare-participle subjects)
    [DataRow("Птиците летят над краят на гората.", "краят", "short_article_expected_after_preposition_candidate", "grammar.short_article.exact_prepositional_group.v1", "над|краят")]
    [DataRow("Говорихме относно достъпът до новата сграда.", "достъпът", "short_article_expected_after_preposition_candidate", "grammar.short_article.exact_prepositional_group.v1", "относно|достъпът")]
    [DataRow("Опитните работници са завършили новият мост.", "новият", "short_article_expected_after_preposition_candidate", "grammar.short_article.exact_prepositional_group.v1", "завършили|новият|мост")]
    [DataRow("Учителят щеше да прегледа болният пациент.", "болният", "short_article_expected_after_preposition_candidate", "grammar.short_article.exact_prepositional_group.v1", "прегледа|болният|пациент")]
    [DataRow("Счупеният прозорец го поправи старият майстор.", "Счупеният", "short_article_expected_after_preposition_candidate", "grammar.short_article.exact_prepositional_group.v1", "Счупеният|прозорец|поправи")]
    [DataRow("Протеста ще продължи и утре.", "Протеста", "full_article_expected_for_subject_candidate", "grammar.full_article.clause_subject.v1", "Протеста")]
    [DataRow("Чая стана студен, докато говорехме.", "Чая", "full_article_expected_for_subject_candidate", "grammar.full_article.clause_subject.v1", "Чая")]
    [DataRow("Стария мост паднал през студената нощ.", "Стария", "full_article_expected_for_subject_candidate", "grammar.full_article.clause_subject.v1", "Стария")]
    // B5: Count & personal numeral constructions across plural adjectives and "и"-coordination
    [DataRow("Важният договор беше подписан от четирите директори.", "четирите", "personal_numeral_selection_mismatch", "grammar.count.reviewed_numeral_selection.v1", "четирите|директори")]
    [DataRow("Петимата директора подписаха документа.", "директора", "quantified_noun_form_mismatch", "grammar.count.reviewed_noun_form.v1", "Петимата|директора")]
    [DataRow("Двама млади лекари прегледаха трима болни пациента.", "пациента", "quantified_noun_form_mismatch", "grammar.count.reviewed_noun_form.v1", "трима|болни|пациента")]
    [DataRow("В светлия кабинет има два широки стола и три нови компютри.", "компютри", "quantified_noun_form_mismatch", "grammar.count.reviewed_noun_form.v1", "има|три|нови|компютри")]
    // B6: Relative pronoun antecedent gender/number agreement (който, която, което, които, pied-piped preposition)
    [DataRow("Човекът, която дойде вчера, донесе документа.", "която", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Човекът|която")]
    [DataRow("Момчето влезе в стаята, който беше тъмна.", "който", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "стаята|който")]
    [DataRow("Книгата, което чета сега, е интересна.", "което", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Книгата|което")]
    [DataRow("Жените говориха за филма, които гледаха вчера.", "които", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "филма|които")]
    [DataRow("Градът, в която живеем, е много тих.", "която", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Градът|която")]
    public void Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_b_families(
        string text, string expectedTarget, string expectedCode, string expectedRuleId, string expectedBoundTokensPipe)
    {
        Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_a_families(
            text, expectedTarget, expectedCode, expectedRuleId, expectedBoundTokensPipe);
    }

    // =========================================================================
    // Package B: TextGrammarMatch Rules, ProjectedText & Single-Token Repairs
    // =========================================================================

    [TestMethod]
    public void Package_b_nominal_article_and_count_rules_project_exact_grammar_matches_and_repairs()
    {
        TextContextOptions options = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        // 1. B1a LocalAttributeAgreementDiagnostics: R1 match + verified single-token repair
        foreach ((string text, string expectedNoun, string expectedMod, string expectedRepair) in new[]
        {
            ("Тя носи новия чанта до големия магазин.", "чанта", "новия", "новата"),
            ("Високият момче свири на китара пред блока.", "момче", "Високият", "Високото")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == "local_exact_attribute_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { expectedNoun },
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedMod },
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            Assert.IsTrue(match.Repairs.SelectMany(r => r.Edits).Any(e => e.Replacement == expectedRepair), text);
        }

        // 2. B1b & B2 ClauseFusionAnalyzer -> R1 ("local_exact_attribute_agreement.v1") projection in ExplainGrammar
        foreach ((string text, string expectedNoun, string expectedMod) in new[]
        {
            ("В езерото плуват големи шарен риби.", "риби", "шарен"),
            ("Мария написа дълга писмо на своята приятелка.", "писмо", "дълга"),
            ("Всички госта седнаха на масата.", "госта", "Всички"),
            ("Той гледа стария филм в тихо стая.", "стая", "тихо")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == "local_exact_attribute_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { expectedNoun },
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedMod },
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
        }

        // 3. B4 SubjectFull ("grammar.full_article.clause_subject.v1") repairs + ProjectedText
        foreach ((string text, string expectedController, string[] expectedDependents, string expectedRepair) in new[]
        {
            ("Протеста ще продължи и утре.", "продължи", new[] { "Протеста" }, "Протестът"),
            ("Чая стана студен, докато говорехме.", "стана", new[] { "Чая" }, "Чаят"),
            ("Стария мост паднал през студената нощ.", "паднал", new[] { "Стария", "мост" }, "Старият")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextDiagnostic diag = analysis.Diagnostics.Single(d => d.Code == "full_article_expected_for_subject_candidate");
            Assert.AreEqual(expectedRepair, diag.ProjectedText, text);

            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == "grammar.full_article.clause_subject.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { expectedController },
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(expectedDependents,
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            Assert.AreEqual(expectedRepair, match.Repairs.Single().Edits.Single().Replacement, text);
        }

        // 4. B4 & B5 ProjectedText replacements on short-article and numeral/count diagnostics
        foreach ((string text, string expectedCode, string expectedProjectedText) in new[]
        {
            ("Птиците летят над краят на гората.", "short_article_expected_after_preposition_candidate", "края"),
            ("Говорихме относно достъпът до новата сграда.", "short_article_expected_after_preposition_candidate", "достъпа"),
            ("Опитните работници са завършили новият мост.", "short_article_expected_after_preposition_candidate", "новия"),
            ("Счупеният прозорец го поправи старият майстор.", "short_article_expected_after_preposition_candidate", "Счупения"),
            ("Важният договор беше подписан от четирите директори.", "personal_numeral_selection_mismatch", "четиримата"),
            ("Петимата директора подписаха документа.", "quantified_noun_form_mismatch", "директори"),
            ("Двама млади лекари прегледаха трима болни пациента.", "quantified_noun_form_mismatch", "пациенти"),
            ("В светлия кабинет има два широки стола и три нови компютри.", "quantified_noun_form_mismatch", "компютъра")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextDiagnostic diag = analysis.Diagnostics.Single(d => d.Code == expectedCode);
            Assert.AreEqual(expectedProjectedText, diag.ProjectedText, text);
        }
    }

    // =========================================================================
    // Package B Negative Controls — Group 1: Morphological Homograph Abstentions
    // =========================================================================

    [TestMethod]
    // Distinct-lemma / G5-locked homograph ("работник" vs "работника" fem / distinct ParadigmId in G5)
    [DataRow("Работника пише.")]
    // Valid full-articled masculine subject
    [DataRow("Учителят влезе в стаята.")]
    // Paradigm-39 masculine -а/-я noun carrying syncretic Членуван + Пълен on the same token ("бояджията")
    [DataRow("Новият бояджията боядисва стената.")]
    [DataRow("Говорихме с бояджията пред входа.")]
    // Masculine short/count + feminine + PlaceName homograph "врата" (врата fem sg / врат masc short & count / Врата Място:195)
    [DataRow("Малкото дете отвори тежки врата.")]
    // Test-locked non-personalized count construction ("офицер" excluded per CountConstructionPackageTests)
    [DataRow("Видях три офицера.")]
    // P0-6 mid-sentence capitalized proper-name prefix ("Нова Лада") must abstain
    [DataRow("Карам Нова Лада.")]
    public void Package_b_abstains_on_morphological_homographs_and_locked_lexicon_controls(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // Package B Negative Controls — Group 2: Structural & Syntactic Alternatives
    // =========================================================================

    [TestMethod]
    // Sentential relative ", което" referring to matrix clause event (QA B6-A #1)
    [DataRow("Тя каза истината, което ни изненада.")]
    // Extraposed relative clause across intransitive motion / stance verb to preverbal subject (QA B6-A #2)
    [DataRow("Човекът влезе в стаята, който ни търсеше.")]
    [DataRow("Книгата лежеше до моя стол, която търсех.")]
    // Participial modifier with intervening PP argument ("подгизналата в червено стръмнина")
    [DataRow("Спряхме пред подгизналата в червено стръмнина.")]
    // Compound numeral ending in "и три" before male-person plural noun (Gate QA #2f)
    [DataRow("Дойдоха трийсет и три ученици.")]
    // OVS postverbal subject splits that are NOT single object NPs (QA B1-B #1 & #2)
    [DataRow("Вчера провери новата учителката.")]
    [DataRow("В стаята поздрави стария капитанът.")]
    // Coordinated singular nouns under plural determiner ("Моите брат и сестра")
    [DataRow("Моите брат и сестра пристигнаха вчера.")]
    // Adverbialized PP / comparison idioms before bare noun ("За кратко тишина...", "Като цяло въпроси...")
    [DataRow("За кратко тишина настъпи в залата.")]
    [DataRow("Като цяло въпроси не възникнаха.")]
    // Bare short-article masculine temporal NP before phase verb ("Първия ден продължи спокойно.")
    [DataRow("Първия ден продължи спокойно.")]
    public void Package_b_abstains_on_valid_structural_and_syntactic_alternatives(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // Package V (V1–V4): Positive Single-Diagnostic + Exact Reading Binding
    // =========================================================================

    [TestMethod]
    // V1: Definite active -л participle replacing finite predicate (matrix & relative clause)
    [DataRow("Старият майстор поправелата счупения прозорец.", "поправелата", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "майстор|поправелата")]
    [DataRow("Младата учителка прочелата новия роман.", "прочелата", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "учителка|прочелата")]
    [DataRow("Книгата, която прочелата сега, е много интересна.", "прочелата", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "която|прочелата")]
    // V2a: PredicativeGrammarEvidence surgical rival-agreement guards (R5 auxiliary_active_participle & R3b passive_participle)
    [DataRow("Аз съм купили малка къща.", "купили", "auxiliary_participle_agreement_mismatch", "grammar.agreement.auxiliary_active_participle.v1", "съм|купили")]
    [DataRow("Филмът беше заснета в стария град.", "заснета", "productive_predicative_agreement_mismatch", "grammar.agreement.passive_participle.v1", "Филмът|беше|заснета")]
    // V2b: AnalyticPerfect Subject <-> -л Participle (with post-participle object & female Име:207 subject)
    [DataRow("Известният автор е написала чудесна книга.", "написала", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Известният|автор|е|написала")]
    [DataRow("Мария е написал дълго писмо.", "написал", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Мария|е|написал")]
    // V2c: Copular & linking predicative agreement (passive + заради, intensifier + adj, linking остава, polite Вие + vocative)
    [DataRow("Пътят е затворени заради падналите дървета.", "затворени", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Пътят|е|затворени")]
    [DataRow("Новият проект е изключително успешна.", "успешна", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Новият|проект|е|успешна")]
    [DataRow("Първото условие остава напълно валиден.", "валиден", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Първото|условие|остава|валиден")]
    [DataRow("Вие сте поканена на срещата, господин Петров.", "поканена", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Вие|сте|поканена")]
    // V2d: Copula + Aorist/MascSgShort homograph before agentive "от"
    [DataRow("Важният договор беше подписа от двамата директори.", "подписа", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "беше|подписа")]
    // V3a: Bare active -л participle & converb in finite-free domain (1p pronoun, pre-да converb, pre-да 3p gender, relative subject)
    [DataRow("Аз чел интересна книга всяка вечер.", "чел", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "Аз|чел")]
    [DataRow("Младите инженери можейки да поправят сложната машина.", "можейки", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "можейки")]
    [DataRow("Умореният пътник искала да намери тиха стая.", "искала", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Умореният|пътник|искала")]
    [DataRow("Хората, които дошъл навреме, влязоха в залата.", "дошъл", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "които|дошъл")]
    [DataRow("Хората, които стоял отвън, влязоха в залата.", "стоял", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "които|стоял")]
    // V3b: Pure 2pl imperative ("влезте") & bare passive participle ("написан") intrusions in finite-free domain
    [DataRow("Старият автобус влезте навреме на гарата.", "влезте", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Старият|автобус|влезте")]
    [DataRow("Младите ученици написан дълго съчинение.", "написан", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "ученици|написан")]
    // V4a: Transitive 1st/2nd-person finite Subject-Verb mismatch
    [DataRow("Старият майстор поправих счупения прозорец.", "поправих", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Старият|майстор|поправих")]
    // V4b: Personal мога + да + Vfin & inverted се-passive plural subject
    [DataRow("Младите инженери могат да поправя сложната машина.", "поправя", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Младите|инженери|поправя")]
    [DataRow("По този въпрос се води дълги спорове.", "води", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "води|дълги|спорове")]
    // V4c: <BOS> То + 1p Vfin, postverbal unhosted enclitic, saturated oblique subject, hardened <BOS> Я / Ни
    [DataRow("То пиша дълго писмо всяка вечер.", "пиша", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "То|пиша")]
    [DataRow("Учителят извика ме пред целия клас.", "ме", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "извика|ме")]
    [DataRow("Тях строят високата сграда до гарата.", "Тях", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Тях|строят")]
    [DataRow("Я пиша дълго писмо.", "Я", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Я|пиша")]
    [DataRow("Ни пиша дълго писмо.", "Ни", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Ни|пиша")]
    public void Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_v_families(
        string text, string expectedTarget, string expectedCode, string expectedRuleId, string expectedBoundTokensPipe)
    {
        Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_a_families(
            text, expectedTarget, expectedCode, expectedRuleId, expectedBoundTokensPipe);
    }

    // =========================================================================
    // Package V: TextGrammarMatch (R2 / R3a / R3b / R4 / R5) & Verified Repairs
    // =========================================================================

    [TestMethod]
    public void Package_v_predicative_and_clause_rules_project_exact_grammar_matches_and_repairs()
    {
        TextContextOptions options = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        // 1. V2a ("Филмът беше заснета в стария град."): R3b ("grammar.agreement.passive_participle.v1") + verified single-token repair "заснет"
        {
            const string text = "Филмът беше заснета в стария град.";
            TextAnalysis detailed = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = detailed.Grammar!.Matches.Single(m =>
                m.RuleId == "grammar.agreement.passive_participle.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { "Филмът" },
                match.ControllerTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray());
            CollectionAssert.AreEqual(new[] { "заснета" },
                match.DependentTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray());
            TextGrammarEdit edit = match.Repairs.SelectMany(r => r.Edits).Single(e => e.Replacement == "заснет");

            string revised = text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];
            TextAnalysis after = session.AnalyzeText(revised, false, options, default);
            TextGrammarMatch retained = after.Grammar!.Matches.Single(m =>
                m.RuleId == match.RuleId &&
                m.ControllerTokenIndices.SequenceEqual(match.ControllerTokenIndices) &&
                m.DependentTokenIndices.SequenceEqual(match.DependentTokenIndices));
            Assert.AreEqual(TextGrammarAssessment.Compatible, retained.Assessment, revised);
        }

        // 2. V2a ("Аз съм купили малка къща."): R5 ("grammar.agreement.auxiliary_active_participle.v1")
        {
            const string text = "Аз съм купили малка къща.";
            TextAnalysis detailed = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = detailed.Grammar!.Matches.Single(m =>
                m.RuleId == "grammar.agreement.auxiliary_active_participle.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { "съм" },
                match.ControllerTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray());
            CollectionAssert.AreEqual(new[] { "купили" },
                match.DependentTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray());
        }

        // 3. V2b, V2c & V3a: Exact R3a ("copular_adjective"), R3b ("passive_participle"), and R4 ("active_participle_controller") matches
        foreach ((string text, string expectedRuleId, string[] expectedControllers, string expectedDependent) in new[]
        {
            ("Известният автор е написала чудесна книга.", "grammar.agreement.active_participle_controller.v1", new[] { "Известният", "автор" }, "написала"),
            ("Мария е написал дълго писмо.", "grammar.agreement.active_participle_controller.v1", new[] { "Мария" }, "написал"),
            ("Умореният пътник искала да намери тиха стая.", "grammar.agreement.active_participle_controller.v1", new[] { "Умореният", "пътник" }, "искала"),
            ("Пътят е затворени заради падналите дървета.", "grammar.agreement.passive_participle.v1", new[] { "Пътят" }, "затворени"),
            ("Вие сте поканена на срещата, господин Петров.", "grammar.agreement.passive_participle.v1", new[] { "Вие" }, "поканена"),
            ("Новият проект е изключително успешна.", "grammar.agreement.copular_adjective.v1", new[] { "Новият", "проект" }, "успешна"),
            ("Първото условие остава напълно валиден.", "grammar.agreement.copular_adjective.v1", new[] { "Първото", "условие" }, "валиден")
        })
        {
            TextAnalysis detailed = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = detailed.Grammar!.Matches.Single(m =>
                m.RuleId == expectedRuleId && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(expectedControllers,
                match.ControllerTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedDependent },
                match.DependentTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray(), text);
        }

        // 4. V3b & V4a–V4c: Exact R2 ("clause_fusion_agreement.v1") Subject-Predicate matches
        foreach ((string text, string[] expectedControllers, string expectedDependent) in new[]
        {
            ("Старият автобус влезте навреме на гарата.", new[] { "Старият", "автобус" }, "влезте"),
            ("Старият майстор поправих счупения прозорец.", new[] { "Старият", "майстор" }, "поправих"),
            ("Младите инженери могат да поправя сложната машина.", new[] { "Младите", "инженери" }, "поправя"),
            ("По този въпрос се води дълги спорове.", new[] { "дълги", "спорове" }, "води"),
            ("То пиша дълго писмо всяка вечер.", new[] { "То" }, "пиша")
        })
        {
            TextAnalysis detailed = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = detailed.Grammar!.Matches.Single(m =>
                m.RuleId == "clause_fusion_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(expectedControllers,
                match.ControllerTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedDependent },
                match.DependentTokenIndices.Select(i => detailed.Tokens[i].Text).ToArray(), text);
        }
    }

    // =========================================================================
    // Package V Negative Controls — Group 1: Morphological Homograph Abstentions
    // =========================================================================

    [TestMethod]
    // Plural neuter noun "правила" vs FemSg active participle "правила" (правя)
    [DataRow("Тези нови правила са правила за всички.")]
    // Plural neuter noun "сладка" (jam/preserves) / FemSg noun "сладка" (pastry) vs FemSg adj "сладка"
    [DataRow("Ябълките от нашата градина са сладка.")]
    // FemSg noun "пила" (file/saw) vs FemSg active participle "пила" (пия)
    [DataRow("До нея имало пила.")]
    // MascSg noun "чакал" (jackal) vs MascSg active participle "чакал" (чакам)
    [DataRow("До старата къща стоял чакал.")]
    // Indefinite FemSg noun "среща" vs 3sg Present/Aorist verb "среща" before "от" (QA V2-B)
    [DataRow("Това беше среща от миналата година.")]
    // FemSg noun "стая" (room) vs 1sg transitive verb "стая" (conceal) after <BOS> "Ни" / "То"
    [DataRow("Ни стая намери в стария град.")]
    [DataRow("То стая нямаше за всички.")]
    // Definite plural neuter noun "челата" (foreheads, чело) vs definite FemSg participle "челата"
    [DataRow("Те избърсаха челата си след работа.")]
    // Име:207 dual-gender paradigm controls: female "Марта" (chitanka1784 #1614), "Мария" + predicate nominal, & unisex "Саша"
    [DataRow("Марта бе събула обувките си на кушетката.")]
    [DataRow("Мария е преживял много човек.")]
    [DataRow("Саша е написал дълго писмо.")]
    [DataRow("Саша е написала дълго писмо.")]
    public void Package_v_abstains_on_morphological_homographs_and_dual_gender_names(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // Package V Negative Controls — Group 2: Syntactic Valid Alternatives
    // (Includes all 28 VALID_RENARRATIVE_3P controls + QA V1–V4 syntactic traps)
    // =========================================================================

    [TestMethod]
    // --- All 28 3rd-person renarrative controls from perturbation-cases.jsonl (VALID_RENARRATIVE_3P) ---
    [DataRow("Малката книга пътувала с мен.")]
    [DataRow("Старият майстор поправил счупения прозорец.")]
    [DataRow("Младата учителка прочела новия роман.")]
    [DataRow("Малкото дете отворило тежката врата.")]
    [DataRow("Добрите ученици решили трудните задачи.")]
    [DataRow("Майсторът затворил прозореца на стаята.")]
    [DataRow("Той гледал стария филм в тихата стая.")]
    [DataRow("Тя носила новата чанта до големия магазин.")]
    [DataRow("То стояло кротко до високия прозорец.")]
    [DataRow("Те строили високата сграда на широката улица.")]
    [DataRow("Учителят му я дал след последния час.")]
    [DataRow("Малкото момче не пило студена вода.")]
    [DataRow("Директорът не ни приел в новия кабинет.")]
    [DataRow("Неговият брат станал известен лекар.")]
    [DataRow("В голямата зала се провела важна среща.")]
    [DataRow("По този въпрос се водили дълги спорове.")]
    [DataRow("На дървената маса лежала дебела тетрадка.")]
    [DataRow("В стария парк расли високи дървета.")]
    [DataRow("Счупения прозорец го поправил старият майстор.")]
    [DataRow("Учителят и директорът обсъдили новата програма.")]
    [DataRow("Моята сестра и нейната приятелка пристигнали заедно.")]
    [DataRow("Този наш стар приятел живял в съседния град.")]
    [DataRow("Моят по-голям брат работил в новата болница.")]
    [DataRow("Двама млади лекари прегледали трима болни пациенти.")]
    [DataRow("Няколко чужди туристи посетили стария музей.")]
    [DataRow("Авторът на новата статия отговорил на острите критики.")]
    [DataRow("Кутията с червените моливи стояла на горния рафт.")]
    [DataRow("Ключовете от старата къща лежали в малкото чекмедже.")]
    // --- OVS object-relative clauses with postverbal subject & renarrative + passive modifier (chitanka1784 #1762) ---
    [DataRow("Това е всичко, което ги научил Иван.")]
    [DataRow("Това е всичко, което ги научил Олаф.")]
    [DataRow("Додето стигали погледите им, там навред стърчали забити в земята големи бели балвани.")]
    // --- Subordinate / adverbial converb clauses (QA V1-A #1) ---
    [DataRow("Отваряйки вратата, за да влезе, той видя госта.")]
    [DataRow("Той кимна, сякаш съгласявайки се с думите ми.")]
    // --- Contrastive negation before substantivized definite participle subject (QA V1-A #2) ---
    [DataRow("Не подалите документи да изчакат отвън.")]
    // --- Predicate nominal NP with pre-nominal adjective & partitive head (QA V2-A #2) ---
    [DataRow("Тази книга е истинско съкровище.")]
    [DataRow("Част от хората са доволни.")]
    // --- Polite plural "Вие" + plural adjective + singular vocative (БЕРОН § 24.1, QA V2-A #1) ---
    [DataRow("Вие сте спокойни, госпожо.")]
    // --- Sentence-initial bare depictive / secondary predicate before 1p verb (QA V4-A #1) ---
    [DataRow("Сирак израснах на село.")]
    // --- Fronted OVS objects before pro-drop finite verb ---
    [DataRow("Майката и дъщерята покани в салона.")]
    [DataRow("Малката книга четем.")]
    // --- Tonic accusative pronoun in canonical postverbal object position (QA V4-B #1) ---
    [DataRow("Майката видя него до магазина.")]
    // --- Sentence-initial hortative/interrogative particle "Я" & correlative negation "Ни ... нито" (QA V4-B #2) ---
    [DataRow("Я виждаш ли какво става!")]
    [DataRow("Ни мога да спя, нито да ям.")]
    // --- Competing copular / prepositional frames locked in PredicativeAgreement & NominalDiagnostics ---
    [DataRow("Тя е преживял много човек.")]
    [DataRow("Тя е поканен от младата лекар.")]
    [DataRow("Малката книга е много хубав.")]
    // --- Reviewer false-positive guards (pro-drop object relative, negated converb + да, polar То ... ли, correlative я...я, PP-bridged predicate nominal) ---
    [DataRow("Тя върна книгата, която взел от библиотеката.")]
    [DataRow("Не желаейки да му пречи, тя излезе тихо.")]
    [DataRow("То знаеш ли защо ти се обаждам?")]
    [DataRow("Учителят търсеше я тетрадката, я химикалката.")]
    [DataRow("Тя е известен заради падналите камъни алпинист.")]
    public void Package_v_abstains_on_valid_syntactic_alternatives_and_renarratives(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // Package G (G1–G4): Positive Single-Diagnostic + Exact Reading Binding
    // =========================================================================

    [TestMethod]
    // G1: Present-active (-щ), ordinal, possessive-clitic bridge, & vocative head (G1d UsesGraph по-/най- verified with GraphReadings below)
    [DataRow("Настоящата регламент влиза в сила веднага.", "Настоящата", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "Настоящата|регламент")]
    [DataRow("Водещата регламент влиза в сила веднага.", "Водещата", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "Водещата|регламент")]
    [DataRow("Децата се радваха на първата сняг.", "първата", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "първата|сняг")]
    [DataRow("Изтощената му лице бледнееше в тъмнината.", "Изтощената", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "Изтощената|лице")]
    [DataRow("Старият учителю влезе в тихата зала.", "учителю", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Старият|учителю")]
    // G2: Singular-modified masculine -а/-я (Count excluded), postverbal VerbalComplex, & preverbal passive/copular/се
    [DataRow("Важният договора беше подписан вчера.", "договора", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Важният|договора")]
    [DataRow("Тя носи новата чанта до големия магазина.", "магазина", "nominal_article_on_noninitial_member", "grammar.article.first_prepositional_nominal_member.v1", "до|големия|магазина")]
    [DataRow("Опитните работници са завършили новия моста.", "моста", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "завършили|новия|моста")]
    [DataRow("Твоите бележките са на дървеното бюро.", "бележките", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Твоите|бележките")]
    [DataRow("Малкото детето се върна от разходката.", "детето", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Малкото|детето")]
    // G3: Detransitivized се-verb OVS lock, FullMascSg clitic-cluster override, aux-person across adverb, & bare който S-V
    [DataRow("Настоящото решение се прилагам за всички продукти.", "прилагам", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Настоящото|решение|прилагам")]
    [DataRow("Учителят му я дадеш след последния час.", "дадеш", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Учителят|дадеш")]
    [DataRow("Новият проект съм изключително успешен.", "съм", "productive_morphology_agreement_mismatch", "grammar.agreement.copular_finite.v1", "Новият|проект|съм|изключително|успешен")]
    [DataRow("Човекът, който дойдоха вчера, донесе документа.", "дойдоха", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "който|дойдоха")]
    // G4a & G4b: Deictic temporal-tense incompatibility & exhaustive/clause-internal relative agreement
    [DataRow("Вчера гостът ще дойде навреме.", "дойде", "deictic_temporal_tense_mismatch", "grammar.tense.deictic_temporal_adverb.v1", "Вчера|ще|дойде")]
    [DataRow("Утре пристигнах с ранния влак.", "пристигнах", "deictic_temporal_tense_mismatch", "grammar.tense.deictic_temporal_adverb.v1", "Утре|пристигнах")]
    [DataRow("Изключени са уреди, който ползват други източници.", "който", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "уреди|който")]
    [DataRow("Той водеше отряд войници, която са набирани от местното население.", "която", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "войници|която")]
    [DataRow("Той водеше отряд сеймени, която са набирани от местното население.", "която", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "която|са|набирани")]
    public void Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_g_families(
        string text, string expectedTarget, string expectedCode, string expectedRuleId, string expectedBoundTokensPipe)
    {
        Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_a_families(
            text, expectedTarget, expectedCode, expectedRuleId, expectedBoundTokensPipe);
    }

    // =========================================================================
    // Package G: TextGrammarMatch Repairs, ProjectedText & 12-Rule Catalog Pin
    // =========================================================================

    [TestMethod]
    public void Package_g_grammar_and_article_rules_project_exact_matches_repairs_and_12_rule_catalog()
    {
        TextContextOptions options = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        // 1. G4a standalone RuleId preserves NominalGrammarRules.Catalog at 12 rules (zero BgLang.Contracts surface churn)
        TextAnalysis temporal = session.AnalyzeText("Вчера гостът ще дойде навреме.", false, options, default);
        Assert.HasCount(12, temporal.Grammar!.Rules);
        TextDiagnostic temporalDiag = temporal.Diagnostics.Single(d => d.Code == "deictic_temporal_tense_mismatch");
        Assert.AreEqual("grammar.tense.deictic_temporal_adverb.v1", temporalDiag.RuleId);

        // 2. G1 LocalAttributeAgreementDiagnostics: R1 match + verified single-token repair
        foreach ((string text, string expectedNoun, string expectedMod, string expectedRepair) in new[]
        {
            ("Водещата регламент влиза в сила веднага.", "регламент", "Водещата", "Водещият"),
            ("Децата се радваха на първата сняг.", "сняг", "първата", "първия"),
            ("Изтощената му лице бледнееше в тъмнината.", "лице", "Изтощената", "Изтощеното")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == "local_exact_attribute_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { expectedNoun },
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedMod },
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            Assert.IsTrue(match.Repairs.SelectMany(r => r.Edits).Any(e => e.Replacement == expectedRepair), text);
        }

        // 2b. G1d UsesGraph degree modifier (по-/най-): GraphReadings cites по-голяма, Readings cites брат, Validation stays Available
        TextAnalysis degree = session.AnalyzeText("Моят по-голяма брат живее в София.", false, options, default);
        Assert.IsNotNull(degree.Validation);
        Assert.IsTrue(degree.Validation.Available);
        TextDiagnostic degreeDiag = degree.Diagnostics.Single(d => d.Code == "productive_attribute_agreement_mismatch");
        Assert.AreEqual("local_exact_attribute_agreement.v1", degreeDiag.RuleId);
        Assert.AreEqual("по-голяма", degree.Text.Substring(degreeDiag.Start, degreeDiag.Length));
        Assert.IsNotNull(degreeDiag.GraphReadings);
        Assert.IsTrue(degreeDiag.GraphReadings.Count > 0);
        CollectionAssert.AreEqual(new[] { "по-голяма" },
            degreeDiag.GraphReadings.Select(r => degree.Tokens[r.TokenIndex].Text).Distinct().ToArray());
        Assert.IsNotNull(degreeDiag.Readings);
        CollectionAssert.AreEqual(new[] { "брат" },
            degreeDiag.Readings.Select(r => degree.Tokens[r.TokenIndex].Text).Distinct().ToArray());

        // 3. G2 Non-initial article placement: ProjectedText stays null per TextValidationAnalyzer:83-85; TextGrammarMatch binds misplaced token
        foreach ((string text, string expectedRuleId, string expectedMisplacedToken) in new[]
        {
            ("Важният договора беше подписан вчера.", "grammar.article.first_initial_nominal_member.v1", "договора"),
            ("Тя носи новата чанта до големия магазина.", "grammar.article.first_prepositional_nominal_member.v1", "магазина"),
            ("Опитните работници са завършили новия моста.", "grammar.article.first_initial_nominal_member.v1", "моста"),
            ("Твоите бележките са на дървеното бюро.", "grammar.article.first_initial_nominal_member.v1", "бележките"),
            ("Малкото детето се върна от разходката.", "grammar.article.first_initial_nominal_member.v1", "детето")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextDiagnostic diag = analysis.Diagnostics.Single(d => d.Code == "nominal_article_on_noninitial_member");
            Assert.IsNull(diag.ProjectedText, text);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == expectedRuleId && m.Assessment == TextGrammarAssessment.Mismatch);
            Assert.AreEqual(expectedMisplacedToken, analysis.Tokens[match.Conflicts.Single().TokenIndex].Text, text);
            Assert.AreEqual("multi_token_article_repair_not_measured", match.Reason, text);
        }

        // 4. G3 Predicate & copular-auxiliary finite person/number TextGrammarMatch projections
        foreach ((string text, string expectedRuleId, string[] expectedController, string expectedDependent) in new[]
        {
            ("Настоящото решение се прилагам за всички продукти.", "clause_fusion_agreement.v1", new[] { "Настоящото", "решение" }, "прилагам"),
            ("Учителят му я дадеш след последния час.", "clause_fusion_agreement.v1", new[] { "Учителят" }, "дадеш"),
            ("Новият проект съм изключително успешен.", "grammar.agreement.copular_finite.v1", new[] { "Новият", "проект" }, "съм"),
            ("Човекът, който дойдоха вчера, донесе документа.", "clause_fusion_agreement.v1", new[] { "който" }, "дойдоха")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == expectedRuleId && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(expectedController,
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedDependent },
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
        }
    }

    // =========================================================================
    // Package G Corpus-Grounded Negative Controls (Zero Actionable Warnings)
    // =========================================================================

    [TestMethod]
    // G1 Negative Controls: comma-preceded participial clause, post-copular passive, verbal dative clitic
    [DataRow("Те стигнаха до малка рекичка, носеща тежкото име Рубикон.")]
    [DataRow("В големите градове те често са наричани метро.")]
    [DataRow("Учителят му даде интересна книга.")]
    // G2 Negative Controls: locked SOV/OSV substantival-argument splits & valid count forms
    [DataRow("Уморената жената чака.")]
    [DataRow("Старата книгата пази.")]
    [DataRow("Два големи прозореца гледат към парка.")]
    // G3 Negative Controls: embedded да-clause, coordinated Sg+Sg subjects, prepositional relative inversion
    [DataRow("Имаме сериозно намерение да се преместим в друг град.")]
    [DataRow("В очите му недоверието и изумлението се смесваха.")]
    [DataRow("Това е сектор, в който преобладават малки стопанства.")]
    // G4 Negative Controls: presumptive past 'ще е пристигнал', future-in-past 'щеше да', scheduled present 'Утре заминавам', modal object extraction, free-choice relative
    [DataRow("Вчера той сигурно ще е пристигнал късно вечерта.")]
    [DataRow("Вчера щеше да дойде, но влакът закъсня.")]
    [DataRow("Утре сутринта заминавам за Пловдив.")]
    [DataRow("Когато утре дойде директорът, ще обсъдим въпроса.")]
    [DataRow("Превърнаха я в бърсалка за краката, която могат да тъпчат.")]
    [DataRow("Можете да изберете пример от който щете период на историята.")]
    // Gate A 3,195-corpus negative controls: classifier compound noun, substantivized transitive -щ participle + definite DO,
    // invariable quantifier по-малко before definite subject, coordinated PP objects before 'и се Verb', & proper/title PPs
    [DataRow("Това са го признали и най-великите джаз музиканти.")]
    [DataRow("Сказанието остава едно от двете жития, наред със саксонското за Бено и възпяващото фризийския слепец Бернлеф.")]
    [DataRow("Колкото повече въпросите на чужденеца ставаха настойчиви, толкова по-малко откликът им оставаше дружелюбен.")]
    [DataRow("Захвърлих настрана ужасите на ада и рая и се заоглеждах.")]
    [DataRow("Това на практика легитимира създаването на Втората българската държава.")]
    [DataRow("Най-малко единадесет от членове на управляващата Юдея и римската провинция Юдея династия са упоменати в Новия завет.")]
    public void Package_g_abstains_on_corpus_grounded_homographs_and_valid_constructions(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // Package H (H1–H4): Positive Single-Diagnostic + Exact Reading Binding
    // =========================================================================

    [TestMethod]
    // H1: Positional verb-homograph pruning inside NP, Numeral+Mod+Count, paradigm-38 vocative companion, & PP-bridged PresActPart
    [DataRow("Той гледа стария филм в тихите стая.", "тихите", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "тихите|стая")]
    [DataRow("Тя носи новата чанта до големите магазин.", "големите", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "големите|магазин")]
    [DataRow("В светлия кабинет има два широк стола.", "широк", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "два|широк|стола")]
    [DataRow("Мои колега разчита на нея всеки ден.", "Мои", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "Мои|колега")]
    [DataRow("Европейската съдебен мрежа провежда първото събрание.", "съдебен", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "съдебен|мрежа")]
    [DataRow("Той гледа подлежащата на одобрение проект.", "подлежащата", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "подлежащата|проект")]
    // H2: Modal 'мога да' -им pruning & control, Explicit Subject + Aux, Initial-PP се/intr inversion, & PP-modified Subject
    [DataRow("Младите инженери могат да поправим сложната машина.", "поправим", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Младите|инженери|поправим")]
    [DataRow("Могат да поправи сложната машина.", "поправи", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Могат|поправи")]
    [DataRow("Опитните работници сме завършили новия мост.", "сме", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Опитните|работници|сме")]
    [DataRow("В голямата зала се проведа важна среща.", "проведа", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "проведа|важна|среща")]
    [DataRow("Селото, в което живее моите родители, е спокойно.", "живее", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "живее|моите|родители")]
    [DataRow("Авторът на новата статия отговорех на въпросите.", "отговорех", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Авторът|отговорех")]
    // H3: 1p/2p bare -л ('чела' noun homograph), 3p renarrative mismatch, finite-less passive/converb, & deverbal -им/-ите
    [DataRow("Ние чела интересна книга всяка вечер.", "чела", "productive_predicative_agreement_mismatch", "grammar.agreement.active_participle_controller.v1", "Ние|чела")]
    [DataRow("Кутията с червените моливи стояли на горния рафт.", "стояли", "productive_predicative_agreement_mismatch", "clause_fusion_agreement.v1", "Кутията|стояли")]
    [DataRow("Старият майстор поправен счупения прозорец.", "поправен", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "майстор|поправен")]
    [DataRow("Старият майстор поправим счупения прозорец.", "поправим", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Старият|майстор|поправим")]
    [DataRow("Малкото дете отворите тежката врата.", "отворите", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Малкото|дете|отворите")]
    [DataRow("Директорът каза, че новата система работейки отлично.", "работейки", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "система|работейки")]
    // H4: Possessive double-article, Prep + enclitic pronoun, & saturated <BOS> tonic-oblique clash
    [DataRow("Моята сестрата е прочела цялото писмо.", "сестрата", "nominal_article_on_noninitial_member", "grammar.article.first_initial_nominal_member.v1", "Моята|сестрата")]
    [DataRow("Моят колега разчита на ми и работи с тях.", "ми", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "на|ми")]
    [DataRow("Него гледа стария филм в тихата стая.", "Него", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Него|гледа")]
    public void Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_h_families(
        string text, string expectedTarget, string expectedCode, string expectedRuleId, string expectedBoundTokensPipe)
    {
        Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_a_families(
            text, expectedTarget, expectedCode, expectedRuleId, expectedBoundTokensPipe);
    }

    // =========================================================================
    // Package H: TextGrammarMatch Repairs, ProjectedText & H1-LOCK1 Preservation
    // =========================================================================

    [TestMethod]
    public void Package_h_grammar_and_article_rules_project_exact_matches_and_repairs()
    {
        TextContextOptions options = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        // 1. H1 LocalAttributeAgreementDiagnostics: R1 match + verified single-token repair
        foreach ((string text, string expectedNoun, string[] expectedDependents, string expectedConflict, string expectedRepair) in new[]
        {
            ("Той гледа стария филм в тихите стая.", "стая", new[] { "тихите" }, "тихите", "тихата"),
            ("Тя носи новата чанта до големите магазин.", "магазин", new[] { "големите" }, "големите", "големия"),
            ("В светлия кабинет има два широк стола.", "стола", new[] { "широк" }, "широк", "широки"),
            ("Мои колега разчита на нея всеки ден.", "колега", new[] { "Мои" }, "Мои", "Мой"),
            ("Европейската съдебен мрежа провежда първото събрание.", "мрежа", new[] { "Европейската", "съдебен" }, "съдебен", "съдебна")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == "local_exact_attribute_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(new[] { expectedNoun },
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(expectedDependents,
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            Assert.AreEqual(expectedConflict, analysis.Tokens[match.Conflicts.Single().TokenIndex].Text, text);
            Assert.IsTrue(match.Repairs.SelectMany(r => r.Edits).Any(e => e.Replacement == expectedRepair), text);
        }

        // 2. H1-LOCK1: Gender-only postverbal prepositional split remains Ambiguous with 0 actionable warnings
        TextAnalysis lockedSplit = session.AnalyzeText("Говоря с малката автобус.", false, options, default);
        TextGrammarMatch lockedMatch = lockedSplit.Grammar!.Matches.Single(m =>
            m.RuleId == "local_exact_attribute_agreement.v1");
        Assert.AreEqual(TextGrammarAssessment.Ambiguous, lockedMatch.Assessment);

        // 3. H2 & H3c Subject-Predicate TextGrammarMatch projections
        foreach ((string text, string[] expectedController, string expectedDependent) in new[]
        {
            ("Младите инженери могат да поправим сложната машина.", new[] { "Младите", "инженери" }, "поправим"),
            ("Опитните работници сме завършили новия мост.", new[] { "Опитните", "работници" }, "сме"),
            ("В голямата зала се проведа важна среща.", new[] { "важна", "среща" }, "проведа"),
            ("Селото, в което живее моите родители, е спокойно.", new[] { "моите", "родители" }, "живее"),
            ("Авторът на новата статия отговорех на въпросите.", new[] { "Авторът" }, "отговорех"),
            ("Старият майстор поправим счупения прозорец.", new[] { "Старият", "майстор" }, "поправим"),
            ("Малкото дете отворите тежката врата.", new[] { "Малкото", "дете" }, "отворите")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextGrammarMatch match = analysis.Grammar!.Matches.Single(m =>
                m.RuleId == "clause_fusion_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
            CollectionAssert.AreEqual(expectedController,
                match.ControllerTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
            CollectionAssert.AreEqual(new[] { expectedDependent },
                match.DependentTokenIndices.Select(i => analysis.Tokens[i].Text).ToArray(), text);
        }

        // 4. H4a Possessive determiner double-article: ProjectedText stays null per AddArticlePlacement; TextGrammarMatch binds misplaced token
        TextAnalysis possArt = session.AnalyzeText("Моята сестрата е прочела цялото писмо.", false, options, default);
        TextDiagnostic possDiag = possArt.Diagnostics.Single(d => d.Code == "nominal_article_on_noninitial_member");
        Assert.IsNull(possDiag.ProjectedText);
        TextGrammarMatch possMatch = possArt.Grammar!.Matches.Single(m =>
            m.RuleId == "grammar.article.first_initial_nominal_member.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
        Assert.AreEqual("сестрата", possArt.Tokens[possMatch.Conflicts.Single().TokenIndex].Text);
        Assert.AreEqual("multi_token_article_repair_not_measured", possMatch.Reason);
    }

    // =========================================================================
    // Package H Corpus-Grounded & Locked Negative Controls (Zero Warnings)
    // =========================================================================

    [TestMethod]
    [DataRow("Говоря с малката автобус.")]
    [DataRow("Малката книгата описва в детайли градината.")]
    [DataRow("Моите брат и сестра пристигнаха вчера.")]
    [DataRow("Портретите е рисувал Никола Петров.")]
    [DataRow("Цял живот съм преподавала пиано.")]
    [DataRow("Първият успех за клуба са бронзовите медали.")]
    [DataRow("Част от хората са доволни.")]
    [DataRow("Продуктите трябва да идват от одобрени предприятия.")]
    [DataRow("Кутията с червените моливи стояла на горния рафт.")]
    [DataRow("Той обясни, че зеленото било първият слой на картината му.")]
    [DataRow("И всички сме на ти, ако е въпросът!")]
    [DataRow("Него търся цял ден в стария град.")]
    [DataRow("Аз пътувала цяла нощ.")]
    [DataRow("Вас търси младата учителка.")]
    [DataRow("По-новата колега пристигна.")]
    [DataRow("Моята колега е прочела цялото писмо.")]
    [DataRow("Мен чакат братът и сестрата.")]
    [DataRow("Него назначиха директор.")]
    // Gate A 3,195-corpus negative controls: substantival discourse PP 'след това' before [Verb|Noun] 'работи', & fronted reflexive-possessed DO + digit capacity PP
    [DataRow("Установява се в Струмица, където първо отваря частен кабинет, а след това работи като общински лекар.")]
    [DataRow("Домакинските си мачове играе на стадион „Едмон Махтенс“, с вместимост 12 266 зрители.")]
    public void Package_h_abstains_on_corpus_grounded_homographs_and_locked_controls(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // Package I (I1–I4): Positive Single-Diagnostic + Exact Reading Binding
    // =========================================================================

    [TestMethod]
    // I1: Degree-bridged NP, personal count forms after няколко/мнозина, SgMod+няколко, Wackernagel си+баща, & врата
    [DataRow("Моят по-голям брате живее в съседния град.", "брате", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Моят|брате")]
    [DataRow("Моите по-голям брат живее в съседния град.", "Моите", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "Моите|брат")]
    [DataRow("Няколко чужди туриста посетиха стария музей.", "туриста", "quantified_noun_form_mismatch", "grammar.count.reviewed_noun_form.v1", "Няколко|чужди|туриста")]
    [DataRow("Мнозина млади студента останаха в залата.", "студента", "quantified_noun_form_mismatch", "grammar.count.reviewed_noun_form.v1", "Мнозина|млади|студента")]
    [DataRow("Планът за следващата няколко години беше одобрен.", "следващата", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "следващата|години")]
    [DataRow("Той се грижи за родната си баща.", "родната", "productive_attribute_agreement_mismatch", "local_exact_attribute_agreement.v1", "родната|баща")]
    [DataRow("Малкото дете отвори тежките врата.", "тежките", "productive_attribute_agreement_mismatch", "clause_fusion_agreement.v1", "тежките|врата")]
    // I2: Hyphenated compounds, syncretic 2pl Fin/Imp & Fin/DefPlNoun, coordinated NPs/VPs, matrix bridging, & relative antecedent
    [DataRow("Държавите-членки гарантира правата на гражданите.", "гарантира", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "гарантира")]
    [DataRow("Министър-председателят приеха новите правила вчера.", "приеха", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Министър-председателят|приеха")]
    [DataRow("Майсторът затворите прозореца на стаята.", "затворите", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Майсторът|затворите")]
    [DataRow("Директорът не ни приемете в кабинета.", "приемете", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Директорът|приемете")]
    [DataRow("Учителят и директорът обсъди новата програма.", "обсъди", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Учителят|и|директорът|обсъди")]
    [DataRow("Моят колега разчита на нея и работехме с тях.", "работехме", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Моят|колега|работехме")]
    [DataRow("Книгата, която чета сега, са много интересна.", "са", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Книгата|са")]
    [DataRow("Голямата стая до малката кухня, който гледа към двора, е светла.", "който", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "кухня|който")]
    // I3: Clitic-doubled OVS, raising трябва да, initial PP + се + PostNP, & monotransitive theta-saturation
    [DataRow("Счупения прозорец го поправеха старият майстор.", "поправеха", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "поправеха|старият|майстор")]
    [DataRow("Продуктите трябва да идва от проверен източник.", "идва", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Продуктите|идва")]
    [DataRow("По този въпрос се водеше дълги спорове.", "водеше", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "водеше|дълги|спорове")]
    [DataRow("Двама млади лекари прегледам трима болни пациенти.", "прегледам", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Двама|млади|лекари|прегледам")]
    // I4: <BOS> Ми + съм, verbless си, noun-homograph завършил, post-passive saturated PP, & embedded non-finite relative
    [DataRow("Ми съм проверил всеки важен документ.", "Ми", "pronoun_case_governance_mismatch", "clause_fusion_agreement.v1", "Ми|съм")]
    [DataRow("Новата сграда си построена до стария парк.", "си", "productive_morphology_agreement_mismatch", "clause_fusion_agreement.v1", "Новата|сграда|си")]
    [DataRow("Опитните работници са завършил новия мост.", "завършил", "auxiliary_participle_agreement_mismatch", "clause_fusion_agreement.v1", "са|завършил")]
    [DataRow("Новата сграда е построен до стария парк.", "построен", "productive_predicative_agreement_mismatch", "grammar.agreement.passive_participle.v1", "Новата|сграда|е|построен")]
    [DataRow("Книгата, която прочетен сега, е много интересна.", "прочетен", "particle_participle_verb_form_mismatch", "clause_fusion_agreement.v1", "която|прочетен")]
    public void Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_i_families(
        string text, string expectedTarget, string expectedCode, string expectedRuleId, string expectedBoundTokensPipe)
    {
        Natural_sentence_fusion_binds_exact_readings_with_zero_extra_warnings_across_package_a_families(
            text, expectedTarget, expectedCode, expectedRuleId, expectedBoundTokensPipe);
    }

    // =========================================================================
    // Package I: Count Counterparts, Proper-Noun Relative Antecedents & Grammar
    // =========================================================================

    [TestMethod]
    public void Package_i_grammar_and_count_rules_project_exact_matches_and_repairs()
    {
        TextContextOptions options = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        // 1. I1b Personal count-form diagnostics carry exact plural ProjectedText and Validation.Diagnostics parity
        foreach ((string text, string expectedTarget, string expectedPlural) in new[]
        {
            ("Няколко чужди туриста посетиха стария музей.", "туриста", "туристи"),
            ("Мнозина млади студента останаха в залата.", "студента", "студенти")
        })
        {
            TextAnalysis analysis = session.AnalyzeText(text, false, options, default);
            TextDiagnostic countDiag = analysis.Diagnostics.Single(d => d.Code == "quantified_noun_form_mismatch");
            Assert.AreEqual(expectedTarget, text.Substring(countDiag.Start, countDiag.Length), text);
            Assert.AreEqual(expectedPlural, countDiag.ProjectedText, text);
            Assert.IsTrue(analysis.Validation!.Diagnostics.Any(v =>
                v.Code == "quantified_noun_form_mismatch" && v.Start == countDiag.Start &&
                v.Basis.Contains("known_counterpart=" + expectedPlural, StringComparison.Ordinal)), text);
        }

        // 2. I2f Proper-name left-context relative antecedent check (Княжество Андора / Югозападна Европа -> zero MascSg)
        const string andorra = "Княжество Андора е малка държава в Югозападна Европа, който привлича туристи.";
        TextAnalysis relAnalysis = session.AnalyzeText(andorra, false, options, default);
        TextDiagnostic relDiag = relAnalysis.Diagnostics.Single(d => d.Code == "pronoun_case_governance_mismatch");
        Assert.AreEqual("който", andorra.Substring(relDiag.Start, relDiag.Length));
        Assert.AreEqual("clause_fusion_agreement.v1", relDiag.RuleId);

        // 3. I4c Auxiliary-participle R5 and passive-participle R7 TextGrammarMatch projections
        TextAnalysis auxPart = session.AnalyzeText("Опитните работници са завършил новия мост.", false, options, default);
        TextGrammarMatch r5Match = auxPart.Grammar!.Matches.Single(m =>
            m.RuleId == "grammar.agreement.auxiliary_active_participle.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
        CollectionAssert.AreEqual(new[] { "са" }, r5Match.ControllerTokenIndices.Select(i => auxPart.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "завършил" }, r5Match.DependentTokenIndices.Select(i => auxPart.Tokens[i].Text).ToArray());

        TextAnalysis passPart = session.AnalyzeText("Новата сграда е построен до стария парк.", false, options, default);
        TextGrammarMatch r7Match = passPart.Grammar!.Matches.Single(m =>
            m.RuleId == "grammar.agreement.passive_participle.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
        CollectionAssert.AreEqual(new[] { "Новата", "сграда" }, r7Match.ControllerTokenIndices.Select(i => passPart.Tokens[i].Text).ToArray());
        CollectionAssert.AreEqual(new[] { "построен" }, r7Match.DependentTokenIndices.Select(i => passPart.Tokens[i].Text).ToArray());

        // 4. I4c Transitive analytic perfect with noun-homograph 'завършили' projects direct-object short-article replacement
        const string objArtText = "Опитните работници са завършили новият мост.";
        TextAnalysis objArt = session.AnalyzeText(objArtText, false, options, default);
        TextDiagnostic objArtDiag = objArt.Diagnostics.Single(d => d.Code == "short_article_expected_after_preposition_candidate");
        Assert.AreEqual("новият", objArtText.Substring(objArtDiag.Start, objArtDiag.Length));
        Assert.AreEqual("новия", objArtDiag.ProjectedText);
    }

    // =========================================================================
    // Package I Corpus-Grounded P1 Guards & Ceiling Negative Controls (0 FP)
    // =========================================================================

    [TestMethod]
    [DataRow("Моят по-голям брат живее в съседния град.")]
    [DataRow("В тези няколко гимназиални класа имаше отлични ученици.")]
    [DataRow("Изследването отчита няколко рискови фактора.")]
    [DataRow("Тук има три офицера.")]
    [DataRow("Новата колега ще подготви пълния доклад.")]
    [DataRow("Тя протегна приятелски ръка към госта.")]
    [DataRow("Моят приятел и колега пристигна навреме.")]
    [DataRow("Моят колега дойде при нас и работихме заедно.")]
    [DataRow("Тази интересна книга четох целия ден.")]
    [DataRow("Двамата млади лекари прегледахме пациента внимателно.")]
    [DataRow("Държавите-членки гарантират правата на гражданите.")]
    [DataRow("Той закъсня за срещата, което изненада всички колеги.")]
    [DataRow("Княжество Андора е малка държава в Югозападна Европа, която привлича туристи.")]
    [DataRow("В училището се преподават граматика, поезия и реторика.")]
    [DataRow("Първият успех за клуба са бронзовите медали.")]
    [DataRow("Можете да изберете от който щете период.")]
    [DataRow("Тя не е бърсалка за краката, която могат да тъпчат.")]
    [DataRow("Опитните работници си завършили новия мост.")]
    [DataRow("Всички гости бяхме заели свободните места.")]
    [DataRow("Неговият брат стана известния лекар в града.")]
    [DataRow("Ни мога да спя, нито да ям.")]
    [DataRow("Храмът е закрит в 1931 година и превърнат в пощенска станция.")]
    [DataRow("Моят колега дойде при нашата група и работихме заедно.")]
    [DataRow("Книгите, които Мария, вдъхновена от успеха, написа миналата година, спечелиха награда.")]
    [DataRow("Майката, разтревожена от шума, децата, се събудиха веднага.")]
    [DataRow("Колегите обсъдихме новата програма.")]
    [DataRow("Студентките предадохме проектите навреме.")]
    public void Package_i_abstains_on_corpus_grounded_homographs_and_ceiling_controls(string text)
    {
        Natural_sentence_fusion_emits_zero_false_warnings_on_protected_and_valid_controls(text);
    }

    // =========================================================================
    // INTERNAL-R3-CUTOVER-20261116: A1/v2 Matrix, Q1–Q3 Profile & Dispatch Retirement
    // =========================================================================

    [TestMethod]
    public void Internal_r3_cutover_satisfies_a1_single_clause_matrix_with_option_parity_and_retirement()
    {
        using var language = GraphStressAcceptance.Open();
        string expectedPkgId = Environment.GetEnvironmentVariable("BGLANG_GRAPH_STRESS_PROFILE") == "CurrentGraphStress"
            ? "bglang-neutral:9e9a4f441f679542"
            : "bglang-neutral:36b0b69685d6d3e9";
        Assert.AreEqual(expectedPkgId, language.Provenance.PackageId);

        (string CaseId, string Text, string ExpectedVerdict, string ExpectedSubjHead,
         string ExpectedPredOrTarget, string ExpectedControllersPipe, string ExpectedDependent)[] migratedCases =
        [
            // Group A (F1): Pronoun Closed Authority (SV & VS)
            ("R3-C01", "Той чете новия доклад.", "Compatible", "Той", "чете", "", ""),
            ("R3-C02", "Той четат новия доклад.", "Mismatch", "", "четат", "Той", "четат"),
            ("R3-C03", "Ние чете новия доклад.", "Mismatch", "", "чете", "Ние", "чете"),
            ("R3-C04", "Чете той вчера.", "Compatible", "той", "Чете", "", ""),
            ("R3-C05", "Четат той вчера.", "Mismatch", "", "Четат", "той", "Четат"),
            ("R3-C06", "Той поздрави учтиво.", "Compatible", "Той", "поздрави", "", ""),
            // Group B (F3 / F2): Honest Open-Frame Abstention without Negative Closure + Case-Marked MascFull VS
            ("R3-C08", "Новата програма пропаднахме вчера.", "Unassessed", "", "", "", ""),
            ("R3-C11", "Работи учителят.", "Compatible", "учителят", "Работи", "", ""),
            // Group C (F2 / F3 / F4): Pro-Drop VO/OV & Sense-Bound Transitives (incl. 161ii играя trap)
            ("R3-C12", "Прочетохме новия доклад вчера.", "Compatible", "", "Прочетохме", "", ""),
            ("R3-C13", "Новите книги прочетохме вчера.", "Compatible", "", "прочетохме", "", ""),
            ("R3-C14", "Новата програма завършихме вчера.", "Compatible", "", "завършихме", "", ""),
            ("R3-C15", "Новата програма завършиха вчера.", "Compatible", "", "завършиха", "", ""),
            ("R3-C16", "Новата програма започнахме вчера.", "Compatible", "", "започнахме", "", ""),
            ("R3-C17", "Новата роля играхме вчера.", "Unassessed", "", "", "", ""),
            // Group D (F1 / F2 / F3): T2-before-T3 Positive Witnesses & Open-Frame Abstention on боксувам (97217, meaning=[])
            ("R3-C18-F1", "Той боксува вчера.", "Compatible", "Той", "боксува", "", ""),
            ("R3-C18-F2", "Учителят боксува вчера.", "Compatible", "Учителят", "боксува", "", ""),
            ("R3-C19", "Новата програма боксувахме вчера.", "Unassessed", "", "", "", ""),
            ("R3-C20", "Боксувахме новата програма вчера.", "Unassessed", "", "", "", ""),
            ("R3-C21", "Той боксувахме новата програма.", "Unassessed", "", "", "", ""),
            // Group E (F4 / F1): Homographs & Complete Readings (incl. T2-before-T3 on 54304, anti-FilterSlots 73907 & 2sg Imperative)
            ("R3-C22", "Новата програма три вчера.", "Compatible", "", "три", "", ""),
            ("R3-C23", "Новите програми три вчера.", "Unassessed", "", "", "", ""),
            ("R3-C24", "Аз вчера работи тук.", "Mismatch", "", "работи", "Аз", "работи"),
            // Group F (F4): SVO/OVS/SOV/OSV Ambiguity & Exact Participant Provenance
            ("R3-C25", "Майката поздрави дъщерята.", "Compatible", "", "поздрави", "", ""),
            ("R3-C26", "Майката поздравиха дъщерята.", "Ambiguous", "", "", "Майката|дъщерята", "поздравиха"),
            ("R3-C27", "Майката дъщерите поздрави.", "Compatible", "Майката", "поздрави", "", ""),
            ("R3-C27-Base", "Майката дъщерята поздрави.", "Compatible", "", "поздрави", "", "")
        ];

        TextContextOptions[] optionProfiles =
        [
            default,
            new() { IncludeAdditionalRoleHeuristics = true },
            new() { IncludeGrammarEvidence = true, IncludeAdditionalRoleHeuristics = true, IncludeWordConstructions = true }
        ];

        foreach (var (caseId, text, expectedVerdict, expectedSelectedSubjectHead,
                      expectedSelectedOrTargetPredicate, expectedGrammarControllersPipe, expectedGrammarDependent) in migratedCases)
        {
            foreach (TextContextOptions opts in optionProfiles)
            {
                TextAnalysis actual = language.AnalyzeText(text, false, opts, default);
                Assert.IsNotNull(actual.Validation, caseId);
                Assert.IsTrue(actual.Validation.Available, caseId);
                Assert.IsNotNull(actual.ReadingInventory, caseId);

                TextContextSentence ctx = actual.Context!.Sentences.Single();
                TextSyntaxSentence syn = actual.Syntax!.Sentences.Single();

                // Verify dispatch-level retirement of replaced discovery lanes (note: projected public RuleIds
                // closed_exact_agreement.v1 and licensed_personal_pronoun_prefix_agreement.v1 are intentionally preserved).
                Assert.IsFalse(ctx.Candidates.Any(c =>
                    c.AuthorityLane is "local_finite_clause_roles.v1"
                        or "typed_first_second_pronoun_ambiguous_predicate.v1"
                        or "closed_third_person_pronoun_nominal_verb_predicate.v1"
                        or "closed_ambiguous_personal_pronoun_decisive_predicate.v1"
                        or "closed_postverbal_full_article_subject.v1"),
                    $"{caseId}: retired duplicate R3 lane must not execute on owned clause.");

                TextDiagnostic[] r3Diags = actual.Diagnostics
                    .Where(d => d.Code == "productive_morphology_agreement_mismatch")
                    .ToArray();

                if (expectedVerdict == "Compatible")
                {
                    Assert.IsEmpty(r3Diags, caseId);
                    Assert.AreEqual(TextContextSelectionStatus.Selected, ctx.Status, caseId);
                    Assert.AreEqual("Selected", syn.Route, caseId);
                    TextContextCandidate selCand = ctx.Candidates.Single(c => c.AuthorityLane == "clause_fusion_selection.v1");
                    Assert.IsTrue(selCand.Selectable, caseId);
                    Assert.HasCount(1, ctx.Candidates, $"{caseId}: sole R3 selection candidate without parallel discovery.");
                    TextContextRoleSelection predSel = ctx.SelectedRoles.Single(r => r.Role == TextContextRole.Predicate);
                    Assert.AreEqual(expectedSelectedOrTargetPredicate, actual.Tokens[predSel.TokenIndex].Text, caseId);

                    TextContextRoleSelection[] subjSels = ctx.SelectedRoles.Where(r => r.Role == TextContextRole.Subject).ToArray();
                    if (expectedSelectedSubjectHead.Length == 0)
                    {
                        Assert.IsEmpty(subjSels, $"{caseId}: must not invent overt Subject.");
                        Assert.IsFalse(syn.Candidates.Any(c => c.Role == TextSyntaxRole.Subject), caseId);
                    }
                    else
                    {
                        Assert.HasCount(1, subjSels, caseId);
                        Assert.AreEqual(expectedSelectedSubjectHead, actual.Tokens[subjSels[0].TokenIndex].Text, caseId);
                    }

                    if (caseId == "R3-C22")
                    {
                        TextReadingOccurrence triOcc = actual.ReadingInventory.Occurrences.Single(o => o.TokenIndex == predSel.TokenIndex);
                        var triRows = actual.ReadingInventory.Sets[triOcc.ReadingSetIndex].ResolvedReadings;
                        Assert.IsTrue(triRows.Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Numeral), "Complete inventory keeps numeral 'три'.");
                        Assert.IsTrue(triRows.Any(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun),
                            "Complete inventory keeps nominal/toponym homograph 'три'/'Три' (4621).");
                        foreach (TextReadingReference pref in predSel.Readings)
                            Assert.AreEqual("трия", triRows[pref.CompleteReadingIndex].Raw.Lemma, caseId);
                    }
                }
                else if (expectedVerdict == "Mismatch")
                {
                    Assert.HasCount(1, r3Diags, caseId);
                    TextDiagnostic diag = r3Diags[0];
                    CollectionAssert.Contains(
                        new[] { "closed_exact_agreement.v1", "licensed_personal_pronoun_prefix_agreement.v1", "clause_fusion_agreement.v1" },
                        diag.RuleId,
                        caseId);
                    TextContextCandidate soleMismatchCand = ctx.Candidates.Single();
                    CollectionAssert.Contains(soleMismatchCand.EvidenceCodes.ToArray(), "simple_clause_r3_universal_mismatch", caseId);
                    Assert.AreEqual(diag.RuleId, soleMismatchCand.AuthorityLane, caseId);
                    Assert.AreEqual(expectedSelectedOrTargetPredicate, text.Substring(diag.Start, diag.Length), caseId);
                    Assert.IsNotNull(diag.Readings, caseId);
                    Assert.IsNull(diag.GraphReadings, caseId);
                    Assert.AreEqual(TextContextSelectionStatus.Ambiguous, ctx.Status, caseId);
                    Assert.AreEqual("Abstained", syn.Route, caseId);
                    Assert.IsEmpty(ctx.SelectedRoles, caseId);

                    int expectedSubjTok = actual.Tokens
                        .Select((t, idx) => (t, idx))
                        .First(x => x.t.Kind == TokenKind.Word && x.t.Text == expectedGrammarControllersPipe)
                        .idx;
                    int expectedPredTok = actual.Tokens
                        .Select((t, idx) => (t, idx))
                        .First(x => x.t.Kind == TokenKind.Word && x.t.Start == diag.Start)
                        .idx;
                    CollectionAssert.AreEqual(
                        new[] { expectedSubjTok, expectedPredTok }.Order().ToArray(),
                        diag.Readings.Select(r => r.TokenIndex).Distinct().Order().ToArray(),
                        $"{caseId}: exact two-endpoint controller+dependent binding.");

                    foreach (TextReadingReference rref in diag.Readings)
                    {
                        TextReadingOccurrence occ = actual.ReadingInventory.Occurrences.Single(o => o.TokenIndex == rref.TokenIndex);
                        var rows = actual.ReadingInventory.Sets[occ.ReadingSetIndex].ResolvedReadings;
                        Assert.IsTrue((uint)rref.CompleteReadingIndex < (uint)rows.Count, caseId);
                        if (rref.TokenIndex == expectedPredTok)
                            Assert.AreEqual(MorphologicalVerbForm.Finite, rows[rref.CompleteReadingIndex].Features.VerbForm, caseId);
                    }

                    if (opts.IncludeGrammarEvidence)
                    {
                        TextGrammarMatch match = actual.Grammar!.Matches.Single(m =>
                            m.RuleId == "clause_fusion_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
                        CollectionAssert.AreEqual(new[] { expectedGrammarControllersPipe },
                            match.ControllerTokenIndices.Select(i => actual.Tokens[i].Text).ToArray(), caseId);
                        CollectionAssert.AreEqual(new[] { expectedGrammarDependent },
                            match.DependentTokenIndices.Select(i => actual.Tokens[i].Text).ToArray(), caseId);
                    }
                }
                else if (expectedVerdict == "Unassessed")
                {
                    Assert.IsEmpty(r3Diags, caseId);
                    Assert.IsEmpty(ctx.Candidates, $"{caseId}: inconclusive NewR3 clause must emit zero fallback candidates.");
                    Assert.AreEqual(TextContextSelectionStatus.Ambiguous, ctx.Status, caseId);
                    Assert.AreEqual("Abstained", syn.Route, caseId);
                    Assert.IsEmpty(ctx.SelectedRoles, caseId);
                    if (opts.IncludeGrammarEvidence)
                    {
                        Assert.IsFalse(actual.Grammar!.Matches.Any(m => m.RuleId == "clause_fusion_agreement.v1"), caseId);
                        Assert.IsEmpty(actual.Grammar.Matches.SelectMany(m => m.Repairs).ToArray(), caseId);
                    }
                }
                else if (expectedVerdict == "Ambiguous")
                {
                    Assert.IsEmpty(r3Diags, $"{caseId}: T5 disjoint locus must emit zero single-locus R3 diagnostics.");
                    Assert.AreEqual(TextContextSelectionStatus.Ambiguous, ctx.Status, caseId);
                    Assert.AreEqual("Abstained", syn.Route, caseId);
                    Assert.IsEmpty(ctx.SelectedRoles, caseId);
                    if (opts.IncludeGrammarEvidence)
                    {
                        TextGrammarMatch ambig = actual.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1");
                        Assert.AreEqual(TextGrammarAssessment.Ambiguous, ambig.Assessment, caseId);
                        CollectionAssert.AreEqual(expectedGrammarControllersPipe.Split('|'),
                            ambig.ControllerTokenIndices.Select(i => actual.Tokens[i].Text).ToArray(), caseId);
                        CollectionAssert.AreEqual(new[] { expectedGrammarDependent },
                            ambig.DependentTokenIndices.Select(i => actual.Tokens[i].Text).ToArray(), caseId);
                        Assert.IsEmpty(ambig.Repairs, caseId);
                    }
                }
            }
        }
    }

    [TestMethod]
    public void Internal_r3_cutover_preserves_multi_clause_same_rule_isolation_hybrid_r1_and_outside_scope_controls()
    {
        using var language = GraphStressAcceptance.Open();
        string expectedPkgId = Environment.GetEnvironmentVariable("BGLANG_GRAPH_STRESS_PROFILE") == "CurrentGraphStress"
            ? "bglang-neutral:9e9a4f441f679542"
            : "bglang-neutral:36b0b69685d6d3e9";
        Assert.AreEqual(expectedPkgId, language.Provenance.PackageId);
        TextContextOptions fullOpts = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        // 1. R3-C29 (single mismatch): Clause 0 ("Той чете доклада") Compatible + Clause 1 ("но ние чете книгата") Mismatch
        {
            const string singleMix = "Той чете доклада, но ние чете книгата.";
            TextAnalysis a = language.AnalyzeText(singleMix, false, fullOpts, default);
            TextDiagnostic[] r3 = a.Diagnostics.Where(d => d.Code == "productive_morphology_agreement_mismatch").ToArray();
            Assert.HasCount(1, r3);
            Assert.AreEqual((25, 4, "чете", "clause_fusion_agreement.v1"),
                (r3[0].Start, r3[0].Length, singleMix.Substring(r3[0].Start, r3[0].Length), r3[0].RuleId));
            TextGrammarMatch gm = a.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1");
            Assert.AreEqual(TextGrammarAssessment.Mismatch, gm.Assessment);
            CollectionAssert.AreEqual(new[] { "ние" }, gm.ControllerTokenIndices.Select(i => a.Tokens[i].Text).ToArray());
            CollectionAssert.AreEqual(new[] { "чете" }, gm.DependentTokenIndices.Select(i => a.Tokens[i].Text).ToArray());
        }

        // 2. R3-C29 (double mismatch): both clauses emit independent R3 diagnostics and grammar matches
        {
            const string doubleMix = "Той четат доклада, но ние чете книгата.";
            TextAnalysis a = language.AnalyzeText(doubleMix, false, fullOpts, default);
            TextDiagnostic[] r3 = a.Diagnostics.Where(d => d.Code == "productive_morphology_agreement_mismatch").ToArray();
            Assert.HasCount(2, r3);
            Assert.AreEqual("четат", doubleMix.Substring(r3[0].Start, r3[0].Length));
            Assert.AreEqual("чете", doubleMix.Substring(r3[1].Start, r3[1].Length));
            TextGrammarMatch[] gms = a.Grammar!.Matches.Where(m => m.RuleId == "clause_fusion_agreement.v1").ToArray();
            Assert.HasCount(2, gms);
            CollectionAssert.AreEqual(new[] { "Той" }, gms[0].ControllerTokenIndices.Select(i => a.Tokens[i].Text).ToArray());
            CollectionAssert.AreEqual(new[] { "ние" }, gms[1].ControllerTokenIndices.Select(i => a.Tokens[i].Text).ToArray());
        }

        // 3. R3-C30: Broken local NP ("Новият книги прочетохме вчера.") stays R3-owned (0 R3 errors, no retired linker) + preserves R1 across options
        foreach (TextContextOptions opts in new TextContextOptions[]
        {
            default,
            new() { IncludeAdditionalRoleHeuristics = true },
            fullOpts
        })
        {
            const string hybrid = "Новият книги прочетохме вчера.";
            TextAnalysis a = language.AnalyzeText(hybrid, false, opts, default);
            Assert.IsFalse(a.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"));
            Assert.IsFalse(a.Context!.Sentences.Single().Candidates.Any(c => c.AuthorityLane == "local_finite_clause_roles.v1"));
            Assert.AreEqual(TextContextSelectionStatus.Selected, a.Context.Sentences.Single().Status);
            Assert.HasCount(1, a.Context.Sentences.Single().SelectedRoles);
            TextDiagnostic attr = a.Diagnostics.Single(d => d.Code == "productive_attribute_agreement_mismatch");
            Assert.AreEqual("local_exact_attribute_agreement.v1", attr.RuleId);
            Assert.AreEqual("Новият", hybrid.Substring(attr.Start, attr.Length));
        }

        // 4. Q2 SVO heuristic vs Context consensus parity on NewR3-owned clauses ("Момичето удари голям камък.", "Малкото момиче държеше голяма синя топка.")
        //    plus preserved OutsideScope participial-homograph row ("Малкото момиче държеше голяма червена топка.", where 'червена' carries PastPassive 'червя').
        foreach ((string svoText, string expectedSpan, string expectedPred) in new[]
        {
            ("Момичето удари голям камък.", "Момичето", "удари"),
            ("Малкото момиче държеше голяма синя топка.", "Малкото момиче", "държеше")
        })
        {
            TextAnalysis defRes = language.AnalyzeText(svoText, false, new() { IncludeGrammarEvidence = true }, default);
            TextAnalysis heurRes = language.AnalyzeText(svoText, false, fullOpts, default);

            foreach (TextAnalysis res in new[] { defRes, heurRes })
            {
                Assert.IsFalse(res.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"), svoText);
                Assert.IsFalse(res.Grammar!.Matches.Any(m => m.RuleId == "clause_fusion_agreement.v1"), svoText);
                TextContextSentence cSent = res.Context!.Sentences.Single();
                Assert.AreEqual(TextContextSelectionStatus.Selected, cSent.Status, svoText);
                Assert.HasCount(1, cSent.SelectedRoles, $"{svoText}: Context consensus remains strictly Predicate-only.");
                Assert.AreEqual(expectedPred, res.Tokens[cSent.SelectedRoles[0].TokenIndex].Text, svoText);
            }

            Assert.IsFalse(defRes.Syntax!.Sentences.Single().Candidates.Any(c => c.Role == TextSyntaxRole.Subject),
                $"{svoText}: Default options emit zero heuristic SVO Subject.");
            TextSyntaxCandidate heurSubj = heurRes.Syntax!.Sentences.Single().Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
            Assert.AreEqual(expectedSpan, svoText.Substring(heurSubj.Start, heurSubj.Length), svoText);
            Assert.IsTrue(heurSubj.Targets.All(t => t.Basis == "ExactCategory"),
                $"{svoText}: Optional Syntax SVO Subject must carry heuristic ExactCategory basis, never TypedFeatures.");
        }

        {
            const string chervena = "Малкото момиче държеше голяма червена топка.";
            TextAnalysis defCh = language.AnalyzeText(chervena, false, new() { IncludeGrammarEvidence = true }, default);
            TextAnalysis heurCh = language.AnalyzeText(chervena, false, fullOpts, default);
            Assert.IsFalse(defCh.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"));
            Assert.IsFalse(heurCh.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"));
            Assert.IsFalse(defCh.Syntax!.Sentences.Single().Candidates.Any(c => c.Role == TextSyntaxRole.Subject));
            TextSyntaxCandidate heurChSubj = heurCh.Syntax!.Sentences.Single().Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
            Assert.AreEqual("Малкото момиче", chervena.Substring(heurChSubj.Start, heurChSubj.Length));
        }

        // 5. Q1 Prospectively deferred 3p unclosed-valency nominal rows (zero cutover credit; retained sole owner without parallel execution)
        foreach ((string deferredCompat, string expectedPred, string expectedSubjSpan) in new[]
        {
            ("Новата програма пропадна вчера.", "пропадна", "Новата програма"),
            ("Пропадна новата програма вчера.", "Пропадна", "новата програма"),
            ("Новата програма боксува вчера.", "боксува", "Новата програма")
        })
        {
            TextAnalysis a = language.AnalyzeText(deferredCompat, false, fullOpts, default);
            Assert.IsFalse(a.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"), deferredCompat);
            Assert.IsFalse(a.Context!.Sentences.Single().Candidates.Any(c =>
                c.AuthorityLane == "clause_fusion_selection.v1"), deferredCompat);
            TextSyntaxSentence dSyn = a.Syntax!.Sentences.Single();
            TextSyntaxCandidate dPred = dSyn.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate);
            Assert.AreEqual(expectedPred, deferredCompat.Substring(dPred.Start, dPred.Length), deferredCompat);
            TextSyntaxCandidate dSubj = dSyn.Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
            Assert.AreEqual(expectedSubjSpan, deferredCompat.Substring(dSubj.Start, dSubj.Length), deferredCompat);
        }

        foreach ((string deferredMis, string expectedTarget) in new[]
        {
            ("Пропаднаха новата програма вчера.", "Пропаднаха"),
            ("Старите планове рухна днес.", "рухна")
        })
        {
            TextAnalysis a = language.AnalyzeText(deferredMis, false, fullOpts, default);
            Assert.IsFalse(a.Context!.Sentences.Single().Candidates.Any(c =>
                c.EvidenceCodes.Contains("simple_clause_r3_universal_mismatch")), deferredMis);
            TextDiagnostic d = a.Diagnostics.Single(x => x.Code == "productive_morphology_agreement_mismatch");
            Assert.AreEqual(expectedTarget, deferredMis.Substring(d.Start, d.Length), deferredMis);
        }

        // 6. Shared-subject VP coordination ("Учителят дойде и тръгнахме вчера.") stays with ScanSharedSubjectVpCoordinationMismatches (no duplicate discovery)
        {
            const string sharedVp = "Учителят дойде и тръгнахме вчера.";
            TextAnalysis a = language.AnalyzeText(sharedVp, false, fullOpts, default);
            TextDiagnostic[] r3 = a.Diagnostics.Where(d => d.Code == "productive_morphology_agreement_mismatch").ToArray();
            Assert.HasCount(1, r3);
            Assert.AreEqual("тръгнахме", sharedVp.Substring(r3[0].Start, r3[0].Length));
        }

        // 7. R3-OUT-01..08: OutsideScope controls retain existing owners and zero false R3 positives
        foreach ((string outText, string expectedCode, string expectedTarget, string? expectedProj) in new (string, string, string, string?)[]
        {
            ("Учителя видя ученика.", "full_article_expected_for_subject_candidate", "Учителя", "Учителят"),
            ("Учителя поздрави учениците.", "full_article_expected_for_subject_candidate", "Учителя", "Учителят"),
            ("Над езерото кръжи бели птици с дълги човки.", "productive_morphology_agreement_mismatch", "кръжи", null),
            ("Усмихва се щастливите деца пред камерата.", "productive_morphology_agreement_mismatch", "Усмихва", null),
            ("Книгата е много интересен.", "productive_predicative_agreement_mismatch", "интересен", null),
            ("Момчетата са играл футбол цял ден.", "auxiliary_participle_agreement_mismatch", "играл", null),
            ("Мария и Иван дойде.", "productive_morphology_agreement_mismatch", "дойде", null)
        })
        {
            TextAnalysis a = language.AnalyzeText(outText, false, fullOpts, default);
            TextDiagnostic d = a.Diagnostics.Single(x => x.Code == expectedCode);
            Assert.AreEqual(expectedTarget, outText.Substring(d.Start, d.Length), outText);
            Assert.AreEqual(expectedProj, d.ProjectedText, outText);
        }

        foreach (string cleanOut in new[]
        {
            "Над езерото кръжат бели птици.",
            "Разхождаме се край езерото.",
            "Част от програмите пропаднаха вчера.",
            "Майката с дъщерята поздравиха учителя.",
            "Даде го ти.",
            "Дадохме го ти.",
            "Аз чета в парка.",
            "Първият учител работи.",
            "Месецът започна.",
            "Новият месец започна."
        })
        {
            TextAnalysis a = language.AnalyzeText(cleanOut, false, fullOpts, default);
            Assert.IsFalse(a.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"), cleanOut);
        }

        // 8. R3-C32 (F2 / M2a forced-case masculine singular Full mismatch, VS & SV)
        foreach ((string c32Text, string expectedRuleId, string expectedTarget) in new[]
        {
            ("Работят учителят.", "clause_fusion_agreement.v1", "Работят"),
            ("Новият учител работят вчера.", "clause_fusion_agreement.v1", "работят"),
            ("Новият учител работят.", "closed_exact_agreement.v1", "работят")
        })
        {
            TextAnalysis a = language.AnalyzeText(c32Text, false, fullOpts, default);
            Assert.IsTrue(a.Context!.Sentences.Single().Candidates.Any(c =>
                c.EvidenceCodes.Contains("simple_clause_r3_universal_mismatch")), c32Text);
            TextDiagnostic d = a.Diagnostics.Single(x => x.Code == "productive_morphology_agreement_mismatch");
            Assert.AreEqual((expectedRuleId, expectedTarget), (d.RuleId, c32Text.Substring(d.Start, d.Length)), c32Text);
        }

        // 9. PREFIX-TAIL-RETIREMENT & RESIDUAL-MARKED-SUBJECT real-package OutsideScope preservation controls
        foreach ((string retainedOutMis, string expectedRuleId, string expectedTarget) in new[]
        {
            ("Аз четат в парка.", "licensed_personal_pronoun_prefix_agreement.v1", "четат"),
            ("Месецът започнаха.", "closed_exact_agreement.v1", "започнаха"),
            ("Новият месец започнаха.", "closed_exact_agreement.v1", "започнаха"),
            ("Първият учител работят.", "clause_fusion_agreement.v1", "работят")
        })
        {
            TextAnalysis a = language.AnalyzeText(retainedOutMis, false, fullOpts, default);
            Assert.IsFalse(a.Context!.Sentences.Single().Candidates.Any(c =>
                c.EvidenceCodes.Contains("simple_clause_r3_universal_mismatch")), retainedOutMis);
            TextDiagnostic d = a.Diagnostics.Single(x => x.Code == "productive_morphology_agreement_mismatch");
            Assert.AreEqual((expectedRuleId, expectedTarget), (d.RuleId, retainedOutMis.Substring(d.Start, d.Length)), retainedOutMis);
        }

        // 10. DIR-05 comitative animacy, parenthetical comma, modifier/noun homograph, and inclusive 1pl/2pl controls
        TextContextOptions[] dir05Profiles =
        [
            new() { IncludeGrammarEvidence = true },
            fullOpts
        ];

        foreach ((string dir05Mis, string expectedTarget, string expectedBoundPipe) in new[]
        {
            ("Кутията с червените моливи стояха на горния рафт.", "стояха", "Кутията|стояха"),
            ("Договорът с новите условия влязоха в сила.", "влязоха", "Договорът|влязоха"),
            ("Майката с чантата поздравиха учителя.", "поздравиха", "Майката|поздравиха"),
            ("Кутията, с червените моливи, стояха на горния рафт.", "стояха", "Кутията|стояха"),
            ("Бащата, със сина си, влязоха в стаята.", "влязоха", "Бащата|влязоха"),
            ("Бащата, с нея, влязоха в стаята.", "влязоха", "Бащата|влязоха"),
            ("Професорът със син молив влязоха в залата.", "влязоха", "Професорът|влязоха"),
            ("Майката с нея говорихме.", "говорихме", "Майката|говорихме"),
            ("Майката с нея тръгнахме.", "тръгнахме", "Майката|тръгнахме"),
            ("Кутията с мен тръгнахме.", "тръгнахме", "Кутията|тръгнахме")
        })
        {
            foreach (TextContextOptions opts in dir05Profiles)
            {
                TextAnalysis a = language.AnalyzeText(dir05Mis, false, opts, default);
                TextDiagnostic[] misDiags = a.Diagnostics.Where(x => x.Code == "productive_morphology_agreement_mismatch").ToArray();
                Assert.HasCount(1, misDiags, dir05Mis);
                TextDiagnostic d = misDiags[0];
                Assert.AreEqual("clause_fusion_agreement.v1", d.RuleId, dir05Mis);
                Assert.AreEqual(expectedTarget, dir05Mis.Substring(d.Start, d.Length), dir05Mis);
                Assert.AreNotEqual(TextContextSelectionStatus.Selected, a.Context!.Sentences.Single().Status, dir05Mis);
                Assert.AreNotEqual("Selected", a.Syntax!.Sentences.Single().Route, dir05Mis);
                TextGrammarMatch gm = a.Grammar!.Matches.Single(m => m.RuleId == "clause_fusion_agreement.v1" && m.Assessment == TextGrammarAssessment.Mismatch);
                string actualPipe = string.Join('|', gm.ControllerTokenIndices.Concat(gm.DependentTokenIndices).Select(ti => a.Tokens[ti].Text));
                Assert.AreEqual(expectedBoundPipe, actualPipe, dir05Mis);
            }
        }

        foreach (string dir05Clean in new[]
        {
            "Майката с дъщерята поздравиха учителя.",
            "Бащата със сина си влязоха в стаята.",
            "Бащата с младия син влязоха в стаята.",
            "Старият капитан с моряците влязоха в пристанището.",
            "Петър с нея заминаха.",
            "Иван с нея заминаха.",
            "Майката с мен говорихме.",
            "Майката с мен говорихме на събранието.",
            "Майката с мен тръгнахме.",
            "Майката с теб тръгнахте.",
            "Част от учениците дойдоха.",
            "Група от туристи пристигнаха.",
            "Група туристи пристигнаха.",
            "Кутията, с червените моливи, стоеше на горния рафт.",
            "Бащата, със сина си, влезе в стаята.",
            "Майката с дъщерята поздрави учителя."
        })
        {
            foreach (TextContextOptions opts in dir05Profiles)
            {
                TextAnalysis a = language.AnalyzeText(dir05Clean, false, opts, default);
                Assert.IsFalse(a.Diagnostics.Any(d => d.Code == "productive_morphology_agreement_mismatch"), dir05Clean);
            }
        }

        {
            const string barePartMis = "Кутията с червените моливи стояли на горния рафт.";
            TextAnalysis aMis = language.AnalyzeText(barePartMis, false, fullOpts, default);
            TextDiagnostic dMis = aMis.Diagnostics.Single(d => d.Code == "productive_predicative_agreement_mismatch");
            Assert.AreEqual("стояли", barePartMis.Substring(dMis.Start, dMis.Length));

            const string barePartOk = "Кутията с червените моливи стояла на горния рафт.";
            TextAnalysis aOk = language.AnalyzeText(barePartOk, false, fullOpts, default);
            Assert.IsFalse(aOk.Diagnostics.Any(d => d.Code is "productive_predicative_agreement_mismatch" or "productive_morphology_agreement_mismatch"));
        }
    }

    private static bool IsActionablePackageWarning(string code) => code is not
        ("full_short_article_check_not_ready" or
         "article_prepositional_group_locally_assessed" or
         "hyphenated_degree_morphology_evidence" or
         "reviewed_word_formation_evidence" or
         "style_repetition_evidence" or
         "contextual_morphology_reading_evidence" or
         "proper_name_exact_morphology_given_name_evidence" or
         "proper_name_exact_morphology_family_name_evidence" or
         "proper_name_person_candidate");

    [TestMethod]
    public void Wave1a_governed_prepositions_homographs_and_kogoto_parentheticals_hold_on_package()
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
            foreach ((string err, string contrast, string target) in new[]
            {
                ("Заради аз закъсняхме.", "Заради мен закъсняхме.", "аз"),
                ("Закъсняхме поради ти.", "Закъсняхме поради теб.", "ти"),
                ("Поради аз пропуснахме влака.", "Поради теб пропуснахме влака.", "аз"),
                ("Освен те никой не дойде.", "Освен тях никой не дойде.", "те"),
                ("Брат му дойде вместо го.", "Вместо него дойде брат му.", "го"),
                ("Вместо аз дойде брат му.", "Вместо него дойде брат му.", "аз"),
                ("Чрез тя успяхме да решим задачата.", "Чрез нея успяхме да решим задачата.", "тя"),
                ("Въпреки аз, проектът успя.", "Въпреки мен, проектът успя.", "аз"),
                ("Гостите дойдоха преди тя.", "Гостите дойдоха преди нея.", "тя"),
                ("Детето застана край аз.", "Детето застана край мен.", "аз")
            })
            {
                TextAnalysis bad = session.AnalyzeText(err, false, opts, default);
                TextDiagnostic hit = bad.Diagnostics.Single(d => d.Code == "pronoun_case_governance_mismatch");
                Assert.AreEqual(target, err.Substring(hit.Start, hit.Length), err);
                TextDiagnostic[] extraWarnings = bad.Diagnostics
                    .Where(d => IsActionablePackageWarning(d.Code) && d.Code != "pronoun_case_governance_mismatch")
                    .ToArray();
                Assert.IsEmpty(extraWarnings, $"{err}: unexpected warnings [{string.Join(',', extraWarnings.Select(w => w.Code))}]");

                TextAnalysis ok = session.AnalyzeText(contrast, false, opts, default);
                TextDiagnostic[] okWarnings = ok.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.IsEmpty(okWarnings, $"{contrast}: [{string.Join(',', okWarnings.Select(w => w.Code))}]");
            }

            foreach (string clean in new[]
            {
                "Преди му го казах.",
                "Преди го виждах често в библиотеката.",
                "Преди той изобщо да разбере, всички излязоха.",
                "Преди тя работеше в съседното училище.",
                "Разхождаме се край езерото.",
                "Край парка играят децата.",
                "Старият мост над реката беше построен преди сто години.",
                "Край мен минава тиха река.",
                "Преди него влезе учителят.",
                "Настъпи неочакван край.",
                "Този край аз го обичам.",
                "Дали съм първият, когото са довели тук, след като вече са били на това място.",
                "Човекът, когото срещнахме пред гарата, донесе писмото.",
                "Приятелят, с когото говорих вчера, замина.",
                "Гостът, край когото седнах, заговори тихо.",
                "Човекът, когото поканилата го жена видя, се усмихна.",
                "Романът, който чета сега, е много интересен.",
                "И всички сме на ти, ако е въпросът!",
                "В службата говорим на вие.",
                "Песента започва в ми минор.",
                "Него търся от сутринта.",
                "Мен ако питаш, всичко е наред.",
                "Иван и Мария дойдоха рано."
            })
            {
                TextAnalysis ok = session.AnalyzeText(clean, false, opts, default);
                TextDiagnostic[] okWarnings = ok.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.IsEmpty(okWarnings, $"{clean}: [{string.Join(',', okWarnings.Select(w => w.Code))}]");
            }

            TextAnalysis negParticle = session.AnalyzeText("Аз не спиш.", false, opts, default);
            TextDiagnostic negHit = negParticle.Diagnostics.Single(x =>
                x.Code is "productive_morphology_agreement_mismatch" or "pronoun_verb_agreement_mismatch");
            Assert.AreEqual("спиш", "Аз не спиш.".Substring(negHit.Start, negHit.Length));
            if (opts.IncludeGrammarEvidence)
            {
                Assert.IsTrue(negParticle.Context!.Sentences.Single().Candidates.Any(c =>
                    c.EvidenceCodes.Contains("simple_clause_r3_universal_mismatch")),
                    "NegativeParticle 'не' must stay inside SimpleClauseAgreementResolver scope.");
            }

            TextAnalysis bosTe = session.AnalyzeText("Те спи.", false, opts, default);
            TextDiagnostic bosHit = bosTe.Diagnostics.Single(x => x.Code == "productive_morphology_agreement_mismatch");
            Assert.AreEqual("спи", "Те спи.".Substring(bosHit.Start, bosHit.Length));
        }
    }

    [TestMethod]
    public void Wave1b_modal_tense_past_copula_noninitial_te_and_temporal_adjuncts_hold_on_package()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions fullOpts = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        string[] cleanAndAbstention =
        [
            "Аз може да съм от благородно потекло, но истинската лейди е тя.",
            "Младите инженери може да поправят сложната машина.",
            "Младите инженери можеше да поправят сложната машина.",
            "Може да е прав.",
            "Може да е бил прав.",
            "Той може да е бил там.",
            "Новата книга може да прочетем в тихата стая.",
            "Новата книга може да я прочетем в тихата стая.",
            "Да бях останал вкъщи!",
            "Използването му е невъзможно в случай на допусната каквато и да било грешка.",
            "Когото и да било срещнахте, кажете му.",
            "Тази книга се опитвам да прочета.",
            "Новата книга се надявам да прочета.",
            "Тази книга се опитвам да я прочета.",
            "Настоящото решение се опитвам да приложа за всички продукти.",
            "И ето един ден те срещнах на улицата, харесах те и те обикнах с цялото си сърце.",
            "Нейната светлина ти открива пътека, която те извежда от гората, тъкмо когато слънцето изгрява пред теб.",
            "Тук те боли.",
            "Вчера те гледам внимателно.",
            "Известно време вървяха в тишина.",
            "Два часа лежахме в стаята.",
            "Час лежахме.",
            "Деня работихме усилено.",
            "Нощта работихме.",
            "Много години ни учи след като разби първия им опит.",
            "Цял живот съм преподавала пиано.",
            "Тази година зимата дойде необичайно рано.",
            "Той трябва да е знаел истината.",
            "Може да беше прав.",
            "Той може да беше там.",
            "Може и да беше прав.",
            "Той може да знаеше отговора."
        ];

        (string Text, string ExpectedCode, string Target)[] negatives =
        [
            ("Всеки гражданин трябва да спазваше новия закон.", "particle_participle_verb_form_mismatch", "спазваше"),
            ("Всеки гражданин трябва да спазвах новия закон.", "particle_participle_verb_form_mismatch", "спазвах"),
            ("Искам да бях останал вкъщи утре.", "particle_participle_verb_form_mismatch", "бях"),
            ("Той ще бях останал вкъщи.", "particle_participle_verb_form_mismatch", "бях"),
            ("Той ще беше там.", "particle_participle_verb_form_mismatch", "беше"),
            ("Той ще бил там.", "particle_participle_verb_form_mismatch", "бил"),
            ("Той излезе, без да беше казал дума.", "particle_participle_verb_form_mismatch", "беше"),
            ("Новият учител може да прочетем книгата в тихата стая.", "productive_morphology_agreement_mismatch", "прочетем"),
            ("Аз ще може да дойда.", "productive_morphology_agreement_mismatch", "може"),
            ("Те гледам стария филм в тихата стая.", "productive_morphology_agreement_mismatch", "гледам"),
            // Adverb-initial "Вчера, те..." tests HasDirectCommaBetween directly (vocative "Иване, те..." abstains via two-NP shadowing).
            ("Вчера, те гледам внимателно.", "productive_morphology_agreement_mismatch", "гледам"),
            ("Вчера те спи спокойно.", "productive_morphology_agreement_mismatch", "спи"),
            ("Вчера те пристигна.", "productive_morphology_agreement_mismatch", "пристигна"),
            ("Денят лежахме.", "productive_morphology_agreement_mismatch", "лежахме"),
            ("Тъмната нощ паднаха изведнъж.", "productive_morphology_agreement_mismatch", "паднаха"),
            ("Той трябва да знаеше истината.", "particle_participle_verb_form_mismatch", "знаеше"),
            ("Моят приятел се опитвам да прочета.", "productive_morphology_agreement_mismatch", "опитвам"),
            ("Тази книга се опитвам да вляза.", "productive_morphology_agreement_mismatch", "опитвам"),
            ("Настоящото решение се опитвам да приложа новия закон.", "productive_morphology_agreement_mismatch", "опитвам"),
            ("Тази книга се опитвам да го прочета.", "productive_morphology_agreement_mismatch", "опитвам")
        ];

        foreach (TextContextOptions opts in new TextContextOptions[] { default, fullOpts })
        {
            foreach (string text in cleanAndAbstention)
            {
                TextAnalysis ok = session.AnalyzeText(text, false, opts, default);
                TextDiagnostic[] warnings = ok.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.IsEmpty(warnings, $"{text}: [{string.Join(',', warnings.Select(w => w.Code))}]");
            }

            foreach ((string text, string expectedCode, string target) in negatives)
            {
                TextAnalysis bad = session.AnalyzeText(text, false, opts, default);
                TextDiagnostic hit = bad.Diagnostics.Single(d => d.Code == expectedCode);
                Assert.AreEqual(target, text.Substring(hit.Start, hit.Length), text);
            }
        }
    }

    [TestMethod]
    public void Post_alpha1_prepositional_pronoun_and_surname_agreement_hold_on_package()
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
            // Item 1: Preposition-governed wrong nominative pronoun warns ONLY on the pronoun, never on the verb.
            foreach ((string err, string pronTarget, string contrast) in new[]
            {
                ("Заради аз закъсняхме.", "аз", "Заради мен закъсняхме."),
                ("Без ти тръгнахме.", "ти", "Без теб тръгнахме."),
                ("Вместо той дойдохме.", "той", "Вместо него дойдохме."),
                ("При тя останахме.", "тя", "При нея останахме."),
                ("С ние говориха.", "ние", "С нас говориха.")
            })
            {
                TextAnalysis bad = session.AnalyzeText(err, false, opts, default);
                TextDiagnostic[] warnings = bad.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.HasCount(1, warnings, $"{err}: [{string.Join(',', warnings.Select(w => $"{w.Code}@{err.Substring(w.Start, w.Length)}"))}]");
                Assert.AreEqual("pronoun_case_governance_mismatch", warnings[0].Code, err);
                Assert.AreEqual(pronTarget, err.Substring(warnings[0].Start, warnings[0].Length), err);

                TextAnalysis ok = session.AnalyzeText(contrast, false, opts, default);
                TextDiagnostic[] okWarnings = ok.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.IsEmpty(okWarnings, $"{contrast}: [{string.Join(',', okWarnings.Select(w => w.Code))}]");
            }

            // Explicit mismatched subjects outside the PP are still flagged.
            {
                const string explicitSubjAfterPp = "Заради мен аз закъсняхме.";
                TextAnalysis a = session.AnalyzeText(explicitSubjAfterPp, false, opts, default);
                TextDiagnostic[] warnings = a.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                TextDiagnostic verbWarn = warnings.Single();
                Assert.AreEqual("productive_morphology_agreement_mismatch", verbWarn.Code);
                Assert.AreEqual("закъсняхме", explicitSubjAfterPp.Substring(verbWarn.Start, verbWarn.Length));
            }
            {
                const string bothMismatched = "Заради аз ние закъсня.";
                TextAnalysis a = session.AnalyzeText(bothMismatched, false, opts, default);
                TextDiagnostic[] warnings = a.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.HasCount(2, warnings, $"{bothMismatched}: [{string.Join(',', warnings.Select(w => w.Code))}]");
                Assert.IsTrue(warnings.Any(d => d.Code == "pronoun_case_governance_mismatch" && bothMismatched.Substring(d.Start, d.Length) == "аз"));
                Assert.IsTrue(warnings.Any(d => d.Code == "productive_morphology_agreement_mismatch" && bothMismatched.Substring(d.Start, d.Length) == "закъсня"));
            }
            {
                const string prePpExplicitSubj = "Той заради мен закъсняхме.";
                TextAnalysis a = session.AnalyzeText(prePpExplicitSubj, false, opts, default);
                TextDiagnostic verbWarn = a.Diagnostics.Single(d => IsActionablePackageWarning(d.Code));
                Assert.AreEqual("productive_morphology_agreement_mismatch", verbWarn.Code);
                Assert.AreEqual("закъсняхме", prePpExplicitSubj.Substring(verbWarn.Start, verbWarn.Length));
            }

            // Item 2: Surname as subject (OOV -ов/-ова/-ови, dictionary FamilyName, titles, given+surname, plural, coordinated).
            {
                const string compoundTarget = "Чорбанова четат на червена пейката.";
                TextAnalysis res = session.AnalyzeText(compoundTarget, false, opts, default);
                TextDiagnostic[] warnings = res.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.IsTrue(warnings.Any(d => d.Code == "productive_morphology_agreement_mismatch" && compoundTarget.Substring(d.Start, d.Length) == "четат"), compoundTarget);
                Assert.IsTrue(warnings.Any(d => d.Code == "nominal_article_on_noninitial_member" && compoundTarget.Substring(d.Start, d.Length) == "пейката"), compoundTarget);
            }

            static bool IsSurnameTestWarning(string text, TextDiagnostic d) =>
                IsActionablePackageWarning(d.Code) &&
                !(d.Code == "spelling_unknown_word_unique_distance_one_candidate" &&
                  (uint)d.Start < (uint)text.Length &&
                  char.IsUpper(text[d.Start]));

            foreach ((string err, string targetVerb) in new[]
            {
                ("Чорбанова четат.", "четат"),
                ("Чорбанов четат.", "четат"),
                ("Каравелова четат.", "четат"),
                ("Дебелянов четат.", "четат"),
                ("Петрова четат.", "четат"),
                ("Колева спят.", "спят"),
                ("Иванов пристигнаха.", "пристигнаха"),
                ("Мария Чорбанова четат.", "четат"),
                ("Г-н Чорбанов четат.", "четат"),
                ("Г-жа Чорбанова четат.", "четат"),
                ("Д-р Петрова четат.", "четат"),
                ("Г-н Петров четат.", "четат"),
                ("Днес г-н Чорбанов четат.", "четат"),
                ("Днес г-жа Чорбанова четат.", "четат"),
                ("Днес д-р Петрова четат.", "четат"),
                ("Днес г-н Петров четат.", "четат"),
                ("Чорбанови чете.", "чете"),
                ("Петрови чете.", "чете"),
                ("Чорбанова и Петрова чете.", "чете")
            })
            {
                TextAnalysis bad = session.AnalyzeText(err, false, opts, default);
                TextDiagnostic[] warnings = bad.Diagnostics.Where(d => IsSurnameTestWarning(err, d)).ToArray();
                TextDiagnostic hit = warnings.SingleOrDefault(d =>
                    d.Code == "productive_morphology_agreement_mismatch" &&
                    err.Substring(d.Start, d.Length) == targetVerb)!;
                Assert.IsNotNull(hit, $"{err}: got [{string.Join(',', warnings.Select(w => $"{w.Code}@{err.Substring(w.Start, w.Length)}"))}]");
                Assert.HasCount(1, warnings, $"{err}: unexpected warnings [{string.Join(',', warnings.Select(w => $"{w.Code}@{err.Substring(w.Start, w.Length)}"))}]");
            }

            foreach (string clean in new[]
            {
                "Чорбанова чете.",
                "Чорбанова чете на червената пейка.",
                "Чорбанови четат.",
                "Петрови четат.",
                "Чорбанова и Петрова четат.",
                "Мария Чорбанова и Иван Петров четат.",
                "Хаджийски чете.",
                "Хаджийски четат.",
                "Чорбанова къща се вижда.",
                "Чорбанови книги лежат на масата.",
                "Иван пътуваме с теб.",
                "Тази шапка по ти отива.",
                "Преди те видях на гарата."
            })
            {
                TextAnalysis ok = session.AnalyzeText(clean, false, opts, default);
                TextDiagnostic[] okWarnings = ok.Diagnostics.Where(d => IsSurnameTestWarning(clean, d)).ToArray();
                Assert.IsEmpty(okWarnings, $"{clean}: [{string.Join(',', okWarnings.Select(w => $"{w.Code}@{clean.Substring(w.Start, w.Length)}"))}]");
            }

            // Item 4 (GPTpro Priority 1): subordinate "че/защото + pronoun + ще" hands off future complexes cleanly to ClauseFusion.
            foreach ((string err, string targetVerb) in new[]
            {
                ("Знам, че аз ще закъснеем.", "закъснеем"),
                ("Ще остана, защото ние ще работиш.", "работиш"),
                ("Знам, че тя ще го видят.", "видят")
            })
            {
                TextAnalysis bad = session.AnalyzeText(err, false, opts, default);
                TextDiagnostic[] warnings = bad.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                TextDiagnostic hit = warnings.SingleOrDefault(d =>
                    d.Code == "productive_morphology_agreement_mismatch" &&
                    d.RuleId == "clause_fusion_agreement.v1" &&
                    err.Substring(d.Start, d.Length) == targetVerb)!;
                Assert.IsNotNull(hit, $"{err}: got [{string.Join(',', warnings.Select(w => $"{w.Code}:{w.RuleId}@{err.Substring(w.Start, w.Length)}"))}]");
                Assert.HasCount(1, warnings, $"{err}: unexpected duplicate or extra warnings");
                if (opts.IncludeGrammarEvidence)
                {
                    Assert.IsEmpty(bad.Context!.Sentences.SelectMany(s => s.Candidates)
                        .Where(c => c.AuthorityLane == "licensed_subordinate_pronoun_prefix_agreement.v1"),
                        $"{err}: SubordinatePronounPrefixLane must not claim future-marked complexes");
                }
            }

            foreach (string cleanFuture in new[]
            {
                "Знам, че аз ще закъснея.",
                "Ще остана, защото ние ще работим.",
                "Знам, „че аз ще закъснеем“."
            })
            {
                TextAnalysis ok = session.AnalyzeText(cleanFuture, false, opts, default);
                TextDiagnostic[] okWarnings = ok.Diagnostics.Where(d => IsActionablePackageWarning(d.Code)).ToArray();
                Assert.IsEmpty(okWarnings, $"{cleanFuture}: [{string.Join(',', okWarnings.Select(w => $"{w.Code}@{cleanFuture.Substring(w.Start, w.Length)}"))}]");
            }
        }
    }

    [TestMethod]
    public void Post_alpha1_surname_and_name_predicative_presentation_vs_subject_agreement()
    {
        using var session = GraphStressAcceptance.Open();
        TextContextOptions fullOpts = new()
        {
            IncludeGrammarEvidence = true,
            IncludeAdditionalRoleHeuristics = true,
            IncludeWordConstructions = true,
            IncludeNominalConstructions = true
        };

        string[] cleanPresentation =
        [
            "Чорбанова се казвам.",
            "Мария се казвам.",
            "Иван Петров се казвам.",
            "Г-жа Чорбанова се казвам.",
            "Чорбанова се наричам.",
            "Петрова се зова.",
            "Чорбанова се именувам.",
            "Чорбанова се пиша.",
            "Чорбанова се казвах.",
            "Чорбанова ще се казвам.",
            "Чорбанова съм се казвала.",
            "Чорбанова щях да се казвам.",
            "Чорбанова може да се казвам.",
            "Казвам се Чорбанова.",
            "Аз се казвам Чорбанова.",
            "Чорбанова съм аз.",
            "Чорбанови сме ние.",
            "Като Чорбанова се представям.",
            "С фамилия Чорбанова се представям.",
            "С име Мария се представям."
        ];

        (string Text, string TargetVerb)[] mismatches =
        [
            ("Чорбанова четат.", "четат"),
            ("Мария четат.", "четат"),
            ("Чорбанова се смеят.", "смеят"),
            ("Чорбанова се усмихваме.", "усмихваме"),
            ("Чорбанова се явявам на изпит.", "явявам"),
            ("Чорбанова се явяват утре.", "явяват"),
            ("Аз се казваме Чорбанова.", "казваме"),
            ("Ти се казвам Чорбанова.", "казвам"),
            ("Настоящото решение се прилагам за всички продукти.", "прилагам"),
            ("Когато Чорбанова четат, ние слушаме.", "четат"),
            ("След като Чорбанова четат, ние слушаме.", "четат")
        ];

        static bool IsPresentationTestWarning(string text, TextDiagnostic d) =>
            IsActionablePackageWarning(d.Code) &&
            !(d.Code == "spelling_unknown_word_unique_distance_one_candidate" &&
              (uint)d.Start < (uint)text.Length &&
              char.IsUpper(text[d.Start]));

        foreach (TextContextOptions opts in new TextContextOptions[] { default, fullOpts })
        {
            foreach (bool includeUnknown in new[] { false, true })
            {
                foreach (string clean in cleanPresentation)
                {
                    TextAnalysis ok = session.AnalyzeText(clean, includeUnknown, opts, default);
                    TextDiagnostic[] okWarnings = ok.Diagnostics.Where(d => IsPresentationTestWarning(clean, d)).ToArray();
                    Assert.IsEmpty(okWarnings, $"{clean} (unknown={includeUnknown}): [{string.Join(',', okWarnings.Select(w => $"{w.Code}@{clean.Substring(w.Start, w.Length)}"))}]");

                    if (opts.IncludeGrammarEvidence && ok.Syntax is { Available: true } syntax &&
                        clean is "Чорбанова се казвам." or "Мария се казвам." or "Казвам се Чорбанова.")
                    {
                        string nameToken = clean.Contains("Мария", StringComparison.Ordinal) ? "Мария" : "Чорбанова";
                        TextSyntaxCandidate[] badSubjects = syntax.Sentences
                            .SelectMany(s => s.Candidates)
                            .Where(c => c.Role == TextSyntaxRole.Subject &&
                                clean.Substring(c.Start, c.Length).Contains(nameToken, StringComparison.Ordinal))
                            .ToArray();
                        Assert.IsEmpty(badSubjects, $"{clean} (unknown={includeUnknown}): unexpected Subject role on {nameToken}");
                    }
                }

                foreach ((string err, string targetVerb) in mismatches)
                {
                    TextAnalysis bad = session.AnalyzeText(err, includeUnknown, opts, default);
                    TextDiagnostic[] warnings = bad.Diagnostics.Where(d => IsPresentationTestWarning(err, d)).ToArray();
                    TextDiagnostic hit = warnings.SingleOrDefault(d =>
                        d.Code == "productive_morphology_agreement_mismatch" &&
                        err.Substring(d.Start, d.Length) == targetVerb)!;
                    Assert.IsNotNull(hit, $"{err} (unknown={includeUnknown}): got [{string.Join(',', warnings.Select(w => $"{w.Code}@{err.Substring(w.Start, w.Length)}"))}]");
                    Assert.HasCount(1, warnings, $"{err} (unknown={includeUnknown}): unexpected warning count");
                }
            }
        }
    }
}
