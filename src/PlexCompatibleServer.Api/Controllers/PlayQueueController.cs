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
    private readonly IPlaybackService playback;
    private readonly PlaybackState state;
    private readonly StreamSelectionStore selections;
    private readonly IMetadataService metadata;

    public PlayQueueController(IPlaybackService playback, PlaybackState state, StreamSelectionStore selections,
                               IMetadataService metadata)
    {
        this.playback = playback;
        this.state = state;
        this.selections = selections;
        this.metadata = metadata;
    }

    [HttpPost("/playQueues")]
    [HttpPut("/playQueues")]
    public async Task<IActionResult> Create([FromQuery] string? uri, CancellationToken ct)
    {
        if (!VideoController.TryResolvePath(uri, out var ratingKey))
            return PlexResults.Error(this, HttpStatusCode.BadRequest, "invalid uri");

        var item = await playback.GetMediaAsync(ratingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        var queue = state.Create(item.Id, uri!, item.Title);
        return PlexResults.Container(this, containerFor(queue, item, 0));
    }

    /// <summary>
    /// Re-read of an existing queue, also how the client reports resume progress by passing
    /// X-Plex-Playback-Consumer-Offset or an offset query parameter.
    /// </summary>
    [HttpGet("/playQueues/{id:int}")]
    public async Task<IActionResult> Get(int id, [FromQuery] string? offset, CancellationToken ct)
    {
        var queue = state.Get(id);
        if (queue is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "play queue not found");

        var reported = parseOffset(offset);
        if (reported is { } milliseconds)
        {
            state.SetOffset(id, milliseconds);
            queue.SelectedOffset = milliseconds;
        }

        var item = await playback.GetMediaAsync(queue.SelectedRatingKey, ct);
        if (item is null)
            return PlexResults.Error(this, HttpStatusCode.NotFound, "media not found");

        return PlexResults.Container(this, containerFor(queue, item, queue.SelectedOffset));
    }

    [HttpGet("/playQueues")]
    public IActionResult GetAll() => PlexResults.Empty(this);

    private XmlMediaContainer containerFor(PlaybackState.PlayQueue queue, MediaItem item, long offset)
    {
        var entry = queue.Items.FirstOrDefault(x => x.Id == queue.SelectedItemId);
        if (entry is null) return new XmlMediaContainer();
        var video = VideoMapper.ToVideoEnriched(metadata, item, includeLibrarySection: true, selections: selections);
        video.PlayQueueItemId = entry.Id.ToString();

        // The queue response is a summary: Media carries the part-less descriptor the client
        // uses to pick a source, without the per-stream detail the decision endpoint adds.
        foreach (var media in video.Media)
        {
            media.Parts.Clear();
            media.Streams.Clear();
            media.OptimizedForStreaming = "1";
            media.Has64BitOffsets = "0";
            media.PartCount = 1;
        }

        return new XmlMediaContainer
        {
            Size = 1,
            Identifier = "com.plexapp.plugins.library",
            MediaTagPrefix = "/system/bundle/media/flags/",
            MediaTagVersion = PlaybackState.MediaTagVersion,
            PlayQueueId = queue.Id.ToString(),
            PlayQueueSelectedItemId = entry.Id.ToString(),
            PlayQueueSelectedItemOffset = offset,
            PlayQueueSelectedMetadataItemId = queue.SelectedRatingKey.ToString(),
            PlayQueueShuffled = "0",
            PlayQueueSourceUri = queue.SourceUri,
            PlayQueueSourceTitle = string.IsNullOrEmpty(queue.SourceTitle) ? "Unknown" : queue.SourceTitle,
            PlayQueueTotalCount = queue.Items.Count,
            PlayQueueVersion = queue.Version,
            Videos = { video }
        };
    }

    private static long? parseOffset(string? offset)
    {
        if (string.IsNullOrWhiteSpace(offset)) return null;
        return long.TryParse(offset, out var value) && value >= 0 ? value : null;
    }
}
