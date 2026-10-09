using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class MediaController : ControllerBase
{
    private readonly IMediaRepository repo;
    private readonly ServerOptions options;

    public MediaController(IMediaRepository repo, ServerOptions options)
    {
        this.repo = repo;
        this.options = options;
    }

    [HttpGet("/media/providers")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Providers(CancellationToken ct)
    {
        var libraries = await repo.GetLibrariesAsync(ct);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var content = new XmlProviderFeature
        {
            Key = "/library/sections",
            Type = "content",
            Directories =
            {
                new XmlProviderDirectory { HubKey = "/hubs", Title = "Home" }
            }
        };

        foreach (var library in libraries)
        {
            var type = PlexType(library.Type);
            content.Directories.Add(new XmlProviderDirectory
            {
                Agent = library.Type == LibraryType.Movie ? "tv.plex.agents.movie" : "tv.plex.agents.series",
                Language = "en-US",
                Refreshing = "0",
                Scanner = library.Type == LibraryType.Movie ? "Plex Movie" : "Plex TV Series",
                Uuid = options.LibraryUuid(library.Id, library.Name, type),
                Id = library.Id.ToString(),
                Key = $"/library/sections/{library.Id}",
                HubKey = $"/hubs/sections/{library.Id}",
                Type = type,
                Title = library.Name,
                UpdatedAt = timestamp.ToString(),
                ScannedAt = timestamp.ToString(),
                Pivots =
                {
                    new XmlProviderPivot
                    {
                        Id = "recommended",
                        Key = $"/hubs/sections/{library.Id}",
                        Type = "hub",
                        Title = "Recommended",
                        Context = "content.discover",
                        Symbol = "star"
                    },
                    new XmlProviderPivot
                    {
                        Id = "library",
                        Key = $"/library/sections/{library.Id}/all?type={plexTypeId(library.Type)}",
                        Type = "list",
                        Title = "Library",
                        Context = "content.library",
                        Symbol = "library"
                    }
                }
            });
        }

        var provider = new XmlMediaProvider
        {
            Features =
            {
                content,
                new XmlProviderFeature { Key = "/hubs/search", Type = "search" },
                new XmlProviderFeature { Key = "/library/matches", Type = "match" },
                new XmlProviderFeature { Key = "/library/metadata", Type = "metadata" },
                new XmlProviderFeature { Key = "/:/rate", Type = "rate" },
                new XmlProviderFeature { Key = "/photo/:/transcode", Type = "imagetranscoder" },
                new XmlProviderFeature { Key = "/hubs/promoted", Type = "promoted" },
                new XmlProviderFeature { Key = "/hubs/continueWatching", Type = "continuewatching" },
                new XmlProviderFeature
                {
                    Key = "/actions",
                    Type = "actions",
                    Actions = { new XmlProviderAction { Id = "removeFromContinueWatching", Key = "/actions/removeFromContinueWatching" } }
                },
                new XmlProviderFeature { Key = "/playlists", Type = "playlist", Flavor = "universal" },
                new XmlProviderFeature { Key = "/playQueues", Type = "playqueue", Flavor = "universal" },
                new XmlProviderFeature
                {
                    Key = "/:/timeline",
                    Type = "timeline",
                    ScrobbleKey = "/:/scrobble",
                    UnscrobbleKey = "/:/unscrobble"
                },
                new XmlProviderFeature { Type = "manage" },
                new XmlProviderFeature { Type = "queryParser" }
            }
        };

        var result = ServerInfo.Build(new XmlMediaProviderContainer
        {
            Size = 1,
            ApiVersion = options.ApiVersion,
            MachineIdentifier = options.MachineIdentifier,
            Version = options.Version,
            FriendlyName = options.Name
        });
        result.Providers.Add(provider);

        return PlexResults.Container(this, result);
    }

    [HttpGet("/media/providers/online")]
    [Produces("application/xml", "application/json")]
    public IActionResult ProvidersOnline()
    {
        var result = ServerInfo.Build(new XmlMediaProviderContainer
        {
            Size = 0,
            ApiVersion = options.ApiVersion,
            MachineIdentifier = options.MachineIdentifier,
            Version = options.Version,
            FriendlyName = options.Name
        });
        return PlexResults.Container(this, result);
    }

    private static string plexTypeId(LibraryType type) => type == LibraryType.Movie ? "1" : "2";

    internal static string PlexType(LibraryType type) => type == LibraryType.Movie ? "movie" : "show";
}
