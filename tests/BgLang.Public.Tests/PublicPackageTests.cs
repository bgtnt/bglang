using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

// Complete candidate checks use public API fields and independent literal records only.
[TestClass]
public sealed class PublicPackageTests
{
    private static BulgarianLanguageSession session = null!;
    private static string manifest = null!;
    private static JsonElement candidate;
    private static JsonElement[] cases = [];
    private static JsonElement[] typedTags = [];

    [TestMethod]
    public void Construction_query_preserves_exact_owner_identity_and_ready_empty_state()
    {
        LexicalEntryLookup lookup = session.LookupLexicalEntries("знаме", 256);
        Assert.IsTrue(lookup.MetadataAvailable, "The successor public profile requires the complete annotated package.");
        Assert.IsNotEmpty(lookup.Entries);
        LexicalEntry owner = lookup.Entries[0];
        WordConstructionResult result = session.GetWordConstructionCandidates(owner.EntryId);
        Assert.AreEqual(WordConstructionStatus.NoApplicable, result.Status);
        Assert.IsNotNull(result.Owner); Assert.AreEqual(owner.EntryId, result.Owner.EntryId);
        Assert.AreEqual(owner.LexemeKey, result.Owner.LexemeKey);
        Assert.IsNotNull(result.OwnForms); Assert.IsNotEmpty(result.OwnForms);
        Assert.IsEmpty(result.Candidates);
        Assert.IsFalse(result.ProductiveReplayAvailable);
        Assert.AreEqual("OriginalGenerationSeedNotRetained", result.ProductiveReplayUnavailableReason);
        WordConstructionResult unknown = session.GetWordConstructionCandidates(" " + owner.EntryId + " ");
        Assert.AreEqual(WordConstructionStatus.OwnerNotFound, unknown.Status);
        Assert.IsNull(unknown.Owner); Assert.IsNull(unknown.OwnForms); Assert.IsEmpty(unknown.Candidates);
        Assert.Throws<ArgumentNullException>(() => session.GetWordConstructionCandidates(null!));
        var signature = typeof(BulgarianLanguageSession).GetMethod(nameof(BulgarianLanguageSession.GetWordConstructionCandidates),
            new[] { typeof(string), typeof(CancellationToken) });
        Assert.IsNotNull(signature); Assert.AreEqual(typeof(WordConstructionResult), signature.ReturnType);
        Assert.IsFalse(signature.GetParameters()[0].IsOptional); Assert.IsTrue(signature.GetParameters()[1].IsOptional);
    }

    [TestMethod]
    public void Construction_text_switch_distinguishes_not_run_from_complete_enabled_empty()
    {
        Assert.IsFalse(default(TextContextOptions).IncludeWordConstructions);
        TextAnalysis disabled = session.AnalyzeText("", false, default, CancellationToken.None);
        TextAnalysis enabled = session.AnalyzeText("", false,
            new TextContextOptions { IncludeWordConstructions = true }, CancellationToken.None);
        Assert.IsNull(disabled.WordConstructions);
        Assert.IsNotNull(enabled.WordConstructions);
        Assert.IsTrue(enabled.WordConstructions.Complete);
        Assert.IsEmpty(enabled.WordConstructions.Owners); Assert.IsEmpty(enabled.WordConstructions.Candidates);
        Assert.IsEmpty(enabled.WordConstructions.Matches); Assert.IsEmpty(enabled.WordConstructions.UnavailableReadings);
        Assert.IsFalse(enabled.WordConstructions.ProductiveReplayAvailable);
        Assert.AreEqual("OriginalGenerationSeedNotRetained", enabled.WordConstructions.ProductiveReplayUnavailableReason);
        JsonElement before = JsonSerializer.SerializeToElement(disabled), after = JsonSerializer.SerializeToElement(enabled);
        foreach (JsonProperty property in before.EnumerateObject().Where(value => value.Name != "WordConstructions"))
            Assert.AreEqual(property.Value.GetRawText(), after.GetProperty(property.Name).GetRawText(), property.Name);
    }

    [TestMethod]
    public void Construction_public_precancellation_preserves_token_and_session()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        OperationCanceledException query = Assert.Throws<OperationCanceledException>(() =>
            session.GetWordConstructionCandidates("opaque-owner", cancellation.Token));
        Assert.AreEqual(cancellation.Token, query.CancellationToken);
        OperationCanceledException text = Assert.Throws<OperationCanceledException>(() => session.AnalyzeText("знаме.", false,
            new TextContextOptions { IncludeWordConstructions = true }, cancellation.Token));
        Assert.AreEqual(cancellation.Token, text.CancellationToken);
        Assert.IsTrue(session.AnalyzeWord("знаме").IsKnown);
    }

    [TestMethod]
    public void Context_four_argument_overload_keeps_default_literal_calls_and_all_parameters_required()
    {
        TextAnalysis manual = new("", []);
        var (manualSource, manualTokens) = manual;
        Assert.AreEqual("", manualSource); Assert.IsEmpty(manualTokens);
        Assert.IsNull(manual.Context); Assert.IsNull(manual.Syntax); Assert.IsNull(manual.Validation);
        Assert.IsNull(manual.WordConstructions);
        const string source = "знаме.";
        TextAnalysis ordinary = session.AnalyzeText(source);
        TextAnalysis tokenDefault = session.AnalyzeText(source, default);
        TextAnalysis nameDefault = session.AnalyzeText(source, default, CancellationToken.None);
        TextAnalysis contextDefault = session.AnalyzeText(source, false, default, CancellationToken.None);
        foreach (TextAnalysis actual in new[] { tokenDefault, nameDefault, contextDefault })
            Assert.AreEqual(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(actual));
        Assert.AreEqual(new TextMetrics(2, 1, 1, 1, 5, 1, 1, 0, 0), contextDefault.Metrics);
        Assert.AreEqual("Съществително:69,Среден", contextDefault.ReadingInventory!.Sets.Single().CanonicalReadings.Single().Raw.PartOfSpeech);
        var signature = typeof(BulgarianLanguageSession).GetMethod(nameof(BulgarianLanguageSession.AnalyzeText),
            new[] { typeof(string), typeof(bool), typeof(TextContextOptions), typeof(CancellationToken) });
        Assert.IsNotNull(signature);
        Assert.IsTrue(signature.GetParameters().All(parameter => !parameter.IsOptional));
        Assert.IsNull(typeof(BulgarianLanguageSession).GetMethod(nameof(BulgarianLanguageSession.AnalyzeText),
            new[] { typeof(string), typeof(TextContextOptions), typeof(CancellationToken) }));
    }

    [TestMethod]
    public void Context_options_preserve_precancelled_token_without_success_or_session_damage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        TextContextOptions[] options = [default, new()
        {
            IncludeAdditionalRoleHeuristics = true, IncludeSubordinateCommaDiagnostics = true,
            IncludeFragmentDiagnostics = true, IncludeSpacingDiagnostics = true
        }];
        foreach (TextContextOptions context in options)
        foreach (bool names in new[] { false, true })
        {
            OperationCanceledException error = Assert.Throws<OperationCanceledException>(() =>
                session.AnalyzeText("знаме.", names, context, cancellation.Token));
            Assert.AreEqual(cancellation.Token, error.CancellationToken);
        }
        Assert.IsTrue(session.AnalyzeWord("знаме").IsKnown);
    }

    [TestMethod]
    public void Complete_text_composition_preserves_literal_inventory_metrics_and_profile()
    {
        const string source = "знаме знаме.";
        TextAnalysis text = session.AnalyzeText(source, default);
        Assert.AreEqual(source, text.Text);
        CollectionAssert.AreEqual(new[] { "Text", "Tokens", "Sentences", "Phonetics", "Diagnostics", "Lexical",
            "ReadingInventory", "Metrics", "Spelling", "Profile", "ProperNames", "Context", "Syntax", "Validation", "WordConstructions" },
            JsonSerializer.SerializeToElement(text).EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(new TextMetrics(3, 2, 1, 2, 5, .5, 1, 1, 0), text.Metrics);
        TextReadingInventory inventory = text.ReadingInventory!;
        Assert.HasCount(1, inventory.Sets);
        CollectionAssert.AreEqual(new[] { new TextReadingOccurrence(0, "знаме", 0, 5, 0),
            new TextReadingOccurrence(1, "знаме", 6, 5, 0) }, inventory.Occurrences.ToArray());
        TextReadingSet set = inventory.Sets[0];
        Assert.AreEqual("знаме", set.LookupText);
        Assert.AreEqual("знаме", set.CanonicalKey);
        Assert.IsNull(set.SupportKey);
        Assert.IsNull(set.SupportReadings);
        Assert.AreEqual(1, set.DisplayedReadingCount);
        Assert.IsFalse(set.DisplayTruncated);
        TextReadingSnapshot reading = set.CanonicalReadings.Single();
        Assert.AreEqual("знаме", reading.Raw.Form);
        Assert.AreEqual("знаме", reading.Raw.Lemma);
        Assert.AreEqual("Съществително:69,Среден", reading.Raw.PartOfSpeech);
        Assert.AreEqual(1d, reading.Raw.Score);
        Assert.AreEqual(MorphologicalPartOfSpeech.Noun, reading.Features.PartOfSpeech);
        Assert.AreEqual(MorphologicalGender.Neuter, reading.Features.Gender);
        Assert.AreEqual(MorphologicalNumber.Singular, reading.Features.Number);
        Assert.AreEqual(MorphologicalArticle.Indefinite, reading.Features.Article);
        Assert.IsNull(reading.Features.NameKind);
        DocumentReadingProfile profile = text.Profile!;
        Assert.AreEqual("typed-reading-profile.v1", profile.ProjectionVersion);
        Assert.AreEqual(2L, profile.WordOccurrences);
        Assert.AreEqual(2L, profile.KnownWordOccurrences);
        Assert.AreEqual(0L, profile.UnknownWordOccurrences);
        Assert.AreEqual(0L, profile.UnmeasuredKnownnessWordOccurrences);
        Assert.AreEqual(2L, profile.DisplayedReadingOccurrences);
        Assert.AreEqual(2L, profile.FullReadingOccurrences);
        Assert.AreEqual(2L, profile.CompleteExactInventoryWordOccurrences);
        Assert.AreEqual(2L, profile.ProjectionAssessmentCompleteWordOccurrences);
        Assert.AreEqual(0L, profile.ObservedProjectedAmbiguityWordOccurrences);
        Assert.AreEqual(0L, profile.ProjectionIssueWordOccurrences);
        Assert.AreEqual(0L, profile.DisplayTruncatedWordOccurrences);
        Assert.AreEqual(1d, profile.KnownWordRatio);
        Assert.IsTrue(profile.AllWordAssessmentsCompleteForProjection);
        Assert.IsTrue(profile.ExactReadingEvidencePresent);
        Assert.IsTrue(profile.Words.All(word => word.FullReadingCount == 1 && word.ProjectedGroupCount == 1 &&
            word.ObservedProjectedAmbiguity == false && word.Issues == ReadingProjectionIssues.None && word.ActualDisplayTruncated == false));
        CollectionAssert.AreEqual(new[] { new KeyValuePair<MorphologicalPartOfSpeech, long>(MorphologicalPartOfSpeech.Noun, 2) },
            profile.DisplayedPosCounts.ToArray());
        CollectionAssert.AreEqual(new[] { new KeyValuePair<string, long>("знаме", 2) }, profile.DisplayedLemmaCounts.ToArray());
        Assert.AreEqual(2, text.Spelling!.ConfiguredMaxDistance);
        Assert.AreEqual(10, text.Spelling.ConfiguredLimit);
        Assert.IsTrue(text.Spelling.Words.All(word => word.Search == SpellingSearchDisposition.NotRequestedKnown &&
            word.Reason == "exact-known" && word.Alternatives.Count == 0));
        Assert.IsEmpty(text.ProperNames!.Candidates);
        Assert.IsEmpty(text.ProperNames.Groups);
        Assert.IsNull(text.ProperNames.UnresolvedContextTokens);
        Assert.IsFalse(text.ProperNames.UnknownContextEvaluated);
        Assert.IsTrue(text.ProperNames.CompleteForEnabledScope);
    }

    [TestMethod]
    public void Complete_empty_text_distinguishes_optin_not_run_from_enabled_empty()
    {
        foreach (bool enabled in new[] { false, true })
        {
            TextAnalysis empty = session.AnalyzeText("", enabled, CancellationToken.None);
            Assert.AreEqual(new TextMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0), empty.Metrics);
            Assert.IsEmpty(empty.ReadingInventory!.Sets);
            Assert.IsEmpty(empty.ReadingInventory.Occurrences);
            Assert.IsNull(empty.Profile!.KnownWordRatio);
            Assert.AreEqual(0L, empty.Profile.FullReadingOccurrences);
            Assert.IsTrue(empty.Profile.AllWordAssessmentsCompleteForProjection);
            Assert.IsFalse(empty.Profile.ExactReadingEvidencePresent);
            Assert.AreEqual(enabled, empty.ProperNames!.UnknownContextEvaluated);
            Assert.AreEqual(enabled, empty.ProperNames.UnresolvedContextTokens is not null);
            Assert.IsEmpty(empty.ProperNames.Candidates);
        }
    }

    [TestMethod]
    public void Complete_new_text_overload_preserves_precancelled_token_and_session()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (bool enabled in new[] { false, true })
        {
            var error = Assert.Throws<OperationCanceledException>(() => session.AnalyzeText("знаме", enabled, cancellation.Token));
            Assert.AreEqual(cancellation.Token, error.CancellationToken);
        }
        Assert.IsTrue(session.AnalyzeWord("знаме").IsKnown);
    }

    [TestMethod]
    public void Complete_context_optin_defers_existing_graph_base_evidence_without_inventing_unknown_names()
    {
        const string source = "д-р Супертрактор.";
        TextAnalysis disabled = session.AnalyzeText(source);
        TextAnalysis enabled = session.AnalyzeText(source, true, CancellationToken.None);
        TokenAnalysis token = enabled.Tokens[1];
        Assert.AreEqual("Супертрактор", token.Text);
        Assert.AreEqual(4, token.Start);
        Assert.AreEqual(12, token.Length);
        Assert.IsFalse(token.Analysis!.IsKnown);
        Assert.IsNotNull(token.Analysis.BaseEvidence);
        Assert.AreEqual("трактор", token.Analysis.BaseEvidence.Base);
        TextReadingOccurrence occurrence = enabled.ReadingInventory!.Occurrences.Single(value => value.TokenIndex == 1);
        Assert.IsEmpty(enabled.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings);
        Assert.IsEmpty(enabled.ProperNames!.Candidates);
        CollectionAssert.AreEqual(new[] { 1 }, enabled.ProperNames.DeferredTokenIndices.ToArray());
        Assert.IsFalse(enabled.ProperNames.CompleteForEnabledScope);
        Assert.IsNotNull(enabled.ProperNames.UnresolvedContextTokens);
        Assert.IsNull(disabled.ProperNames!.UnresolvedContextTokens);
        Assert.IsEmpty(disabled.ProperNames.DeferredTokenIndices);
        CollectionAssert.AreEqual(disabled.Diagnostics.Select(value => JsonSerializer.Serialize(value)).ToArray(),
            enabled.Diagnostics.Select(value => JsonSerializer.Serialize(value)).ToArray());
    }

    [TestMethod]
    [DataRow("д-р Жжжж.", ProperNameLane.Title, 1, 4)]
    [DataRow("знаме Жжжж.", ProperNameLane.MidSentence, 1, 6)]
    [DataRow("Жжжж Иванов.", ProperNameLane.Surname, 0, 0)]
    [DataRow("Жжжж Жжжж писа.", ProperNameLane.ClosedClause, 0, 0)]
    public void Complete_text_optin_runs_each_advisory_lane_without_changing_exact_evidence(
        string source, ProperNameLane lane, int tokenIndex, int start)
    {
        TextAnalysis disabled = session.AnalyzeText(source);
        TextAnalysis enabled = session.AnalyzeText(source, true, CancellationToken.None);
        Assert.IsNull(disabled.ProperNames!.UnresolvedContextTokens);
        Assert.IsNotNull(enabled.ProperNames!.UnresolvedContextTokens);
        Assert.IsFalse(disabled.ProperNames.Candidates.Any(candidate => candidate.Lane != ProperNameLane.Exact));
        CollectionAssert.AreEqual(disabled.Diagnostics.Select(value => JsonSerializer.Serialize(value)).ToArray(),
            enabled.Diagnostics.Select(value => JsonSerializer.Serialize(value)).ToArray());
        CollectionAssert.AreEqual(disabled.ReadingInventory!.Sets.Select(value => JsonSerializer.Serialize(value)).ToArray(),
            enabled.ReadingInventory!.Sets.Select(value => JsonSerializer.Serialize(value)).ToArray());
        ProperNameCandidate candidate = enabled.ProperNames.Candidates.Single(value => value.TokenIndex == tokenIndex);
        Assert.AreEqual(lane, candidate.Lane);
        Assert.AreEqual(start, candidate.Start);
        Assert.AreEqual(4, candidate.Length);
        Assert.AreEqual("Жжжж", source.Substring(candidate.Start, candidate.Length));
        Assert.IsEmpty(candidate.ExactSupports);
        Assert.IsTrue(candidate.GroupingKindKnown);
        Assert.IsTrue(enabled.ProperNames.CompleteForEnabledScope);
        foreach (TextReadingReference support in candidate.ContextSupports)
        {
            TextReadingOccurrence occurrence = enabled.ReadingInventory.Occurrences.Single(value => value.TokenIndex == support.TokenIndex);
            TextReadingSnapshot reading = enabled.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings[support.CompleteReadingIndex];
            if (lane == ProperNameLane.ClosedClause)
            {
                Assert.AreEqual(2, support.TokenIndex);
                Assert.AreEqual(MorphologicalPerson.Third, reading.Features.Person);
                Assert.AreEqual(MorphologicalVerbForm.Finite, reading.Features.VerbForm);
            }
            else
                Assert.AreEqual(MorphologicalNameKind.FamilyName, reading.Features.NameKind);
        }
        if (lane is ProperNameLane.Surname or ProperNameLane.ClosedClause) Assert.IsNotEmpty(candidate.ContextSupports);
        if (lane == ProperNameLane.ClosedClause)
        {
            ProperNameGroup group = enabled.ProperNames.Groups.Single();
            Assert.AreEqual(2, group.CandidateCount);
            Assert.AreEqual(0, group.Start);
            Assert.AreEqual(9, group.Length);
            Assert.AreEqual(ProperNameKinds.Unspecified, group.Kinds);
            Assert.IsTrue(group.MaximalWithinCompleteEnabledScope);
        }
    }

    [TestMethod]
    public void Complete_package_conditional_roots_refuse_empty_hyphen_components()
    {
        foreach (string input in new[] { "-наносензор", "наносензор-", "нано--сензор", "ултра--лек",
            "свръх---проводник", "-хиперчетлив", "хиперчетлив-" })
        {
            Assert.IsEmpty(session.Lookup(input, LookupMode.Exact, 256), "The frozen negative must remain unseen: " + input);
            WordAnalysis word = session.AnalyzeWord(input, 256);
            Assert.IsFalse(word.IsKnown);
            Assert.IsFalse(word.GraphCandidates.Any(path => path.IsConditional &&
                (path.Base.StartsWith('-') || path.Base.EndsWith('-') || path.Base.Contains("--", StringComparison.Ordinal))), input);
        }
        foreach ((string input, int count) in new[] { ("бяло-зелено-червен", 54), ("бяло-зелено-червените", 173) })
        {
            Assert.IsEmpty(session.Lookup(input, LookupMode.Exact, 256), "A lexical hit cannot prove this unseen-form control: " + input);
            WordAnalysis word = session.AnalyzeWord(input, 256);
            Assert.IsFalse(word.IsKnown);
            Assert.AreEqual(count, word.GraphCandidates.Count, input);
            Assert.IsTrue(word.GraphCandidates.All(path => path.IsConditional &&
                path.UnmetExactSelfReadingRequirements is { Count: > 0 }), input);
            AssertSearchFlags(word, false, false, false, false, false, false, false);
        }
    }

    [TestMethod]
    public void Complete_successor_adds_imperative_alternatives_without_losing_noun_or_finite_readings()
    {
        Assert.AreEqual(candidate.GetProperty("packageId").GetString(), session.Provenance.PackageId);
        AssertLanguage(session, candidate.GetProperty("sourceId").GetString()!);
    }

    [TestMethod]
    public void Complete_package_admits_exact_adverb_degree_with_independent_public_proof()
    {
        foreach ((string input, string rule) in new[]
        {
            ("по-късно", "productive.adverb.degree_comparative_po.v1"),
            ("най-късно", "productive.adverb.degree_superlative_nai.v1")
        })
        {
            WordAnalysis word = session.AnalyzeWord(input, 256);
            Assert.IsFalse(word.IsKnown);
            WordDerivation path = word.GraphCandidates.Single(row => row.RuleIds.Contains(rule));
            Assert.AreEqual("късно", path.Base);
            Assert.AreEqual("state:adverb:188", path.FromState); Assert.AreEqual(path.FromState, path.ToState);
            CollectionAssert.AreEqual(new[] { "Наречие:188" }, path.FullTags!.ToArray());
            Assert.IsFalse(path.IsConditional); Assert.IsEmpty(path.ApplicationIds);
            Assert.AreEqual(MorphologicalPartOfSpeech.Adverb, path.Features.Single().PartOfSpeech);
            Assert.AreEqual("transparent", word.BaseEvidence?.Transparency);
            Assert.AreEqual("късно", word.BaseEvidence?.Base);
        }
    }

    // Frozen independent literal oracle: 6a59de97076cc6c8207d7d0d9e91e8b1da4d24290188cfd821fb8711b726ad55.
    private static readonly string[] Doyda40 =
    [
        "дойдем|дойда|Глагол:146a,1л.,Сегашно,мн.ч.",
        "дошло|дойда|Глагол:146a,Минало_свършено_деятелно,Среден,1л.,Сегашно",
        "дойде|дойда|Глагол:146a,2л.,Минало_свършено",
        "дойдеха|дойда|Глагол:146a,3л.,Минало_несвършено,мн.ч.",
        "дошлите|дойда|Глагол:146a,Минало_свършено_деятелно,1л.,Сегашно,мн.ч.,Членуван",
        "дойдеше|дойда|Глагол:146a,3л.,Минало_несвършено",
        "дошла|дойда|Глагол:146a,Минало_свършено_деятелно,Женски,1л.,Сегашно",
        "дойдеш|дойда|Глагол:146a,2л.,Сегашно",
        "дойдела|дойда|Глагол:146a,Минало_несвършено_деятелно,Женски,1л.,Сегашно",
        "дойделите|дойда|Глагол:146a,Минало_несвършено_деятелно,1л.,Сегашно,мн.ч.,Членуван",
        "дойделата|дойда|Глагол:146a,Минало_несвършено_деятелно,Женски,1л.,Сегашно,Членуван",
        "дойде|дойда|Глагол:146a,3л.,Сегашно",
        "дойдехме|дойда|Глагол:146a,1л.,Минало_несвършено,мн.ч.",
        "дошлият|дойда|Глагол:146a,Минало_свършено_деятелно,Мъжки,1л.,Сегашно,Пълен",
        "дойделото|дойда|Глагол:146a,Минало_несвършено_деятелно,Среден,1л.,Сегашно,Членуван",
        "дойде|дойда|Глагол:146a,3л.,Минало_свършено",
        "дошли|дойда|Глагол:146a,Минало_свършено_деятелно,1л.,Сегашно,мн.ч.",
        "дойдоха|дойда|Глагол:146a,3л.,Минало_свършено,мн.ч.",
        "дошъл|дойда|Глагол:146a,Минало_свършено_деятелно,Мъжки,1л.,Сегашно",
        "дойда|дойда|Глагол:146a,1л.,Сегашно",
        "дошлия|дойда|Глагол:146a,Минало_свършено_деятелно,Мъжки,1л.,Сегашно,Членуван",
        "дошлата|дойда|Глагол:146a,Минало_свършено_деятелно,Женски,1л.,Сегашно,Членуван",
        "дойдете|дойда|Глагол:146a,2л.,Сегашно,мн.ч.",
        "дойделия|дойда|Глагол:146a,Минало_несвършено_деятелно,Мъжки,1л.,Сегашно,Членуван",
        "дойдохме|дойда|Глагол:146a,1л.,Минало_свършено,мн.ч.",
        "дойдеше|дойда|Глагол:146a,2л.,Минало_несвършено",
        "дойдехте|дойда|Глагол:146a,2л.,Минало_несвършено,мн.ч.",
        "дойдете|дойда|Глагол:146a,1л.,Сегашно,Повелително,мн.ч.",
        "дойдело|дойда|Глагол:146a,Минало_несвършено_деятелно,Среден,1л.,Сегашно",
        "дойдели|дойда|Глагол:146a,Минало_несвършено_деятелно,1л.,Сегашно,мн.ч.",
        "дойдох|дойда|Глагол:146a,1л.,Минало_свършено",
        "дойди|дойда|Глагол:146a,1л.,Сегашно,Повелително",
        "дойдех|дойда|Глагол:146a,1л.,Минало_несвършено",
        "дойдел|дойда|Глагол:146a,Минало_несвършено_деятелно,Мъжки,1л.,Сегашно",
        "дойдат|дойда|Глагол:146a,3л.,Сегашно,мн.ч.",
        "дойдохте|дойда|Глагол:146a,2л.,Минало_свършено,мн.ч.",
        "дойделият|дойда|Глагол:146a,Минало_несвършено_деятелно,Мъжки,1л.,Сегашно,Пълен",
        "дошлото|дойда|Глагол:146a,Минало_свършено_деятелно,Среден,1л.,Сегашно,Членуван",
        "ела|дойда|Глагол:146a,1л.,Сегашно,Повелително",
        "елате|дойда|Глагол:146a,1л.,Сегашно,Повелително,мн.ч.",
    ];
    private static readonly string[] NounEla4 =
    [
        "елата|ела|Съществително:41,Женски,Членуван",
        "ела|ела|Съществително:41,Женски",
        "ели|ела|Съществително:41,Женски,мн.ч.",
        "елите|ела|Съществително:41,Женски,мн.ч.,Членуван",
    ];
    private static void AssertLanguage(BulgarianLanguageSession language, string source)
    {
        static string ReadingKey(WordReading r) => r.Form + "|" + r.Lemma + "|" + r.PartOfSpeech;
        static string FormKey(WordForm r) => r.Form + "|" + r.Lemma + "|" + r.Tag;
        foreach ((string lemma, string[] expected) in new[] { ("дойда", Doyda40), ("ела", NounEla4) })
        {
            WordForm[] forms = language.GenerateForms(lemma, 256).ToArray();
            CollectionAssert.AreEqual(expected.Order(StringComparer.Ordinal).ToArray(), forms.Select(FormKey).ToArray());
            Assert.IsTrue(forms.All(r => r.Source == source && r.LegacyBaseId is null));
            foreach (WordForm form in forms)
                Assert.IsTrue(language.Lookup(form.Form, LookupMode.Exact, 256).Any(r => ReadingKey(r) == FormKey(form)));
        }
        foreach (string query in new[] { "ела", "елате", "дойди", "дойдете", "Ела", "ЕЛАТЕ", "ДОЙДЕТЕ" })
        {
            string normalized = query.ToLowerInvariant();
            string[] expected = Doyda40.Concat(NounEla4).Where(r => r.Split('|')[0] == normalized)
                .Order(StringComparer.Ordinal).ToArray();
            WordReading[] lookup = language.Lookup(query, LookupMode.Exact, 256).ToArray();
            CollectionAssert.AreEqual(expected, lookup.Select(ReadingKey).ToArray(), query);
            WordAnalysis word = language.AnalyzeWord(query, 256);
            Assert.AreEqual(query, word.Word); Assert.AreEqual(normalized, word.NormalizedWord); Assert.IsTrue(word.IsKnown);
            CollectionAssert.AreEqual(expected, word.Readings.Select(ReadingKey).ToArray());
            Assert.IsNull(word.BaseEvidence); Assert.IsEmpty(word.GraphCandidates); Assert.IsEmpty(word.Candidates);
            Assert.IsEmpty(word.KnownBaseCandidates); Assert.IsEmpty(word.BaseTypeCandidates);
            CollectionAssert.AreEqual(new bool[7], new[] { word.GraphSearchIncomplete, word.GraphCandidatesTruncated,
                word.CandidateSearchLimited, word.CandidateEvidenceLimited, word.KnownBaseSearchLimited,
                word.BaseTypeSearchLimited, word.BaseTypeEvidenceLimited });
            foreach (WordReading r in lookup)
            {
                Assert.AreEqual(source, r.Source); Assert.AreEqual(1d, r.Score);
                Assert.IsNull(r.LegacyTypeId); Assert.IsNull(r.LegacyBaseId); Assert.IsNull(r.LegacyWordId);
                if (r.Lemma == "дойда" && r.PartOfSpeech!.Contains("Повелително", StringComparison.Ordinal))
                {
                    MorphologicalFeatures f = r.Features;
                    Assert.AreEqual(MorphologicalPartOfSpeech.Verb, f.PartOfSpeech);
                    Assert.AreEqual("146a", f.ParadigmId); Assert.AreEqual(MorphologicalPerson.Second, f.Person);
                    Assert.AreEqual(normalized is "елате" or "дойдете" ? MorphologicalNumber.Plural : MorphologicalNumber.Singular, f.Number);
                    Assert.AreEqual(MorphologicalVerbForm.Imperative, f.VerbForm); Assert.IsNull(f.Tense); Assert.IsNull(f.Gender); Assert.IsNull(f.Article);
                    Assert.IsFalse(f.HasUnrecognizedFeatures); Assert.IsFalse(f.HasConflictingFeatures);
                }
            }
        }
        WordReading finite = language.Lookup("дойдете", LookupMode.Exact, 256).Single(r => r.PartOfSpeech == "Глагол:146a,2л.,Сегашно,мн.ч.");
        Assert.AreEqual(MorphologicalPerson.Second, finite.Features.Person);
        Assert.AreEqual(MorphologicalNumber.Plural, finite.Features.Number);
        Assert.AreEqual(MorphologicalTense.Present, finite.Features.Tense);
        Assert.AreEqual(MorphologicalVerbForm.Finite, finite.Features.VerbForm);
        WordReading baseVerb = language.Lookup("дойда", LookupMode.Exact, 256).Single(r => r.PartOfSpeech == "Глагол:146a,1л.,Сегашно");
        Assert.AreEqual(MorphologicalPerson.First, baseVerb.Features.Person);
        Assert.AreEqual(MorphologicalNumber.Singular, baseVerb.Features.Number);
        Assert.AreEqual(MorphologicalTense.Present, baseVerb.Features.Tense);
        Assert.AreEqual(MorphologicalVerbForm.Finite, baseVerb.Features.VerbForm);
        WordReading tree = language.Lookup("ела", LookupMode.Exact, 256).Single(r => r.Lemma == "ела");
        Assert.AreEqual(MorphologicalPartOfSpeech.Noun, tree.Features.PartOfSpeech);
        Assert.AreEqual(MorphologicalGender.Feminine, tree.Features.Gender);
        Assert.AreEqual(MorphologicalNumber.Singular, tree.Features.Number);
        Assert.AreEqual(MorphologicalArticle.Indefinite, tree.Features.Article);
        Assert.IsNull(tree.Features.Person); Assert.IsNull(tree.Features.VerbForm); Assert.IsNull(tree.Features.Tense);
        Assert.IsFalse(language.GenerateForms("ела", 256).Any(r => r.Lemma == "дойда"));
        const string text = "😀 Ела, ЕЛАТЕ! Дойдете.";
        TextAnalysis analysis = language.AnalyzeText(text);
        Assert.AreEqual(text, analysis.Text);
        CollectionAssert.AreEqual(new[] { "Symbol@0:2:😀", "Word@3:3:Ела", "Punctuation@6:1:,", "Word@8:5:ЕЛАТЕ",
            "Punctuation@13:1:!", "Word@15:7:Дойдете", "Punctuation@22:1:." },
            analysis.Tokens.Select(t => $"{t.Kind}@{t.Start}:{t.Length}:{t.Text}").ToArray());
        foreach (var token in analysis.Tokens)
        {
            Assert.AreEqual(token.Text, text.Substring(token.Start, token.Length));
            if (token.Kind == TokenKind.Word)
            {
                Assert.IsTrue(token.Analysis!.IsKnown);
                Assert.AreEqual(JsonSerializer.Serialize(language.AnalyzeWord(token.Text)), JsonSerializer.Serialize(token.Analysis), "Exact-known text must preserve the complete context-free word result.");
                CollectionAssert.AreEqual(language.Lookup(token.Text, LookupMode.Exact, 256).Select(ReadingKey).ToArray(),
                    token.Analysis.Readings.Select(ReadingKey).ToArray());
            }
        }
        Console.WriteLine("ELA exact-known text diagnostics: " + JsonSerializer.Serialize(analysis.Diagnostics));
    }

    [ClassInitialize]
    public static void OpenExactCompletePackage(TestContext _)
    {
        string? lexicalManifest = Environment.GetEnvironmentVariable("BGLANG_LEXICAL_MANIFEST");
        if (Environment.GetEnvironmentVariable("BGLANG_LEXICAL_REQUIRE_MANIFEST") == "1")
            Assert.IsFalse(string.IsNullOrWhiteSpace(lexicalManifest), "The required annotated successor cannot fall back to the historical public lock.");
        bool lexical = lexicalManifest is not null;
        string fixtureName = lexical ? "lexical-annotated-package.v1.json" : "public-input-lock.v1.json";
        byte[] identity = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", fixtureName));
        if (lexical) Assert.AreEqual("4a4da62b4e33643429bc4f7fea50a86e0e73ca6380ca55c6d2b7f972e130b3ba", Convert.ToHexStringLower(SHA256.HashData(identity)));
        using var document = JsonDocument.Parse(identity);
        Assert.AreEqual(lexical ? "bglang.lexical-public-package.v1" : "bglang.public-input-lock.v1", document.RootElement.GetProperty("schema").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("contractComplete").GetBoolean(), "Complete public input contract is required; missing inputs never skip.");
        candidate = document.RootElement.GetProperty("expectedCandidate").Clone();
        string? supplied = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(supplied), "Set the explicit complete candidate manifest.");
        Assert.IsTrue(Path.IsPathFullyQualified(supplied!));
        manifest = Path.GetFullPath(supplied!);
        if (lexical) Assert.AreEqual(Path.GetFullPath(lexicalManifest!), manifest, true, "Public and generic controls must select the same exact annotated package.");
        Assert.AreEqual("manifest.json", Path.GetFileName(manifest));
        string directory = Path.GetDirectoryName(manifest)!;
        var files = candidate.GetProperty("files").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(files.Select(file => file.GetProperty("name").GetString()).ToArray(),
            Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray());
        foreach (var file in files)
        {
            string name = file.GetProperty("name").GetString()!;
            Assert.AreEqual(name, Path.GetFileName(name));
            string path = Path.Combine(directory, name);
            Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
            Assert.AreEqual(file.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        byte[] fixtureBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data/public-literal-readings.v1.json"));
        Assert.AreEqual("a8a188b2145a8201b8d6d49ae85ac730bc21839645f1311c52a2e6387f0659f4", Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)));
        using var fixture = JsonDocument.Parse(fixtureBytes);
        cases = fixture.RootElement.GetProperty("cases").EnumerateArray().Select(value => value.Clone()).ToArray();
        typedTags = fixture.RootElement.GetProperty("frozenTypedTagAssertions").EnumerateArray().Select(value => value.Clone()).ToArray();
        session = BulgarianLanguageSession.Open(manifest);
    }

    [ClassCleanup]
    public static void ClosePackage() => session?.Dispose();

    [TestMethod]
    public void Complete_package_identity_is_exact()
    {
        Assert.AreEqual(candidate.GetProperty("packageId").GetString(), session.Provenance.PackageId);
        Assert.AreEqual(candidate.GetProperty("sourceId").GetString(), session.Provenance.LexiconSource);
        if (candidate.TryGetProperty("annotations", out JsonElement annotations))
        {
            Assert.AreEqual(candidate.GetProperty("manifestSchema").GetString(), session.Provenance.SchemaVersion);
            Assert.AreEqual(annotations.GetProperty("sha256").GetString(), session.Provenance.AnnotationsSha256);
            Assert.AreEqual(annotations.GetProperty("sourceId").GetString(), session.Provenance.AnnotationsSource);
        }
        else Assert.IsNull(session.Provenance.AnnotationsSha256);
    }

    [TestMethod]
    [DataRow("име")]
    [DataRow("знаме")]
    public void Complete_forms_equal_independent_literal_records(string lemma)
    {
        var fixture = cases.Single(value => value.GetProperty("lemma").GetString() == lemma);
        var expected = fixture.GetProperty("rawReadings").EnumerateArray().Select(value => Key(
            value.GetProperty("form").GetString()!, value.GetProperty("lemma").GetString()!, value.GetProperty("fullTag").GetString())).Order(StringComparer.Ordinal).ToArray();
        var actual = session.GenerateForms(lemma, 16).Select(value => Key(value.Form, value.Lemma, value.Tag)).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("име")]
    [DataRow("знаме")]
    public void Lookup_preserves_every_literal_tag_and_score(string lemma)
    {
        foreach (var reading in cases.Single(value => value.GetProperty("lemma").GetString() == lemma).GetProperty("rawReadings").EnumerateArray())
        {
            var actual = session.Lookup(reading.GetProperty("form").GetString()!, LookupMode.Exact, 16);
            Assert.AreEqual(1, actual.Count);
            Assert.AreEqual(reading.GetProperty("lemma").GetString(), actual[0].Lemma);
            Assert.AreEqual(reading.GetProperty("fullTag").GetString(), actual[0].PartOfSpeech);
            Assert.AreEqual(reading.GetProperty("score").GetDouble(), actual[0].Score);
            Assert.IsNull(actual[0].LegacyTypeId);
            Assert.IsNull(actual[0].LegacyBaseId);
            Assert.IsNull(actual[0].LegacyWordId);
            Assert.AreEqual(candidate.GetProperty("sourceId").GetString(), actual[0].Source);
            JsonElement tag = typedTags.Single(value => value.GetProperty("rawTag").GetString() == actual[0].PartOfSpeech);
            MorphologicalFeatures features = actual[0].Features;
            Assert.AreEqual(tag.GetProperty("partOfSpeech").GetString(), features.PartOfSpeech.ToString());
            Assert.AreEqual(tag.GetProperty("paradigmId").GetString(), features.ParadigmId);
            Assert.AreEqual(tag.GetProperty("number").GetString(), features.Number?.ToString());
            Assert.AreEqual(tag.GetProperty("gender").GetString(), features.Gender?.ToString());
            Assert.AreEqual(tag.GetProperty("article").GetString(), features.Article?.ToString());
            Assert.AreEqual(tag.GetProperty("hasUnrecognizedFeatures").GetBoolean(), features.HasUnrecognizedFeatures);
            Assert.AreEqual(tag.GetProperty("hasConflictingFeatures").GetBoolean(), features.HasConflictingFeatures);
        }
    }

    [TestMethod]
    public void Repeated_text_preserves_original_offsets_and_readings()
    {
        var words = session.AnalyzeText("име име").Tokens.Where(value => value.Kind == TokenKind.Word).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 4 }, words.Select(value => value.Start).ToArray());
        foreach (var word in words)
        {
            Assert.AreEqual("име", word.Text);
            Assert.AreEqual(3, word.Length);
            Assert.IsNotNull(word.Analysis);
            Assert.IsTrue(word.Analysis.IsKnown);
            CollectionAssert.AreEqual(session.Lookup("име").Select(value => Key(value.Form, value.Lemma, value.PartOfSpeech)).ToArray(),
                word.Analysis.Readings.Select(value => Key(value.Form, value.Lemma, value.PartOfSpeech)).ToArray());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Canceled_public_overloads_leave_exact_lookup_usable(bool text)
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        OperationCanceledException error = Assert.ThrowsExactly<OperationCanceledException>(() =>
        {
            if (text) session.AnalyzeText("име име", source.Token);
            else session.AnalyzeWord("име", 16, source.Token);
        });
        Assert.AreEqual(source.Token, error.CancellationToken);
        Assert.AreEqual("име", session.Lookup("име", LookupMode.Exact, 16).Single().Lemma);
    }

    [TestMethod]
    [DataRow("суперработник", "работник")]
    [DataRow("свръхсупертракторите", "трактор")]
    [DataRow("супертрактор", "трактор")]
    [DataRow("суперхубав", "хубав")]
    [DataRow("суперкнига", "книга")]
    public void Unknown_prefixes_preserve_exact_root_paths_and_independent_limits(string query, string root)
    {
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.AreEqual(query, word.Word);
        Assert.AreEqual(query, word.NormalizedWord);
        Assert.IsFalse(word.IsKnown);
        Assert.AreEqual(0, word.Readings.Count);
        Assert.IsNotNull(word.BaseEvidence);
        Assert.AreEqual(root, word.BaseEvidence.Base);
        WordDerivation exact = word.GraphCandidates.Single(path => path.Base == root && !path.IsConditional);
        Assert.IsNull(exact.ConditionalRootProgramId);
        Assert.IsNull(exact.ConditionalRootBaseFullTag);
        Assert.IsNull(exact.ConditionalRootSupportCount);
        Assert.IsNull(exact.ConditionalSupportingLemmas);
        Assert.IsNull(exact.UnmetExactSelfReadingRequirements);
        AssertSearchFlags(word, false, false, false, true, false, true, true);
    }

    [TestMethod]
    public void Conditional_zero_path_keeps_unmet_exact_self_requirement()
    {
        WordAnalysis word = session.AnalyzeWord("суперработник", 256);
        WordDerivation path = word.GraphCandidates.Single(value => value.IsConditional &&
            value.Base == "суперработник" && value.ConditionalRootProgramId == "productive.noun.masculine_type14.v0");
        Assert.IsFalse(word.IsKnown);
        Assert.AreEqual(0, word.Readings.Count);
        Assert.AreEqual("Съществително:14,Мъжки", path.ConditionalRootBaseFullTag);
        Assert.AreEqual(1042, path.ConditionalRootSupportCount);
        Assert.IsNotNull(path.ConditionalSupportingLemmas);
        Assert.AreEqual(1042, path.ConditionalSupportingLemmas.Count);
        CollectionAssert.Contains(path.ConditionalSupportingLemmas.ToArray(), "авджилък");
        Assert.IsNotNull(path.UnmetExactSelfReadingRequirements);
        ExactSelfReadingRequirement requirement = path.UnmetExactSelfReadingRequirements.Single();
        Assert.AreEqual("суперработник", requirement.InputSurface);
        Assert.AreEqual("Съществително:14,Мъжки", requirement.FullTag);
        Assert.AreEqual("program|productive.noun.masculine_type14.v0|base", requirement.EdgeId);
        Assert.AreEqual("суперработник", path.DerivedLemma);
        CollectionAssert.AreEqual(new[] { "Съществително:14,Мъжки" }, path.FullTags!.ToArray());
    }

    [TestMethod]
    public void Reviewed_decomposition_does_not_imply_productive_generation()
    {
        WordDecomposition value = session.DecomposeWord("писател");
        Assert.IsTrue(value.HasEvidence);
        Assert.AreEqual("a:писател", value.ApplicationId);
        Assert.AreEqual("transparent", value.Transparency);
        Assert.AreEqual(false, value.ProductiveGeneration);
    }

    [TestMethod]
    public void Fuzzy_transposition_preserves_independent_book_reading()
    {
        FuzzyMatch value = session.FindSimilar("книаг", 1, 256).Single();
        Assert.AreEqual("книга", value.Form);
        Assert.AreEqual(1, value.Distance);
        WordReading reading = value.Readings.Single();
        Assert.AreEqual("книга", reading.Form);
        Assert.AreEqual("книга", reading.Lemma);
        Assert.AreEqual("Съществително:41,Женски", reading.PartOfSpeech);
        Assert.AreEqual(1.0, reading.Score);
        Assert.AreEqual(candidate.GetProperty("sourceId").GetString(), reading.Source);
        Assert.IsNull(reading.LegacyTypeId);
        Assert.IsNull(reading.LegacyBaseId);
        Assert.IsNull(reading.LegacyWordId);
    }

    [TestMethod]
    public void Segmentation_preserves_literal_cover_without_syntax_claim()
    {
        WordSegmentationCandidate value = session.SegmentWord("старитекниги", 5, 5)
            .Single(row => row.SegmentedText == "старите книги");
        Assert.AreEqual("старитекниги", value.OriginalText);
        Assert.AreEqual("старитекниги", value.NormalizedText);
        CollectionAssert.AreEqual(new[] { "старите", "книги" }, value.Segments.Select(row => row.Text).ToArray());
        Assert.AreEqual("exact-dawg-segmentation.v1", value.Source);
        Assert.IsFalse(value.IsSyntaxRanked);
        Assert.IsTrue(value.Segments.All(row => row.Readings.Count > 0 && row.MorphologyEvidence is null));
        Assert.AreEqual(0, session.SegmentWord("старитекниги", 5, 1).Count);
    }

    [TestMethod]
    public void Agreement_diagnostic_has_literal_rule_and_source_span()
    {
        const string text = "Аз го четем.";
        TextDiagnostic value = session.AnalyzeText(text).Diagnostics
            .Where(row => row.Code.StartsWith("pronoun_verb_agreement_", StringComparison.Ordinal)).Single();
        Assert.AreEqual("pronoun_verb_agreement_mismatch", value.Code);
        Assert.AreEqual(6, value.Start);
        Assert.AreEqual(5, value.Length);
        Assert.AreEqual("четем", text.Substring(value.Start, value.Length));
        Assert.AreEqual("pronoun_verb_agreement", value.RuleId);
        Assert.IsNull(value.ProjectedText);
    }

    [TestMethod]
    public void Agreement_does_not_claim_the_unsupported_SVO_pattern()
    {
        Assert.IsFalse(session.AnalyzeText("Аз четем книга.").Diagnostics.Any(value =>
            value.Code == "pronoun_verb_agreement_mismatch"));
    }

    [TestMethod]
    [DataRow("той", "той")]
    [DataRow("тои\u0306", "той")]
    [DataRow("ТОИ\u0306", "той")]
    [DataRow("и", "и")]
    [DataRow("ѝ", "ѝ")]
    [DataRow("и\u0300", "ѝ")]
    public void Canonical_Bulgarian_keys_preserve_the_raw_word(string raw, string key)
    {
        WordAnalysis value = session.AnalyzeWord(raw);
        Assert.AreEqual(raw, value.Word);
        Assert.AreEqual(key, value.NormalizedWord);
    }

    [TestMethod]
    public void Canonical_grave_spellings_share_the_actual_lookup_owner_and_keep_raw_words()
    {
        WordAnalysis composed = session.AnalyzeWord("кафѐ");
        WordAnalysis decomposed = session.AnalyzeWord("кафе\u0300");
        Assert.AreEqual("кафѐ", composed.Word);
        Assert.AreEqual("кафе\u0300", decomposed.Word);
        Assert.AreEqual(composed.NormalizedWord, decomposed.NormalizedWord);
        Assert.AreEqual(composed.IsKnown, decomposed.IsKnown);
        CollectionAssert.AreEqual(composed.Readings.Select(row => JsonSerializer.Serialize(row)).ToArray(),
            decomposed.Readings.Select(row => JsonSerializer.Serialize(row)).ToArray());
        Assert.IsTrue(composed.NormalizedWord is "кафѐ" or "кафе");
        if (composed.NormalizedWord == "кафе")
        {
            Assert.IsTrue(composed.IsKnown);
            Assert.IsTrue(composed.Readings.Count > 0);
        }
    }

    [TestMethod]
    public void Canonical_letters_keep_raw_text_tokens_and_phonetic_UTF16_offsets()
    {
        const string raw = "😀 Тои\u0306 кафе\u0300.";
        TextAnalysis value = session.AnalyzeText(raw);
        Assert.AreEqual(raw, value.Text);
        CollectionAssert.AreEqual(new[] { "Symbol@0:2:😀", "Word@3:4:Тои\u0306", "Word@8:5:кафе\u0300", "Punctuation@13:1:." },
            value.Tokens.Select(token => $"{token.Kind}@{token.Start}:{token.Length}:{token.Text}").ToArray());
        WordAnalysis[] words = value.Tokens.Where(token => token.Kind == TokenKind.Word)
            .Select(token => token.Analysis!).ToArray();
        Assert.AreEqual("той", words[0].NormalizedWord);
        WordAnalysis composed = session.AnalyzeWord("кафѐ");
        Assert.AreEqual(composed.NormalizedWord, words[1].NormalizedWord);
        Assert.AreEqual(composed.IsKnown, words[1].IsKnown);
        Assert.AreEqual("кафе\u0300", words[1].Word);
        Assert.IsTrue(words[1].NormalizedWord is "кафѐ" or "кафе");
        if (words[1].NormalizedWord == "кафе")
            Assert.IsTrue(words[1].IsKnown && words[1].Readings.Count > 0);
        CollectionAssert.AreEqual(new[] { 11 }, value.Phonetics.ExplicitStressPositions.ToArray());
        CollectionAssert.AreEqual(new[] { 4 }, value.Phonetics.InferredSingleVowelStressPositions.ToArray());
        CollectionAssert.AreEqual(new[] { "е" }, value.Phonetics.ExplicitRhymeTailCandidates.ToArray());
        CollectionAssert.AreEqual(new[] { "ой" }, value.Phonetics.InferredSingleVowelRhymeTailCandidates.ToArray());
    }

    [TestMethod]
    public void Article_diagnostics_preserve_candidate_suggestions_and_coverage_gaps()
    {
        const string text = "Големия човек е лекар. Писателя е човек. " +
            "Големият човек е лекар. Видях големия човек.";
        var diagnostics = session.AnalyzeText(text).Diagnostics;
        TextDiagnostic[] actual = diagnostics.Where(row => row.Code == "full_article_expected_for_subject_candidate").ToArray();
        CollectionAssert.AreEqual(new[] { 0, 23 }, actual.Select(row => row.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 7, 8 }, actual.Select(row => row.Length).ToArray());
        CollectionAssert.AreEqual(new[] { "Големият", "Писателят" }, actual.Select(row => row.ProjectedText).ToArray());
        CollectionAssert.AreEqual(new[] { "Големия", "Писателя" }, actual.Select(row => text.Substring(row.Start, row.Length)).ToArray());
        Assert.IsTrue(actual.All(row => row.RuleId == "grammar.full_article.copular_subject_exact.v1"));
        TextDiagnostic[] gaps = diagnostics.Where(row => row.Code == "full_short_article_check_not_ready").ToArray();
        CollectionAssert.AreEqual(new[] { 41, 71 }, gaps.Select(row => row.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 8, 7 }, gaps.Select(row => row.Length).ToArray());
        Assert.IsTrue(gaps.All(row => row.ProjectedText is null && row.RuleId == "grammar.full_short_article_morphology_coverage.v1"));
    }

    [TestMethod]
    public void Tractor_chain_is_a_graph_relation_for_an_already_known_word()
    {
        WordDerivation path = session.DeriveWords("трактор", 16).Single(value => value.Word == "трактористка");
        Assert.AreEqual("трактор", path.Base);
        Assert.AreEqual("трактористка", path.DerivedLemma);
        Assert.IsFalse(path.IsConditional);
        CollectionAssert.AreEqual(new[] { "accepted-lemma-derivational-suffix-ist.v0", "derivational_suffix.agent_feminine_ka.v1" }, path.RuleIds.ToArray());
        CollectionAssert.AreEqual(new[] { "трактор", "ист", "ка" }, path.Segments.Select(value => value.Surface).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 7, 10 }, path.Segments.Select(value => value.Start).ToArray());
        CollectionAssert.AreEqual(new[] { 7, 3, 2 }, path.Segments.Select(value => value.Length).ToArray());
        CollectionAssert.AreEqual(new[] { "Съществително:41,Женски" }, path.FullTags!.ToArray());
        Assert.IsTrue(session.AnalyzeWord("трактористка", 256).IsKnown);
        WordDecomposition decomposition = session.DecomposeWord("трактористка");
        Assert.AreEqual("трактор", decomposition.Base);
        Assert.IsNull(decomposition.ProductiveGeneration);
    }

    [TestMethod]
    [DataRow("жълто-зелен", null)]
    [DataRow("жълто-зеления", "productive.adjective.type76.v0")]
    public void Known_color_forms_do_not_claim_a_coordinate_compound_path(string query, string? selectedRule)
    {
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.IsTrue(word.IsKnown);
        Assert.AreEqual(0, word.GraphCandidates.Count);
        AssertSearchFlags(word, false, false, false, false, false, false, false);
        WordDecomposition value = session.DecomposeWord(query);
        Assert.AreEqual(selectedRule, value.RuleId);
        Assert.AreEqual(selectedRule is not null, value.HasEvidence);
        Assert.IsNull(value.ProductiveGeneration);
    }

    [TestMethod]
    [DataRow("жълто--зелен", false, 0)]
    [DataRow("жълто-книга", true, 0)]
    public void Unsupported_color_relation_keeps_conditional_or_incomplete_evidence(string query, bool incomplete, int graphCount)
    {
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.IsFalse(word.IsKnown);
        Assert.AreEqual(0, word.Readings.Count);
        Assert.IsNull(word.BaseEvidence);
        Assert.AreEqual(graphCount, word.GraphCandidates.Count);
        Assert.IsTrue(word.GraphCandidates.All(value => value.IsConditional && !value.RuleIds.Contains("productive.adjective.coordinate_color.v1")));

        AssertSearchFlags(word, incomplete, false, false, false, false, false, false);
        Assert.IsFalse(session.DecomposeWord(query).HasEvidence);
    }

    [TestMethod]
    public void Complete_package_preserves_conditional_donor_authority_for_a_well_formed_root()
    {
        const string query = "бяло-зелено-червен";
        Assert.IsEmpty(session.Lookup(query, LookupMode.Exact, 256));
        WordAnalysis word = session.AnalyzeWord(query, 256);
        Assert.IsFalse(word.IsKnown);
        // Preserve the conditional noun-model proof, without claiming a grammatical color-adjective reading.
        WordDerivation added = word.GraphCandidates.Single(value => value.Base == query &&
            value.ConditionalRootProgramId == "productive.noun.feminine_type50_en.v0");
        Assert.IsTrue(added.IsConditional);
        Assert.AreEqual("Съществително:50,Женски", added.ConditionalRootBaseFullTag);
        Assert.AreEqual(2, added.ConditionalRootSupportCount);
        CollectionAssert.AreEqual(new[] { "песен", "суперпесен" }, added.ConditionalSupportingLemmas!.ToArray());
        Assert.IsTrue(added.UnmetExactSelfReadingRequirements!.Any(value => value.InputSurface == query &&
            value.FullTag == "Съществително:50,Женски" && value.EdgeId == "program|productive.noun.feminine_type50_en.v0|base"));
        AssertSearchFlags(word, false, false, false, false, false, false, false);
    }
    private static void AssertSearchFlags(WordAnalysis word, params bool[] expected)
    {
        CollectionAssert.AreEqual(expected, new[] { word.GraphSearchIncomplete, word.GraphCandidatesTruncated,
            word.CandidateSearchLimited, word.CandidateEvidenceLimited, word.KnownBaseSearchLimited,
            word.BaseTypeSearchLimited, word.BaseTypeEvidenceLimited });
    }

    private static string Key(string form, string lemma, string? tag) => JsonSerializer.Serialize(new[] { form, lemma, tag });

    [TestMethod]
    [DataRow("Италия", "Италия", "Държава:196", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("Чикаго", "Чикаго", "Град:198", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("Берлин", "Берлин", "Столица:197", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("Тунджа", "Тунджа", "Място:195", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("Тунджа", "Тунджа", "Наименование:194", MorphologicalPartOfSpeech.Unknown)]
    [DataRow("Иван", "Иван", "Име:205,Мъжки", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("Иван", "Иван", "Място:195", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("Иванов", "иванов", "Прилагателно:76,Мъжки", MorphologicalPartOfSpeech.Adjective)]
    [DataRow("Иванов", "Иванов", "Фамилия:201,Мъжки", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("София", "София", "Място:195", MorphologicalPartOfSpeech.ProperNoun)]
    [DataRow("маса", "маса", "Съществително:41,Женски", MorphologicalPartOfSpeech.Noun)]
    public void Complete_package_projects_explicit_geographic_categories_and_preserves_controls(
        string query, string lemma, string rawTag, MorphologicalPartOfSpeech expected)
    {
        Assert.AreEqual(candidate.GetProperty("packageId").GetString(), session.Provenance.PackageId);
        WordReading reading = session.AnalyzeWord(query, 32).Readings.Single(r =>
            r.Lemma == lemma && r.PartOfSpeech == rawTag);
        Assert.AreEqual(expected, reading.Features.PartOfSpeech);
        Assert.AreEqual(rawTag, reading.Features.RawTag);
        Assert.AreEqual(expected == MorphologicalPartOfSpeech.Unknown, reading.Features.HasUnrecognizedFeatures);
        Assert.IsFalse(reading.Features.HasConflictingFeatures);
        if (expected is MorphologicalPartOfSpeech.ProperNoun or MorphologicalPartOfSpeech.Unknown)
        {
            Assert.IsNull(reading.Features.Number);
            Assert.IsNull(reading.Features.Article);
            Assert.IsNull(reading.Features.Person);
            Assert.IsNull(reading.Features.Tense);
            Assert.IsNull(reading.Features.VerbForm);
        }
    }
}
