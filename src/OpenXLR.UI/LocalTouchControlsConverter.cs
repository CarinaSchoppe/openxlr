using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.UI;

/// <summary>A malformed local sizing preference must not discard other settings.</summary>
public sealed class LocalTouchControlsConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
        reader.Skip();
        return false;
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
