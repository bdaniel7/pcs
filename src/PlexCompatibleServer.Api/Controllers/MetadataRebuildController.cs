using Microsoft.AspNetCore.Mvc;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// On-demand run of the plex.tv metadata agent (normally triggered after each media scan):
/// fills plex-metadata.json gaps for every movie no store covers and re-walks episodes whose
/// records still lack the season/episode hierarchy. Re-running is cheap - covered items cost
/// no network traffic.
/// </summary>
[ApiController]
public sealed class MetadataRebuildController : ControllerBase
{
    private readonly MetadataSyncService _sync;

    public MetadataRebuildController(MetadataSyncService sync) => _sync = sync;

    [HttpGet("/admin/metadata/backfill")]
    [HttpPost("/admin/metadata/backfill")]
    public async Task<IActionResult> Backfill(CancellationToken ct)
    {
        var result = await _sync.RunAsync(ct);
        return Ok(new
        {
            result.Scanned,
            result.Present,
            result.Created,
            result.Enriched,
            result.CreatedTitles,
            result.Failed
        });
    }
}
