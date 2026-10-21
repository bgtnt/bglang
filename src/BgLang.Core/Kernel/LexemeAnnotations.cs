using System.Text.Encodings.Web;
using System.Text.Json;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Editorial owners and exact lexical annotations, separate from morphology identity.</summary>
internal sealed record LexemeAnnotations(string SchemaVersion, LexemeAnnotations.Entry[] Entries)
{
    internal const string Schema = "bglang-neutral-annotations.v1";
    internal const string SourceEvidenceSchema = "bglang-neutral-annotations.v2";
    internal const string FileName = "lexeme-annotations.v1.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly HashSet<string> RelationKinds = new(StringComparer.Ordinal)
    {
        "unknown", "Дублет", "антоним", "контрарна_антоним", "комплементарeн_антоним",
        "комплементарен_антоним", // Keep the historical mixed-script wire label above as well.
        "отрицание", "контрадикция", "съкращение", "съкратено", "синоним", "синоним_Глагол",
        "синоним_Междуметие", "синоним_Въпросително_местоимение", "синоним_Неопределително_местоимение",
        "синоним_Обобщително_местоимение", "синоним_Отрицателно_местоимение", "синоним_Показателно_местоимение",
        "синоним_Притежателно_местоимение", "синоним_Предлог", "синоним_Прилагателно", "синоним_Съществително",
        "синоним_Съюз", "синоним_Частица", "синоним_Бройно_Числително", "синоним_Редно_Числително",
        "синоним_Народно", "синоним_Словосъчетание"
    };

    internal static LexemeAnnotations Read(string text)
    {
        const string label = "Lexeme annotations";
        LexemeAnnotations result = StrictJson.ReadText<LexemeAnnotations>(text, label, root =>
        {
            StrictJson.RequireObject(root, label, ["schemaVersion", "entries"]);
            string schema = StrictJson.RequireString(root, "schemaVersion", label);
            if (schema is not (Schema or SourceEvidenceSchema))
                throw new InvalidDataException("Unsupported annotation schema.");
            foreach (JsonElement entry in StrictJson.RequireArray(root, "entries", label))
                ValidateEntryJson(entry, allowUnresolved: schema == SourceEvidenceSchema);
        });
        for (int i = 1; i < result.Entries.Length; i++)
            if (StringComparer.Ordinal.Compare(result.Entries[i - 1].EntryId, result.Entries[i].EntryId) >= 0)
                throw new InvalidDataException("Annotation entries require unique ordinal entryId order.");
        foreach (Entry entry in result.Entries)
        {
            for (int i = 1; i < entry.Stress.Length; i++)
                if (CompareStress(entry.Stress[i - 1], entry.Stress[i]) >= 0)
                    throw new InvalidDataException("Stress rows require unique ordinal form/fullTag order.");
            foreach (StressRow row in entry.Stress)
            {
                int vowels = row.Form.Count(IsVowel), previous = -1;
                if (row.VowelOrdinals.Length == 0) throw new InvalidDataException("Stress requires vowel ordinals.");
                foreach (int ordinal in row.VowelOrdinals)
                {
                    if (ordinal <= previous || ordinal >= vowels)
                        throw new InvalidDataException("Stress ordinals must be unique, increasing and within the exact form.");
                    previous = ordinal;
                }
            }
        }
        return result;
    }

    internal LexemeAnnotations Canonicalize() => this with
    {
        Entries = Entries.OrderBy(e => e.EntryId, StringComparer.Ordinal).Select(e => e with
        {
            Stress = e.Stress.OrderBy(s => s.Form, StringComparer.Ordinal).ThenBy(s => s.FullTag, StringComparer.Ordinal).ToArray()
        }).ToArray()
    };

    internal byte[] Serialize() => [.. JsonSerializer.SerializeToUtf8Bytes(Canonicalize(), Json), (byte)'\n'];

    internal string SourceId => (SchemaVersion == SourceEvidenceSchema ? "neutral-annotations-v2:" : "neutral-annotations-v1:") +
        LexicalIdentity.HashLengthPrefixed([SchemaVersion, JsonSerializer.Serialize(Canonicalize(), Json)]);

    /// <summary>Bind all references, then retain only stress supported by every visible reading and owner.</summary>
    internal IReadOnlyDictionary<string, int[]>? Bind(IReadOnlyDictionary<string, WordReading[]> inventory)
    {
        Dictionary<string, Entry> entries = Entries.ToDictionary(e => e.EntryId, StringComparer.Ordinal);
        HashSet<string> ownedKeys = Entries.Select(e => e.LexemeKey).ToHashSet(StringComparer.Ordinal);
        if (ownedKeys.Count != inventory.Count || ownedKeys.Any(key => !inventory.ContainsKey(key)))
            throw new InvalidDataException("Every lexical declaration needs an owner, and every owner needs a declaration.");
        Dictionary<string, HashSet<(string Form, string? Tag)>> readingSets = new(StringComparer.Ordinal);
        bool Contains(string key, string form, string? tag)
        {
            if (!readingSets.TryGetValue(key, out var set))
            {
                set = inventory[key].Select(r => (r.Form, r.PartOfSpeech)).ToHashSet();
                readingSets.Add(key, set);
            }
            return set.Contains((form, tag));
        }

        Dictionary<string, int[]?> candidates = new(StringComparer.Ordinal);
        var stressByEntry = new Dictionary<string, Dictionary<(string Form, string? Tag), int[]>>(StringComparer.Ordinal);
        foreach (Entry entry in Entries)
        {
            Dictionary<(string Form, string? Tag), int[]>? stress = null;
            foreach (StressRow row in entry.Stress)
            {
                if (!Contains(entry.LexemeKey, row.Form, row.FullTag))
                    throw new InvalidDataException("Stress must bind an exact reading of its owner.");
                (stress ??= []).Add((row.Form, row.FullTag), row.VowelOrdinals);
                candidates.TryAdd(BulgarianTextNormalizer.NormalizeKey(row.Form), row.VowelOrdinals);
            }
            if (stress is not null) stressByEntry.Add(entry.EntryId, stress);
            foreach (WordReference word in entry.Related.SelectMany(r => r.Phrases).SelectMany(p => p.Words))
                if (!entries.TryGetValue(word.EntryId, out Entry? target) || !Contains(target.LexemeKey, word.Form, word.FullTag))
                    throw new InvalidDataException("Related words must bind an existing owner and its exact form/fullTag.");
        }
        if (candidates.Count == 0) return null;
        var owners = Entries.GroupBy(e => e.LexemeKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var lexeme in inventory)
        foreach (WordReading reading in lexeme.Value)
        {
            string form = BulgarianTextNormalizer.NormalizeKey(reading.Form);
            if (!candidates.TryGetValue(form, out int[]? expected) || expected is null) continue;
            foreach (Entry owner in owners[lexeme.Key])
                if (!stressByEntry.TryGetValue(owner.EntryId, out var stress) ||
                    !stress.TryGetValue((reading.Form, reading.PartOfSpeech), out int[]? actual) ||
                    !expected.AsSpan().SequenceEqual(actual))
                {
                    candidates[form] = null;
                    break;
                }
        }
        Dictionary<string, int[]> resolved = new(StringComparer.Ordinal);
        foreach (var candidate in candidates)
            if (candidate.Value is { } ordinals) resolved.Add(candidate.Key, (int[])ordinals.Clone());
        return resolved.Count == 0 ? null : resolved;
    }

    internal static void ValidateEntryJson(JsonElement entry, bool allowUnresolved = false)
    {
        const string label = "Annotation entry";
        StrictJson.RequireObject(entry, label, ["entryId", "lexemeKey", "meaning", "stress", "related"]);
        RequireCanonical(entry, "lexemeKey");
        ValidateMetadataJson(entry, allowUnresolved);
    }

    internal static void ValidateMetadataJson(JsonElement entry, bool allowUnresolved = false)
    {
        const string label = "Editorial metadata";
        RequireCanonical(entry, "entryId");
        foreach (JsonElement line in StrictJson.RequireArray(entry, "meaning", label))
            if (line.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Meaning must contain literal strings.");
        foreach (JsonElement stress in StrictJson.RequireArray(entry, "stress", label))
        {
            StrictJson.RequireObject(stress, label, ["form", "fullTag", "vowelOrdinals"]);
            ValidateFormTag(stress);
            foreach (JsonElement ordinal in StrictJson.RequireArray(stress, "vowelOrdinals", label))
                if (ordinal.ValueKind != JsonValueKind.Number || !ordinal.TryGetInt32(out _))
                    throw new InvalidDataException("Stress ordinals must be integers.");
        }
        foreach (JsonElement relation in StrictJson.RequireArray(entry, "related", label))
        {
            StrictJson.RequireObject(relation, label, ["kind", "phrases"]);
            if (!RelationKinds.Contains(StrictJson.RequireString(relation, "kind", label)))
                throw new InvalidDataException("Unsupported relation kind.");
            JsonElement phrases = StrictJson.RequireProperty(relation, "phrases", JsonValueKind.Array, label);
            if (phrases.GetArrayLength() == 0) throw new InvalidDataException("Related groups require phrases.");
            foreach (JsonElement phrase in phrases.EnumerateArray())
            {
                if (allowUnresolved && phrase.ValueKind == JsonValueKind.Object)
                {
                    StrictJson.RequireObject(phrase, label, ["unresolvedSourcePhrase"]);
                    JsonElement words = StrictJson.RequireProperty(phrase, "unresolvedSourcePhrase", JsonValueKind.Array, label);
                    if (words.GetArrayLength() == 0 || words.EnumerateArray().Any(word =>
                        word.ValueKind != JsonValueKind.Number || !word.TryGetUInt32(out uint id) || id == 0))
                        throw new InvalidDataException("Unavailable source phrases require nonzero raw Word IDs.");
                    continue;
                }
                if (phrase.ValueKind != JsonValueKind.Array || phrase.GetArrayLength() == 0)
                    throw new InvalidDataException("Related phrases require words.");
                foreach (JsonElement word in phrase.EnumerateArray())
                {
                    StrictJson.RequireObject(word, label, ["entryId", "form", "fullTag"]);
                    RequireCanonical(word, "entryId"); ValidateFormTag(word);
                }
            }
        }
    }

    private static void RequireCanonical(JsonElement row, string field)
    {
        string value = StrictJson.RequireString(row, field, "Annotation");
        if (value != value.Trim()) throw new InvalidDataException("Annotation identifiers and reading fields must be trimmed.");
    }

    private static void ValidateFormTag(JsonElement row)
    {
        RequireCanonical(row, "form");
        StrictJson.RequireStringOrNull(row, "fullTag", "Annotation");
        if (row.GetProperty("fullTag").ValueKind != JsonValueKind.Null) RequireCanonical(row, "fullTag");
    }

    private static int CompareStress(StressRow left, StressRow right)
    {
        int form = StringComparer.Ordinal.Compare(left.Form, right.Form);
        return form != 0 ? form : StringComparer.Ordinal.Compare(left.FullTag, right.FullTag);
    }

    private static bool IsVowel(char value) => char.ToLowerInvariant(value) is 'а' or 'ъ' or 'о' or 'у' or 'е' or 'и' or 'ю' or 'я' or 'ѐ' or 'ѝ';

    internal sealed record Entry(string EntryId, string LexemeKey, string[] Meaning, StressRow[] Stress, Relation[] Related);
    internal sealed record StressRow(string Form, string? FullTag, int[] VowelOrdinals);
    internal sealed record Relation(string Kind, AnnotationPhrase[] Phrases);
    internal sealed record WordReference(string EntryId, string Form, string? FullTag);
}
