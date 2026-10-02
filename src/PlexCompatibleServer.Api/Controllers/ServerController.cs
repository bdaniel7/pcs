using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class ServerController : ControllerBase
{
    private readonly ServerOptions _options;
    private readonly IMediaRepository _repo;

    public ServerController(ServerOptions options, IMediaRepository repo)
    {
        _options = options;
        _repo = repo;
    }

    [HttpGet("/")]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Root(CancellationToken ct)
    {
        var libraries = await _repo.GetLibrariesAsync(ct);

        var directories = new List<XmlRootDirectory>();
        foreach (var key in DiscoveryKeys)
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
            ApiVersion = _options.ApiVersion,
            FriendlyName = _options.Name,
            MachineIdentifier = _options.MachineIdentifier,
            MyPlexMappingState = "unknown",
            MyPlexSigninState = "none",
            Version = _options.Version,
            Directories = directories
        }));
    }

    private static readonly string[] DiscoveryKeys =
    [
        "actions", "activities", "butler", "channels", "clients", "devices", "diagnostics",
        "downloadQueue", "hubs", "library", "livetv", "media", "neighborhood", "playQueues",
        "playlists", "resources", "search", "server", "servers", "service", "statistics",
        "system", "transcode", "updater", "user"
    ];
}

