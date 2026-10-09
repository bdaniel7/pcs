using Microsoft.AspNetCore.Mvc;

namespace PlexCompatibleServer.Api.Serialization;

public static class PlexResults
{
    public static IActionResult Container<T>(ControllerBase controller, T model)
    {
        var mediaType = PlexResults.mediaType(controller);
        return controller.Content(render(controller, model), mediaType, System.Text.Encoding.UTF8);
    }

    public static IActionResult Empty(ControllerBase controller)
        => Container(controller, new XmlMediaContainer { Size = 0 });

    /// <summary>
    /// Plex-shaped error body. The client reads status codes on media endpoints, so this keeps the
    /// XML/JSON dialect consistent with the success path instead of returning a bare status.
    /// </summary>
    public static IActionResult Error(ControllerBase controller, System.Net.HttpStatusCode status, string message)
    {
        var container = new XmlMediaContainer
        {
            Size = 0,
            Error = "1",
            Message = message,
            Status = ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return new ContentResult
        {
            Content = render(controller, container),
            ContentType = mediaType(controller),
            StatusCode = (int)status
        };
    }

    private static string render<T>(ControllerBase controller, T model)
        => WantsJson(controller)
            ? PlexJson.Serialize(model)
            : PlexXml.Serialize(model);

    private static string mediaType(ControllerBase controller)
        => WantsJson(controller) ? "application/json" : "application/xml";

    public static bool WantsJson(ControllerBase controller)
    {
        var accept = controller.Request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(accept)) return false;

        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            // Honour an explicit XML preference if the client listed it first.
            var xmlIndex = accept.IndexOf("application/xml", StringComparison.OrdinalIgnoreCase);
            var jsonIndex = accept.IndexOf("application/json", StringComparison.OrdinalIgnoreCase);
            if (xmlIndex >= 0 && xmlIndex < jsonIndex) return false;
            return true;
        }

        return false;
    }
}
