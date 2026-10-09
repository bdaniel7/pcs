using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Builds the <see cref="XmlVideo"/> projections served by the library, hub, metadata, video and
/// play-queue endpoints. Pulled out of <see cref="LibraryController"/> so the controller keeps to
/// routing/paging and the mapping (slug/guid derivation and per-stream shaping) lives in one place.
/// </summary>
internal static class VideoMapper
{
    internal static XmlVideo ToVideo(
        MediaItem x,
        bool includeLibrarySection = true,
        StreamSelectionStore? selections = null)
    {
        var timestamp = x.UpdatedAt.ToUnixTimeSeconds();
        var extension = Path.GetExtension(x.FilePath).TrimStart('.').ToLowerInvariant();
        var container = string.IsNullOrEmpty(x.Container) ? extension : x.Container;
        var type = x.Library.Type == LibraryType.Movie ? "movie" : "episode";
        var guid = $"plex://{type}/{stableGuidHex(x)}";
        var slug = uniqueSlug(x.Title, guid);
        var streams = readStreams(x, selections);

        var part = new XmlPart
        {
            Id = x.Id,
            Key = $"/library/parts/{x.Id}/{timestamp}/file.{extension}",
            Duration = x.DurationMs ?? 0,
            File = x.FilePath,
            Container = container,
            Size = x.FileSize,
            Bitrate = x.Bitrate,
            DeepAnalysisVersion = 6,
            AudioProfile = x.AudioCodec == "aac" ? "lc" : "",
            VideoProfile = (x.VideoProfile ?? "").ToLowerInvariant(),
            OptimizedForStreaming = "1",
            Selected = "1",
            // Real Plex states this on the part as well as the media element. The JSON serializer
            // treats an unset (empty) attribute as absent, so it has to be set explicitly here.
            Has64BitOffsets = "0",
            // The scanner only records items it found on disk and stores their length, so a non-zero
            // FileSize means the file is present and readable. The client treats a missing exists as
            // "not available" and refuses to play.
            Exists = x.FileSize > 0 ? "1" : "0",
            Accessible = x.FileSize > 0 ? "1" : "0"
        };

        // Every track - video, audio and subtitle - is published on the part. Real Plex has no
        // Media-level Stream list, and clients read Part[].Stream to work out whether they can
        // play the file; parking the video stream on Media instead leaves the part with audio only,
        // which the client treats as unloadable.
        part.Streams.AddRange(streams);

        var media = new XmlMedia
        {
            Id = x.Id,
            Duration = x.DurationMs ?? 0,
            Bitrate = x.Bitrate,
            Width = x.Width,
            Height = x.Height,
            AspectRatio = MediaFormatting.AspectRatio(x.Width, x.Height),
            AudioChannels = x.AudioChannels,
            AudioCodec = x.AudioCodec ?? "",
            AudioProfile = x.AudioCodec == "aac" ? "lc" : "",
            VideoCodec = x.VideoCodec ?? "",
            VideoResolution = MediaFormatting.VideoResolution(x.Width),
            VideoFrameRate = MediaFormatting.FrameRate(x.FrameRate),
            VideoProfile = (x.VideoProfile ?? "").ToLowerInvariant(),
            Container = container,
            OptimizedForStreaming = "1",
            Selected = "1",
            // Real Plex reports false here even for libraries holding large files, so the client
            // clearly does not gate on it. Matching it keeps the JSON shape identical.
            Has64BitOffsets = "0",
            PartCount = 1
        };

        media.Parts.Add(part);

        return new XmlVideo
        {
            RatingKey = x.Id,
            Key = $"/library/metadata/{x.Id}",
            Guid = guid,
            Guids =
            {
                new XmlGuid { Id = guid }
            },
            Slug = slug,
            Type = type,
            Title = x.Title,
            TitleSort = x.SortTitle ?? "",
            Studio = x.Library.Name,
            Year = x.Year is > 0 ? x.Year.Value.ToString() : "",
            Summary = x.Summary ?? "",
            LibrarySectionId = includeLibrarySection ? x.LibraryId.ToString() : "",
            LibrarySectionTitle = includeLibrarySection ? x.Library.Name : "",
            LibrarySectionKey = includeLibrarySection ? $"/library/sections/{x.LibraryId}" : "",
            Thumb = $"/library/metadata/{x.Id}/thumb/{timestamp}",
            Art = $"/library/metadata/{x.Id}/art/{timestamp}",
            // Stated only once the official poster has actually been downloaded: an attr pointing
            // at a route that has nothing to serve would leave the season/show card blank where
            // the frame extract could still be used.
            ParentThumb = !string.IsNullOrEmpty(x.OfficialParentPosterPath) ||
                          !string.IsNullOrEmpty(x.OfficialGrandparentPosterPath)
                ? $"/library/metadata/{x.Id}/parentThumb/{timestamp}"
                : "",
            GrandparentThumb = !string.IsNullOrEmpty(x.OfficialGrandparentPosterPath)
                ? $"/library/metadata/{x.Id}/grandparentThumb/{timestamp}"
                : "",
            Duration = x.DurationMs ?? 0,
            // Playback progress: empty (= absent in JSON) until the viewer has actually watched
            // part of the item. This is what the client reads to offer "Resume from ..." and to
            // draw the progress ring on grid cards.
            ViewOffset = x.ViewOffset is > 0 ? x.ViewOffset.Value.ToString() : "",
            ViewCount = x.ViewCount > 0 ? x.ViewCount.ToString() : "",
            LastViewedAt = x.LastViewedAt is { } viewed ? viewed.ToUnixTimeSeconds().ToString() : "",
            AddedAt = timestamp,
            UpdatedAt = timestamp,
            Images =
            {
                new XmlImage { Alt = x.Title, Type = "coverPoster", Url = $"/library/metadata/{x.Id}/thumb/{timestamp}" },
                new XmlImage { Alt = x.Title, Type = "background", Url = $"/library/metadata/{x.Id}/art/{timestamp}" },
                new XmlImage { Alt = x.Title, Type = "backgroundSquare", Url = $"/library/metadata/{x.Id}/squareArt/{timestamp}" }
            },
            Media = { media }
        };
    }

    /// <summary>
    /// A list row: the generated metadata overlaid with the cached official sidecar record, so
    /// grid cards show the real title instead of the raw file name. The overlay is served strictly
    /// from cache (no plex.tv fallback) because the client re-requests lists constantly and the
    /// backfill keeps them covered; a miss simply keeps the file-name title.
    /// </summary>
    internal static XmlVideo ToVideoEnriched(
        IMetadataService metadata,
        MediaItem x,
        bool includeLibrarySection = true,
        StreamSelectionStore? selections = null)
    {
        var video = ToVideo(x, includeLibrarySection, selections);
        try
        {
            var rec = metadata.ResolveLocal(x, video.Title, video.TitleSort);
            if (rec is not null) MetadataMapper.Overlay(x, video, rec);
        }
        catch (Exception)
        {
            // A broken record must not take the whole grid down; the file-name title is the fallback.
        }
        return video;
    }

    private static List<XmlStream> readStreams(MediaItem x, StreamSelectionStore? selections)
    {
        // The viewer's own pick, written through PUT /library/parts/{id}. The client re-reads
        // this list right after choosing and looks for the track flagged selected, so it is the
        // only thing that ever flags a subtitle (or moves the audio choice off the first track).
        var (selectedAudio, selectedSubtitle) = selections?.Get(x.Id) ?? (0, 0);

        // An unprobed file still gets its sidecar tracks: the video/audio detail ffprobe would
        // have supplied is missing, but the .srt files on disk are discoverable on their own.
        List<MediaStreamInfo> parsed = [];

        if (!string.IsNullOrWhiteSpace(x.StreamsJson))
        {
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<List<MediaStreamInfo>>(x.StreamsJson) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                parsed = [];
            }
        }

        var result = new List<XmlStream>();
        var index = 0;
        var identifier = 1;
        var perKind = new Dictionary<int, int>();

        foreach (var s in parsed)
        {
            var order = perKind.GetValueOrDefault(s.StreamType);
            perKind[s.StreamType] = order + 1;

            var stream = new XmlStream
            {
                // Real Plex gives every stream a server-assigned id; deterministic from the item and
                // stream position so it stays stable across requests.
                Id = (x.Id * 1000 + index).ToString(),
                StreamType = s.StreamType,
                Index = index++,
                Codec = s.Codec ?? "",
                Width = s.Width ?? 0,
                Height = s.Height ?? 0,
                CodedWidth = s.Width ?? 0,
                CodedHeight = s.Height ?? 0,
                Bitrate = s.Bitrate is > 0 ? s.Bitrate.Value / 1000 : 0,
                Channels = s.Channels ?? 0,
                SamplingRate = s.SamplingRate ?? 0,
                FrameRate = MediaFormatting.StreamFrameRate(s.FrameRate ?? 0),
                Location = s.Location ?? "",
                // The menu shows language, not codes: a track tagged "eng" has to read as
                // "English", and languageTag has to be the two-letter form clients group by.
                Language = SidecarSubtitles.DisplayName(s.Language, s.LanguageCode),
                LanguageCode = s.LanguageCode ?? "",
                LanguageTag = SidecarSubtitles.LanguageTag(s.LanguageCode),
                StreamIdentifier = (identifier++).ToString(),
                RequiredBandwidths = MediaFormatting.Bandwidths(s.Bitrate is > 0 ? s.Bitrate.Value / 1000 : 0),
                // One video and one audio track are flagged as the pair in use. A subtitle is
                // never pre-selected: a selected track makes the client load it during startup,
                // and a track it cannot load turns into a burn-in request this server cannot serve.
                Default = order == 0 && s.StreamType != 3 ? "1" : "",
                Selected = order == 0 && s.StreamType != 3 ? "1" : ""
            };

            switch (s.StreamType)
            {
                case 1:
                    // The item row is the authoritative source for display dimensions. Stored
                    // StreamsJson can predate width/height being recorded on the video stream, and a
                    // video stream with no width/height leaves the client unable to judge the file.
                    if (stream.Width == 0) stream.Width = x.Width;
                    if (stream.Height == 0) stream.Height = x.Height;
                    stream.CodedWidth = stream.Width;
                    stream.CodedHeight = stream.Height;
                    stream.Profile = "high";
                    stream.ScanType = "progressive";
                    stream.BitDepth = 8;
                    stream.ChromaLocation = "left";
                    stream.ChromaSubsampling = "4:2:0";
                    stream.HasScalingMatrix = "0";
                    // H.264 level and reference frame count are part of the profile the client
                    // matches its decoder against; an info page that omits them renders as
                    // "content could not be loaded".
                    stream.Level = MediaFormatting.H264Level(x.Width, x.Height, x.FrameRate);
                    stream.RefFrames = 3;
                    stream.DisplayTitle = MediaFormatting.VideoResolution(x.Width) != "" ? $"{MediaFormatting.VideoResolution(x.Width)}p" : "";
                    stream.ExtendedDisplayTitle = stream.DisplayTitle != "" ? $"{stream.DisplayTitle} ({s.Codec!.ToUpperInvariant()})" : "";
                    break;

                case 2:
                    if (stream.SamplingRate == 0) stream.SamplingRate = 48000;
                    // The label has to reflect the real channel layout. Calling a 6-channel
                    // track "Stereo" makes the client pick the wrong output and can stop it
                    // playing the file at all.
                    stream.DisplayTitle = $"{s.Codec!.ToUpperInvariant()} {channelLabel(stream.Channels)}";
                    stream.ExtendedDisplayTitle = stream.DisplayTitle;
                    break;

                case 3:
                    // A stored sidecar marker only ever came from an older scan: external tracks
                    // are rebuilt from the directory listing below, one per .srt file, so the
                    // stale row is dropped rather than published twice.
                    if (string.Equals(s.Location, "sidecar-subs", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (stream.Language.Length == 0) stream.Language = s.Codec ?? "";

                    if (EmbeddedSubtitles.IsTextCodec(s.Codec))
                    {
                        // Text inside the container is published exactly like an external track
                        // and extracted to SRT when the client fetches the key: a direct-playing
                        // client cannot read a subtitle track out of the file itself, and a
                        // track with no key leaves it with nothing to load at all.
                        stream.Codec = "srt";
                        stream.Format = "srt";
                        stream.Location = "sidecar-subs";
                        stream.Key = $"/library/streams/{stream.Id}";
                        stream.DisplayTitle = stream.Language;
                        stream.ExtendedDisplayTitle = $"{stream.Language} (SRT Internal)";
                    }
                    else
                    {
                        // Bitmap subtitles carry no text to extract; they stay inside the
                        // container with no key, exactly as real Plex publishes them.
                        stream.Location = "direct";
                        stream.Format = s.Codec ?? "";
                        stream.DisplayTitle = stream.Language;
                        stream.ExtendedDisplayTitle =
                            $"{stream.Language} ({stream.Format.ToUpperInvariant()} Internal)";
                    }
                    break;
            }

            if (s.StreamType == 2 && selectedAudio != 0)
                stream.Selected = stream.Id == selectedAudio.ToString() ? "1" : "";
            else if (s.StreamType == 3)
                stream.Selected = stream.Id == selectedSubtitle.ToString() ? "1" : "";

            result.Add(stream);
        }

        // One track per external .srt file next to the video. The stream id carries the item id
        // in its high digits, which is how /library/streams/{id} finds the file again when the
        // client fetches the track through the key published here.
        var defaulted = false;
        foreach (var sub in SidecarSubtitles.Find(x.FilePath))
        {
            var sidecarId = x.Id * 1000 + result.Count;
            var title = sub.Forced ? $"{sub.Language} (Forced)"
                : sub.Caption ? $"{sub.Language} (SDH)"
                : sub.Language;

            var sidecar = new XmlStream
            {
                Id = sidecarId.ToString(),
                StreamType = 3,
                Index = result.Count,
                // Real Plex reports external SRT files as codec="srt". The ffprobe spelling
                // "subrip" is what a client checks against its supported-codec list, and a
                // mismatch there is what leaves a selected track never fetched at all.
                Codec = "srt",
                Format = "srt",
                Location = "sidecar-subs",
                Key = $"/library/streams/{sidecarId}",
                StreamIdentifier = (identifier++).ToString(),
                Language = sub.Language,
                LanguageCode = sub.LanguageCode,
                LanguageTag = sub.LanguageTag,
                DisplayTitle = title,
                ExtendedDisplayTitle = $"{title} (SRT External)",
                // Marked as the track the menu opens on, but not selected: a selected subtitle
                // makes the client load it during startup, and real Plex only selects one the
                // viewer has actually chosen - which is exactly what the selection store records.
                Default = !sub.Forced && !defaulted ? "1" : "",
                Selected = sidecarId == selectedSubtitle ? "1" : ""
            };

            defaulted |= sidecar.Default == "1";
            result.Add(sidecar);
        }

        return result;
    }

    /// <summary>
    /// Renders a channel count the way Plex names audio tracks: 2 is "Stereo", 6 is "5.1", and so on.
    /// </summary>
    private static string channelLabel(int channels) => channels switch
    {
        <= 0 => "",
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} Channels"
    };

    /// <summary>Mirrors Plex's URL-safe title slug, e.g. "The End of Oak Street" becomes "the-end-of-oak-street".</summary>
    private static string slugify(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();

        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// A slug identifies one item, and clients key cached detail pages by it. Deriving it from the
    /// title alone makes two records for the same file collide: the same movie in a Movies library
    /// and as an episode in a TV Shows library both slugify to the same string, so the second one
    /// opened overwrites the first one's cached detail page. The discriminator is derived from the
    /// item's own guid, which already differs between the two records.
    /// </summary>
    private static string uniqueSlug(string title, string guid)
    {
        var baseSlug = slugify(title);
        if (baseSlug.Length == 0) baseSlug = "item";

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(guid));

        return $"{baseSlug}-{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}";
    }

    /// <summary>
    /// Real Plex guids are <c>plex://{type}/</c> followed by exactly 24 lowercase hex characters,
    /// and the same item must present the same guid on every response and after every restart:
    /// the movie info screen reads the guid and rejects anything that is not shaped like a real
    /// one ("content could not be loaded"), so a short id or a guid that changes between requests
    /// breaks the detail page. Derived from the file path, which is stable across rescans - a
    /// string hash cannot be used because .NET randomises it per process.
    /// </summary>
    private static string stableGuidHex(MediaItem x)
    {
        var key = string.IsNullOrEmpty(x.FilePath) ? $"{x.LibraryId}/{x.Id}/{x.Title}" : x.FilePath;
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
    }
}
