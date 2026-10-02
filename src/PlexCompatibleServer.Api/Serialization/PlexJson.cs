using System.Collections;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;

namespace PlexCompatibleServer.Api.Serialization;

public static class PlexJson
{
    public static string Serialize<T>(T value)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        WriteQuoted(sb, RootName(typeof(T)));
        sb.Append(':');
        WriteObject(sb, value!);
        sb.Append('}');
        return sb.ToString();
    }

    private static string RootName(Type type)
    {
        var attr = type.GetCustomAttribute<XmlRootAttribute>();
        return attr != null ? attr.ElementName : type.Name;
    }

    // Plex's XML and JSON dialects disagree on a few element names. The client parses
    // JSON, so a hub whose rows arrive as "Video" instead of "Metadata" renders no rows.
    private static readonly Dictionary<string, string> JsonElementNames = new()
    {
        ["Video"] = "Metadata"
    };

    // These are 0/1 in the XML dialect but real booleans in JSON.
    // optimizedForStreaming is deliberately absent: official Plex sends it as an integer there,
    // and it is the only int/bool type mismatch left in a rich metadata response.
    private static readonly HashSet<string> BooleanAttributes = new()
    {
        "allowSync", "allowCameraUpload", "claimed", "more", "hidden", "refreshing",
        "has64bitOffsets", "watched", "unwatched", "primaryExtra", "hasPremiumPrimaryExtra",
        "selected", "default", "exists", "accessible",
        "hasScalingMatrix"
    };

    // Metadata sections a movie detail screen asks for via includeExternalMetadata=1. The client
    // reads them unconditionally on that path (it explicitly excludes Actor/Country/Producer for
    // episodes, but not for movies), so omitting them when we have nothing to put in them leaves
    // the detail screen reporting that content could not be loaded. Sending them empty is honest:
    // it says "this server has no scraped metadata for this item".
    private static readonly HashSet<string> EmptyMetadataSections = new(StringComparer.Ordinal)
    {
        "Genre", "Director", "Writer", "Role", "Rating", "Country", "Producer", "Review"
    };

    // Same reasoning for the string-valued scraped fields: an absent key is not the same as an
    // empty one to a client that reads them unconditionally on the movie detail path.
    // audienceRating and contentRatingAge are deliberately NOT here: official Plex types them as
    // float and int, so emitting "" for them would trade a missing key for a type mismatch.
    private static readonly HashSet<string> EmptyMetadataAttributes = new(StringComparer.Ordinal)
    {
        "summary", "tagline", "contentRating", "audienceRatingImage"
    };

    // Real Plex quotes these even though they look numeric, so they must not be swept up by the
    // all-digits heuristic below: "streamIdentifier":"1" and "videoResolution":"720".
    // ratingKey belongs here too: real Plex sends "ratingKey":"826" as a string, and the client
    // calls string methods on it, so a bare number raises a TypeError once the detail screen is
    // already on screen.
    private static readonly HashSet<string> StringValuedAttributes = new(StringComparer.Ordinal)
    {
        "streamIdentifier", "videoResolution", "videoFrameRate", "ratingKey"
    };

    // ...and the mirror image: real Plex emits these unquoted even though the model holds them as
    // strings, so "2.35" must reach the client as 2.35.
    private static readonly HashSet<string> NumericValuedAttributes = new(StringComparer.Ordinal)
    {
        "aspectRatio", "frameRate", "audienceRating", "contentRatingAge"
    };

    // Stream attributes that only apply to one track kind. Real Plex leaves them out entirely when
    // they do not apply - an audio track carries no width/height, a subtitle track no bitDepth - and
    // the models cannot use Nullable<T> because XmlSerializer rejects it on an XmlAttribute. The
    // XML dialect still writes 0; the JSON the client actually parses omits the key.
    private static readonly HashSet<string> ZeroMeansAbsent = new(StringComparer.Ordinal)
    {
        "width", "height", "codedWidth", "codedHeight", "bitDepth", "level", "refFrames",
        "channels", "samplingRate", "bitrate"
    };

    private static string JsonElementName(string xmlName)
        => JsonElementNames.TryGetValue(xmlName, out var jsonName) ? jsonName : xmlName;

    private static void WriteObject(StringBuilder sb, object model)
    {
        var type = model.GetType();
        sb.Append('{');
        var first = true;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;

            var attribute = property.GetCustomAttribute<XmlAttributeAttribute>();
            if (attribute?.AttributeName is { } attrName)
            {
                var raw = property.GetValue(model);
                if (raw is null) continue;
                // Models are non-nullable and default to "", so blank is the "absent" signal.
                if (raw is string text && text.Length == 0)
                {
                    if (!WantsEmptyMetadataSections(model) || !EmptyMetadataAttributes.Contains(attrName)) continue;
                }
                if (type == typeof(XmlStream) && ZeroMeansAbsent.Contains(attrName) && IsZero(raw)) continue;
                WriteSeparator(sb, ref first);
                WriteQuoted(sb, attrName);
                sb.Append(':');
                WriteAttributeScalar(sb, attrName, raw);
                continue;
            }

            var element = property.GetCustomAttribute<XmlElementAttribute>();
            if (element?.ElementName is { } elemName)
            {
                var value = property.GetValue(model);
                if (value is null) continue;
                if (value is not IEnumerable items)
                {
                    WriteSeparator(sb, ref first);
                    WriteQuoted(sb, JsonElementName(elemName));
                    sb.Append(':');
                    WriteObject(sb, value);
                    continue;
                }

                var elements = items.Cast<object>().Where(x => x is not null).ToList();
                if (elements.Count == 0)
                {
                    if (!WantsEmptyMetadataSections(model)) continue;

                    if (EmptyMetadataSections.Contains(elemName))
                    {
                        WriteSeparator(sb, ref first);
                        WriteQuoted(sb, JsonElementName(elemName));
                        sb.Append(":[");
                        sb.Append(']');
                    }
                    continue;
                }

                WriteSeparator(sb, ref first);
                WriteQuoted(sb, JsonElementName(elemName));
                sb.Append(":[");
                for (var i = 0; i < elements.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteObject(sb, elements[i]);
                }
                sb.Append(']');
                continue;
            }
        }

        sb.Append('}');
    }

    // True only for the metadata responses built for an includeExternalMetadata=1 request.
    private static bool WantsEmptyMetadataSections(object model)
        => model is XmlVideo video && video.EmitEmptyMetadataSections;

    // A boxed int does not match a "long l" type pattern, so this cannot be a switch on the boxed
    // value: every numeric arm has to be reached through IConvertible instead.
    private static bool IsZero(object value)
        => value is IConvertible convertible
           && convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture) == 0d;

    // The client deserializes metadata into typed fields and throws on a type mismatch, which
    // surfaces as "content could not be loaded" on the info page even though every request
    // succeeded. The JSON type of each attribute is therefore pinned to what real Plex sends
    // rather than inferred from how the model happens to store it.
    private static void WriteAttributeScalar(StringBuilder sb, string attrName, object value)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;

        if (StringValuedAttributes.Contains(attrName))
        {
            WriteQuoted(sb, Convert.ToString(value, invariant) ?? "");
            return;
        }

        if (BooleanAttributes.Contains(attrName))
        {
            if (value is bool actual) { sb.Append(actual ? "true" : "false"); return; }
            if (value is string flag && (flag == "0" || flag == "1"))
            {
                sb.Append(flag == "1" ? "true" : "false");
                return;
            }
        }

        if (NumericValuedAttributes.Contains(attrName))
        {
            var text = Convert.ToString(value, invariant) ?? "";
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, invariant, out var number))
            {
                sb.Append(number.ToString("R", invariant));
                return;
            }
        }

        WriteScalar(sb, value);
    }

    private static void WriteScalar(StringBuilder sb, object value)
    {
        switch (value)
        {
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                WriteScalarText(sb, s);
                return;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            case double or float or decimal:
                sb.Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                return;
            default:
                WriteScalarText(sb, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                return;
        }
    }

    // Plex emits numeric-looking attributes as JSON numbers, not strings.
    private static void WriteScalarText(StringBuilder sb, string s)
    {
        if (s.Length > 0 && s.All(char.IsAsciiDigit) && long.TryParse(s, out var n))
        {
            sb.Append(n);
            return;
        }
        WriteQuoted(sb, s);
    }

    private static void WriteSeparator(StringBuilder sb, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
    }

    private static void WriteQuoted(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
