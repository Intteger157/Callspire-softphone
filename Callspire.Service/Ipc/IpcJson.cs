using System.Text.Json;
using System.Text.Json.Serialization;

namespace Softphone.Service.Ipc
{
    /// <summary>Shared serializer options for the wire format (camelCase, enums as strings, nulls kept).</summary>
    public static class IpcJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            WriteIndented = false,
        };

        /// <summary>Envelope-only options: absent fields (id/method/error/…) are omitted on the wire.</summary>
        public static readonly JsonSerializerOptions EnvelopeOptions = new(Options)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

        public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);

        public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    }
}
