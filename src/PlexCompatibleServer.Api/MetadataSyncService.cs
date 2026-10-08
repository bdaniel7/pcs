using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api;

/// <summary>
/// The plex.tv metadata agent: for every item neither the sidecar nor the lookup cache covers,
/// fetches guid + detail from plex.tv and merges the record into plex-metadata.json (and the
/// in-memory cache). Runs after every media scan and on demand via /admin/metadata/backfill.
/// Already-covered items are skipped without network traffic, so runs are cheap to repeat.
/// Movies use the movie search endpoint; episodes parse SxxExx from the filename and walk
/// show search -> season children -> episode list.
/// After the lookups it downloads the captured remote artwork into the art cache, so grids
/// and detail screens can prefer the official poster over the frame extract without any
/// request ever waiting on the network.
/// </summary>
public sealed class MetadataSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetadataSyncService> _logger;
    private readonly RemoteArtworkCache _artwork;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MetadataSyncService(IServiceScopeFactory scopeFactory, ILogger<MetadataSyncService> logger,
                               RemoteArtworkCache artwork)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _artwork = artwork;
    }

    public async Task<BackfillResult> RunAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaRepository>();
            var libraries = await repo.GetLibrariesAsync(ct);

            var items = new List<MediaItem>();
            foreach (var library in libraries)
                items.AddRange(await repo.GetItemsAsync(library.Id, ct));

            var result = ExternalMetadata.Backfill(items);
            _logger.LogInformation(
                "Metadata sync finished: {Scanned} scanned, {Present} present, {Created} created, {Enriched} enriched, {Failed} failed.",
                result.Scanned, result.Present, result.Created, result.Enriched, result.Failed.Count);
            if (result.Failed.Count > 0)
                _logger.LogDebug("Metadata sync unmatched: {Titles}.", string.Join("; ", result.Failed));

            await SyncOfficialArtworkAsync(items, repo, ct);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads the poster/backdrop URLs the lookup captured into the local art cache and
    /// stores the file paths on the items. Runs only for columns still empty (or whose file has
    /// vanished), so repeat syncs do no network work; a missing record or a record without
    /// URLs is simply not downloadable yet.
    /// </summary>
    private async Task SyncOfficialArtworkAsync(IReadOnlyList<MediaItem> items,
                                                IMediaRepository repo, CancellationToken ct)
    {
        var stats = new ArtworkStats();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!ExternalMetadata.TryGetRecord(item, out var rec)) continue;

            string? poster = null;
            string? art = null;
            string? parent = null;
            string? grandparent = null;

            if (item.Library is { Type: LibraryType.Movie })
            {
                poster = await ResolveAsync(rec.ThumbUrl, item.OfficialPosterPath, stats, ct);
                art = await ResolveAsync(rec.ArtUrl, item.OfficialArtPath, stats, ct);
            }
            else if (item.Library is { Type: LibraryType.Show })
            {
                parent = await ResolveAsync(rec.ParentThumbUrl, item.OfficialParentPosterPath, stats, ct);
                grandparent = await ResolveAsync(rec.GrandparentThumbUrl, item.OfficialGrandparentPosterPath, stats, ct);
            }
            else
            {
                continue;
            }

            if (poster is null && art is null && parent is null && grandparent is null) continue;

            await repo.SaveOfficialArtworkAsync(item.Id, poster, art, parent, grandparent, ct);
        }

        if (stats.Downloaded + stats.Failed > 0 || stats.Cached > 0)
            _logger.LogInformation(
                "Artwork sync finished: {Downloaded} downloaded, {Cached} already cached, {Failed} failed.",
                stats.Downloaded, stats.Cached, stats.Failed);
    }

    private sealed class ArtworkStats
    {
        public int Downloaded;
        public int Cached;
        public int Failed;
    }

    /// <summary>
    /// Path to store for the URL: null keeps whatever the item already has - either because the
    /// column is filled and the file is still on disk, or because there is nothing to fetch.
    /// </summary>
    private async Task<string?> ResolveAsync(string? url, string? currentPath,
                                             ArtworkStats stats, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;

        if (!string.IsNullOrEmpty(currentPath) && File.Exists(currentPath))
        {
            stats.Cached++;

            return null;
        }

        var path = await _artwork.EnsureAsync(url, ct);

        if (path is null)
        {
            stats.Failed++;

            return null;
        }
        stats.Downloaded++;

        return path;
    }
}
