using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class HubController : ControllerBase
{
    private const int DefaultHubSize = 6;
    private const int MaxHubSize = 50;

    private readonly IMediaRepository _repo;
    private readonly ServerOptions _options;

    public HubController(IMediaRepository repo, ServerOptions options)
    {
        _repo = repo;
        _options = options;
    }

    [HttpGet("/hubs")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Hubs(CancellationToken ct)
    {
        var limit = ResolveLimit();
        var libraries = await _repo.GetLibrariesAsync(ct);
        var movieLibraries = libraries.Where(x => x.Type == LibraryType.Movie).ToList();
        var showLibraries = libraries.Where(x => x.Type == LibraryType.Show).ToList();

        var hubs = new List<XmlHub>
        {
            EmptyHub("/hubs/home/continueWatching", "Continue Watching", "mixed", "home.continue", "hub.home.continue"),
            EmptyHub("/hubs/home/onDeck", "On Deck", "episode", "home.ondeck", "hub.home.ondeck")
        };

        if (movieLibraries.Count > 0)
        {
            hubs.Add(await RecentHub(movieLibraries, 1, "movie", "Recently Added Movies",
                "home.movies.recent", "hub.home.movies.recent", limit, ct));
        }

        if (showLibraries.Count > 0)
        {
            hubs.Add(await RecentHub(showLibraries, 2, "mixed", "Recently Added TV",
                "home.television.recent", "hub.home.television.recent", limit, ct));
        }

        hubs.Add(EmptyHub("/hubs/home/recentlyAdded?type=8", "Recently Added Music", "album",
            "home.music.recent", "hub.home.music.recent"));
        hubs.Add(EmptyHub("/hubs/home/recentlyAdded?type=13", "Recently Added Photos", "photo",
            "home.photos.recent", "hub.home.photos.recent"));
        hubs.Add(EmptyHub("/hubs/home/recentlyAdded?type=1&personal=1", "Recently Added Videos", "clip",
            "home.videos.recent", "hub.home.videos.recent"));
        hubs.Add(EmptyHub("/playlists/all?type=15&sort=lastViewedAt:desc&playlistType=video,audio",
            "Recent Playlists", "playlist", "home.playlists", "hub.home.playlists"));

        return PlexResults.Container(this, new XmlHubContainer
        {
            Size = hubs.Count,
            Hubs = hubs
        });
    }

    [HttpGet("/hubs/continueWatching")]
    [Produces("application/xml", "application/json")]
    public IActionResult ContinueWatching()
    {
        var hubs = new List<XmlHub>
        {
            EmptyHub("/hubs/continueWatching/items", "Continue Watching", "mixed",
                "continueWatching", "hub.continueWatching")
        };

        return PlexResults.Container(this, new XmlHubContainer { Size = hubs.Count, Hubs = hubs });
    }

    [HttpGet("/hubs/promoted")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Promoted(CancellationToken ct)
    {
        var libraryId = int.TryParse(Request.Query["contentDirectoryID"], out var parsed) ? parsed : 0;
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var kind = library.Type == LibraryType.Movie ? "movie" : "show";
        var items = await _repo.GetItemsAsync(libraryId, ct);
        var hub = SectionHub(
            $"/library/sections/{libraryId}/all?sort=addedAt:desc",
            $"Recently Added in {library.Name}",
            kind,
            $"{kind}.recentlyadded.{libraryId}",
            $"hub.{kind}.recentlyadded",
            items,
            ResolveLimit());

        return PlexResults.Container(this, new XmlHubContainer
        {
            Size = 1,
            Hubs = { hub }
        });
    }

    [HttpGet("/hubs/sections/{libraryId:int}")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> SectionHubs(int libraryId, CancellationToken ct)
    {
        var library = await _repo.GetLibraryAsync(libraryId, ct);
        if (library is null) return NotFound();

        var limit = ResolveLimit();
        var isMovie = library.Type == LibraryType.Movie;
        var kind = isMovie ? "movie" : "show";

        var items = await _repo.GetItemsAsync(libraryId, ct);
        var recent = items.OrderByDescending(x => x.UpdatedAt).Take(limit).ToList();

        var recentlyAdded = SectionHub(
            $"/library/sections/{libraryId}/all?sort=addedAt:desc",
            $"Recently Added in {library.Name}",
            kind,
            $"{kind}.recentlyadded.{libraryId}",
            $"hub.{kind}.recentlyadded",
            items,
            limit);

        var hubs = new List<XmlHub>
        {
            EmptyHub($"/hubs/sections/{libraryId}/continueWatching/items", "Continue Watching", kind,
                $"{kind}.inprogress.{libraryId}", $"hub.{kind}.inprogress"),
            SectionHub(
                $"/library/sections/{libraryId}/all?sort=originallyAvailableAt:desc&originallyAvailableAt>=-1y",
                $"Recently Released {(isMovie ? "Movies" : "TV")}",
                kind,
                $"{kind}.recentlyreleased.{libraryId}",
                $"hub.{kind}.recentlyreleased",
                items,
                limit),
            recentlyAdded,
            EmptyHub($"/library/sections/{libraryId}/all?unwatched=1&sort=addedAt:desc",
                $"Unwatched {(isMovie ? "Movies" : "TV")}", kind,
                $"{kind}.topunwatched.{libraryId}", $"hub.{kind}.topunwatched"),
            EmptyHub(
                $"/library/sections/{libraryId}/all?sort=lastViewedAt:desc&unwatched=0&viewOffset=0",
                $"Recently Watched {(isMovie ? "Movies" : "TV")}", kind,
                $"{kind}.recentlyviewed.{libraryId}", $"hub.{kind}.recentlyviewed")
        };

        return PlexResults.Container(this, new XmlSectionHubContainer
        {
            Size = hubs.Count,
            LibrarySectionID = libraryId.ToString(),
            LibrarySectionTitle = library.Name,
            LibrarySectionUUID = _options.LibraryUuid(libraryId, library.Name, kind),
            Hubs = hubs
        });
    }

    // A browser client follows each hub's "key" to load its rows. Every key advertised above
    // must resolve, or the client renders an empty shelf instead of the hub we just returned.

    [HttpGet("/hubs/home/continueWatching")]
    [HttpGet("/hubs/home/onDeck")]
    [HttpGet("/hubs/sections/{libraryId:int}/continueWatching/items")]
    [Produces("application/xml", "application/json")]
    public IActionResult EmptyHubItems()
        => PlexResults.Container(this, new XmlMediaContainer { Size = 0, MixedParents = "1" });

    /// <summary>
    /// The detail screen loads this hub for its "More Like This" row. The official response to
    /// <c>/hubs/metadata/826/related?includeMeta=1&amp;wait=1</c> is a single hub carrying
    /// <c>type="movie"</c>, <c>context="hub.movie.similar"</c>, <c>hubIdentifier="movie.similar"</c>,
    /// <c>key="/library/metadata/826/similar"</c>, <c>size</c> equal to the row count, and
    /// <c>hubKey</c> as a comma-joined list of <c>/library/metadata/{id}</c> keys. Only movie items
    /// trigger this call on the LG client, and it is the last request before the detail screen
    /// either renders or reports that content could not be loaded.
    /// </summary>
    [HttpGet("/hubs/metadata/{id:int}/related")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Related(int id, CancellationToken ct)
    {
        // TEMPORARY DIAGNOSTIC - see OfficialReplay. Replays the official related-hub body so the
        // movie path is exercised end to end with genuine Plex bytes.
        if (OfficialReplay.HubEnabled)
        {
            var replay = await OfficialReplay.ReadAsync("084");
            if (replay is not null)
            {
                replay = replay
                    .Replace("/library/metadata/826", $"/library/metadata/{id}")
                    .Replace("\"ratingKey\":\"826\"", $"\"ratingKey\":\"{id}\"")
                    .Replace("\"ratingKey\": \"826\"", $"\"ratingKey\": \"{id}\"");
                return Content(replay, "application/json");
            }
        }

        var item = await _repo.GetItemAsync(id, ct);
        if (item is null) return NotFound();

        var library = item.Library;
        var isMovie = library.Type == LibraryType.Movie;
        var kind = isMovie ? "movie" : "episode";

        var candidates = await _repo.GetItemsAsync(library.Id, ct);
        var related = candidates
            .Where(x => x.Id != id)
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(ResolveLimit())
            .ToList();

        var relatedVideos = related
            .Select(x => LibraryController.ToVideo(x, includeLibrarySection: true))
            .ToList();

        // Official rows in this hub carry the same scraped-metadata sections as the detail screen.
        foreach (var video in relatedVideos) video.EmitEmptyMetadataSections = true;

        var hub = new XmlHub
        {
            Key = $"/library/metadata/{id}/similar",
            HubKey = string.Join(",", related.Select(x => $"/library/metadata/{x.Id}")),
            Title = isMovie ? "More Like This" : "Related Episodes",
            Type = kind,
            HubIdentifier = $"{kind}.similar",
            Context = $"hub.{kind}.similar",
            Size = related.Count,
            More = candidates.Count - 1 > related.Count ? "1" : "0",
            Videos = relatedVideos
        };

        return PlexResults.Container(this, new XmlSectionHubContainer
        {
            Size = 1,
            LibrarySectionID = library.Id.ToString(),
            LibrarySectionTitle = library.Name,
            LibrarySectionUUID = _options.LibraryUuid(library.Id, library.Name, isMovie ? "movie" : "show"),
            Hubs = { hub }
        });
    }

    [HttpGet("/hubs/home/recentlyAdded")]
    [HttpGet("/playlists/all")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> HomeRecentlyAdded(int? type, CancellationToken ct)
    {
        var libraries = await _repo.GetLibrariesAsync(ct);

        // Plex "type" ids: 1 = movie, 2 = show, 8 = music, 13 = photo, 15 = playlist.
        // Only movie and show are backed by real data here; the rest resolve to empty shelves.
        var wanted = type switch
        {
            1 => LibraryType.Movie,
            2 => LibraryType.Show,
            _ => (LibraryType?)null
        };

        var items = new List<MediaItem>();
        if (wanted is not null)
        {
            foreach (var library in libraries.Where(x => x.Type == wanted.Value))
            {
                items.AddRange(await _repo.GetItemsAsync(library.Id, ct));
            }
        }

        var recent = items.OrderByDescending(x => x.UpdatedAt).Take(ResolveLimit()).ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => LibraryController.ToVideo(x)).ToList()
        });
    }

    private int ResolveLimit()
    {
        if (int.TryParse(Request.Query["count"], out var requested) && requested > 0)
        {
            return Math.Min(requested, MaxHubSize);
        }
        return DefaultHubSize;
    }

    private async Task<XmlHub> RecentHub(
        List<MediaLibrary> libraries,
        int plexTypeId,
        string hubType,
        string title,
        string hubIdentifier,
        string context,
        int limit,
        CancellationToken ct)
    {
        var items = new List<MediaItem>();
        foreach (var library in libraries)
        {
            items.AddRange(await _repo.GetItemsAsync(library.Id, ct));
        }

        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(limit)
            .ToList();

        var videos = recent
            .Select(x => LibraryController.ToVideo(x, includeLibrarySection: true))
            .ToList();

        return new XmlHub
        {
            HubKey = string.Join(",", recent.Select(x => $"/library/metadata/{x.Id}")),
            Key = $"/hubs/home/recentlyAdded?type={plexTypeId}",
            Title = title,
            Type = hubType,
            HubIdentifier = hubIdentifier,
            Context = context,
            Size = videos.Count,
            More = items.Count > videos.Count ? "1" : "0",
            Videos = videos
        };
    }

    private static XmlHub SectionHub(
        string key,
        string title,
        string hubType,
        string hubIdentifier,
        string context,
        IReadOnlyList<MediaItem> items,
        int limit)
    {
        var recent = items.OrderByDescending(x => x.UpdatedAt).Take(limit).ToList();
        var videos = recent.Select(x => LibraryController.ToVideo(x)).ToList();

        return new XmlHub
        {
            HubKey = string.Join(",", recent.Select(x => $"/library/metadata/{x.Id}")),
            Key = key,
            Title = title,
            Type = hubType,
            HubIdentifier = hubIdentifier,
            Context = context,
            Size = videos.Count,
            More = items.Count > videos.Count ? "1" : "0",
            Videos = videos
        };
    }

    private static XmlHub EmptyHub(string key, string title, string type, string hubIdentifier, string context)
        => new()
        {
            Key = key,
            Title = title,
            Type = type,
            HubIdentifier = hubIdentifier,
            Context = context,
            Size = 0,
            More = "0"
        };
}
