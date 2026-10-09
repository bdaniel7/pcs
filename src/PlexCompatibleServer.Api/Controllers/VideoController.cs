using System.Net;
using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class VideoController : ControllerBase
{
    private readonly IPlaybackService _playback;
    private readonly StreamSelectionStore _selections;

    public VideoController(IPlaybackService playback, StreamSelectionStore selections)
    {
        _playback = playback;
        _selections = selections;
    }

    internal const string MediaTagPrefix = "/system/bundle/media/flags/";

    /// <summary>
    /// Plex addresses the file itself by a Part key of the form
    /// /library/parts/{id}/{timestamp}/file.{ext}. This is the URL the client puts in a
    /// &lt;video src&gt; for direct play, so it has to serve bytes with range support.
    /// </summary>
    [HttpGet("/library/parts/{id:int}/{timestamp}/{file}")]
    public Task<IActionResult> Part(int id, string timestamp, string file, CancellationToken ct) => File(id, ct);

    /// <summary>
    /// Several players (the LG TV app among them) probe a media URL with HEAD before committing to a
    /// stream, to learn the length and whether ranges are supported. Answering with the same headers as
    /// GET minus the body keeps that probe from failing.
    /// </summary>
    [HttpHead("/library/parts/{id:int}/{timestamp}/{file}")]
    public Task<IActionResult> PartHead(int id, string timestamp, string file, CancellationToken ct) => File(id, ct);

    /// <summary>Legacy direct-stream path kept for older clients.</summary>
    [HttpGet("/library/metadata/{id:int}/media/{partId:int}")]
    public Task<IActionResult> Stream(int id, int partId, CancellationToken ct) => File(id, ct);

    [HttpHead("/library/metadata/{id:int}/media/{partId:int}")]
    public Task<IActionResult> StreamHead(int id, int partId, CancellationToken ct) => File(id, ct);

    private async Task<IActionResult> File(int id, CancellationToken ct)
    {
        var item = await _playback.GetMediaAsync(id, ct);
        if (item is null || !System.IO.File.Exists(item.FilePath))
            return NotFound();

        Response.Headers.AcceptRanges = "bytes";

        // A HEAD request must report the same length/range headers as GET but send no body. Opening the
        // stream first lets FileStreamResult fill in Content-Length from the real file.
        if (HttpMethods.IsHead(Request.Method))
        {
            await using var probe = new FileStream(
                item.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Response.ContentLength = probe.Length;
            Response.StatusCode = StatusCodes.Status200OK;
            return new EmptyResult();
        }

        // Opened with FileOptions.Asynchronous so range requests stream instead of loading the file.
        var stream = new FileStream(
            item.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        return File(stream, item.MimeType, enableRangeProcessing: true);
    }

    /// <summary>
    /// How a viewer picks a track: the client pauses, PUTs the stream id it wants, then re-reads
    /// metadata for the track flagged selected and fetches that one's key. An unimplemented route
    /// answering 404 here makes the client abandon the choice and carry on with no subtitle at
    /// all, which is what the TV was doing. The real server persists the pair and answers with an
    /// empty container; a stream that does not belong to the part is a 400.
    /// </summary>
    [HttpPut("/library/parts/{partId:int}")]
    public async Task<IActionResult> SetStreamSelection(
        int partId,
        int? audioStreamId = null,
        int? subtitleStreamId = null,
        CancellationToken ct = default)
    {
        var item = await _playback.GetMediaAsync(partId, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var streams = VideoMapper.ToVideo(item, selections: _selections)
            .Media[0].Parts[0].Streams;

        // 0 is the documented way of saying "no subtitle" and always passes; anything else has to
        // name a stream this part actually publishes.
        if (audioStreamId is > 0 &&
            !streams.Any(s => s.StreamType == 2 && s.Id == audioStreamId.ToString()))
            return PlexResults.Error(this, HttpStatusCode.BadRequest, "audio stream not found");

        if (subtitleStreamId is > 0 &&
            !streams.Any(s => s.StreamType == 3 && s.Id == subtitleStreamId.ToString()))
            return PlexResults.Error(this, HttpStatusCode.BadRequest, "subtitle stream not found");

        _selections.Set(partId, audioStreamId, subtitleStreamId);
        return PlexResults.Empty(this);
    }

    /// <summary>
    /// The client asks this before it will play anything. Plex answers with a machine decision code
    /// plus the full media/stream layout, and the TV reads the per-Part "decision" attribute to decide
    /// whether to fetch the file as-is or start a transcode session.
    /// There is no transcoder here, so every playable file is offered as a direct play.
    /// </summary>
    [HttpGet("/video/:/transcode/universal/decision")]
    public async Task<IActionResult> Decision(
        string path = "",
        int? audioStreamId = null,
        int? subtitleStreamId = null,
        CancellationToken ct = default)
    {
        if (!TryResolvePath(path, out var ratingKey))
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var item = await _playback.GetMediaAsync(ratingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var video = VideoMapper.ToVideo(item, selections: _selections);
        foreach (var media in video.Media)
        {
            media.Selected = "1";
            media.Has64bitOffsets = "0";
            media.OptimizedForStreaming = "1";

            foreach (var part in media.Parts)
            {
                part.Decision = "directplay";
                part.Selected = "1";
                part.Has64bitOffsets = "0";
                part.OptimizedForStreaming = "1";

                // The decision echoes back the session the client just asked for: a track it
                // named as its audio or subtitle comes back flagged as the one in use, the way
                // the real server answers a "subtitles=stream" request. Metadata stays untouched
                // (no track pre-selected for anyone else).
                foreach (var stream in part.Streams)
                {
                    if (audioStreamId is int audio && stream.Id == audio.ToString()) stream.Selected = "1";
                    else if (subtitleStreamId is int sub && stream.Id == sub.ToString()) stream.Selected = "1";
                }
            }
        }

        var response = new XmlMediaContainer
        {
            Size = 1,
            AllowSync = "1",
            Identifier = "com.plexapp.plugins.library",
            LibrarySectionID = item.LibraryId.ToString(),
            LibrarySectionTitle = item.Library.Name,
            LibrarySectionUUID = item.Library.Uuid,
            MdeDecisionCode = "1000",
            MdeDecisionText = "Direct play OK.",
            MediaTagPrefix = MediaTagPrefix,
            MediaTagVersion = PlaybackState.MediaTagVersion,
            ResourceSession = ResourceSessionFor,
            Videos = { video }
        };

        return PlexResults.Container(this, response);
    }

    /// <summary>
    /// How a direct-playing client actually receives a subtitle during playback. Captured from the
    /// real server for this exact TV: the response body is the plain SRT text, nothing else. The
    /// query names the target (path/mediaIndex/partIndex/subtitles=sidecar) but carries no stream
    /// id, so the answer is whatever the viewer last chose for that part - the selection stored by
    /// the PUT above. With no choice made there is no subtitle to send, and an empty 200 is what
    /// the client tolerates; a 404 here is the fetch failing and the subtitle silently never shows.
    /// </summary>
    [HttpGet("/subtitles/:/transcode/universal/start")]
    public async Task<IActionResult> SubtitleTranscodeStart(string path = "", CancellationToken ct = default)
    {
        if (!TryResolvePath(path, out var ratingKey))
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var item = await _playback.GetMediaAsync(ratingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var selectedSubtitle = _selections.Get(ratingKey).Subtitle;
        if (selectedSubtitle <= 0) return EmptySubtitle();

        return await GetSubtitle(selectedSubtitle, ratingKey, ct);
    }

    /// <summary>
    /// Only reached when the client decides to transcode. Nothing is implemented, but returning a
    /// clean 501 with an explanation is better than a 404 the client cannot interpret.
    /// </summary>
    [HttpGet("/video/:/transcode/universal/start")]
    public IActionResult TranscodePlaceholder() =>
        StatusCode(StatusCodes.Status501NotImplemented,
            "Transcoding is not implemented. Direct play is currently supported.");

    /// <summary>
    /// Serves an external subtitle file. The real server answers a request for a track it has no
    /// bytes for with an empty text/plain body rather than a 404, and that difference matters:
    /// a 404 here surfaces as a playback error on the client.
    /// </summary>
    [HttpGet("/library/streams/{id:int}")]
    public Task<IActionResult> StreamFile(int id, CancellationToken ct)
    {
        return GetSubtitle(id, 0, ct);
    }

    [HttpGet("/library/parts/{partId:int}/subtitles/{streamId:int}")]
    [HttpGet("/library/parts/{partId:int}/subtitles/{streamId:int}.srt")]
    public Task<IActionResult> StreamFileAlt(int partId, int streamId, CancellationToken ct)
    {
        return GetSubtitle(streamId, partId, ct);
    }

    private async Task<IActionResult> GetSubtitle(int streamId, int partId, CancellationToken ct)
    {
        // The part id is the item id, so it is the most trustworthy handle on the request. A
        // stream id only carries one when it was minted by this server as itemId * 1000 + index.
        MediaItem? media = null;
        if (partId > 0) media = await _playback.GetMediaAsync(partId, ct);
        if (media is null && streamId >= 1000) media = await _playback.GetMediaAsync(streamId / 1000, ct);
        if (media is null) media = await _playback.GetMediaAsync(streamId, ct);
        if (media is null || !System.IO.File.Exists(media.FilePath)) return NotFound();

        var probed = ProbedStreams(media);
        var sidecars = SidecarSubtitles.Find(media.FilePath);

        // Sidecar tracks are published after every probed track, so a stream id that decodes
        // past them addresses a sidecar by position - which is how the right .srt is picked when
        // a video has more than one.
        var index = streamId % 1000;
        var sidecarIndex = index - probed.Count;
        if (sidecarIndex >= 0 && sidecarIndex < sidecars.Count)
            return ServeSubtitle(sidecars[sidecarIndex].FilePath);

        // The request addresses a track that lives inside the container. Its text is pulled out
        // to SRT first, because a direct-playing client cannot read a subtitle track out of the
        // file itself. When there is no text to extract the answer is an empty body, not an
        // error: a 404 here surfaces as a playback error on the client.
        if (index < probed.Count && probed[index].StreamType == 3)
        {
            var track = probed[index];
            var position = 0;
            for (var i = 0; i < index; i++)
                if (probed[i].StreamType == 3) position++;

            var extracted = await EmbeddedSubtitles.ExtractAsync(media.FilePath, position, track.Codec, ct);
            return extracted is null ? EmptySubtitle() : ServeSubtitle(extracted);
        }

        // An id that does not decode to a sidecar slot still came from a client asking for a
        // subtitle, so fall back to the first one rather than failing the playback.
        if (sidecars.Count > 0)
            return ServeSubtitle(sidecars[0].FilePath);

        return index <= probed.Count ? EmptySubtitle() : NotFound();
    }

    private IActionResult ServeSubtitle(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, options: FileOptions.Asynchronous);
        return File(stream, "text/plain", enableRangeProcessing: false);
    }

    private IActionResult EmptySubtitle() => Content("", "text/plain");

    private static List<MediaStreamInfo> ProbedStreams(MediaItem media)
    {
        if (string.IsNullOrWhiteSpace(media.StreamsJson)) return [];

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<MediaStreamInfo>>(media.StreamsJson) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private string ResourceSessionFor =>
        Request.Query["session"].FirstOrDefault()
        ?? Request.Headers["X-Plex-Session-Identifier"].FirstOrDefault()
        ?? "pcs-playback";

    /// <summary>
    /// Plex passes the target as an absolute library path, e.g.
    /// "/library/metadata/763?own=1&amp;window=2000". Strip the query and read the rating key.
    /// Also accepts the server:// form used by play queues.
    /// </summary>
    internal static bool TryResolvePath(string? path, out int ratingKey)
    {
        ratingKey = 0;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var queryStart = path.IndexOf('?');
        if (queryStart >= 0) path = path[..queryStart];

        // server://<machine>/com.plexapp.plugins.library/library/metadata/763
        var libraryIndex = path.IndexOf("/library/metadata/", StringComparison.OrdinalIgnoreCase);
        if (libraryIndex < 0) return false;

        var tail = path[(libraryIndex + "/library/metadata/".Length)..].Trim('/');
        var slash = tail.IndexOf('/');
        if (slash >= 0) tail = tail[..slash];

        // Decoded queue sources wrap the path in a percent-encoded item URI.
        try
        {
            tail = Uri.UnescapeDataString(tail);
        }
        catch (UriFormatException)
        {
            // Keep the raw value; a plain integer still parses fine.
        }

        return int.TryParse(tail, out ratingKey) && ratingKey > 0;
    }
}
