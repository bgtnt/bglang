using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Contracts;
using BgLang.Core;

namespace BgLang.SourceBuilder;

/// <summary>Imports captured Word observables through exact current owner and form/tag membership.</summary>
internal static class LegacyStressImporter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(NeutralSourceBuilder.JsonOptions)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static object Import(string sourcePath, string graphPath, string mapPath, string observationsPath,
        string sourceId, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        if (sourceId.Length != 81 || !sourceId.StartsWith("legacy-stress-v1:", StringComparison.Ordinal) ||
            sourceId[17..].Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException("Captured Legacy source identity is required.");
        using FileStream sourceFile = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream graphFile = File.Open(graphPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream mapFile = File.Open(mapPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream observationsFile = File.Open(observationsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var inputs = new[] { sourceFile, graphFile, mapFile, observationsFile }.Select(Pin).ToArray();
        AuthoringDocument document = AuthoringDocument.Read(ReadText(sourceFile));
        string graphText = ReadText(graphFile);
        using JsonDocument graphJson = JsonDocument.Parse(graphText);
        WordFormationAnalyzer graph = WordFormationAnalyzer.LoadSource(graphText, graphJson.RootElement.GetProperty("sourceId").GetString()!);
        Dictionary<string, AuthoringDocument.Entry> entries = document.Entries.ToDictionary(entry => entry.EntryId, StringComparer.Ordinal);
        Dictionary<string, Inventory> inventories = new(StringComparer.Ordinal);
        Dictionary<uint, OwnerMap> owners = [];
        int additiveEntries = 0;
        foreach (string line in Lines(mapFile))
        {
            using JsonDocument mapJson = JsonDocument.Parse(line);
            OwnerMap? map = null; string entryId, currentKey, currentDigest; int currentCount;
            if (mapJson.RootElement.TryGetProperty("sourceKind", out _))
            {
                AdditiveMap additive = JsonSerializer.Deserialize<AdditiveMap>(line, Json)!;
                string prefix = additive.SourceKind switch { "accepted-surface" => "accepted-surface", "overlay" => "legacy-overlay", _ => "" };
                if (prefix.Length == 0 || additive.SourceSha256.Length != 64 || additive.SourceSha256.Any(c => !char.IsAsciiHexDigitLower(c)) ||
                    additive.SourceRow < 1 || additive.EntryId != $"{prefix}:{additive.SourceSha256}:{additive.SourceRow}")
                    throw new InvalidDataException("Additive map identity is invalid.");
                entryId = additive.EntryId; currentKey = additive.CurrentKey; currentDigest = additive.CurrentDigest;
                currentCount = additive.CurrentReadingCount; additiveEntries++;
            }
            else
            {
                map = JsonSerializer.Deserialize<OwnerMap>(line, Json)!;
                if (map.EntryId != $"legacy-base:{map.OwnerId}") throw new InvalidDataException("Legacy owner identity is invalid.");
                entryId = map.EntryId; currentKey = map.CurrentKey; currentDigest = map.CurrentDigest; currentCount = map.CurrentReadingCount;
            }
            if (!entries.TryGetValue(entryId, out var entry))
                throw new InvalidDataException("Legacy owner map is not bound to this exact source entry.");
            var materialized = NeutralSourceBuilder.Materialize(entry.Lexeme, graph, owners.Count + 1);
            if (materialized.Key != currentKey)
                throw new InvalidDataException("Current declaration does not belong to its mapped owner key.");
            if (!inventories.TryGetValue(currentKey, out Inventory? inventory))
            {
                WordReading[] readings = entry.Lexeme.Kind == "productive"
                    ? graph.GenerateLexicalReadings(entry.Lexeme.Lemma, entry.Lexeme.BaseFullTag!, entry.Lexeme.ProductiveRuleId!, entry.Lexeme.Score!.Value)
                    : entry.Lexeme.Readings!.Select(row => row.ToWordReading()).ToArray();
                // The retained map digest writes a null stored tag as an empty identity field.
                string digest = GeneratedLexemeSource.ReadingDigest(readings.Select(row =>
                    row.PartOfSpeech is null ? row with { PartOfSpeech = string.Empty } : row).ToArray());
                if (readings.Length != currentCount || digest != currentDigest)
                    throw new InvalidDataException("Current owner key or complete reading digest/count differs from the map.");
                inventory = new(readings.Select(row => (row.Form, row.PartOfSpeech)).ToHashSet(), currentDigest, readings.Length);
                inventories.Add(currentKey, inventory);
            }
            else if (currentDigest != inventory.Digest || currentCount != inventory.Count)
                throw new InvalidDataException("Alias owner map has a different current inventory.");
            if (map is not null) owners.Add(map.OwnerId, map);
        }
        Dictionary<string, Dictionary<(string Form, string? Tag), LexemeAnnotations.StressRow>> admitted = new(StringComparer.Ordinal);
        HashSet<(uint Owner, string Form, string? Tag)> conflicting = [];
        Dictionary<(uint Owner, string Form, string? Tag), string> blocked = [];
        Dictionary<string, long> abstentions = new(StringComparer.Ordinal);
        long rows = 0, withOffsets = 0, coherentDuplicates = 0, baseRows = 0, carriedRows = 0, displayRows = 0, multiMarkRows = 0;
        using OwnedStageDirectory stage = OwnedStageDirectory.Create(Path.GetDirectoryName(target)!);
        stage.VerifyOwnership();
        using (StreamWriter quarantine = new(new FileStream(Path.Combine(stage.Path, "stress-quarantine.v1.jsonl"), FileMode.CreateNew), Utf8))
        {
            quarantine.NewLine = "\n";
            foreach (string line in Lines(observationsFile))
            {
                Observation row = JsonSerializer.Deserialize<Observation>(line, Json)!; rows++;
                string? reason = null;
                if (row.Offsets is { Length: > 0 }) withOffsets++;
                if (row.OffsetError is not null || row.DisplayError is not null) reason = "legacy-observable-error";
                else if (!owners.TryGetValue(row.OwnerId, out OwnerMap? map)) reason = "unbound-owner";
                else if (!inventories[map.CurrentKey].Forms.Contains((row.Form, row.FullTag))) reason = "not-current-exact-form-tag";
                int[] ordinals = [], effectiveOffsets = [];
                bool displayOnly = false;
                if (reason is null)
                {
                    bool exactDisplay = TryReadDisplay(row.Form, row.Displayed, out int[] displayedOffsets);
                    if (row.Offsets is { Length: > 0 }) effectiveOffsets = row.Offsets;
                    else if (!exactDisplay) reason = "invalid-legacy-display";
                    else if (displayedOffsets.Length == 0) reason = "no-captured-stress";
                    else { effectiveOffsets = displayedOffsets; displayOnly = true; }
                    int[] vowels = row.Form.Select((value, offset) => (value, offset))
                        .Where(item => BulgarianOrthography.IsVowel(item.value)).Select(item => item.offset).ToArray();
                    ordinals = effectiveOffsets.Select(offset => Array.IndexOf(vowels, offset)).ToArray();
                    if (reason is null && (ordinals.Any(value => value < 0) || ordinals.Zip(ordinals.Skip(1)).Any(pair => pair.First >= pair.Second)))
                        reason = displayOnly ? "invalid-legacy-display-vowel-offsets" : "invalid-legacy-vowel-offsets";
                    else if (reason is null && !displayOnly && (!exactDisplay || !effectiveOffsets.AsSpan().SequenceEqual(displayedOffsets)))
                        reason = "legacy-display-offset-conflict";
                }
                if (reason is null)
                {
                    string entryId = owners[row.OwnerId].EntryId;
                    var key = (row.Form, row.FullTag); var conflictKey = (row.OwnerId, row.Form, row.FullTag);
                    if (!admitted.TryGetValue(entryId, out var stress)) admitted.Add(entryId, stress = []);
                    if (blocked.TryGetValue(conflictKey, out string? priorReason)) reason = priorReason;
                    else if (conflicting.Contains(conflictKey)) reason = "conflicting-duplicate-tuple";
                    else if (entries[entryId].Stress.Any(existing => (existing.Form, existing.FullTag) == key)) reason = "existing-authored-evidence-preserved";
                    else if (stress.TryGetValue(key, out var previous))
                    {
                        if (previous.VowelOrdinals.AsSpan().SequenceEqual(ordinals) &&
                            previous.Source!.WordId == row.WordId &&
                            previous.Source.Kind == (displayOnly ? "LegacyDisplayInferred" : row.IsBase ? "LegacyStored" : "LegacyCarried") &&
                            EqualOffsets(previous.Source.Utf16Positions, row.Offsets) &&
                            EqualOffsets(previous.Source.DisplayUtf16Positions, displayOnly ? effectiveOffsets : null)) coherentDuplicates++;
                        else { stress.Remove(key); conflicting.Add(conflictKey); reason = "conflicting-duplicate-tuple"; }
                    }
                    else stress.Add(key, new(row.Form, row.FullTag, ordinals)
                    {
                        Source = new(displayOnly ? "LegacyDisplayInferred" : row.IsBase ? "LegacyStored" : "LegacyCarried",
                            sourceId, row.OwnerId, row.WordId, row.Offsets)
                        { DisplayUtf16Positions = displayOnly ? effectiveOffsets : null }
                    });
                }
                if (reason is null) continue;
                if (owners.TryGetValue(row.OwnerId, out OwnerMap? bound) &&
                    inventories[bound.CurrentKey].Forms.Contains((row.Form, row.FullTag)))
                {
                    blocked.TryAdd((row.OwnerId, row.Form, row.FullTag), reason);
                    if (admitted.TryGetValue(bound.EntryId, out var previous)) previous.Remove((row.Form, row.FullTag));
                }
                abstentions[reason] = abstentions.GetValueOrDefault(reason) + 1;
                quarantine.WriteLine(JsonSerializer.Serialize(new { line = rows, row.OwnerId, row.WordId, row.Form, row.FullTag, reason }, Json));
            }
        }
        AuthoringDocument.Entry[] updated = document.Entries.Select(entry =>
        {
            if (!admitted.TryGetValue(entry.EntryId, out var stress)) return entry;
            foreach (var row in stress.Values)
            {
                if (row.Source!.Kind == "LegacyStored") baseRows++;
                else if (row.Source.Kind == "LegacyCarried") carriedRows++;
                else displayRows++;
                if (row.VowelOrdinals.Length > 1) multiMarkRows++;
            }
            return entry with { Stress = entry.Stress.Concat(stress.Values).OrderBy(row => row.Form, StringComparer.Ordinal)
                .ThenBy(row => row.FullTag, StringComparer.Ordinal).ToArray() };
        }).ToArray();
        AuthoringDocument result = new(AuthoringDocument.StressEvidenceSchema, updated);
        NeutralSourceBuilder.WriteNew(stage, AuthoringDocument.FileName, result.Serialize());
        NeutralSourceBuilder.WriteNew(stage, NeutralDictionaryPackageLoader.WordFormationFileName, Utf8.GetBytes(graphText));
        var report = new { schema = "bglang.legacy-stress-import.v1", sourceId, inputs, owners = owners.Count,
            entries = updated.Length, additiveEntries, capturedRows = rows, withOffsets, importedBaseRows = baseRows,
            importedCarriedRows = carriedRows, importedDisplayOnlyRows = displayRows,
            multiMarkRows, coherentDuplicates, conflictingTuples = conflicting.Count, blockedCurrentTuples = blocked.Count, abstentions,
            authority = "Captured Legacy offsets and separately inferred captured display; multi-mark roles unclassified; no independent linguistic correctness claim.",
            coordinates = "Exact current owner/form/fullTag; zero-based vowel ordinals plus captured Legacy UTF16 offsets.",
            actualTemp = Path.GetTempPath() };
        NeutralSourceBuilder.WriteNew(stage, "stress-import.json", JsonSerializer.SerializeToUtf8Bytes(report, Json));
        stage.Publish(target);
        return report;
    }

    private static bool EqualOffsets(int[]? left, int[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static object Pin(FileStream stream)
    {
        stream.Position = 0; string sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)); stream.Position = 0;
        return new { path = stream.Name, bytes = stream.Length, sha256 };
    }
    private static bool TryReadDisplay(string form, string? displayed, out int[] offsets)
    {
        offsets = [];
        if (displayed is null) return false;
        StringBuilder plain = new(); List<int> observed = [];
        foreach (char value in displayed)
            if (value is '\u0300' or '\u0301') observed.Add(plain.Length - 1);
            else plain.Append(value);
        offsets = observed.ToArray();
        return plain.ToString() == form;
    }
    private static string ReadText(FileStream stream)
    { stream.Position = 0; using StreamReader reader = new(stream, Utf8, false, 64 * 1024, leaveOpen: true); return reader.ReadToEnd(); }
    private static IEnumerable<string> Lines(FileStream stream)
    {
        stream.Position = 0; using StreamReader reader = new(stream, Utf8, false, 64 * 1024, leaveOpen: true);
        while (reader.ReadLine() is { } line) yield return line;
    }
    private sealed record Inventory(HashSet<(string Form, string? Tag)> Forms, string Digest, int Count);
    private sealed record OwnerMap(string EntryId, uint OwnerId, uint TypeId, string OriginalKey, string CurrentKey,
        string Binding, string CurrentDigest, int CurrentReadingCount);
    private sealed record AdditiveMap(string EntryId, string CurrentKey, string SourceKind, string SourceSha256,
        int SourceRow, string CurrentDigest, int CurrentReadingCount);
    private sealed record Observation(uint OwnerId, uint WordId, bool IsBase, string Form, string Lemma,
        string? FullTag, int[]? Offsets, string? Displayed, string? OffsetError, string? DisplayError);
}
