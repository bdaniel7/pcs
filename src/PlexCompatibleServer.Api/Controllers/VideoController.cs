using System.Net;
using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class VideoController : ControllerBase
{
    private readonly IPlaybackService _playback;

    public VideoController(IPlaybackService playback) => _playback = playback;

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
    /// The client asks this before it will play anything. Plex answers with a machine decision code
    /// plus the full media/stream layout, and the TV reads the per-Part "decision" attribute to decide
    /// whether to fetch the file as-is or start a transcode session.
    /// There is no transcoder here, so every playable file is offered as a direct play.
    /// </summary>
    [HttpGet("/video/:/transcode/universal/decision")]
    public async Task<IActionResult> Decision(string path = "", CancellationToken ct = default)
    {
        if (!TryResolvePath(path, out var ratingKey))
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var item = await _playback.GetMediaAsync(ratingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var video = LibraryController.ToVideo(item);
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
    /// Only reached when the client decides to transcode. Nothing is implemented, but returning a
    /// clean 501 with an explanation is better than a 404 the client cannot interpret.
    /// </summary>
    [HttpGet("/video/:/transcode/universal/start")]
    public IActionResult TranscodePlaceholder() =>
        StatusCode(StatusCodes.Status501NotImplemented,
            "Transcoding is not implemented. Direct play is currently supported.");

    /// <summary>
    /// Subtitle burn-in/transcode entry point. The capture shows the real server answering with an
    /// empty text/plain body when the selected file has no transcode-worthy subtitle track, so an
    /// empty 200 is what keeps the client from treating subtitles as a failure.
    /// </summary>
    [HttpGet("/subtitles/:/transcode/universal/start")]
    public IActionResult SubtitleStart()
    {
        Response.Headers.ContentType = "text/plain; charset=utf-8";
        return new ContentResult { Content = "", ContentType = "text/plain; charset=utf-8" };
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
