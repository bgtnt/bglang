using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Core;

namespace BgLang.SourceBuilder;

/// <summary>Source-bound replacement of selected explicit lexemes through the shared builder.</summary>
internal static class StoredLemmaMigration
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static NeutralSourceBuilder.Result RetainLemmas(string manifestPath, string selectionPath, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        manifestPath = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(manifestPath)!;
        string predecessorPrefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (target.StartsWith(predecessorPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Output must be outside the immutable predecessor package.");
        using FileStream selectionLock = OpenRegular(selectionPath);
        Selection selection = StrictJson.ReadText<Selection>(Text(selectionLock), "lemma selection", root =>
        {
            StrictJson.RequireObject(root, "lemma selection", ["schemaVersion", "manifestSha256", "lexiconSourceId", "lexiconSha256", "targets"]);
            foreach (string field in new[] { "schemaVersion", "manifestSha256", "lexiconSourceId", "lexiconSha256" })
                StrictJson.RequireString(root, field, "lemma selection");
            foreach (JsonElement item in StrictJson.RequireArray(root, "targets", "lemma selection"))
            {
                StrictJson.RequireObject(item, "lemma target", ["lexemeKey", "lemma", "readingCount", "rowSha256"]);
                foreach (string field in new[] { "lexemeKey", "lemma", "rowSha256" }) StrictJson.RequireString(item, field, "lemma target");
                StrictJson.RequireProperty(item, "readingCount", JsonValueKind.Number, "lemma target");
            }
        });
        if (selection.SchemaVersion != "bglang-retain-lemmas.v1" || selection.Targets.Length == 0 ||
            selection.Targets.Any(item => item.ReadingCount <= 0))
            throw new InvalidDataException("Selection requires nonempty source-bound lemma targets.");
        Dictionary<string, Target> targets = new(StringComparer.Ordinal);
        foreach (Target item in selection.Targets)
            if (!targets.TryAdd(item.LexemeKey, item)) throw new InvalidDataException("Selection contains duplicate lexeme keys.");

        if (Path.GetFileName(manifestPath) != "manifest.json") throw new InvalidDataException("Expected manifest.json.");
        using FileStream manifestLock = OpenRegular(manifestPath);
        if (Hash(manifestLock) != selection.ManifestSha256) throw new InvalidDataException("Predecessor manifest identity differs from selection.");
        Manifest manifest = ReadManifest(manifestLock);
        if (manifest.SchemaVersion != NeutralDictionaryPackageLoader.ManifestSchemaVersion || manifest.CreatedAtUtc == default ||
            manifest.Lexicon.Path is not (NeutralDictionaryPackageLoader.LexiconFileName or NeutralDictionaryPackageLoader.RoutedLexiconFileName) ||
            manifest.WordFormation.Path != NeutralDictionaryPackageLoader.WordFormationFileName ||
            manifest.Lexicon.Sha256 != selection.LexiconSha256 || manifest.Lexicon.SourceId != selection.LexiconSourceId ||
            manifest.PackageId != NeutralDictionaryPackageLoader.CreatePackageId(manifest.Lexicon, manifest.WordFormation))
            throw new InvalidDataException("Selection requires the exact unannotated explicit v1/v2 predecessor package.");
        string[] files = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        if (!files.SequenceEqual(new[] { "manifest.json", manifest.Lexicon.Path, manifest.WordFormation.Path }.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Predecessor package must contain exactly its three declared files.");
        string lexiconPath = Path.Combine(directory, manifest.Lexicon.Path);
        using FileStream lexiconLock = OpenRegular(lexiconPath);
        using FileStream graphLock = OpenRegular(Path.Combine(directory, manifest.WordFormation.Path));
        ValidateComponent(lexiconLock, manifest.Lexicon);
        ValidateComponent(graphLock, manifest.WordFormation);
        if (NeutralDictionaryPackageLoader.ComputeSourceId(lexiconPath) != manifest.Lexicon.SourceId)
            throw new InvalidDataException("Predecessor lexical source identity is invalid.");
        byte[] graphBytes = Bytes(graphLock);
        _ = WordFormationAnalyzer.LoadSource(Utf8.GetString(graphBytes), manifest.WordFormation.SourceId);

        using OwnedStageDirectory authoring = OwnedStageDirectory.Create(Path.GetDirectoryName(target)!);
        authoring.VerifyOwnership();
        string sourcePath = Path.Combine(authoring.Path, "retained-lemmas.source.jsonl");
        HashSet<string> seen = new(StringComparer.Ordinal);
        lexiconLock.Position = 0;
        using (StreamReader reader = new(lexiconLock, Utf8, false, 64 * 1024, leaveOpen: true))
        using (StreamWriter writer = new(new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None), Utf8))
        {
            writer.NewLine = "\n";
            while (reader.ReadLine() is { } line)
            {
                // ComputeSourceId above is the strict authority for every original row and key.
                StoredRow row = JsonSerializer.Deserialize<StoredRow>(line, NeutralSourceBuilder.JsonOptions)!;
                AuthoringLexeme.Reading[] readings = row.Readings;
                if (targets.TryGetValue(row.LexemeKey, out Target? selected))
                {
                    if (!seen.Add(row.LexemeKey) || row.SchemaVersion != NeutralDictionaryPackageLoader.LexiconSchemaVersion ||
                        row.Route is not null || row.ProductiveRuleId is not null || row.Lemma != selected.Lemma ||
                        readings.Length != selected.ReadingCount ||
                        Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(line + "\n"))).ToLowerInvariant() != selected.RowSha256 ||
                        !readings.Any(reading => reading.Form == row.Lemma && reading.Lemma == row.Lemma))
                        throw new InvalidDataException("Selected lexeme does not match its complete predecessor anchor.");
                    readings = [new(row.Lemma, row.Lemma, null, 1)];
                }
                AuthoringLexeme declaration = new("bglang-neutral-authoring.v1", row.Route is null ? "stored" : "routed",
                    row.Lemma, row.ProductiveRuleId, null, null, readings, row.Route);
                writer.WriteLine(JsonSerializer.Serialize(declaration, NeutralSourceBuilder.JsonOptions));
            }
        }
        if (seen.Count != targets.Count) throw new InvalidDataException("A selected lexeme key is absent from predecessor source.");
        // Identity calculation, strict public Open and atomic publication remain one mechanism.
        return NeutralSourceBuilder.Build(File.ReadAllBytes(sourcePath), graphBytes, target);
    }

    internal static Manifest ReadManifest(FileStream stream) =>
        StrictJson.ReadText<Manifest>(Text(stream), "predecessor manifest", root =>
        {
            StrictJson.RequireObject(root, "predecessor manifest", ["schemaVersion", "packageId", "createdAtUtc", "lexicon", "wordFormation"]);
            foreach (string field in new[] { "schemaVersion", "packageId", "createdAtUtc" }) StrictJson.RequireString(root, field, "predecessor manifest");
            foreach (string field in new[] { "lexicon", "wordFormation" })
            {
                JsonElement component = root.GetProperty(field);
                StrictJson.RequireObject(component, field, ["path", "bytes", "sha256", "sourceId"]);
                foreach (string name in new[] { "path", "sha256", "sourceId" }) StrictJson.RequireString(component, name, field);
                StrictJson.RequireProperty(component, "bytes", JsonValueKind.Number, field);
            }
        });
    internal static FileStream OpenRegular(string path)
    {
        path = Path.GetFullPath(path);
        for (FileSystemInfo? item = new FileInfo(path); item is not null;
             item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Input ancestry contains a reparse point.");
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) throw new InvalidDataException("Expected a regular file.");
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
    }

    internal static byte[] Bytes(FileStream stream)
    {
        stream.Position = 0;
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    private static string Text(FileStream stream) => Utf8.GetString(Bytes(stream));
    internal static string Hash(FileStream stream)
    {
        stream.Position = 0;
        string result = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return result;
    }

    internal static void ValidateComponent(FileStream stream, NeutralDictionaryPackageLoader.ComponentDocument component)
    {
        if (component.Bytes <= 0 || stream.Length != component.Bytes || Hash(stream) != component.Sha256)
            throw new InvalidDataException("Predecessor component size or hash is invalid.");
    }

    private sealed record Selection(string SchemaVersion, string ManifestSha256, string LexiconSourceId, string LexiconSha256, Target[] Targets);
    private sealed record Target(string LexemeKey, string Lemma, int ReadingCount, string RowSha256);
    internal sealed record Manifest(string SchemaVersion, string PackageId, DateTimeOffset CreatedAtUtc,
        NeutralDictionaryPackageLoader.ComponentDocument Lexicon, NeutralDictionaryPackageLoader.ComponentDocument WordFormation);
    internal sealed record StoredRow(string SchemaVersion, string LexemeKey, string Lemma, string? ProductiveRuleId,
        AuthoringLexeme.Reading[] Readings, string? Route);
}
