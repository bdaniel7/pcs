using System.Xml.Serialization;

namespace PlexCompatibleServer.Api.Serialization;

[XmlRoot("MediaContainer")]
public sealed class XmlStatusContainer
{
    [XmlAttribute("size")] public int Size { get; set; }

    [XmlElement("Metadata")]
    public List<XmlStatusMetadata> Metadata { get; set; } = new();
}

public sealed class XmlStatusMetadata
{
    [XmlAttribute("accountID")] public int AccountId { get; set; }
    [XmlAttribute("machineIdentifier")] public string MachineIdentifier { get; set; } = "";
    [XmlAttribute("state")] public string State { get; set; } = "stopped";
}
