using BgLang.Api;
using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class LocalPrepositionalArticleGraphPackageTests
{
    private const string Warning = "short_article_expected_after_preposition_candidate";
    private const string LocalFact = "article_prepositional_group_locally_assessed";
    private const string Gap = "full_short_article_check_not_ready";
    private static BulgarianLanguageSession session = null!;

    [ClassInitialize]
    public static void Open(TestContext _) => session = GraphStressAcceptance.Open();

    [ClassCleanup]
    public static void Close() => session.Dispose();

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Говоря {0}.", "с", "учителят", "учителя", "")]
    [DataRow("{0} говоря.", "С", "учителят", "учителя", "")]
    [DataRow("{0}.", "С", "учителят", "учителя", "")]
    [DataRow("Говоря {0} и пиша.", "с", "учителят", "учителя", "")]
    [DataRow("В задънена улица говорех {0}.", "с", "учителят", "учителя", "")]
    [DataRow("Говоря {0}, а той чете „Под игото“.", "с", "учителят", "учителя", "")]
    [DataRow("„Под игото“ прочетох и после говорих {0}.", "с", "учителят", "учителя", "")]
    [DataRow("🙂 Говоря {0} Иван Петров.", "с", "учителят", "учителя", "")]
    [DataRow("Помагам {0}.", "на", "ученикът", "ученика", "")]
    [DataRow("{0} помагам.", "На", "ученикът", "ученика", "")]
    [DataRow("Говоря {0}.", "с", "добрият", "добрия", " учител")]
    [DataRow("Говоря {0}.", "с", "добрият", "добрия", " млад учител")]
    [DataRow("Говорих {0}.", "с", "държавният", "държавния", " глава")]
    [DataRow("В задънена улица попадна и делото за убийството {0} Георги Сивков.", "на", "наркодилърът", "наркодилъра", "")]
    public void Local_result_is_independent_of_predicates_and_unrelated_text(
        string frame, string preposition, string full, string shortForm, string tail) =>
        CheckLocalPair(frame, preposition, full, shortForm, tail);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Боята се разля {0}.", "по", " стол")]
    [DataRow("Стоях {0}.", "до", ", изоставен стол")]
    public void Measured_common_head_keeps_simple_and_comma_bridged_modifiers(string frame, string preposition, string tail) =>
        CheckLocalPair(frame, preposition, "старият", "стария", tail);

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Минах по старият мост.")]
    [DataRow("Стоях до старият, изоставен мост.")]
    public void Original_place_name_alternative_at_the_head_preserves_abstention(string text)
    {
        TextAnalysis analysis = session.AnalyzeText(text);
        TextReadingInventory inventory = analysis.ReadingInventory!;
        var occurrence = inventory.Occurrences.Single(row => row.Text == "мост");
        var rows = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
        Assert.IsTrue(rows.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun));
        Assert.IsTrue(rows.Any(row => row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.ProperNoun,
            NameKind: MorphologicalNameKind.PlaceName }));
        Assert.IsFalse(analysis.Diagnostics.Any(row => row.Code == Warning), text);
        Assert.IsFalse(analysis.Validation!.Diagnostics.Any(row => row.Code == LocalFact), text);
    }

    private static void CheckLocalPair(string frame, string preposition, string full, string shortForm, string tail)
    {
        Check(full, expectedWarning: true);
        Check(shortForm, expectedWarning: false);

        void Check(string carrier, bool expectedWarning)
        {
            string pp = preposition + " " + carrier + tail;
            string text = frame.Replace("{0}", pp, StringComparison.Ordinal);
            int ppStart = text.IndexOf(pp, StringComparison.Ordinal);
            int start = ppStart + preposition.Length + 1;
            TextAnalysis analysis = session.AnalyzeText(text);
            var warnings = analysis.Diagnostics.Where(row => row.Code == Warning).ToArray();
            var facts = analysis.Validation!.Diagnostics.Where(row => row.Code == LocalFact).ToArray();
            IReadOnlyList<TextReadingReference> supports;
            if (expectedWarning)
            {
                Assert.HasCount(1, warnings, text);
                Assert.IsEmpty(facts, text);
                TextDiagnostic warning = warnings[0];
                Assert.AreEqual((start, carrier.Length, shortForm), (warning.Start, warning.Length, warning.ProjectedText));
                Assert.IsNull(warning.GraphReadings);
                supports = warning.Readings!;
                var mirror = analysis.Validation.Diagnostics.Single(row => row.Code == Warning);
                CollectionAssert.AreEqual(supports.ToArray(), mirror.Supports.ToArray());
            }
            else
            {
                Assert.IsEmpty(warnings, text);
                Assert.HasCount(1, facts, text);
                Assert.AreEqual((start, carrier.Length), (facts[0].Start, facts[0].Length));
                StringAssert.Contains(facts[0].Basis, "exact_inverse_full_counterpart_roundtrip");
                supports = facts[0].Supports;
            }
            Assert.AreEqual(text, analysis.Text);
            Assert.IsFalse(analysis.Diagnostics.Any(row => row.Code == Gap && row.Start == start));
            TextReadingInventory inventory = analysis.ReadingInventory!;
            var occurrences = inventory.Occurrences.ToDictionary(row => row.TokenIndex);
            int[] ppTokens = inventory.Occurrences.Where(row => row.Start >= ppStart && row.Start + row.Length <= ppStart + pp.Length)
                .Select(row => row.TokenIndex).ToArray();
            CollectionAssert.AreEqual(ppTokens, supports.Select(row => row.TokenIndex).Distinct().ToArray());
            foreach (var reference in supports)
            {
                var occurrence = occurrences[reference.TokenIndex];
                var row = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings[reference.CompleteReadingIndex];
                if (occurrence.Start == ppStart)
                    Assert.AreEqual(MorphologicalPartOfSpeech.Preposition, row.Features.PartOfSpeech);
            }
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("Надписът „Говоря с учителят“ е дословен.")]
    [DataRow("„Пример. Говоря с учителя. Край.“")]
    [DataRow("„Пример. Говоря с учителят. Край.")]
    [DataRow("Говоря с „учителят“.")]
    [DataRow("`Говоря с учителят`")]
    [DataRow("Говоря с Учителят.")]
    [DataRow("Говорих с държавният Глава.")]
    [DataRow("Говоря с добрият учителят.")]
    [DataRow("Говоря с добрият учители.")]
    [DataRow("Говоря с добрият и млад учител.")]
    [DataRow("Стоях до два стола.")]
    [DataRow("Добрият учител дойде.")]
    public void Quoted_or_locally_unproved_groups_do_not_gain_article_advice(string text)
    {
        TextAnalysis analysis = session.AnalyzeText(text);
        Assert.IsFalse(analysis.Diagnostics.Any(row => row.Code == Warning), text);
        Assert.IsFalse(analysis.Validation!.Diagnostics.Any(row => row.Code is Warning or LocalFact), text);
    }

    [TestMethod, TestCategory("FullPackage")]
    public void Repeated_targets_use_distinct_original_utf16_occurrences()
    {
        const string text = "🙂 Говоря с учителят и после пак с учителят.";
        TextAnalysis analysis = session.AnalyzeText(text);
        var warnings = analysis.Diagnostics.Where(row => row.Code == Warning).ToArray();
        CollectionAssert.AreEqual(new[] { text.IndexOf("учителят", StringComparison.Ordinal), text.LastIndexOf("учителят", StringComparison.Ordinal) },
            warnings.Select(row => row.Start).ToArray());
        foreach (var warning in warnings)
        {
            Assert.AreEqual("учителят", text.Substring(warning.Start, warning.Length));
            Assert.AreEqual("учителя", warning.ProjectedText);
            Assert.AreEqual(warning.Start, analysis.Tokens[warning.Readings![^1].TokenIndex].Start);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("По някакъв много странен начин тя приличаше на Хюго.", "някакъв|странен|начин", "тя", "приличаше")]
    [DataRow("По един много дълъг път той вървеше.", "един|дълъг|път", "той", "вървеше")]
    [DataRow("С един много добър човек тя разговаряше.", "един|добър|човек", "тя", "разговаряше")]
    [DataRow("По някакъв странен начин тя приличаше на Хюго.", "някакъв|странен|начин", "тя", "приличаше")]
    public void Governed_prefix_preserves_noncontiguous_original_members(
        string text, string members, string subject, string predicate)
    {
        foreach ((bool unknownNames, TextContextOptions options) in Profiles())
        {
            TextAnalysis analysis = session.AnalyzeText(text, unknownNames, options, default);
            TextContextSentence sentence = analysis.Context!.Sentences.Single();
            TextContextCandidate candidate = sentence.Candidates.Single(c => c.AuthorityLane == "local_finite_clause_roles.v1");
            Assert.IsFalse(candidate.Selectable, "A local role hypothesis does not gain selection authority.");
            Assert.AreEqual(predicate, analysis.Tokens[candidate.PredicateTokenIndex].Text);
            Assert.AreEqual(predicate, analysis.Tokens[candidate.Roles.Single(r => r.Role == TextContextRole.Predicate).TokenIndex].Text);
            Assert.AreEqual(subject, analysis.Tokens[candidate.Roles.Single(r => r.Role == TextContextRole.Subject).TokenIndex].Text);
            TextContextRoleSelection[] prefix = candidate.Roles.Where(r => r.EvidenceCodes.Contains("governed_prefix")).ToArray();
            CollectionAssert.AreEqual(members.Split('|'), prefix.Select(r => analysis.Tokens[r.TokenIndex].Text).ToArray(), text);
            foreach (TextContextRoleSelection member in prefix)
            {
                Assert.AreEqual(TextContextRole.NominalContext, member.Role, text);
                TextReadingInventory inventory = analysis.ReadingInventory!;
                TextReadingOccurrence occurrence = inventory.Occurrences.Single(o => o.TokenIndex == member.TokenIndex);
                TokenAnalysis token = analysis.Tokens[member.TokenIndex];
                Assert.AreEqual((token.Start, token.Length), (member.Start, member.Length));
                Assert.AreEqual((token.Start, token.Length), (occurrence.Start, occurrence.Length));
                Assert.AreEqual(token.Text, text.Substring(member.Start, member.Length));
                var complete = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
                CollectionAssert.AreEqual(session.Lookup(token.Text, LookupMode.Exact, 256).ToArray(),
                    complete.Select(r => r.Raw).ToArray(), token.Text);
                CollectionAssert.AreEqual(Enumerable.Range(0, complete.Count)
                    .Select(i => new TextReadingReference(member.TokenIndex, i)).ToArray(), member.Readings.ToArray(), text);
                Assert.IsNull(member.GraphReadings, text);
            }
            int[] ppTokens = prefix.Select(r => r.TokenIndex).ToArray();
            Assert.IsFalse(sentence.SelectedRoles.Any(r => r.Role == TextContextRole.Subject && ppTokens.Contains(r.TokenIndex)), text);
            Assert.IsFalse(analysis.Syntax!.Sentences.SelectMany(s => s.Candidates)
                .Where(c => c.Role == TextSyntaxRole.Subject).SelectMany(c => c.Targets)
                .Any(t => ppTokens.Contains(t.TokenIndex)), text);
            TextContextRoleSelection boundSubject = candidate.Roles.Single(r => r.Role == TextContextRole.Subject);
            TextSyntaxSentence syntax = analysis.Syntax.Sentences.Single();
            string expectedRoute = predicate switch
            {
                "разговаряше" => "SourceScan",
                "вървеше" when options.IncludeAdditionalRoleHeuristics => "Selected",
                _ => "Abstained"
            };
            Assert.AreEqual(expectedRoute, syntax.Route, text);
            if (expectedRoute == "Abstained")
            {
                Assert.IsNull(syntax.PredicateTokenIndex, text);
                Assert.IsEmpty(syntax.Candidates.Where(c => c.Role == TextSyntaxRole.Subject), text);
                continue;
            }
            TextSyntaxCandidate syntaxSubject = syntax.Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
            Assert.AreEqual((boundSubject.Start, boundSubject.Length), (syntaxSubject.Start, syntaxSubject.Length), text);
            Assert.AreEqual(subject, text.Substring(syntaxSubject.Start, syntaxSubject.Length), text);
            TextSyntaxSupport target = syntaxSubject.Targets.Single();
            Assert.AreEqual(boundSubject.TokenIndex, target.TokenIndex, text);
            Assert.AreEqual("TypedFeatures", target.Basis, text);
            CollectionAssert.AreEqual(boundSubject.Readings.ToArray(), target.Readings.ToArray(), text);
            Assert.IsNull(target.GraphReadings, text);
            TextSyntaxCandidate syntaxPredicate = syntax.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate);
            Assert.AreEqual(candidate.PredicateTokenIndex, syntaxPredicate.Targets.Single().TokenIndex, text);
            TextSyntaxSupport governor = syntaxSubject.Context.Single(s => s.TokenIndex == candidate.PredicateTokenIndex);
            CollectionAssert.AreEqual(syntaxPredicate.Targets.Single().Readings.ToArray(), governor.Readings.ToArray(), text);
            if (text == "С един много добър човек тя разговаряше.")
            {
                Assert.AreEqual((25, 2), (syntaxSubject.Start, syntaxSubject.Length));
                Assert.IsNull(sentence.SelectedPredicateTokenIndex, "Local role proof must remain candidate-only.");
                Assert.IsFalse(sentence.SelectedRoles.Any(r => r.Role == TextContextRole.Subject));
            }
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("С един много добър човек тя разговаряше.", "тя", "разговаряше")]
    [DataRow("За един много добър човек тя говореше.", "тя", "говореше")]
    [DataRow("С един добър човек тя разговаряше.", "тя", "разговаряше")]
    [DataRow("Един добър човек разговаряше.", "Един добър човек", "разговаряше")]
    public void Subject_span_survives_all_public_profiles_and_ordinary_nominal_alternatives(
        string text, string subjectSpan, string predicate)
    {
        foreach ((bool unknownNames, TextContextOptions options) in Profiles())
        {
            TextAnalysis analysis = session.AnalyzeText(text, unknownNames, options, default);
            TextSyntaxSentence sentence = analysis.Syntax!.Sentences.Single();
            TextSyntaxCandidate subject = sentence.Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
            Assert.AreEqual((text.IndexOf(subjectSpan, StringComparison.Ordinal), subjectSpan.Length), (subject.Start, subject.Length), text);
            Assert.AreEqual(predicate, analysis.Tokens[sentence.PredicateTokenIndex!.Value].Text, text);
            Assert.IsTrue(subject.Targets.All(t => analysis.Tokens[t.TokenIndex].Start >= subject.Start &&
                analysis.Tokens[t.TokenIndex].Start + analysis.Tokens[t.TokenIndex].Length <= subject.Start + subject.Length), text);
            Assert.IsTrue(subject.Targets.All(t => t.Readings.Count > 0 && t.GraphReadings is null), text);
        }
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("С един много добър човек разговаряше.")]
    [DataRow("С един много добър човек, тя разговаряше.")]
    [DataRow("С един много добър човек тя, разговаряше.")]
    [DataRow("„С един много добър човек тя разговаряше.“")]
    public void Missing_or_disconnected_subject_has_no_governed_prefix_proof(string text)
    {
        foreach ((bool unknownNames, TextContextOptions options) in Profiles())
        {
            TextAnalysis analysis = session.AnalyzeText(text, unknownNames, options, default);
            Assert.IsFalse(analysis.Context!.Sentences.SelectMany(s => s.Candidates)
                .SelectMany(c => c.Roles).Any(r => r.Role == TextContextRole.Subject &&
                    r.EvidenceCodes.Contains("canonical_subject_after_governed_prefix")), text);
            Assert.IsFalse(analysis.Context.Sentences.SelectMany(s => s.Candidates)
                .Where(c => c.AuthorityLane == "local_finite_clause_roles.v1").Any(c => c.Selectable), text);
        }
    }

    private static IEnumerable<(bool UnknownNames, TextContextOptions Options)> Profiles()
    {
        var hub = new TextContextOptions
        {
            IncludeAdditionalRoleHeuristics = true, IncludeSubordinateCommaDiagnostics = true,
            IncludeFragmentDiagnostics = true, IncludeSpacingDiagnostics = true,
            IncludeNominalConstructions = true, IncludeGrammarEvidence = true
        };
        yield return (false, default);
        yield return (false, new() { IncludeAdditionalRoleHeuristics = true });
        yield return (true, hub);
        yield return (true, hub with { IncludeAdditionalRoleHeuristics = false });
    }

    [TestMethod, TestCategory("FullPackage")]
    [DataRow("С един много добър човек тя разговаряше.", "тя", "тя", "разговаряше")]
    [DataRow("За един много добър човек тя говореше.", "тя", "тя", "говореше")]
    [DataRow("Един добър човек разговаряше.", "Един добър човек", "Един|добър|човек", "разговаряше")]
    public void Source_subject_extent_excludes_proved_governed_prefix_and_preserves_unbound_nominals(
        string text, string subjectSpan, string subjectTargets, string predicate)
    {
        TextAnalysis analysis = session.AnalyzeText(text, false, default, default);
        TextSyntaxSentence sentence = analysis.Syntax!.Sentences.Single();
        TextSyntaxCandidate subject = sentence.Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
        TextSyntaxCandidate verb = sentence.Candidates.Single(c => c.Role == TextSyntaxRole.Predicate);
        Assert.AreEqual((text.IndexOf(subjectSpan, StringComparison.Ordinal), subjectSpan.Length), (subject.Start, subject.Length));
        Assert.AreEqual(subjectSpan, text.Substring(subject.Start, subject.Length));
        CollectionAssert.AreEqual(subjectTargets.Split('|'), subject.Targets.Select(t => analysis.Tokens[t.TokenIndex].Text).ToArray());
        Assert.AreEqual(predicate, text.Substring(verb.Start, verb.Length));
        Assert.AreEqual(verb.Targets.Single().TokenIndex, sentence.PredicateTokenIndex);
        Assert.AreEqual(predicate, analysis.Tokens[verb.Targets.Single().TokenIndex].Text);
        foreach (TextSyntaxCandidate role in new[] { subject, verb })
            foreach (TextSyntaxSupport target in role.Targets)
            {
                Assert.IsTrue(target.TokenIndex >= 0 && target.TokenIndex < analysis.Tokens.Count);
                TokenAnalysis token = analysis.Tokens[target.TokenIndex];
                Assert.IsTrue(token.Start >= role.Start && token.Start + token.Length <= role.Start + role.Length);
                Assert.AreEqual(token.Text, text.Substring(token.Start, token.Length));
                Assert.IsNotEmpty(target.Readings);
                TextReadingInventory inventory = analysis.ReadingInventory!;
                TextReadingOccurrence occurrence = inventory.Occurrences.Single(o => o.TokenIndex == target.TokenIndex);
                int completeCount = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Count;
                foreach (TextReadingReference reading in target.Readings)
                {
                    Assert.AreEqual(target.TokenIndex, reading.TokenIndex);
                    Assert.IsTrue(reading.CompleteReadingIndex >= 0 && reading.CompleteReadingIndex < completeCount);
                }
            }
    }
}
