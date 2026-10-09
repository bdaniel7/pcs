using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Core.Interfaces;

/// <summary>
/// The metadata agent behind the sidecar/lookup stores: resolves an item to its external
/// metadata record (locally, or against plex.tv when allowed) and backfills the store.
/// Implementations own the token + caches; callers never touch the stores directly.
/// </summary>
public interface IMetadataService
{
    /// <summary>True once the client's plex.tv auth token has been captured.</summary>
    bool TokenKnown { get; }

    /// <summary>Records the auth token the client sent (only meaningful on /identity).</summary>
    void CaptureToken(string? token);

    /// <summary>
    /// Resolves an item against the loaded stores only. Null means no loaded store covers the
    /// item. <paramref name="title"/>/<paramref name="titleSort"/> are the generated display
    /// values used for the title fallbacks.
    /// </summary>
    SidecarItem? ResolveLocal(MediaItem item, string? title, string? titleSort);

    /// <summary>
    /// Resolves an item like <see cref="ResolveLocal"/>, falling back to a plex.tv lookup on a
    /// cache miss. Every network wait is awaited.
    /// </summary>
    Task<SidecarItem?> ResolveAsync(MediaItem item, string? title, string? titleSort,
                                    CancellationToken ct = default);

    /// <summary>Looks up the stored record for an item without any network access.</summary>
    bool TryGetRecord(MediaItem item, out SidecarItem record);

    /// <summary>Fetches and merges metadata for every not-yet-covered item.</summary>
    Task<BackfillResult> BackfillAsync(IReadOnlyList<MediaItem> items, CancellationToken ct = default);
}
