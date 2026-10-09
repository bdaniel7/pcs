using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class IdentityController : ControllerBase
{
    private readonly ServerOptions options;
    public IdentityController(ServerOptions options) => this.options = options;

    [HttpGet("/identity")]
    [Produces("application/xml", "application/json")]
    public IActionResult Get() => PlexResults.Container(this, new XmlIdentity
    {
        Size = 0,
        ApiVersion = options.ApiVersion,
        Claimed = "0",
        MachineIdentifier = options.MachineIdentifier,
        Version = options.Version
    });
}
