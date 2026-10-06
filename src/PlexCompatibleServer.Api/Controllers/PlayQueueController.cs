using System.Net;
using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// The client talks to /playQueues before it will play anything: POST creates the queue from a
/// library path, GET re-reads it. Both responses carry the full media descriptor so the player can
/// build its source list without another metadata round trip.
/// Plex hands out three distinct ids: the queue itself, the play-queue item, and the library
/// rating key the item points at. They are not interchangeable.
/// </summary>
[ApiController]
public sealed class PlayQueueController : ControllerBase
{
    private readonly IPlaybackService _playback;
    private readonly PlaybackState _state;
    private readonly StreamSelectionStore _selections;

    public PlayQueueController(IPlaybackService playback, PlaybackState state, StreamSelectionStore selections)
    {
        _playback = playback;
        _state = state;
        _selections = selections;
    }

    [HttpPost("/playQueues")]
    [HttpPut("/playQueues")]
    public async Task<IActionResult> Create([FromQuery] string? uri, CancellationToken ct)
    {
        if (!VideoController.TryResolvePath(uri, out var ratingKey))
            return PlexResults.Error(this, HttpStatusCode.BadRequest, "invalid uri");

        var item = await _playback.GetMediaAsync(ratingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var queue = _state.Create(item.Id, uri!, item.Title);
        return PlexResults.Container(this, ContainerFor(queue, item, 0));
    }

    /// <summary>
    /// Re-read of an existing queue, also how the client reports resume progress by passing
    /// X-Plex-Playback-Consumer-Offset or an offset query parameter.
    /// </summary>
    [HttpGet("/playQueues/{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] string? offset, CancellationToken ct)
    {
        var queue = _state.Get(id);
        if (queue is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "play queue not found");

        var reported = ParseOffset(offset);
        if (reported is { } milliseconds)
        {
            _state.SetOffset(id, milliseconds);
            queue.SelectedOffset = milliseconds;
        }

        var item = await _playback.GetMediaAsync(queue.SelectedRatingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        return PlexResults.Container(this, ContainerFor(queue, item, queue.SelectedOffset));
    }

    [HttpGet("/playQueues")]
    public IActionResult GetAll() => PlexResults.Empty(this);

    private XmlMediaContainer ContainerFor(PlaybackState.PlayQueue queue, MediaItem item, long offset)
    {
        var entry = queue.Items.FirstOrDefault(x => x.Id == queue.SelectedItemId);
        if (entry is null) return new XmlMediaContainer();
        var video = LibraryController.ToVideo(item, includeLibrarySection: true, selections: _selections);
        video.PlayQueueItemID = entry.Id.ToString();

        // The queue response is a summary: Media carries the part-less descriptor the client
        // uses to pick a source, without the per-stream detail the decision endpoint adds.
        foreach (var media in video.Media)
        {
            media.Parts.Clear();
            media.Streams.Clear();
            media.OptimizedForStreaming = "1";
            media.Has64bitOffsets = "0";
            media.PartCount = 1;
        }

        return new XmlMediaContainer
        {
            Size = 1,
            Identifier = "com.plexapp.plugins.library",
            MediaTagPrefix = "/system/bundle/media/flags/",
            MediaTagVersion = PlaybackState.MediaTagVersion,
            PlayQueueID = queue.Id.ToString(),
            PlayQueueSelectedItemID = entry?.Id.ToString() ?? "",
            PlayQueueSelectedItemOffset = offset,
            PlayQueueSelectedMetadataItemID = queue.SelectedRatingKey.ToString(),
            PlayQueueShuffled = "0",
            PlayQueueSourceURI = queue.SourceUri,
            PlayQueueSourceTitle = string.IsNullOrEmpty(queue.SourceTitle) ? "Unknown" : queue.SourceTitle,
            PlayQueueTotalCount = queue.Items.Count,
            PlayQueueVersion = queue.Version,
            Videos = { video }
        };
    }

    private static long? ParseOffset(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset)) return null;
        return long.TryParse(offset, out var value) && value >= 0 ? value : null;
    }
}
