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

    // TEMPORARY DIAGNOSTIC - set when replaying an official body with regions of ours merged in.
    private string? officialBody;

    public MetadataController(IMediaRepository repo, ServerOptions options, StreamSelectionStore selections)
    {
        _repo = repo;
        _options = options;
        _selections = selections;
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
        // TEMPORARY DIAGNOSTIC - delete once the movie detail path is understood.
        // Replays the official Plex response bodies captured for this client, re-pointed at the
        // requested rating key. If the detail screen renders from these bytes the fault is in the
        // data we generate; if it still fails, the fault is not in the response body at all.
if (OfficialReplay.ItemEnabled)
        {
            var rich = Request.Query.ContainsKey("includeExternalMetadata");
            var body = await OfficialReplay.ReadAsync(rich ? "074" : "016");
            if (body is not null)
            {
                body = body
                    .Replace("\"/library/metadata/826", $"\"/library/metadata/{id}")
                    .Replace("\"ratingKey\":\"826\"", $"\"ratingKey\":\"{id}\"")
                    .Replace("\"ratingKey\": \"826\"", $"\"ratingKey\":\"{id}\"")
                    .Replace("\"ratingKey\":826", $"\"ratingKey\":{id}");

                // With no regions requested the official body is returned untouched.
                if (OfficialReplay.MergeRegions.Count == 0) return Content(body, "application/json");
                officialBody = body;
            }
        }

        var item = await _repo.GetItemAsync(id, ct);
        if (item is null) return NotFound();

        var kind = item.Library.Type == LibraryType.Movie ? "movie" : "show";

        var video = LibraryController.ToVideo(item, selections: _selections);

        // Overlay real metadata from the official Plex sidecar when available.
        try
        {
            ExternalMetadata.Apply(item, video);
        }
        catch (Exception)
        {
            // Ignore failures; fall back to generated/placeholder metadata.
        }

        // The detail screen renders from the *first* item response it receives, which carries only
        // includeUserState=1 and no includeExternalMetadata. Gating the scraped-metadata sections
        // on includeExternalMetadata therefore left the fields the movie screen reads (summary,
        // Genre, Director, Writer, Role, Rating, ...) absent from the response it actually renders
        // from, and the screen reports "content could not be loaded". Official Plex states these
        // fields on every item response even when it knows nothing about them, so we do too.
        video.EmitEmptyMetadataSections = true;
        video.Extras = new XmlExtras();

        // The two remaining fields official Plex states that we have no scraped data for. They are
        // stated rather than omitted for the same reason as the tag lists: the detail screen reads
        // them, and an absent key is not the same as an empty one. The values are neutral
        // placeholders, not real data.
        video.UltraBlurColors ??= new XmlUltraBlurColors
        {
            TopLeft = "1c1c1c",
            TopRight = "1c1c1c",
            BottomRight = "0d0d0d",
            BottomLeft = "0d0d0d",
        };
        if (video.CommonSenseMedia.Count == 0)
        {
            video.CommonSenseMedia.Add(new XmlCommonSenseMedia
            {
                Id = "0",
                OneLiner = "Unknown",
                AgeRatings = { new XmlAgeRating { Type = "official", Rating = 0, Age = 0 } },
            });
        }

        // Official Plex always states a release date here. We only know the year, so state the
        // year rather than omitting the field the detail screen reads.
        if (video.OriginallyAvailableAt.Length == 0 && video.Year.Length > 0)
        {
            video.OriginallyAvailableAt = $"{video.Year}-01-01";
        }

        SeedUnknownTags(video, id);

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

        if (officialBody is not null)
        {
            var merged = OfficialReplay.Merge(officialBody, PlexJson.Serialize(container));
            if (merged is not null) return Content(merged, "application/json");
        }

        return PlexResults.Container(this, container);
    }

    /// <summary>
    /// Gives every tag list on a detail response at least one entry.
    ///
    /// The client dereferences the first element of the credit lists (Director, Writer, Role) and
    /// the genre/country lists without checking that one exists. We carry no cast or crew metadata,
    /// so those lists are empty, the dereference throws, and the movie screen falls back to
    /// "content could not be loaded" - which is why TV Shows renders the same file without trouble:
    /// an episode screen never reads these lists.
    ///
    /// Seeding a single "Unknown" entry keeps every list non-empty for the client. It is a
    /// placeholder, not real metadata, and it renders on screen as an "Unknown" chip.
    ///
    /// Ids are derived from the item's rating key so a given entry keeps the same id across
    /// responses, matching the way official Plex numbers its tags.
    /// </summary>
    private static void SeedUnknownTags(XmlVideo video, int ratingKey)
    {
        XmlTag Seed(int offset, string filterName) => new()
        {
            Id = (ratingKey * 10 + offset).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Filter = $"{filterName}={ratingKey * 10 + offset}",
            Tag = "Unknown",
        };

        if (video.Directors.Count == 0) video.Directors.Add(Seed(1, "director"));
        if (video.Writers.Count == 0) video.Writers.Add(Seed(2, "writer"));
        // Role entries are filtered by "actor", not "role": that is the filter name the client uses to
        // build its person pages, so a "role=" filter would produce dead links.
        if (video.Roles.Count == 0) video.Roles.Add(Seed(3, "actor"));
        if (video.Genres.Count == 0) video.Genres.Add(Seed(4, "genre"));
        if (video.Countries.Count == 0) video.Countries.Add(Seed(5, "country"));
        if (video.Producers.Count == 0) video.Producers.Add(Seed(6, "producer"));
        if (video.Ratings.Count == 0)
        {
            video.Ratings.Add(new XmlRating
            {
                Image = "imdb://image.rating",
                Value = "0",
                Type = "audience",
            });
        }
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
