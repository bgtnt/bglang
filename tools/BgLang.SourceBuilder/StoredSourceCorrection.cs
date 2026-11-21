using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Core;
using static BgLang.SourceBuilder.StoredLemmaMigration;

namespace BgLang.SourceBuilder;

/// <summary>Exact owner preimages and complete source corrections; publication and identity stay in the shared builder.</summary>
internal static class StoredSourceCorrection
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static Result Correct(string manifestPath, string selectionPath, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        manifestPath = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(manifestPath)!;
        if (Path.GetFileName(manifestPath) != "manifest.json" ||
            target.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Correction requires manifest.json and output outside its immutable package.");
        using FileStream selectionLock = OpenRegular(selectionPath);
        Selection selection = StrictJson.ReadText<Selection>(Utf8.GetString(Bytes(selectionLock)), "stored correction", root =>
        {
            StrictJson.RequireObject(root, "stored correction", ["schemaVersion", "manifestSha256", "lexiconSourceId", "lexiconSha256", "targets"]);
            foreach (string name in new[] { "schemaVersion", "manifestSha256", "lexiconSourceId", "lexiconSha256" })
                StrictJson.RequireString(root, name, "stored correction");
            foreach (JsonElement item in StrictJson.RequireArray(root, "targets", "stored correction"))
            {
                StrictJson.RequireObject(item, "correction target", ["lexemeKey", "rowSha256", "readingCount", "replacement"]);
                StrictJson.RequireString(item, "lexemeKey", "correction target");
                StrictJson.RequireString(item, "rowSha256", "correction target");
                StrictJson.RequireProperty(item, "readingCount", JsonValueKind.Number, "correction target");
                _ = AuthoringLexeme.Read(StrictJson.RequireProperty(item, "replacement", JsonValueKind.Object, "correction target").GetRawText(), 1);
            }
        });
        if (selection.SchemaVersion != "bglang-correct-stored.v1" || selection.Targets.Length == 0 ||
            selection.Targets.Any(item => item.ReadingCount <= 0 || item.Replacement.Kind != "stored"))
            throw new InvalidDataException("Correction requires nonempty source-bound complete stored replacements.");
        Dictionary<string, Target> targets = new(StringComparer.Ordinal);
        foreach (Target item in selection.Targets)
            if (!targets.TryAdd(item.LexemeKey, item)) throw new InvalidDataException("Correction repeats a source owner.");

        using FileStream manifestLock = OpenRegular(manifestPath);
        if (Hash(manifestLock) != selection.ManifestSha256) throw new InvalidDataException("Correction manifest preimage differs.");
        Manifest manifest = ReadManifest(manifestLock);
        if (manifest.SchemaVersion != NeutralDictionaryPackageLoader.ManifestSchemaVersion || manifest.CreatedAtUtc == default ||
            manifest.Lexicon.Path is not (NeutralDictionaryPackageLoader.LexiconFileName or NeutralDictionaryPackageLoader.RoutedLexiconFileName or NeutralDictionaryPackageLoader.GeneratedLexiconFileName) ||
            manifest.WordFormation.Path != NeutralDictionaryPackageLoader.WordFormationFileName ||
            manifest.Lexicon.Sha256 != selection.LexiconSha256 || manifest.Lexicon.SourceId != selection.LexiconSourceId ||
            manifest.PackageId != NeutralDictionaryPackageLoader.CreatePackageId(manifest.Lexicon, manifest.WordFormation))
            throw new InvalidDataException("Correction requires the exact unannotated complete explicit predecessor.");
        if (!Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "manifest.json", manifest.Lexicon.Path, manifest.WordFormation.Path }.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Correction predecessor must contain exactly its three declared files.");
        string lexiconPath = Path.Combine(directory, manifest.Lexicon.Path);
        using FileStream lexiconLock = OpenRegular(lexiconPath);
        using FileStream graphLock = OpenRegular(Path.Combine(directory, manifest.WordFormation.Path));
        ValidateComponent(lexiconLock, manifest.Lexicon);
        ValidateComponent(graphLock, manifest.WordFormation);
        // The existing strict loader verifies every old key, canonical order and LF text envelope.
        if (NeutralDictionaryPackageLoader.ComputeSourceId(lexiconPath) != manifest.Lexicon.SourceId)
            throw new InvalidDataException("Correction lexical source identity differs.");
        byte[] graphBytes = Bytes(graphLock);
        WordFormationAnalyzer graph = WordFormationAnalyzer.LoadSource(Utf8.GetString(graphBytes), manifest.WordFormation.SourceId);
        HashSet<string> seen = new(StringComparer.Ordinal);
        long beforeReadings = 0, afterReadings = 0;

        IEnumerable<(string Key, string Line, int Readings)> CorrectRows()
        {
            lexiconLock.Position = 0;
            using StreamReader reader = new(lexiconLock, Utf8, false, 64 * 1024, leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                StoredRow row = JsonSerializer.Deserialize<StoredRow>(line, NeutralSourceBuilder.JsonOptions)!;
                if (row.SchemaVersion is not (NeutralDictionaryPackageLoader.LexiconSchemaVersion or NeutralDictionaryPackageLoader.RoutedLexiconSchemaVersion))
                    throw new InvalidDataException("Correction input must contain explicit stored or routed rows only.");
                if (!targets.TryGetValue(row.LexemeKey, out Target? selected))
                {
                    yield return (row.LexemeKey, line, row.Readings.Length);
                    continue;
                }
                AuthoringLexeme replacement = selected.Replacement;
                if (!seen.Add(row.LexemeKey) || row.SchemaVersion != NeutralDictionaryPackageLoader.LexiconSchemaVersion || row.Route is not null ||
                    row.Readings.Length != selected.ReadingCount || replacement.Lemma != row.Lemma || replacement.ProductiveRuleId != row.ProductiveRuleId ||
                    Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(line + "\n"))) != selected.RowSha256)
                    throw new InvalidDataException("Correction target differs from its complete owner preimage.");
                var corrected = NeutralSourceBuilder.Materialize(replacement, graph, seen.Count);
                if (corrected.Key == row.LexemeKey) throw new InvalidDataException("Correction must change the complete reading identity.");
                beforeReadings += row.Readings.Length;
                afterReadings += corrected.Readings;
                yield return corrected;
            }
            if (seen.Count != targets.Count) throw new InvalidDataException("A correction owner is absent from the predecessor.");
        }

        NeutralSourceBuilder.Result package = NeutralSourceBuilder.Publish(CorrectRows(), graphBytes, manifest.WordFormation.SourceId, target);
        return new(package, seen.Count, beforeReadings, afterReadings);
    }

    private sealed record Selection(string SchemaVersion, string ManifestSha256, string LexiconSourceId, string LexiconSha256, Target[] Targets);
    private sealed record Target(string LexemeKey, string RowSha256, int ReadingCount, AuthoringLexeme Replacement);
    internal sealed record Result(NeutralSourceBuilder.Result Package, int CorrectedOwners, long BeforeReadings, long AfterReadings);
}
