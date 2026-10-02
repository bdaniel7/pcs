using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Hosted;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class LibraryController : ControllerBase
{
    private readonly IMediaRepository _repo;
    private readonly MediaScanTrigger _trigger;
    private readonly ServerOptions _options;

    public LibraryController(IMediaRepository repo, MediaScanTrigger trigger, ServerOptions options)
    {
        _repo = repo;
        _trigger = trigger;
        _options = options;
    }

    [HttpGet("/library")]
    [HttpGet("/library/sections")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Sections(CancellationToken ct)
    {
        var libraries = await _repo.GetLibrariesAsync(ct);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = new XmlMediaContainer
        {
            Size = libraries.Count,
            AllowSync = "0",
            Title1 = "Plex Library",
            Directories = libraries.Select(x => new XmlDirectory
            {
                AllowSync = "0",
                Filters = "1",
                RatingKey = x.Id.ToString(),
                Key = x.Id.ToString(),
                Title = x.Name,
                Type = x.Type == LibraryType.Movie ? "movie" : "show",
                Agent = x.Type == LibraryType.Movie ? "tv.plex.agents.movie" : "tv.plex.agents.series",
                Scanner = x.Type == LibraryType.Movie ? "Plex Movie" : "Plex TV Series",
                Language = "en-US",
                Refreshing = "0",
                Uuid = _options.LibraryUuid(x.Id, x.Name, x.Type == LibraryType.Movie ? "movie" : "show"),
                UpdatedAt = timestamp.ToString(),
                ContentChangedAt = timestamp.ToString(),
                ScannedAt = timestamp.ToString(),
                CreatedAt = timestamp.ToString(),
                Content = "1",
                Directory = "1",
                Hidden = "0",
                Locations =
                {
                    new XmlLocation { Id = x.Id.ToString(), Path = x.RootPath }
                }
            }).ToList()
        };
        return PlexResults.Container(this, result);
    }

    [HttpGet("/library/sections/{libraryId:int}/all")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> All(int libraryId, CancellationToken ct)
    {
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var items = await _repo.GetItemsAsync(libraryId, ct);
        var result = new XmlMediaContainer
        {
            Size = items.Count,
            LibrarySectionID = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            Videos = items.Select(x => ToVideo(x)).ToList()
        };
        return PlexResults.Container(this, result);
    }

    [HttpGet("/library/recentlyAdded")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RecentlyAddedAll(CancellationToken ct)
    {
        var libraries = await _repo.GetLibrariesAsync(ct);
        var items = new List<MediaItem>();
        foreach (var library in libraries)
        {
            items.AddRange(await _repo.GetItemsAsync(library.Id, ct));
        }

        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => ToVideo(x)).ToList()
        });
    }

    [HttpGet("/library/sections/{libraryId:int}/recentlyAdded")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RecentlyAdded(int libraryId, CancellationToken ct)
    {
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var items = await _repo.GetItemsAsync(libraryId, ct);
        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            LibrarySectionID = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => ToVideo(x)).ToList()
        });
    }

    [HttpGet("/library/sections/{libraryId:int}/refresh")]
    [HttpPut("/library/sections/{libraryId:int}/refresh")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Refresh(int libraryId, CancellationToken ct)
    {
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        _trigger.Request(MediaScanReason.Manual);

        return PlexResults.Empty(this);
    }

    /// <summary>Mirrors Plex's URL-safe title slug, e.g. "The End of Oak Street" becomes "the-end-of-oak-street".</summary>
    private static string Slugify(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();

        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    internal static XmlVideo ToVideo(MediaItem x, bool includeLibrarySection = true)
    {
        var timestamp = x.UpdatedAt.ToUnixTimeSeconds();
        var extension = Path.GetExtension(x.FilePath).TrimStart('.').ToLowerInvariant();
        var container = string.IsNullOrEmpty(x.Container) ? extension : x.Container;
        var slug = Slugify(x.Title);
        var type = x.Library.Type == LibraryType.Movie ? "movie" : "episode";
        var streams = ReadStreams(x);

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
            OptimizedForStreaming = "0",
            Selected = "1",
            // Real Plex states this on the part as well as the media element. The JSON serializer
            // treats an unset (empty) attribute as absent, so it has to be set explicitly here.
            Has64bitOffsets = "0",
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
            AspectRatio = AspectRatio(x),
            AudioChannels = x.AudioChannels,
            AudioCodec = x.AudioCodec ?? "",
            AudioProfile = x.AudioCodec == "aac" ? "lc" : "",
            VideoCodec = x.VideoCodec ?? "",
            VideoResolution = VideoResolution(x.Width),
            VideoFrameRate = FrameRate(x.FrameRate),
            VideoProfile = (x.VideoProfile ?? "").ToLowerInvariant(),
            Container = container,
            OptimizedForStreaming = "0",
            Selected = "1",
            // Real Plex reports false here even for libraries holding large files, so the client
            // clearly does not gate on it. Matching it keeps the JSON shape identical.
            Has64bitOffsets = "0",
            PartCount = 1
        };

        media.Parts.Add(part);

        return new XmlVideo
        {
            RatingKey = x.Id,
            Key = $"/library/metadata/{x.Id}",
            Guid = $"plex://{type}/{x.Id:x}{Math.Abs(x.Title.GetHashCode()):x8}",
            Guids =
            {
                new XmlGuid { Id = $"plex://{type}/{x.Id:x}{Math.Abs(x.Title.GetHashCode()):x8}" }
            },
            Slug = slug,
            Type = type,
            Title = x.Title,
            TitleSort = x.SortTitle ?? "",
            Studio = x.Library.Name,
            Year = x.Year is > 0 ? x.Year.Value.ToString() : "",
            Summary = x.Summary ?? "",
            LibrarySectionID = includeLibrarySection ? x.LibraryId.ToString() : "",
            LibrarySectionTitle = includeLibrarySection ? x.Library.Name : "",
            LibrarySectionKey = includeLibrarySection ? $"/library/sections/{x.LibraryId}" : "",
            Thumb = $"/library/metadata/{x.Id}/thumb/{timestamp}",
            Art = $"/library/metadata/{x.Id}/art/{timestamp}",
            Duration = x.DurationMs ?? 0,
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

    private static List<XmlStream> ReadStreams(MediaItem x)
    {
        if (string.IsNullOrWhiteSpace(x.StreamsJson)) return [];

        List<MediaStreamInfo>? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<List<MediaStreamInfo>>(x.StreamsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }

        if (parsed is null) return [];

        var result = new List<XmlStream>();
        var index = 0;
        var identifier = 1;

        foreach (var s in parsed)
        {
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
                FrameRate = StreamFrameRate(s.FrameRate ?? 0),
                Location = s.Location ?? "",
                Language = s.Language ?? "",
                LanguageCode = s.LanguageCode ?? "",
                LanguageTag = s.LanguageCode ?? "",
                StreamIdentifier = (identifier++).ToString(),
                RequiredBandwidths = Bandwidths(s.Bitrate is > 0 ? s.Bitrate.Value / 1000 : 0),
                Default = index == 1 ? "1" : "",
                Selected = "1"
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
                    stream.Level = H264Level(x);
                    stream.RefFrames = 3;
                    stream.DisplayTitle = VideoResolution(x.Width) != "" ? $"{VideoResolution(x.Width)}p" : "";
                    stream.ExtendedDisplayTitle = stream.DisplayTitle != "" ? $"{stream.DisplayTitle} ({s.Codec!.ToUpperInvariant()})" : "";
                    break;

                case 2:
                    if (stream.SamplingRate == 0) stream.SamplingRate = 48000;
                    // The label has to reflect the real channel layout. Calling a 6-channel
                    // track "Stereo" makes the client pick the wrong output and can stop it
                    // playing the file at all.
                    stream.DisplayTitle = $"{s.Codec!.ToUpperInvariant()} {ChannelLabel(stream.Channels)}";
                    stream.ExtendedDisplayTitle = stream.DisplayTitle;
                    break;

                case 3:
                    stream.Format = s.Codec == "subrip" ? "srt" : s.Codec ?? "";
                    stream.DisplayTitle = !string.IsNullOrEmpty(s.Language) ? s.Language : s.Codec ?? "";
                    stream.ExtendedDisplayTitle = $"{(!string.IsNullOrEmpty(s.Language) ? s.Language : s.Codec ?? "")} ({stream.Format.ToUpperInvariant()} External)";
                    stream.Location = "sidecar-subs";
                    // Sidecar subtitles are separate files, so Plex serves them from /library/streams.
                    stream.Key = $"/library/streams/{stream.StreamIdentifier}";
                    break;
            }

            result.Add(stream);
        }

        return result;
    }

    /// <summary>
    /// Renders a channel count the way Plex names audio tracks: 2 is "Stereo", 6 is "5.1", and so on.
    /// </summary>
    private static string ChannelLabel(int channels) => channels switch
    {
        <= 0 => "",
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels} Channels"
    };

    /// <summary>
    /// Plex publishes a bandwidth ladder the client picks from; 8 descending points ending at the
    /// stream's own bitrate is the shape the real server emits.
    /// </summary>
    private static string Bandwidths(int bitrateKbps)
    {
        if (bitrateKbps <= 0) return "";
        var ladder = new List<string>();
        var value = bitrateKbps * 1.25;

        for (var i = 0; i < 4; i++)
        {
            ladder.Add(((int)Math.Round(value)).ToString());
            value *= 0.75;
        }

        var floor = Math.Max(1, bitrateKbps / 5);
        for (var i = 0; i < 4; i++) ladder.Add(floor.ToString());

        return string.Join(",", ladder);
    }

    /// <summary>
    /// H.264 level from the picture size, using the level table's MaxFS/MBPS limits rather than
    /// raw height: a 1920x1080 frame is 8160 macroblocks and is level 4.0, not 5.1.
    /// </summary>
    private static int H264Level(MediaItem x)
    {
        var width = x.Width > 0 ? x.Width : 1920;
        var height = x.Height > 0 ? x.Height : 1080;
        var fps = x.FrameRate > 0 ? x.FrameRate : 24d;

        var frameSize = (long)Math.Ceiling(width / 16d) * (long)Math.Ceiling(height / 16d);
        var macroblocksPerSecond = frameSize * (long)Math.Ceiling(fps);

        if (frameSize <= 1620 && macroblocksPerSecond <= 40500) return 30;
        if (frameSize <= 3600 && macroblocksPerSecond <= 108000) return 31;
        if (frameSize <= 8192 && macroblocksPerSecond <= 245760) return 40;
        if (frameSize <= 8192) return 41;
        if (frameSize <= 22080) return 50;
        return 51;
    }

    private static string AspectRatio(MediaItem x)
    {
        if (x.Width <= 0 || x.Height <= 0) return "";
        var ratio = Math.Round((double)x.Width / x.Height, 2);

        return ratio switch
        {
            >= 2.20 => "2.35",
            >= 1.75 => "1.85",
            >= 1.60 => "1.78",
            >= 1.45 => "1.5",
            _ => ((decimal)ratio).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Plex labels a video by its horizontal resolution, not the vertical one. A 1920x800
    /// letterboxed feature is 1080p: matching the 1920-sample reference against the official server
    /// shows videoResolution="1080" and displayTitle="1080p (H.264)". Deriving it from height
    /// labels the same file 720p, which is what the detail screen then shows.
    /// </summary>
    private static string VideoResolution(int width) => width switch
    {
        >= 3000 => "2160",
        >= 2200 => "1440",
        >= 1800 => "1080",
        >= 1200 => "720",
        >= 900 => "576",
        > 0 => "480",
        _ => ""
    };

    private static string FrameRate(double rate)
    {
        if (rate <= 0) return "";

        // Plex labels 23.976 as "24p", rounding up rather than emitting a decimal.
        var rounded = Math.Round(rate, MidpointRounding.AwayFromZero);
        return rounded >= 1 ? $"{rounded:0}p" : "";
    }

    /// <summary>Raw frame rate for the per-stream frameRate attribute, e.g. 23.976.</summary>
    private static string StreamFrameRate(double rate)
        => rate <= 0
            ? ""
            : rate.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
}
