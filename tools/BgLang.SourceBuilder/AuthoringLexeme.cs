using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Contracts;
using BgLang.Core;

namespace BgLang.SourceBuilder;

internal sealed record AuthoringLexeme(
    string SchemaVersion, string Kind, string Lemma, string? ProductiveRuleId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseFullTag,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Score,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthoringLexeme.Reading[]? Readings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Route)
{
    internal static AuthoringLexeme Read(string line, int number)
    {
        string label = $"Authoring row {number}";
        return StrictJson.ReadText<AuthoringLexeme>(line, label, root =>
        {
            StrictJson.RequireObject(root, label, ["schemaVersion", "kind", "lemma", "productiveRuleId"],
                ["baseFullTag", "score", "readings", "route"]);
            foreach (string name in new[] { "schemaVersion", "kind", "lemma" }) StrictJson.RequireString(root, name, label);
            if (root.GetProperty("schemaVersion").GetString() != "bglang-neutral-authoring.v1")
                throw new InvalidDataException($"{label} schema is unsupported.");
            string kind = root.GetProperty("kind").GetString()!;
            string[] fields = kind switch
            {
                "productive" => ["baseFullTag", "score"],
                "stored" => ["readings"],
                "routed" => ["readings", "route"],
                _ => throw new InvalidDataException($"{label} kind is unsupported.")
            };
            StrictJson.RequireObject(root, label, ["schemaVersion", "kind", "lemma", "productiveRuleId", .. fields]);
            if (kind == "productive")
            {
                StrictJson.RequireString(root, "productiveRuleId", label);
                StrictJson.RequireString(root, "baseFullTag", label);
                StrictJson.RequireProperty(root, "score", JsonValueKind.Number, label);
            }
            else
            {
                RequireNullableString(root, "productiveRuleId", label);
                foreach (JsonElement reading in StrictJson.RequireProperty(root, "readings", JsonValueKind.Array, label).EnumerateArray())
                {
                    StrictJson.RequireObject(reading, label, ["form", "lemma", "fullTag", "score"]);
                    StrictJson.RequireString(reading, "form", label);
                    StrictJson.RequireString(reading, "lemma", label);
                    RequireNullableString(reading, "fullTag", label);
                    StrictJson.RequireProperty(reading, "score", JsonValueKind.Number, label);
                }
                if (kind == "routed" && StrictJson.RequireString(root, "route", label) != NeutralDictionaryPackageLoader.ExactPrefixOnlyRoute)
                    throw new InvalidDataException($"{label} route is unsupported.");
            }
        });
    }

    private static void RequireNullableString(JsonElement root, string name, string label)
    {
        if (root.GetProperty(name).ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidDataException($"{label} {name} must be a string or null.");
    }

    internal sealed record Reading(string Form, string Lemma, string? FullTag, double Score)
    {
        internal WordReading ToWordReading() => new(Form, Lemma, FullTag, Score, null, null, null, string.Empty);
    }
}
