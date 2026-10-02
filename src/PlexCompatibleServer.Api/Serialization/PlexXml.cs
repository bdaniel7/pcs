using System.Text;
using System.Xml.Serialization;

namespace PlexCompatibleServer.Api.Serialization;

public static class PlexXml
{
    public static string Serialize<T>(T value)
    {
        var serializer = new XmlSerializer(typeof(T));
        var ns = new XmlSerializerNamespaces();
        ns.Add("", "");
        using var sw = new Utf8StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        serializer.Serialize(sw, value, ns);
        return sw.ToString();
    }

    public static string PlatformName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return "Unknown";
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(System.Globalization.CultureInfo culture) : base(culture) { }

        public override Encoding Encoding => Encoding.UTF8;
    }
}
