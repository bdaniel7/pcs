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
    private readonly IMetadataService _metadata;

    public HubController(IMediaRepository repo, ServerOptions options, IMetadataService metadata)
    {
        _repo = repo;
        _options = options;
        _metadata = metadata;
    }

    [HttpGet("/hubs")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Hubs(CancellationToken ct)
    {
        var limit = ResolveLimit();
        var libraries = await _repo.GetLibrariesAsync(ct);
        var movieLibraries = libraries.Where(x => x.Type == LibraryType.Movie).ToList();
        var showLibraries = libraries.Where(x => x.Type == LibraryType.Show).ToList();
        var progress = await _repo.GetInProgressAsync(null, limit, ct);

        var hubs = new List<XmlHub>
        {
            ProgressHubOrEmpty("/hubs/home/continueWatching", "Continue Watching", "mixed",
                "home.continue", "hub.home.continue", progress),
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
    public async Task<IActionResult> ContinueWatching(CancellationToken ct)
    {
        var progress = await _repo.GetInProgressAsync(null, ResolveLimit(), ct);
        var hubs = new List<XmlHub>
        {
            ProgressHubOrEmpty("/hubs/continueWatching/items", "Continue Watching", "mixed",
                "continueWatching", "hub.continueWatching", progress)
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
        var progress = await _repo.GetInProgressAsync(libraryId, limit, ct);

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
            ProgressHubOrEmpty($"/hubs/sections/{libraryId}/continueWatching/items", "Continue Watching", kind,
                $"{kind}.inprogress.{libraryId}", $"hub.{kind}.inprogress", progress),
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
    [Produces("application/xml", "application/json")]
    public Task<IActionResult> HomeContinueWatching(CancellationToken ct)
        => ProgressContainer(null, ct);

    // On Deck (next unwatched episode of a partly watched show) stays empty: episode order lives
    // only in the cached sidecar titles, and a wrong next-episode is worse than none.
    [HttpGet("/hubs/home/onDeck")]
    [Produces("application/xml", "application/json")]
    public IActionResult OnDeckItems()
        => PlexResults.Container(this, new XmlMediaContainer { Size = 0, MixedParents = "1" });

    [HttpGet("/hubs/sections/{libraryId:int}/continueWatching/items")]
    [Produces("application/xml", "application/json")]
    public Task<IActionResult> SectionContinueWatching(int libraryId, CancellationToken ct)
        => ProgressContainer(libraryId, ct);

    private async Task<IActionResult> ProgressContainer(int? libraryId, CancellationToken ct)
    {
        var items = await _repo.GetInProgressAsync(libraryId, ResolveLimit(), ct);
        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = items.Count,
            MixedParents = "1",
            TotalSize = items.Count.ToString(),
            Videos = items.Select(x => LibraryController.ToVideoEnriched(_metadata, x)).ToList()
        });
    }

    /// <summary>
    /// The detail screen loads this hub for its "More Like This" row. The official response to
    /// <c>/hubs/metadata/826/related?includeMeta=1&amp;wait=1</c> is a single hub carrying
    /// <c>type="movie"</c>, <c>context="hub.movie.similar"</c>, <c>hubIdentifier="movie.similar"</c>,
    /// <c>key="/library/metadata/826/similar"</c>, <c>size</c> equal to the row count, and
    /// <c>hubKey</c> as a single <c>/library/metadata/</c> prefix followed by the comma-joined
    /// rating keys of the rows (<c>/library/metadata/3,644,645</c>). Only movie items
    /// trigger this call on the LG client, and it is the last request before the detail screen
    /// either renders or reports that content could not be loaded.
    /// </summary>
    [HttpGet("/hubs/metadata/{id:int}/related")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Related(int id, CancellationToken ct)
    {
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

        // Official rows in this hub carry the same scraped-metadata sections as the detail screen,
        // and the client dereferences their credit lists the same way it does the detail item's -
        // empty lists crash the screen. Hub rows additionally state chapterSource; plain metadata
        // items do not.
        for (var i = 0; i < related.Count; i++)
        {
            await MetadataParity.ApplyAsync(_metadata, related[i], relatedVideos[i], related[i].Id,
                includeExtras: false, ct: ct);
            relatedVideos[i].ChapterSource = "media";
        }

        var hub = new XmlHub
        {
            Key = $"/library/metadata/{id}/similar",
            HubKey = "/library/metadata/" + string.Join(",", related.Select(x => x.Id)),
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

        IReadOnlyList<MediaItem> items = [];
        if (wanted is not null)
        {
            items = await _repo.GetItemsByLibrariesAsync(
                libraries.Where(x => x.Type == wanted.Value).Select(x => x.Id).ToList(), ct);
        }

        var recent = items.OrderByDescending(x => x.UpdatedAt).Take(ResolveLimit()).ToList();

        return PlexResults.Container(this, new XmlMediaContainer
        {
            Size = recent.Count,
            MixedParents = "1",
            TotalSize = recent.Count.ToString(),
            Videos = recent.Select(x => LibraryController.ToVideoEnriched(_metadata, x)).ToList()
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
        var items = await _repo.GetItemsByLibrariesAsync(libraries.Select(x => x.Id).ToList(), ct);

        var recent = items
            .OrderByDescending(x => x.UpdatedAt)
            .Take(limit)
            .ToList();

        var videos = recent
            .Select(x => LibraryController.ToVideoEnriched(_metadata, x, includeLibrarySection: true))
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

    private XmlHub SectionHub(
        string key,
        string title,
        string hubType,
        string hubIdentifier,
        string context,
        IReadOnlyList<MediaItem> items,
        int limit)
    {
        var recent = items.OrderByDescending(x => x.UpdatedAt).Take(limit).ToList();
        var videos = recent.Select(x => LibraryController.ToVideoEnriched(_metadata, x)).ToList();

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

    /// <summary>
    /// The Continue Watching shelf: the same empty shape as before when nothing is in progress,
    /// otherwise one row per half-watched item, newest interaction first. Rows carry viewOffset,
    /// which is what the client uses both for the progress ring and to seek on resume.
    /// </summary>
    private XmlHub ProgressHubOrEmpty(
        string key,
        string title,
        string type,
        string hubIdentifier,
        string context,
        IReadOnlyList<MediaItem> items)
    {
        if (items.Count == 0) return EmptyHub(key, title, type, hubIdentifier, context);

        var videos = items.Select(x => LibraryController.ToVideoEnriched(_metadata, x)).ToList();

        return new XmlHub
        {
            HubKey = string.Join(",", items.Select(x => $"/library/metadata/{x.Id}")),
            Key = key,
            Title = title,
            Type = type,
            HubIdentifier = hubIdentifier,
            Context = context,
            Size = videos.Count,
            More = "0",
            Videos = videos
        };
    }
}
