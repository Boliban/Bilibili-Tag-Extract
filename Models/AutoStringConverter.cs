using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoTagProcessor;

public class AutoStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString()!;
        if (reader.TokenType == JsonTokenType.Number)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.GetRawText();
        }
        using var docFallback = JsonDocument.ParseValue(ref reader);
        return docFallback.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}