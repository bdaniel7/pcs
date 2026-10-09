using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class MetadataController : ControllerBase
{
    private readonly IMediaRepository _repo;
    private readonly ServerOptions _options;
    private readonly StreamSelectionStore _selections;
    private readonly IMetadataService _metadata;

    public MetadataController(IMediaRepository repo, ServerOptions options, StreamSelectionStore selections,
                              IMetadataService metadata)
    {
        _repo = repo;
        _options = options;
        _selections = selections;
        _metadata = metadata;
    }

    /// <summary>
    /// The detail screen reads the container's identifier and section fields, not just the item, so
    /// a metadata response that carries only size and Metadata leaves the client unable to build
    /// the page. These are the attributes real Plex sends here.
    /// </summary>
    [HttpGet("/library/metadata/{id:int}")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var item = await _repo.GetItemAsync(id, ct);
        if (item is null) return NotFound();

        var kind = item.Library.Type == LibraryType.Movie ? "movie" : "show";

        var video = LibraryController.ToVideo(item, selections: _selections);

        // The detail screen renders from the *first* item response it receives, which carries only
        // includeUserState=1 and no includeExternalMetadata. Gating the scraped-metadata sections
        // on includeExternalMetadata therefore left the fields the movie screen reads (summary,
        // Genre, Director, Writer, Role, Rating, ...) absent from the response it actually renders
        // from, and the screen reports "content could not be loaded". Official Plex states these
        // fields on every item response even when it knows nothing about them, so we do too.
        await MetadataParity.ApplyAsync(_metadata, item, video, id, includeExtras: true, ct: ct);

        var container = new XmlMediaContainer
        {
            Size = 1,
            AllowSync = "1",
            Identifier = "com.plexapp.plugins.library",
            LibrarySectionID = item.LibraryId.ToString(),
            LibrarySectionTitle = item.Library.Name,
            LibrarySectionUUID = _options.LibraryUuid(item.LibraryId, item.Library.Name, kind),
            MediaTagPrefix = "/system/bundle/media/flags/",
            MediaTagVersion = PlaybackState.MediaTagVersion,
            Videos = new List<XmlVideo> { video }
        };

        return PlexResults.Container(this, container);
    }

    [HttpGet("/library/metadata/{id:int}/children")]
    [Produces("application/xml", "application/json")]
    public IActionResult Children(int id) => PlexResults.Empty(this);

    // Plex appends an updatedAt cache-buster to every image URL. Accept it with or without.
    [HttpGet("/library/metadata/{id:int}/thumb")]
    [HttpGet("/library/metadata/{id:int}/thumb/{cacheBuster}")]
    public Task<IActionResult> Thumb(int id, CancellationToken ct) =>
        ServeImageAsync(id, item => FirstExisting(item.OfficialPosterPath, item.PosterPath), ct);

    [HttpGet("/library/metadata/{id:int}/art")]
    [HttpGet("/library/metadata/{id:int}/art/{cacheBuster}")]
    public Task<IActionResult> Art(int id, CancellationToken ct) =>
        ServeImageAsync(id, item => FirstExisting(item.OfficialArtPath, item.ArtPath), ct);

    // Season poster pointer carried on episode rows (real Plex states parentThumb/grandparentThumb
    // on episodes; the season grid has no row of its own and renders from these). Chains into the
    // official show poster and finally the frame extract so the request never comes up empty.
    [HttpGet("/library/metadata/{id:int}/parentThumb")]
    [HttpGet("/library/metadata/{id:int}/parentThumb/{cacheBuster}")]
    public Task<IActionResult> ParentThumb(int id, CancellationToken ct) =>
        ServeImageAsync(id, item => FirstExisting(item.OfficialParentPosterPath,
                                                  item.OfficialGrandparentPosterPath,
                                                  item.OfficialPosterPath,
                                                  item.PosterPath, item.ArtPath), ct);

    [HttpGet("/library/metadata/{id:int}/grandparentThumb")]
    [HttpGet("/library/metadata/{id:int}/grandparentThumb/{cacheBuster}")]
    public Task<IActionResult> GrandparentThumb(int id, CancellationToken ct) =>
        ServeImageAsync(id, item => FirstExisting(item.OfficialGrandparentPosterPath,
                                                  item.OfficialPosterPath,
                                                  item.PosterPath, item.ArtPath), ct);

    [HttpGet("/library/metadata/{id:int}/squareArt")]
    [HttpGet("/library/metadata/{id:int}/squareArt/{cacheBuster}")]
    public async Task<IActionResult> SquareArt(int id, CancellationToken ct) => await Art(id, ct);

    [HttpGet("/library/metadata/{id:int}/clearLogo")]
    [HttpGet("/library/metadata/{id:int}/clearLogo/{cacheBuster}")]
    public IActionResult ClearLogo() => NotFound();

    /// <summary>First path of the chain that still exists on disk, else 404.</summary>
    private async Task<IActionResult> ServeImageAsync(int id, Func<MediaItem, string?> pick,
                                                      CancellationToken ct)
    {
        var item = await _repo.GetItemAsync(id, ct);
        if (item is null) return NotFound();

        var path = pick(item);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return NotFound();

        return PhysicalFile(path, GetContentType(path));
    }

    private static string? FirstExisting(params string?[] paths)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                return path;

        return null;
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
