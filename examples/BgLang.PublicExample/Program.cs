using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using BgLang.Api;
using BgLang.Contracts;

long entryTick = Stopwatch.GetTimestamp();
DateTime entryUtc = DateTime.UtcNow;
if (args.Length == 0)
    throw new ArgumentException("Usage: <manifest.json> [--json] [--measure] [--text-file <utf8.txt>]");
bool json = false, measure = false;
string? textFile = null;
for (int index = 1; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--json" when !json: json = true; break;
        case "--measure" when !measure: measure = true; break;
        case "--text-file" when textFile is null && index + 1 < args.Length &&
            !string.IsNullOrWhiteSpace(args[index + 1]) && !args[index + 1].StartsWith("--", StringComparison.Ordinal):
            textFile = args[++index]; break;
        default: throw new ArgumentException($"Unknown, repeated or incomplete option: {args[index]}");
    }
}
Console.OutputEncoding = Encoding.UTF8;
var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
options.Converters.Add(new JsonStringEnumConverter());
using Process process = Process.GetCurrentProcess();
var observations = new List<object>();
double? entryToOpenMilliseconds = null;
using var language = Query("Open", new { manifest = Path.GetFullPath(args[0]) },
    () => BulgarianLanguageSession.Open(args[0]), value => value.Provenance,
    value => $"Package: {value.Provenance.PackageId}");

Query("Lookup", new { word = "книгата", mode = "Exact", limit = 16 },
    () => language.Lookup("книгата", LookupMode.Exact, 16),
    values => values.Select(ReadingValue).ToArray(),
    values => string.Join("; ", values.Select(value => $"{value.Form} → {value.Lemma}: {value.PartOfSpeech}")));
Query("GenerateForms", new { lemma = "книга", limit = 256 },
    () => language.GenerateForms("книга", 256), values => values,
    values => string.Join("; ", values.Select(value => $"{value.Form}: {value.Tag}")));
foreach (string input in new[] { "книгата", "суперработник", "свръхсупертракторите", "супертрактор",
    "суперхубав", "суперкнига", "трактористка", "жълто-зелен", "жълто-зеления", "жълто--зелен", "жълто-книга" })
    Query("AnalyzeWord", new { word = input, maxReadings = 256 },
        () => language.AnalyzeWord(input, 256, CancellationToken.None), WordValue, WordSummary);
foreach (var input in new[] { (Word: "трактор", Limit: 16), (Word: "жълт", Limit: 256) })
    Query("DeriveWords", new { word = input.Word, limit = input.Limit },
        () => language.DeriveWords(input.Word, input.Limit),
        values => values.Select(PathValue).ToArray(),
        values => $"{input.Word}: returned {values.Count}; displayed {Math.Min(3, values.Count)}: " +
            JsonSerializer.Serialize(values.Take(3).Select(PathSummary), options));
foreach (string input in new[] { "писател", "трактористка", "жълто-зелен", "жълто-зеления", "жълто--зелен", "жълто-книга" })
    Query("DecomposeWord", new { word = input }, () => language.DecomposeWord(input), value => value,
        value => JsonSerializer.Serialize(value, options));
Query("FindSimilar", new { word = "книаг", maxDistance = 1, limit = 256 },
    () => language.FindSimilar("книаг", 1, 256),
    values => values.Select(value => new { value.Form, value.Distance, Readings = value.Readings.Select(ReadingValue).ToArray() }).ToArray(),
    values => JsonSerializer.Serialize(values.Select(value => new { value.Form, value.Distance,
        Readings = value.Readings.Select(ReadingValue).ToArray() }), options));
foreach (int maxSegments in new[] { 5, 1 })
    Query("SegmentWord", new { word = "старитекниги", limit = 5, maxSegments },
        () => language.SegmentWord("старитекниги", 5, maxSegments),
        values => values.Select(value => SegmentValue(value, PathValue)).ToArray(),
        values => JsonSerializer.Serialize(values.Select(value => SegmentValue(value, PathSummary)), options));
foreach (string input in new[] { "Книгата е на масата.", "Аз го четем.",
    "Големия човек е лекар. Писателя е човек. Големият човек е лекар. Видях големия човек." })
    AnalyzeText(input, null);
if (textFile is not null)
{
    // Input IO is outside the API timer. Invalid UTF-8 is refused rather than replaced silently.
    byte[] raw = File.ReadAllBytes(textFile);
    string input = new UTF8Encoding(false, true).GetString(raw);
    if (input.StartsWith('\uFEFF')) input = input[1..];
    AnalyzeText(input, new { path = Path.GetFullPath(textFile), bytes = raw.Length,
        sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)) });
}
var environment = new { runtime = Environment.Version.ToString(), RuntimeInformation.OSDescription,
    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
    GCSettings.IsServerGC, tempPath = Path.GetTempPath() };
var startup = measure ? new { entryUtc, processStartUtc = process.StartTime.ToUniversalTime(),
    approximateProcessAgeAtManagedEntryMilliseconds = (entryUtc - process.StartTime.ToUniversalTime()).TotalMilliseconds,
    managedEntryToOpenCompleteMilliseconds = entryToOpenMilliseconds } : null;
const string measurementNote = "One session; API timers exclude projection, output and memory snapshots. Fresh process does not clear OS cache. PeakWorkingSetBytes is lifetime peak; managed bytes are not process RAM; allocation deltas are process-wide. No resource acceptance threshold.";
if (json)
    Console.WriteLine(JsonSerializer.Serialize(new {
        schema = "bglang-public-example.v1", language.Provenance,
        environment, startup, measurementNote = measure ? measurementNote : null,
        observations }, new JsonSerializerOptions(options) { WriteIndented = true }));
else if (measure)
    Console.WriteLine(JsonSerializer.Serialize(new { environment, startup, measurementNote }, options));

T Query<T>(string method, object input, Func<T> call, Func<T, object> project, Func<T, string> summary)
{
    var before = measure ? Snapshot() : null;
    long started = Stopwatch.GetTimestamp();
    T result = call();
    long finished = Stopwatch.GetTimestamp();
    double elapsed = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
    if (method == "Open") entryToOpenMilliseconds = Stopwatch.GetElapsedTime(entryTick, finished).TotalMilliseconds;
    var after = measure ? Snapshot() : null;
    var measurement = measure ? new { milliseconds = elapsed, before, after,
        workingSetDeltaBytes = after!.WorkingSetBytes - before!.WorkingSetBytes,
        privateDeltaBytes = after.PrivateBytes - before.PrivateBytes,
        lifetimePeakIncreaseBytes = after.PeakWorkingSetBytes - before.PeakWorkingSetBytes,
        managedDeltaBytes = after.ManagedBytes - before.ManagedBytes,
        allocatedDeltaBytes = after.AllocatedBytes - before.AllocatedBytes } : null;
    if (json) observations.Add(new { method, input, result = project(result), measurement });
    if (!json)
    {
        Console.WriteLine($"{method}: {summary(result)}");
        if (measure) Console.WriteLine(JsonSerializer.Serialize(measurement, options));
    }
    return result;
}

MemorySnapshot Snapshot()
{
    process.Refresh();
    return new(process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64,
        GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(false));
}

void AnalyzeText(string input, object? file)
{
    Query("AnalyzeText", new { text = input, utf16Length = input.Length, file },
        () => language.AnalyzeText(input, CancellationToken.None),
        value => new { value.Text, value.Sentences, value.Phonetics, value.Diagnostics,
            Tokens = value.Tokens.Select(token => new { token.Text, token.Kind, token.Start, token.Length,
                Analysis = token.Analysis is { } word ? WordValue(word) : null }).ToArray() },
        value => $"{value.Tokens.Count} tokens; diagnostics={JsonSerializer.Serialize(value.Diagnostics, options)}\n" +
            string.Join("\n", value.Tokens.Where(token => token.Analysis is not null).Select(token =>
                $"  [{token.Start},{token.Length}] {WordSummary(token.Analysis!)}")));
}

object ReadingValue(WordReading value) => new { reading = value, value.Features };
object PathValue(WordDerivation value) => new {
    value.Word, value.Base, value.FromState, value.ToState, value.Segments, value.EdgeIds, value.RuleIds,
    value.ApplicationIds, value.Confidence, value.EvidenceCodes, value.Source, value.DerivedLemma, value.FullTags,
    value.IsConditional, value.ConditionalRootProgramId, value.ConditionalRootBaseFullTag,
    value.ConditionalRootSupportCount, value.ConditionalSupportingLemmas, value.UnmetExactSelfReadingRequirements, value.Features };
object PathSummary(WordDerivation value) => new {
    value.Word, value.Base, value.FromState, value.ToState, value.DerivedLemma, value.FullTags, value.RuleIds,
    value.IsConditional, value.ConditionalRootProgramId, value.ConditionalRootBaseFullTag, value.ConditionalRootSupportCount,
    supportingLemmas = value.ConditionalSupportingLemmas is { } donors ? new { returned = donors.Count,
        displayed = donors.Take(3).ToArray() } : null, value.UnmetExactSelfReadingRequirements };
object SegmentValue(WordSegmentationCandidate value, Func<WordDerivation, object> pathValue) => new {
    value.OriginalText, value.NormalizedText, value.SegmentedText, value.Rank, value.IsSyntaxRanked, value.Source,
    Segments = value.Segments.Select(segment => new { segment.Text, Readings = segment.Readings.Select(ReadingValue).ToArray(),
        MorphologyEvidence = segment.MorphologyEvidence is { } path ? pathValue(path) : null }).ToArray() };
object CandidateValue(MorphologyCandidate value) => new {
    value.Word, value.Lemma, value.BaseFullTag, value.FullTag, value.ProductiveRuleId, value.MatchedEndingLength,
    value.SupportingLemmas, value.Source, value.Features, value.BaseFeatures,
    ShorterEndingCandidates = value.ShorterEndingCandidates.Select(CandidateValue).ToArray() };
object Flags(WordAnalysis value) => new { value.GraphSearchIncomplete, value.GraphCandidatesTruncated,
    value.CandidateSearchLimited, value.CandidateEvidenceLimited, value.KnownBaseSearchLimited,
    value.BaseTypeSearchLimited, value.BaseTypeEvidenceLimited };
object WordValue(WordAnalysis value) => new { value.Word, value.NormalizedWord, value.IsKnown,
    value.GraphSearchIncomplete, value.GraphCandidatesTruncated, value.CandidateSearchLimited,
    value.CandidateEvidenceLimited, value.KnownBaseSearchLimited, value.BaseTypeSearchLimited, value.BaseTypeEvidenceLimited,
    Readings = value.Readings.Select(ReadingValue).ToArray(), value.BaseEvidence,
    GraphCandidates = value.GraphCandidates.Select(PathValue).ToArray(),
    Candidates = value.Candidates.Select(CandidateValue).ToArray(),
    KnownBaseCandidates = value.KnownBaseCandidates.Select(CandidateValue).ToArray(),
    BaseTypeCandidates = value.BaseTypeCandidates.Select(CandidateValue).ToArray() };
string WordSummary(WordAnalysis value) => $"{value.Word}: known={value.IsKnown}; readings={value.Readings.Count}; " +
    $"graph={value.GraphCandidates.Count}; candidates/known-base/base-type={value.Candidates.Count}/{value.KnownBaseCandidates.Count}/{value.BaseTypeCandidates.Count}; " +
    $"flags={JsonSerializer.Serialize(Flags(value), options)}; graph displayed {Math.Min(3, value.GraphCandidates.Count)}: " +
    JsonSerializer.Serialize(value.GraphCandidates.Take(3).Select(PathSummary), options);

sealed record MemorySnapshot(long WorkingSetBytes, long PrivateBytes, long PeakWorkingSetBytes, long ManagedBytes, long AllocatedBytes);
