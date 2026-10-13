using System.Text.Json;
using BgLang.Core;

namespace BgLang.SourceBuilder;

/// <summary>Editable v2 document; lexical declarations reuse the original authoring contract.</summary>
internal sealed record AuthoringDocument(string SchemaVersion, AuthoringDocument.Entry[] Entries)
{
    internal const string Schema = "bglang-neutral-authoring.v2";
    internal const string FileName = "lexemes.source.json";

    internal static AuthoringDocument Read(string text) => StrictJson.ReadText<AuthoringDocument>(text, "Authoring document", root =>
    {
        StrictJson.RequireObject(root, "Authoring document", ["schemaVersion", "entries"]);
        if (StrictJson.RequireString(root, "schemaVersion", "Authoring document") != Schema)
            throw new InvalidDataException("Unsupported authoring document schema.");
        int number = 0;
        foreach (JsonElement entry in StrictJson.RequireArray(root, "entries", "Authoring document"))
        {
            StrictJson.RequireObject(entry, "Authoring entry", ["entryId", "lexeme", "meaning", "stress", "related"]);
            AuthoringLexeme.Read(StrictJson.RequireProperty(entry, "lexeme", JsonValueKind.Object, "Authoring entry").GetRawText(), ++number);
            LexemeAnnotations.ValidateMetadataJson(entry);
        }
    });

    internal byte[] Serialize() => [.. JsonSerializer.SerializeToUtf8Bytes(this with
    {
        Entries = Entries.OrderBy(e => e.EntryId, StringComparer.Ordinal).ToArray()
    }, NeutralSourceBuilder.JsonOptions), (byte)'\n'];

    internal sealed record Entry(string EntryId, AuthoringLexeme Lexeme, string[] Meaning,
        LexemeAnnotations.StressRow[] Stress, LexemeAnnotations.Relation[] Related);
}
