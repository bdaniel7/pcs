using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

[ApiController]
public sealed class PhotoController : ControllerBase
{
    // 1x1 neutral grey PNG; scaled by the client. Used when no artwork exists.
    private static readonly byte[] placeholderPng =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    // The metadata id appears once per artwork URL; compile the pattern once instead of
    // rebuilding it (and re-scanning the pattern) on every transcode request.
    private static readonly Regex metadataPath = new(@"/library/metadata/(\d+)", RegexOptions.Compiled);


    private readonly IMediaRepository repo;
    private readonly ImageTranscoder transcoder;

    public PhotoController(IMediaRepository repo, ImageTranscoder transcoder) =>
        (this.repo, this.transcoder) = (repo, transcoder);

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
        var item = await resolveItemAsync(url, ct);

        if (!string.IsNullOrEmpty(item) && System.IO.File.Exists(item))
        {
            // Serve the requested size, not the original: a client that downscales a 4K poster
            // in its compositor makes it look grainy, and the endpoint promises width/height.
            // Plex sends the flag as 1/0, so parse it as a string: binding a bool would make
            // the model validator 400 every grid request.
            var wantUpscale = upscale is "1" or "true";
            var path = await transcoder.ResizeAsync(item, width, height, wantUpscale, ct);

            return PhysicalFile(path, contentType(path));
        }

        // Never 404: clients treat a failed photo probe as a broken server.
        return File(placeholderPng, "image/png");
    }

    private async Task<string> resolveItemAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";

        var match = metadataPath.Match(url);
        if (!match.Success) return "";

        var item = await repo.GetItemAsync(int.Parse(match.Groups[1].Value), ct);
        if (item is null) return "";

        // The url names the slot the caller wants: /art/ is the wide backdrop, /thumb/ the portrait
        // poster. A capture of a working session shows the TV asking for a 1232x693 backdrop and
        // receiving exactly that; answering with the 600x900 poster instead hands the detail screen
        // a 2:3 image where it expects 16:9. parentThumb/grandparentThumb carry the season and show
        // posters for episode rows; each chain ends in the frame extract so the never-404 promise
        // holds even before official artwork has been downloaded.
        var wantsArt = slot(url, "/art/");
        var wantsGrandparent = slot(url, "/grandparentthumb/");
        var wantsParent = slot(url, "/parentthumb/");

        if (wantsGrandparent)
            return firstExisting(item.OfficialGrandparentPosterPath, item.OfficialPosterPath,
                                 item.PosterPath, item.ArtPath);
        if (wantsParent)
            return firstExisting(item.OfficialParentPosterPath, item.OfficialGrandparentPosterPath,
                                 item.OfficialPosterPath, item.PosterPath, item.ArtPath);
        if (wantsArt)
            return firstExisting(item.OfficialArtPath, item.ArtPath, item.PosterPath);
        return firstExisting(item.OfficialPosterPath, item.PosterPath, item.ArtPath);
    }

    // Slot markers are fixed literals ("/art/", "/parentthumb/", ...): a case-insensitive
    // substring check is all the old Regex.IsMatch did, without rebuilding a pattern per call.
    private static bool slot(string url, string slot)
        => url.Contains(slot, StringComparison.OrdinalIgnoreCase);

    private static string firstExisting(params string?[] paths)
    {
        foreach (var path in paths)
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                return path;

        return "";
    }

    private static string contentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
}
