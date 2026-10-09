using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Core.Interfaces;

public interface IMediaRepository
{
    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct);
    Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct);
    Task<MediaItem?> GetItemAsync(int id, CancellationToken ct);

    /// <summary>
    /// Every item across the given libraries in one query, ordered by library then id so the
    /// result keeps the same order as concatenating <see cref="GetItemsAsync"/> per library.
    /// Unknown ids contribute nothing; an empty request returns nothing without a round trip.
    /// </summary>
    Task<IReadOnlyList<MediaItem>> GetItemsByLibrariesAsync(IReadOnlyList<int> libraryIds, CancellationToken ct);

    Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct);

    /// <summary>
    /// Persists a /:/timeline report: stores the clamped position, updates the last-viewed time,
    /// and counts a completed view when the position crosses the 90% watched threshold. A
    /// non-positive time is ignored so a buffering report at 0 can never erase a resume point.
    /// </summary>
    Task SaveProgressAsync(int id, long timeMs, long durationMs, DateTimeOffset viewedAt, CancellationToken ct);

    /// <summary>/:/scrobble: counts a completed view and pins the offset at the end of the item.</summary>
    Task MarkWatchedAsync(int id, DateTimeOffset viewedAt, CancellationToken ct);

    /// <summary>/:/unscrobble: forgets all progress for the item.</summary>
    Task ClearProgressAsync(int id, CancellationToken ct);

    /// <summary>
    /// Items the viewer is in the middle of: position past zero but short of the 90% watched
    /// threshold, newest first. Optionally restricted to one library.
    /// </summary>
    Task<IReadOnlyList<MediaItem>> GetInProgressAsync(int? libraryId, int limit, CancellationToken ct);

    /// <summary>
    /// /actions/removeFromContinueWatching: hides the item from every Continue Watching shelf
    /// without altering its progress, resume point or watch state. The next accepted timeline
    /// report clears the dismissal, so playing the item brings the card back - matching Plex,
    /// where removal is reversed by the next playthrough.
    /// </summary>
    Task DismissFromContinueWatchingAsync(int id, CancellationToken ct);

    /// <summary>
    /// Stores the local paths of artwork downloaded from plex.tv. Any argument left null keeps
    /// the current value, so a partial sync (e.g. a show without a season poster) never erases
    /// what an earlier run found.
    /// </summary>
    Task SaveOfficialArtworkAsync(int id, string? posterPath, string? artPath, string? parentPosterPath,
                                  string? grandparentPosterPath, CancellationToken ct);
}
