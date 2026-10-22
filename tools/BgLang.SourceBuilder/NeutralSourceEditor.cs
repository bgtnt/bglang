using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using BgLang.Core;

namespace BgLang.SourceBuilder;

/// <summary>Round-trip editing over a validated immutable package, published only to a fresh target.</summary>
internal static class NeutralSourceEditor
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static ExportResult Export(string manifest, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        var source = ReadSource(manifest);
        using OwnedStageDirectory stage = OwnedStageDirectory.Create(Path.GetDirectoryName(target)!);
        NeutralSourceBuilder.WriteNew(stage, AuthoringDocument.FileName, source.Document.Serialize());
        NeutralSourceBuilder.WriteNew(stage, NeutralDictionaryPackageLoader.WordFormationFileName, source.GraphBytes);
        stage.Publish(target);
        return new(Path.Combine(target, AuthoringDocument.FileName), Path.Combine(target, NeutralDictionaryPackageLoader.WordFormationFileName),
            source.Document.Entries.Length,
            "Пакети от SourceBuilder v2 се възстановяват байт по байт; други валидни v2 пакети запазват съдържанието с канонично форматиране и време.");
    }

    internal static NeutralSourceBuilder.Result Remove(string manifest, string entryId, string outputDirectory,
        string? transferTo = null, bool dropInboundPhrases = false)
    {
        if (transferTo is not null && dropInboundPhrases) throw new ArgumentException("Изберете само една политика за препратките.");
        NeutralSourceBuilder.PrepareTarget(outputDirectory);
        var source = ReadSource(manifest);
        AuthoringDocument.Entry removed = source.Document.Entries.SingleOrDefault(e => e.EntryId == entryId)
            ?? throw new ArgumentException("Записът за изтриване не съществува.");
        AuthoringDocument.Entry[] remaining = source.Document.Entries.Where(e => e.EntryId != entryId).ToArray();
        if (transferTo is not null)
        {
            int targetIndex = Array.FindIndex(remaining, e => e.EntryId == transferTo);
            if (targetIndex < 0) throw new ArgumentException("Получателят трябва да е друг съществуващ запис.");
            AuthoringDocument.Entry target = remaining[targetIndex];
            WordReading[] readings = target.Lexeme.Kind == "productive"
                ? source.Graph.GenerateLexicalReadings(target.Lexeme.Lemma, target.Lexeme.BaseFullTag!,
                    target.Lexeme.ProductiveRuleId!, target.Lexeme.Score!.Value)
                : target.Lexeme.Readings!.Select(r => r.ToWordReading()).ToArray();
            if (!readings.Any(r => string.Equals(r.Form, removed.Lexeme.Lemma, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Получателят няма форма, съвпадаща с изтриваната дума.");
            remaining[targetIndex] = target with
            {
                Meaning = [.. target.Meaning, .. removed.Meaning],
                Related = [.. target.Related, .. removed.Related]
            };
        }

        for (int index = 0; index < remaining.Length; index++)
        {
            List<LexemeAnnotations.Relation> relations = [];
            foreach (LexemeAnnotations.Relation relation in remaining[index].Related)
            {
                List<AnnotationPhrase> phrases = [];
                foreach (AnnotationPhrase phrase in relation.Phrases)
                {
                    bool incoming = phrase.Words.Any(word => word.EntryId == entryId);
                    if (incoming && dropInboundPhrases) continue;
                    if (incoming && transferTo is null)
                        throw new InvalidDataException("Запазен запис сочи към изтривания; посочете политика за препратките.");
                    phrases.Add(incoming
                        ? phrase with { Words = phrase.Words.Select(word => word.EntryId == entryId ? word with { EntryId = transferTo! } : word).ToArray() }
                        : phrase);
                }
                if (phrases.Count > 0) relations.Add(relation with { Phrases = phrases.ToArray() });
            }
            remaining[index] = remaining[index] with { Related = relations.ToArray() };
        }
        // The shared builder validates the entire final owner/form/tag set before atomic publication.
        AuthoringDocument final = source.Document with { Entries = remaining };
        return NeutralSourceBuilder.Build(final.Serialize(), source.GraphBytes, outputDirectory);
    }

    internal static CarryResult CarryStress(string manifest, string entryId, string outputDirectory, bool replaceFormStress)
    {
        NeutralSourceBuilder.PrepareTarget(outputDirectory);
        var source = ReadSource(manifest);
        int targetIndex = Array.FindIndex(source.Document.Entries, entry => entry.EntryId == entryId);
        if (targetIndex < 0)
            throw new ArgumentException("Записът за пренасяне на ударение не съществува.");
        AuthoringDocument.Entry target = source.Document.Entries[targetIndex];
        if (target.Lexeme.Kind != "productive")
            throw new InvalidDataException("Пренасянето на ударение изисква продуктивен запис.");
        string lemma = target.Lexeme.Lemma;
        string baseTag = target.Lexeme.BaseFullTag!;
        LexemeAnnotations.StressRow seed = target.Stress.SingleOrDefault(row =>
            string.Equals(row.Form, lemma, StringComparison.Ordinal) &&
            string.Equals(row.FullTag, baseTag, StringComparison.Ordinal))
            ?? throw new InvalidDataException("Продуктивният запис няма точно базово ударение.");
        if (seed.VowelOrdinals.Length == 0)
            throw new InvalidDataException("Базовото ударение трябва да съдържа поне една гласна.");
        if (seed.Source is not null)
            throw new InvalidDataException("Legacy ударение не може да бъде преквалифицирано като редакционно чрез carry-stress.");

        int[] vowelPositions = VowelPositions(lemma);
        Dictionary<(string Form, string? FullTag), List<int[]>> paths = [];
        WordReading[] readings = source.Graph.GenerateLexicalReadings(
            lemma, baseTag, target.Lexeme.ProductiveRuleId!, target.Lexeme.Score!.Value,
            observation =>
            {
                int[] projected = seed.VowelOrdinals
                    .Where(ordinal => vowelPositions[ordinal] < observation.RetainedOriginalPrefixLength)
                    .ToArray();
                paths.TryGetValue((observation.Form, observation.FullTag), out List<int[]>? values);
                (values ??= []).Add(projected);
                paths[(observation.Form, observation.FullTag)] = values;
            });
        Dictionary<(string Form, string? FullTag), LexemeAnnotations.StressRow> existing = target.Stress
            .Where(row => !(row.Form == seed.Form && row.FullTag == seed.FullTag))
            .ToDictionary(row => (row.Form, row.FullTag));
        List<LexemeAnnotations.StressRow> nextStress = [seed];
        int carried = 0, preserved = 0, unresolved = 0;
        foreach (WordReading reading in readings)
        {
            (string Form, string? FullTag) key = (reading.Form, reading.PartOfSpeech);
            if (key == (seed.Form, seed.FullTag)) continue;
            if (!replaceFormStress && existing.TryGetValue(key, out LexemeAnnotations.StressRow? old))
            {
                nextStress.Add(old); preserved++; continue;
            }
            int[]? projection = Unanimous(paths.TryGetValue(key, out List<int[]>? values) ? values : null);
            if (projection is { Length: > 0 })
            {
                nextStress.Add(new(reading.Form, reading.PartOfSpeech, projection)); carried++;
            }
            else unresolved++;
        }
        nextStress = nextStress.OrderBy(row => row.Form, StringComparer.Ordinal)
            .ThenBy(row => row.FullTag, StringComparer.Ordinal).ToList();
        AuthoringDocument.Entry[] entries = source.Document.Entries.ToArray();
        entries[targetIndex] = target with { Stress = nextStress.ToArray() };
        AuthoringDocument final = source.Document with { Entries = entries };
        NeutralSourceBuilder.Result result = NeutralSourceBuilder.Build(final.Serialize(), source.GraphBytes, outputDirectory);
        return new(result.ManifestPath, result.SourceId, result.PackageId, result.Lexemes, result.Readings, result.Entries,
            readings.Length - 1, carried, preserved, unresolved);
    }

    private static int[]? Unanimous(List<int[]>? values)
    {
        if (values is null || values.Count == 0) return null;
        int[] first = values[0];
        return values.All(value => value.AsSpan().SequenceEqual(first)) ? first : null;
    }

    private static int[] VowelPositions(string text)
    {
        List<int> positions = [];
        for (int index = 0; index < text.Length; index++)
            if (char.ToLowerInvariant(text[index]) is 'а' or 'ъ' or 'о' or 'у' or 'е' or 'и' or 'ю' or 'я' or 'ѐ' or 'ѝ')
                positions.Add(index);
        return positions.ToArray();
    }

    private static (AuthoringDocument Document, byte[] GraphBytes, WordFormationAnalyzer Graph) ReadSource(string manifest)
    {
        NeutralPackageSnapshot? snapshot = null;
        var loaded = NeutralDictionaryPackageLoader.Load(manifest, capture: value => snapshot = value);
        using var lexicon = loaded.Lexicon;
        LexemeAnnotations annotations = LexemeAnnotations.Read(Utf8.GetString(snapshot!.Annotations));
        Dictionary<string, AuthoringLexeme> declarations = new(StringComparer.Ordinal);
        foreach (string line in Utf8.GetString(snapshot.Lexicon).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument row = JsonDocument.Parse(line);
            JsonElement root = row.RootElement;
            string key = root.GetProperty("lexemeKey").GetString()!;
            AuthoringLexeme source;
            if (root.GetProperty("schemaVersion").GetString() == GeneratedLexemeSource.Schema)
            {
                GeneratedLexemeSource generated = GeneratedLexemeSource.Read(line, declarations.Count + 1);
                source = new("bglang-neutral-authoring.v1", "productive", generated.Lemma, generated.ProductiveRuleId,
                    generated.BaseFullTag, generated.Score, null, null);
            }
            else
            {
                string? route = root.TryGetProperty("route", out JsonElement routeValue) ? routeValue.GetString() : null;
                source = new("bglang-neutral-authoring.v1", route is null ? "stored" : "routed", root.GetProperty("lemma").GetString()!,
                    root.GetProperty("productiveRuleId").GetString(), null, null,
                    root.GetProperty("readings").Deserialize<AuthoringLexeme.Reading[]>(NeutralSourceBuilder.JsonOptions)!, route);
            }
            declarations.Add(key, source);
        }
        string authoringSchema = annotations.SchemaVersion switch
        {
            LexemeAnnotations.AccentSchema => AuthoringDocument.AccentSchema,
            LexemeAnnotations.StressEvidenceSchema => AuthoringDocument.StressEvidenceSchema,
            LexemeAnnotations.SourceEvidenceSchema => AuthoringDocument.SourceEvidenceSchema,
            _ => AuthoringDocument.Schema
        };
        return (new(authoringSchema,
            annotations.Entries.Select(e => new AuthoringDocument.Entry(
            e.EntryId, declarations[e.LexemeKey], e.Meaning, e.Stress, e.Related) { AccentParadigm = e.AccentParadigm, StressPattern = e.StressPattern }).ToArray()), snapshot.WordFormation, loaded.WordFormation);
    }

    internal sealed record ExportResult(string SourcePath, string GraphPath, int Entries, string RebuildGuarantee);
    internal sealed record CarryResult(string ManifestPath, string SourceId, string PackageId, int Lexemes, long Readings,
        int? Entries, int NonBaseReadings, int CarriedReadings, int PreservedReadings, int UnresolvedReadings);
}
