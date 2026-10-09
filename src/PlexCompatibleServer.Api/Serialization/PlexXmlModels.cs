using System.Xml.Serialization;

namespace PlexCompatibleServer.Api.Serialization;

public sealed class XmlRootDirectory
{
    [XmlAttribute("count")] public int Count { get; set; }
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("title")] public string Title { get; set; } = "";
}

[XmlRoot("MediaContainer")]
public sealed class XmlServerInfo
{
    [XmlAttribute("size")] public int Size { get; set; }
    // Clients treat an unclaimed server as not ready to serve media, so it has to be present.
    [XmlAttribute("claimed")] public string Claimed { get; set; } = "";
    [XmlAttribute("allowCameraUpload")] public string AllowCameraUpload { get; set; } = "";
    [XmlAttribute("allowChannelAccess")] public string AllowChannelAccess { get; set; } = "";
    [XmlAttribute("allowSharing")] public string AllowSharing { get; set; } = "";
    [XmlAttribute("allowSync")] public string AllowSync { get; set; } = "";
    [XmlAttribute("allowTuners")] public string AllowTuners { get; set; } = "";
    [XmlAttribute("apiVersion")] public string ApiVersion { get; set; } = "";
    [XmlAttribute("backgroundProcessing")] public string BackgroundProcessing { get; set; } = "";
    [XmlAttribute("companionProxy")] public string CompanionProxy { get; set; } = "";
    [XmlAttribute("countryCode")] public string CountryCode { get; set; } = "";
    [XmlAttribute("diagnostics")] public string Diagnostics { get; set; } = "";
    [XmlAttribute("eventStream")] public string EventStream { get; set; } = "";
    [XmlAttribute("friendlyName")] public string FriendlyName { get; set; } = "";
    [XmlAttribute("hubSearch")] public string HubSearch { get; set; } = "";
    [XmlAttribute("itemClusters")] public string ItemClusters { get; set; } = "";
    [XmlAttribute("livetv")] public string LiveTv { get; set; } = "";
    [XmlAttribute("machineIdentifier")] public string MachineIdentifier { get; set; } = "";
    [XmlAttribute("mediaProviders")] public string MediaProviders { get; set; } = "";
    [XmlAttribute("multiuser")] public string Multiuser { get; set; } = "";
    [XmlAttribute("myPlex")] public string MyPlex { get; set; } = "";
    [XmlAttribute("myPlexMappingState")] public string MyPlexMappingState { get; set; } = "unknown";
    [XmlAttribute("myPlexSigninState")] public string MyPlexSigninState { get; set; } = "none";
    [XmlAttribute("myPlexSubscription")] public string MyPlexSubscription { get; set; } = "";
    [XmlAttribute("ownerFeatures")] public string OwnerFeatures { get; set; } = "";
    [XmlAttribute("platform")] public string Platform { get; set; } = "";
    [XmlAttribute("platformVersion")] public string PlatformVersion { get; set; } = "";
    [XmlAttribute("pluginHost")] public string PluginHost { get; set; } = "";
    [XmlAttribute("pushNotifications")] public string PushNotifications { get; set; } = "";
    [XmlAttribute("readOnlyLibraries")] public string ReadOnlyLibraries { get; set; } = "";
    [XmlAttribute("streamingBrainABRVersion")] public string StreamingBrainABRVersion { get; set; } = "";
    [XmlAttribute("streamingBrainVersion")] public string StreamingBrainVersion { get; set; } = "";
    [XmlAttribute("sync")] public string Sync { get; set; } = "";
    [XmlAttribute("transcoderActiveVideoSessions")] public string TranscoderActiveVideoSessions { get; set; } = "";
    [XmlAttribute("transcoderAudio")] public string TranscoderAudio { get; set; } = "";
    [XmlAttribute("transcoderLyrics")] public string TranscoderLyrics { get; set; } = "";
    [XmlAttribute("transcoderPhoto")] public string TranscoderPhoto { get; set; } = "";
    [XmlAttribute("transcoderSubtitles")] public string TranscoderSubtitles { get; set; } = "";
    [XmlAttribute("transcoderVideo")] public string TranscoderVideo { get; set; } = "";
    [XmlAttribute("transcoderVideoBitrates")] public string TranscoderVideoBitrates { get; set; } = "";
    [XmlAttribute("transcoderVideoQualities")] public string TranscoderVideoQualities { get; set; } = "";
    [XmlAttribute("transcoderVideoResolutions")] public string TranscoderVideoResolutions { get; set; } = "";
    [XmlAttribute("updatedAt")] public string UpdatedAt { get; set; } = "";
    [XmlAttribute("updater")] public string Updater { get; set; } = "";
    [XmlAttribute("version")] public string Version { get; set; } = "";
    [XmlAttribute("voiceSearch")] public string VoiceSearch { get; set; } = "";

    [XmlElement("Directory")]
    public List<XmlRootDirectory> Directories { get; set; } = new();
}

/// <summary>
/// Plex keeps /identity deliberately minimal: size, apiVersion, claimed, machineIdentifier, version.
/// The richer per-client fields live on the root and on /media/providers.
/// </summary>
[XmlRoot("MediaContainer")]
public sealed class XmlIdentity
{
    [XmlAttribute("size")] public int Size { get; set; }
    [XmlAttribute("apiVersion")] public string ApiVersion { get; set; } = "1.2.3";
    [XmlAttribute("claimed")] public string Claimed { get; set; } = "0";
    [XmlAttribute("machineIdentifier")] public string MachineIdentifier { get; set; } = "";
    [XmlAttribute("version")] public string Version { get; set; } = "";
}

[XmlRoot("MediaContainer")]
public sealed class XmlMediaContainer
{
    [XmlAttribute("size")] public int Size { get; set; }
    [XmlAttribute("allowSync")] public string AllowSync { get; set; } = "";
    [XmlAttribute("identifier")] public string Identifier { get; set; } = "";
    [XmlAttribute("title1")] public string Title1 { get; set; } = "";
    [XmlAttribute("title2")] public string Title2 { get; set; } = "";
    [XmlAttribute("librarySectionID")] public string LibrarySectionID { get; set; } = "";
    [XmlAttribute("librarySectionTitle")] public string LibrarySectionTitle { get; set; } = "";
    [XmlAttribute("librarySectionUUID")] public string LibrarySectionUUID { get; set; } = "";
    [XmlAttribute("mixedParents")] public string MixedParents { get; set; } = "";
    [XmlAttribute("totalSize")] public string TotalSize { get; set; } = "";
    [XmlAttribute("offset")] public int Offset { get; set; }

    [XmlAttribute("error")] public string Error { get; set; } = "";
    [XmlAttribute("message")] public string Message { get; set; } = "";

    // Blank rather than a default: these three must be absent on responses where real Plex omits
    // them, and a non-nullable value would stamp error="False"/status="200" on every metadata
    // response. Only the error and decision helpers set them.
    [XmlAttribute("status")] public string Status { get; set; } = "";

    // Present on /video/:/transcode/universal/decision responses only.
    [XmlAttribute("mdeDecisionCode")] public string MdeDecisionCode { get; set; } = "";
    [XmlAttribute("mdeDecisionText")] public string MdeDecisionText { get; set; } = "";
    [XmlAttribute("mediaTagPrefix")] public string MediaTagPrefix { get; set; } = "";
    [XmlAttribute("mediaTagVersion")] public string MediaTagVersion { get; set; } = "";
    [XmlAttribute("resourceSession")] public string ResourceSession { get; set; } = "";

    [XmlAttribute("playQueueID")] public string PlayQueueID { get; set; } = "";
    [XmlAttribute("playQueueSelectedItemID")] public string PlayQueueSelectedItemID { get; set; } = "";
    [XmlAttribute("playQueueSelectedItemPlayer")] public string PlayQueueSelectedItemPlayer { get; set; } = "";
    // Play-queue counters. Official Plex omits these from a plain item response, so PlexJson drops
    // them when they are zero - see ZeroMeansAbsentContainer in PlexJson.
    [XmlAttribute("playQueueSelectedItemOffset")] public long PlayQueueSelectedItemOffset { get; set; }
    [XmlAttribute("playQueueSelectedMetadataItemID")] public string PlayQueueSelectedMetadataItemID { get; set; } = "";
    [XmlAttribute("playQueueShuffled")] public string PlayQueueShuffled { get; set; } = "";
    [XmlAttribute("playQueueRepeat")] public string PlayQueueRepeat { get; set; } = "";
    [XmlAttribute("playQueueSourceURI")] public string PlayQueueSourceURI { get; set; } = "";
    [XmlAttribute("playQueueSourceTitle")] public string PlayQueueSourceTitle { get; set; } = "";
    [XmlAttribute("playQueueTotalCount")] public int PlayQueueTotalCount { get; set; }
    [XmlAttribute("playQueueVersion")] public int PlayQueueVersion { get; set; }

    [XmlElement("Directory")]
    public List<XmlDirectory> Directories { get; set; } = new();

    [XmlElement("Video")]
    public List<XmlVideo> Videos { get; set; } = new();
}

public sealed class XmlDirectory
{
    [XmlAttribute("allowSync")] public string AllowSync { get; set; } = "";
    [XmlAttribute("ratingKey")] public string RatingKey { get; set; } = "";
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("title")] public string Title { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "";
    [XmlAttribute("agent")] public string Agent { get; set; } = "";
    [XmlAttribute("scanner")] public string Scanner { get; set; } = "";
    [XmlAttribute("language")] public string Language { get; set; } = "";
    [XmlAttribute("refreshing")] public string Refreshing { get; set; } = "";
    [XmlAttribute("uuid")] public string Uuid { get; set; } = "";
    [XmlAttribute("librarySectionID")] public string LibrarySectionID { get; set; } = "";
    [XmlAttribute("librarySectionTitle")] public string LibrarySectionTitle { get; set; } = "";
    [XmlAttribute("librarySectionKey")] public string LibrarySectionKey { get; set; } = "";
    [XmlAttribute("filters")] public string Filters { get; set; } = "";
    [XmlAttribute("updatedAt")] public string UpdatedAt { get; set; } = "";
    [XmlAttribute("createdAt")] public string CreatedAt { get; set; } = "";
    [XmlAttribute("scannedAt")] public string ScannedAt { get; set; } = "";
    [XmlAttribute("content")] public string Content { get; set; } = "";
    [XmlAttribute("directory")] public string Directory { get; set; } = "";
    [XmlAttribute("contentChangedAt")] public string ContentChangedAt { get; set; } = "";
    [XmlAttribute("hidden")] public string Hidden { get; set; } = "";
    [XmlAttribute("thumb")] public string Thumb { get; set; } = "";

    [XmlElement("Location")]
    public List<XmlLocation> Locations { get; set; } = new();
}

public sealed class XmlLocation
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
    [XmlAttribute("path")] public string Path { get; set; } = "";
}

public sealed class XmlVideo
{
    [XmlAttribute("ratingKey")] public int RatingKey { get; set; }
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("guid")] public string Guid { get; set; } = "";
    [XmlAttribute("slug")] public string Slug { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "movie";
    [XmlAttribute("title")] public string Title { get; set; } = "";
    [XmlAttribute("titleSort")] public string TitleSort { get; set; } = "";

    // Episode hierarchy: season/episode numbers plus the show/season breadcrumb the info screen
    // renders above the episode title. Display strings only - the parent/grandparent KEY and
    // RATINGKEY attributes are deliberately absent: they would carry plex.tv's foreign rating
    // keys and the client follows them (/library/metadata/{key}/children), which cannot resolve
    // locally. Left empty on movies (omitted from JSON, empty attributes in XML).
    [XmlAttribute("index")] public string Index { get; set; } = "";
    [XmlAttribute("parentIndex")] public string ParentIndex { get; set; } = "";
    [XmlAttribute("parentTitle")] public string ParentTitle { get; set; } = "";
    [XmlAttribute("parentType")] public string ParentType { get; set; } = "";
    [XmlAttribute("grandparentTitle")] public string GrandparentTitle { get; set; } = "";
    [XmlAttribute("grandparentType")] public string GrandparentType { get; set; } = "";

    // Season/show poster pointers, emitted only when official artwork has been downloaded for
    // the episode: the season grid has no row of its own, so it draws from its episodes'
    // parentThumb (season art) and the show cards from grandparentThumb (show art). Left empty
    // on movies (omitted from JSON, empty attribute in XML).
    [XmlAttribute("parentThumb")] public string ParentThumb { get; set; } = "";
    [XmlAttribute("grandparentThumb")] public string GrandparentThumb { get; set; } = "";

    /// <summary>Set on items returned inside a play queue; distinct from the library rating key.</summary>
    [XmlAttribute("playQueueItemID")] public string PlayQueueItemID { get; set; } = "";
    [XmlAttribute("studio")] public string Studio { get; set; } = "";
    [XmlAttribute("year")] public string Year { get; set; } = "";
    [XmlAttribute("summary")] public string Summary { get; set; } = "";
    [XmlAttribute("tagline")] public string Tagline { get; set; } = "";
    [XmlAttribute("librarySectionID")] public string LibrarySectionID { get; set; } = "";
    [XmlAttribute("librarySectionTitle")] public string LibrarySectionTitle { get; set; } = "";
    [XmlAttribute("librarySectionKey")] public string LibrarySectionKey { get; set; } = "";
    [XmlAttribute("thumb")] public string Thumb { get; set; } = "";
    [XmlAttribute("art")] public string Art { get; set; } = "";
    [XmlAttribute("duration")] public int Duration { get; set; }
    [XmlAttribute("viewOffset")] public string ViewOffset { get; set; } = "";
    [XmlAttribute("viewCount")] public string ViewCount { get; set; } = "";
    [XmlAttribute("lastViewedAt")] public string LastViewedAt { get; set; } = "";
    [XmlAttribute("addedAt")] public long AddedAt { get; set; }
    [XmlAttribute("updatedAt")] public long UpdatedAt { get; set; }
    [XmlAttribute("audienceRating")] public string AudienceRating { get; set; } = "";
    [XmlAttribute("audienceRatingImage")] public string AudienceRatingImage { get; set; } = "";
    [XmlAttribute("contentRating")] public string ContentRating { get; set; } = "";
    // Nullable numerics must stay strings: XmlSerializer cannot put Nullable<T> on an XmlAttribute.
    [XmlAttribute("contentRatingAge")] public string ContentRatingAge { get; set; } = "";
    [XmlAttribute("originallyAvailableAt")] public string OriginallyAvailableAt { get; set; } = "";
    // Official Plex states this on related-hub rows ("media") but not on plain metadata items;
    // left empty it serialises as absent, which is what the item responses want.
    [XmlAttribute("chapterSource")] public string ChapterSource { get; set; } = "";
    [XmlAttribute("hasPremiumPrimaryExtra")] public string HasPremiumPrimaryExtra { get; set; } = "";

    [XmlElement("Media")]
    public List<XmlMedia> Media { get; set; } = new();

    [XmlElement("Image")]
    public List<XmlImage> Images { get; set; } = new();

    [XmlElement("UltraBlurColors")]
    public XmlUltraBlurColors? UltraBlurColors { get; set; }

    [XmlElement("Genre")]
    public List<XmlTag> Genres { get; set; } = new();

    [XmlElement("Director")]
    public List<XmlTag> Directors { get; set; } = new();

    [XmlElement("Writer")]
    public List<XmlTag> Writers { get; set; } = new();

    [XmlElement("Role")]
    public List<XmlTag> Roles { get; set; } = new();

    [XmlElement("Rating")]
    public List<XmlRating> Ratings { get; set; } = new();

    [XmlElement("Country")]
    public List<XmlTag> Countries { get; set; } = new();

    [XmlElement("Producer")]
    public List<XmlTag> Producers { get; set; } = new();

    [XmlElement("Review")]
    public List<XmlTag> Reviews { get; set; } = new();

    [XmlElement("CommonSenseMedia")]
    public List<XmlCommonSenseMedia> CommonSenseMedia { get; set; } = new();

    /// <summary>
    /// Present-but-empty extras container. Official Plex emits this whenever the request carried
    /// includeExtras=1, even with nothing to show.
    /// </summary>
    [XmlElement("Extras")]
    public XmlExtras? Extras { get; set; }

    /// <summary>
    /// Set when the client asked for external metadata (includeExternalMetadata=1) so that the
    /// scraped-metadata sections are serialised as empty arrays rather than omitted. Not an XML
    /// attribute: it steers serialisation only.
    /// </summary>
    [XmlIgnore]
    public bool EmitEmptyMetadataSections { get; set; }

    /// <summary>
    /// Source GUIDs. Real Plex emits this array on every metadata item even when the file has no
    /// scraped metadata, so a client reading Guid[0] finds nothing when it is missing.
    /// </summary>
    [XmlElement("Guid")]
    public List<XmlGuid> Guids { get; set; } = new();
}

/// <summary>Child element form of a source GUID, e.g. imdb://tt0087538.</summary>
public sealed class XmlGuid
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
}

/// <summary>Tag-shaped child element used by Plex for genres, directors, writers and roles.</summary>
public sealed class XmlTag
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
    [XmlAttribute("filter")] public string Filter { get; set; } = "";
    [XmlAttribute("tag")] public string Tag { get; set; } = "";
    [XmlAttribute("tagKey")] public string? TagKey { get; set; }
    [XmlAttribute("thumb")] public string? Thumb { get; set; }
    [XmlAttribute("role")] public string? Role { get; set; }
}

/// <summary>Score entry, e.g. image="imdb://image.rating" value=6.2 type="audience".</summary>
public sealed class XmlRating
{
    [XmlAttribute("image")] public string Image { get; set; } = "";
    [XmlAttribute("value")] public string Value { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "";
}

/// <summary>The extras container, emitted with size=0 when a library has no extra clips.</summary>
public sealed class XmlExtras
{
    [XmlAttribute("size")] public string Size { get; set; } = "0";
}

/// <summary>Dominant colours the client uses to tint blurred backgrounds behind an item.</summary>
public sealed class XmlUltraBlurColors
{
    [XmlAttribute("topLeft")] public string TopLeft { get; set; } = "";
    [XmlAttribute("topRight")] public string TopRight { get; set; } = "";
    [XmlAttribute("bottomRight")] public string BottomRight { get; set; } = "";
    [XmlAttribute("bottomLeft")] public string BottomLeft { get; set; } = "";
}

/// <summary>
/// The Common Sense Media age-rating advisory a scraped agent attaches to an item, e.g.
/// oneLiner="Intense, bloody, ..." with AgeRating=[{type:"official", rating:2, age:14}].
/// </summary>
public sealed class XmlCommonSenseMedia
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
    [XmlAttribute("oneLiner")] public string OneLiner { get; set; } = "";

    [XmlElement("AgeRating")]
    public List<XmlAgeRating> AgeRatings { get; set; } = new();
}

public sealed class XmlAgeRating
{
    [XmlAttribute("type")] public string Type { get; set; } = "";
    [XmlAttribute("rating")] public int Rating { get; set; }
    [XmlAttribute("age")] public int Age { get; set; }
}

public sealed class XmlImage
{
    [XmlAttribute("alt")] public string Alt { get; set; } = "";
    [XmlAttribute("type")] public string Type { get; set; } = "";
    [XmlAttribute("url")] public string Url { get; set; } = "";
}

public sealed class XmlMedia
{
    [XmlAttribute("id")] public int Id { get; set; }
    [XmlAttribute("duration")] public int Duration { get; set; }
    [XmlAttribute("bitrate")] public int Bitrate { get; set; }
    [XmlAttribute("width")] public int Width { get; set; }
    [XmlAttribute("height")] public int Height { get; set; }
    [XmlAttribute("aspectRatio")] public string AspectRatio { get; set; } = "";
    [XmlAttribute("audioChannels")] public int AudioChannels { get; set; }
    [XmlAttribute("audioCodec")] public string AudioCodec { get; set; } = "";
    [XmlAttribute("audioProfile")] public string AudioProfile { get; set; } = "";
    [XmlAttribute("videoCodec")] public string VideoCodec { get; set; } = "";
    [XmlAttribute("videoResolution")] public string VideoResolution { get; set; } = "";
    [XmlAttribute("videoFrameRate")] public string VideoFrameRate { get; set; } = "";
    [XmlAttribute("videoProfile")] public string VideoProfile { get; set; } = "";
    [XmlAttribute("container")] public string Container { get; set; } = "";
    [XmlAttribute("optimizedForStreaming")] public string OptimizedForStreaming { get; set; } = "";
    [XmlAttribute("selected")] public string Selected { get; set; } = "";
    [XmlAttribute("has64bitOffsets")] public string Has64bitOffsets { get; set; } = "";
    [XmlAttribute("partCount")] public int PartCount { get; set; }

    [XmlElement("Part")]
    public List<XmlPart> Parts { get; set; } = new();

    [XmlElement("Stream")]
    public List<XmlStream> Streams { get; set; } = new();
}

public sealed class XmlStream
{
    [XmlAttribute("id")] public string Id { get; set; } = "";
    /// <summary>1 = video, 2 = audio, 3 = subtitle.</summary>
    [XmlAttribute("streamType")] public int StreamType { get; set; }
    [XmlAttribute("index")] public int Index { get; set; }
    [XmlAttribute("codec")] public string Codec { get; set; } = "";
    [XmlAttribute("width")] public int Width { get; set; }
    [XmlAttribute("height")] public int Height { get; set; }
    [XmlAttribute("codedWidth")] public int CodedWidth { get; set; }
    [XmlAttribute("codedHeight")] public int CodedHeight { get; set; }
    [XmlAttribute("bitrate")] public int Bitrate { get; set; }
    [XmlAttribute("channels")] public int Channels { get; set; }
    [XmlAttribute("samplingRate")] public int SamplingRate { get; set; }
    // Must stay a string: XmlSerializer cannot put Nullable<T> on an XmlAttribute.
    [XmlAttribute("frameRate")] public string FrameRate { get; set; } = "";
    [XmlAttribute("profile")] public string Profile { get; set; } = "";
    [XmlAttribute("scanType")] public string ScanType { get; set; } = "";
    [XmlAttribute("language")] public string Language { get; set; } = "";
    [XmlAttribute("languageCode")] public string LanguageCode { get; set; } = "";
    [XmlAttribute("languageTag")] public string LanguageTag { get; set; } = "";
    [XmlAttribute("format")] public string Format { get; set; } = "";
    [XmlAttribute("title")] public string Title { get; set; } = "";
    [XmlAttribute("file")] public string File { get; set; } = "";
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("location")] public string Location { get; set; } = "";
    [XmlAttribute("displayTitle")] public string DisplayTitle { get; set; } = "";
    [XmlAttribute("extendedDisplayTitle")] public string ExtendedDisplayTitle { get; set; } = "";
    [XmlAttribute("default")] public string Default { get; set; } = "";
    [XmlAttribute("selected")] public string Selected { get; set; } = "";
    [XmlAttribute("streamIdentifier")] public string StreamIdentifier { get; set; } = "";
    [XmlAttribute("bitDepth")] public int BitDepth { get; set; }
    [XmlAttribute("level")] public int Level { get; set; }
    [XmlAttribute("refFrames")] public int RefFrames { get; set; }
    [XmlAttribute("chromaLocation")] public string ChromaLocation { get; set; } = "";
    [XmlAttribute("chromaSubsampling")] public string ChromaSubsampling { get; set; } = "";
    [XmlAttribute("hasScalingMatrix")] public string HasScalingMatrix { get; set; } = "";
    [XmlAttribute("requiredBandwidths")] public string RequiredBandwidths { get; set; } = "";
}

public sealed class XmlPart
{
    [XmlAttribute("id")] public int Id { get; set; }
    [XmlAttribute("key")] public string Key { get; set; } = "";
    [XmlAttribute("duration")] public int Duration { get; set; }
    [XmlAttribute("file")] public string File { get; set; } = "";
    [XmlAttribute("container")] public string Container { get; set; } = "";
    [XmlAttribute("size")] public long Size { get; set; }
    [XmlAttribute("optimizedForStreaming")] public string OptimizedForStreaming { get; set; } = "";
    [XmlAttribute("bitrate")] public int Bitrate { get; set; }
    [XmlAttribute("deepAnalysisVersion")] public int DeepAnalysisVersion { get; set; }
    [XmlAttribute("audioProfile")] public string AudioProfile { get; set; } = "";
    [XmlAttribute("videoProfile")] public string VideoProfile { get; set; } = "";
    [XmlAttribute("has64bitOffsets")] public string Has64bitOffsets { get; set; } = "";
    [XmlAttribute("requiredBandwidths")] public string RequiredBandwidths { get; set; } = "";
    [XmlAttribute("decision")] public string Decision { get; set; } = "";
    [XmlAttribute("selected")] public string Selected { get; set; } = "";
    /// <summary>
    /// Real Plex states whether the backing file is present and readable. Clients use exists to
    /// decide whether the item is playable at all, so leaving it off makes a healthy file look
    /// missing ("content could not be loaded").
    /// </summary>
    [XmlAttribute("exists")] public string Exists { get; set; } = "";
    [XmlAttribute("accessible")] public string Accessible { get; set; } = "";

    [XmlElement("Stream")]
    public List<XmlStream> Streams { get; set; } = new();
}
