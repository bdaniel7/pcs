using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class TimelineController : ControllerBase
{
    [HttpGet("/:/timeline")]
    [Produces("application/xml", "application/json")]
    public IActionResult Timeline(
        [FromQuery] int ratingKey = 0,
        [FromQuery] int time = 0,
        [FromQuery] int duration = 0,
        [FromQuery] string state = "",
        [FromQuery] int playQueueItemID = 0) => PlexResults.Empty(this);

    [HttpGet("/:/scrobble")]
    [Produces("application/xml", "application/json")]
    public IActionResult Scrobble([FromQuery] int key = 0, [FromQuery] int identifier = 0) =>
        PlexResults.Empty(this);
}
