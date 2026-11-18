[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [string]$OutputDir = "",

    [switch]$VerifyExpected
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiCsproj = (Resolve-Path (Join-Path $repoRoot "src/BgLang.Api/BgLang.Api.csproj")).Path
$resolvedManifest = (Resolve-Path $ManifestPath).Path
$ind59Path = (Resolve-Path (Join-Path $PSScriptRoot "evaluation/independent-59.v1.tsv")).Path
$udpipePath = (Resolve-Path (Join-Path $PSScriptRoot "evaluation/udpipe-independent-59.v1.conllu")).Path
$edge30Path = (Resolve-Path (Join-Path $PSScriptRoot "evaluation/edge-and-ambiguity-controls.v1.tsv")).Path
$expectedSummaryPath = Join-Path $PSScriptRoot "evaluation/expected-alpha2-summary.v1.json"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path ([System.IO.Path]::GetTempPath()) "bglang-public-eval"
}
$resolvedOutDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $resolvedOutDir | Out-Null

$tempRunnerDir = Join-Path ([System.IO.Path]::GetTempPath()) ("bglang-public-eval-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tempRunnerDir | Out-Null

$csprojXml = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$apiCsproj" />
  </ItemGroup>
</Project>
"@

$programCs = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using BgLang.Api;
using BgLang.Contracts;

internal static class Program
{
    private static readonly HashSet<string> WarningCodes = new(StringComparer.Ordinal)
    {
        "missing_terminal_punctuation",
        "sentence_initial_lowercase",
        "mixed_script_ocr_known_candidate",
        "digit_shape_ocr_known_candidate",
        "accent_sensitive_i_grave",
        "joined_verb_negation_should_be_separate",
        "pronoun_verb_agreement_mismatch",
        "unseparated_finite_predications",
        "preposition_missing_complement",
        "full_article_expected_for_subject_candidate",
        "short_article_expected_after_preposition_candidate",
        "nominal_article_on_noninitial_member",
        "productive_morphology_agreement_mismatch",
        "productive_attribute_agreement_mismatch",
        "productive_predicative_agreement_mismatch",
        "auxiliary_participle_agreement_mismatch",
        "quantified_noun_form_mismatch",
        "personal_numeral_selection_mismatch",
        "particle_participle_verb_form_mismatch",
        "pronoun_case_governance_mismatch",
        "deictic_temporal_tense_mismatch"
    };

    private static readonly TextContextOptions DemoOptions = new()
    {
        IncludeAdditionalRoleHeuristics = true,
        IncludeSubordinateCommaDiagnostics = true,
        IncludeFragmentDiagnostics = true,
        IncludeSpacingDiagnostics = true,
        IncludeWordConstructions = true,
        IncludeNominalConstructions = true,
        IncludeGrammarEvidence = true
    };

    private static readonly Regex NonCyrillic = new(@"[^\p{IsCyrillic}]", RegexOptions.Compiled);

    private sealed record InputItem(string Kind, string Arg1, string Arg2, string Sentence);

    private sealed record UdToken(int Id, string Form, string Lemma, string Upos, string Xpos,
        Dictionary<string, string> Feats, int Head, string Dep);

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static int Main(string[] args)
    {
        if (args.Length != 5)
        {
            Console.Error.WriteLine("Expected 5 arguments: <manifest> <ind59.tsv> <udpipe.conllu> <edge30.tsv> <outDir>");
            return 2;
        }

        string manifestPath = args[0];
        string ind59Path = args[1];
        string udpipePath = args[2];
        string edge30Path = args[3];
        string outDir = args[4];

        using BulgarianLanguageSession session = BulgarianLanguageSession.Open(manifestPath);

        object ind59Summary = EvaluateDataset(
            session,
            ind59Path,
            "packaging/evaluation/independent-59.v1.tsv",
            udpipePath,
            "packaging/evaluation/udpipe-independent-59.v1.conllu",
            Path.Combine(outDir, "independent-59-results.jsonl"),
            includeRoleGold: true);

        object edge30Summary = EvaluateDataset(
            session,
            edge30Path,
            "packaging/evaluation/edge-and-ambiguity-controls.v1.tsv",
            null,
            null,
            Path.Combine(outDir, "edge-and-ambiguity-results.jsonl"),
            includeRoleGold: false);

        var summary = new
        {
            schema = "bglang.public-benchmark-summary.v1",
            packageId = session.Provenance.PackageId,
            options = new
            {
                includeGrammarEvidence = DemoOptions.IncludeGrammarEvidence,
                includeAdditionalRoleHeuristics = DemoOptions.IncludeAdditionalRoleHeuristics,
                includeWordConstructions = DemoOptions.IncludeWordConstructions,
                includeNominalConstructions = DemoOptions.IncludeNominalConstructions,
                includeSubordinateCommaDiagnostics = DemoOptions.IncludeSubordinateCommaDiagnostics,
                includeFragmentDiagnostics = DemoOptions.IncludeFragmentDiagnostics,
                includeSpacingDiagnostics = DemoOptions.IncludeSpacingDiagnostics,
                includeUnknownContextProperNames = false
            },
            surfaceAmbiguousReasonsInWarnings = new[] { "disjoint_subject_predicate_locus" },
            independent59 = ind59Summary,
            edgeAndAmbiguityControls = edge30Summary
        };

        string summaryJson = JsonSerializer.Serialize(summary, IndentedJson).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(Path.Combine(outDir, "summary.json"), summaryJson, new UTF8Encoding(false));
        Console.WriteLine($"Evaluated public benchmark for packageId={session.Provenance.PackageId}");
        return 0;
    }

    private static object EvaluateDataset(
        BulgarianLanguageSession session,
        string tsvPath,
        string relativeTsvName,
        string? udpipePath,
        string? relativeUdpipeName,
        string outJsonlPath,
        bool includeRoleGold)
    {
        List<InputItem> items = LoadInputItems(tsvPath);
        List<List<UdToken>>? udSentences = udpipePath is not null ? LoadUdpipeSentences(udpipePath) : null;
        if (udSentences is not null && udSentences.Count != items.Count)
            throw new InvalidOperationException($"UDPipe sentence count {udSentences.Count} != input count {items.Count}.");

        int totalWords = 0, bgPredResolved = 0;
        int rTotal = 0, rPredOk = 0, rPredWrong = 0, rPredNone = 0;
        int rSubjGold = 0, rSubjOk = 0, rSubjWrong = 0, rSubjNone = 0;
        int rNoSubjGold = 0, rNoSubjSpurious = 0;
        int cleanTotal = 0, cleanWords = 0, cleanWithGrammarWarning = 0, cleanGrammarWarnings = 0;
        SortedDictionary<string, int> cleanWarningsByCode = new(StringComparer.Ordinal);
        SortedDictionary<string, int[]> eByKind = new(StringComparer.Ordinal);
        int errWithExtraWarning = 0, errExtraWarningsTotal = 0;
        SortedDictionary<string, int> errExtraWarningsByCode = new(StringComparer.Ordinal);
        int ambigMatchesTotal = 0, unassessedMatchesTotal = 0, unavailableChecksTotal = 0;
        SortedDictionary<string, int> byAmbigReason = new(StringComparer.Ordinal);
        SortedDictionary<string, int> byUnassessedReason = new(StringComparer.Ordinal);
        SortedDictionary<string, int> bySyntaxRoute = new(StringComparer.Ordinal);
        SortedDictionary<string, int> bySyntaxStatus = new(StringComparer.Ordinal);
        SortedDictionary<string, int> byUnavailableCheck = new(StringComparer.Ordinal);

        int udVerbal = 0, bgPredWhenUdVerbal = 0, bgNoneWhenUdVerbal = 0, sameMainPred = 0;
        int udHasNsubjOnSamePred = 0, bothHaveSubj = 0, sameSubj = 0, bgOnlySubj = 0, udOnlySubj = 0, neitherSubj = 0;
        int udCleanTotal = 0, udCleanFlagged = 0, udCleanFlags = 0, udRPredOk = 0, udRSubjOk = 0;
        SortedDictionary<string, int[]> udEByKind = new(StringComparer.Ordinal);

        using var writer = new StreamWriter(outJsonlPath, append: false, new UTF8Encoding(false)) { NewLine = "\n" };

        for (int idx = 0; idx < items.Count; idx++)
        {
            InputItem item = items[idx];
            TextAnalysis analysis = session.AnalyzeText(item.Sentence, false, DemoOptions, CancellationToken.None);
            int wordCount = analysis.Tokens.Count(t => t.Kind == TokenKind.Word);
            totalWords += wordCount;

            List<(string Code, int Start, int Length)> messages = CollectMessages(analysis);
            List<(string Code, int Start, int Length)> grammarWarnings = messages
                .Where(m => WarningCodes.Contains(m.Code) && m.Code is not ("missing_terminal_punctuation" or "sentence_initial_lowercase"))
                .ToList();

            var disjointWarnings = new List<(string Code, int Start, int Length)>();
            foreach (TextGrammarMatch g in analysis.Grammar?.Matches ?? [])
            {
                if (g.Assessment == TextGrammarAssessment.Ambiguous &&
                    string.Equals(g.Reason, "disjoint_subject_predicate_locus", StringComparison.Ordinal) &&
                    g.DependentTokenIndices.Count == 1 &&
                    g.ControllerTokenIndices.Count >= 2 &&
                    g.Conflicts.Count >= 2)
                {
                    TokenAnalysis predTok = analysis.Tokens[g.DependentTokenIndices[0]];
                    int predEnd = predTok.Start + predTok.Length;
                    bool alreadyCovered = grammarWarnings.Any(w => w.Length > 0 && w.Start < predEnd && w.Start + w.Length > predTok.Start) ||
                        disjointWarnings.Any(w => w.Start == predTok.Start && w.Length == predTok.Length);
                    if (!alreadyCovered)
                        disjointWarnings.Add(("disjoint_subject_predicate_locus", predTok.Start, predTok.Length));
                }

                if (g.Assessment == TextGrammarAssessment.Ambiguous)
                {
                    ambigMatchesTotal++;
                    string rKey = g.Reason ?? "unspecified";
                    byAmbigReason[rKey] = byAmbigReason.GetValueOrDefault(rKey) + 1;
                }
                else if (g.Assessment == TextGrammarAssessment.Unassessed)
                {
                    unassessedMatchesTotal++;
                    string rKey = g.Reason ?? "unspecified";
                    byUnassessedReason[rKey] = byUnassessedReason.GetValueOrDefault(rKey) + 1;
                }
            }

            foreach (TextSyntaxSentence ss in analysis.Syntax?.Sentences ?? [])
            {
                bySyntaxRoute[ss.Route] = bySyntaxRoute.GetValueOrDefault(ss.Route) + 1;
                bySyntaxStatus[ss.Status] = bySyntaxStatus.GetValueOrDefault(ss.Status) + 1;
            }
            foreach (string uc in analysis.Validation?.UnavailableChecks ?? [])
            {
                unavailableChecksTotal++;
                byUnavailableCheck[uc] = byUnavailableCheck.GetValueOrDefault(uc) + 1;
            }

            List<(string Code, int Start, int Length)> surfacedWarnings = [.. grammarWarnings, .. disjointWarnings];

            TextSyntaxSentence? firstSyn = analysis.Syntax?.Sentences.FirstOrDefault(s => s.PredicateTokenIndex is not null);
            string? bgPred = firstSyn?.PredicateTokenIndex is int pi ? analysis.Tokens[pi].Text : null;
            string bgRoute = firstSyn?.Route ?? (analysis.Syntax?.Sentences.FirstOrDefault()?.Route ?? "none");
            if (bgPred is not null) bgPredResolved++;

            List<string> subjectSpans = (analysis.Syntax?.Sentences ?? [])
                .SelectMany(s => s.Candidates)
                .Where(c => c.Role == TextSyntaxRole.Subject)
                .Select(c => SpanText(item.Sentence, c.Start, c.Length))
                .ToList();

            List<string> subjectTokenWords = (analysis.Syntax?.Sentences ?? [])
                .SelectMany(s => s.Candidates)
                .Where(c => c.Role == TextSyntaxRole.Subject)
                .SelectMany(c => c.Targets)
                .Select(t => analysis.Tokens[t.TokenIndex].Text)
                .ToList();

            if (item.Kind is "R" or "C")
            {
                cleanTotal++;
                cleanWords += wordCount;
                if (surfacedWarnings.Count > 0) cleanWithGrammarWarning++;
                cleanGrammarWarnings += surfacedWarnings.Count;
                foreach (var w in surfacedWarnings)
                    cleanWarningsByCode[w.Code] = cleanWarningsByCode.GetValueOrDefault(w.Code) + 1;
            }

            if (item.Kind == "R")
            {
                rTotal++;
                if (bgPred is null) rPredNone++;
                else if (SameWord(bgPred, item.Arg1)) rPredOk++;
                else rPredWrong++;

                if (item.Arg2 == "-")
                {
                    rNoSubjGold++;
                    if (subjectSpans.Count > 0) rNoSubjSpurious++;
                }
                else
                {
                    rSubjGold++;
                    if (subjectSpans.Count == 0) rSubjNone++;
                    else if (subjectTokenWords.Any(w => SameWord(w, item.Arg2))) rSubjOk++;
                    else rSubjWrong++;
                }
            }

            List<(string Code, int Start, int Length)> extraInErr = [];
            if (item.Kind == "E")
            {
                string[] targetWords = item.Arg2.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var targetGroups = targetWords
                    .Select(tw => analysis.Tokens.Select((t, ti) => (t, ti))
                        .Where(x => x.t.Kind == TokenKind.Word && SameWord(x.t.Text, tw))
                        .ToArray())
                    .ToArray();
                var targetToks = targetGroups.SelectMany(g => g).ToArray();
                bool allTargetsSurfaced = targetGroups.Length > 0 && targetGroups.All(grp => grp.Any(tok => surfacedWarnings.Any(m =>
                    m.Start < tok.t.Start + tok.t.Length && m.Start + m.Length > tok.t.Start)));
                bool anyDisjoint = targetToks.Any(tok => disjointWarnings.Any(m =>
                    m.Start < tok.t.Start + tok.t.Length && m.Start + m.Length > tok.t.Start));
                bool onExact = allTargetsSurfaced && !anyDisjoint;
                bool onDisjoint = allTargetsSurfaced && anyDisjoint;
                bool conditional = targetToks.Any(tok => (analysis.Grammar?.Matches ?? []).Any(g =>
                    g.Assessment is TextGrammarAssessment.Mismatch or TextGrammarAssessment.Ambiguous &&
                    !string.Equals(g.Reason, "disjoint_subject_predicate_locus", StringComparison.Ordinal) &&
                    (g.ControllerTokenIndices.Contains(tok.ti) || g.DependentTokenIndices.Contains(tok.ti)) &&
                    g.Conflicts.Count > 0));
                int bucket = onExact ? 1 : onDisjoint ? 2 : surfacedWarnings.Count > 0 ? 3 : conditional ? 4 : 5;
                if (!eByKind.TryGetValue(item.Arg1, out int[]? counts)) eByKind[item.Arg1] = counts = new int[6];
                counts[0]++;
                counts[bucket]++;

                foreach (var w in surfacedWarnings)
                {
                    bool overlapsAnyTarget = targetToks.Any(tok => w.Start < tok.t.Start + tok.t.Length && w.Start + w.Length > tok.t.Start);
                    if (!overlapsAnyTarget)
                        extraInErr.Add(w);
                }
                if (extraInErr.Count > 0)
                {
                    errWithExtraWarning++;
                    errExtraWarningsTotal += extraInErr.Count;
                    foreach (var ew in extraInErr)
                        errExtraWarningsByCode[ew.Code] = errExtraWarningsByCode.GetValueOrDefault(ew.Code) + 1;
                }
            }

            if (udSentences is not null)
            {
                List<UdToken> u = udSentences[idx];
                UdToken? root = u.FirstOrDefault(t => t.Head == 0);
                if (root is not null)
                {
                    List<UdToken> helpers = u.Where(t => t.Head == root.Id && BaseDep(t.Dep) is "aux" or "cop").ToList();
                    bool verbal = root.Upos is "VERB" or "AUX" || helpers.Count > 0;
                    if (verbal)
                    {
                        udVerbal++;
                        if (bgPred is null) bgNoneWhenUdVerbal++;
                        else
                        {
                            bgPredWhenUdVerbal++;
                            HashSet<string> compoundForms = [NormCyr(root.Form), .. helpers.Select(h => NormCyr(h.Form))];
                            HashSet<string> bgForms = [NormCyr(bgPred)];
                            foreach (var ps in analysis.PredicateLinks?.Sentences ?? [])
                            {
                                foreach (var link in ps.Links)
                                {
                                    string lText = NormCyr(analysis.Tokens[link.Left.TokenIndex].Text);
                                    string rText = NormCyr(analysis.Tokens[link.Right.TokenIndex].Text);
                                    if (bgForms.Contains(lText) || bgForms.Contains(rText))
                                    {
                                        bgForms.Add(lText);
                                        bgForms.Add(rText);
                                    }
                                }
                            }
                            if (bgForms.Overlaps(compoundForms))
                            {
                                sameMainPred++;
                                List<string> uSubj = u.Where(t => t.Head == root.Id && BaseDep(t.Dep) == "nsubj")
                                    .Select(t => NormCyr(t.Form)).Where(s => s.Length > 0).ToList();
                                HashSet<string> bSubj = subjectSpans
                                    .SelectMany(sp => sp.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                                    .Select(NormCyr).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
                                if (uSubj.Count > 0) udHasNsubjOnSamePred++;
                                if (uSubj.Count > 0 && bSubj.Count > 0)
                                {
                                    bothHaveSubj++;
                                    if (uSubj.Any(bSubj.Contains)) sameSubj++;
                                }
                                else if (bSubj.Count > 0) bgOnlySubj++;
                                else if (uSubj.Count > 0) udOnlySubj++;
                                else neitherSubj++;
                            }
                        }
                    }

                    var udFlags = CheckUdpipeSixRules(u);
                    if (item.Kind is "R" or "C")
                    {
                        udCleanTotal++;
                        udCleanFlags += udFlags.Count;
                        if (udFlags.Count > 0) udCleanFlagged++;
                    }
                    if (item.Kind == "R")
                    {
                        List<UdToken> predParts = [root, .. helpers];
                        if (predParts.Any(p => SameWord(p.Form, item.Arg1))) udRPredOk++;
                        List<UdToken> nsubjs = u.Where(t => t.Head == root.Id && BaseDep(t.Dep) == "nsubj").ToList();
                        if (item.Arg2 != "-" && nsubjs.Any(t => SameWord(t.Form, item.Arg2))) udRSubjOk++;
                    }
                    else if (item.Kind == "E")
                    {
                        var byId = u.ToDictionary(t => t.Id);
                        bool hitTarget = udFlags.Any(f => byId.TryGetValue(f.TokenId, out UdToken? tok) && SameWord(tok.Form, item.Arg2));
                        int b = hitTarget ? 1 : udFlags.Count > 0 ? 2 : 3;
                        if (!udEByKind.TryGetValue(item.Arg1, out int[]? uc)) udEByKind[item.Arg1] = uc = new int[4];
                        uc[0]++;
                        uc[b]++;
                    }
                }
            }

            var row = new
            {
                index = idx + 1,
                kind = item.Kind,
                arg1 = item.Arg1,
                arg2 = item.Arg2,
                sentence = item.Sentence,
                bgRoute,
                bgPredicate = bgPred,
                bgSubjects = subjectSpans,
                warnings = surfacedWarnings.Select(m => new { code = m.Code, text = SpanText(item.Sentence, m.Start, m.Length) }).ToArray(),
                extraWarnings = extraInErr.Select(m => new { code = m.Code, text = SpanText(item.Sentence, m.Start, m.Length) }).ToArray(),
                grammarAmbiguous = (analysis.Grammar?.Matches ?? [])
                    .Where(g => g.Assessment == TextGrammarAssessment.Ambiguous)
                    .Select(g => new { ruleId = g.RuleId, reason = g.Reason ?? "unspecified" }).ToArray(),
                grammarUnassessed = (analysis.Grammar?.Matches ?? [])
                    .Where(g => g.Assessment == TextGrammarAssessment.Unassessed)
                    .Select(g => new { ruleId = g.RuleId, reason = g.Reason ?? "unspecified" }).ToArray()
            };
            writer.WriteLine(JsonSerializer.Serialize(row, CompactJson));
        }

        object? roleGoldObj = includeRoleGold
            ? new
            {
                sentences = rTotal,
                predicateOk = rPredOk,
                predicateWrong = rPredWrong,
                predicateNone = rPredNone,
                subjectGoldSentences = rSubjGold,
                subjectOk = rSubjOk,
                subjectWrong = rSubjWrong,
                subjectNone = rSubjNone,
                noSubjectGoldSentences = rNoSubjGold,
                noSubjectSpurious = rNoSubjSpurious
            }
            : null;

        object? udpipeObj = udSentences is null
            ? null
            : new
            {
                udpipeConllu = relativeUdpipeName,
                udVerbalSentences = udVerbal,
                bgPredicateWhenUdVerbal = bgPredWhenUdVerbal,
                bgMissingWhenUdVerbal = bgNoneWhenUdVerbal,
                sameMainPredicate = sameMainPred,
                sameMainPredicateOverUdVerbalPct = Pct(sameMainPred, udVerbal),
                sameMainPredicateOverBothResolvedPct = Pct(sameMainPred, bgPredWhenUdVerbal),
                subjectAmongSameMainPredicate = new
                {
                    udHasNsubj = udHasNsubjOnSamePred,
                    bothHaveSubject = bothHaveSubj,
                    sameSubject = sameSubj,
                    sameSubjectOverUdNsubjPct = Pct(sameSubj, udHasNsubjOnSamePred),
                    sameSubjectOverBothPct = Pct(sameSubj, bothHaveSubj),
                    bgOnlySubject = bgOnlySubj,
                    udOnlySubject = udOnlySubj,
                    neitherSubject = neitherSubj
                },
                udpipeSixRuleBaseline = new
                {
                    rSentences = rTotal,
                    rPredicateOk = udRPredOk,
                    rSubjectOk = udRSubjOk,
                    cleanSentences = udCleanTotal,
                    cleanSentencesFlagged = udCleanFlagged,
                    cleanFlagsTotal = udCleanFlags,
                    errorTotal = udEByKind.Values.Sum(v => v[0]),
                    errorHitOnTarget = udEByKind.Values.Sum(v => v[1]),
                    errorHitElsewhere = udEByKind.Values.Sum(v => v[2]),
                    errorSilent = udEByKind.Values.Sum(v => v[3]),
                    errorByKind = udEByKind.ToDictionary(
                        kv => kv.Key,
                        kv => new { total = kv.Value[0], hitOnTarget = kv.Value[1], hitElsewhere = kv.Value[2], silent = kv.Value[3] },
                        StringComparer.Ordinal)
                }
            };

        return new
        {
            dataset = relativeTsvName,
            udpipeConllu = relativeUdpipeName,
            totals = new { sentences = items.Count, words = totalWords, bgPredicateResolved = bgPredResolved },
            roleGold = roleGoldObj,
            errorControls = new
            {
                total = eByKind.Values.Sum(v => v[0]),
                hitOnTarget = eByKind.Values.Sum(v => v[1] + v[2]),
                hitOnTargetExactDiagnostic = eByKind.Values.Sum(v => v[1]),
                hitOnTargetAmbiguousDisjoint = eByKind.Values.Sum(v => v[2]),
                hitElsewhere = eByKind.Values.Sum(v => v[3]),
                conditionalOnly = eByKind.Values.Sum(v => v[4]),
                silent = eByKind.Values.Sum(v => v[5]),
                sentencesWithExtraWarning = errWithExtraWarning,
                extraWarningsTotal = errExtraWarningsTotal,
                extraWarningsByCode = errExtraWarningsByCode,
                byKind = eByKind.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        total = kv.Value[0],
                        hitOnTarget = kv.Value[1] + kv.Value[2],
                        hitOnTargetExactDiagnostic = kv.Value[1],
                        hitOnTargetAmbiguousDisjoint = kv.Value[2],
                        hitElsewhere = kv.Value[3],
                        conditionalOnly = kv.Value[4],
                        silent = kv.Value[5]
                    },
                    StringComparer.Ordinal)
            },
            cleanControls = new
            {
                sentences = cleanTotal,
                words = cleanWords,
                sentencesWithGrammarWarning = cleanWithGrammarWarning,
                grammarWarningsTotal = cleanGrammarWarnings,
                grammarWarningsPer1000Words = cleanWords == 0 ? 0.0 : Math.Round(1000.0 * cleanGrammarWarnings / cleanWords, 3),
                byCode = cleanWarningsByCode
            },
            unassessedAndAmbiguity = new
            {
                sentences = items.Count,
                grammarAmbiguousMatchesTotal = ambigMatchesTotal,
                byAmbiguousReason = byAmbigReason,
                grammarUnassessedMatchesTotal = unassessedMatchesTotal,
                byUnassessedReason = byUnassessedReason,
                bySyntaxRoute = bySyntaxRoute,
                bySyntaxStatus = bySyntaxStatus,
                unavailableChecksTotal = unavailableChecksTotal,
                byUnavailableCheck = byUnavailableCheck
            },
            udpipeComparison = udpipeObj
        };
    }

    private static List<InputItem> LoadInputItems(string path)
    {
        List<InputItem> items = [];
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("R|", StringComparison.Ordinal) ||
                line.StartsWith("E|", StringComparison.Ordinal) ||
                line.StartsWith("C|", StringComparison.Ordinal))
            {
                string[] p = line.Split('|', 4);
                items.Add(new(p[0], p[1], p[2], p[3]));
            }
            else
            {
                items.Add(new("C", string.Empty, string.Empty, line));
            }
        }
        return items;
    }

    private static List<List<UdToken>> LoadUdpipeSentences(string path)
    {
        List<List<UdToken>> sentences = [];
        List<UdToken> cur = [];
        foreach (string raw in File.ReadLines(path, Encoding.UTF8))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#')) continue;
            if (string.IsNullOrWhiteSpace(line))
            {
                if (cur.Count > 0) { sentences.Add(cur); cur = []; }
                continue;
            }
            string[] cols = line.Split('\t');
            if (cols.Length < 8 || cols[0].Contains('-') || cols[0].Contains('.')) continue;
            Dictionary<string, string> feats = new(StringComparer.Ordinal);
            if (cols[5] != "_")
            {
                foreach (string kv in cols[5].Split('|'))
                {
                    int eq = kv.IndexOf('=');
                    if (eq > 0) feats[kv[..eq]] = kv[(eq + 1)..];
                }
            }
            cur.Add(new(int.Parse(cols[0]), cols[1], cols[2], cols[3], cols[4], feats, int.Parse(cols[6]), cols[7]));
        }
        if (cur.Count > 0) sentences.Add(cur);
        return sentences;
    }

    private static List<(int TokenId, string Rule)> CheckUdpipeSixRules(List<UdToken> s)
    {
        var byId = s.ToDictionary(t => t.Id);
        List<UdToken> Kids(int head, params string[] deps) =>
            s.Where(t => t.Head == head && deps.Contains(BaseDep(t.Dep))).ToList();

        UdToken? FinCarrier(UdToken h) =>
            h.Feats.GetValueOrDefault("VerbForm") == "Fin"
                ? h
                : Kids(h.Id, "aux", "cop").FirstOrDefault(a => a.Feats.GetValueOrDefault("VerbForm") == "Fin");

        static bool IsNum(string? n) => n is "Sing" or "Plur";
        List<(int, string)> flags = [];
        foreach (UdToken t in s)
        {
            string d = BaseDep(t.Dep);
            if (d == "nsubj" && byId.TryGetValue(t.Head, out UdToken? h))
            {
                bool coord = Kids(t.Id, "conj").Count > 0;
                string sp = t.Upos == "PRON" ? t.Feats.GetValueOrDefault("Person", "3") : "3";
                string? sn = t.Feats.GetValueOrDefault("Number");
                UdToken? v = FinCarrier(h);
                if (v is not null && v.Feats.TryGetValue("Person", out string? vp) &&
                    IsNum(v.Feats.GetValueOrDefault("Number")) && IsNum(sn) && !coord &&
                    (vp != sp || v.Feats["Number"] != sn))
                    flags.Add((v.Id, "subject-finite"));

                if ((h.Upos == "ADJ" || h.Feats.GetValueOrDefault("VerbForm") == "Part") &&
                    Kids(h.Id, "aux", "cop").Count > 0 && !coord &&
                    !string.Equals(t.Form, "вие", StringComparison.OrdinalIgnoreCase))
                {
                    string? hn = h.Feats.GetValueOrDefault("Number");
                    if (IsNum(hn) && IsNum(sn) &&
                        (hn != sn || (hn == "Sing" && h.Feats.TryGetValue("Gender", out string? hg) &&
                                      t.Feats.TryGetValue("Gender", out string? tg) && hg != tg)))
                        flags.Add((h.Id, "predicative"));
                }
            }
            if (d is "amod" or "det" && byId.TryGetValue(t.Head, out UdToken? mh) &&
                t.Upos is "ADJ" or "DET" or "PRON" or "NUM" && mh.Upos == "NOUN")
            {
                string? tn = t.Feats.GetValueOrDefault("Number"), hn = mh.Feats.GetValueOrDefault("Number");
                if (IsNum(tn) && IsNum(hn) &&
                    (tn != hn || (tn == "Sing" && t.Feats.TryGetValue("Gender", out string? tg) &&
                                  mh.Feats.TryGetValue("Gender", out string? hg) && tg != hg)))
                    flags.Add((t.Id, "modifier-noun"));
            }
            if (d == "aux" && t.Feats.GetValueOrDefault("VerbForm") == "Fin" && byId.TryGetValue(t.Head, out UdToken? ah))
            {
                if (ah.Feats.GetValueOrDefault("VerbForm") == "Part" && ah.Feats.GetValueOrDefault("Voice") != "Pass" &&
                    IsNum(ah.Feats.GetValueOrDefault("Number")) && IsNum(t.Feats.GetValueOrDefault("Number")) &&
                    ah.Feats["Number"] != t.Feats["Number"])
                    flags.Add((ah.Id, "aux-participle"));
            }
            if (t.Upos is "NOUN" or "ADJ" && (t.Xpos.StartsWith("Ncms", StringComparison.Ordinal) || t.Xpos.StartsWith("Ams", StringComparison.Ordinal)) &&
                t.Xpos.Length == 5 && t.Xpos[4] is 'f' or 'h')
            {
                UdToken? np = d == "amod" ? byId.GetValueOrDefault(t.Head) : t;
                if (np is not null)
                {
                    string nd = BaseDep(np.Dep);
                    bool prep = Kids(np.Id, "case").Count > 0;
                    if (t.Xpos[4] == 'f' && (prep || nd is "obj" or "iobj" or "obl" or "nmod"))
                        flags.Add((t.Id, "full-article"));
                    if (t.Xpos[4] == 'h' && nd == "nsubj" && !prep)
                        flags.Add((t.Id, "short-article"));
                }
            }
        }
        return flags;
    }

    private static List<(string Code, int Start, int Length)> CollectMessages(TextAnalysis a)
    {
        HashSet<(string, int, int)> seen = [];
        List<(string, int, int)> list = [];
        void Add(string code, int start, int length)
        {
            if (seen.Add((code, start, length))) list.Add((code, start, length));
        }
        foreach (var d in a.Diagnostics) Add(d.Code, d.Start, d.Length);
        if (a.Validation is { } v)
        {
            foreach (var d in v.Diagnostics) Add(d.Code, d.Start, d.Length);
            foreach (var d in v.SpacingDiagnostics ?? []) Add(d.Code, d.Start, d.Length);
            foreach (var d in v.SubordinateCommaDiagnostics ?? []) Add(d.Code, d.Start, d.Length);
            foreach (var d in v.FragmentDiagnostics ?? []) Add(d.Code, d.Start, d.Length);
        }
        return list;
    }

    private static string SpanText(string text, int start, int length) =>
        length == 0 ? "^" + start : text.Substring(start, length);

    private static bool SameWord(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string BaseDep(string dep)
    {
        int colon = dep.IndexOf(':');
        return colon >= 0 ? dep[..colon] : dep;
    }

    private static string NormCyr(string word) =>
        NonCyrillic.Replace(word.ToLowerInvariant(), string.Empty);

    private static double Pct(int num, int den) =>
        den == 0 ? 0.0 : Math.Round(100.0 * num / den, 2);
}
'@

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
try {
    [System.IO.File]::WriteAllText((Join-Path $tempRunnerDir "EvaluatePublic.csproj"), ($csprojXml -replace "`r`n", "`n"), $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $tempRunnerDir "Program.cs"), ($programCs -replace "`r`n", "`n"), $utf8NoBom)

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    & $dotnet run --project (Join-Path $tempRunnerDir "EvaluatePublic.csproj") -c Release -- `
        $resolvedManifest $ind59Path $udpipePath $edge30Path $resolvedOutDir
    if ($LASTEXITCODE -ne 0) {
        throw "Public benchmark evaluation failed with exit code $LASTEXITCODE."
    }

    if ($VerifyExpected) {
        if (-not (Test-Path $expectedSummaryPath)) {
            throw "Expected summary file not found: $expectedSummaryPath"
        }
        $actualSummaryPath = Join-Path $resolvedOutDir "summary.json"
        $actualNorm = ([System.IO.File]::ReadAllText($actualSummaryPath, $utf8NoBom)) -replace "`r`n", "`n"
        $expectedNorm = ([System.IO.File]::ReadAllText($expectedSummaryPath, $utf8NoBom)) -replace "`r`n", "`n"
        if ($actualNorm -cne $expectedNorm) {
            throw "Public benchmark summary mismatch against $expectedSummaryPath."
        }
        Write-Host "Verified summary.json matches expected-alpha2-summary.v1.json."
    }
}
finally {
    if (Test-Path $tempRunnerDir) {
        Remove-Item -Recurse -Force $tempRunnerDir -ErrorAction SilentlyContinue
    }
}
