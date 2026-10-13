using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using BgLang.Core;
using static BgLang.SourceBuilder.StoredLemmaMigration;

namespace BgLang.SourceBuilder;

/// <summary>Replace complete explicit paradigms only when native replay preserves every raw reading.</summary>
internal static class NativeSourcePromotion
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static Result Promote(string manifestPath, string graphPath, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        manifestPath = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(manifestPath)!;
        if (Path.GetFileName(manifestPath) != "manifest.json" ||
            target.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Promotion requires manifest.json and an output outside its immutable package.");
        using FileStream manifestLock = OpenRegular(manifestPath);
        Manifest manifest = ReadManifest(manifestLock);
        if (manifest.SchemaVersion != NeutralDictionaryPackageLoader.ManifestSchemaVersion || manifest.CreatedAtUtc == default ||
            manifest.Lexicon.Path is not (NeutralDictionaryPackageLoader.LexiconFileName or NeutralDictionaryPackageLoader.RoutedLexiconFileName or NeutralDictionaryPackageLoader.GeneratedLexiconFileName) ||
            manifest.WordFormation.Path != NeutralDictionaryPackageLoader.WordFormationFileName ||
            manifest.PackageId != NeutralDictionaryPackageLoader.CreatePackageId(manifest.Lexicon, manifest.WordFormation))
            throw new InvalidDataException("Promotion requires an unannotated explicit predecessor package.");
        if (!Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "manifest.json", manifest.Lexicon.Path, manifest.WordFormation.Path }.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Predecessor package must contain exactly its three declared files.");
        string lexiconPath = Path.Combine(directory, manifest.Lexicon.Path);
        using FileStream lexiconLock = OpenRegular(lexiconPath);
        using FileStream previousGraphLock = OpenRegular(Path.Combine(directory, manifest.WordFormation.Path));
        using FileStream graphLock = OpenRegular(graphPath);
        ValidateComponent(lexiconLock, manifest.Lexicon);
        ValidateComponent(previousGraphLock, manifest.WordFormation);
        if (NeutralDictionaryPackageLoader.ComputeSourceId(lexiconPath) != manifest.Lexicon.SourceId)
            throw new InvalidDataException("Predecessor lexical source identity is invalid.");
        _ = WordFormationAnalyzer.LoadSource(Utf8.GetString(Bytes(previousGraphLock)), manifest.WordFormation.SourceId);
        byte[] graphBytes = Bytes(graphLock);
        using JsonDocument graphDocument = JsonDocument.Parse(graphBytes);
        string graphSource = StrictJson.RequireString(graphDocument.RootElement, "sourceId", "current graph");
        WordFormationAnalyzer graph = WordFormationAnalyzer.LoadSource(Utf8.GetString(graphBytes), graphSource);
        var bindings = graphDocument.RootElement.GetProperty("paradigmEdges").EnumerateArray()
            .Concat(graphDocument.RootElement.TryGetProperty("lexicalCliticProjections", out JsonElement clitics)
                ? clitics.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Select(row => (Tag: row.GetProperty("baseFullTag").GetString()!, Rule: row.GetProperty("ruleId").GetString()!))
            .Distinct().GroupBy(binding => binding.Tag, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(binding => binding.Rule, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        HashSet<string> displayGroups = FindDisplayCollisions(lexiconLock);
        SortedDictionary<string, int> retained = new(StringComparer.Ordinal);
        int promoted = 0, equivalentAlternatives = 0;
        long generatedReadingCount = 0, storedReadingCount = 0;

        IEnumerable<(string Key, string Line, int Readings)> ConvertRows()
        {
            foreach ((StoredRow row, string line) in ReadRows(lexiconLock))
            {
                string reason = "no_binding";
                GeneratedLexemeSource? selected = null;
                int matches = 0;
                if (row.Route is not null) reason = "routed";
                else if (displayGroups.Contains(BulgarianTextNormalizer.NormalizeKey(row.Lemma))) reason = "raw_display_group";
                else if (row.Readings.Select(reading => BitConverter.DoubleToInt64Bits(reading.Score)).Distinct().Skip(1).Any())
                    reason = "nonuniform_score";
                else
                {
                    WordReading[] expected = row.Readings.Select(reading => reading.ToWordReading()).ToArray();
                    var candidates = row.Readings.Where(reading => reading.Form == row.Lemma && reading.Lemma == row.Lemma && reading.FullTag is not null)
                        .SelectMany(reading => bindings.GetValueOrDefault(reading.FullTag!) ?? [])
                        .Where(binding => row.ProductiveRuleId is null || binding.Rule == row.ProductiveRuleId)
                        .Distinct().OrderBy(binding => binding.Rule, StringComparer.Ordinal).ThenBy(binding => binding.Tag, StringComparer.Ordinal);
                    foreach (var binding in candidates)
                    {
                        try
                        {
                            WordReading[] actual = graph.GenerateLexicalReadings(row.Lemma, binding.Tag, binding.Rule, expected[0].Score);
                            if (!ReadingKeys(expected).SequenceEqual(ReadingKeys(actual))) { reason = "reading_mismatch"; continue; }
                            GeneratedLexemeSource seed = new(GeneratedLexemeSource.Schema, string.Empty, row.Lemma,
                                binding.Rule, binding.Tag, expected[0].Score, actual.Length, GeneratedLexemeSource.ReadingDigest(actual));
                            seed = seed with { LexemeKey = seed.CalculateKey() };
                            try { GeneratedLexemeSource.Read(JsonSerializer.Serialize(seed, NeutralSourceBuilder.JsonOptions), 1); }
                            catch (InvalidDataException) { reason = "native_metadata"; continue; }
                            selected ??= seed;
                            matches++;
                        }
                        catch (InvalidDataException) { if (reason == "no_binding") reason = "generation_refused"; }
                    }
                }
                if (selected is not null)
                {
                    promoted++;
                    generatedReadingCount += row.Readings.Length;
                    equivalentAlternatives += matches - 1;
                    yield return (selected.LexemeKey, JsonSerializer.Serialize(selected, NeutralSourceBuilder.JsonOptions), row.Readings.Length);
                }
                else
                {
                    retained[reason] = retained.GetValueOrDefault(reason) + 1;
                    storedReadingCount += row.Readings.Length;
                    yield return (row.LexemeKey, line, row.Readings.Length);
                }
            }
        }

        NeutralSourceBuilder.Result package = NeutralSourceBuilder.Publish(ConvertRows(), graphBytes, graphSource, target);
        return new(package.ManifestPath, package.SourceId, package.PackageId, package.Lexemes, package.Readings,
            promoted, retained.Values.Sum(), generatedReadingCount, storedReadingCount, retained, equivalentAlternatives, displayGroups.Count,
            new(Hash(manifestLock), manifest.PackageId, manifest.Lexicon,
                manifest.WordFormation, new(Path.GetFullPath(graphPath), graphBytes.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(graphBytes)), graphSource)));
    }

    private static IEnumerable<(StoredRow Row, string Line)> ReadRows(FileStream stream)
    {
        stream.Position = 0;
        using StreamReader reader = new(stream, Utf8, false, 64 * 1024, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            StoredRow row = JsonSerializer.Deserialize<StoredRow>(line, NeutralSourceBuilder.JsonOptions)!;
            if (row.SchemaVersion is not (NeutralDictionaryPackageLoader.LexiconSchemaVersion or NeutralDictionaryPackageLoader.RoutedLexiconSchemaVersion))
                throw new InvalidDataException("Promotion input must contain explicit stored or routed rows only.");
            yield return (row, line);
        }
    }

    // FormEquality discards score and display case before GenerateForms sorting. Rekeying must not
    // reorder competing raw representatives from different lexemes in the same normalized lemma group.
    private static HashSet<string> FindDisplayCollisions(FileStream stream)
    {
        Dictionary<(string Form, string Lemma, string? Tag), (string Key, string Form, string Lemma)> variants = [];
        HashSet<string> groups = new(StringComparer.Ordinal);
        foreach ((StoredRow row, _) in ReadRows(stream))
        {
            if (row.Route is not null) continue;
            foreach (var reading in row.Readings)
            {
                string form = BulgarianTextNormalizer.NormalizeKey(reading.Form), lemma = BulgarianTextNormalizer.NormalizeKey(reading.Lemma);
                if (reading.Form != form || reading.Lemma != lemma)
                    variants.TryAdd((form, lemma, reading.FullTag), (row.LexemeKey, reading.Form, reading.Lemma));
            }
        }
        if (variants.Count == 0) return groups;
        foreach ((StoredRow row, _) in ReadRows(stream))
        {
            if (row.Route is not null) continue;
            foreach (var reading in row.Readings)
                if (variants.TryGetValue((BulgarianTextNormalizer.NormalizeKey(reading.Form), BulgarianTextNormalizer.NormalizeKey(reading.Lemma), reading.FullTag), out var first) &&
                    row.LexemeKey != first.Key && (reading.Form != first.Form || reading.Lemma != first.Lemma))
                    groups.Add(BulgarianTextNormalizer.NormalizeKey(row.Lemma));
        }
        return groups;
    }

    private static IEnumerable<(string Form, string Lemma, string? Tag, long Score)> ReadingKeys(IEnumerable<WordReading> readings) =>
        readings.Select(reading => (reading.Form, reading.Lemma, Tag: reading.PartOfSpeech, Score: BitConverter.DoubleToInt64Bits(reading.Score)))
            .OrderBy(reading => reading.Form, StringComparer.Ordinal).ThenBy(reading => reading.Lemma, StringComparer.Ordinal)
            .ThenBy(reading => reading.Tag, StringComparer.Ordinal).ThenBy(reading => reading.Score);

    internal sealed record Result(string ManifestPath, string SourceId, string PackageId, int Lexemes, long Readings,
        int Promoted, int Retained, long GeneratedReadingCount, long StoredReadingCount,
        SortedDictionary<string, int> RetainedByReason, int EquivalentAlternativeBindings,
        int RawDisplayGroups, InputIdentity Input);
    internal sealed record InputIdentity(string ManifestSha256, string PackageId,
        NeutralDictionaryPackageLoader.ComponentDocument Lexicon, NeutralDictionaryPackageLoader.ComponentDocument PreviousGraph,
        NeutralDictionaryPackageLoader.ComponentDocument CurrentGraph);
}
