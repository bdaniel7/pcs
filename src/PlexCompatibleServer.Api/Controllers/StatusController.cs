using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class StatusController : ControllerBase
{
    private readonly ServerOptions _options;
    public StatusController(ServerOptions options) => _options = options;

    [HttpGet("/status/sessions")]
    [Produces("application/xml", "application/json")]
    public IActionResult Sessions() => PlexResults.Container(this, new XmlStatusContainer
    {
        Size = 0,
        Metadata =
        {
            new XmlStatusMetadata
            {
                MachineIdentifier = _options.MachineIdentifier,
                State = "stopped"
            }
        }
    });
}
