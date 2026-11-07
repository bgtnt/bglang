// Independently checks the local PREP+NP proof against literal cases and original complete readings.
// The measured article route opens explicit Current locally; shared Baseline initialization is preserved.
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

public sealed partial class PublicPackageTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly TextContextOptions ArticleOptions = new();
    private sealed record ArticleSignal(string Code, int Start, int Length, TextDiagnostic[] Root, TextValidationDiagnostic[] Validation);
    private sealed record ArticleRunRow(string Id, bool Required, string Expected, bool Measured, bool LexicallyApplicable,
        string? LexicalIssue, bool ExactWarning, int WrongLocalization, int ProposalCount, int CorrectProposals,
        int FalseAccusations, bool AssessedValid, bool EvaluationUnavailable, bool NotReady, bool ProofValid,
        string? Relation, bool RequiredPass, string[] UnknownCodes, ArticleSignal[] Signals, string? Error)
    { public int WrongProposals { get; init; } public int UnverifiedProposals { get; init; } public string? RelationAuthority { get; init; } }
    private sealed record ArticleFormEvidence(string Surface, TextReadingSnapshot[] Readings);

    [TestMethod]
    [DoNotParallelize]
    public void Generated_article_cases_use_one_complete_package_and_keep_uncovered_required_cells_visible()
    {
        string? selectedManifest = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_CURRENT_MANIFEST");
        Assert.IsFalse(string.IsNullOrWhiteSpace(selectedManifest), "Article method acceptance requires the explicit Current complete package.");
        string manifest = selectedManifest!;
        Assert.IsTrue(Path.IsPathFullyQualified(manifest), "Current manifest must be an explicit absolute path.");
        using BulgarianLanguageSession session = BulgarianLanguageSession.Open(manifest);
        Assert.AreEqual("bglang-neutral:90bea7c5c799f04d", session.Provenance.PackageId);
        Assert.AreEqual("c718c78baaf244b0dfde39f9fe0e9295abb974cc254a94903e70a3014625bbc7",
            session.Provenance.ManifestSha256.ToLowerInvariant());
        using JsonDocument projectionDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data/article-projection.v1.json")));
        JsonElement projection = projectionDoc.RootElement;
        Assert.AreEqual("bglang.article-acceptance-projection.v1", projection.GetProperty("schema").GetString());
        // This label identifies the unchanged historical fixture; the current proof uses only original local readings.
        Assert.AreEqual("candidate_pp_or_exact_local_adposition_with_finite_hypothesis.v2", projection.GetProperty("independentPrepositionProofPolicy").GetString());
        Assert.AreEqual(projection.GetProperty("manifestSha256").GetString(), ArticleHash(File.ReadAllBytes(manifest)));
        string optionsJson = JsonSerializer.Serialize(new { includeUnknownContextProperNames = false,
            includeAdditionalRoleHeuristics = ArticleOptions.IncludeAdditionalRoleHeuristics,
            includeSubordinateCommaDiagnostics = ArticleOptions.IncludeSubordinateCommaDiagnostics,
            includeFragmentDiagnostics = ArticleOptions.IncludeFragmentDiagnostics,
            includeSpacingDiagnostics = ArticleOptions.IncludeSpacingDiagnostics,
            includeWordConstructions = ArticleOptions.IncludeWordConstructions,
            includeNominalConstructions = ArticleOptions.IncludeNominalConstructions, continuation = ArticleOptions.Continuation });
        using JsonDocument actualOptions = JsonDocument.Parse(optionsJson);
        Assert.IsTrue(JsonElement.DeepEquals(projection.GetProperty("options"), actualOptions.RootElement));
        string projectionHash = ArticleHash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data/article-projection.v1.json")));
        string optionsHash = ArticleHash(System.Text.Encoding.UTF8.GetBytes(optionsJson));
        string partition = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_PARTITION") ?? "development";
        IReadOnlyList<ArticleGeneratedCase> generated = LoadArticleCases(partition, projectionHash, optionsHash, manifest);
        Assert.IsTrue(partition is "development" or "withheld" or "combined_frozen");
        foreach(var group in generated.GroupBy(c=>c.Partition)) ArticleCaseFactory.Validate(group.ToArray(),group.Key);
        Assert.AreEqual(partition=="combined_frozen" ? 5760 : 2880,generated.Count);
        bool referenceCompared = false;
        if (partition is "development" or "combined_frozen")
        {
            string? reference = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_DEVELOPMENT_REFERENCE");
            if (Environment.GetEnvironmentVariable("BGLANG_ARTICLE_REQUIRE_DEVELOPMENT_REFERENCE") == "1")
                Assert.IsNotNull(reference, "Required reference parity is not a skip.");
            if (reference is not null)
            {
                byte[] bytes = File.ReadAllBytes(reference);
                Assert.AreEqual("d39b7ed5642d301564d8328617b43d153e750b492884f13b3fba2909a624f4bd", ArticleHash(bytes));
                string[] expected = System.Text.Encoding.UTF8.GetString(bytes).Split('\n',StringSplitOptions.RemoveEmptyEntries);
                ArticleGeneratedCase[] development=generated.Where(c=>c.Partition=="development").ToArray();
                Assert.AreEqual(development.Length,expected.Length);
                for (int i=0;i<expected.Length;i++)
                { using JsonDocument row=JsonDocument.Parse(expected[i]);
                  Assert.IsTrue(JsonElement.DeepEquals(row.RootElement,JsonSerializer.SerializeToElement(development[i],ArticleCaseFactory.Json)),development[i].Id); }
                referenceCompared = true;
            }
        }
        Assert.AreEqual(partition=="combined_frozen" ? 768 : 384, generated.Count(c => c.InitialDeliveryScope == "REQUIRED_BASIC"));
        var declared = projection.GetProperty("declaredExtensionIds").EnumerateArray().Select(x=>x.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(declared.All(id=>generated.Any(c=>c.Id==id && c.Construction=="prepositional_np")), "Full-role positive scope has no independent mapped evaluator in this first PP batch.");
        Assert.IsTrue(generated.GroupBy(c=>c.PairId).All(g=>!g.Any(c=>declared.Contains(c.Id)) || g.All(c=>declared.Contains(c.Id))), "Declare whole positive/inverse pairs before measurement.");
        Dictionary<string, ArticleFormEvidence> forms = new(StringComparer.Ordinal);
        ArticleFormEvidence Form(string surface)
        {
            if (forms.TryGetValue(surface, out var saved)) return saved;
            TextAnalysis acquired = session.AnalyzeText(surface, false, ArticleOptions, default);
            TextReadingInventory? inventory = acquired.ReadingInventory;
            TextReadingSnapshot[] rows = inventory is { Occurrences.Count: 1 }
                ? inventory.Sets[inventory.Occurrences[0].ReadingSetIndex].ResolvedReadings.ToArray() : [];
            return forms[surface] = new(surface, rows);
        }
        Dictionary<string, ArticleGeneratedCase[]> pairs = generated.GroupBy(c => c.PairId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        List<ArticleRunRow> measured = [];
        foreach (ArticleGeneratedCase c in generated)
        {
            bool required = ArticleRequired(c,projection);
            try
            {
                ArticleGeneratedCase mate = pairs[c.PairId].Single(x => x.Id != c.Id);
                string full = c.ExpectedArticle == "Full" ? (c.ExpectedReplacement ?? c.Target.Surface) : (mate.ExpectedReplacement is null ? c.Target.Surface : mate.Target.Surface);
                if (c.ExpectedArticle == "Short") full = pairs[c.PairId].Single(x => x.NormativeExpectation == "MUST_WARN").Target.Surface;
                string shortForm = c.ExpectedArticle == "Short" ? pairs[c.PairId].Single(x => x.NormativeExpectation == "MUST_ACCEPT_LOCALLY").Target.Surface
                    : pairs[c.PairId].Single(x => x.NormativeExpectation == "MUST_WARN").Target.Surface;
                measured.Add(ObserveArticle(c,required,full,shortForm,projection,Form,t=>session.AnalyzeText(t,false,ArticleOptions,default)));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                measured.Add(new(c.Id, required, c.NormativeExpectation, false, false, "OPERATIONAL_NONMEASUREMENT", false, 0, 0, 0, 0,
                    false, true, false, false, null, false, [], [], error.ToString()));
            }
        }
        var relationTransitions = pairs.Select(pair => {
            var a = measured.Single(x => x.Id == pair.Value[0].Id); var b = measured.Single(x => x.Id == pair.Value[1].Id);
            return new { pairId=pair.Key, required=pair.Value.Any(c => ArticleRequired(c,projection)),
                preserved=a.Relation is not null && a.Relation == b.Relation, first=a.Relation, second=b.Relation };
        }).ToArray();
        using JsonDocument controls = JsonDocument.Parse(ArticlePinnedData("article-controls.v1.json","a5d64ad8ece32f23d8844c260a0e0c0479a3f9e33dce955aa8b399ba256ddf7c"));
        var protectedResults = controls.RootElement.GetProperty("surface").EnumerateArray().Select(c => CheckArticleSurface(c, projection, Form, t => session.AnalyzeText(t, false, ArticleOptions, default))).ToArray();
        using var process = Process.GetCurrentProcess();
        string output = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_EVIDENCE_DIRECTORY") ?? Path.Combine(TestContext.TestResultsDirectory!, "article-generated");
        Directory.CreateDirectory(output);
        var summary = new { partition, referenceCompared, cases=measured.Count, required=measured.Count(r => r.Required),
            requiredWrong=measured.Count(r => r.Required && r.Expected == "MUST_WARN"), requiredCorrect=measured.Count(r => r.Required && r.Expected == "MUST_ACCEPT_LOCALLY"),
            requiredMisses=measured.Count(r => r.Required && r.Expected == "MUST_WARN" && !r.ExactWarning),
            requiredUnassessed=measured.Count(r => r.Required && r.Expected == "MUST_ACCEPT_LOCALLY" && !r.AssessedValid),
            missingLexical=measured.Count(r => !r.LexicallyApplicable), wrongLocalization=measured.Sum(r => r.WrongLocalization),
            proposals=measured.Sum(r => r.ProposalCount), correctProposals=measured.Sum(r => r.CorrectProposals),
            wrongProposals=measured.Sum(r=>r.WrongProposals), unverifiedProposals=measured.Sum(r=>r.UnverifiedProposals),
            falseAccusations=measured.Sum(r => r.FalseAccusations), operationalFailures=measured.Count(r => !r.Measured),
            typedControls="NOT_EXECUTED_BY_THIS_SURFACE_BATCH; require separately mapped Core fixtures",
            pid=process.Id, birthUtc=process.StartTime.ToUniversalTime(), actualTemp=Path.GetTempPath(), TEMP=Environment.GetEnvironmentVariable("TEMP"), TMP=Environment.GetEnvironmentVariable("TMP"),
            modulePins=ArticleModulePins(), manifestSha256=ArticleHash(File.ReadAllBytes(manifest)), projectionHash, optionsHash,
            qualification="No overall accuracy; warning-free is not assessed-valid; challenge rows are not delivered coverage. Single existing package session; in-process only." };
        using (var file = new FileStream(Path.Combine(output, "article-observations-" + partition + ".json"), FileMode.CreateNew))
            JsonSerializer.Serialize(file, new { summary, measured, relationTransitions, protectedResults, namedForms=forms.Values }, ArticleCaseFactory.Json);
        Assert.IsTrue(measured.All(r => !r.Required || r.RequiredPass), "Required cells incomplete; see preserved observations.");
        Assert.IsTrue(measured.All(r=>r.Measured), "Unexpected operational failure on a legal generated input; never a green challenge skip.");
        Assert.AreEqual(0, measured.Sum(r => r.FalseAccusations));
        Assert.AreEqual(0,measured.Sum(r=>r.WrongLocalization),"Wrong-span article warning is not accepted as challenge coverage.");
        Assert.AreEqual(0,measured.Sum(r=>r.WrongProposals));
        Assert.AreEqual(0,measured.Sum(r=>r.UnverifiedProposals),"Unverified correction is not green.");
        Assert.IsTrue(measured.All(r => r.UnknownCodes.Length == 0), "Unmapped article code is not silently ignored.");
        Assert.IsTrue(relationTransitions.All(r => !r.required || r.preserved), "Full/Short swap lost independently supplied PP relation.");
        Assert.IsTrue(protectedResults.All(r => r.Passed), "Protected surface-control accusation/edit or missing required evidence.");
    }

    [TestMethod]
    public void Article_factory_keeps_literal_ranges_pairs_and_rejects_corruptions()
    {
        var seeds=JsonSerializer.Deserialize<ArticleSeeds>(ArticlePinnedData("article-seeds.development.v1.json","1895282616bcacfe17147e472705238abec925d67e25280e087a17ab7b140ed6"),ArticleCaseFactory.Json)!;
        var rows=ArticleCaseFactory.Generate(seeds); ArticleCaseFactory.Validate(rows,"development");
        var anchors=new (string Id,string Text,int Start,int Length,string? Repair)[]
        {
            ("development/stazhant/noun/govorya_za/postposed/plain/correct","Говоря за стажанта.",10,8,null),
            ("development/stazhant/noun/govorya_za/postposed/uppercase/error","ГОВОРЯ ЗА СТАЖАНТЪТ.",10,9,"СТАЖАНТА"),
            ("development/stazhant/noun/govorya_za/postposed/emoji/error","🙂 Говоря за стажантът.",13,9,"стажанта"),
            ("development/stazhant/noun/govorya_za/postposed/repeated/error","Говоря за стажанта. Говоря за стажантът.",30,9,"стажанта"),
            ("development/stazhant/adj_nov/govorya_za/postposed/plain/error","Говоря за новият стажант.",10,6,"новия"),
            ("development/lekar/noun/govorya_za/postposed/plain/error","Говоря за лекарят.",10,7,"лекаря"),
            ("development/хотел/добър/single_attribute/copular_complement/plain/correct","Това е добрият хотел.",7,7,null)
        };
        foreach(var anchor in anchors)
        { var c=rows.Single(r=>r.Id==anchor.Id); Assert.AreEqual(anchor.Text,c.Text); Assert.AreEqual(anchor.Start,c.Target.Start);
          Assert.AreEqual(anchor.Length,c.Target.Length); string? expectedReplacement=anchor.Repair; string? actualReplacement=c.ExpectedReplacement;
          Assert.AreEqual(expectedReplacement,actualReplacement); }
        var duplicate=rows.ToArray(); duplicate[1]=duplicate[1] with {Id=duplicate[0].Id};
        Assert.ThrowsExactly<ArgumentException>(()=>ArticleCaseFactory.Validate(duplicate,"development"));
        var wrongSpan=rows.ToArray(); int emoji=Array.FindIndex(wrongSpan,r=>r.Presentation=="emoji");
        wrongSpan[emoji]=wrongSpan[emoji] with {Target=wrongSpan[emoji].Target with {Start=wrongSpan[emoji].Target.Start+1}};
        Assert.ThrowsExactly<ArgumentException>(()=>ArticleCaseFactory.Validate(wrongSpan,"development"));
        var wrongRepair=rows.ToArray(); wrongRepair[0]=wrongRepair[0] with {ExpectedTextAfterRepair=wrongRepair[0].Text+"x"};
        Assert.ThrowsExactly<ArgumentException>(()=>ArticleCaseFactory.Validate(wrongRepair,"development"));
        var orphan=rows.ToArray(); orphan[0]=orphan[0] with {PairId="orphan"};
        Assert.ThrowsExactly<ArgumentException>(()=>ArticleCaseFactory.Validate(orphan,"development"));
        ArticleLocalProofControls();
    }

    [TestMethod]
    [DataRow("s07","accusation","FORBIDDEN_ACCUSATION:",false)]
    [DataRow("s12","accusation","FORBIDDEN_ACCUSATION:",false)]
    [DataRow("s18","first_occurrence","FORBIDDEN_REPAIR:",false)]
    [DataRow("s02","silent","EVALUATION_UNAVAILABLE",false)]
    [DataRow("s01","foreign_counterpart","COUNTERPART_EVIDENCE_UNAVAILABLE:",false)]
    [DataRow("s07","empty_deletion","FORBIDDEN_REPAIR:",false)]
    [DataRow("s12","notice","",true)]
    public void Article_oracle_rejects_false_strategies_without_requiring_text_edits(string id,string mutation,string violation,bool passes)
    {
        using JsonDocument controls=JsonDocument.Parse(ArticlePinnedData("article-controls.v1.json","a5d64ad8ece32f23d8844c260a0e0c0479a3f9e33dce955aa8b399ba256ddf7c"));
        using JsonDocument profile=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"data/article-projection.v1.json")));
        JsonElement c=controls.RootElement.GetProperty("surface").EnumerateArray().Single(c=>c.GetProperty("id").GetString()==id);
        ArticleFormEvidence Form(string form)
        {
            // Explicit synthetic oracle witnesses, not claims about package rows or normative labels.
            WordReading r=form switch
            {
                "учителят"=>new(form,"учител","Съществително:7,Мъжки,Пълен",1,null,null,null,"oracle_fixture"),
                "учителя"=>new(form,mutation=="foreign_counterpart" ? "друг" : "учител","Съществително:7,Мъжки,Членуван",1,null,null,null,"oracle_fixture"),
                _=>throw new InvalidOperationException("Unexpected oracle form: "+form)
            };
            return new(form,[new(r,r.Features)]);
        }
        TextAnalysis Observe(string text)
        {
            if(mutation=="silent") return new(text,[]);
            JsonElement span=mutation=="first_occurrence" ? c.GetProperty("protectedRanges")[0]
                : c.TryGetProperty("utf16",out var focus) ? focus : c.GetProperty("protectedRanges")[0];
            string code=mutation=="notice" ? "full_short_article_check_not_ready" : mutation is "foreign_counterpart" or "first_occurrence"
                ? "short_article_expected_after_preposition_candidate" : "full_article_expected_for_subject_candidate";
            string? replacement=mutation is "foreign_counterpart" or "first_occurrence" ? "учителя" : mutation=="empty_deletion" ? string.Empty : null;
            return new(text,[]) {Diagnostics=[new(code,span.GetProperty("start").GetInt32(),span.GetProperty("length").GetInt32(),
                code=="short_article_expected_after_preposition_candidate" ? "grammar.short_article.exact_prepositional_group.v1" : "oracle_fixture") {ProjectedText=replacement}]};
        }
        var result=CheckArticleSurface(c,profile.RootElement,Form,Observe);
        Assert.AreEqual(passes,result.Passed);
        if(!passes) Assert.IsTrue(result.Violations.Any(v=>v.StartsWith(violation,StringComparison.Ordinal)),string.Join(";",result.Violations));
    }

    private static ArticleRunRow ObserveArticle(ArticleGeneratedCase c, bool required, string full, string shortForm,
        JsonElement p, Func<string,ArticleFormEvidence> form, Func<string,TextAnalysis> analyze)
    {
        try
        {
                ArticleFormEvidence fullEvidence = form(full), shortEvidence = form(shortForm);
                string? lexicalIssue = ArticlePairIssue(fullEvidence, shortEvidence, c.CarrierLemma);
                TextAnalysis actual = analyze(c.Text);
                ArticleSignal[] signals = ArticleSignals(actual);
                string[] unknown = UnknownArticleCodes(signals, p);
                ArticleSignal[] accusations = signals.Where(s => ArticleCategorical(s.Code, p)).ToArray();
                string expectedCode = p.GetProperty(c.ExpectedArticle == "Short" ? "expectedShortWarningCode" : "expectedFullWarningCode").GetString()!;
                ArticleSignal[] exact = accusations.Where(s => s.Code == expectedCode && s.Start == c.Target.Start && s.Length == c.Target.Length).ToArray();
                var proposals = signals.SelectMany(s => s.Root.Where(d => d.ProjectedText is not null &&
                    (s.Start<0 || s.Length<0 || s.Start>c.Text.Length-s.Length || d.ProjectedText!=c.Text.Substring(s.Start,s.Length))).Select(d => (Signal:s, Diagnostic:d)))
                    .DistinctBy(p => (p.Signal.Code, p.Signal.Start, p.Signal.Length, p.Diagnostic.ProjectedText)).ToArray();
                bool localPp = c.Construction == "prepositional_np";
                ArticleWitness? witness = ArticleIndependentWitness(actual, c);
                if (witness is not null && lexicalIssue is null && !ArticleSameOccurrenceReadings(witness,
                    c.NormativeExpectation == "MUST_WARN" ? fullEvidence : shortEvidence))
                    lexicalIssue = "FOREIGN_OCCURRENCE_READING_IDENTITY";
                int matchingProposals = proposals.Count(p => c.ExpectedReplacement is not null && p.Signal.Start == c.Target.Start && p.Signal.Length == c.Target.Length &&
                    p.Diagnostic.ProjectedText == c.ExpectedReplacement && ArticleCaseFactory.Repair(c, p.Diagnostic.ProjectedText!) == c.ExpectedTextAfterRepair);
                int correctProposals = lexicalIssue is null ? matchingProposals : 0;
                string? relation = witness?.Key;
                bool proof = exact.Length > 0 && exact.All(s => s.Root.Length == 1 && s.Validation.Length == 1 && s.Root.All(d =>
                    ArticleProof(actual, c.Target, d.Readings, localPp, witness) &&
                    (c.ExpectedArticle != "Short" || d.RuleId == p.GetProperty("expectedShortRule").GetString()) &&
                    s.Validation.All(v => d.Readings is not null && v.Supports.SequenceEqual(d.Readings) &&
                        v.Basis.StartsWith("existing_diagnostic=" + d.RuleId + ";bounded_article_advice", StringComparison.Ordinal))));
                bool notReady = signals.Any(s => ArticleAvailability(s.Code, p) && s.Start == c.Target.Start && s.Length == c.Target.Length);
                bool assessed = localPp && ArticleAssessed(actual, c.Target, p, witness) && !notReady;
                bool correct = c.NormativeExpectation == "MUST_ACCEPT_LOCALLY";
                int wrongLocalization = accusations.Count(s => s.Start != c.Target.Start || s.Length != c.Target.Length);
                bool pass = lexicalIssue is null && unknown.Length == 0 && (correct
                    ? accusations.Length == 0 && proposals.Length == 0 && assessed
                    : exact.Length > 0 && wrongLocalization == 0 && correctProposals > 0 && correctProposals == proposals.Length && proof && !notReady);
                return new(c.Id, required, c.NormativeExpectation, true, lexicalIssue is null, lexicalIssue, exact.Length > 0,
                    wrongLocalization, proposals.Length, correctProposals, correct ? accusations.Length : 0,
                    assessed, correct && !assessed, notReady, proof, relation, pass, unknown, signals, null) { WrongProposals=proposals.Length-matchingProposals, UnverifiedProposals=matchingProposals-correctProposals, RelationAuthority=witness is null ? null : "literal_prep_np_original_inventory" };
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return new(c.Id,required,c.NormativeExpectation,false,false,"OPERATIONAL_NONMEASUREMENT",false,0,0,0,0,false,true,false,false,null,false,[],[],error.ToString()); }
    }

    private static IReadOnlyList<ArticleGeneratedCase> LoadArticleCases(string partition, string projectionHash, string optionsHash, string manifest)
    {
        if (partition == "combined_frozen")
            return LoadArticleCases("development",projectionHash,optionsHash,manifest).Concat(LoadArticleCases("withheld",projectionHash,optionsHash,manifest)).ToArray();
        if (partition == "development")
        {
            byte[] bytes = ArticlePinnedData("article-seeds.development.v1.json","1895282616bcacfe17147e472705238abec925d67e25280e087a17ab7b140ed6");
            Assert.AreEqual("1895282616bcacfe17147e472705238abec925d67e25280e087a17ab7b140ed6", ArticleHash(bytes));
            return ArticleCaseFactory.Generate(JsonSerializer.Deserialize<ArticleSeeds>(bytes, ArticleCaseFactory.Json)!);
        }
        Assert.AreEqual("withheld", partition);
        using JsonDocument profileState = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"data/article-projection.v1.json")));
        Assert.AreEqual("FROZEN_FOR_ARTICLE_VALIDATION",profileState.RootElement.GetProperty("state").GetString());
        Assert.HasCount(4, ArticleModulePins());
        string freezePath = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_FROZEN_RECEIPT") ?? throw new InvalidOperationException("Withheld requires a source/rules/projection/options freeze before any content read.");
        using JsonDocument freeze = JsonDocument.Parse(File.ReadAllBytes(freezePath));
        JsonElement f = freeze.RootElement;
        Assert.AreEqual("FROZEN_BEFORE_WITHHELD", f.GetProperty("status").GetString());
        Assert.AreEqual(projectionHash, f.GetProperty("projectionSha256").GetString());
        Assert.AreEqual(optionsHash, f.GetProperty("optionsSha256").GetString());
        Assert.AreEqual(ArticleHash(File.ReadAllBytes(manifest)), f.GetProperty("manifestSha256").GetString());
        foreach (var actual in ArticleModulePins())
        {
            JsonElement pin = f.GetProperty("modulePins").EnumerateArray().Single(p => p.GetProperty("name").GetString() == actual.Name);
            Assert.AreEqual(actual.Bytes, pin.GetProperty("bytes").GetInt64()); Assert.AreEqual(actual.Sha256, pin.GetProperty("sha256").GetString());
        }
        // Content is first opened only after exact loaded source/test/profile/options/package binding.
        string input = Environment.GetEnvironmentVariable("BGLANG_ARTICLE_WITHHELD_CASES") ?? throw new InvalidOperationException("Withheld fixture path missing.");
        byte[] withheld = File.ReadAllBytes(input);
        Assert.AreEqual("203744f63c8eb06445773a38b86278db2e66e7193fa719f4a7356a59b9cd0ef0", ArticleHash(withheld));
        return System.Text.Encoding.UTF8.GetString(withheld).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<ArticleGeneratedCase>(line, ArticleCaseFactory.Json)!).ToArray();
    }

    private static ArticleWitness? ArticleIndependentWitness(TextAnalysis actual, ArticleGeneratedCase c)
    {
        if (c.Construction != "prepositional_np" || c.Layout is not ("noun" or "single_attribute" or "control"))
            return null;
        if (actual.Text != c.Text)
            throw new InvalidDataException("Article case lost its literal source.");
        (string Verb, string Preposition) frame = c.Frame switch
        {
            "govorya_za" => ("Говоря", "за"), "mislya_za" => ("Мисля", "за"),
            "pisha_na" => ("Пиша", "на"), "tragvam_bez" => ("Тръгвам", "без"),
            "control" when c.Id is "s01" or "s02" or "s03" or "s04" or "s17" or "s18" => ("Говоря", "с"),
            "control" when c.Id is "s05" or "s06" => ("Помагам", "на"),
            _ => throw new InvalidDataException("Article case lacks an independent literal frame.")
        };
        int prepStart = c.Target.Start - frame.Preposition.Length - 1;
        if (c.Position == "postposed")
        {
            string prefix = frame.Verb + " " + frame.Preposition + " ";
            int start = c.Target.Start - prefix.Length;
            if (start < 0 || !c.Text.AsSpan(start, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Article case lost its literal frame.");
        }
        else if (c.Position != "fronted" || c.InitialDeliveryScope == "REQUIRED_BASIC")
            throw new InvalidDataException("Unexpected position in the independent article fixture.");

        if (prepStart < 0 || !c.Text.AsSpan(prepStart, frame.Preposition.Length).Equals(frame.Preposition, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Article preposition lost its original range.");
        List<ArticleTarget> phrase = [new(prepStart, frame.Preposition.Length, c.Text.Substring(prepStart, frame.Preposition.Length)), c.Target];
        if (c.Layout == "single_attribute" || c.Frame == "control" && c.Id is "s03" or "s04")
        {
            string head = c.Frame == "control" ? "учител" : c.NounLemma;
            int start = c.Target.Start + c.Target.Length + 1;
            if (start > c.Text.Length - head.Length || !c.Text.AsSpan(start, head.Length).Equals(head, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Article noun head lost its independent literal range.");
            phrase.Add(new(start, head.Length, c.Text.Substring(start, head.Length)));
        }
        return ArticlePpWitness(actual, phrase);
    }

    private static bool ArticleSameSupport(IReadOnlyList<TextReadingReference>? actual, IReadOnlyList<TextReadingReference> expected) =>
        actual is not null && actual.OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex)
            .SequenceEqual(expected.OrderBy(r => r.TokenIndex).ThenBy(r => r.CompleteReadingIndex));

    private static ArticleSignal[] ArticleSignals(TextAnalysis analysis)
    {
        TextValidationDiagnostic[] validation = analysis.Validation?.Diagnostics.ToArray() ?? [];
        return analysis.Diagnostics.Select(d => (d.Code, d.Start, d.Length)).Concat(validation.Select(d => (d.Code,d.Start,d.Length))).Distinct()
            .Where(k => k.Code.Contains("article", StringComparison.OrdinalIgnoreCase)).Select(k => new ArticleSignal(k.Code,k.Start,k.Length,
                analysis.Diagnostics.Where(d => (d.Code,d.Start,d.Length) == k).ToArray(), validation.Where(d => (d.Code,d.Start,d.Length) == k).ToArray())).ToArray();
    }
    private static bool ArticleCategorical(string code, JsonElement p) => p.GetProperty("categoricalArticleCodes").EnumerateArray().Any(x => x.GetString() == code);
    private static bool ArticleAvailability(string code, JsonElement p) => p.GetProperty("availabilityCodes").EnumerateArray().Any(x => x.GetString() == code);
    private static string[] UnknownArticleCodes(ArticleSignal[] rows, JsonElement p) => rows.Where(s => !ArticleCategorical(s.Code,p) && !ArticleAvailability(s.Code,p) &&
        s.Code != p.GetProperty("assessedValidCode").GetString() && !p.GetProperty("quoteAdvisoryCodes").EnumerateArray().Any(x => x.GetString() == s.Code)).Select(s => s.Code).Distinct().ToArray();
    private static bool ArticleProof(TextAnalysis a, ArticleTarget target, IReadOnlyList<TextReadingReference>? refs, bool requirePp, ArticleWitness? witness)
    {
        if (refs is not { Count: > 0 }) return false;
        if (requirePp) return witness is not null && refs.SequenceEqual(witness.RoleSupports);
        if (a.ReadingInventory is not { } inventory) return false;
        TokenAnalysis? carrier = a.Tokens.SingleOrDefault(t => t.Kind == TokenKind.Word && t.Start == target.Start && t.Length == target.Length);
        if (carrier is null) return false;
        int index = Array.IndexOf(a.Tokens.ToArray(), carrier);
        TextReadingOccurrence occurrence = inventory.Occurrences.Single(o => o.TokenIndex == index);
        int count = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.Count;
        if (count == 0 || !Enumerable.Range(0,count).All(i => refs.Contains(new TextReadingReference(index,i)))) return false;
        foreach (TextReadingReference r in refs)
        {
            var found = inventory.Occurrences.Where(o => o.TokenIndex == r.TokenIndex).ToArray();
            if (found.Length != 1 || (uint)r.CompleteReadingIndex >= (uint)inventory.Sets[found[0].ReadingSetIndex].ResolvedReadings.Count ||
                !a.Sentences.Any(s => target.Start >= s.Start && target.Start + target.Length <= s.Start + s.Length &&
                    found[0].Start >= s.Start && found[0].Start + found[0].Length <= s.Start + s.Length)) return false;
        }
        return true;
    }
    private sealed record ArticleWitness(string Key, TextReadingReference[] RoleSupports,
        TextReadingReference[] OriginalCoverage, TextReadingSnapshot[] CarrierRows);

    private static ArticleWitness? ArticlePpWitness(TextAnalysis a, IReadOnlyList<ArticleTarget> phrase)
    {
        if (a.ReadingInventory is not { } inventory || phrase.Count is not (2 or 3)) return null;
        ArticleTarget last = phrase[^1];
        if (last.Start < 0 || last.Length <= 0 || last.Start > a.Text.Length - last.Length) return null;
        int start = phrase[0].Start, end = last.Start + last.Length;
        if (start < 0 || end <= start ||
            a.Sentences.Count(s => s.Start >= 0 && s.Length > 0 && s.Start <= a.Text.Length - s.Length &&
                start >= s.Start && end <= s.Start + s.Length) != 1 ||
            ArticleOverlapsQuote(a.Text, start, end)) return null;
        List<int> indices = [];
        List<TextReadingSnapshot[]> rows = [];
        foreach (ArticleTarget part in phrase)
        {
            if (part.Start < 0 || part.Length <= 0 || part.Start > a.Text.Length - part.Length ||
                !a.Text.AsSpan(part.Start, part.Length).SequenceEqual(part.Surface)) return null;
            if (ArticleOccurrence(inventory.Occurrences, part) is not { } occurrence ||
                (uint)occurrence.TokenIndex >= (uint)a.Tokens.Count ||
                a.Tokens[occurrence.TokenIndex] is not { Kind: TokenKind.Word } token ||
                token.Start != part.Start || token.Length != part.Length || token.Text != part.Surface) return null;
            if (indices.Count > 0)
            {
                var previous = phrase[indices.Count - 1];
                int gap = previous.Start + previous.Length;
                if (occurrence.TokenIndex != indices[^1] + 1 || part.Start <= gap ||
                    !a.Text.AsSpan(gap, part.Start - gap).Trim().IsEmpty) return null;
            }
            var exact = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings.ToArray();
            if (exact.Length == 0 || exact.Any(r => r.Features != r.Raw.Features || r.Features.HasConflictingFeatures ||
                r.Features.HasUnrecognizedFeatures || r.Features.NameKind is not null ||
                !string.Equals(r.Raw.Form, part.Surface, StringComparison.OrdinalIgnoreCase))) return null;
            indices.Add(occurrence.TokenIndex);
            rows.Add(exact);
        }

        if (a.ProperNames is { } names && (names.Candidates.Any(n => n.Start < end && start < n.Start + n.Length) ||
            names.Groups.Any(n => n.Start < end && start < n.Start + n.Length) ||
            indices.Skip(1).Any(i => names.DeferredTokenIndices.Contains(i) ||
                names.UnresolvedContextTokens?.Any(n => n.TokenIndex == i) == true))) return null;
        if (rows[0].Any(r => r.Features.PartOfSpeech != MorphologicalPartOfSpeech.Preposition &&
            (r.Features.PartOfSpeech != MorphologicalPartOfSpeech.Particle ||
             !string.Equals(r.Raw.Lemma, phrase[0].Surface, StringComparison.OrdinalIgnoreCase)))) return null;

        bool bare = phrase.Count == 2;
        MorphologicalPartOfSpeech carrierPos = bare ? MorphologicalPartOfSpeech.Noun : MorphologicalPartOfSpeech.Adjective;
        var carriers = rows[1];
        var anchors = carriers.Where(r => r.Features.PartOfSpeech == carrierPos &&
            r.Features.Gender == MorphologicalGender.Masculine && r.Features.Number == MorphologicalNumber.Singular &&
            r.Features.Article is MorphologicalArticle.Full or MorphologicalArticle.Short or MorphologicalArticle.Definite).ToArray();
        if (anchors.Length == 0) return null;
        var anchor = anchors[0];
        if (carriers.Any(r => !ArticleSameOwner(r.Raw, anchor.Raw) ||
            (r.Features with { RawTag = null }) != (anchor.Features with { RawTag = null }) &&
            !(bare && anchor.Features.Article != MorphologicalArticle.Full && ArticleCountAlternative(r, anchor)))) return null;
        if (!bare && rows[2].Any(r => r.Features is not { PartOfSpeech: MorphologicalPartOfSpeech.Noun,
            Gender: MorphologicalGender.Masculine, Number: MorphologicalNumber.Singular, Article: MorphologicalArticle.Indefinite }))
            return null;

        // An adjacent second noun belongs to the literal counterexample, not to an isolated shorter witness.
        int next = indices[^1] + 1;
        if (next < a.Tokens.Count && a.Tokens[next] is { Kind: TokenKind.Word } following &&
            following.Start > end && following.Start <= a.Text.Length && a.Text.AsSpan(end, following.Start - end).Trim().IsEmpty &&
            inventory.Occurrences.Any(o => o.TokenIndex == next && inventory.Sets[o.ReadingSetIndex].ResolvedReadings.Any(
                r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.ProperNoun))) return null;

        // Complete raw coverage includes particles. Only PREP and all NP ordinals belong in the emitted proof.
        var rawCoverage = rows.SelectMany((set, i) => set.Select((_, j) => new TextReadingReference(indices[i], j))).ToArray();
        var emitted = rawCoverage.Where(r => r.TokenIndex != indices[0] ||
            rows[0][r.CompleteReadingIndex].Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition).ToArray();
        if (emitted.Length == 0 || emitted[0].TokenIndex != indices[0]) return null;
        return new("independent_preposition:" + phrase[0].Surface.ToLowerInvariant() + ":" + (phrase[1].Start - start),
            emitted, rawCoverage, carriers);
    }

    private static TextReadingOccurrence? ArticleOccurrence(IReadOnlyList<TextReadingOccurrence> occurrences, ArticleTarget part)
    {
        var spanMatches = occurrences.Where(o => o.Start == part.Start && o.Length == part.Length).ToArray();
        return spanMatches.Length == 1 && spanMatches[0].Text == part.Surface &&
            occurrences.Count(o => o.TokenIndex == spanMatches[0].TokenIndex) == 1 ? spanMatches[0] : null;
    }

    private static bool ArticleOverlapsQuote(string text, int start, int end)
    {
        for (int i = 0; i < end; i++)
        {
            char close = text[i] switch { '„' => '“', '“' => '”', '«' => '»', '‘' => '’',
                '"' or '\'' or '\u0060' => text[i], _ => '\0' };
            if (close == '\0') continue;
            int closing = text.IndexOf(close, i + 1);
            if (closing < 0 || closing >= start) return true;
            i = closing;
        }
        return false;
    }

    private static bool ArticleSameOccurrenceReadings(ArticleWitness witness, ArticleFormEvidence form) =>
        witness.CarrierRows.Select(r => ArticleReadingIdentity(r.Raw)).ToHashSet()
            .SetEquals(form.Readings.Select(r => ArticleReadingIdentity(r.Raw)));

    private static (string Form, string Lemma, string? Tag, int? Type, uint? Base, uint? Word, string Source)
        ArticleReadingIdentity(WordReading row) =>
        (row.Form.ToLowerInvariant(), row.Lemma.ToLowerInvariant(), row.PartOfSpeech,
         row.LegacyTypeId, row.LegacyBaseId, row.LegacyWordId, row.Source);

    private static bool ArticleSameOwner(WordReading a, WordReading b) =>
        string.Equals(a.Lemma, b.Lemma, StringComparison.OrdinalIgnoreCase) && a.Features.ParadigmId is not null &&
        a.Features.ParadigmId == b.Features.ParadigmId && a.Features.PartOfSpeech == b.Features.PartOfSpeech &&
        a.LegacyTypeId == b.LegacyTypeId && a.LegacyBaseId == b.LegacyBaseId && a.Source == b.Source;

    private static bool ArticleCountAlternative(TextReadingSnapshot row, TextReadingSnapshot anchor) =>
        row.Features is { Number: MorphologicalNumber.Count, Article: MorphologicalArticle.Indefinite } &&
        (row.Features with { RawTag = null, Number = anchor.Features.Number, Article = anchor.Features.Article }) ==
        (anchor.Features with { RawTag = null });

    private static void ArticleLocalProofControls()
    {
        string[] badInput = ["missing_inventory", "missing_head_occurrence", "unsupported_raw_prep", "foreign_particle",
            "conjunction_prep", "mismatch_head", "proper_head", "unknown_head", "count_only", "foreign_count",
            "malformed_head_span", "malformed_sentence_span", "overflowing_sentence_span", "second_head", "quote", "comma_in_group", "articulated_head_hint",
            "mixed_adj_verb", "mixed_noun_verb", "mixed_noun_proper"];
        string[] badProof = ["particle_only_role", "extra_particle_role", "omitted_eligible_prep", "foreign_role_token",
            "foreign_role_ordinal", "duplicate_role_ref", "old_ordinals_after_reorder", "missing_head_support", "predicate_support"];
        string[] outside = ["missing_local", "wrong_local_evidence", "incomplete_context", "truncated_context",
            "missing_hypothesis", "foreign_hypothesis", "empty_predicate_role", "graph_local_role", "nonverb_predicate",
            "predicate_nonverb_alternative", "comma", "unavailable_context_and_syntax"];
        string? relation = null;
        foreach (string mode in new[] { "валиден_full", "валиден_short", "bare_short_count", "display_prefix_only",
            "multiple_preposition_ordinals", "reordered_preposition_ordinals", "missing_particle_coverage", "foreign_coverage",
            "counterfeit_occurrence_owner", "counterfeit_word_id" }.Concat(outside).Concat(badInput).Concat(badProof))
        {
            var input = ArticleLocalProofFixture(mode);
            ArticleWitness? witness = ArticlePpWitness(input.Analysis, input.Phrase);
            if (badInput.Contains(mode)) { Assert.IsNull(witness, mode); continue; }
            Assert.IsNotNull(witness, mode);
            bool rawCoverageMatches = ArticleSameSupport(witness.OriginalCoverage, input.Coverage);
            Assert.AreEqual(mode is not ("missing_particle_coverage" or "foreign_coverage"), rawCoverageMatches, mode);
            Assert.AreEqual(!badProof.Contains(mode),
                ArticleProof(input.Analysis, input.Phrase[1], input.Emitted, true, witness), mode);
            Assert.IsTrue(witness.OriginalCoverage.Contains(input.Particle), mode);
            Assert.IsFalse(witness.RoleSupports.Contains(input.Particle), mode);
            Assert.AreEqual(mode is not ("counterfeit_occurrence_owner" or "counterfeit_word_id"),
                ArticleSameOccurrenceReadings(witness, input.NamedForm), mode);
            if (mode == "валиден_full")
            {
                relation = witness.Key;
                var inventory = input.Analysis.ReadingInventory!;
                var prep = ArticleOccurrence(inventory.Occurrences, input.Phrase[0])!.Value;
                // The public inventory constructor rejects duplicate token indices. Exercise the
                // occurrence matcher directly to cover the same ambiguity at a different span.
                TextReadingOccurrence[] duplicateAtForeignSpan = [..inventory.Occurrences, prep with { Start = 0 }];
                Assert.IsNull(ArticleOccurrence(duplicateAtForeignSpan, input.Phrase[0]), "duplicate-token-occurrence-at-foreign-span");
                Assert.ThrowsExactly<ArgumentException>(() => new TextReadingInventory(inventory.Sets, duplicateAtForeignSpan));
            }
            if (mode == "валиден_short" || outside.Contains(mode))
                Assert.AreEqual(relation, witness.Key, "Локалното отношение е независимо от Article и от материала извън PP: " + mode);
        }
    }

    private static (TextAnalysis Analysis, ArticleTarget[] Phrase, TextReadingReference[] Coverage,
        TextReadingReference[] Emitted, TextReadingReference Particle, ArticleFormEvidence NamedForm) ArticleLocalProofFixture(string mode)
    {
        bool bare=mode is "bare_short_count" or "count_only" or "foreign_count",shortForm=mode=="валиден_short"||bare;
        string carrier=bare ? "учителя" : shortForm ? "добрия" : "добрият";
        WordReading Row(string form,string lemma,string tag)=>new(form,lemma,tag,1,null,null,null,"independent-article-proof-control");
        List<WordReading[]> raw =
        [
            [Row("Говоря","говоря","Глагол:186,1л.,ед.ч.,Сегашно")],
            [Row("на","на","Предлог:192"),Row("на","на","Частица")],
            [Row(carrier,bare ? "учител" : "добър",(bare ? "Съществително:7" : "Прилагателно:76")+",Мъжки,"+(shortForm ? "Членуван" : "Пълен"))]
        ];
        if (bare) raw[2] = [..raw[2], Row(carrier, mode == "foreign_count" ? "друг" : "учител", "Съществително:7,Мъжки,Бройна")];
        else raw.Add([Row("учител","учител","Съществително:7,Мъжки")]);
        if (mode == "count_only") raw[2] = [raw[2][^1]];
        if(mode is "omitted_eligible_prep" or "multiple_preposition_ordinals") raw[1]=[..raw[1],Row("на","на","Предлог:192")];
        if(mode is "reordered_preposition_ordinals" or "old_ordinals_after_reorder")
            raw[1]=[Row("на","на","Частица"),Row("на","на","Предлог:192"),Row("на","на","Предлог:192")];
        if(mode=="unsupported_raw_prep") raw[1]=[raw[1][0],Row("на","на","Съществително:7,Мъжки")];
        if(mode=="foreign_particle") raw[1]=[raw[1][0],Row("на","друг","Частица")];
        if(mode=="conjunction_prep") raw[1]=[raw[1][0],Row("на","на","Съюз")];
        if(mode=="nonverb_predicate") raw[0]=[Row("Говоря","говоря","Съществително:7,Мъжки")];
        if(mode=="predicate_nonverb_alternative") raw[0]=[..raw[0],Row("Говоря","говоря","Съществително:7,Мъжки")];
        if(mode=="mismatch_head") raw[3]=[Row("учител","учител","Съществително:41,Женски")];
        if(mode=="proper_head") raw[3]=[Row("учител","учител","Име:7,Мъжки,ед.ч.")];
        if(mode=="unknown_head") raw[3]=[Row("учител","учител","unknown:500")];
        if(mode=="mixed_adj_verb") raw[2]=[..raw[2],Row(carrier,"добър","Глагол:186,1л.,ед.ч.,Сегашно")];
        if(mode=="mixed_noun_verb") raw[3]=[..raw[3],Row("учител","учител","Глагол:186,1л.,ед.ч.,Сегашно")];
        if(mode=="mixed_noun_proper") raw[3]=[..raw[3],Row("учител","учител","Име:7,Мъжки,ед.ч.")];
        if(mode=="articulated_head_hint") raw[3]=[Row("учителят","учител","Съществително:7,Мъжки,Пълен")];
        if(mode is "proper_head" or "mixed_noun_proper")
        {
            MorphologicalFeatures proper=raw[3][^1].Features;
            Assert.AreEqual(MorphologicalPartOfSpeech.ProperNoun,proper.PartOfSpeech,mode);
            Assert.AreEqual(MorphologicalNameKind.GivenName,proper.NameKind,mode);
            Assert.IsFalse(proper.HasUnrecognizedFeatures,mode);
        }
        string text="Говоря на "+carrier+(bare ? "" : " "+raw[3][0].Form)+".";
        if(mode=="quote") text="„"+text+"“";
        if(mode=="comma") text=text.Replace("Говоря на","Говоря, на",StringComparison.Ordinal);
        if(mode=="comma_in_group") text=text.Replace("на ","на, ",StringComparison.Ordinal);
        if(mode=="second_head") { text=text[..^1]+" гост."; raw.Add([Row("гост","гост","Съществително:7,Мъжки")]); }
        List<TokenAnalysis> tokens=[]; List<TextReadingSet> sets=[]; List<TextReadingOccurrence> occurrences=[];
        int cursor=0;
        for(int i=0;i<raw.Count;i++)
        {
            string form=raw[i][0].Form; int start=text.IndexOf(form,cursor,StringComparison.Ordinal); cursor=start+form.Length;
            tokens.Add(new(form,TokenKind.Word,start,form.Length,new(form,form.ToLowerInvariant(),raw[i],null)));
            sets.Add(new(form,form.ToLowerInvariant(),raw[i].Select(r=>new TextReadingSnapshot(r,r.Features)).ToArray(),null,null,
                mode=="display_prefix_only"&&i==1 ? 1 : raw[i].Length));
            occurrences.Add(new(i,form,start,form.Length,i));
        }
        int end = bare ? 3 : 4;
        var phrase = tokens.Skip(1).Take(end - 1).Select(t => new ArticleTarget(t.Start, t.Length, t.Text)).ToArray();
        var coverage = raw.Take(end).SelectMany((rows,i) => Enumerable.Range(0,rows.Length)
            .Select(j => new TextReadingReference(i,j))).Where(r => r.TokenIndex != 0).ToArray();
        if (mode == "missing_head_occurrence") occurrences.RemoveAt(3);
        if(mode=="missing_particle_coverage") coverage=coverage.Where(r=>r!=new TextReadingReference(1,1)).ToArray();
        if(mode=="foreign_coverage") coverage=[..coverage,new(999,0)];
        if(mode=="malformed_head_span") tokens[3]=tokens[3] with { Start=tokens[3].Start+1 };
        TextReadingReference[] prep=mode switch
        {
            "particle_only_role"=>[new(1,1)],"extra_particle_role"=>[new(1,0),new(1,1)],"foreign_role_ordinal"=>[new(1,9)],
            "foreign_role_token"=>[new(2,0)],"duplicate_role_ref"=>[new(1,0),new(1,0)],
            "multiple_preposition_ordinals" or "old_ordinals_after_reorder"=>[new(1,0),new(1,2)],
            "reordered_preposition_ordinals"=>[new(1,1),new(1,2)],_=>[new(1,0)]
        };
        var adposition=new TextContextRoleSelection(TextContextRole.Adposition,mode=="foreign_role_token" ? 2 : 1,tokens[1].Start,tokens[1].Length,prep,
            [mode=="wrong_local_evidence" ? "unlicensed_fixture" : bare ? "exact_adposition_with_nominal_complement" : "exact_adposition_with_coherent_adjective_noun_complement"])
            { GraphReadings=mode=="graph_local_role" ? [new(1,0,0)] : null };
        var predicate=new TextContextRoleSelection(TextContextRole.Predicate,0,tokens[0].Start,tokens[0].Length,
            mode=="empty_predicate_role" ? [] : [new(0,0)],["independent_finite_hypothesis_fixture"]);
        var hypothesis=new TextContextCandidate(mode=="foreign_hypothesis" ? 2 : 0,true,null,40,"independent_fixture",[predicate],[]);
        var contextSentence=new TextContextSentence(0,text.Length,mode!="incomplete_context",mode=="truncated_context" ? TextContextSelectionStatus.Truncated : TextContextSelectionStatus.Ambiguous,
            null,null,mode=="missing_hypothesis" ? [] : [hypothesis],mode=="missing_local" ? [] : [adposition],[],[]);
        var analysis=new TextAnalysis(text,tokens)
        {
            Sentences = mode switch
            {
                "malformed_sentence_span" => [new(-1, text.Length + 2)],
                "overflowing_sentence_span" => [new(1, int.MaxValue)],
                _ => [new(0, text.Length)]
            },
            ReadingInventory=mode=="missing_inventory" ? null : new(sets,occurrences),
            Context=mode=="unavailable_context_and_syntax" ? null : new("independent_fixture",true,null,[contextSentence]),
            Syntax=mode=="unavailable_context_and_syntax" ? null : new("independent_fixture",true,null,[new(0,text.Length,"Calculated","Abstained",null,[])],null)
        };
        var emitted = prep.Concat(raw.Take(end).SelectMany((set, i) => set.Select((_, j) => new TextReadingReference(i, j)))
            .Where(r => r.TokenIndex >= 2 && (mode != "missing_head_support" || r.TokenIndex != 3))).ToArray();
        if (mode == "predicate_support") emitted = [new(0, 0), ..emitted];
        int particle = mode is "reordered_preposition_ordinals" or "old_ordinals_after_reorder" ? 0 : 1;
        WordReading[] named = raw[2].ToArray();
        if (mode == "counterfeit_occurrence_owner") named[0] = named[0] with { Source = "foreign" };
        if (mode == "counterfeit_word_id") named[0] = named[0] with { LegacyWordId = 900 };
        return (analysis, phrase, coverage, emitted, new(1, particle), new(carrier, named.Select(r => new TextReadingSnapshot(r, r.Features)).ToArray()));
    }

    private static bool ArticleRequired(ArticleGeneratedCase c,JsonElement p)=>c.InitialDeliveryScope=="REQUIRED_BASIC" || p.GetProperty("declaredExtensionIds").EnumerateArray().Any(x=>x.GetString()==c.Id);
    private static (string Full,string Short,string Lemma)? ArticleControlForms(string id)=>id switch
    { "s01" or "s02" or "s17" or "s18"=>("учителят","учителя","учител"),"s03" or "s04"=>("добрият","добрия","добър"),
      "s05" or "s06"=>("ученикът","ученика","ученик"),_=>null };
    private static bool ArticleAssessed(TextAnalysis a, ArticleTarget target, JsonElement p, ArticleWitness? witness)
    {
        if (witness is null || a.Validation is not { Available: true }) return false;
        string? code = p.GetProperty("assessedValidCode").GetString(), prefix = p.GetProperty("assessedValidBasisPrefix").GetString();
        var facts = a.Validation.Diagnostics.Where(d => d.Code == code && d.Start == target.Start && d.Length == target.Length).ToArray();
        return code is not null && prefix is not null && facts.Length == 1 &&
            facts[0].Basis.StartsWith(prefix, StringComparison.Ordinal) && ArticleProof(a, target, facts[0].Supports, true, witness);
    }
    private static string? ArticlePairIssue(ArticleFormEvidence full, ArticleFormEvidence shortForm, string lemma)
    {
        if (full.Readings.Length == 0 || shortForm.Readings.Length == 0) return "MISSING_LEXICAL_EVIDENCE";
        if (full.Readings.Any(r=>!string.Equals(r.Raw.Form,full.Surface,StringComparison.OrdinalIgnoreCase)) ||
            shortForm.Readings.Any(r=>!string.Equals(r.Raw.Form,shortForm.Surface,StringComparison.OrdinalIgnoreCase))) return "FOREIGN_FORM_READING";
        TextReadingSnapshot anchor = full.Readings[0];
        if (!string.Equals(anchor.Raw.Lemma,lemma,StringComparison.OrdinalIgnoreCase) || anchor.Features is not
            { Article:MorphologicalArticle.Full, Number:MorphologicalNumber.Singular, Gender:MorphologicalGender.Masculine,
              PartOfSpeech:MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective, HasConflictingFeatures:false, HasUnrecognizedFeatures:false }) return "FULL_FEATURE_UNAVAILABLE";
        if (full.Readings.Any(r => r.Features != r.Raw.Features || !ArticleSameOwner(r.Raw,anchor.Raw) ||
            (r.Features with {RawTag=null}) != (anchor.Features with {RawTag=null}))) return "COMPETING_FULL_READINGS";
        bool matched = false;
        foreach (TextReadingSnapshot r in shortForm.Readings)
        {
            if (r.Features != r.Raw.Features || !ArticleSameOwner(r.Raw,anchor.Raw)) return "FOREIGN_COUNTERPART_IDENTITY";
            if (anchor.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                ArticleCountAlternative(r, anchor)) continue;
            if (r.Features.Article is not (MorphologicalArticle.Short or MorphologicalArticle.Definite) ||
                (r.Features with {RawTag=null,Article=anchor.Features.Article}) != (anchor.Features with {RawTag=null})) return "COMPETING_COUNTERPART_READINGS";
            matched = true;
        }
        return matched ? null : "NO_COMPATIBLE_COUNTERPART";
    }

    private sealed record ArticleProtectedResult(string Id, bool Passed, bool Required, bool WarningObserved, bool AssessedValid,
        string[] Violations, ArticleSignal[] Signals, string? Error);
    private static ArticleProtectedResult CheckArticleSurface(JsonElement c, JsonElement p, Func<string,ArticleFormEvidence> form, Func<string,TextAnalysis>? analyze = null)
    {
        string id=c.GetProperty("id").GetString()!, text=c.GetProperty("text").GetString()!, expected=c.GetProperty("expectation").GetString()!;
        bool required=c.TryGetProperty("scope",out var scope) && scope.GetString()=="REQUIRED_BASIC";
        try
        {
            TextAnalysis actual=(analyze ?? (t=>session.AnalyzeText(t,false,ArticleOptions,default)))(text); ArticleSignal[] signals=ArticleSignals(actual); List<string> errors=[];
            errors.AddRange(UnknownArticleCodes(signals,p).Select(x => "UNMAPPED:"+x));
            bool warning=false,assessed=false; ArticleWitness? requiredWitness=null;
            if (c.TryGetProperty("utf16",out var span))
            {
                int start=span.GetProperty("start").GetInt32(),length=span.GetProperty("length").GetInt32();
                warning=signals.Any(s => (required ? s.Code==p.GetProperty("expectedShortWarningCode").GetString() : ArticleCategorical(s.Code,p)) && s.Start==start && s.Length==length);

            }
            if (required)
            {
                var pair=ArticleControlForms(id);
                if (pair is null) throw new InvalidDataException("Required control lacks independent literal form metadata.");
                JsonElement range=c.TryGetProperty("utf16",out var targetSpan) ? targetSpan : c.GetProperty("protectedRanges").EnumerateArray().Single(r=>r.GetProperty("requireAssessedValid").GetBoolean());
                int start=range.GetProperty("start").GetInt32(),length=range.GetProperty("length").GetInt32();
                ArticleGeneratedCase specimen=new(id,id,"surface","prepositional_np",pair.Value.Lemma,null,pair.Value.Lemma,"control","control","postposed","control",
                    expected,"REQUIRED_BASIC","Short",text,new(start,length,text.Substring(start,length)),
                    expected=="MUST_WARN" ? c.GetProperty("replacement").GetString() : null,
                    expected=="MUST_WARN" ? c.GetProperty("expectedTextAfterRepair").GetString()! : text,"https://beron.mon.bg/rechnik/363",[],"Independent literal surface control.");
                var row=ObserveArticle(specimen,true,pair.Value.Full,pair.Value.Short,p,form,_=>actual);
                warning=row.ExactWarning; assessed=row.AssessedValid;
                if(row.Measured) requiredWitness=ArticleIndependentWitness(actual,specimen);
                if (!row.RequiredPass) errors.Add("REQUIRED_PUBLIC_PROFILE_UNMET");
                if (!row.Measured) errors.Add("OPERATIONAL_NONMEASUREMENT");
                if (!row.LexicallyApplicable) errors.Add("COUNTERPART_EVIDENCE_UNAVAILABLE:"+row.LexicalIssue);
                if (expected=="MUST_WARN" && !row.ExactWarning) errors.Add("MISSING_REQUIRED_WARNING");
                if (expected=="MUST_ACCEPT_LOCALLY" && !row.AssessedValid) errors.Add("EVALUATION_UNAVAILABLE");
                if (expected=="MUST_WARN" && !row.ProofValid) errors.Add("MISSING_ORIGINAL_PROOF");
                if (expected=="MUST_WARN" && row.CorrectProposals==0) errors.Add("MISSING_EXACT_REPAIR");
                if (row.NotReady) errors.Add("TARGET_NOT_READY");
                if (row.WrongLocalization>0) errors.Add("MISDIRECTED_ARTICLE_ACCUSATION");
                if (row.ProposalCount!=row.CorrectProposals) errors.Add("UNEXPECTED_ARTICLE_PROPOSAL");
            }
            if (c.TryGetProperty("protectedRanges",out var ranges)) foreach (JsonElement range in ranges.EnumerateArray())
            {
                int start=range.GetProperty("start").GetInt32(),length=range.GetProperty("length").GetInt32();
                if (text.Substring(start,length)!=range.GetProperty("surface").GetString()) throw new InvalidDataException("Bad protected range.");
                foreach (ArticleSignal s in signals.Where(s => s.Length==0 ? s.Start>=start && s.Start<=start+length : s.Start<start+length && start<s.Start+s.Length))
                {
                    bool changed=s.Root.Any(d=>d.ProjectedText is not null && (s.Start<0 || s.Length<0 || s.Start>text.Length-s.Length || d.ProjectedText!=text.Substring(s.Start,s.Length)));
                    bool quote=range.GetProperty("policy").GetString()=="QUOTE_ADVISORY_ONLY";
                    if (expected=="MUST_ABSTAIN" && s.Code==p.GetProperty("assessedValidCode").GetString()) errors.Add("UNWARRANTED_ASSESSED_VERDICT");
                    if (range.GetProperty("forbidActionableArticleReplacement").GetBoolean() && changed) errors.Add("FORBIDDEN_REPAIR:"+s.Code);
                    if (ArticleCategorical(s.Code,p) && (range.GetProperty("forbidCategoricalArticleAccusation").GetBoolean() || quote)) errors.Add("FORBIDDEN_ACCUSATION:"+s.Code);
                    if (quote && !ArticleAvailability(s.Code,p) && !ArticleCategorical(s.Code,p) &&
                        (!p.GetProperty("quoteAdvisoryCodes").EnumerateArray().Any(x=>x.GetString()==s.Code) || changed ||
                         !s.Validation.Any(d=>d.Basis.StartsWith("quoted_source;non_actionable;",StringComparison.Ordinal)))) errors.Add("UNATTRIBUTED_QUOTE_ADVISORY:"+s.Code);
                }
                if (range.GetProperty("requireAssessedValid").GetBoolean())
                {
                    ArticleTarget target=new(start,length,text.Substring(start,length));
                    assessed=ArticleAssessed(actual,target,p,requiredWitness);
                    if (!assessed) errors.Add("EVALUATION_UNAVAILABLE");
                }
            }
            return new(id,errors.Count==0,required,warning,assessed,errors.ToArray(),signals,null);
        }
        catch(Exception e){return new(id,false,required,false,false,["OPERATIONAL_NONMEASUREMENT"],[],e.ToString());}
    }
    private sealed record ArticleModulePin(string Name,long Bytes,string Sha256);
    private static ArticleModulePin[] ArticleModulePins() => AppDomain.CurrentDomain.GetAssemblies().Where(a=>
        a.GetName().Name is "BgLang.Api" or "BgLang.Core" or "BgLang.Contracts" or "BgLang.Public.Tests").OrderBy(a=>a.GetName().Name,StringComparer.Ordinal)
        .Select(a=>new ArticleModulePin(a.GetName().Name!,new FileInfo(a.Location).Length,ArticleHash(File.ReadAllBytes(a.Location)))).ToArray();
    private static byte[] ArticlePinnedData(string name,string sha)
    { byte[] bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"data",name));
      Assert.AreEqual(sha,ArticleHash(bytes));return bytes; }
    private static string ArticleHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
