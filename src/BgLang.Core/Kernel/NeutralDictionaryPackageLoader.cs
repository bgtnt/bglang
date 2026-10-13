using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Strict runtime loader for a lexeme-grouped, origin-neutral source package.</summary>
internal static class NeutralDictionaryPackageLoader
{
    internal const string ManifestSchemaVersion = "bglang-neutral-runtime-package.v1";
    internal const string AnnotatedManifestSchemaVersion = "bglang-neutral-runtime-package.v2";
    internal const string LexiconSchemaVersion = "bglang-neutral-lexeme.v1";
    internal const string RoutedLexiconSchemaVersion = "bglang-neutral-lexeme.v2";
    internal const string LexiconFileName = "lexicon.v1.jsonl";
    internal const string RoutedLexiconFileName = "lexicon.v2.jsonl";
    internal const string GeneratedLexiconFileName = "lexicon.v3.jsonl";
    internal const string ExactPrefixOnlyRoute = "exact_prefix_only";
    internal const string WordFormationFileName = "word-formation.v0.json";
    private const string SourceIdentitySchema = "bglang.neutral-dictionary-source.v1";
    private const string RoutedSourceIdentitySchema = "bglang.neutral-dictionary-source.v2";
    private const string RoutedLexemeIdentitySchema = "bglang.lexeme.v2";
    private const string PackageIdentitySchema = "bglang.neutral-runtime-package.v1";
    private const int MaxSharedTags = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static (IRuntimeLexicon Lexicon, WordFormationAnalyzer WordFormation, PackageProvenance Provenance,
        IReadOnlyDictionary<string, int[]>? LexicalStress)
        Load(string manifestPath, Action? lockedSnapshotProbe = null, Action<NeutralPackageSnapshot>? capture = null)
    {
        PackageLoadEvents.Log.Phase("read");
        string fullManifestPath = Path.GetFullPath(manifestPath);
        string root = Path.GetDirectoryName(fullManifestPath)!;
        if (!string.Equals(fullManifestPath, Path.Combine(root, "manifest.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Neutral package entry point must be manifest.json.");

        string wordFormationPath = Path.Combine(root, WordFormationFileName);
        using FileStream manifestStream = OpenLockedRegularFile(fullManifestPath, "manifest");
        ManifestDocument manifest = ReadJson<ManifestDocument>(
            manifestStream,
            "neutral package manifest",
            ValidateManifestJson);
        ValidateManifest(manifest);
        bool annotated = manifest.SchemaVersion == AnnotatedManifestSchemaVersion;
        if (capture is not null && !annotated) throw new InvalidDataException("Source export requires an annotated v2 package.");
        string lexiconPath = Path.Combine(root, manifest.Lexicon.Path);
        ValidateExactFileSet(root, manifest.Lexicon.Path, annotated);

        using FileStream lexiconStream = OpenLockedRegularFile(lexiconPath, "neutral lexicon");
        using FileStream wordFormationStream = OpenLockedRegularFile(wordFormationPath, "word formation");
        using FileStream? annotationStream = annotated
            ? OpenLockedRegularFile(Path.Combine(root, LexemeAnnotations.FileName), "annotations") : null;
        lockedSnapshotProbe?.Invoke();
        string manifestSha256 = Sha256Hex(manifestStream);
        string lexiconSha256 = ValidateComponent(lexiconStream, manifest.Lexicon, "neutral lexicon");
        string wordFormationSha256 = ValidateComponent(
            wordFormationStream,
            manifest.WordFormation,
            "word formation");

        string? annotationSha256 = annotationStream is null ? null :
            ValidateComponent(annotationStream, manifest.Annotations!, "annotations");
        bool generatedSource = manifest.Lexicon.Path == GeneratedLexiconFileName;
        IReadOnlyList<LexemeMaterialization>? declarations = generatedSource
            ? LoadDeclarations(lexiconStream, routedSource: true, generatedSource: true, allowEmpty: annotated) : null;
        SourceMaterialization? source = generatedSource ? null : LoadSource(
            lexiconStream,
            string.Equals(manifest.Lexicon.Path, RoutedLexiconFileName, StringComparison.Ordinal));
        string sourceId = source?.SourceId ?? CreateGeneratedSourceId(declarations!);
        if (!string.Equals(sourceId, manifest.Lexicon.SourceId, StringComparison.Ordinal))
            throw new InvalidDataException("Neutral lexicon semantic identity does not match the manifest.");

        PackageLoadEvents.Log.Phase("graph");
        WordFormationAnalyzer graph = WordFormationAnalyzer.LoadSource(
            ReadUtf8(wordFormationStream, "word-formation component"),
            manifest.WordFormation.SourceId);
        PackageLoadEvents.Log.Phase("generate");
        if (declarations is not null)
        {
            declarations = declarations.Select(row => row.Generated is null ? row :
                row with { Readings = row.Generated.Materialize(graph), Generated = null }).ToArray();
        }
        PackageLoadEvents.Log.Phase("materialize");
        if (declarations is not null)
        {
            source = MaterializeSource(declarations, sourceId);
        }
        PackageLoadEvents.Log.Phase("validate");
        IReadOnlyDictionary<string, int[]>? lexicalStress = null;
        if (annotationStream is not null)
        {
            LexemeAnnotations annotations = LexemeAnnotations.Read(ReadUtf8(annotationStream, "annotations"));
            if (annotations.SourceId != manifest.Annotations!.SourceId)
                throw new InvalidDataException("Annotation semantic identity does not match the manifest.");
            lexicalStress = annotations.Bind(declarations!.ToDictionary(row => row.LexemeKey, row => row.Readings, StringComparer.Ordinal));
        }
        ValidateExactFileSet(root, manifest.Lexicon.Path, annotated);
        PackageProvenance provenance = new(
            manifest.PackageId,
            manifest.SchemaVersion,
            manifestSha256,
            lexiconSha256,
            sourceId,
            wordFormationSha256,
            graph.SourceId)
        {
            AnnotationsSha256 = annotationSha256,
            AnnotationsSource = manifest.Annotations?.SourceId
        };
        PackageLoadEvents.Log.Phase("index");
        var lexicon = new InMemoryRuntimeLexicon(source!.Readings,
            new NeutralSourceFormInventory(source.FormsByLemma), source.RoutedReadings, allowEmptyOrdinary: annotated);
        if (capture is not null)
        {
            try { capture(new(ReadBytes(lexiconStream), ReadBytes(wordFormationStream), ReadBytes(annotationStream!))); }
            catch { lexicon.Dispose(); throw; }
        }
        PackageLoadEvents.Log.Phase("complete");
        return (lexicon, graph, provenance, lexicalStress);
    }

    internal static string ComputeSourceId(string path, bool allowEmpty = false)
    {
        string fullPath = Path.GetFullPath(path);
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(name, LexiconFileName, StringComparison.Ordinal) &&
            !string.Equals(name, RoutedLexiconFileName, StringComparison.Ordinal) &&
            !string.Equals(name, GeneratedLexiconFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Neutral lexicon filename is unsupported.");
        if (allowEmpty && name != GeneratedLexiconFileName)
            throw new InvalidDataException("Only lexical v3 supports an explicitly empty source.");
        using FileStream stream = OpenLockedRegularFile(fullPath, "neutral lexicon");
        return name == GeneratedLexiconFileName
            ? CreateGeneratedSourceId(LoadDeclarations(stream, routedSource: true, generatedSource: true, allowEmpty))
            : LoadSource(stream, string.Equals(name, RoutedLexiconFileName, StringComparison.Ordinal)).SourceId;
    }

    internal static string CreatePackageId(
        ComponentDocument lexicon, ComponentDocument wordFormation, ComponentDocument? annotations = null)
    {
        IEnumerable<string> Fields()
        {
            yield return annotations is null ? PackageIdentitySchema : "bglang.neutral-runtime-package.v2";
            foreach (ComponentDocument component in new[] { lexicon, wordFormation, annotations }.OfType<ComponentDocument>())
            {
                yield return component.Path;
                yield return component.Bytes.ToString(CultureInfo.InvariantCulture);
                yield return component.Sha256;
                yield return component.SourceId;
            }
        }
        return "bglang-neutral:" + LexicalIdentity.HashLengthPrefixed(Fields())[..16];
    }

    private static SourceMaterialization LoadSource(FileStream stream, bool routedSource = false)
    {
        IReadOnlyList<LexemeMaterialization> lexemes = LoadDeclarations(stream, routedSource, generatedSource: false);
        return MaterializeSource(lexemes, CreateSourceId(lexemes, routedSource));
    }

    private static IReadOnlyList<LexemeMaterialization> LoadDeclarations(
        FileStream stream, bool routedSource, bool generatedSource, bool allowEmpty = false)
    {
        ValidateTextEnvelope(stream, allowEmpty);
        List<LexemeMaterialization> lexemes = [];
        HashSet<string> sharedTags = new(StringComparer.Ordinal);
        string? previousLexemeKey = null;
        int lineNumber = 0;
        try
        {
            stream.Position = 0;
            using StreamReader reader = new(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024,
                leaveOpen: true);
            while (reader.ReadLine() is { } line)
            {
                lineNumber++;
                if (line.Length == 0)
                    throw new InvalidDataException($"Neutral lexicon line {lineNumber} is empty.");
                LexemeMaterialization lexeme;
                using JsonDocument? envelope = generatedSource ? JsonDocument.Parse(line) : null;
                if (generatedSource && envelope!.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Neutral lexicon declaration must be an object.");
                if (generatedSource && envelope!.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) &&
                    schema.ValueKind == JsonValueKind.String && schema.GetString() == GeneratedLexemeSource.Schema)
                {
                    GeneratedLexemeSource seed = GeneratedLexemeSource.Read(line, lineNumber);
                    lexeme = new(seed.LexemeKey, seed.Lemma, seed.ProductiveRuleId, null, [], seed);
                }
                else
                {
                    LexemeRow row = StrictJson.ReadText<LexemeRow>(line, $"neutral lexicon line {lineNumber}",
                        root => ValidateLexemeJson(root, lineNumber));
                    lexeme = ValidateLexeme(row, lineNumber, routedSource, sharedTags);
                }
                if (previousLexemeKey is not null &&
                    StringComparer.Ordinal.Compare(previousLexemeKey, lexeme.LexemeKey) >= 0)
                {
                    throw new InvalidDataException("Neutral lexicon rows must have unique ordinal lexemeKey order.");
                }

                lexemes.Add(lexeme);
                previousLexemeKey = lexeme.LexemeKey;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Neutral lexicon JSON is invalid.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Neutral lexicon contains invalid UTF-8.", exception);
        }

        if (lexemes.Count == 0 && !allowEmpty)
            throw new InvalidDataException("Neutral lexicon must contain at least one lexeme.");
        return lexemes;
    }

    private static void ValidateReadingSets(IReadOnlyList<LexemeMaterialization> lexemes)
    {
        HashSet<SemanticReading> semanticReadings = [];
        HashSet<string> routedExactForms = new(StringComparer.Ordinal);
        foreach (LexemeMaterialization lexeme in lexemes)
        foreach (WordReading reading in lexeme.Readings)
        {
            if (!semanticReadings.Add(SemanticReading.Create(reading)))
                throw new InvalidDataException("Neutral lexicon repeats one semantic reading across lexemes.");
            if (lexeme.Route is not null)
                routedExactForms.Add(BulgarianTextNormalizer.NormalizeKey(reading.Form));
        }
        if (routedExactForms.Count > 0 && lexemes
                .Where(lexeme => lexeme.Route is null)
                .SelectMany(lexeme => lexeme.Readings)
                .Any(reading => routedExactForms.Contains(BulgarianTextNormalizer.NormalizeKey(reading.Form))))
            throw new InvalidDataException("Routed neutral surface collides with an ordinary exact group.");

    }

    private static SourceMaterialization MaterializeSource(IReadOnlyList<LexemeMaterialization> lexemes, string sourceId)
    {
        ValidateReadingSets(lexemes);
        WordReading[] readings = lexemes
            .Where(lexeme => lexeme.Route is null)
            .SelectMany(lexeme => lexeme.Readings)
            .Select(reading => reading with { Source = sourceId })
            .OrderBy(reading => reading, WordReadingOrder.Comparer)
            .ToArray();
        Dictionary<string, WordForm[]> formsByLemma = lexemes
            .Where(lexeme => lexeme.Route is null)
            .GroupBy(lexeme => BulgarianTextNormalizer.NormalizeKey(lexeme.Lemma), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                        .SelectMany(lexeme => lexeme.Readings)
                        .Select(reading => new WordForm(
                            reading.Form,
                            reading.Lemma,
                            reading.PartOfSpeech,
                            null,
                            sourceId))
                        .Distinct(LexicalIdentity.FormEquality)
                        .OrderBy(form => BulgarianTextNormalizer.NormalizeKey(form.Form), StringComparer.Ordinal)
                        .ThenBy(form => BulgarianTextNormalizer.NormalizeKey(form.Lemma), StringComparer.Ordinal)
                        .ThenBy(form => form.Tag, StringComparer.Ordinal)
                        .ToArray(),
                StringComparer.Ordinal);
        WordReading[] routedReadings = lexemes
            .Where(lexeme => lexeme.Route is not null)
            .SelectMany(lexeme => lexeme.Readings)
            .Select(reading => reading with { Source = sourceId })
            .OrderBy(reading => reading, WordReadingOrder.Comparer)
            .ToArray();
        return new SourceMaterialization(sourceId, readings, routedReadings, formsByLemma);
    }

    private static LexemeMaterialization ValidateLexeme(
        LexemeRow row, int lineNumber, bool routedSource, HashSet<string> sharedTags)
    {
        bool routed = string.Equals(row.SchemaVersion, RoutedLexiconSchemaVersion, StringComparison.Ordinal);
        if ((!routed && !string.Equals(row.SchemaVersion, LexiconSchemaVersion, StringComparison.Ordinal)) ||
            routed && (!routedSource || !string.Equals(row.Route, ExactPrefixOnlyRoute, StringComparison.Ordinal)))
            throw new InvalidDataException($"Neutral lexicon line {lineNumber} has unsupported schema.");
        if (string.IsNullOrWhiteSpace(row.LexemeKey) || !IsRawCanonical(row.Lemma))
            throw new InvalidDataException($"Neutral lexicon line {lineNumber} has an incomplete lexeme identity.");
        if (row.ProductiveRuleId is not null && !IsRawCanonical(row.ProductiveRuleId))
            throw new InvalidDataException($"Neutral lexicon line {lineNumber} has an empty productiveRuleId.");
        if (row.Readings is null || row.Readings.Length == 0)
            throw new InvalidDataException($"Neutral lexicon line {lineNumber} must contain readings.");

        string normalizedLemma = BulgarianTextNormalizer.NormalizeKey(row.Lemma);
        if (normalizedLemma.Length == 0)
            throw new InvalidDataException($"Neutral lexicon line {lineNumber} has an empty normalized lemma.");
        List<(string Key, WordReading Reading)> readings = [];
        foreach (ReadingRow source in row.Readings)
        {
            if (!IsRawCanonical(source.Form) || !IsRawCanonical(source.Lemma) ||
                source.FullTag is not null && !IsRawCanonical(source.FullTag) ||
                BulgarianTextNormalizer.NormalizeKey(source.Form).Length == 0 ||
                !string.Equals(
                    BulgarianTextNormalizer.NormalizeKey(source.Lemma),
                    normalizedLemma,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Neutral lexicon line {lineNumber} has a reading outside its lemma.");
            }
            if (!double.IsFinite(source.Score) || source.Score <= 0)
                throw new InvalidDataException($"Neutral lexicon line {lineNumber} has an invalid score.");

            WordReading reading = new(
                source.Form,
                string.Equals(source.Lemma, row.Lemma, StringComparison.Ordinal) ? row.Lemma : source.Lemma,
                ShareTag(source.FullTag, sharedTags),
                source.Score,
                null,
                null,
                null,
                string.Empty);
            readings.Add((string.Empty, reading));
        }

        string expectedLexemeKey = routed
            ? CreateRoutedLexemeKey(row.Lemma, row.ProductiveRuleId, row.Route!, readings.Select(item => item.Reading))
            : LexicalIdentity.CreateLexemeKey(row.Lemma, row.ProductiveRuleId, readings.Select(item => item.Reading));
        if (!string.Equals(expectedLexemeKey, row.LexemeKey, StringComparison.Ordinal))
            throw new InvalidDataException($"Neutral lexicon line {lineNumber} lexemeKey does not match its readings.");

        for (int index = 0; index < readings.Count; index++)
            readings[index] = (LexicalIdentity.CreateReadingKey(row.LexemeKey, readings[index].Reading), readings[index].Reading);
        for (int index = 1; index < readings.Count; index++)
        {
            if (StringComparer.Ordinal.Compare(readings[index - 1].Key, readings[index].Key) >= 0)
                throw new InvalidDataException($"Neutral lexicon line {lineNumber} readings must have unique readingKey order.");
        }

        return new LexemeMaterialization(
            row.LexemeKey,
            row.Lemma,
            row.ProductiveRuleId,
            row.Route,
            readings.Select(item => item.Reading).ToArray());
    }

    private static string? ShareTag(string? tag, HashSet<string> sharedTags)
    {
        if (tag is null) return null;
        if (sharedTags.TryGetValue(tag, out string? shared)) return shared;
        // Bound this load's temporary index while retaining reuse after saturation.
        if (sharedTags.Count < MaxSharedTags) sharedTags.Add(tag);
        return tag;
    }

    internal static string CreateRoutedLexemeKey(
        string lemma,
        string? productiveRuleId,
        string route,
        IEnumerable<WordReading> paradigm)
    {
        string[] rows = paradigm
            .Select(reading => string.Join('\u001f',
                BulgarianTextNormalizer.NormalizeKey(reading.Form),
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
                reading.PartOfSpeech ?? string.Empty))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
        if (rows.Length == 0)
            throw new InvalidDataException("A routed lexeme identity requires at least one semantic reading.");
        return "lexeme-v2:" + LexicalIdentity.HashLengthPrefixed(
            [
                RoutedLexemeIdentitySchema,
                BulgarianTextNormalizer.NormalizeKey(lemma),
                productiveRuleId ?? string.Empty,
                route,
                LexicalIdentity.HashLengthPrefixed(rows)
            ]);
    }

    private static string CreateSourceId(IReadOnlyList<LexemeMaterialization> lexemes, bool routedSource)
    {
        IEnumerable<string> Fields()
        {
            yield return routedSource ? RoutedSourceIdentitySchema : SourceIdentitySchema;
            foreach (LexemeMaterialization lexeme in lexemes)
            {
                if (routedSource)
                {
                    yield return lexeme.Route is null ? LexiconSchemaVersion : RoutedLexiconSchemaVersion;
                    yield return lexeme.Route ?? string.Empty;
                }
                yield return lexeme.LexemeKey;
                yield return lexeme.Lemma;
                yield return lexeme.ProductiveRuleId ?? string.Empty;
                yield return lexeme.Readings.Length.ToString(CultureInfo.InvariantCulture);
                for (int index = 0; index < lexeme.Readings.Length; index++)
                {
                    WordReading reading = lexeme.Readings[index];
                    yield return LexicalIdentity.CreateReadingKey(lexeme.LexemeKey, reading);
                    yield return reading.Form;
                    yield return reading.Lemma;
                    yield return reading.PartOfSpeech ?? string.Empty;
                    yield return BitConverter.DoubleToInt64Bits(reading.Score).ToString("x16", CultureInfo.InvariantCulture);
                }
            }
        }

        return (routedSource ? "neutral-source-v2:" : "neutral-source-v1:") +
            LexicalIdentity.HashLengthPrefixed(Fields());
    }

    private static string CreateGeneratedSourceId(IReadOnlyList<LexemeMaterialization> lexemes)
    {
        ValidateReadingSets(lexemes); // Explicit rows are checked even during metadata-only hashing.
        IEnumerable<string> Fields()
        {
            yield return "bglang.neutral-dictionary-source.v3";
            yield return lexemes.Count.ToString(CultureInfo.InvariantCulture);
            foreach (LexemeMaterialization row in lexemes)
            {
                yield return row.Generated is not null ? GeneratedLexemeSource.Schema :
                    row.Route is null ? LexiconSchemaVersion : RoutedLexiconSchemaVersion;
                yield return row.Route ?? string.Empty;
                yield return row.LexemeKey;
                yield return row.Lemma;
                yield return row.ProductiveRuleId ?? string.Empty;
                if (row.Generated is not null)
                {
                    foreach (string field in row.Generated.IdentityFields()) yield return field;
                }
                else
                {
                    yield return row.Readings.Length.ToString(CultureInfo.InvariantCulture);
                    foreach (WordReading reading in row.Readings)
                    {
                        yield return LexicalIdentity.CreateReadingKey(row.LexemeKey, reading);
                        yield return reading.Form;
                        yield return reading.Lemma;
                        yield return reading.PartOfSpeech ?? string.Empty;
                        yield return BitConverter.DoubleToInt64Bits(reading.Score).ToString("x16", CultureInfo.InvariantCulture);
                    }
                }
            }
        }
        return "neutral-source-v3:" + LexicalIdentity.HashLengthPrefixed(Fields());
    }

    private static void ValidateTextEnvelope(FileStream stream, bool allowEmpty = false)
    {
        if (stream.Length == 0 && allowEmpty) return;
        if (stream.Length == 0)
            throw new InvalidDataException("Neutral lexicon must not be empty.");
        Span<byte> prefix = stackalloc byte[3];
        int prefixLength = stream.Read(prefix);
        if (prefixLength == 3 && prefix.SequenceEqual(Encoding.UTF8.Preamble))
            throw new InvalidDataException("Neutral lexicon must be UTF-8 without BOM.");
        stream.Position = 0;
        byte[] buffer = new byte[64 * 1024];
        int read;
        byte last = 0;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (buffer.AsSpan(0, read).Contains((byte)'\r'))
                throw new InvalidDataException("Neutral lexicon must use LF line endings.");
            last = buffer[read - 1];
        }
        if (last != (byte)'\n')
            throw new InvalidDataException("Neutral lexicon must end with LF.");
        stream.Position = 0;
    }

    private static void ValidateManifest(ManifestDocument manifest)
    {
        bool annotated = manifest.SchemaVersion == AnnotatedManifestSchemaVersion;
        if (manifest.SchemaVersion != ManifestSchemaVersion && !annotated || manifest.CreatedAtUtc == default)
        {
            throw new InvalidDataException("Neutral package manifest schema or timestamp is invalid.");
        }
        if (manifest.Lexicon is null ||
            !string.Equals(manifest.Lexicon.Path, LexiconFileName, StringComparison.Ordinal) &&
            !string.Equals(manifest.Lexicon.Path, RoutedLexiconFileName, StringComparison.Ordinal) &&
            !string.Equals(manifest.Lexicon.Path, GeneratedLexiconFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Neutral package lexicon metadata is invalid.");
        if (annotated && manifest.Lexicon.Path != GeneratedLexiconFileName)
            throw new InvalidDataException("Annotated packages require lexical v3 declarations.");
        ValidateComponentMetadata(manifest.Lexicon, manifest.Lexicon.Path, "neutral lexicon", allowEmpty: annotated);
        ValidateComponentMetadata(manifest.WordFormation, WordFormationFileName, "word formation");
        if (annotated) ValidateComponentMetadata(manifest.Annotations!, LexemeAnnotations.FileName, "annotations");
        else if (manifest.Annotations is not null) throw new InvalidDataException("Annotations require manifest v2.");
        string expectedPackageId = CreatePackageId(manifest.Lexicon, manifest.WordFormation, manifest.Annotations);
        if (!string.Equals(manifest.PackageId, expectedPackageId, StringComparison.Ordinal))
            throw new InvalidDataException("Neutral packageId does not match its component identities.");
    }

    private static void ValidateComponentMetadata(ComponentDocument component, string path, string label, bool allowEmpty = false)
    {
        if (component is null || !string.Equals(component.Path, path, StringComparison.Ordinal) ||
            (component.Bytes < 0 || component.Bytes == 0 && !allowEmpty) || !IsSha256(component.Sha256) || string.IsNullOrWhiteSpace(component.SourceId))
        {
            throw new InvalidDataException($"Neutral package {label} metadata is invalid.");
        }
    }

    private static void ValidateExactFileSet(string root, string lexiconFileName, bool annotated)
    {
        DirectoryInfo directory = new(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Neutral package root is missing or unsafe.");
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase)
        {
            "manifest.json", lexiconFileName, WordFormationFileName
        };
        if (annotated) expected.Add(LexemeAnnotations.FileName);
        FileSystemInfo[] entries = directory.GetFileSystemInfos();
        if (entries.Length != expected.Count)
            throw new InvalidDataException("Neutral package contains missing or extra entries.");
        foreach (FileSystemInfo entry in entries)
        {
            if (entry is not FileInfo || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || !expected.Remove(entry.Name))
                throw new InvalidDataException($"Neutral package entry '{entry.Name}' is unexpected or unsafe.");
        }
        if (expected.Count != 0)
            throw new InvalidDataException("Neutral package is incomplete.");
    }

    private static string ValidateComponent(FileStream stream, ComponentDocument component, string label)
    {
        if (stream.Length != component.Bytes)
            throw new InvalidDataException($"Neutral package {label} size/path identity is invalid.");
        string sha256 = Sha256Hex(stream);
        if (!string.Equals(sha256, component.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Neutral package {label} SHA-256 is invalid.");
        return sha256;
    }

    private static void ValidateManifestJson(JsonElement root)
    {
        const string label = "Neutral package manifest";
        bool annotated = StrictJson.RequireString(root, "schemaVersion", label) == AnnotatedManifestSchemaVersion;
        string[] fields = ["schemaVersion", "packageId", "createdAtUtc", "lexicon", "wordFormation"];
        StrictJson.RequireObject(root, label, annotated ? [.. fields, "annotations"] : fields);
        if (annotated) ValidateComponentJson(StrictJson.RequireProperty(root, "annotations", JsonValueKind.Object, label), label + ".annotations");
        StrictJson.RequireString(root, "packageId", label);
        StrictJson.RequireProperty(root, "createdAtUtc", JsonValueKind.String, label);
        ValidateComponentJson(StrictJson.RequireProperty(root, "lexicon", JsonValueKind.Object, label), label + ".lexicon");
        ValidateComponentJson(StrictJson.RequireProperty(root, "wordFormation", JsonValueKind.Object, label), label + ".wordFormation");
    }

    private static void ValidateComponentJson(JsonElement root, string label)
    {
        StrictJson.RequireObject(root, label, ["path", "bytes", "sha256", "sourceId"]);
        StrictJson.RequireString(root, "path", label);
        StrictJson.RequireProperty(root, "bytes", JsonValueKind.Number, label);
        StrictJson.RequireString(root, "sha256", label);
        StrictJson.RequireString(root, "sourceId", label);
    }

    private static void ValidateLexemeJson(JsonElement root, int lineNumber)
    {
        string label = $"Neutral lexicon line {lineNumber}";
        string schema = StrictJson.RequireString(root, "schemaVersion", label);
        string[] fields = string.Equals(schema, RoutedLexiconSchemaVersion, StringComparison.Ordinal)
            ? ["schemaVersion", "lexemeKey", "lemma", "productiveRuleId", "route", "readings"]
            : ["schemaVersion", "lexemeKey", "lemma", "productiveRuleId", "readings"];
        StrictJson.RequireObject(root, label, fields);
        StrictJson.RequireString(root, "schemaVersion", label);
        StrictJson.RequireString(root, "lexemeKey", label);
        StrictJson.RequireString(root, "lemma", label);
        StrictJson.RequireStringOrNull(root, "productiveRuleId", label);
        if (fields.Length == 6)
            StrictJson.RequireString(root, "route", label);
        int index = 0;
        foreach (JsonElement reading in StrictJson.RequireArray(root, "readings", label))
        {
            string readingLabel = $"{label}.readings[{index}]";
            StrictJson.RequireObject(reading, readingLabel, ["form", "lemma", "fullTag", "score"]);
            StrictJson.RequireString(reading, "form", readingLabel);
            StrictJson.RequireString(reading, "lemma", readingLabel);
            StrictJson.RequireStringOrNull(reading, "fullTag", readingLabel);
            StrictJson.RequireProperty(reading, "score", JsonValueKind.Number, readingLabel);
            index++;
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character =>
        character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsRawCanonical(string value) =>
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static FileStream OpenLockedRegularFile(string path, string label)
    {
        FileInfo file = new(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"Neutral package {label} path is missing or unsafe.");
        for (DirectoryInfo? directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Neutral package {label} ancestry is unsafe.");
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
    }

    private static T ReadJson<T>(
        FileStream stream,
        string label,
        Action<JsonElement> validate) where T : class =>
        StrictJson.ReadText<T>(ReadUtf8(stream, label), label, validate);

    private static string ReadUtf8(FileStream stream, string label)
    {
        try
        {
            stream.Position = 0;
            using StreamReader reader = new(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024,
                leaveOpen: true);
            string text = reader.ReadToEnd();
            stream.Position = 0;
            return text;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Neutral package {label} contains invalid UTF-8.", exception);
        }
    }

    private static byte[] ReadBytes(FileStream stream)
    {
        stream.Position = 0;
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        stream.Position = 0;
        return bytes;
    }

    private static string Sha256Hex(FileStream stream)
    {
        stream.Position = 0;
        string sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return sha256;
    }

    internal sealed record ComponentDocument(string Path, long Bytes, string Sha256, string SourceId);
    private sealed record ManifestDocument(
        string SchemaVersion,
        string PackageId,
        DateTimeOffset CreatedAtUtc,
        ComponentDocument Lexicon,
        ComponentDocument WordFormation,
        ComponentDocument? Annotations);
    private sealed record LexemeRow(
        string SchemaVersion,
        string LexemeKey,
        string Lemma,
        string? ProductiveRuleId,
        string? Route,
        ReadingRow[] Readings);
    private sealed record ReadingRow(string Form, string Lemma, string? FullTag, double Score);
    private sealed record LexemeMaterialization(
        string LexemeKey,
        string Lemma,
        string? ProductiveRuleId,
        string? Route,
        WordReading[] Readings,
        GeneratedLexemeSource? Generated = null);
    private sealed record SourceMaterialization(
        string SourceId,
        WordReading[] Readings,
        WordReading[] RoutedReadings,
        IReadOnlyDictionary<string, WordForm[]> FormsByLemma);
    private readonly record struct SemanticReading(
        string Form,
        string Lemma,
        string FullTag,
        long ScoreBits)
    {
        internal static SemanticReading Create(WordReading reading) => new(
            BulgarianTextNormalizer.NormalizeKey(reading.Form),
            BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
            reading.PartOfSpeech ?? string.Empty,
            BitConverter.DoubleToInt64Bits(reading.Score));
    }
}
