using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatAiCopilot.Api.Modules.Agent.Contracts;

/// <summary>
/// Reads the dashed and undashed spellings of the same identifier. Agent responses hand out
/// undashed identifiers, so a request that echoes one back must not be rejected as malformed.
/// </summary>
public sealed class LenientGuidJsonConverterV1 : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A GUID string is required.");
        return Guid.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("The value is not a valid GUID.");
    }

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}
