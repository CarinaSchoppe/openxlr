using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenXLR.UI;

/// <summary>A damaged local mode must not discard unrelated window preferences.</summary>
public sealed class LocalAppearanceModeConverter : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return AppearanceModes.Normalize(reader.GetString());
        reader.Skip();
        return AppearanceModes.System;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(AppearanceModes.Normalize(value));
}
