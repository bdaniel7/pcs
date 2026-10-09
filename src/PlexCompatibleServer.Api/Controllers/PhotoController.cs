using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class PhotoController : ControllerBase
{
    // 1x1 neutral grey PNG; scaled by the client. Used when no artwork exists.
    private static readonly byte[] PlaceholderPng =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly IMediaRepository _repo;
    private readonly ImageTranscoder _transcoder;

    public PhotoController(IMediaRepository repo, ImageTranscoder transcoder) =>
        (_repo, _transcoder) = (repo, transcoder);

    // Real Plex exposes the artwork resizer at /photo/:/transcode. The TV client asks for that exact
    // path for every poster it draws, and a 404 there breaks the screens that request it. The bare
    // /:/transcode spelling is kept for older clients.
    [HttpGet("/photo/:/transcode")]
    [HttpGet("/:/transcode")]
    public async Task<IActionResult> Transcode(
            [FromQuery] string url = "",
            [FromQuery] int width = 0,
            [FromQuery] int height = 0,
            [FromQuery] string upscale = "",
            CancellationToken ct = default)
    {
        var item = await ResolveItemAsync(url, ct);

        if (!string.IsNullOrEmpty(item) && System.IO.File.Exists(item))
        {
            // Serve the requested size, not the original: a client that downscales a 4K poster
            // in its compositor makes it look grainy, and the endpoint promises width/height.
            // Plex sends the flag as 1/0, so parse it as a string: binding a bool would make
            // the model validator 400 every grid request.
            var wantUpscale = upscale is "1" or "true";
            var path = await _transcoder.ResizeAsync(item, width, height, wantUpscale, ct);

            return PhysicalFile(path, ContentType(path));
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
        // a 2:3 image where it expects 16:9. parentThumb/grandparentThumb carry the season and show
        // posters for episode rows; each chain ends in the frame extract so the never-404 promise
        // holds even before official artwork has been downloaded.
        var wantsArt = Slot(url, "/art/");
        var wantsGrandparent = Slot(url, "/grandparentthumb/");
        var wantsParent = Slot(url, "/parentthumb/");

        if (wantsGrandparent)
            return FirstExisting(item.OfficialGrandparentPosterPath, item.OfficialPosterPath,
                                 item.PosterPath, item.ArtPath);
        if (wantsParent)
            return FirstExisting(item.OfficialParentPosterPath, item.OfficialGrandparentPosterPath,
                                 item.OfficialPosterPath, item.PosterPath, item.ArtPath);
        if (wantsArt)
            return FirstExisting(item.OfficialArtPath, item.ArtPath, item.PosterPath);
        return FirstExisting(item.OfficialPosterPath, item.PosterPath, item.ArtPath);
    }

    private static bool Slot(string url, string slot) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            url, slot, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string FirstExisting(params string?[] paths)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                return path;

        return "";
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
