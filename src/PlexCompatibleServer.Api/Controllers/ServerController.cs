using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class ServerController : ControllerBase
{
    private readonly ServerOptions options;
    private readonly IMediaRepository repo;

    public ServerController(ServerOptions options, IMediaRepository repo)
    {
        this.options = options;
        this.repo = repo;
    }

    [HttpGet("/")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Root(CancellationToken ct)
    {
        var libraries = await repo.GetLibrariesAsync(ct);

        var directories = new List<XmlRootDirectory>();
        foreach (var key in discoveryKeys)
        {
            directories.Add(new XmlRootDirectory
            {
                Count = key is "library" or "media" ? libraries.Count : 1,
                Key = key,
                Title = key
            });
        }

        return PlexResults.Container(this, ServerInfo.Build(new XmlServerInfo
        {
            Size = directories.Count,
            ApiVersion = options.ApiVersion,
            FriendlyName = options.Name,
            MachineIdentifier = options.MachineIdentifier,
            MyPlexMappingState = "unknown",
            MyPlexSigninState = "none",
            Version = options.Version,
            Directories = directories
        }));
    }

    private static readonly string[] discoveryKeys =
    [
        "actions", "activities", "butler", "channels", "clients", "devices", "diagnostics",
        "downloadQueue", "hubs", "library", "livetv", "media", "neighborhood", "playQueues",
        "playlists", "resources", "search", "server", "servers", "service", "statistics",
        "system", "transcode", "updater", "user"
    ];
}

