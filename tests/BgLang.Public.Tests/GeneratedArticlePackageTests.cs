// Reuses V7, preserves its assertions, and adds exact independent required-participant support guards.
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
                string? lexicalIssue = ArticlePairIssue(form(full), form(shortForm), c.CarrierLemma);
                TextAnalysis actual = analyze(c.Text);
                ArticleSignal[] signals = ArticleSignals(actual);
                string[] unknown = UnknownArticleCodes(signals, p);
                ArticleSignal[] accusations = signals.Where(s => ArticleCategorical(s.Code, p)).ToArray();
                string expectedCode = p.GetProperty(c.ExpectedArticle == "Short" ? "expectedShortWarningCode" : "expectedFullWarningCode").GetString()!;
                ArticleSignal[] exact = accusations.Where(s => s.Code == expectedCode && s.Start == c.Target.Start && s.Length == c.Target.Length).ToArray();
                var proposals = signals.SelectMany(s => s.Root.Where(d => d.ProjectedText is not null &&
                    (s.Start<0 || s.Length<0 || s.Start>c.Text.Length-s.Length || d.ProjectedText!=c.Text.Substring(s.Start,s.Length))).Select(d => (Signal:s, Diagnostic:d)))
                    .DistinctBy(p => (p.Signal.Code, p.Signal.Start, p.Signal.Length, p.Diagnostic.ProjectedText)).ToArray();
                int matchingProposals = proposals.Count(p => c.ExpectedReplacement is not null && p.Signal.Start == c.Target.Start && p.Signal.Length == c.Target.Length &&
                    p.Diagnostic.ProjectedText == c.ExpectedReplacement && ArticleCaseFactory.Repair(c, p.Diagnostic.ProjectedText!) == c.ExpectedTextAfterRepair);
                int correctProposals = proposals.Count(p => c.ExpectedReplacement is not null && p.Signal.Start == c.Target.Start && p.Signal.Length == c.Target.Length &&
                    p.Diagnostic.ProjectedText == c.ExpectedReplacement && ArticleCaseFactory.Repair(c, p.Diagnostic.ProjectedText) == c.ExpectedTextAfterRepair && lexicalIssue is null);
                TextReadingReference[]? independentSupport = ArticleIndependentSupport(actual, c);
                ArticleWitness? witness = ArticlePpWitness(actual, c.Target, p.GetProperty("independentPrepositionalRule").GetString()!, independentSupport);
                string? relation = witness?.Key;
                bool independentRelation = independentSupport is null || witness is not null && ArticleSameSupport(witness.OriginalCoverage, independentSupport);
                bool proof = independentRelation && exact.Length>0 && exact.All(s => s.Root.Length>0 && s.Validation.Length>0 && s.Root.All(d => ArticleProof(actual, c.Target, d.Readings, c.Construction == "prepositional_np", independentSupport) &&
                    (independentSupport is null || ArticleSameSupport(d.Readings, independentSupport)) &&
                    (c.ExpectedArticle != "Short" || d.RuleId == p.GetProperty("expectedShortRule").GetString()) &&
                    s.Validation.All(v => d.Readings is not null && v.Supports.SequenceEqual(d.Readings) &&
                        v.Basis.StartsWith("existing_diagnostic="+d.RuleId+";bounded_article_advice",StringComparison.Ordinal)))) &&
                    (c.Construction != "prepositional_np" || relation is not null);
                bool notReady = signals.Any(s => ArticleAvailability(s.Code, p) && s.Start == c.Target.Start && s.Length == c.Target.Length);
                bool assessed = c.Construction == "prepositional_np" && ArticleAssessed(actual, c.Target, p, independentSupport) && relation is not null && independentRelation && !notReady;
                bool correct = c.NormativeExpectation == "MUST_ACCEPT_LOCALLY";
                int wrongLocalization = accusations.Count(s => s.Start != c.Target.Start || s.Length != c.Target.Length);
                bool pass = lexicalIssue is null && unknown.Length == 0 && (correct
                    ? accusations.Length == 0 && proposals.Length == 0 && assessed
                    : exact.Length > 0 && wrongLocalization == 0 && correctProposals > 0 && correctProposals == proposals.Length && proof && !notReady);
                return new(c.Id, required, c.NormativeExpectation, true, lexicalIssue is null, lexicalIssue, exact.Length > 0,
                    wrongLocalization, proposals.Length, correctProposals, correct ? accusations.Length : 0,
                    assessed, correct && !assessed, notReady, proof, relation, pass, unknown, signals, null) { WrongProposals=proposals.Length-matchingProposals, UnverifiedProposals=matchingProposals-correctProposals, RelationAuthority=witness?.Authority };
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

    private static TextReadingReference[]? ArticleIndependentSupport(TextAnalysis actual, ArticleGeneratedCase c)
    {
        if (c.InitialDeliveryScope != "REQUIRED_BASIC") return null;
        if (actual.Text != c.Text || c.Construction != "prepositional_np" || c.Position != "postposed")
            throw new InvalidDataException("Required article case lost its declared construction.");
        (string Verb, string Preposition) frame = c.Frame switch
        {
            "govorya_za" => ("Говоря", "за"), "mislya_za" => ("Мисля", "за"),
            "pisha_na" => ("Пиша", "на"), "tragvam_bez" => ("Тръгвам", "без"),
            "control" when c.Id is "s01" or "s02" or "s03" or "s04" or "s17" or "s18" => ("Говоря", "с"),
            "control" when c.Id is "s05" or "s06" => ("Помагам", "на"),
            _ => throw new InvalidDataException("Required article case lacks an independent literal frame.")
        };
        bool adjective = c.Layout == "single_attribute" || c.Frame == "control" && (c.Id is "s03" or "s04");
        if (c.Frame != "control" && c.Layout is not ("noun" or "single_attribute"))
            throw new InvalidDataException("Required article case lost its declared head layout.");
        int predicateStart = c.Target.Start - frame.Verb.Length - frame.Preposition.Length - 2;
        int prepositionStart = predicateStart + frame.Verb.Length + 1;
        string prefix = frame.Verb + " " + frame.Preposition + " ";
        if (predicateStart < 0 || !string.Equals(c.Text.Substring(predicateStart, prefix.Length), prefix, StringComparison.OrdinalIgnoreCase) ||
            c.Text.Substring(c.Target.Start, c.Target.Length) != c.Target.Surface)
            throw new InvalidDataException("Required article literal participants do not match their original ranges.");
        List<ArticleParticipant> participants =
        [
            new(predicateStart, frame.Verb.Length, MorphologicalPartOfSpeech.Verb),
            new(prepositionStart, frame.Preposition.Length, MorphologicalPartOfSpeech.Preposition),
            new(c.Target.Start, c.Target.Length, adjective ? MorphologicalPartOfSpeech.Adjective : MorphologicalPartOfSpeech.Noun)
        ];
        if (adjective)
        {
            string head = c.Frame == "control" ? "учител" : c.NounLemma;
            int start = c.Target.Start + c.Target.Length + 1;
            if (c.Text[c.Target.Start + c.Target.Length] != ' ' ||
                !string.Equals(c.Text.Substring(start, head.Length), head, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Required article noun head lost its independent literal range.");
            participants.Add(new(start, head.Length, MorphologicalPartOfSpeech.Noun));
        }
        // Reuse the independently fixed original-occurrence/all-reading expansion.
        return ExpectedArticleSupport(actual, new ArticleCase(c.Id, c.PairId, c.Text, c.Target.Start, c.Target.Length,
            c.Target.Surface, c.NormativeExpectation == "MUST_WARN", c.ExpectedReplacement, c.ExpectedTextAfterRepair, participants.ToArray()));
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
    private static bool ArticleProof(TextAnalysis a, ArticleTarget target, IReadOnlyList<TextReadingReference>? refs, bool requirePp = false, IReadOnlyList<TextReadingReference>? expectedCoverage = null)
    {
        if (a.ReadingInventory is not { } inventory || refs is not { Count: > 0 }) return false;
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
        if (requirePp)
        { var witness=ArticlePpWitness(a,target,"BasicPrepositionalObject",expectedCoverage);
          if (witness is null || !witness.RoleSupports.All(refs.Contains) || !witness.OriginalCoverage.All(refs.Contains)) return false; }
        return true;
    }
    private sealed record ArticleWitness(string Key,TextReadingReference[] RoleSupports,TextReadingReference[] OriginalCoverage,string Authority);
    private static ArticleWitness? ArticlePpWitness(TextAnalysis a,ArticleTarget target,string rule,IReadOnlyList<TextReadingReference>? expectedCoverage = null)
    {
        if (a.Syntax is not {Available:true} || a.ReadingInventory is not { } inventory) return null;
        var candidates=a.Syntax.Sentences.Where(s=>s.Status=="Calculated").SelectMany(s=>s.Candidates).Where(c=>c.Role==TextSyntaxRole.PrepositionalObject &&
            c.RuleId==rule && c.Start<=target.Start && c.Start+c.Length>=target.Start+target.Length).ToArray();
        if (candidates.Length==0) return ArticleLocalPpWitness(a,target,rule,expectedCoverage);
        if (candidates.Length!=1) return null;
        TextSyntaxCandidate c=candidates[0];
        TokenAnalysis[] phrase=a.Tokens.Where(t=>t.Kind==TokenKind.Word && t.Start>=c.Start && t.Start+t.Length<=c.Start+c.Length).ToArray();
        if (phrase.Length<2 || c.Targets.Count!=phrase.Length || c.Context.Count!=1 ||
            c.Start!=phrase[0].Start || c.Start+c.Length!=phrase[^1].Start+phrase[^1].Length) return null;
        List<TextReadingReference> roleRefs=[], originalCoverage=[]; List<TextReadingSnapshot[]> rows=[];
        TextSyntaxSupport[] supports=c.Targets.Concat(c.Context).ToArray();
        for (int participant=0;participant<supports.Length;participant++)
        {
            TextSyntaxSupport support=supports[participant];
            var original=inventory.Occurrences.Where(o=>o.TokenIndex==support.TokenIndex).ToArray();
            if (original.Length!=1 || support.Basis is not ("ExactCategory" or "TypedFeatures") || support.GraphReadings is {Count:>0}) return null;
            var all=inventory.Sets[original[0].ReadingSetIndex].ResolvedReadings.ToArray();
            var exact=Enumerable.Range(0,all.Length).Select(i=>new TextReadingReference(support.TokenIndex,i)).ToArray();
            var selected=ArticleCheckedRoleSupport(support.TokenIndex,all.Select(r=>r.Features.PartOfSpeech).ToArray(),support.Readings,
                participant==0 ? ArticleProofRole.Preposition : participant==c.Targets.Count ? ArticleProofRole.Predicate : ArticleProofRole.Nominal);
            if (all.Length==0 || selected is null || all.Any(r=>r.Features!=r.Raw.Features || r.Features.HasConflictingFeatures ||
                r.Features.HasUnrecognizedFeatures || !string.Equals(r.Raw.Form,original[0].Text,StringComparison.OrdinalIgnoreCase))) return null;
            roleRefs.AddRange(selected); originalCoverage.AddRange(exact);
            rows.Add(participant==0 || participant==c.Targets.Count ? selected.Select(r=>all[r.CompleteReadingIndex]).ToArray() : all);
        }
        for (int i=0;i<phrase.Length;i++)
        {
            if (a.Tokens[c.Targets[i].TokenIndex]!=phrase[i]) return null;
            if (i>0 && !a.Text.AsSpan(phrase[i-1].Start+phrase[i-1].Length,phrase[i].Start-phrase[i-1].Start-phrase[i-1].Length).ToString().All(char.IsWhiteSpace)) return null;
        }
        if (phrase[1].Start!=target.Start || phrase[1].Length!=target.Length ||
            !rows[0].All(r=>r.Features.PartOfSpeech==MorphologicalPartOfSpeech.Preposition) ||
            !rows[^1].All(r=>r.Features.PartOfSpeech==MorphologicalPartOfSpeech.Verb && r.Features.VerbForm==MorphologicalVerbForm.Finite) ||
            a.Tokens[c.Context[0].TokenIndex].Start>=phrase[0].Start) return null;
        TokenAnalysis predicate=a.Tokens[c.Context[0].TokenIndex];
        if (a.Sentences.Count(s=>c.Start>=s.Start && c.Start+c.Length<=s.Start+s.Length &&
            predicate.Start>=s.Start && predicate.Start+predicate.Length<=s.Start+s.Length)!=1) return null;
        TextReadingSnapshot[] head=rows[phrase.Length-1];
        if (!head.Any(r=>r.Features.Number==MorphologicalNumber.Singular) || !head.All(r=>r.Features.PartOfSpeech==MorphologicalPartOfSpeech.Noun &&
            r.Features.Gender==MorphologicalGender.Masculine && r.Features.Number is MorphologicalNumber.Singular or MorphologicalNumber.Count && r.Features.NameKind is null)) return null;
        if (rows[1][0].Features.PartOfSpeech==MorphologicalPartOfSpeech.Noun)
        { if (phrase.Length!=2) return null; }
        else for (int i=1;i<phrase.Length-1;i++)
            if (!rows[i].All(r=>r.Features.PartOfSpeech==MorphologicalPartOfSpeech.Adjective && r.Features.Number==MorphologicalNumber.Singular &&
                r.Features.Gender==MorphologicalGender.Masculine && r.Features.NameKind is null)) return null;
        // Role supports keep every eligible original ordinal; complete input coverage keeps all other raw alternatives separately.
        // Count alternatives stay in original coverage. A bare count-only head or a numeral route is never accepted.
        return new("independent_preposition:"+phrase[0].Text.ToLowerInvariant()+":"+(target.Start-phrase[0].Start),roleRefs.ToArray(),originalCoverage.ToArray(),"global_basic_prepositional_object");
    }
    private static void ArticleLocalProofControls()
    {
        string[] rejected = ["particle_only_role","extra_particle_role","omitted_eligible_prep","foreign_role_token","foreign_role_ordinal","duplicate_role_ref",
            "missing_local","wrong_local_evidence","incomplete_context","truncated_context","missing_inventory","missing_hypothesis","foreign_hypothesis",
            "empty_predicate_role","graph_local_role","unsupported_raw_prep","nonverb_predicate","predicate_nonverb_alternative","mismatch_head","proper_head",
            "unknown_head","missing_head_coverage","missing_particle_coverage","foreign_coverage","malformed_head_span","second_head","quote","comma",
            "old_ordinals_after_reorder","mixed_adj_verb","mixed_noun_verb","mixed_noun_proper"];
        string? relation = null;
        foreach (string mode in new[] { "валиден_full", "валиден_short", "bare_short_count", "display_prefix_only",
            "multiple_preposition_ordinals", "reordered_preposition_ordinals", "articulated_head_hint" }.Concat(rejected))
        {
            var input = ArticleLocalProofFixture(mode);
            ArticleWitness? witness = ArticlePpWitness(input.Analysis,input.Target,"BasicPrepositionalObject",input.Coverage);
            if (rejected.Contains(mode)) { Assert.IsNull(witness,mode); continue; }
            Assert.IsNotNull(witness,mode); Assert.AreEqual("local_adposition_finite_hypothesis",witness.Authority,mode);
            Assert.IsTrue(ArticleSameSupport(witness.OriginalCoverage,input.Coverage),mode);
            TextReadingReference[] expectedPrep = mode switch
            {
                "multiple_preposition_ordinals" => [new(1,0),new(1,2)],
                "reordered_preposition_ordinals" => [new(1,1),new(1,2)],
                _ => [new(1,0)]
            };
            Assert.IsTrue(witness.RoleSupports.Where(r=>r.TokenIndex==1).SequenceEqual(expectedPrep),mode);
            var particle = new TextReadingReference(1,mode=="reordered_preposition_ordinals" ? 0 : 1);
            Assert.IsTrue(witness.OriginalCoverage.Contains(particle),mode);
            Assert.IsFalse(witness.RoleSupports.Contains(particle),mode);
            Assert.AreEqual(TextContextSelectionStatus.Ambiguous,input.Analysis.Context!.Sentences[0].Status,mode);
            Assert.HasCount(0,input.Analysis.Context.Sentences[0].SelectedRoles,mode);
            Assert.AreEqual("Abstained",input.Analysis.Syntax!.Sentences[0].Route,mode);
            Assert.HasCount(0,input.Analysis.Syntax.Sentences[0].Candidates,mode);
            if(mode=="валиден_full") relation=witness.Key;
            if(mode=="валиден_short") Assert.AreEqual(relation,witness.Key,"Смяната само на Article пази независимата relation.");
            // Articulated глава запазва role hint; реалният article consumer има отделен naked-head guard.
            if(mode=="articulated_head_hint") Assert.AreEqual(relation,witness.Key,"Head Article не избира local role и не удостоверява цялата NP.");
        }
    }
    private static (TextAnalysis Analysis,ArticleTarget Target,TextReadingReference[] Coverage) ArticleLocalProofFixture(string mode)
    {
        bool bare=mode=="bare_short_count",shortForm=mode=="валиден_short"||bare;
        string carrier=bare ? "учителя" : shortForm ? "добрия" : "добрият";
        WordReading Row(string form,string lemma,string tag)=>new(form,lemma,tag,1,null,null,null,"independent-article-proof-control");
        List<WordReading[]> raw =
        [
            [Row("Говоря","говоря","Глагол:186,1л.,ед.ч.,Сегашно")],
            [Row("на","на","Предлог:192"),Row("на","на","Частица")],
            [Row(carrier,bare ? "учител" : "добър",(bare ? "Съществително:7" : "Прилагателно:76")+",Мъжки,"+(shortForm ? "Членуван" : "Пълен"))]
        ];
        if(bare) raw[2]=[..raw[2],Row(carrier,"учител","Съществително:7,Мъжки,Бройна")];
        else raw.Add([Row("учител","учител","Съществително:7,Мъжки")]);
        if(mode is "omitted_eligible_prep" or "multiple_preposition_ordinals") raw[1]=[..raw[1],Row("на","на","Предлог:192")];
        if(mode is "reordered_preposition_ordinals" or "old_ordinals_after_reorder")
            raw[1]=[Row("на","на","Частица"),Row("на","на","Предлог:192"),Row("на","на","Предлог:192")];
        if(mode=="unsupported_raw_prep") raw[1]=[raw[1][0],Row("на","на","Съществително:7,Мъжки")];
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
        TextReadingReference[] coverage=raw.Take(bare ? 3 : 4).SelectMany((rows,i)=>Enumerable.Range(0,rows.Length).Select(j=>new TextReadingReference(i,j))).ToArray();
        if(mode=="missing_head_coverage") coverage=coverage.Where(r=>r.TokenIndex!=3).ToArray();
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
            Sentences=[new(0,text.Length)],ReadingInventory=mode=="missing_inventory" ? null : new(sets,occurrences),
            Context=new("independent_fixture",true,null,[contextSentence]),
            Syntax=new("independent_fixture",true,null,[new(0,text.Length,"Calculated","Abstained",null,[])],null)
        };
        return(analysis,new(tokens[2].Start,tokens[2].Length,carrier),coverage);
    }

    private enum ArticleProofRole { Preposition, Predicate, Nominal }
    private static TextReadingReference[]? ArticleCheckedRoleSupport(int token, IReadOnlyList<MorphologicalPartOfSpeech> pos,
        IReadOnlyList<TextReadingReference> actual, ArticleProofRole role)
    {
        bool Eligible(MorphologicalPartOfSpeech p) => role switch
        {
            ArticleProofRole.Preposition => p == MorphologicalPartOfSpeech.Preposition,
            ArticleProofRole.Predicate => p == MorphologicalPartOfSpeech.Verb,
            ArticleProofRole.Nominal => p is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective or MorphologicalPartOfSpeech.Numeral or
                MorphologicalPartOfSpeech.ProperNoun or MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.Pronoun,
            _ => false
        };
        TextReadingReference[] expected = Enumerable.Range(0, pos.Count).Where(i => Eligible(pos[i]))
            .Select(i => new TextReadingReference(token, i)).ToArray();
        return expected.Length > 0 && actual.SequenceEqual(expected) ? expected : null;
    }
    private static ArticleWitness? ArticleLocalPpWitness(TextAnalysis a, ArticleTarget target, string rule,
        IReadOnlyList<TextReadingReference>? expectedCoverage)
    {
        if (rule != "BasicPrepositionalObject" || expectedCoverage is null || a.ReadingInventory is not { } inventory ||
            a.Context is not { Available: true } context) return null;
        int[] indices = expectedCoverage.Select(r => r.TokenIndex).Distinct().OrderBy(i => i).ToArray();
        if (indices.Length is not (3 or 4) || indices.Any(i => (uint)i >= (uint)a.Tokens.Count)) return null;
        TokenAnalysis[] words = indices.Select(i => a.Tokens[i]).ToArray();
        if (words.Any(w => w.Kind != TokenKind.Word || w.Start < 0 || w.Length < 1 || w.Start > a.Text.Length - w.Length ||
            a.Text.Substring(w.Start, w.Length) != w.Text) || words[2].Start != target.Start || words[2].Length != target.Length ||
            words[2].Text != target.Surface) return null;
        SentenceSpan[] spans = a.Sentences.Where(s => words.All(w => w.Start >= s.Start && w.Start + w.Length <= s.Start + s.Length)).ToArray();
        if (spans.Length != 1) return null;
        SentenceSpan span = spans[0];
        if (a.Text.AsSpan(span.Start, span.Length).ContainsAny("\"'„“”«»‘’,;:") ||
            !a.Tokens.Select((t, i) => (t, i)).Where(x => x.t.Kind == TokenKind.Word && x.t.Start >= span.Start &&
                x.t.Start + x.t.Length <= span.Start + span.Length).Select(x => x.i).SequenceEqual(indices)) return null;
        for (int i = 1; i < words.Length; i++)
            if (words[i].Start <= words[i - 1].Start + words[i - 1].Length ||
                !a.Text.AsSpan(words[i - 1].Start + words[i - 1].Length, words[i].Start - words[i - 1].Start - words[i - 1].Length).ToString().All(char.IsWhiteSpace)) return null;
        List<TextReadingSnapshot[]> rows = []; List<TextReadingReference> coverage = [];
        for (int i = 0; i < indices.Length; i++)
        {
            TextReadingOccurrence[] occurrences = inventory.Occurrences.Where(o => o.TokenIndex == indices[i]).ToArray();
            if (occurrences.Length != 1 || occurrences[0].Start != words[i].Start || occurrences[0].Length != words[i].Length || occurrences[0].Text != words[i].Text) return null;
            TextReadingSnapshot[] all = inventory.Sets[occurrences[0].ReadingSetIndex].ResolvedReadings.ToArray();
            if (all.Length == 0 || all.Any(r => r.Features != r.Raw.Features || r.Features.HasConflictingFeatures || r.Features.HasUnrecognizedFeatures ||
                r.Features.NameKind is not null || !string.Equals(r.Raw.Form, words[i].Text, StringComparison.OrdinalIgnoreCase))) return null;
            rows.Add(all); coverage.AddRange(Enumerable.Range(0, all.Length).Select(j => new TextReadingReference(indices[i], j)));
        }
        if (!ArticleSameSupport(coverage, expectedCoverage) || !rows[0].All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Verb &&
                r.Features.VerbForm == MorphologicalVerbForm.Finite) || !rows[1].Any(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Preposition) ||
            !rows[1].All(r => r.Features.PartOfSpeech is MorphologicalPartOfSpeech.Preposition or MorphologicalPartOfSpeech.Particle)) return null;
        if (words.Length == 3)
        {
            if (!rows[2].Any(r => r.Features.Number == MorphologicalNumber.Singular) || !rows[2].All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                r.Features.Gender == MorphologicalGender.Masculine && r.Features.Number is MorphologicalNumber.Singular or MorphologicalNumber.Count)) return null;
        }
        else if (!rows[2].All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Adjective && r.Features.Gender == MorphologicalGender.Masculine &&
                r.Features.Number == MorphologicalNumber.Singular) || !rows[3].All(r => r.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                r.Features.Gender == MorphologicalGender.Masculine && r.Features.Number == MorphologicalNumber.Singular)) return null;
        int groupStart = words[1].Start, groupEnd = words[^1].Start + words[^1].Length;
        if (a.ProperNames is { } names && (names.Candidates.Any(n => n.Start < groupEnd && groupStart < n.Start + n.Length) ||
            names.Groups.Any(n => n.Start < groupEnd && groupStart < n.Start + n.Length) || indices.Skip(2).Any(i => names.DeferredTokenIndices.Contains(i) ||
                names.UnresolvedContextTokens?.Any(n => n.TokenIndex == i) == true))) return null;
        TextContextSentence[] localSentences = context.Sentences.Where(s => s.Start == span.Start && s.Length == span.Length).ToArray();
        if (localSentences.Length != 1 || !localSentences[0].Complete || localSentences[0].Status is null or TextContextSelectionStatus.Truncated ||
            !a.Syntax!.Sentences.Any(s => s.Start == span.Start && s.Length == span.Length && s.Status == "Calculated")) return null;
        TextContextSentence localSentence = localSentences[0];
        TextContextRoleSelection[] adpositions = localSentence.LocalRoles.Where(r => r.Role == TextContextRole.Adposition).ToArray();
        if (adpositions.Length != 1 || adpositions[0].TokenIndex != indices[1] || adpositions[0].Start != words[1].Start || adpositions[0].Length != words[1].Length ||
            adpositions[0].GraphReadings is { Count: > 0 } || adpositions[0].EvidenceCodes.Count != 1 || adpositions[0].EvidenceCodes[0] !=
                (words.Length == 3 ? "exact_adposition_with_nominal_complement" : "exact_adposition_with_coherent_adjective_noun_complement")) return null;
        TextReadingReference[]? prepRefs = ArticleCheckedRoleSupport(indices[1], rows[1].Select(r => r.Features.PartOfSpeech).ToArray(), adpositions[0].Readings, ArticleProofRole.Preposition);
        TextContextCandidate[] hypotheses = localSentence.Candidates.Where(c => c.Selectable).ToArray();
        if (prepRefs is null || hypotheses.Length == 0 || hypotheses.Any(c => c.PredicateTokenIndex != indices[0])) return null;
        TextReadingReference[]? predicateRefs = null;
        foreach (TextContextCandidate hypothesis in hypotheses)
        {
            TextContextRoleSelection[] predicates = hypothesis.Roles.Where(r => r.Role == TextContextRole.Predicate).ToArray();
            if (predicates.Length != 1 || predicates[0].TokenIndex != indices[0] || predicates[0].Start != words[0].Start || predicates[0].Length != words[0].Length ||
                predicates[0].GraphReadings is { Count: > 0 }) return null;
            predicateRefs = ArticleCheckedRoleSupport(indices[0], rows[0].Select(r => r.Features.PartOfSpeech).ToArray(), predicates[0].Readings, ArticleProofRole.Predicate);
            if (predicateRefs is null) return null;
        }
        TextReadingReference[] roles = prepRefs.Concat(coverage.Where(r => r.TokenIndex != indices[0] && r.TokenIndex != indices[1])).Concat(predicateRefs!).ToArray();
        return new("independent_preposition:" + words[1].Text.ToLowerInvariant() + ":" + (target.Start - words[1].Start), roles, coverage.ToArray(), "local_adposition_finite_hypothesis");
    }

    private static string? ArticleRelation(TextAnalysis a,ArticleTarget target,string rule,IReadOnlyList<TextReadingReference>? expectedCoverage = null)=>ArticlePpWitness(a,target,rule,expectedCoverage)?.Key;
    private static bool ArticleRequired(ArticleGeneratedCase c,JsonElement p)=>c.InitialDeliveryScope=="REQUIRED_BASIC" || p.GetProperty("declaredExtensionIds").EnumerateArray().Any(x=>x.GetString()==c.Id);
    private static (string Full,string Short,string Lemma)? ArticleControlForms(string id)=>id switch
    { "s01" or "s02" or "s17" or "s18"=>("учителят","учителя","учител"),"s03" or "s04"=>("добрият","добрия","добър"),
      "s05" or "s06"=>("ученикът","ученика","ученик"),_=>null };
    private static bool ArticleAssessed(TextAnalysis a, ArticleTarget target, JsonElement p, IReadOnlyList<TextReadingReference>? independentSupport = null)
    {
        string? code = p.GetProperty("assessedValidCode").GetString(), prefix = p.GetProperty("assessedValidBasisPrefix").GetString();
        return code is not null && prefix is not null && a.Validation is { Available:true } &&
            (independentSupport is null || a.Validation.Diagnostics.Count(d => d.Code == code && d.Start == target.Start && d.Length == target.Length) == 1) &&
            a.Validation.Diagnostics.Any(d => d.Code == code &&
            d.Start == target.Start && d.Length == target.Length && d.Basis.StartsWith(prefix,StringComparison.Ordinal) && ArticleProof(a,target,d.Supports,true,independentSupport) &&
            (independentSupport is null || ArticleSameSupport(d.Supports, independentSupport)));
    }
    private static string? ArticlePairIssue(ArticleFormEvidence full, ArticleFormEvidence shortForm, string lemma)
    {
        if (full.Readings.Length == 0 || shortForm.Readings.Length == 0) return "MISSING_LEXICAL_EVIDENCE";
        if (full.Readings.Any(r=>!string.Equals(r.Raw.Form,full.Surface,StringComparison.OrdinalIgnoreCase)) ||
            shortForm.Readings.Any(r=>!string.Equals(r.Raw.Form,shortForm.Surface,StringComparison.OrdinalIgnoreCase))) return "FOREIGN_FORM_READING";
        TextReadingSnapshot anchor = full.Readings[0];
        bool Owner(WordReading a, WordReading b) => string.Equals(a.Lemma,b.Lemma,StringComparison.OrdinalIgnoreCase) && a.Features.ParadigmId is not null &&
            a.Features.ParadigmId == b.Features.ParadigmId && a.Features.PartOfSpeech == b.Features.PartOfSpeech &&
            a.LegacyTypeId == b.LegacyTypeId && a.LegacyBaseId == b.LegacyBaseId && a.Source == b.Source;
        if (!string.Equals(anchor.Raw.Lemma,lemma,StringComparison.OrdinalIgnoreCase) || anchor.Features is not
            { Article:MorphologicalArticle.Full, Number:MorphologicalNumber.Singular, Gender:MorphologicalGender.Masculine,
              PartOfSpeech:MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective, HasConflictingFeatures:false, HasUnrecognizedFeatures:false }) return "FULL_FEATURE_UNAVAILABLE";
        if (full.Readings.Any(r => r.Features != r.Raw.Features || !Owner(r.Raw,anchor.Raw) ||
            (r.Features with {RawTag=null}) != (anchor.Features with {RawTag=null}))) return "COMPETING_FULL_READINGS";
        bool matched = false;
        foreach (TextReadingSnapshot r in shortForm.Readings)
        {
            if (r.Features != r.Raw.Features || !Owner(r.Raw,anchor.Raw)) return "FOREIGN_COUNTERPART_IDENTITY";
            if (r.Features.Number == MorphologicalNumber.Count && anchor.Features.PartOfSpeech == MorphologicalPartOfSpeech.Noun &&
                r.Features.Article == MorphologicalArticle.Indefinite &&
                (r.Features with {RawTag=null,Number=anchor.Features.Number,Article=anchor.Features.Article}) == (anchor.Features with {RawTag=null})) continue;
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
            bool warning=false,assessed=false; TextReadingReference[]? requiredSupport=null;
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
                if(row.Measured) requiredSupport=ArticleIndependentSupport(actual,specimen);
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
                    assessed=ArticleAssessed(actual,target,p,requiredSupport) && ArticleRelation(actual,target,p.GetProperty("independentPrepositionalRule").GetString()!,requiredSupport) is not null;
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
