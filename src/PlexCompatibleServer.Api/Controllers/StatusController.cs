using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class StatusController : ControllerBase
{
    private readonly ServerOptions options;
    public StatusController(ServerOptions options) => this.options = options;

    [HttpGet("/status/sessions")]
    [Produces("application/xml", "application/json")]
    public IActionResult Sessions() => PlexResults.Container(this, new XmlStatusContainer
    {
        Size = 0,
        Metadata =
        {
            new XmlStatusMetadata
            {
                MachineIdentifier = options.MachineIdentifier,
                State = "stopped"
            }
        }
    });
}
