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
var outputLines = new List<string>();
double? entryToOpenMilliseconds = null;
double? entryToOpenStartMilliseconds = null;
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
// Captured Legacy evidence, written observations and qualified predictions stay separate.
Query("GetStressEvidence", new { text = "😀 аванпост.", selection = new StressReadingSelection(1, "legacy-base:64", "аванпост", "Съществително:1,Мъжки") },
    () => language.GetStressEvidence("😀 аванпост.", new(1, "legacy-base:64", "аванпост", "Съществително:1,Мъжки")),
    value => value, value => value.RenderedText);
Query("GetStressEvidence", new { text = "абата кни\u0300ги жжжабракадабрю.", selection = new StressReadingSelection(0, "legacy-base:824640", "абата", "Съществително:41,Женски,Членуван") },
    () => language.GetStressEvidence("абата кни\u0300ги жжжабракадабрю.", new(0, "legacy-base:824640", "абата", "Съществително:41,Женски,Членуван")),
    value => value, value => value.RenderedText);
foreach (string prototypeEntryId in new[] { "legacy-base:64", " legacy-base:64 " })
    Query("GenerateStressForms", new { prototypeEntryId, lemma = "космоаванпост" },
        () => language.GenerateStressForms(prototypeEntryId, "космоаванпост"), value => value,
        value => JsonSerializer.Serialize(value, options));
Query("GetDerivationalStressEvidence", new { word = "играч" },
    () => language.GetDerivationalStressEvidence("играч"), value => value,
    value => JsonSerializer.Serialize(value, options));
// A prototype prediction does not insert a novel lemma into the exact dictionary.
Query("Lookup", new { word = "космоаванпост", mode = "Exact", limit = 16 },
    () => language.Lookup("космоаванпост", LookupMode.Exact, 16),
    values => values.Select(ReadingValue).ToArray(), values => JsonSerializer.Serialize(values, options));
Query("GenerateForms", new { lemma = "космоаванпост", limit = 256 },
    () => language.GenerateForms("космоаванпост", 256), values => values,
    values => JsonSerializer.Serialize(values, options));

foreach (string input in new[] { "книгата", "суперработник", "свръхсупертракторите", "супертрактор",
    "суперхубав", "суперкнига", "трактористка", "жълто-зелен", "жълто-зеления", "жълто--зелен", "жълто-книга" })
    Query("AnalyzeWord", new { word = input, maxReadings = 256 },
        () => language.AnalyzeWord(input, 256, CancellationToken.None), WordValue, WordSummary);
foreach (var input in new[] { (Word: "трактор", Limit: 16), (Word: "жълт", Limit: 256) })
    Query("DeriveWords", new { word = input.Word, limit = input.Limit },
        () => language.DeriveWords(input.Word, input.Limit),
        values => values.Select(PathValue).ToArray(),
        values => $"{input.Word}: " + JsonSerializer.Serialize(Preview(values, PathSummary), options));
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
    managedEntryToOpenCompleteMilliseconds = entryToOpenMilliseconds,
    managedEntryToOpenStartMilliseconds = entryToOpenStartMilliseconds } : null;
const string measurementNote = "One session; API timers exclude projection, output and memory snapshots. Fresh process does not clear OS cache. PeakWorkingSetBytes is lifetime peak; managed bytes are not process RAM; allocation deltas are process-wide. No resource acceptance threshold.";
object? document = null;
if (json)
    document = new {
        schema = "bglang-public-example.v1", language.Provenance,
        environment, startup, measurementNote = measure ? measurementNote : null,
        observations };
else if (measure)
    outputLines.Add(JsonSerializer.Serialize(new { environment, startup, measurementNote }, options));
var outputOptions = new JsonSerializerOptions(options) { WriteIndented = true };
byte[] newline = Encoding.UTF8.GetBytes(Environment.NewLine);
var beforeSerialization = measure ? Snapshot() : null;
long serializationStarted = Stopwatch.GetTimestamp();
byte[] payload = json ? JsonSerializer.SerializeToUtf8Bytes(document, outputOptions) :
    Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, outputLines));
long serializationFinished = Stopwatch.GetTimestamp();
var afterSerialization = measure ? Snapshot() : null;
using (Stream output = Console.OpenStandardOutput())
{
    output.Write(payload);
    output.Write(newline);
}
if (measure)
    Console.Error.WriteLine(JsonSerializer.Serialize(new {
        schema = "bglang-public-example.measurement.v1",
        format = json ? "json" : "console", payloadBytes = payload.LongLength,
        trailingNewlineBytes = newline.LongLength, outputBytes = payload.LongLength + newline.LongLength,
        payloadDefinition = "All stdout UTF8 bytes except the separately counted final newline; this stderr record is excluded.",
        serialization = PhaseValue(json ? "json-serialization-and-utf8" : "console-join-and-utf8",
            serializationStarted, serializationFinished, beforeSerialization!, afterSerialization!),
        outputIoMeasured = false, inputIoMeasured = false,
        phaseDefinition = "Preparation calls project or summary exactly once; console summary includes nested JSON formatting. API/preparation timers exclude snapshots and measurement-metadata construction/formatting. JSON final serialization includes every prepared envelope field, including measurement metadata. Console final serialization joins already formatted summary/measurement lines and encodes UTF8. Serialization timers exclude snapshots, measurement object construction, newline encoding and output IO. Allocation deltas can include snapshot overhead and other process allocations."
    }, options));

T Query<T>(string method, object input, Func<T> call, Func<T, object> project, Func<T, string> summary)
{
    var before = measure ? Snapshot() : null;
    long started = Stopwatch.GetTimestamp();
    T result = call();
    long finished = Stopwatch.GetTimestamp();
    double elapsed = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
    if (method == "Open")
    {
        entryToOpenMilliseconds = Stopwatch.GetElapsedTime(entryTick, finished).TotalMilliseconds;
        entryToOpenStartMilliseconds = Stopwatch.GetElapsedTime(entryTick, started).TotalMilliseconds;
    }
    var after = measure ? Snapshot() : null;
    var beforePreparation = measure ? Snapshot() : null;
    long preparationStarted = Stopwatch.GetTimestamp();
    object? projected = json ? project(result) : null;
    string? summarized = json ? null : summary(result);
    long preparationFinished = Stopwatch.GetTimestamp();
    var afterPreparation = measure ? Snapshot() : null;
    var measurement = measure ? new { milliseconds = elapsed, before, after,
        workingSetDeltaBytes = after!.WorkingSetBytes - before!.WorkingSetBytes,
        privateDeltaBytes = after.PrivateBytes - before.PrivateBytes,
        lifetimePeakIncreaseBytes = after.PeakWorkingSetBytes - before.PeakWorkingSetBytes,
        managedDeltaBytes = after.ManagedBytes - before.ManagedBytes,
        allocatedDeltaBytes = after.AllocatedBytes - before.AllocatedBytes,
        preparation = PhaseValue(json ? "json-projection" : "console-summary",
            preparationStarted, preparationFinished, beforePreparation!, afterPreparation!) } : null;
    if (json) observations.Add(new { method, input, result = projected, measurement });
    if (!json)
    {
        outputLines.Add($"{method}: {summarized}");
        if (measure) outputLines.Add(JsonSerializer.Serialize(measurement, options));
    }
    return result;
}

object PhaseValue(string phase, long started, long finished, MemorySnapshot before, MemorySnapshot after) => new {
    phase, milliseconds = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds, before, after,
    workingSetDeltaBytes = after.WorkingSetBytes - before.WorkingSetBytes,
    privateDeltaBytes = after.PrivateBytes - before.PrivateBytes,
    lifetimePeakIncreaseBytes = after.PeakWorkingSetBytes - before.PeakWorkingSetBytes,
    managedDeltaBytes = after.ManagedBytes - before.ManagedBytes,
    allocatedDeltaBytes = after.AllocatedBytes - before.AllocatedBytes };

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
        value => new { value.Text, value.Sentences, value.Phonetics,
            Diagnostics = value.Diagnostics.Select(DiagnosticValue).ToArray(),
            Tokens = value.Tokens.Select(token => new { token.Text, token.Kind, token.Start, token.Length,
                Analysis = token.Analysis is { } word ? WordValue(word) : null }).ToArray(),
            value.Lexical, value.ReadingInventory, value.Metrics, value.Spelling, value.Profile, value.ProperNames,
            value.Context, value.Syntax, value.Validation, value.WordConstructions },
        value => $"{value.Tokens.Count} tokens; diagnostics={JsonSerializer.Serialize(value.Diagnostics.Select(DiagnosticValue), options)}\n" +
            string.Join("\n", value.Tokens.Where(token => token.Analysis is not null).Select(token =>
                $"  [{token.Start},{token.Length}] {WordSummary(token.Analysis!)}")));
}

// Consumer guidance only: a candidate warning asks for review; it is not an automatic correction.
// Unknown codes retain their identity as information and do not establish check coverage.
object DiagnosticValue(TextDiagnostic value) => new {
    value.Code, value.Start, value.Length, value.RuleId, value.ProjectedText,
    Classification = value.Code switch {
        "full_short_article_check_not_ready" => "unsupported-check",
        "missing_terminal_punctuation" or "sentence_initial_lowercase" or
        "mixed_script_ocr_known_candidate" or "digit_shape_ocr_known_candidate" or
        "accent_sensitive_i_grave" or "spelling_unknown_word_unique_distance_one_candidate" or
        "pronoun_verb_agreement_mismatch" or "preposition_missing_complement" or
        "full_article_expected_for_subject_candidate" or "productive_morphology_agreement_mismatch" or
        "productive_attribute_agreement_mismatch" => "warning",
        _ => "information"
    }
};

object ReadingValue(WordReading value) => new { reading = value, value.Features };
object PathValue(WordDerivation value) => new {
    value.Word, value.Base, value.FromState, value.ToState, value.Segments, value.EdgeIds, value.RuleIds,
    value.ApplicationIds, value.Confidence, value.EvidenceCodes, value.Source, value.DerivedLemma, value.FullTags,
    value.IsConditional, value.ConditionalRootProgramId, value.ConditionalRootBaseFullTag,
    value.ConditionalRootSupportCount, value.ConditionalSupportingLemmas, value.UnmetExactSelfReadingRequirements, value.Features };
object PathSummary(WordDerivation value) => new {
    value.Word, value.Base, value.FromState, value.ToState, value.DerivedLemma, value.FullTags, value.RuleIds,
    value.IsConditional, value.ConditionalRootProgramId, value.ConditionalRootBaseFullTag, value.ConditionalRootSupportCount,
    supportingLemmas = value.ConditionalSupportingLemmas is { } donors ? Preview(donors, donor => donor) : null,
    value.UnmetExactSelfReadingRequirements, rootAuthority = value.IsConditional ? "conditional" : "exact-self-reading" };
object SegmentValue(WordSegmentationCandidate value, Func<WordDerivation, object> pathValue) => new {
    value.OriginalText, value.NormalizedText, value.SegmentedText, value.Rank, value.IsSyntaxRanked, value.Source,
    Segments = value.Segments.Select(segment => new { segment.Text, Readings = segment.Readings.Select(ReadingValue).ToArray(),
        MorphologyEvidence = segment.MorphologyEvidence is { } path ? pathValue(path) : null }).ToArray() };
object CandidateValue(MorphologyCandidate value) => new {
    value.Word, value.Lemma, value.BaseFullTag, value.FullTag, value.ProductiveRuleId, value.MatchedEndingLength,
    value.SupportingLemmas, value.Source, value.Features, value.BaseFeatures,
    ShorterEndingCandidates = value.ShorterEndingCandidates.Select(CandidateValue).ToArray() };
object CandidateSummary(MorphologyCandidate value) => new {
    value.Word, value.Lemma, value.BaseFullTag, value.FullTag, value.ProductiveRuleId, value.MatchedEndingLength,
    supportingLemmas = Preview(value.SupportingLemmas, donor => donor), value.Source, value.Features, value.BaseFeatures,
    ShorterEndingCandidates = Preview(value.ShorterEndingCandidates, CandidateSummary), authority = "hypothesis" };
// Display only; the API result and full JSON retain every item and its original order.
object Preview<T>(IReadOnlyList<T> values, Func<T, object> project) => new {
    total = values.Count, displayed = Math.Min(3, values.Count), items = values.Take(3).Select(project).ToArray() };
object Flags(WordAnalysis value) => new { value.GraphSearchIncomplete, value.GraphCandidatesTruncated,
    value.CandidateSearchLimited, value.CandidateEvidenceLimited, value.KnownBaseSearchLimited,
    value.BaseTypeSearchLimited, value.BaseTypeEvidenceLimited };
object WordValue(WordAnalysis value) => new WordProjection(value.Word, value.NormalizedWord, value.IsKnown,
    value.GraphSearchIncomplete, value.GraphCandidatesTruncated, value.CandidateSearchLimited,
    value.CandidateEvidenceLimited, value.KnownBaseSearchLimited, value.BaseTypeSearchLimited, value.BaseTypeEvidenceLimited,
    value.Readings.Select(ReadingValue).ToArray(), value.BaseEvidence,
    value.GraphCandidates.Select(PathValue).ToArray(), value.Candidates.Select(CandidateValue).ToArray(),
    value.KnownBaseCandidates.Select(CandidateValue).ToArray(), value.BaseTypeCandidates.Select(CandidateValue).ToArray(), value.GraphStress);
string WordSummary(WordAnalysis value) => $"{value.Word}: known={value.IsKnown}; " +
    JsonSerializer.Serialize(new { value.NormalizedWord, Readings = Preview(value.Readings, ReadingValue), value.BaseEvidence,
        GraphCandidates = Preview(value.GraphCandidates, PathSummary), Candidates = Preview(value.Candidates, CandidateSummary),
        KnownBaseCandidates = Preview(value.KnownBaseCandidates, CandidateSummary),
        BaseTypeCandidates = Preview(value.BaseTypeCandidates, CandidateSummary), flags = Flags(value) }, options);

sealed record MemorySnapshot(long WorkingSetBytes, long PrivateBytes, long PeakWorkingSetBytes, long ManagedBytes, long AllocatedBytes);

// Keep all existing JSON fields while omitting absent graph evidence, just like the API contract.
sealed record WordProjection(string Word, string NormalizedWord, bool IsKnown,
    bool GraphSearchIncomplete, bool GraphCandidatesTruncated, bool CandidateSearchLimited,
    bool CandidateEvidenceLimited, bool KnownBaseSearchLimited, bool BaseTypeSearchLimited, bool BaseTypeEvidenceLimited,
    object[] Readings, object? BaseEvidence, object[] GraphCandidates, object[] Candidates,
    object[] KnownBaseCandidates, object[] BaseTypeCandidates,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GraphStressEvidence? GraphStress);
