using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgLang.Core;

internal static class StrictJson
{
    private static readonly JsonSerializerOptions StrictOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions CaseSensitiveOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false
    };

    internal static T Read<T>(
        string path,
        string label,
        Action<JsonElement> validateSchema,
        bool rejectUnmappedMembers = true)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            RejectDuplicateProperties(document.RootElement, "$", label);
            validateSchema(document.RootElement);
            return document.RootElement.Deserialize<T>(
                       rejectUnmappedMembers ? StrictOptions : CaseSensitiveOptions)
                   ?? throw new InvalidDataException($"The {label} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {label} JSON is invalid: {exception.Message}", exception);
        }
    }

    internal static T ReadText<T>(
        string json,
        string label,
        Action<JsonElement> validateSchema,
        bool rejectUnmappedMembers = true)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            RejectDuplicateProperties(document.RootElement, "$", label);
            validateSchema(document.RootElement);
            return document.RootElement.Deserialize<T>(
                       rejectUnmappedMembers ? StrictOptions : CaseSensitiveOptions)
                   ?? throw new InvalidDataException($"The {label} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {label} JSON is invalid: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Probes only the root schemaVersion so the selected loader stays the single authority.
    /// The loader re-reads the file under its own lock and performs the full nested validation,
    /// so this probe deliberately checks duplicates at the root only.
    /// </summary>
    internal static string? ReadSchemaVersion(string path, string label)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            RejectDuplicateRootProperties(document.RootElement, label);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"The {label} must be a JSON object.");
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return value.GetString();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {label} JSON is invalid: {exception.Message}", exception);
        }
    }

    internal static void RequireObject(
        JsonElement element,
        string label,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} must be a JSON object.");

        HashSet<string> allowed = new(required, StringComparer.Ordinal);
        if (optional is not null)
            allowed.UnionWith(optional);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"{label} contains unknown field '{property.Name}'.");
        }

        foreach (string propertyName in required)
        {
            if (!element.TryGetProperty(propertyName, out _))
                throw new InvalidDataException($"{label} is missing required field '{propertyName}'.");
        }
    }

    internal static JsonElement RequireProperty(
        JsonElement element,
        string propertyName,
        JsonValueKind kind,
        string label)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            throw new InvalidDataException($"{label} is missing required field '{propertyName}'.");
        if (value.ValueKind != kind)
            throw new InvalidDataException($"{label}.{propertyName} must be {Describe(kind)}.");
        return value;
    }

    internal static JsonElement RequireProperty(
        JsonElement element,
        string propertyName,
        JsonValueKind firstKind,
        JsonValueKind secondKind,
        string label)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            throw new InvalidDataException($"{label} is missing required field '{propertyName}'.");
        if (value.ValueKind != firstKind && value.ValueKind != secondKind)
        {
            throw new InvalidDataException(
                $"{label}.{propertyName} must be {Describe(firstKind)} or {Describe(secondKind)}.");
        }
        return value;
    }

    internal static string RequireString(JsonElement element, string propertyName, string label)
    {
        JsonElement value = RequireProperty(element, propertyName, JsonValueKind.String, label);
        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"{label}.{propertyName} must be non-empty.");
        return text;
    }

    internal static JsonElement.ArrayEnumerator RequireArray(
        JsonElement element,
        string propertyName,
        string label) =>
        RequireProperty(element, propertyName, JsonValueKind.Array, label).EnumerateArray();

    internal static void RequireStringOrNull(JsonElement element, string propertyName, string label)
    {
        JsonElement value = RequireProperty(
            element,
            propertyName,
            JsonValueKind.String,
            JsonValueKind.Null,
            label);
        if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{label}.{propertyName} must be non-empty when present.");
    }

    internal static void RequireStringArray(JsonElement element, string propertyName, string label)
    {
        int index = 0;
        foreach (JsonElement item in RequireArray(element, propertyName, label))
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new InvalidDataException(
                    $"{label}.{propertyName}[{index}] must be a non-empty string.");
            }
            index++;
        }
    }

    internal static void RequireStringArrayOrNull(JsonElement element, string propertyName, string label)
    {
        JsonElement value = RequireProperty(
            element,
            propertyName,
            JsonValueKind.Array,
            JsonValueKind.Null,
            label);
        if (value.ValueKind == JsonValueKind.Null)
            return;
        RequireStringArray(element, propertyName, label);
    }

    private static void RejectDuplicateRootProperties(JsonElement element, string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException($"The {label} contains duplicate field '{property.Name}' at $.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string path, string label)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"The {label} contains duplicate field '{property.Name}' at {path}.");
                RejectDuplicateProperties(property.Value, path + "." + property.Name, label);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item, $"{path}[{index}]", label);
                index++;
            }
        }
    }

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a Boolean",
        _ => kind.ToString()
    };
}
