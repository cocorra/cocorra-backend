using System;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace Cocorra.BLL.Base
{
    /// <summary>
    /// Puts every timestamp on the wire as UTC with a trailing "Z". Without it, values read back
    /// from the database serialize with no offset and clients parse them as device-local time,
    /// while values created in memory already carry "Z" — the same field in two formats.
    /// </summary>
    public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            UtcDateTime.Normalize(reader.GetDateTime());

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(UtcDateTime.Normalize(value));
    }
}
