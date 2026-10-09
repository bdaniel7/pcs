using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Real Plex answers /photo/:/transcode with an image sized to the requested width/height;
/// handing back the full-size original instead makes the client downscale in its compositor,
/// which reads as grain on a 4K panel. This scales cached artwork to the requested box with
/// lanczos (aspect preserved, never enlarged unless the caller passes upscale), caches the
/// result next to the source so a grid repaint is a disk hit after the first pass, and falls
/// back to the original file whenever it cannot prove it did the work - unknown format, no
/// ffmpeg, failed run. The endpoint must never 404 or serve a broken file.
/// </summary>
public sealed class ImageTranscoder
{
    private readonly MediaArtOptions options;
    private readonly ILogger<ImageTranscoder> log;
    private readonly SemaphoreSlim throttle;

    public ImageTranscoder(MediaArtOptions options, ILogger<ImageTranscoder> log)
    {
        this.options = options;
        this.log = log;
        throttle = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
    }

    /// <summary>
    /// Returns a path whose image matches the requested box as closely as aspect allows, or
    /// <paramref name="sourcePath"/> unchanged when no resize is needed or possible.
    /// </summary>
    public async Task<string> ResizeAsync(string sourcePath, int width, int height,
                                          bool upscale, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return sourcePath;
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return sourcePath;

        if (!ImageSize.TryGet(sourcePath, out var sourceWidth, out var sourceHeight))
            return sourcePath;

        var (targetWidth, targetHeight) = FitWithin(sourceWidth, sourceHeight, width, height, upscale);
        if (targetWidth == sourceWidth && targetHeight == sourceHeight) return sourcePath;

        // Keep png inputs as png so transparency survives; photos go out as jpg.
        var extension = Path.GetExtension(sourcePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : ".jpg";
        var output = Path.Combine(cacheDirectory,
            $"{SourceKey(sourcePath)}-{targetWidth}x{targetHeight}{extension}");

        if (hasContent(output)) return output;

        var ffmpeg = FfmpegLocator.Executable;
        if (string.IsNullOrEmpty(ffmpeg)) return sourcePath;

        await throttle.WaitAsync(ct);
        try
        {
            if (hasContent(output)) return output;

            Directory.CreateDirectory(cacheDirectory);
            var tmp = Path.Combine(cacheDirectory, $".transcode.{Guid.NewGuid():N}.tmp{extension}");

            var ok = await runAsync(ffmpeg, buildArgs(sourcePath, tmp, targetWidth, targetHeight, extension), ct);
            if (ok && hasContent(tmp))
            {
                File.Move(tmp, output, overwrite: true);
                return output;
            }

            tryDelete(tmp);
            return sourcePath;
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>Aspect-preserving fit of the source into the box; <paramref name="upscale"/>=false never enlarges.</summary>
    internal static (int Width, int Height) FitWithin(int sourceWidth, int sourceHeight,
                                                      int boxWidth, int boxHeight, bool upscale)
    {
        var scale = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        if (!upscale) scale = Math.Min(scale, 1.0);

        var width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        return (width, height);
    }

    internal static string SourceKey(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var material = $"{sourcePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .Substring(0, 16).ToLowerInvariant();
    }

    private string cacheDirectory =>
        string.IsNullOrEmpty(options.CacheDirectory)
            ? MediaArtOptions.DefaultCacheDirectory
            : options.CacheDirectory;

    private static string[] buildArgs(string source, string output, int width, int height, string extension)
    {
        var args = new List<string>
        {
            "-i", source,
            "-vf", $"scale={width}:{height}:flags=lanczos"
        };

        // -q:v is a JPEG encoder option; the png encoder rejects unknown options outright.
        if (extension is ".jpg") args.AddRange(["-q:v", "3"]);

        args.AddRange(["-y", output]);

        return [.. args];
    }

    private static bool hasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private async Task<bool> runAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args) info.ArgumentList.Add(arg);

        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null) return false;

            // Drain both pipes or ffmpeg deadlocks once the buffers fill.
            var drained = Task.WhenAll(
                process.StandardError.ReadToEndAsync(ct),
                process.StandardOutput.ReadToEndAsync(ct));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(1));

            await process.WaitForExitAsync(timeout.Token);
            await drained;

            if (process.ExitCode != 0)
                log.LogDebug("ffmpeg exited {Code} resizing {Source}", process.ExitCode, args[1]);

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Timed out resizing {Source}", args[1]);
            tryKill(process);
            return false;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Resize failed for {Source}", args[1]);
            tryKill(process);
            return false;
        }
    }

    private static void tryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private static void tryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
