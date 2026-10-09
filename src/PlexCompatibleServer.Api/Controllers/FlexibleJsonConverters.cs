using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>Reads a JSON string, number or boolean as a string ("2026" and 2026 are equal).</summary>
internal sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader,
                                 Type typeToConvert,
                                 JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False =>
                Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for string.")
        };
    }

    public override void Write(Utf8JsonWriter writer,
                               string value,
                               JsonSerializerOptions options)
      => writer.WriteStringValue(value);
}

/// <summary>Reads a JSON number or its quoted form as T.</summary>
internal sealed class FlexibleNumberConverter<T> : JsonConverter<T> where T : struct, IFormattable, IParsable<T>
{
    public override T Read(ref Utf8JsonReader reader,
                           Type typeToConvert,
                           JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return T.Parse(Encoding.UTF8.GetString(reader.ValueSpan), CultureInfo.InvariantCulture);
            case JsonTokenType.String:
                var s = reader.GetString();

                if (string.IsNullOrWhiteSpace(s)) throw new JsonException($"Empty value for {typeof(T).Name}.");

                return T.Parse(s, CultureInfo.InvariantCulture);
            default:
                throw new JsonException($"Unexpected token {reader.TokenType} for {typeof(T).Name}.");
        }
    }

    public override void Write(Utf8JsonWriter writer,
                               T value,
                               JsonSerializerOptions options)
      => writer.WriteRawValue(value.ToString(null, CultureInfo.InvariantCulture), skipInputValidation: true);
}
