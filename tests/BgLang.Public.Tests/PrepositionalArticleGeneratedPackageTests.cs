using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    private const string ShortArticleCode = "short_article_expected_after_preposition_candidate";
    private const string LocalArticleCode = "article_prepositional_group_locally_assessed";

    [TestMethod]
    public void Required_development_article_pairs_are_assessed_at_the_exact_occurrence()
    {
        string? currentManifest = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_CURRENT_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(currentManifest), "Article development acceptance requires the explicit Current full package.");
        using BulgarianLanguageSession current = BulgarianLanguageSession.Open(currentManifest!);
        Assert.AreEqual("bglang-neutral:90bea7c5c799f04d", current.Provenance.PackageId);
        Assert.AreEqual("c718c78baaf244b0dfde39f9fe0e9295abb974cc254a94903e70a3014625bbc7",
            current.Provenance.ManifestSha256.ToLowerInvariant());
        ArticleCase[] cases = DevelopmentArticleCases().ToArray();
        Assert.HasCount(384, cases);
        Assert.HasCount(192, cases.Select(value => value.Pair).Distinct().ToArray());
        List<string> failures = [];
        int detected = 0, locallyAssessed = 0;
        foreach (ArticleCase test in cases)
        {
            TextAnalysis actual = current.AnalyzeText(test.Text);
            TextValidationEvidence? validation = actual.Validation;
            if (actual.Text != test.Text || actual.ReadingInventory is null || validation?.Available != true ||
                test.Start < 0 || test.Length < 1 || test.Start + test.Length > test.Text.Length ||
                test.Text.Substring(test.Start, test.Length) != test.Surface)
            { failures.Add("invalid-output-or-range=" + test.Id); continue; }
            TextReadingReference[] expectedSupport = ExpectedArticleSupport(actual, test);
            TextDiagnostic[] warnings = actual.Diagnostics.Where(value => value.Code == ShortArticleCode).ToArray();
            TextValidationDiagnostic[] projected = validation.Diagnostics.Where(value => value.Code == ShortArticleCode).ToArray();
            if (test.Wrong)
            {
                if (warnings.Length != 1)
                { failures.Add("missed-or-duplicate=" + test.Id + ";count=" + warnings.Length); continue; }
                TextDiagnostic warning = warnings.Single();
                bool exact = warning.RuleId == "grammar.short_article.exact_prepositional_group.v1" &&
                    (test.Start, test.Length).Equals((warning.Start, warning.Length)) && warning.ProjectedText == test.Replacement &&
                    warning.Readings is { Count: > 0 } && warning.Readings.OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex)
                        .SequenceEqual(expectedSupport) && warning.GraphReadings is null && projected.Length == 1 &&
                    (test.Start, test.Length).Equals((projected[0].Start, projected[0].Length)) && projected[0].Supports.Count > 0 &&
                    projected[0].Supports.OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex).SequenceEqual(expectedSupport);
                if (!exact) { failures.Add("wrong-warning-or-support=" + test.Id); continue; }
                string repaired = test.Text[..test.Start] + warning.ProjectedText + test.Text[(test.Start + test.Length)..];
                if (test.ExpectedRepair != repaired) failures.Add("wrong-projection=" + test.Id);
                else detected++;
            }
            else
            {
                TextValidationDiagnostic[] local = validation.Diagnostics.Where(value =>
                    value.Code == LocalArticleCode && value.Start == test.Start && value.Length == test.Length).ToArray();
                if (warnings.Length != 0 || projected.Length != 0 || local.Length != 1 ||
                    !local[0].Basis.StartsWith("independent_preposition;", StringComparison.Ordinal) || local[0].Supports.Count == 0 ||
                    !local[0].Supports.OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex)
                        .SequenceEqual(expectedSupport) ||
                    actual.Diagnostics.Any(value => value.Start == test.Start && value.Length == test.Length &&
                        value.Code is ShortArticleCode or "full_article_expected_for_subject_candidate") ||
                    actual.Diagnostics.Any(value => value.Code == "full_short_article_check_not_ready" &&
                        value.Start == test.Start && value.Length == test.Length))
                {
                    TokenAnalysis token = actual.Tokens.Single(value => value.Start == test.Start && value.Length == test.Length);
                    string articles = string.Join('|', token.Analysis!.Readings.Select(value => value.Features.Article));
                    failures.Add("required-local-assessment-uncovered=" + test.Id + ";articles=" + articles +
                        ";notReady=" + actual.Diagnostics.Any(value => value.Code == "full_short_article_check_not_ready" &&
                            value.Start == test.Start && value.Length == test.Length));
                }
                else locallyAssessed++;
            }
        }
        Assert.AreEqual(0, failures.Count,
            $"Development REQUIRED_BASIC: detected={detected}/192, locallyAssessed={locallyAssessed}/192, failures={failures.Count}. " +
            string.Join(" | ", failures.Take(12)));
    }

    private static TextReadingReference[] ExpectedArticleSupport(TextAnalysis analysis, ArticleCase test)
    {
        TextReadingInventory inventory = analysis.ReadingInventory!;
        TextReadingOccurrence[] occurrences = inventory.Occurrences.Where(value => analysis.Tokens[value.TokenIndex].Kind == TokenKind.Word)
            .OrderBy(value => value.Start).ToArray();
        // The factory fixes participants from its text construction, independently of analyzer output.
        TextReadingOccurrence[] supported = test.Participants.Select(span => occurrences.Single(value =>
            value.Start == span.Start && value.Length == span.Length)).ToArray();
        for (int index = 0; index < supported.Length; index++)
        {
            TextReadingSet rows = inventory.Sets[supported[index].ReadingSetIndex];
            Assert.IsTrue(rows.ResolvedReadings.Count > 0, test.Id + ": empty participant readings.");
            Assert.IsTrue(rows.ResolvedReadings.Any(row => row.Features.PartOfSpeech == test.Participants[index].PartOfSpeech),
                test.Id + ": expected participant part of speech.");
        }
        int carrierPosition = Array.FindIndex(occurrences, value => value.Start == test.Start && value.Length == test.Length);
        Assert.IsTrue(carrierPosition >= 2, "Generated carrier must follow its preposition and finite predicate.");
        TextReadingOccurrence predicate = supported[0], preposition = supported[1], carrier = supported[2];
        Assert.AreEqual(occurrences[carrierPosition - 2], predicate);
        Assert.AreEqual(occurrences[carrierPosition - 1], preposition);
        Assert.AreEqual(occurrences[carrierPosition], carrier);
        TextReadingSet prepRows = inventory.Sets[preposition.ReadingSetIndex], predicateRows = inventory.Sets[predicate.ReadingSetIndex];
        Assert.IsTrue(prepRows.ResolvedReadings.Any(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition));
        Assert.IsTrue(prepRows.ResolvedReadings.All(row => row.Features.PartOfSpeech is
            MorphologicalPartOfSpeech.Preposition or MorphologicalPartOfSpeech.Particle));
        Assert.IsTrue(predicateRows.ResolvedReadings.All(row => row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
            row.Features.VerbForm == MorphologicalVerbForm.Finite));
        if (supported.Length == 4)
        {
            TextReadingOccurrence head = supported[3];
            Assert.IsTrue(carrierPosition + 1 < occurrences.Length);
            Assert.AreEqual(occurrences[carrierPosition + 1], head);
            Assert.AreEqual(carrier.Start + carrier.Length + 1, head.Start);
            Assert.IsTrue(inventory.Sets[head.ReadingSetIndex].ResolvedReadings.All(row =>
                row.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun));
        }
        return supported.SelectMany(occurrence => Enumerable.Range(0, inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Count)
                .Select(ordinal => new TextReadingReference(occurrence.TokenIndex, ordinal)))
            .OrderBy(reference => reference.TokenIndex).ThenBy(reference => reference.CompleteReadingIndex).ToArray();
    }

    private sealed record ArticleSeed(string Id, string Lemma, string Full, string Short);
    private sealed record ArticleFrame(string Id, string Verb, string Preposition);
    private sealed record ArticleParticipant(int Start, int Length, MorphologicalPartOfSpeech PartOfSpeech);
    private sealed record ArticleCase(string Id, string Pair, string Text, int Start, int Length,
        string Surface, bool Wrong, string? Replacement, string ExpectedRepair, ArticleParticipant[] Participants);

    private static IEnumerable<ArticleCase> DevelopmentArticleCases()
    {
        ArticleSeed[] nouns =
        [
            new("stazhant", "стажант", "стажантът", "стажанта"),
            new("hotel", "хотел", "хотелът", "хотела"),
            new("lekar", "лекар", "лекарят", "лекаря"),
            new("sluzhitel", "служител", "служителят", "служителя")
        ];
        ArticleSeed[] adjectives =
        [
            new("nov", "нов", "новият", "новия"),
            new("dobar", "добър", "добрият", "добрия")
        ];
        ArticleFrame[] frames =
        [
            new("govorya_za", "Говоря", "за"), new("mislya_za", "Мисля", "за"),
            new("pisha_na", "Пиша", "на"), new("tragvam_bez", "Тръгвам", "без")
        ];
        foreach (ArticleSeed noun in nouns)
        foreach ((string layout, ArticleSeed? adjective) in new[]
            { ("noun", (ArticleSeed?)null), ("adj_nov", adjectives[0]), ("adj_dobar", adjectives[1]) })
        foreach (ArticleFrame frame in frames)
        foreach (string presentation in new[] { "plain", "uppercase", "emoji", "repeated" })
        foreach (bool wrong in new[] { false, true })
        {
            ArticleSeed carrier = adjective ?? noun;
            string before = frame.Verb + " " + frame.Preposition + " ";
            string after = (adjective is null ? "" : " " + noun.Lemma) + ".";
            string expected = carrier.Short;
            string observed = wrong ? carrier.Full : expected;
            string prefix = before, ending = after;
            if (presentation == "repeated") prefix = before + expected + after + " " + before;
            if (presentation == "emoji") prefix = "🙂 " + prefix;
            if (presentation == "uppercase")
            {
                prefix = prefix.ToUpperInvariant(); ending = ending.ToUpperInvariant();
                observed = observed.ToUpperInvariant(); expected = expected.ToUpperInvariant();
            }
            string text = prefix + observed + ending;
            int predicateStart = prefix.Length - before.Length;
            List<ArticleParticipant> participants =
            [
                new(predicateStart, frame.Verb.Length, MorphologicalPartOfSpeech.Verb),
                new(predicateStart + frame.Verb.Length + 1, frame.Preposition.Length, MorphologicalPartOfSpeech.Preposition),
                new(prefix.Length, observed.Length, adjective is null ? MorphologicalPartOfSpeech.Noun : MorphologicalPartOfSpeech.Adjective)
            ];
            if (adjective is not null)
                participants.Add(new(prefix.Length + observed.Length + 1, noun.Lemma.Length, MorphologicalPartOfSpeech.Noun));
            string pair = $"development/{noun.Id}/{layout}/{frame.Id}/postposed/{presentation}";
            yield return new ArticleCase(pair + (wrong ? "/error" : "/correct"), pair, text,
                prefix.Length, observed.Length, observed, wrong, wrong ? expected : null,
                wrong ? prefix + expected + ending : text, participants.ToArray());
        }
    }
}
