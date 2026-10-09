using System.Xml.Serialization;

namespace PlexCompatibleServer.Api.Serialization;

/// <summary>
/// /hubs is what the TV home screen is built from. The container carries
/// <c>identifier="com.plexapp.plugins.library"</c> and every Hub needs both a
/// navigational <c>key</c> and a stable <c>hubIdentifier</c>; without them the client
/// re-polls the endpoint instead of rendering rows.
/// </summary>
[XmlRoot("MediaContainer")]
public sealed class XmlHubContainer
{
    [XmlAttribute("size")] public int Size { get; set; }
    [XmlAttribute("allowSync")] public string AllowSync { get; set; } = "1";
    [XmlAttribute("identifier")] public string Identifier { get; set; } = "com.plexapp.plugins.library";

    [XmlElement("Hub")]
    public List<XmlHub> Hubs { get; set; } = new();
}

/// <summary>
/// Section-scoped hubs add the library coordinates so the client can title the screen
/// and keep filtering while the user browses a single library.
/// </summary>
[XmlRoot("MediaContainer")]
public sealed class XmlSectionHubContainer
{
    [XmlAttribute("size")] public int Size { get; set; }
    [XmlAttribute("allowSync")] public string AllowSync { get; set; } = "1";
    [XmlAttribute("identifier")] public string Identifier { get; set; } = "com.plexapp.plugins.library";
    [XmlAttribute("librarySectionID")] public string LibrarySectionId { get; set; } = "";
    [XmlAttribute("librarySectionTitle")] public string LibrarySectionTitle { get; set; } = "";
    [XmlAttribute("librarySectionUUID")] public string? LibrarySectionUuid { get; set; }

    [XmlElement("Hub")]
    public List<XmlHub> Hubs { get; set; } = new();
}

public sealed class XmlHub
{
    /// <summary>Comma-separated <c>/library/metadata/{ratingKey}</c> paths of the items in this hub.</summary>
    [XmlAttribute("hubKey")] public string? HubKey { get; set; }
    [XmlAttribute("key")] public string? Key { get; set; }
    [XmlAttribute("title")] public string Title { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "mixed";
    [XmlAttribute("hubIdentifier")] public string? HubIdentifier { get; set; }
    [XmlAttribute("context")] public string Context { get; set; } = "hub.home";
    [XmlAttribute("size")] public int Size { get; set; }
    [XmlAttribute("more")] public string More { get; set; } = "0";

    [XmlElement("Video")]
    public List<XmlVideo> Videos { get; set; } = new();

    [XmlElement("Directory")]
    public List<XmlDirectory> Directories { get; set; } = new();
}
