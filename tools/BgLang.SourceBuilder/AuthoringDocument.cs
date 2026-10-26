using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Core;

namespace BgLang.SourceBuilder;

/// <summary>Editable v2 document; lexical declarations reuse the original authoring contract.</summary>
internal sealed record AuthoringDocument(string SchemaVersion, AuthoringDocument.Entry[] Entries)
{
    internal const string Schema = "bglang-neutral-authoring.v2";
    internal const string SourceEvidenceSchema = "bglang-neutral-authoring.v3";
    internal const string StressEvidenceSchema = "bglang-neutral-authoring.v4";
    internal const string AccentSchema = "bglang-neutral-authoring.v5";
    internal const string FileName = "lexemes.source.json";

    internal static AuthoringDocument Read(string text) => StrictJson.ReadText<AuthoringDocument>(text, "Authoring document", root =>
    {
        StrictJson.RequireObject(root, "Authoring document", ["schemaVersion", "entries"]);
        string schema = StrictJson.RequireString(root, "schemaVersion", "Authoring document");
        if (schema is not (Schema or SourceEvidenceSchema or StressEvidenceSchema or AccentSchema))
            throw new InvalidDataException("Unsupported authoring document schema.");
        int number = 0;
        foreach (JsonElement entry in StrictJson.RequireArray(root, "entries", "Authoring document"))
        {
            StrictJson.RequireObject(entry, "Authoring entry", ["entryId", "lexeme", "meaning", "stress", "related"],
                schema == AccentSchema ? ["accentParadigm", "stressPattern"] : []);
            AuthoringLexeme.Read(StrictJson.RequireProperty(entry, "lexeme", JsonValueKind.Object, "Authoring entry").GetRawText(), ++number);
            LexemeAnnotations.ValidateMetadataJson(entry, allowUnresolved: schema != Schema,
                allowStressSource: schema is StressEvidenceSchema or AccentSchema, allowAccent: schema == AccentSchema);
        }
    });

    internal byte[] Serialize() => [.. JsonSerializer.SerializeToUtf8Bytes(this with
    {
        Entries = Entries.OrderBy(e => e.EntryId, StringComparer.Ordinal).ToArray()
    }, NeutralSourceBuilder.JsonOptions), (byte)'\n'];

    internal sealed record Entry(string EntryId, AuthoringLexeme Lexeme, string[] Meaning,
        LexemeAnnotations.StressRow[] Stress, LexemeAnnotations.Relation[] Related)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LexemeAccentParadigm? AccentParadigm { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LexemeStressPattern? StressPattern { get; init; }
    }
}
