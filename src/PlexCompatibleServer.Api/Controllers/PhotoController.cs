using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Core.Interfaces;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class PhotoController : ControllerBase
{
    // 1x1 neutral grey PNG; scaled by the client. Used when no artwork exists.
    private static readonly byte[] PlaceholderPng =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly IMediaRepository _repo;

    public PhotoController(IMediaRepository repo) => _repo = repo;

    // Real Plex exposes the artwork resizer at /photo/:/transcode. The TV client asks for that exact
// path for every poster it draws, and a 404 there breaks the screens that request it. The bare
// /:/transcode spelling is kept for older clients.
[HttpGet("/photo/:/transcode")]
    [HttpGet("/:/transcode")]
    public async Task<IActionResult> Transcode(
        [FromQuery] string url = "",
        [FromQuery] int width = 0,
        [FromQuery] int height = 0,
        CancellationToken ct = default)
    {
        var item = await ResolveItemAsync(url, ct);

        if (!string.IsNullOrEmpty(item) && System.IO.File.Exists(item))
        {
            return PhysicalFile(item, ContentType(item));
        }

        // Never 404: clients treat a failed photo probe as a broken server.
        return File(PlaceholderPng, "image/png");
    }

    private async Task<string> ResolveItemAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        var match = System.Text.RegularExpressions.Regex.Match(url, @"/library/metadata/(\d+)");
        if (!match.Success) return "";

        var item = await _repo.GetItemAsync(int.Parse(match.Groups[1].Value), ct);
        if (item is null) return "";

        // The url names the slot the caller wants: /art/ is the wide backdrop, /thumb/ the portrait
        // poster. A capture of a working session shows the TV asking for a 1232x693 backdrop and
        // receiving exactly that; answering with the 600x900 poster instead hands the detail screen
        // a 2:3 image where it expects 16:9.
        var wantsArt = System.Text.RegularExpressions.Regex.IsMatch(
            url, @"/art/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (wantsArt) return item.ArtPath ?? item.PosterPath ?? "";
        return item.PosterPath ?? item.ArtPath ?? "";
    }

    private static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
