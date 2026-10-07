using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api;

/// <summary>
/// The plex.tv metadata agent: for every item neither the sidecar nor the lookup cache covers,
/// fetches guid + detail from plex.tv and merges the record into plex-metadata.json (and the
/// in-memory cache). Runs after every media scan and on demand via /admin/metadata/backfill.
/// Already-covered items are skipped without network traffic, so runs are cheap to repeat.
/// Movies use the movie search endpoint; episodes parse SxxExx from the filename and walk
/// show search -> season children -> episode list.
/// </summary>
public sealed class MetadataSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetadataSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MetadataSyncService(IServiceScopeFactory scopeFactory, ILogger<MetadataSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
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
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
