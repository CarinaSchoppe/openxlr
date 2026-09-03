using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace OpenXLR;

/// <summary>
/// A pw-dump invocation can finish with additional change arrays during graph
/// churn. Changed objects carry their complete current information; null info
/// or props denotes removal. Fold by registry id, never by display name.
/// Shared as source so the UI does not depend on the hardware/backend assembly.
/// </summary>
internal static class PipeWireSnapshot
{
    internal static JsonDocument Parse(string json)
    {
        try
        {
            JsonDocument single = JsonDocument.Parse(json);
            if (single.RootElement.ValueKind == JsonValueKind.Array) return single;
            single.Dispose();
            throw new JsonException("Expected a PipeWire object array.");
        }
        catch (JsonException)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json),
                new JsonReaderOptions { AllowMultipleValues = true });
            var objects = new Dictionary<uint, JsonElement>();
            bool received = false;
            while (reader.Read())
            {
                using JsonDocument batch = JsonDocument.ParseValue(ref reader);
                if (batch.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Expected a PipeWire object array.");
                received = true;
                foreach (JsonElement item in batch.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement key)
                        || key.ValueKind != JsonValueKind.Number || !key.TryGetUInt32(out uint id))
                        throw new JsonException("PipeWire update has no valid registry id.");
                    if (item.TryGetProperty("info", out JsonElement info) && info.ValueKind == JsonValueKind.Null
                        || item.TryGetProperty("props", out JsonElement props) && props.ValueKind == JsonValueKind.Null)
                        objects.Remove(id);
                    else
                        objects[id] = item.Clone();
                }
            }
            if (!received) throw new JsonException("Empty PipeWire dump.");
            return JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(objects.Values));
        }
    }
}
