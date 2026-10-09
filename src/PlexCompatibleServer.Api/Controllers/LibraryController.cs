using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Hosted;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class LibraryController : ControllerBase
{
    private readonly IMediaRepository repo;
    private readonly MediaScanTrigger trigger;
    private readonly ServerOptions options;
    private readonly StreamSelectionStore selections;
    private readonly IMetadataService metadata;

    public LibraryController(
        IMediaRepository repo,
        MediaScanTrigger trigger,
        ServerOptions options,
        StreamSelectionStore selections,
        IMetadataService metadata)
    {
        this.repo = repo;
        this.trigger = trigger;
        this.options = options;
        this.selections = selections;
        this.metadata = metadata;
    }

    [HttpGet("/library")]
    [HttpGet("/library/sections")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Sections(CancellationToken ct)
    {
        var libraries = await repo.GetLibrariesAsync(ct);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var result = new XmlMediaContainer
        {
            Size = libraries.Count,
            AllowSync = "0",
            Title1 = "Plex Library",
            Directories = libraries.Select(x => new XmlDirectory
            {
                AllowSync = "0",
                Filters = "1",
                RatingKey = x.Id.ToString(),
                Key = x.Id.ToString(),
                Title = x.Name,
                Type = x.Type == LibraryType.Movie ? "movie" : "show",
                Agent = x.Type == LibraryType.Movie ? "tv.plex.agents.movie" : "tv.plex.agents.series",
                Scanner = x.Type == LibraryType.Movie ? "Plex Movie" : "Plex TV Series",
                Language = "en-US",
                Refreshing = "0",
                Uuid = options.LibraryUuid(x.Id, x.Name, x.Type == LibraryType.Movie ? "movie" : "show"),
                UpdatedAt = timestamp.ToString(),
                ContentChangedAt = timestamp.ToString(),
                ScannedAt = timestamp.ToString(),
                CreatedAt = timestamp.ToString(),
                Content = "1",
                Directory = "1",
                Hidden = "0",
                Locations =
                {
                    new XmlLocation { Id = x.Id.ToString(), Path = x.RootPath }
                }
            }).ToList()
        };
        return PlexResults.Container(this, result);
    }

    [HttpGet("/library/sections/{libraryId:int}/all")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> All(int libraryId, CancellationToken ct)
    {
        var library = await repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var all = await repo.GetItemsAsync(libraryId, ct);
        var items = ContainerPaging.Page(HttpContext, all, out var offset, out var total);
        var result = new XmlMediaContainer
        {
            Size = items.Count,
            Offset = offset,
            TotalSize = total.ToString(),
            LibrarySectionId = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            Videos = items.Select(x => VideoMapper.ToVideoEnriched(metadata, x, selections: selections)).ToList()
        };
        return PlexResults.Container(this, result);
    }

    [HttpGet("/library/recentlyAdded")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RecentlyAddedAll(CancellationToken ct)
    {
        var libraries = await repo.GetLibrariesAsync(ct);
        var items = await repo.GetItemsByLibrariesAsync(libraries.Select(x => x.Id).ToList(), ct);

        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => VideoMapper.ToVideoEnriched(metadata, x, selections: selections)).ToList()
        });
    }

    [HttpGet("/library/sections/{libraryId:int}/recentlyAdded")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RecentlyAdded(int libraryId, CancellationToken ct)
    {
        var library = await repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var items = await repo.GetItemsAsync(libraryId, ct);
        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            LibrarySectionId = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => VideoMapper.ToVideoEnriched(metadata, x, selections: selections)).ToList()
        });
    }

    [HttpGet("/library/sections/{libraryId:int}/refresh")]
    [HttpPut("/library/sections/{libraryId:int}/refresh")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Refresh(int libraryId, CancellationToken ct)
    {
        var library = await repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        trigger.Request(MediaScanReason.Manual);

        return PlexResults.Empty(this);
    }
}
