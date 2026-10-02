using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class IdentityController : ControllerBase
{
    private readonly ServerOptions _options;
    public IdentityController(ServerOptions options) => _options = options;

    [HttpGet("/identity")]
    [Produces("application/xml", "application/json")]
    public IActionResult Get() => PlexResults.Container(this, new XmlIdentity
    {
        Size = 0,
        ApiVersion = _options.ApiVersion,
        Claimed = "0",
        MachineIdentifier = _options.MachineIdentifier,
        Version = _options.Version
    });
}
