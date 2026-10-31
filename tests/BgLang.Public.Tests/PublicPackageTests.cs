using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

// Complete candidate checks use public API fields and independent literal records only.
[TestClass]
public sealed partial class PublicPackageTests
{
    private static BulgarianLanguageSession session = null!;
    private static string manifest = null!;
    private static JsonElement candidate;
    private static JsonElement[] cases = [];
    private static JsonElement[] typedTags = [];

    [TestMethod]
    [DataRow("аванпост", "legacy-base:64", "Съществително:1,Мъжки", "ава\u0300нпост", StressOrigin.LegacyStored, 2)]
    [DataRow("абата", "legacy-base:824640", "Съществително:41,Женски,Членуван", "аба\u0300та", StressOrigin.LegacyDisplayInferred, 2)]
    [DataRow("алпаката", "legacy-base:839872", "Съществително:41,Женски,Членуван", "алпака\u0300та", StressOrigin.LegacyDisplayInferred, 5)]
    public void Stress_public_query_returns_captured_owner_bound_realizations_without_source_promotion(
        string form, string id, string tag, string displayed, StressOrigin origin, int offset)
    {
        Assert.AreEqual("1", Environment.GetEnvironmentVariable("BGLANG_STRESS_REQUIRE_MANIFEST"));
        StressEvidence actual = session.GetStressEvidence("😀 " + form + ".", new(1, id, form, tag));
        Assert.AreEqual(StressSelectionStatus.Matched, actual.SelectionStatus); Assert.AreEqual("😀 " + displayed + ".", actual.RenderedText);
        StressTokenEvidence token = actual.Tokens.Single(); Assert.AreEqual(3, token.Start); Assert.AreEqual(form.Length, token.Length);
        StressRealization realization = token.Readings.Where(row => row.Reading.Form == form && row.Reading.PartOfSpeech == tag)
            .SelectMany(row => row.Owners).Where(owner => owner.EntryId == id).First().Realizations.Single();
        Assert.AreEqual(origin, realization.Origin); Assert.AreEqual(3 + offset, realization.Marks.Single().Utf16Position);
        if (origin == StressOrigin.LegacyDisplayInferred)
        { Assert.AreEqual(StressStatus.Inferred, token.Status); Assert.IsNull(realization.LegacyUtf16Positions); CollectionAssert.AreEqual(new[] { offset }, realization.LegacyDisplayUtf16Positions!.ToArray()); }
        else Assert.AreEqual(StressStatus.Supported, token.Status);
        StressEvidence disconnected = session.GetStressEvidence(form, new(0, " " + id, form, tag));
        Assert.AreEqual(StressSelectionStatus.NotConnected, disconnected.SelectionStatus); Assert.AreEqual(form, disconnected.RenderedText);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.AreEqual(cancel.Token, Assert.Throws<OperationCanceledException>(() =>
            session.GetStressEvidence(form, cancellationToken: cancel.Token)).CancellationToken);
        Assert.AreEqual(displayed, session.GetStressEvidence(form, new(0, id, form, tag)).RenderedText);
    }

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
            "ReadingInventory", "Metrics", "Spelling", "Profile", "ProperNames", "Context", "Syntax", "Validation", "PredicateLinks", "WordConstructions" },
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
        string? repairManifest = Environment.GetEnvironmentVariable("BGLANG_PREVIEW6_REPAIR_MANIFEST");
        if (Environment.GetEnvironmentVariable("BGLANG_PREVIEW6_REPAIR_REQUIRE_MANIFEST") == "1")
            Assert.IsFalse(string.IsNullOrWhiteSpace(repairManifest), "The required repair successor cannot fall back or skip.");
        bool repair = repairManifest is not null;
        string? stressManifest = Environment.GetEnvironmentVariable("BGLANG_STRESS_MANIFEST");
        if (Environment.GetEnvironmentVariable("BGLANG_STRESS_REQUIRE_MANIFEST") == "1")
            Assert.IsFalse(string.IsNullOrWhiteSpace(stressManifest), "The required stress successor cannot fall back to an older package.");
        bool stress = stressManifest is not null;
        string? lexicalManifest = Environment.GetEnvironmentVariable("BGLANG_LEXICAL_MANIFEST");
        if (Environment.GetEnvironmentVariable("BGLANG_LEXICAL_REQUIRE_MANIFEST") == "1")
            Assert.IsFalse(string.IsNullOrWhiteSpace(lexicalManifest), "The required annotated successor cannot fall back to the historical public lock.");
        bool lexical = lexicalManifest is not null;
        string fixtureName = repair ? "preview6-repair-package.v1.json" : stress ? "stress-annotated-package.v1.json" : lexical ? "lexical-annotated-package.v1.json" : "public-input-lock.v1.json";
        byte[] identity = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", fixtureName));
        if (repair) Assert.AreEqual("01460077278b1b315e5e9c17199b88340f5916ba187f1bc9c17a47290d163116", Convert.ToHexStringLower(SHA256.HashData(identity)));
        else if (stress) Assert.AreEqual("59e28d79691ac42fe6e9db79417032bd55709530a7056917f7baf7c764de1ab9", Convert.ToHexStringLower(SHA256.HashData(identity)));
        else if (lexical) Assert.AreEqual("4a4da62b4e33643429bc4f7fea50a86e0e73ca6380ca55c6d2b7f972e130b3ba", Convert.ToHexStringLower(SHA256.HashData(identity)));
        using var document = JsonDocument.Parse(identity);
        Assert.AreEqual(repair ? "bglang.preview6-repair-public-package.v1" : stress ? "bglang.stress-public-package.v1" : lexical ? "bglang.lexical-public-package.v1" : "bglang.public-input-lock.v1",
            document.RootElement.GetProperty("schema").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("contractComplete").GetBoolean(), "Complete public input contract is required; missing inputs never skip.");
        candidate = document.RootElement.GetProperty("expectedCandidate").Clone();
        string? supplied = Environment.GetEnvironmentVariable("BGLANG_PUBLIC_PACKAGE_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(supplied), "Set the explicit complete candidate manifest.");
        Assert.IsTrue(Path.IsPathFullyQualified(supplied!));
        manifest = Path.GetFullPath(supplied!);
        if (repair) Assert.AreEqual(Path.GetFullPath(repairManifest!), manifest, true, "Public and generic controls must select the same exact repair successor.");
        if (stress) Assert.AreEqual(Path.GetFullPath(stressManifest!), manifest, true, "Public and generic controls must select the same exact stress successor.");
        else if (lexical) Assert.AreEqual(Path.GetFullPath(lexicalManifest!), manifest, true, "Public and generic controls must select the same exact annotated package.");
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
    public void Marked_compounds_preserve_public_raw_tokens_and_utf16_spans()
    {
        const string input = "😀 наи\u0306-силен по-си\u0300лен.";
        TextAnalysis actual = session.AnalyzeText(input);
        Assert.AreEqual(input, actual.Text);
        CollectionAssert.AreEqual(new[] { "Symbol@0:2:😀", "Word@3:10:наи\u0306-силен", "Word@14:9:по-си\u0300лен", "Punctuation@23:1:." },
            actual.Tokens.Select(token => $"{token.Kind}@{token.Start}:{token.Length}:{token.Text}").ToArray());
        Assert.IsNotNull(actual.Validation);
        Assert.IsTrue(actual.Validation.Available);
        Assert.IsFalse(actual.Validation.Diagnostics.Any(row => row.Code == "invalid_word_phrase_compact_separator_order"));
        Assert.IsNull(actual.Validation.SubordinateCommaDiagnostics);
    }

    [TestMethod]
    [DataRow("по-ле\u0300сен", 9, 12)]
    [DataRow("по-л\u0450сен", 8, 11)]
    [DataRow("ПО-ЛЕ\u0300СЕН", 9, 12)]
    [DataRow("ПО-Л\u0400СЕН", 8, 11)]
    public void Grave_e_compounds_preserve_public_raw_tokens_and_utf16_spans(string form, int length, int periodStart)
    {
        string input = "😀 " + form + ".";
        TextAnalysis actual = session.AnalyzeText(input);
        Assert.AreEqual(input, actual.Text);
        CollectionAssert.AreEqual(new[] { "Symbol@0:2:😀", $"Word@3:{length}:{form}", $"Punctuation@{periodStart}:1:." },
            actual.Tokens.Select(token => $"{token.Kind}@{token.Start}:{token.Length}:{token.Text}").ToArray());
        TextReadingOccurrence occurrence = actual.ReadingInventory!.Occurrences.Single();
        Assert.AreEqual(form, occurrence.Text); Assert.AreEqual(3, occurrence.Start); Assert.AreEqual(length, occurrence.Length);
        Assert.IsNotNull(actual.Validation); Assert.IsTrue(actual.Validation.Available);
        Assert.IsFalse(actual.Validation.Diagnostics.Any(row => row.Code == "invalid_word_phrase_compact_separator_order"));
        Assert.IsNull(actual.Validation.SubordinateCommaDiagnostics);
    }

    // Literal tuples are independently established by the accepted auxiliary authority.
    [TestMethod]
    [DataRow("бих", "Глагол:143,1л.,Минало_свършено", 3)]
    [DataRow("би", "Глагол:143,2л.,Минало_свършено", 7)]
    [DataRow("би", "Глагол:143,3л.,Минало_свършено", 7)]
    [DataRow("бихме", "Глагол:143,1л.,Минало_свършено,мн.ч.", 3)]
    [DataRow("бихте", "Глагол:143,2л.,Минало_свършено,мн.ч.", 3)]
    [DataRow("биха", "Глагол:143,3л.,Минало_свършено,мн.ч.", 3)]
    [DataRow("бидох", "Глагол:143,1л.,Минало_свършено", 1)]
    [DataRow("биде", "Глагол:143,2л.,Минало_свършено", 3)]
    [DataRow("биде", "Глагол:143,3л.,Минало_свършено", 3)]
    [DataRow("бидохме", "Глагол:143,1л.,Минало_свършено,мн.ч.", 1)]
    [DataRow("бидохте", "Глагол:143,2л.,Минало_свършено,мн.ч.", 1)]
    [DataRow("бидоха", "Глагол:143,3л.,Минало_свършено,мн.ч.", 1)]
    public void Auxiliary_readings_preserve_literal_form_tag_and_owner(string form, string tag, int totalReadings)
    {
        WordReading[] rows = session.Lookup(form, LookupMode.Exact, 256).ToArray();
        Assert.AreEqual(totalReadings, rows.Length);
        CollectionAssert.AreEqual(rows, session.AnalyzeWord(form, 256).Readings.ToArray());
        WordReading reading = rows.Single(row => row.Form == form && row.Lemma == "бъда" && row.PartOfSpeech == tag);
        Assert.AreEqual(1d, reading.Score); Assert.AreEqual(session.Provenance.LexiconSource, reading.Source);
        StressEvidence evidence = session.GetStressEvidence(form, new(0, "legacy-base:6202240", form, tag));
        Assert.AreEqual(StressSelectionStatus.Matched, evidence.SelectionStatus);
        StressOwnerEvidence owner = evidence.Tokens.Single().Readings
            .Where(row => row.Reading.Form == form && row.Reading.Lemma == "бъда" && row.Reading.PartOfSpeech == tag)
            .SelectMany(row => row.Owners).Single(row => row.EntryId == "legacy-base:6202240");
        Assert.IsFalse(owner.Realizations.Any(row =>
            row.Origin is StressOrigin.LegacyStored or StressOrigin.LegacyCarried or StressOrigin.LegacyDisplayInferred));
    }

    [TestMethod]
    [DataRow("бих", "бия", "Глагол:163ti,1л.,Минало_свършено")]
    [DataRow("биха", "бия", "Глагол:163ti,3л.,Минало_свършено,мн.ч.")]
    [DataRow("би", "би", "Частица:191")]
    [DataRow("биде", "биде", "Съществително:65,Среден")]
    public void Auxiliary_surface_homographs_keep_their_literal_readings(string form, string lemma, string tag)
    {
        WordReading[] rows = session.Lookup(form, LookupMode.Exact, 256).ToArray();
        Assert.AreEqual(1, rows.Count(row => row.Form == form && row.Lemma == lemma && row.PartOfSpeech == tag));
        Assert.IsTrue(rows.Any(row => row.Lemma == "бъда" && row.Features.ParadigmId == "143"));
    }

    [TestMethod]
    [DataRow("Ще дойда само когато мога.")]
    [DataRow("Само аз ще дойда когато мога.")]
    public void Public_comma_trigger_unavailability_is_preserved(string input)
    {
        const string reason = "subordinate_comma:trigger_authority_unavailable";
        TextAnalysis disabled = session.AnalyzeText(input);
        TextAnalysis enabled = session.AnalyzeText(input, false,
            new TextContextOptions { IncludeSubordinateCommaDiagnostics = true }, default);
        Assert.AreEqual(input, enabled.Text);
        Assert.IsNotNull(disabled.Validation);
        Assert.IsNull(disabled.Validation.SubordinateCommaDiagnostics);
        Assert.IsFalse(disabled.Validation.UnavailableChecks.Contains(reason));
        Assert.IsNotNull(enabled.Validation);
        Assert.IsTrue(enabled.Validation.Available);
        Assert.IsNull(enabled.Validation.UnavailableReason);
        Assert.IsNotNull(enabled.Validation.SubordinateCommaDiagnostics);
        Assert.AreEqual(0, enabled.Validation.SubordinateCommaDiagnostics.Count);
        Assert.IsTrue(enabled.Validation.UnavailableChecks.Contains(reason));
        TextReadingInventory inventory = enabled.ReadingInventory!;
        TextReadingOccurrence occurrence = inventory.Occurrences.Single(row => row.Text == "когато");
        CollectionAssert.AreEqual(new[] { "Наречие:188" }, inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Select(row => row.Raw.PartOfSpeech).ToArray());
        CollectionAssert.AreEqual(disabled.Diagnostics.ToArray(), enabled.Diagnostics.ToArray());
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
    [TestMethod]
    [DataRow("суперработник", "работник", "суперработник", "Съществително:14a,Мъжки")]
    [DataRow("свръхсупертракторите", "трактор", "свръхсупертрактор", "Съществително:7,Мъжки,мн.ч.,Членуван")]
    [DataRow("супертрактор", "трактор", "супертрактор", "Съществително:7,Мъжки")]
    [DataRow("суперхубав", "хубав", "суперхубав", "Прилагателно:76,Мъжки")]
    [DataRow("суперкнига", "книга", "суперкнига", "Съществително:41,Женски")]
    public void Complete_graph_root_cells_bind_their_original_positions_and_source(
        string word, string root, string lemma, string tag)
    {
        Assert.AreEqual("bglang-neutral:36b0b69685d6d3e9", session.Provenance.PackageId);
        Assert.AreEqual("word-formation-source-v0", session.Provenance.WordFormationSource);
        WordAnalysis actual = session.AnalyzeWord(word, 256);
        Assert.IsFalse(actual.IsKnown); Assert.IsEmpty(actual.Readings);
        Assert.IsFalse(actual.GraphSearchIncomplete); Assert.IsFalse(actual.GraphCandidatesTruncated);
        Assert.IsTrue(actual.GraphExactRootCandidatesComplete);
        WordDerivation path = actual.GraphCandidates[0];
        Assert.IsFalse(path.IsConditional); Assert.IsNull(path.UnmetExactSelfReadingRequirements);
        Assert.AreEqual(root, path.Base); Assert.AreEqual(lemma, path.DerivedLemma);
        Assert.AreEqual("word-formation-source-v0", path.Source);
        CollectionAssert.AreEqual(new[] { tag }, path.FullTags!.ToArray());
    }

    [TestMethod]
    [DataRow("Супертрактор работи.", false, false, "Съществително:7,Мъжки")]
    [DataRow("Супертрактор работят.", true, false, "Съществително:7,Мъжки")]
    [DataRow("Свръхсупертракторите работят.", false, true, "Съществително:7,Мъжки,мн.ч.,Членуван")]
    public void Complete_package_graph_subjects_produce_nonempty_qualified_context_and_syntax(
        string text, bool mismatch, bool conditionalTailTruncated, string literalTag)
    {
        const string code = "productive_morphology_agreement_mismatch";
        TextAnalysis actual = session.AnalyzeText(text);
        Assert.AreEqual(text, actual.Text);
        Assert.AreEqual("bounded-context.v2", actual.Context!.Version);
        Assert.AreEqual("bounded-syntax-roles.v2", actual.Syntax!.Version);
        Assert.AreEqual("bounded-validation.v2", actual.Validation!.Version);
        WordAnalysis subject = actual.Tokens[0].Analysis!;
        Assert.IsFalse(subject.IsKnown); Assert.IsEmpty(subject.Readings);
        Assert.IsEmpty(GraphBridgeComplete(actual, 0));
        Assert.IsFalse(subject.GraphSearchIncomplete);
        Assert.IsTrue(subject.GraphExactRootCandidatesComplete);
        Assert.AreEqual(conditionalTailTruncated, subject.GraphCandidatesTruncated);
        if (conditionalTailTruncated) Assert.HasCount(32, subject.GraphCandidates);
        CollectionAssert.AreEqual(new[] { literalTag }, subject.GraphCandidates[0].FullTags!.ToArray());
        Assert.IsNull(subject.GraphCandidates[0].Features.Single().Person,
            "A noun has no lexical Person; third is the effective finite-agreement person only.");
        Assert.IsNotEmpty(GraphBridgeComplete(actual, 1));
        GraphBridgeAssertReferences(actual);
        bool compatible = actual.Context.Sentences.SelectMany(sentence => sentence.Candidates).Any(candidate =>
            candidate.Selectable && candidate.Roles.Any(role => role.Role == TextContextRole.Subject &&
                role.GraphReadings?.Any(reference => reference.TokenIndex == 0) == true) &&
            candidate.Roles.Any(role => role.Role == TextContextRole.Predicate && role.Readings.Any(reference => reference.TokenIndex == 1)));
        if (mismatch)
        {
            Assert.IsFalse(compatible);
            TextDiagnostic diagnostic = actual.Diagnostics.Single(item => item.Code == code);
            // Qualified advice anchors the first graph-affected role; the exact predicate is separate support.
        Assert.AreEqual(0, diagnostic.Start); Assert.AreEqual(12, diagnostic.Length);
            Assert.AreEqual("productive_subject_predicate_graph.v1", diagnostic.RuleId);
            Assert.IsNull(diagnostic.ProjectedText);
            Assert.IsTrue(diagnostic.Readings!.Any(reference => reference.TokenIndex == 1));
            Assert.IsTrue(diagnostic.GraphReadings!.Any(reference => reference.TokenIndex == 0 &&
                GraphBridgeResolve(actual, reference).Tag == literalTag));
        }
        else
        {
            Assert.IsTrue(compatible, "A complete-package graph-only positive must be nonempty.");
            Assert.IsFalse(actual.Diagnostics.Any(item => item.Code == code));
            Assert.IsTrue(actual.Syntax.Sentences.SelectMany(sentence => sentence.Candidates).Any(candidate =>
                candidate.Role == TextSyntaxRole.Subject && candidate.Targets.Concat(candidate.Context).Any(support =>
                    support.GraphReadings?.Any(reference => reference.TokenIndex == 0) == true)));
            Assert.IsTrue(actual.Syntax.Sentences.SelectMany(sentence => sentence.Candidates).Any(candidate =>
                candidate.Role == TextSyntaxRole.Predicate && candidate.Targets.Any(support =>
                    support.Readings.Any(reference => reference.TokenIndex == 1))));
        }
        Assert.IsNull(actual.WordConstructions);
        Assert.IsFalse(actual.Diagnostics.Any(item => item.Code == "full_article_expected_for_subject_candidate"),
            "Graph morphology cannot acquire exact-only article authority.");
    }

    [TestMethod]
    public void Graph_bridge_shared_unicode_analyses_preserve_original_occurrence_indices()
    {
        const string text = "😀 и\u0300.\r\nСупертрактор работи. Супертрактор работи.";
        Assert.AreEqual(49, text.Length);
        TextAnalysis actual = session.AnalyzeText(text);
        Assert.AreEqual(text, actual.Text); Assert.HasCount(9, actual.Tokens);
        Assert.AreEqual("😀", actual.Tokens[0].Text); Assert.AreEqual(2, actual.Tokens[0].Length);
        Assert.AreEqual("и\u0300", actual.Tokens[1].Text); Assert.AreEqual(3, actual.Tokens[1].Start);
        Assert.AreEqual(8, actual.Tokens[3].Start); Assert.AreEqual(29, actual.Tokens[6].Start);
        Assert.AreSame(actual.Tokens[3].Analysis, actual.Tokens[6].Analysis);
        GraphBridgeAssertReferences(actual);
        TextContextRoleSelection[] roles = GraphBridgeRoles(actual).Where(role => role.Role == TextContextRole.Subject &&
            role.GraphReadings is { Count: > 0 }).ToArray();
        foreach ((int token, int start) in new[] { (3, 8), (6, 29) })
        {
            Assert.IsTrue(roles.Any(role => role.TokenIndex == token && role.Start == start && role.Length == 12 &&
                role.GraphReadings!.Any(reference => reference.TokenIndex == token && reference.GraphCandidateIndex == 0 && reference.FullTagIndex == 0)));
            Assert.AreEqual("Супертрактор", text.Substring(start, 12));
        }
    }

    private static IEnumerable<TextContextRoleSelection> GraphBridgeRoles(TextAnalysis actual) =>
        actual.Context!.Sentences.SelectMany(sentence => sentence.LocalRoles.Concat(sentence.SelectedRoles)
            .Concat(sentence.Candidates.SelectMany(candidate => candidate.Roles)));

    private static IReadOnlyList<TextReadingSnapshot> GraphBridgeComplete(TextAnalysis actual, int tokenIndex)
    {
        TextReadingInventory inventory = actual.ReadingInventory!;
        TextReadingOccurrence occurrence = inventory.Occurrences.Single(value => value.TokenIndex == tokenIndex);
        TokenAnalysis token = actual.Tokens[tokenIndex];
        Assert.AreEqual(token.Start, occurrence.Start); Assert.AreEqual(token.Length, occurrence.Length);
        Assert.AreEqual(token.Text, occurrence.Text);
        TextReadingSet set = inventory.Sets[occurrence.ReadingSetIndex];
        return set.SupportReadings ?? set.CanonicalReadings;
    }

    private static (WordDerivation Path, string Tag) GraphBridgeResolve(TextAnalysis actual, TextGraphReadingReference reference)
    {
        Assert.IsTrue((uint)reference.TokenIndex < (uint)actual.Tokens.Count);
        TokenAnalysis token = actual.Tokens[reference.TokenIndex]; WordAnalysis word = token.Analysis!;
        Assert.AreEqual(TokenKind.Word, token.Kind);
        Assert.AreEqual(token.Text, actual.Text.Substring(token.Start, token.Length));
        Assert.IsTrue((uint)reference.GraphCandidateIndex < (uint)word.GraphCandidates.Count);
        WordDerivation path = word.GraphCandidates[reference.GraphCandidateIndex];
        Assert.IsNotNull(path.FullTags); Assert.IsTrue((uint)reference.FullTagIndex < (uint)path.FullTags.Count);
        Assert.AreEqual(word.NormalizedWord, path.Word); Assert.IsFalse(path.IsConditional);
        Assert.IsNotNull(path.DerivedLemma); Assert.IsTrue(path.UnmetExactSelfReadingRequirements is null or { Count: 0 });
        Assert.IsFalse(word.GraphSearchIncomplete); Assert.IsTrue(word.GraphExactRootCandidatesComplete);
        Assert.AreEqual("word-formation-source-v0", path.Source);
        Assert.IsEmpty(GraphBridgeComplete(actual, reference.TokenIndex)); Assert.IsEmpty(word.Readings);
        return (path, path.FullTags[reference.FullTagIndex]);
    }

    private static void GraphBridgeAssertReferences(TextAnalysis actual)
    {
        TextContextRoleSelection[] roles = GraphBridgeRoles(actual).ToArray();
        TextSyntaxSupport[] supports = actual.Syntax!.Sentences.SelectMany(sentence => sentence.Candidates)
            .SelectMany(candidate => candidate.Targets.Concat(candidate.Context)).ToArray();
        foreach (TextContextRoleSelection role in roles)
            foreach (TextGraphReadingReference reference in role.GraphReadings ?? [])
            { Assert.AreEqual(role.TokenIndex, reference.TokenIndex); _ = GraphBridgeResolve(actual, reference); Assert.IsEmpty(role.Readings); }
        foreach (TextSyntaxSupport support in supports)
            foreach (TextGraphReadingReference reference in support.GraphReadings ?? [])
            { Assert.AreEqual(support.TokenIndex, reference.TokenIndex); _ = GraphBridgeResolve(actual, reference); Assert.AreEqual("LicensedGraphMorphology", support.Basis); Assert.IsEmpty(support.Readings); }
        foreach (TextGraphReadingReference reference in actual.Diagnostics.SelectMany(diagnostic => diagnostic.GraphReadings ?? []))
            _ = GraphBridgeResolve(actual, reference);
        IEnumerable<TextReadingReference> exact = roles.SelectMany(role => role.Readings)
            .Concat(supports.SelectMany(support => support.Readings))
            .Concat(actual.Diagnostics.SelectMany(diagnostic => diagnostic.Readings ?? []));
        foreach (TextReadingReference reference in exact)
        {
            IReadOnlyList<TextReadingSnapshot> rows = GraphBridgeComplete(actual, reference.TokenIndex);
            Assert.IsTrue((uint)reference.CompleteReadingIndex < (uint)rows.Count);
            Assert.AreEqual("neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7",
                rows[reference.CompleteReadingIndex].Raw.Source);
        }
    }

    [TestMethod]
    public void Graph_context_preserves_complete_exact_homographs_and_both_compatible_predicate_cells()
    {
        TextAnalysis actual = session.AnalyzeText("Супертрактор работи.");
        IReadOnlyList<TextReadingSnapshot> exact = GraphBridgeComplete(actual, 1);
        CollectionAssert.AreEqual(new[]
        {
            "Съществително:41,Женски,мн.ч.",
            "Глагол:173ti,1л.,Сегашно,Повелително",
            "Глагол:173ti,2л.,Минало_свършено",
            "Глагол:173ti,3л.,Минало_свършено",
            "Глагол:173ti,3л.,Сегашно"
        }, exact.Select(row => row.Raw.PartOfSpeech).ToArray());
        CollectionAssert.AreEqual(new[] { "работа", "работя", "работя", "работя", "работя" },
            exact.Select(row => row.Raw.Lemma).ToArray());
        Assert.IsTrue(exact.All(row => row.Raw.Form == "работи" && row.Raw.Source ==
            "neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7"));
        TextContextSentence sentence = actual.Context!.Sentences.Single();
        Assert.AreEqual(TextContextSelectionStatus.Selected, sentence.Status);
        TextContextRoleSelection predicate = sentence.SelectedRoles.Single(role => role.Role == TextContextRole.Predicate);
        CollectionAssert.AreEqual(new[] { new TextReadingReference(1, 3), new TextReadingReference(1, 4) }, predicate.Readings.ToArray());
        Assert.IsNull(predicate.GraphReadings);
        TextContextRoleSelection subject = sentence.SelectedRoles.Single(role => role.Role == TextContextRole.Subject);
        Assert.IsEmpty(subject.Readings);
        CollectionAssert.AreEqual(new[] { new TextGraphReadingReference(0, 0, 0) }, subject.GraphReadings!.ToArray());
        // No nominal or imperative row is removed from the original exact inventory;
        // finite-compatible supports are a response-local overlay only.
    }

    [TestMethod]
    [DataRow("Суперхубав супертрактор работи.", 1, "3,4")]
    [DataRow("Новият супертрактор работи.", 1, "3,4")]
    [DataRow("Нова суперкнига пристигна.", 1, "2")]
    [DataRow("Нови супертрактори работят.", 1, "0")]
    [DataRow("Новият голям червен супертрактор работи.", 3, "3,4")]
    public void Graph_nominal_phrase_public_query_binds_each_attribute_to_one_actual_head(string text, int head, string predicateIndices)
    {
        TextAnalysis actual = session.AnalyzeText(text);
        GraphBridgeAssertReferences(actual);
        TextContextSentence sentence = actual.Context!.Sentences.Single();
        TextContextCandidate candidate = sentence.Candidates.Single(c => c.AuthorityLane == "productive_nominal_phrase_graph.v1");
        Assert.IsTrue(candidate.Selectable); Assert.AreEqual(TextContextSelectionStatus.Selected, sentence.Status);
        TextContextRoleSelection[] attributes = candidate.Roles.Where(r => r.Role == TextContextRole.Attribute).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(0, head).ToArray(), attributes.Select(r => r.TokenIndex).ToArray());
        CollectionAssert.AreEqual(new[] { new TextGraphReadingReference(head, 0, 0) }, candidate.Roles.Single(r => r.Role == TextContextRole.Subject).GraphReadings!.ToArray());
        CollectionAssert.AreEqual(predicateIndices.Split(',').Select(int.Parse).Select(i => new TextReadingReference(head + 1, i)).ToArray(),
            candidate.Roles.Single(r => r.Role == TextContextRole.Predicate).Readings.ToArray());
        if (text.StartsWith("Суперхубав", StringComparison.Ordinal))
            CollectionAssert.AreEqual(new[] { new TextGraphReadingReference(0, 0, 0) }, attributes[0].GraphReadings!.ToArray());
        foreach (TextContextRoleSelection attribute in attributes)
        {
            Assert.IsTrue(attribute.Readings.Count > 0 || attribute.GraphReadings is { Count: > 0 });
            TextSyntaxCandidate syntax = actual.Syntax!.Sentences.Single().Candidates.Single(c => c.Role == TextSyntaxRole.Attribute && c.Start == attribute.Start);
            Assert.AreEqual("ProductiveNominalPhraseAttribute", syntax.RuleId);
            Assert.AreEqual(attribute.TokenIndex, syntax.Targets.Single().TokenIndex);
            Assert.AreEqual(head, syntax.Context.Single().TokenIndex);
            CollectionAssert.AreEqual(candidate.Roles.Single(r => r.Role == TextContextRole.Subject).GraphReadings!.ToArray(), syntax.Context.Single().GraphReadings!.ToArray());
        }
        TextSyntaxCandidate subject = actual.Syntax!.Sentences.Single().Candidates.Single(c => c.Role == TextSyntaxRole.Subject);
        Assert.AreEqual(0, subject.Start); Assert.AreEqual(actual.Tokens[head].Start + actual.Tokens[head].Length, subject.Length);
        Assert.AreEqual(head, subject.Targets.Single().TokenIndex);
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Code is "productive_attribute_agreement_mismatch" or "productive_morphology_agreement_mismatch"));
        Assert.IsFalse(actual.Tokens[head].Analysis!.IsKnown); Assert.IsEmpty(GraphBridgeComplete(actual, head));
        if (text.StartsWith("Новият", StringComparison.Ordinal))
        {
            Assert.AreEqual(MorphologicalArticle.Full, GraphBridgeComplete(actual, 0)[0].Features.Article);
            Assert.AreNotEqual(MorphologicalArticle.Full, actual.Tokens[head].Analysis!.GraphCandidates[0].Features[0].Article);
        }
    }

    [TestMethod]
    [DataRow("Суперхубав суперкнига работи.", "productive_attribute_agreement_mismatch")]
    [DataRow("Нови супертрактор работи.", "productive_attribute_agreement_mismatch")]
    [DataRow("Суперхубав супертрактор работят.", "productive_morphology_agreement_mismatch")]
    public void Graph_nominal_phrase_public_mismatch_identifies_only_the_measured_relation(string text, string expectedCode)
    {
        TextAnalysis actual = session.AnalyzeText(text); GraphBridgeAssertReferences(actual);
        TextContextCandidate candidate = actual.Context!.Sentences.Single().Candidates.Single(c => c.AuthorityLane == "productive_nominal_phrase_graph.v1");
        Assert.IsFalse(candidate.Selectable); Assert.IsEmpty(actual.Context.Sentences.Single().SelectedRoles);
        TextDiagnostic diagnostic = actual.Diagnostics.Single(d => d.Code == expectedCode);
        Assert.AreEqual("productive_nominal_phrase_graph.v1", diagnostic.RuleId); Assert.IsNull(diagnostic.ProjectedText);
        Assert.IsNotEmpty(diagnostic.GraphReadings!);
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Code == (expectedCode == "productive_attribute_agreement_mismatch"
            ? "productive_morphology_agreement_mismatch" : "productive_attribute_agreement_mismatch")));
        if (expectedCode == "productive_attribute_agreement_mismatch")
        {
            CollectionAssert.AreEqual(new[] { 3, 4 }, candidate.Roles.Single(r => r.Role == TextContextRole.Predicate).Readings.Select(r => r.CompleteReadingIndex).ToArray());
            Assert.IsTrue(diagnostic.GraphReadings!.All(r => r.TokenIndex is 0 or 1));
            Assert.IsTrue((diagnostic.Readings ?? []).All(r => r.TokenIndex == 0));
        }
        else
        {
            Assert.IsTrue(diagnostic.GraphReadings!.All(r => r.TokenIndex == 1), "Graph Attribute cannot supply finite-relation diagnostic authority.");
            Assert.IsTrue(diagnostic.Readings!.All(r => r.TokenIndex == 2));
        }
    }

    [TestMethod]
    [DataRow("Супертрактор нов работи.")][DataRow("Супертрактор суперкнига работи.")]
    [DataRow("Супертрактор и суперкнига работят.")][DataRow("Новият хръмзог супертрактор работи.")]
    [DataRow("Новият, супертрактор работи.")][DataRow("Новият супертрактор, работи.")]
    [DataRow("Новият супертрактор. Работи.")][DataRow("Новият супертрактор")]
    [DataRow("Новият супертрактор работи")][DataRow("Ние млади работим.")]
    public void Graph_nominal_phrase_public_query_abstains_outside_the_closed_single_head_structure(string text)
    {
        TextAnalysis actual = session.AnalyzeText(text); GraphBridgeAssertReferences(actual);
        Assert.IsFalse(actual.Context!.Sentences.SelectMany(s => s.Candidates).Any(c => c.AuthorityLane == "productive_nominal_phrase_graph.v1"));
        Assert.IsFalse(actual.Diagnostics.Any(d => d.Code is "productive_attribute_agreement_mismatch" or "productive_morphology_agreement_mismatch"));
    }

    [TestMethod]
    public void Graph_nominal_phrase_public_long_repeated_chain_preserves_all_attributes_and_cancellation_retry()
    {
        const int count = 65;
        string text = string.Concat(Enumerable.Repeat("Голям ", count)) + "супертрактор работи.";
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.AreEqual(cancel.Token, Assert.ThrowsExactly<OperationCanceledException>(() => session.AnalyzeText(text, cancellationToken: cancel.Token)).CancellationToken);
        TextAnalysis actual = session.AnalyzeText(text); GraphBridgeAssertReferences(actual);
        TextContextSentence sentence = actual.Context!.Sentences.Single();
        Assert.AreEqual(TextContextSelectionStatus.Selected, sentence.Status);
        CollectionAssert.AreEqual(Enumerable.Range(0, count).ToArray(), sentence.SelectedRoles.Where(r => r.Role == TextContextRole.Attribute).Select(r => r.TokenIndex).ToArray());
        Assert.AreEqual(count, actual.Syntax!.Sentences.Single().Candidates.Count(c => c.Role == TextSyntaxRole.Attribute));
        Assert.IsTrue(actual.Tokens.Take(count).All(t => ReferenceEquals(actual.Tokens[0].Analysis, t.Analysis)));
    }


    [TestMethod]
    [DataRow("Суперхубав супертрактор раб", MorphologicalPerson.Third, MorphologicalNumber.Singular, "работи")]
    [DataRow("Ти раб", MorphologicalPerson.Second, MorphologicalNumber.Singular, "работи")]
    [DataRow("Ние чет", MorphologicalPerson.First, MorphologicalNumber.Plural, "четем")]
    [DataRow("Вие чет", MorphologicalPerson.Second, MorphologicalNumber.Plural, "четете")]
    public void Finite_continuation_preserves_original_candidate_homographs_and_matching_indices(
        string text, MorphologicalPerson person, MorphologicalNumber number, string form)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { Continuation = new(text.Length, 256) }, default);
        TextContinuationEvidence result = actual.Continuation!;
        Assert.IsNotNull(result); Assert.IsTrue(result.Available, result.UnavailableReason);
        Assert.AreEqual("finite_continuation.v1", result.Version);
        Assert.AreEqual("exact_package_finite_vocabulary", result.CandidateSource);
        Assert.AreEqual("dictionary_score", result.Ranking);
        Assert.AreEqual(person, result.Expectations.Single().Person); Assert.AreEqual(number, result.Expectations.Single().Number);
        TextContinuationSuggestion candidate = result.Suggestions.Single(row => row.Form == form);
        CollectionAssert.AreEqual(session.Lookup(form, LookupMode.Exact, 256).ToArray(), candidate.Readings.ToArray());
        if (form == "работи")
        {
            CollectionAssert.AreEqual(new[] { "Съществително:41,Женски,мн.ч.", "Глагол:173ti,1л.,Сегашно,Повелително",
                "Глагол:173ti,2л.,Минало_свършено", "Глагол:173ti,3л.,Минало_свършено", "Глагол:173ti,3л.,Сегашно" },
                candidate.Readings.Select(row => row.PartOfSpeech).ToArray());
            CollectionAssert.AreEqual(person == MorphologicalPerson.Third ? new[] { 3, 4 } : new[] { 2 },
                candidate.Matches.Select(match => match.ReadingIndex).ToArray());
        }
        if (text.StartsWith("Ти", StringComparison.Ordinal))
            CollectionAssert.AreEqual(new[] { new TextReadingReference(0, 1) }, result.Expectations.Single().Supports.Single().Readings.ToArray());
        foreach (TextContinuationSuggestion suggestion in result.Suggestions)
            foreach (TextContinuationMatch match in suggestion.Matches)
            {
                MorphologicalFeatures features = suggestion.Readings[match.ReadingIndex].Features;
                Assert.AreEqual(MorphologicalVerbForm.Finite, features.VerbForm);
                foreach (int branch in match.ExpectationIndices)
                { Assert.AreEqual(result.Expectations[branch].Person, features.Person); Assert.AreEqual(result.Expectations[branch].Number, features.Number); }
            }
    }

    [TestMethod]
    [DataRow("Супертрактор ", MorphologicalNumber.Singular)]
    [DataRow("Нови супертрактори ", MorphologicalNumber.Plural)]
    public void Empty_prefix_returns_real_finite_forms_from_graph_subject_context(string text, MorphologicalNumber number)
    {
        TextAnalysis actual = session.AnalyzeText(text, false, new() { Continuation = new(text.Length, 12) }, default);
        TextContinuationEvidence result = actual.Continuation!;
        Assert.IsTrue(result.Available, result.UnavailableReason); Assert.IsNotEmpty(result.Suggestions);
        Assert.AreEqual(text.Length, result.Start); Assert.AreEqual(0, result.Length); Assert.AreEqual("", result.Prefix);
        Assert.IsTrue(result.Expectations.All(e => e.Person == MorphologicalPerson.Third && e.Number == number));
        Assert.IsTrue(result.Expectations.SelectMany(e => e.Supports).Any(s => s.GraphReadings is { Count: > 0 }));
        Assert.IsTrue(actual.Tokens.Any(t => t.Analysis is { IsKnown: false, Readings.Count: 0 }));
        foreach (var candidate in result.Suggestions)
            foreach (var match in candidate.Matches)
            {
                Assert.AreEqual(MorphologicalVerbForm.Finite, candidate.Readings[match.ReadingIndex].Features.VerbForm);
                Assert.AreEqual(number, candidate.Readings[match.ReadingIndex].Features.Number);
                Assert.AreEqual(MorphologicalPerson.Third, candidate.Readings[match.ReadingIndex].Features.Person);
            }
    }

    [TestMethod]
    [DataRow("Супертрактор -раб")]
    [DataRow("Супертрактор раб--сп")]
    [DataRow("Новият ")]
    [DataRow("Суперхубав суперкнига ")]
    [DataRow("мен ")]
    [DataRow("ми ")]
    [DataRow("него ")]
    [DataRow("го ")]
    [DataRow("Ти и ")]
    [DataRow("Супертрактор работи ")]
    public void Unsupported_subject_context_does_not_become_complete_empty_prediction(string text)
    {
        TextContinuationEvidence result = session.AnalyzeText(text, false, new() { Continuation = new(text.Length) }, default).Continuation!;
        Assert.IsFalse(result.Available); Assert.IsFalse(result.SearchComplete); Assert.IsFalse(result.Truncated);
        Assert.IsNotEmpty(result.UnavailableReason!); Assert.IsEmpty(result.Expectations); Assert.IsEmpty(result.Suggestions);
    }

    [TestMethod]
    public void Continuation_is_additive_and_omitted_from_default_text_json()
    {
        const string text = "Ти раб";
        TextAnalysis original = session.AnalyzeText(text);
        TextAnalysis optional = session.AnalyzeText(text, false, new() { Continuation = new(text.Length) }, default);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string baseline = JsonSerializer.Serialize(original, json);
        Assert.IsNull(original.Continuation);
        using JsonDocument document = JsonDocument.Parse(baseline);
        Assert.IsFalse(document.RootElement.TryGetProperty("continuation", out _));
        Assert.AreEqual(baseline, JsonSerializer.Serialize(optional with { Continuation = null }, json));
    }

    [TestMethod]
    [DataRow("Ти раб", -1, 12)]
    [DataRow("Ти раб", 7, 12)]
    [DataRow("Ти раб", 6, 0)]
    [DataRow("Ти раб", 6, 257)]
    [DataRow("😀. Ти раб", 1, 12)]
    [DataRow("Ти ра̀б", 5, 12)]
    public void Continuation_rejects_invalid_bounds_and_grapheme_carets(string text, int caret, int limit)
        => Assert.Throws<ArgumentException>(() => session.AnalyzeText(text, false, new() { Continuation = new(caret, limit) }, default));

    [TestMethod]
    public void Continuation_midword_span_replaces_the_full_original_suffix_and_preserves_right_text()
    {
        const string text = "😀. Супертрактор рабXYZ край.";
        TextAnalysis actual = session.AnalyzeText(text, false, new() { Continuation = new(20, 256) }, default);
        TextContinuationEvidence result = actual.Continuation!;
        Assert.IsTrue(result.Available, result.UnavailableReason);
        Assert.AreEqual(20, result.Caret); Assert.AreEqual(17, result.Start); Assert.AreEqual(6, result.Length); Assert.AreEqual("раб", result.Prefix);
        Assert.IsTrue(result.Suggestions.Any(s => s.Form == "работи"));
        Assert.AreEqual("😀. Супертрактор работи край.", text[..result.Start] + "работи" + text[(result.Start + result.Length)..]);
        Assert.AreEqual(text, actual.Text);
    }

    [TestMethod]
    public void Continuation_cancellation_preserves_token_and_allows_session_reuse()
    {
        const string text = "Супертрактор ";
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.AreEqual(cancel.Token, Assert.Throws<OperationCanceledException>(() => session.AnalyzeText(text, false,
            new() { Continuation = new(text.Length) }, cancel.Token)).CancellationToken);
        Assert.IsTrue(session.AnalyzeText(text, false, new() { Continuation = new(text.Length) }, default).Continuation!.Available);
    }
    // Insert-only additions to PublicPackageTests. No old method/key/assertion changes.
    // Six source-declared leaves; independent of new package-row bindings.
    [TestMethod]
    public void Nominal_target_preserves_existing_constructor_deconstruction_and_default_request_wire()
    {
        var request = new TextContinuationRequest(4);
        (int caret, int limit) = request;
        Assert.AreEqual(4, caret);
        Assert.AreEqual(12, limit);
        Assert.AreEqual(TextContinuationTarget.Finite, request.Target);
        CollectionAssert.AreEqual(new[] { "Finite", "Noun", "Auto" }, Enum.GetNames<TextContinuationTarget>());
        Assert.AreEqual(0, Convert.ToInt32(Enum.Parse<TextContinuationTarget>("Finite")));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        const string oldRequest = "{\"caret\":4,\"limit\":12}";
        Assert.AreEqual(oldRequest, JsonSerializer.Serialize(request, json));
        Assert.AreEqual(oldRequest, JsonSerializer.Serialize(request with { Target = TextContinuationTarget.Finite }, json));
        using JsonDocument noun = JsonDocument.Parse(JsonSerializer.Serialize(request with { Target = TextContinuationTarget.Noun }, json));
        using JsonDocument auto = JsonDocument.Parse(JsonSerializer.Serialize(request with { Target = TextContinuationTarget.Auto }, json));
        Assert.AreEqual("Noun", noun.RootElement.GetProperty("target").GetString());
        Assert.AreEqual("Auto", auto.RootElement.GetProperty("target").GetString());
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(3)]
    [DataRow(int.MaxValue)]
    public void Nominal_target_rejects_undefined_public_enum_values(int undefined)
    {
        Assert.IsFalse(Enum.IsDefined((TextContinuationTarget)undefined));
        Assert.Throws<ArgumentException>(() => session.AnalyzeText("Нова к", false,
            new() { Continuation = new(6, 12) { Target = (TextContinuationTarget)undefined } }, default));
    }

    [TestMethod]
    [DataRow("Ти раб")]
    [DataRow("Супертрактор раб")]
    public void Nominal_target_addition_keeps_default_explicit_finite_and_finite_auto_responses_identical(string text)
    {
        TextAnalysis omitted = session.AnalyzeText(text, false,
            new() { Continuation = new(text.Length, 256) }, default);
        TextAnalysis explicitFinite = session.AnalyzeText(text, false,
            new() { Continuation = new(text.Length, 256) { Target = TextContinuationTarget.Finite } }, default);
        TextAnalysis auto = session.AnalyzeText(text, false,
            new() { Continuation = new(text.Length, 256) { Target = TextContinuationTarget.Auto } }, default);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.AreEqual(JsonSerializer.Serialize(omitted, json), JsonSerializer.Serialize(explicitFinite, json));
        Assert.AreEqual(JsonSerializer.Serialize(omitted, json), JsonSerializer.Serialize(auto, json));
        foreach (TextAnalysis analysis in new[] { omitted, explicitFinite, auto })
        {
            TextContinuationEvidence result = analysis.Continuation!;
            Assert.IsTrue(result.Available, result.UnavailableReason);
            Assert.AreEqual(TextContinuationTarget.Finite, result.Target);
            Assert.AreEqual("finite_continuation.v1", result.Version);
            Assert.AreEqual("exact_package_finite_vocabulary", result.CandidateSource);
            Assert.IsNull(result.NominalExpectations);
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result, json));
            Assert.IsFalse(document.RootElement.TryGetProperty("target", out _));
            Assert.IsFalse(document.RootElement.TryGetProperty("nominalExpectations", out _));
        }
    }

    [TestMethod]
    [DataRow("P1", "Нов ", "град", "книга|училище|градове")]
    [DataRow("P2", "Нова ", "книга", "град|училище|книги")]
    [DataRow("P3", "Ново ", "училище", "град|книга|училища")]
    [DataRow("P4", "Нови ", "градове|книги|училища", "град|книга|училище")]
    [DataRow("D1", "Новият ", "град", "градът|града|книга|градове")]
    [DataRow("D2", "Новия ", "град", "градът|града")]
    [DataRow("D3", "Новата ", "книга", "книгата|книги|град")]
    [DataRow("D4", "Новото ", "училище", "училището|училища|книга")]
    [DataRow("D5", "Новите ", "градове|книги|училища", "градовете|книгите|училищата|книга")]
    [DataRow("M1", "Новият голям ", "град", "градът|книга|градове")]
    [DataRow("M2", "Новата голяма ", "книга", "книгата|град")]
    [DataRow("M3", "Новото голямо ", "училище", "училището|град")]
    [DataRow("M4", "Новите големи ", "книги|градове|училища", "книгите|книга")]
    [DataRow("M5", "Новият голям красив български ", "град", "градът|книга")]
    [DataRow("N1", "Нова голям ", "", "град|книга|училище")]
    [DataRow("N2", "Новият големият ", "", "град")]
    [DataRow("N3", "Нов големият ", "", "град")]
    [DataRow("N4", "Нови голяма ", "", "книги|книга")]
    [DataRow("A1", "Български ", "град|градове", "книга")]
    [DataRow("A2", "Български нови ", "градове", "град")]
    [DataRow("A3", "Нови ", "коли", "")]
    public void Nominal_independent_literal_matrix_preserves_source_cells_and_only_compatible_noun_ordinals(
        string id, string left, string acceptedForms, string excludedForms)
    {
        NominalLiteralBranch[] wanted = NominalLiteralBranches(id);
        string[] accepted = acceptedForms.Split('|', StringSplitOptions.RemoveEmptyEntries);
        string[] excluded = excludedForms.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (string form in accepted.Concat(excluded))
        {
            string text = left + form;
            TextAnalysis analysis = NominalContinue(text);
            TextContinuationEvidence evidence = analysis.Continuation!;
            Assert.AreEqual(TextContinuationTarget.Noun, evidence.Target);
            Assert.AreEqual("nominal_continuation.v1", evidence.Version);
            Assert.AreEqual("exact_package_nominal_vocabulary", evidence.CandidateSource);
            Assert.IsEmpty(evidence.Expectations);
            Assert.AreEqual(left.Length, evidence.Start);
            Assert.AreEqual(form.Length, evidence.Length);
            Assert.AreEqual(form, evidence.Prefix);
            if (wanted.Length == 0)
            {
                Assert.IsFalse(evidence.Available);
                Assert.IsNotEmpty(evidence.UnavailableReason!);
                Assert.IsEmpty(evidence.NominalExpectations ?? []);
                Assert.IsEmpty(evidence.Suggestions);
                Assert.IsFalse(evidence.SearchComplete);
                Assert.IsFalse(evidence.Truncated);
            }
            else
            {
                int[] actualBranches = NominalAssertLiteralBranches(analysis, evidence, left, wanted);
                if (accepted.Contains(form, StringComparer.Ordinal))
                {
                    TextContinuationSuggestion suggestion = evidence.Suggestions.Single(s => s.Form == form);
                    WordReading[] source = NominalPinnedRows(form);
                    CollectionAssert.AreEqual(source, suggestion.Readings.ToArray());
                    CollectionAssert.AreEqual(source, session.Lookup(form, LookupMode.Exact, 256).ToArray());
                    // The premeasurement noun witnesses all use row0. Коли has eight raw
                    // rows: only noun-Plural row0 supports A3; Neuter-Singular row1 and
                    // verb rows2..7 stay present but unmatched.
                    CollectionAssert.AreEqual(new[] { 0 }, suggestion.Matches.Select(m => m.ReadingIndex).ToArray());
                    int expectedBranch = id == "A1" && form == "градове" ? actualBranches[1] : actualBranches[0];
                    CollectionAssert.AreEqual(new[] { expectedBranch }, suggestion.Matches.Single().ExpectationIndices.ToArray());
                }
                else
                {
                    // All excluded spellings in this pinned pool have no compatible
                    // common-noun cell for this case. This is not a general surface ban.
                    Assert.IsFalse(evidence.Suggestions.Any(s => s.Form.Equals(form, StringComparison.OrdinalIgnoreCase)));
                    CollectionAssert.AreEqual(NominalPinnedRows(form), session.Lookup(form, LookupMode.Exact, 256).ToArray());
                }
                NominalAssertAllMatches(evidence);
            }
            NominalOriginalAnalysisUnchanged(analysis);
        }
    }

    private static TextAnalysis NominalContinue(string text, int? caret = null, int limit = 256,
        TextContinuationTarget target = TextContinuationTarget.Noun) => session.AnalyzeText(text, false,
            new() { Continuation = new(caret ?? text.Length, limit) { Target = target } }, default);

    private static int[] NominalAssertLiteralBranches(TextAnalysis analysis, TextContinuationEvidence evidence,
        string left, NominalLiteralBranch[] wanted)
    {
        Assert.IsTrue(evidence.Available, evidence.UnavailableReason);
        Assert.IsNull(evidence.UnavailableReason);
        Assert.IsNotNull(evidence.NominalExpectations);
        Assert.HasCount(wanted.Length, evidence.NominalExpectations!);
        var actual = evidence.NominalExpectations!;
        var result = new int[wanted.Length];
        string[] words = left.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int b = 0; b < wanted.Length; b++)
        {
            NominalLiteralBranch expected = wanted[b];
            int branch = Enumerable.Range(0, actual.Count).Single(i => actual[i].Number == expected.Number &&
                actual[i].Gender == expected.Gender && actual[i].FirstAttributeArticle == expected.FirstArticle);
            result[b] = branch;
            TextNominalContinuationExpectation interpretation = actual[branch];
            Assert.AreEqual(MorphologicalArticle.Indefinite, interpretation.NounArticle);
            Assert.HasCount(words.Length, interpretation.Attributes);
            Assert.HasCount(words.Length, expected.AttributeRows);
            for (int token = 0; token < words.Length; token++)
            {
                TextContextRoleSelection support = interpretation.Attributes[token];
                Assert.AreEqual(TextContextRole.Attribute, support.Role);
                Assert.AreEqual(token, support.TokenIndex);
                Assert.AreEqual(analysis.Tokens[token].Start, support.Start);
                Assert.AreEqual(analysis.Tokens[token].Length, support.Length);
                Assert.IsNull(support.GraphReadings);
                CollectionAssert.AreEqual(expected.AttributeRows[token].Select(i => new TextReadingReference(token, i)).ToArray(),
                    support.Readings.ToArray());
                var occurrence = analysis.ReadingInventory!.Occurrences.Single(o => o.TokenIndex == token);
                CollectionAssert.AreEqual(NominalPinnedRows(words[token]),
                    analysis.ReadingInventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Select(row => row.Raw).ToArray());
                CollectionAssert.AreEqual(NominalPinnedRows(words[token]), analysis.Tokens[token].Analysis!.Readings.ToArray());
            }
        }
        Assert.AreEqual(result.Length, result.Distinct().Count());
        return result;
    }

    private static void NominalAssertAllMatches(TextContinuationEvidence evidence)
    {
        Assert.AreEqual(TextContinuationTarget.Noun, evidence.Target);
        Assert.IsEmpty(evidence.Expectations);
        Assert.IsNotNull(evidence.NominalExpectations);
        foreach (TextContinuationSuggestion suggestion in evidence.Suggestions)
        {
            Assert.IsNotEmpty(suggestion.Readings);
            Assert.IsNotEmpty(suggestion.Matches);
            Assert.AreEqual(suggestion.Matches.Count, suggestion.Matches.Select(m => m.ReadingIndex).Distinct().Count());
            foreach (TextContinuationMatch match in suggestion.Matches)
            {
                Assert.IsTrue((uint)match.ReadingIndex < (uint)suggestion.Readings.Count);
                Assert.IsNotEmpty(match.ExpectationIndices);
                MorphologicalFeatures row = suggestion.Readings[match.ReadingIndex].Features;
                Assert.AreEqual(MorphologicalPartOfSpeech.Noun, row.PartOfSpeech);
                Assert.AreEqual(MorphologicalArticle.Indefinite, row.Article);
                Assert.IsFalse(row.HasUnrecognizedFeatures);
                Assert.IsFalse(row.HasConflictingFeatures);
                Assert.IsNull(row.Case);
                Assert.IsNull(row.IsShortForm);
                foreach (int branch in match.ExpectationIndices)
                {
                    Assert.IsTrue((uint)branch < (uint)evidence.NominalExpectations!.Count);
                    TextNominalContinuationExpectation expected = evidence.NominalExpectations[branch];
                    Assert.AreEqual(expected.Number, row.Number);
                    if (expected.Number == MorphologicalNumber.Singular) Assert.AreEqual(expected.Gender, row.Gender);
                    else { Assert.AreEqual(MorphologicalNumber.Plural, row.Number); Assert.IsNull(expected.Gender); }
                }
            }
            Assert.AreEqual(suggestion.Matches.Max(m => suggestion.Readings[m.ReadingIndex].Score), suggestion.Score);
        }
    }

    private static void NominalOriginalAnalysisUnchanged(TextAnalysis enabled)
    {
        TextAnalysis ordinary = session.AnalyzeText(enabled.Text);
        Assert.IsNull(ordinary.Continuation);
        Assert.AreEqual(JsonSerializer.Serialize(ordinary), JsonSerializer.Serialize(enabled with { Continuation = null }),
            "Future-head Attribute supports do not create accepted Context/Syntax roles, diagnostic authority, tokens or exact readings.");
    }

    private sealed record NominalLiteralBranch(MorphologicalNumber Number, MorphologicalGender? Gender,
        MorphologicalArticle FirstArticle, int[][] AttributeRows);

    private static NominalLiteralBranch[] NominalLiteralBranches(string id) => id switch
    {
        "P1" => [new(MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalArticle.Indefinite, [[0]])],
        "P2" => [new(MorphologicalNumber.Singular, MorphologicalGender.Feminine, MorphologicalArticle.Indefinite, [[0]])],
        "P3" => [new(MorphologicalNumber.Singular, MorphologicalGender.Neuter, MorphologicalArticle.Indefinite, [[0]])],
        "P4" => [new(MorphologicalNumber.Plural, null, MorphologicalArticle.Indefinite, [[0]])],
        "D1" => [new(MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalArticle.Full, [[0]])],
        "D2" => [new(MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalArticle.Definite, [[0]])],
        "D3" => [new(MorphologicalNumber.Singular, MorphologicalGender.Feminine, MorphologicalArticle.Definite, [[0]])],
        "D4" => [new(MorphologicalNumber.Singular, MorphologicalGender.Neuter, MorphologicalArticle.Definite, [[0]])],
        "D5" => [new(MorphologicalNumber.Plural, null, MorphologicalArticle.Definite, [[0]])],
        "M1" => [new(MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalArticle.Full, [[0], [1]])],
        "M2" => [new(MorphologicalNumber.Singular, MorphologicalGender.Feminine, MorphologicalArticle.Definite, [[0], [0]])],
        "M3" => [new(MorphologicalNumber.Singular, MorphologicalGender.Neuter, MorphologicalArticle.Definite, [[0], [0]])],
        "M4" => [new(MorphologicalNumber.Plural, null, MorphologicalArticle.Definite, [[0], [5]])],
        "M5" => [new(MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalArticle.Full, [[0], [1], [0], [1]])],
        "N1" => [],
        "N2" => [],
        "N3" => [],
        "N4" => [],
        "A1" => [new(MorphologicalNumber.Singular, MorphologicalGender.Masculine, MorphologicalArticle.Indefinite, [[1]]), new(MorphologicalNumber.Plural, null, MorphologicalArticle.Indefinite, [[2]])],
        "A2" => [new(MorphologicalNumber.Plural, null, MorphologicalArticle.Indefinite, [[2], [0]])],
        "A3" => [new(MorphologicalNumber.Plural, null, MorphologicalArticle.Indefinite, [[0]])],
        _ => throw new InvalidOperationException("Unknown independent literal case: " + id)
    };

    // Immutable raw cells from premeasurement pool 0df19d9bf39da84a1c829721f23d9d3d09b6191c08854fd23ede5fadf6dcfa3a; no continuation output used.
    private const string NominalPinnedSource = "neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7";

    private static WordReading[] NominalPinnedRows(string word) => word switch
    {
        "нов" =>
        [
            new("нов", "нов", "Прилагателно:76,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        "град" =>
        [
            new("град", "град", "Съществително:1,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        "книга" =>
        [
            new("книга", "книга", "Съществително:41,Женски", 1d, null, null, null, NominalPinnedSource),
        ],
        "училище" =>
        [
            new("училище", "училище", "Съществително:66,Среден", 1d, null, null, null, NominalPinnedSource),
        ],
        "градове" =>
        [
            new("градове", "град", "Съществително:1,Мъжки,мн.ч.", 1d, null, null, null, NominalPinnedSource),
        ],
        "нова" =>
        [
            new("нова", "нов", "Прилагателно:76,Женски", 1d, null, null, null, NominalPinnedSource),
            new("Нова", "Нова", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "книги" =>
        [
            new("книги", "книга", "Съществително:41,Женски,мн.ч.", 1d, null, null, null, NominalPinnedSource),
        ],
        "ново" =>
        [
            new("ново", "нов", "Прилагателно:76,Среден", 1d, null, null, null, NominalPinnedSource),
            new("Ново", "Ново", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "училища" =>
        [
            new("училища", "училище", "Съществително:66,Среден,мн.ч.", 1d, null, null, null, NominalPinnedSource),
        ],
        "нови" =>
        [
            new("нови", "нов", "Прилагателно:76,Мъжки,мн.ч.", 1d, null, null, null, NominalPinnedSource),
            new("Нови", "Нови", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "новият" =>
        [
            new("новият", "нов", "Прилагателно:76,Мъжки,Пълен", 1d, null, null, null, NominalPinnedSource),
        ],
        "градът" =>
        [
            new("градът", "град", "Съществително:1,Мъжки,Пълен", 1d, null, null, null, NominalPinnedSource),
            new("Градът", "Градът", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "града" =>
        [
            new("града", "град", "Съществително:1,Мъжки,Бройна", 1d, null, null, null, NominalPinnedSource),
            new("града", "град", "Съществително:1,Мъжки,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "новия" =>
        [
            new("новия", "нов", "Прилагателно:76,Мъжки,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "новата" =>
        [
            new("новата", "нов", "Прилагателно:76,Женски,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "книгата" =>
        [
            new("книгата", "книга", "Съществително:41,Женски,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "новото" =>
        [
            new("новото", "нов", "Прилагателно:76,Среден,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "училището" =>
        [
            new("училището", "училище", "Съществително:66,Среден,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "новите" =>
        [
            new("новите", "нов", "Прилагателно:76,Мъжки,мн.ч.,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "градовете" =>
        [
            new("градовете", "град", "Съществително:1,Мъжки,мн.ч.,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "книгите" =>
        [
            new("книгите", "книга", "Съществително:41,Женски,мн.ч.,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "училищата" =>
        [
            new("училищата", "училище", "Съществително:66,Среден,мн.ч.,Членуван", 1d, null, null, null, NominalPinnedSource),
        ],
        "голям" =>
        [
            new("Голям", "Голям", "Място:195", 1d, null, null, null, NominalPinnedSource),
            new("голям", "голям", "Прилагателно:81,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        "голяма" =>
        [
            new("голяма", "голям", "Прилагателно:81,Женски", 1d, null, null, null, NominalPinnedSource),
            new("Голяма", "Голяма", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "голямо" =>
        [
            new("голямо", "голям", "Прилагателно:81,Среден", 1d, null, null, null, NominalPinnedSource),
            new("Голямо", "Голямо", "Място:195", 1d, null, null, null, NominalPinnedSource),
        ],
        "големи" =>
        [
            new("Големи", "Големи", "Място:195", 1d, null, null, null, NominalPinnedSource),
            new("големи", "големя", "Глагол:174ii,1л.,Сегашно,Повелително", 1d, null, null, null, NominalPinnedSource),
            new("големи", "големя", "Глагол:174ii,2л.,Минало_свършено", 1d, null, null, null, NominalPinnedSource),
            new("големи", "големя", "Глагол:174ii,3л.,Минало_свършено", 1d, null, null, null, NominalPinnedSource),
            new("големи", "големя", "Глагол:174ii,3л.,Сегашно", 1d, null, null, null, NominalPinnedSource),
            new("големи", "голям", "Прилагателно:81,Мъжки,мн.ч.", 1d, null, null, null, NominalPinnedSource),
        ],
        "красив" =>
        [
            new("красив", "красив", "Прилагателно:76,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        "български" =>
        [
            new("Български", "Български", "Място:195", 1d, null, null, null, NominalPinnedSource),
            new("български", "български", "Прилагателно:78,Мъжки", 1d, null, null, null, NominalPinnedSource),
            new("български", "български", "Прилагателно:78,Мъжки,мн.ч.", 1d, null, null, null, NominalPinnedSource),
        ],
        "големият" =>
        [
            new("големият", "голям", "Прилагателно:81,Мъжки,Пълен", 1d, null, null, null, NominalPinnedSource),
        ],
        "коли" =>
        [
            new("коли", "кола", "Съществително:41,Женски,мн.ч.", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коли", "Съществително:65,Среден", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коля", "Глагол:173ti,1л.,Сегашно,Повелително", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коля", "Глагол:173ti,2л.,Минало_свършено", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коля", "Глагол:173ti,3л.,Минало_свършено", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коля", "Глагол:173ti,3л.,Сегашно", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коля", "Глагол:185ti,1л.,Сегашно,Повелително", 1d, null, null, null, NominalPinnedSource),
            new("коли", "коля", "Глагол:185ti,3л.,Сегашно", 1d, null, null, null, NominalPinnedSource),
        ],
        "учен" =>
        [
            new("учен", "уча", "Глагол:176ti,Минало_страдателно,Мъжки,1л.,Сегашно", 1d, null, null, null, NominalPinnedSource),
            new("учен", "учен", "Прилагателно:76,Мъжки", 1d, null, null, null, NominalPinnedSource),
            new("учен", "учен", "Съществително:5,Мъжки", 1d, null, null, null, NominalPinnedSource),
        ],
        _ => throw new InvalidOperationException("No premeasurement raw fixture: " + word)
    };

    [TestMethod]
    public void Nominal_explicit_noun_reveals_the_adjective_branch_of_real_uchen_while_auto_remains_finite()
    {
        const string text = "Учен гр";
        TextAnalysis auto = NominalContinue(text, target: TextContinuationTarget.Auto);
        TextAnalysis finite = NominalContinue(text, target: TextContinuationTarget.Finite);
        TextAnalysis noun = NominalContinue(text);
        CollectionAssert.AreEqual(NominalPinnedRows("учен"), auto.Tokens[0].Analysis!.Readings.ToArray());
        CollectionAssert.AreEqual(NominalPinnedRows("учен"), noun.Tokens[0].Analysis!.Readings.ToArray());
        Assert.AreEqual(JsonSerializer.Serialize(finite), JsonSerializer.Serialize(auto));
        Assert.IsTrue(auto.Continuation!.Available, auto.Continuation.UnavailableReason);
        Assert.AreEqual(TextContinuationTarget.Finite, auto.Continuation.Target);
        Assert.IsNull(auto.Continuation.NominalExpectations);
        CollectionAssert.AreEqual(new[] { new TextReadingReference(0, 2) },
            auto.Continuation.Expectations.Single().Supports.Single().Readings.ToArray());
        Assert.IsTrue(noun.Continuation!.Available, noun.Continuation.UnavailableReason);
        Assert.AreEqual(TextContinuationTarget.Noun, noun.Continuation.Target);
        Assert.IsEmpty(noun.Continuation.Expectations);
        CollectionAssert.AreEqual(new[] { new TextReadingReference(0, 1) },
            noun.Continuation.NominalExpectations!.Single().Attributes.Single().Readings.ToArray());
        TextContinuationSuggestion city = noun.Continuation.Suggestions.Single(s => s.Form == "град");
        CollectionAssert.AreEqual(NominalPinnedRows("град"), city.Readings.ToArray());
        NominalAssertAllMatches(noun.Continuation);
        NominalOriginalAnalysisUnchanged(noun);
    }

    [TestMethod]
    [DataRow("Нова книга", 6, 5, 5, "Нова книга")]
    [DataRow("Нова книга лежат.", 6, 5, 5, "Нова книга лежат.")]
    [DataRow("😀. Нова кXYZ лежат.", 10, 9, 4, "😀. Нова книга лежат.")]
    public void Nominal_midword_completion_replaces_the_entire_original_word_and_preserves_unassessed_right_text(
        string text, int caret, int start, int length, string replaced)
    {
        TextAnalysis analysis = NominalContinue(text, caret);
        TextContinuationEvidence evidence = analysis.Continuation!;
        Assert.IsTrue(evidence.Available, evidence.UnavailableReason);
        Assert.AreEqual(TextContinuationTarget.Noun, evidence.Target);
        Assert.AreEqual(caret, evidence.Caret); Assert.AreEqual(start, evidence.Start);
        Assert.AreEqual(length, evidence.Length); Assert.AreEqual("к", evidence.Prefix);
        Assert.IsNotEmpty(evidence.Suggestions);
        // Keep the original one-letter edit case; a separate narrow prefix locates the fixed
        // книга witness without assuming that it ranks within the API's 256-form bound for к.
        string narrowedText = text[..caret] + "ни" + text[caret..];
        TextAnalysis narrowed = NominalContinue(narrowedText, caret + 2);
        TextContinuationEvidence witness = narrowed.Continuation!;
        Assert.IsTrue(witness.Available, witness.UnavailableReason);
        Assert.AreEqual(start, witness.Start); Assert.AreEqual(length + 2, witness.Length);
        Assert.AreEqual("кни", witness.Prefix);
        TextContinuationSuggestion book = witness.Suggestions.Single(s => s.Form == "книга");
        CollectionAssert.AreEqual(NominalPinnedRows("книга"), book.Readings.ToArray());
        CollectionAssert.AreEqual(new[] { 0 }, book.Matches.Select(m => m.ReadingIndex).ToArray());
        Assert.AreEqual(replaced, text[..start] + book.Form + text[(start + length)..]);
        Assert.AreEqual(replaced, narrowedText[..witness.Start] + book.Form + narrowedText[(witness.Start + witness.Length)..]);
        Assert.AreEqual(text, analysis.Text);
        NominalAssertAllMatches(evidence);
        NominalOriginalAnalysisUnchanged(analysis);
        NominalAssertAllMatches(witness);
        NominalOriginalAnalysisUnchanged(narrowed);
    }

    [TestMethod]
    public void Nominal_valid_combining_grapheme_caret_keeps_original_prefix_and_whole_word_span()
    {
        const string text = "Нова кнѝга";
        TextAnalysis analysis = NominalContinue(text, 9);
        TextContinuationEvidence evidence = analysis.Continuation!;
        Assert.IsTrue(evidence.Available, evidence.UnavailableReason);
        Assert.AreEqual(TextContinuationTarget.Noun, evidence.Target);
        Assert.AreEqual(9, evidence.Caret); Assert.AreEqual(5, evidence.Start);
        Assert.AreEqual(6, evidence.Length); Assert.AreEqual("кнѝ", evidence.Prefix);
        Assert.AreEqual(text, analysis.Text);
        NominalOriginalAnalysisUnchanged(analysis);
    }

    [TestMethod]
    [DataRow("😀. Нова к", 1)]
    [DataRow("Нова кнѝга", 8)]
    [DataRow("Нова к", 7)]
    public void Nominal_target_reuses_original_grapheme_and_range_rejection(string text, int caret)
        => Assert.Throws<ArgumentException>(() => NominalContinue(text, caret));

    [TestMethod]
    public void Nominal_cancellation_keeps_the_original_token_and_allows_session_reuse()
    {
        const string text = "Нова к";
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.AreEqual(canceled.Token, Assert.Throws<OperationCanceledException>(() => session.AnalyzeText(text, false,
            new() { Continuation = new(text.Length, 256) { Target = TextContinuationTarget.Noun } }, canceled.Token)).CancellationToken);
        TextAnalysis reused = NominalContinue(text);
        Assert.IsTrue(reused.Continuation!.Available, reused.Continuation.UnavailableReason);
        Assert.IsNotEmpty(reused.Continuation.Suggestions); NominalAssertAllMatches(reused.Continuation);
        TextAnalysis witness = NominalContinue(text + "ни");
        Assert.IsTrue(witness.Continuation!.Available, witness.Continuation.UnavailableReason);
        Assert.IsTrue(witness.Continuation.Suggestions.Any(s => s.Form == "книга"));
    }


}
