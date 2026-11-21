using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgLang.Core;

/// <summary>A strict resolved phrase or one whole unavailable source phrase, in its original position.</summary>
[JsonConverter(typeof(AnnotationPhrase.Converter))]
internal sealed record AnnotationPhrase(LexemeAnnotations.WordReference[] Words, uint[] UnresolvedSourcePhrase)
{
    internal sealed class Converter : JsonConverter<AnnotationPhrase>
    {
        public override AnnotationPhrase Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
                return new(JsonSerializer.Deserialize<LexemeAnnotations.WordReference[]>(ref reader, options)!, []);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            return new([], document.RootElement.GetProperty("unresolvedSourcePhrase").Deserialize<uint[]>(options)!);
        }

        public override void Write(Utf8JsonWriter writer, AnnotationPhrase value, JsonSerializerOptions options)
        {
            if (value.UnresolvedSourcePhrase.Length == 0)
                JsonSerializer.Serialize(writer, value.Words, options);
            else
            {
                writer.WriteStartObject();
                writer.WritePropertyName("unresolvedSourcePhrase");
                JsonSerializer.Serialize(writer, value.UnresolvedSourcePhrase, options);
                writer.WriteEndObject();
            }
        }
    }
}
