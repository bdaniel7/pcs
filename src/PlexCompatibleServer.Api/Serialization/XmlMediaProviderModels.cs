using System.Xml.Serialization;

namespace PlexCompatibleServer.Api.Serialization;

/// <summary>
/// /media/providers wraps the full root capability set in a container and then describes a
/// single <c>com.plexapp.plugins.library</c> provider whose <c>Feature type="content"</c>
/// entry lists the libraries as Directories, each carrying the two Pivots the TV client
/// uses to build its navigation model.
/// </summary>
[XmlRoot("MediaContainer")]
public sealed class XmlMediaProviderContainer
{
    [XmlAttribute("size")] public int Size { get; set; }
    [XmlAttribute("allowCameraUpload")] public string? AllowCameraUpload { get; set; }
    [XmlAttribute("allowChannelAccess")] public string? AllowChannelAccess { get; set; }
    [XmlAttribute("allowSharing")] public string? AllowSharing { get; set; }
    [XmlAttribute("allowSync")] public string? AllowSync { get; set; }
    [XmlAttribute("allowTuners")] public string? AllowTuners { get; set; }
    [XmlAttribute("apiVersion")] public string ApiVersion { get; set; } = "";
    [XmlAttribute("backgroundProcessing")] public string? BackgroundProcessing { get; set; }
    [XmlAttribute("companionProxy")] public string? CompanionProxy { get; set; }
    [XmlAttribute("countryCode")] public string? CountryCode { get; set; }
    [XmlAttribute("diagnostics")] public string? Diagnostics { get; set; }
    [XmlAttribute("eventStream")] public string? EventStream { get; set; }
    [XmlAttribute("friendlyName")] public string FriendlyName { get; set; } = "";
    [XmlAttribute("livetv")] public string? LiveTv { get; set; }
    [XmlAttribute("machineIdentifier")] public string MachineIdentifier { get; set; } = "";
    [XmlAttribute("myPlex")] public string? MyPlex { get; set; }
    [XmlAttribute("myPlexMappingState")] public string MyPlexMappingState { get; set; } = "unknown";
    [XmlAttribute("myPlexSigninState")] public string MyPlexSigninState { get; set; } = "none";
    [XmlAttribute("myPlexSubscription")] public string? MyPlexSubscription { get; set; }
    [XmlAttribute("ownerFeatures")] public string? OwnerFeatures { get; set; }
    [XmlAttribute("platform")] public string Platform { get; set; } = "";
    [XmlAttribute("platformVersion")] public string PlatformVersion { get; set; } = "";
    [XmlAttribute("pluginHost")] public string? PluginHost { get; set; }
    [XmlAttribute("pushNotifications")] public string? PushNotifications { get; set; }
    [XmlAttribute("readOnlyLibraries")] public string? ReadOnlyLibraries { get; set; }
    [XmlAttribute("streamingBrainABRVersion")] public string? StreamingBrainAbrVersion { get; set; }
    [XmlAttribute("streamingBrainVersion")] public string? StreamingBrainVersion { get; set; }
    [XmlAttribute("sync")] public string? Sync { get; set; }
    [XmlAttribute("transcoderActiveVideoSessions")] public string? TranscoderActiveVideoSessions { get; set; }
    [XmlAttribute("transcoderAudio")] public string? TranscoderAudio { get; set; }
    [XmlAttribute("transcoderLyrics")] public string? TranscoderLyrics { get; set; }
    [XmlAttribute("transcoderSubtitles")] public string? TranscoderSubtitles { get; set; }
    [XmlAttribute("transcoderVideo")] public string? TranscoderVideo { get; set; }
    [XmlAttribute("transcoderVideoBitrates")] public string? TranscoderVideoBitrates { get; set; }
    [XmlAttribute("transcoderVideoQualities")] public string? TranscoderVideoQualities { get; set; }
    [XmlAttribute("transcoderVideoResolutions")] public string? TranscoderVideoResolutions { get; set; }
    [XmlAttribute("updatedAt")] public string UpdatedAt { get; set; } = "";
    [XmlAttribute("updater")] public string Updater { get; set; } = "";
    [XmlAttribute("version")] public string Version { get; set; } = "";
    [XmlAttribute("voiceSearch")] public string VoiceSearch { get; set; } = "";

    [XmlElement("MediaProvider")]
    public List<XmlMediaProvider> Providers { get; set; } = new();
}

public sealed class XmlMediaProvider
{
    [XmlAttribute("identifier")] public string Identifier { get; set; } = "com.plexapp.plugins.library";
    [XmlAttribute("title")] public string Title { get; set; } = "Library";
    [XmlAttribute("types")] public string Types { get; set; } = "video,audio,photo";
    [XmlAttribute("protocols")] public string Protocols { get; set; } = "stream,download";

    [XmlElement("Feature")]
    public List<XmlProviderFeature> Features { get; set; } = new();
}

public sealed class XmlProviderFeature
{
    [XmlAttribute("flavor")] public string Flavor { get; set; } = "";
    [XmlAttribute("scrobbleKey")] public string ScrobbleKey { get; set; } = "";
    [XmlAttribute("unscrobbleKey")] public string UnscrobbleKey { get; set; } = "";
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "";

    [XmlElement("Directory")]
    public List<XmlProviderDirectory> Directories { get; set; } = new();

    [XmlElement("Action")]
    public List<XmlProviderAction> Actions { get; set; } = new();
}

public sealed class XmlProviderDirectory
{
    [XmlAttribute("hubKey")] public string HubKey { get; set; } = "";
    [XmlAttribute("agent")] public string Agent { get; set; } = "";
    [XmlAttribute("language")] public string Language { get; set; } = "";
    [XmlAttribute("refreshing")] public string Refreshing { get; set; } = "";
    [XmlAttribute("scanner")] public string Scanner { get; set; } = "";
    [XmlAttribute("uuid")] public string Uuid { get; set; } = "";
    [XmlAttribute("id")] public string Id { get; set; } = "";
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "";
    [XmlAttribute("title")] public string Title { get; set; } = "";
    [XmlAttribute("updatedAt")] public string UpdatedAt { get; set; } = "";
    [XmlAttribute("scannedAt")] public string ScannedAt { get; set; } = "";

    [XmlElement("Pivot")]
    public List<XmlProviderPivot> Pivots { get; set; } = new();
}

public sealed class XmlProviderPivot
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "";
    [XmlAttribute("title")] public string Title { get; set; } = "";
    [XmlAttribute("context")] public string Context { get; set; } = "";
    [XmlAttribute("symbol")] public string Symbol { get; set; } = "";
}

public sealed class XmlProviderAction
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
    [XmlAttribute("key")] public string Key { get; set; } = "";
}
