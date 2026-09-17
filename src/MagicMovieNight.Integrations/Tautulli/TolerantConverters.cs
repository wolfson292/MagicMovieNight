using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicMovieNight.Integrations.Tautulli;

/// <summary>
/// Tautulli's API is generated from loosely typed Python, so a field's JSON type varies
/// row to row: `rating_key` comes back as a number, and `media_index` is a number for
/// episodes but an empty string for movies. System.Text.Json is strict by default, so a
/// single mismatched row throws and takes the whole page of history with it.
///
/// These converters accept whichever shape arrives. That is deliberately lenient — this
/// is a foreign API we do not control, and losing 500 rows of history because one movie
/// had `""` where an episode number goes is a bad trade.
/// </summary>
internal class TolerantStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString()
                : reader.GetDouble().ToString("G17"),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// Reads an optional integer that may arrive as a number, a numeric string, an empty
/// string, or null. Empty and unparseable values become null rather than throwing —
/// "this row has no episode number" is information, not an error.
/// </summary>
internal class TolerantIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) ? n : null;

            case JsonTokenType.String:
                var raw = reader.GetString();
                return int.TryParse(raw, out var parsed) ? parsed : null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

/// <summary>Same tolerance for the 64-bit fields — row ids and unix timestamps.</summary>
internal class TolerantLongConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt64(out var n) ? n : 0,
            JsonTokenType.String => long.TryParse(reader.GetString(), out var p) ? p : 0,
            _ => 0,
        };

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}
