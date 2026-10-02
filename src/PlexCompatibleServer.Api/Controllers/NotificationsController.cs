using System.Net.WebSockets;
using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Api.Serialization;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class NotificationsController : ControllerBase
{
    [HttpGet("/:/websockets/notifications")]
    public async Task<IActionResult> Notifications(CancellationToken ct)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return PlexResults.Empty(this);
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var buffer = new byte[8 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }

        return new EmptyResult();
    }
}
