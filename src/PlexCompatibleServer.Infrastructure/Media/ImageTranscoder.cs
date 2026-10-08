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
    private readonly MediaArtOptions _options;
    private readonly ILogger<ImageTranscoder> _log;
    private readonly SemaphoreSlim _throttle;

    public ImageTranscoder(MediaArtOptions options, ILogger<ImageTranscoder> log)
    {
        _options = options;
        _log = log;
        _throttle = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
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
        var output = Path.Combine(CacheDirectory,
            $"{SourceKey(sourcePath)}-{targetWidth}x{targetHeight}{extension}");

        if (HasContent(output)) return output;

        var ffmpeg = FfmpegLocator.Executable;
        if (string.IsNullOrEmpty(ffmpeg)) return sourcePath;

        await _throttle.WaitAsync(ct);
        try
        {
            if (HasContent(output)) return output;

            Directory.CreateDirectory(CacheDirectory);
            var tmp = Path.Combine(CacheDirectory, $".transcode.{Guid.NewGuid():N}.tmp{extension}");

            var ok = await RunAsync(ffmpeg, BuildArgs(sourcePath, tmp, targetWidth, targetHeight, extension), ct);
            if (ok && HasContent(tmp))
            {
                File.Move(tmp, output, overwrite: true);
                return output;
            }

            TryDelete(tmp);
            return sourcePath;
        }
        finally
        {
            _throttle.Release();
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

    private string CacheDirectory =>
        string.IsNullOrEmpty(_options.CacheDirectory)
            ? MediaArtOptions.DefaultCacheDirectory
            : _options.CacheDirectory;

    private static string[] BuildArgs(string source, string output, int width, int height, string extension)
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

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    private async Task<bool> RunAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken ct)
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
                _log.LogDebug("ffmpeg exited {Code} resizing {Source}", process.ExitCode, args[1]);

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Timed out resizing {Source}", args[1]);
            TryKill(process);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Resize failed for {Source}", args[1]);
            TryKill(process);
            return false;
        }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
        catch { /* best effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
