using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Playback progress. The client reports its position through /:/timeline every few seconds and
/// on every state change (captured: state=buffering|playing|stopped with time and duration in
/// milliseconds); the stored offset is what detail pages, grids and the Continue Watching hub
/// hand back so the next session resumes where this one stopped.
/// </summary>
[ApiController]
public sealed class TimelineController : ControllerBase
{
    private readonly IMediaRepository _repo;

    // Trailing digits of a "/library/metadata/5"-style key; compiled once, not per report.
    private static readonly Regex TrailingId = new(@"(\d+)$", RegexOptions.Compiled);

    public TimelineController(IMediaRepository repo) => _repo = repo;

    [HttpGet("/:/timeline")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Timeline(
        [FromQuery] int ratingKey = 0,
        [FromQuery] string key = "",
        [FromQuery] int time = 0,
        [FromQuery] int duration = 0,
        [FromQuery] string state = "",
        [FromQuery] int playQueueItemID = 0,
        CancellationToken ct = default)
    {
        var id = ratingKey > 0 ? ratingKey : ParseKey(key);
        if (id > 0)
        {
            await _repo.SaveProgressAsync(id, time, duration, DateTimeOffset.UtcNow, ct);
        }

        return PlexResults.Empty(this);
    }

    /// <summary>
    /// The parameters are strings on purpose: real clients send key as either a bare id or a
    /// /library/metadata path and identifier as com.plexapp.plugins.library, and int-typed
    /// parameters turn those requests into model-binding 400s before any handler runs.
    /// </summary>
    [HttpGet("/:/scrobble")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Scrobble(
        [FromQuery] string key = "",
        [FromQuery] string identifier = "",
        CancellationToken ct = default)
    {
        var id = ParseKey(key);
        if (id > 0)
        {
            await _repo.MarkWatchedAsync(id, DateTimeOffset.UtcNow, ct);
        }

        return PlexResults.Empty(this);
    }

    [HttpGet("/:/unscrobble")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Unscrobble(
        [FromQuery] string key = "",
        [FromQuery] string identifier = "",
        CancellationToken ct = default)
    {
        var id = ParseKey(key);
        if (id > 0)
        {
            await _repo.ClearProgressAsync(id, ct);
        }

        return PlexResults.Empty(this);
    }

    /// <summary>Accepts "5" as well as "/library/metadata/5"; returns 0 when nothing parseable.</summary>
    internal static int ParseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (int.TryParse(value, out var bare)) return bare;

        var match = TrailingId.Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out var id) ? id : 0;
    }

    /// <summary>
    /// The long-press menu's "Remove from continue watching", advertised through /media/providers.
    /// The captured client calls it with PUT and ratingKey + identifier; the card must actually
    /// leave the Continue Watching shelves, which is what the 404 used to prevent. Progress,
    /// resume point and watch state stay untouched (real Plex keeps them too - the detail screen
    /// still offers Resume); the next accepted timeline report reverses the removal.
    /// </summary>
    [HttpPut("/actions/removeFromContinueWatching")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RemoveFromContinueWatching(
        [FromQuery] string ratingKey = "",
        [FromQuery] string identifier = "",
        CancellationToken ct = default)
    {
        var id = ParseKey(ratingKey);
        if (id > 0)
        {
            await _repo.DismissFromContinueWatchingAsync(id, ct);
        }

        return PlexResults.Empty(this);
    }
}
