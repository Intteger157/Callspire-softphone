using System;
using System.Globalization;
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
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new LocalDateTimeConverter() },
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

    /// <summary>
    /// History/statistics timestamps are stored as local <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>.
    /// The default System.Text.Json output ("2026-10-06T12:46:39.1234567", no offset) is not parseable by Foundation's
    /// ISO-8601 decoder, so the Swift side silently dropped every snapshot. Always write an offset and read any ISO
    /// value back as local time, which is what every lookup (<c>FindHistoryItem</c>) compares against.
    /// </summary>
    public sealed class LocalDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("DateTime must be an ISO-8601 string");
            var s = reader.GetString() ?? "";
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
                return dto.LocalDateTime;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                return dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt;
            throw new JsonException($"Invalid DateTime '{s}'");
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            var local = value.Kind switch
            {
                DateTimeKind.Utc => value.ToLocalTime(),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Local),
                _ => value,
            };
            writer.WriteStringValue(new DateTimeOffset(local).ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
        }
    }
}
