using Microsoft.AspNetCore.Mvc;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// The webOS client opens a long-poll session here right after it discovers the server and
/// then repeats it for the life of the app. A 404 here makes the client re-run discovery
/// every second, so answer it like Plex does instead of leaving it unimplemented.
/// </summary>
[ApiController]
public sealed class PlayerProxyController : ControllerBase
{
    [HttpGet("/player/proxy/poll")]
    [Produces("text/plain")]
    public IActionResult Poll() => Content(string.Empty, "text/plain");
}
