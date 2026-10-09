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
    private readonly IMediaRepository _repo;
    private readonly MediaScanTrigger _trigger;
    private readonly ServerOptions _options;
    private readonly StreamSelectionStore _selections;
    private readonly IMetadataService _metadata;

    public LibraryController(
        IMediaRepository repo,
        MediaScanTrigger trigger,
        ServerOptions options,
        StreamSelectionStore selections,
        IMetadataService metadata)
    {
        _repo = repo;
        _trigger = trigger;
        _options = options;
        _selections = selections;
        _metadata = metadata;
    }

    [HttpGet("/library")]
    [HttpGet("/library/sections")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Sections(CancellationToken ct)
    {
        var libraries = await _repo.GetLibrariesAsync(ct);
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
                Uuid = _options.LibraryUuid(x.Id, x.Name, x.Type == LibraryType.Movie ? "movie" : "show"),
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
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var all = await _repo.GetItemsAsync(libraryId, ct);
        var items = ContainerPaging.Page(HttpContext, all, out var offset, out var total);
        var result = new XmlMediaContainer
        {
            Size = items.Count,
            Offset = offset,
            TotalSize = total.ToString(),
            LibrarySectionID = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            Videos = items.Select(x => VideoMapper.ToVideoEnriched(_metadata, x, selections: _selections)).ToList()
        };
        return PlexResults.Container(this, result);
    }

    [HttpGet("/library/recentlyAdded")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RecentlyAddedAll(CancellationToken ct)
    {
        var libraries = await _repo.GetLibrariesAsync(ct);
        var items = await _repo.GetItemsByLibrariesAsync(libraries.Select(x => x.Id).ToList(), ct);

        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => VideoMapper.ToVideoEnriched(_metadata, x, selections: _selections)).ToList()
        });
    }

    [HttpGet("/library/sections/{libraryId:int}/recentlyAdded")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> RecentlyAdded(int libraryId, CancellationToken ct)
    {
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var items = await _repo.GetItemsAsync(libraryId, ct);
        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(50)
            .ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            LibrarySectionID = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => VideoMapper.ToVideoEnriched(_metadata, x, selections: _selections)).ToList()
        });
    }

    [HttpGet("/library/sections/{libraryId:int}/refresh")]
    [HttpPut("/library/sections/{libraryId:int}/refresh")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Refresh(int libraryId, CancellationToken ct)
    {
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        _trigger.Request(MediaScanReason.Manual);

        return PlexResults.Empty(this);
    }
}
