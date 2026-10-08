using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Core.Interfaces;

public interface IMediaRepository
{
    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct);
    Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct);
    Task<MediaItem?> GetItemAsync(int id, CancellationToken ct);
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
}
