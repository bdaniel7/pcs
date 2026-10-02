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

    public MetadataController(IMediaRepository repo, ServerOptions options)
    {
        _repo = repo;
        _options = options;
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

        // A movie detail screen asks for includeExternalMetadata=1 and then reads the scraped
        // metadata sections unconditionally. We have none, so send them empty instead of omitting
        // them: absent sections are what leave that screen reporting "content could not be loaded".
        var wantsExternalMetadata =
            Request.Query.ContainsKey("includeExternalMetadata") || Request.Query.ContainsKey("includeMeta");

        var video = LibraryController.ToVideo(item);
        if (wantsExternalMetadata)
        {
            video.EmitEmptyMetadataSections = true;
            video.Extras = new XmlExtras();

            // Official Plex always states a release date here. We only know the year, so state the
            // year rather than omitting the field the detail screen reads.
            if (video.OriginallyAvailableAt.Length == 0 && video.Year.Length > 0)
            {
                video.OriginallyAvailableAt = $"{video.Year}-01-01";
            }

            // TEMPORARY DIAGNOSTIC PROBE - delete once the movie detail path is understood.
            // Tests whether the detail screen needs non-empty scraped values or only their
            // presence. These strings are synthetic and are not real metadata.
            if (video.Summary.Length == 0) video.Summary = "PROBE synthetic plot text.";
            if (video.Tagline.Length == 0) video.Tagline = "PROBE synthetic tagline.";
            if (video.ContentRating.Length == 0) video.ContentRating = "PROBE";
            if (video.AudienceRating.Length == 0) video.AudienceRating = "7.0";
            if (video.ContentRatingAge.Length == 0) video.ContentRatingAge = "0";
        }

        return PlexResults.Container(this, new XmlMediaContainer
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
        });
    }

    [HttpGet("/library/metadata/{id:int}/children")]
    [Produces("application/xml", "application/json")]
    public IActionResult Children(int id) => PlexResults.Empty(this);

    // Plex appends an updatedAt cache-buster to every image URL. Accept it with or without.
    [HttpGet("/library/metadata/{id:int}/thumb")]
    [HttpGet("/library/metadata/{id:int}/thumb/{cacheBuster}")]
    public async Task<IActionResult> Thumb(int id, CancellationToken ct)
    {
        var item = await _repo.GetItemAsync(id, ct);
        if (item is null || string.IsNullOrEmpty(item.PosterPath) || !System.IO.File.Exists(item.PosterPath))
            return NotFound();

        return PhysicalFile(item.PosterPath, GetContentType(item.PosterPath));
    }

    [HttpGet("/library/metadata/{id:int}/art")]
    [HttpGet("/library/metadata/{id:int}/art/{cacheBuster}")]
    public async Task<IActionResult> Art(int id, CancellationToken ct)
    {
        var item = await _repo.GetItemAsync(id, ct);
        if (item is null || string.IsNullOrEmpty(item.ArtPath) || !System.IO.File.Exists(item.ArtPath))
            return NotFound();

        return PhysicalFile(item.ArtPath, GetContentType(item.ArtPath));
    }

    [HttpGet("/library/metadata/{id:int}/squareArt")]
    [HttpGet("/library/metadata/{id:int}/squareArt/{cacheBuster}")]
    public async Task<IActionResult> SquareArt(int id, CancellationToken ct) => await Art(id, ct);

    [HttpGet("/library/metadata/{id:int}/clearLogo")]
    [HttpGet("/library/metadata/{id:int}/clearLogo/{cacheBuster}")]
    public IActionResult ClearLogo() => NotFound();

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
