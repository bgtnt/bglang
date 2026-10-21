using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Api;
using BgLang.Contracts;
using BgLang.Core;

namespace BgLang.SourceBuilder;

internal static class NeutralSourceBuilder
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static Result Build(string sourcePath, string graphPath, string outputDirectory) =>
        Build(File.ReadAllBytes(sourcePath), File.ReadAllBytes(graphPath), outputDirectory);

    internal static string PrepareTarget(string outputDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("SourceBuilder currently requires Windows for atomic publication without replacement.");
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Output requires an existing parent.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Output parent must already exist.");
        try
        {
            File.GetAttributes(target);
            throw new IOException("Output target already exists; no content is overwritten.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        return target;
    }

    internal static Result Build(byte[] sourceBytes, byte[] graphBytes, string outputDirectory)
    {
        string target = PrepareTarget(outputDirectory);
        string graphText = StrictUtf8.GetString(graphBytes);
        using JsonDocument graphDocument = JsonDocument.Parse(graphText);
        string graphSource = StrictJson.RequireString(graphDocument.RootElement, "sourceId", "reviewed graph");
        WordFormationAnalyzer graph = WordFormationAnalyzer.LoadSource(graphText, graphSource);
        string sourceText = StrictUtf8.GetString(sourceBytes);
        if (sourceText.StartsWith('\uFEFF')) sourceText = sourceText[1..];
        sourceText = sourceText.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (sourceText.Contains('\r')) throw new InvalidDataException("Authoring rows require LF or CRLF endings.");
        if (sourceText.Length == 0) throw new InvalidDataException("Authoring source must contain at least one lexeme.");
        Utf8JsonReader probe = new(StrictUtf8.GetBytes(sourceText));
        using JsonDocument first = JsonDocument.ParseValue(ref probe);
        bool annotated = first.RootElement.ValueKind == JsonValueKind.Object &&
            first.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) &&
            schema.ValueKind == JsonValueKind.String && schema.GetString() is AuthoringDocument.Schema or AuthoringDocument.SourceEvidenceSchema;
        List<(string Key, string Line, int Readings)> rows = [];
        LexemeAnnotations? annotations = null;
        if (annotated)
        {
            AuthoringDocument document = AuthoringDocument.Read(sourceText);
            List<LexemeAnnotations.Entry> owners = [];
            foreach (AuthoringDocument.Entry entry in document.Entries)
            {
                var row = Materialize(entry.Lexeme, graph, owners.Count + 1);
                rows.Add(row);
                owners.Add(new(entry.EntryId, row.Key, entry.Meaning, entry.Stress, entry.Related));
            }
            rows = rows.GroupBy(row => row.Key, StringComparer.Ordinal).Select(group =>
            {
                var firstRow = group.First();
                if (group.Any(row => row.Line != firstRow.Line))
                    throw new InvalidDataException("Shared owners require identical complete lexical declarations.");
                return firstRow;
            }).ToList();
            annotations = new LexemeAnnotations(document.SchemaVersion == AuthoringDocument.SourceEvidenceSchema
                ? LexemeAnnotations.SourceEvidenceSchema : LexemeAnnotations.Schema, owners.ToArray()).Canonicalize();
            annotations = LexemeAnnotations.Read(StrictUtf8.GetString(annotations.Serialize()));
        }
        else
        {
            string[] lines = sourceText.Split('\n');
            int count = lines.Length - (lines[^1].Length == 0 ? 1 : 0);
            for (int number = 0; number < count; number++)
                rows.Add(Materialize(AuthoringLexeme.Read(lines[number], number + 1), graph, number + 1));
        }
        return Publish(rows, graphBytes, graphSource, target, annotations);
    }

    internal static Result Publish(IEnumerable<(string Key, string Line, int Readings)> sourceRows,
        byte[] graphBytes, string graphSource, string outputDirectory, LexemeAnnotations? annotations = null)
    {
        string target = PrepareTarget(outputDirectory);
        using OwnedStageDirectory stage = OwnedStageDirectory.Create(Path.GetDirectoryName(target)!);
        bool annotated = annotations is not null;
        var rows = sourceRows.OrderBy(row => row.Key, StringComparer.Ordinal).ToArray();
        string lexiconName = NeutralDictionaryPackageLoader.GeneratedLexiconFileName;
        stage.VerifyOwnership();
        using (StreamWriter writer = new(new FileStream(Path.Combine(stage.Path, lexiconName),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None), StrictUtf8))
        {
            writer.NewLine = "\n";
            foreach (var row in rows) writer.WriteLine(row.Line);
        }
        WriteNew(stage, NeutralDictionaryPackageLoader.WordFormationFileName, graphBytes);
        if (annotations is not null) WriteNew(stage, LexemeAnnotations.FileName, annotations.Serialize());
        string sourceId = NeutralDictionaryPackageLoader.ComputeSourceId(Path.Combine(stage.Path, lexiconName), allowEmpty: annotated);
        var lexicon = Component(stage.Path, lexiconName, sourceId);
        var wordFormation = Component(stage.Path, NeutralDictionaryPackageLoader.WordFormationFileName, graphSource);
        var annotationComponent = annotations is null ? null : Component(stage.Path, LexemeAnnotations.FileName, annotations.SourceId);
        string packageId = NeutralDictionaryPackageLoader.CreatePackageId(lexicon, wordFormation, annotationComponent);
        WriteNew(stage, "manifest.json", [.. JsonSerializer.SerializeToUtf8Bytes(new ManifestRow(
            annotated ? NeutralDictionaryPackageLoader.AnnotatedManifestSchemaVersion : NeutralDictionaryPackageLoader.ManifestSchemaVersion,
            packageId, DateTimeOffset.UnixEpoch, lexicon, wordFormation, annotationComponent), JsonOptions), (byte)'\n']);
        using (BulgarianLanguageSession.Open(Path.Combine(stage.Path, "manifest.json"))) { }
        stage.Publish(target);
        return new(Path.Combine(target, "manifest.json"), sourceId, packageId, rows.Length, rows.Sum(row => (long)row.Readings), annotations?.Entries.Length);
    }

    internal static (string Key, string Line, int Readings) Materialize(AuthoringLexeme row, WordFormationAnalyzer graph, int number)
    {
        if (row.Kind == "productive")
        {
            WordReading[] readings = graph.GenerateLexicalReadings(row.Lemma, row.BaseFullTag!, row.ProductiveRuleId!, row.Score!.Value);
            GeneratedLexemeSource generated = new(GeneratedLexemeSource.Schema, string.Empty, row.Lemma,
                row.ProductiveRuleId!, row.BaseFullTag!, row.Score.Value, readings.Length, GeneratedLexemeSource.ReadingDigest(readings));
            generated = generated with { LexemeKey = generated.CalculateKey() };
            string line = JsonSerializer.Serialize(generated, JsonOptions);
            GeneratedLexemeSource.Read(line, number);
            return (generated.LexemeKey, line, readings.Length);
        }
        AuthoringLexeme.Reading[] sourceReadings = row.Readings!;
        WordReading[] stored = sourceReadings.Select(reading => reading.ToWordReading()).ToArray();
        string key = row.Kind == "routed"
            ? NeutralDictionaryPackageLoader.CreateRoutedLexemeKey(row.Lemma, row.ProductiveRuleId, row.Route!, stored)
            : LexicalIdentity.CreateLexemeKey(row.Lemma, row.ProductiveRuleId, stored);
        var output = new StoredRow(row.Kind == "routed" ? NeutralDictionaryPackageLoader.RoutedLexiconSchemaVersion :
            NeutralDictionaryPackageLoader.LexiconSchemaVersion, key, row.Lemma, row.ProductiveRuleId,
            sourceReadings.OrderBy(reading => LexicalIdentity.CreateReadingKey(key, reading.ToWordReading()), StringComparer.Ordinal).ToArray(), row.Route);
        return (key, JsonSerializer.Serialize(output, JsonOptions), stored.Length);
    }

    private static NeutralDictionaryPackageLoader.ComponentDocument Component(string directory, string name, string source)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(directory, name));
        return new(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), source);
    }

    internal static void WriteNew(OwnedStageDirectory stage, string name, byte[] bytes)
    {
        stage.VerifyOwnership();
        using FileStream stream = new(Path.Combine(stage.Path, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    internal sealed record Result(string ManifestPath, string SourceId, string PackageId, int Lexemes, long Readings,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Entries = null);
    private sealed record ManifestRow(string SchemaVersion, string PackageId, DateTimeOffset CreatedAtUtc,
        NeutralDictionaryPackageLoader.ComponentDocument Lexicon, NeutralDictionaryPackageLoader.ComponentDocument WordFormation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NeutralDictionaryPackageLoader.ComponentDocument? Annotations);
    private sealed record StoredRow(string SchemaVersion, string LexemeKey, string Lemma, string? ProductiveRuleId,
        AuthoringLexeme.Reading[] Readings, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Route);
}
