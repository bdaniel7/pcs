using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Finds ffmpeg once per process. Both the poster generator and the stream probe depend on it,
/// so the search lives here rather than being duplicated.
/// </summary>
public static class FfmpegLocator
{
    private static readonly Lock Gate = new();
    private static string _cached = "";
    private static bool _searched;

    public static string Executable
    {
        get
        {
            if (_searched) return _cached;
            lock (Gate)
            {
                if (_searched) return _cached;
                _searched = true;
                _cached = Search();
            }

            return _cached;
        }
    }

    private static string Search()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return "";
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        yield return Environment.GetEnvironmentVariable("PLEX_FFMPEG") ?? "";

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir)) yield return Path.Combine(dir, "ffmpeg.exe");
        }

        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            yield return Path.Combine(dir, "ffmpeg.exe");

        // Common places a portable ffmpeg tends to live on Windows.
        foreach (var drive in new[] { "C", "D", "E" })
        {
            var root = $"{drive}:\\";
            if (!Directory.Exists(root)) continue;

            foreach (var sub in new[] { @"temp\dld", "Downloads", "ffmpeg\bin", "tools\ffmpeg\bin" })
                yield return Path.Combine(root, sub.Replace('/', '\\'), "ffmpeg.exe");
        }
    }
}

/// <summary>
/// Produces Plex-style artwork for media files that ship without a poster.
/// Real Plex grabs a still from the video itself, so this does the same with ffmpeg:
/// a 2:3 poster for list/grid art and a 16:9 backdrop for the background layer.
/// Generated files are cached on disk and keyed by path, size and modification time,
/// so a rescan only redoes work when the underlying video changes.
/// </summary>
public sealed class PosterGenerator
{
    private const int PosterWidth = 600;
    private const int PosterHeight = 900;
    private const int ArtWidth = 1920;
    private const int ArtHeight = 1080;

    /// <summary>
    /// Below this a 600x900 JPEG is essentially a flat fill. Real posters land far higher,
    /// so a smaller file means we sampled a black leader or a genuinely dark scene.
    /// </summary>
    private const long FlatFrameBytes = 12_000;

    private readonly MediaArtOptions _options;
    private readonly SemaphoreSlim _throttle;
    private readonly ILogger<PosterGenerator> _log;
    private bool _reportedFfmpeg;
    private bool _reportedMissingFfmpeg;

    public PosterGenerator(MediaArtOptions options, ILogger<PosterGenerator> log)
    {
        _options = options;
        _throttle = new SemaphoreSlim(Math.Max(1, options.MaxConcurrency));
        _log = log;
    }

    /// <summary>Resolves ffmpeg, reporting once per process when it is unavailable.</summary>
    public string ResolveFfmpeg()
    {
        var resolved = FfmpegLocator.Executable;
        if (!string.IsNullOrEmpty(resolved))
        {
            if (!_reportedFfmpeg)
            {
                _log.LogInformation("Artwork generation using ffmpeg at {Path}", resolved);
                _reportedFfmpeg = true;
            }

            return resolved;
        }

        if (!_reportedMissingFfmpeg)
        {
            _reportedMissingFfmpeg = true;
            _log.LogWarning(
                "ffmpeg not found; media without a sidecar image will show no artwork. " +
                "Set Media:Art:FfmpegPath in appsettings.json or the PLEX_FFMPEG environment variable.");
        }

        return "";
    }

    /// <summary>
    /// Ensures a cached poster exists for the given video and returns its path, or empty
    /// when artwork is disabled, ffmpeg is unavailable, or extraction failed.
    /// </summary>
    public async Task<string?> EnsurePosterAsync(string videoPath, int? durationMs, CancellationToken ct)
    {
        if (!_options.Enabled || durationMs == null) return null;

        var cacheDir = string.IsNullOrEmpty(_options.CacheDirectory) ? MediaArtOptions.DefaultCacheDirectory : _options.CacheDirectory;
        var outPath = Path.Combine(cacheDir, CacheKey(videoPath) + "-poster.jpg");

        if (HasContent(outPath)) return outPath;

        var ffmpeg = ResolveFfmpeg();
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        await _throttle.WaitAsync(ct);
        try
        {
            if (HasContent(outPath)) return outPath;

            Directory.CreateDirectory(cacheDir);
            var candidate = Path.Combine(cacheDir, CacheKey(videoPath) + "-candidate.jpg");

            foreach (var seek in SeekOffsets(durationMs))
            {
                var ok = await RunAsync(ffmpeg, BuildPosterArgs(videoPath, candidate, seek), ct);
                if (!ok || !HasContent(candidate)) continue;

                // A flat frame (black leader, dark night scene) compresses to a tiny JPEG even
                // at high quality, while a normal one lands well above this. Keep sampling other
                // offsets when the frame looks empty, otherwise a still frame reads as "no poster".
                var size = new FileInfo(candidate).Length;
                if (size < FlatFrameBytes)
                {
                    _log.LogDebug("Frame at {Seek}s for {Video} looked flat ({Size} bytes), sampling later offset",
                        seek, Path.GetFileName(videoPath), size);
                    continue;
                }

                File.Move(candidate, outPath, overwrite: true);
                _log.LogInformation("Generated poster for {Video}", Path.GetFileName(videoPath));
                return outPath;
            }

            TryDelete(candidate);
            return "";
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string[] BuildPosterArgs(string source, string outPath, double seek) =>
    [
        "-ss", seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        "-i", source,
        "-frames:v", "1",
        "-vf", $"scale={PosterWidth}:{PosterHeight}:force_original_aspect_ratio=increase,crop={PosterWidth}:{PosterHeight}",
        "-q:v", "3",
        "-y", outPath
    ];

    /// <summary>
    /// Tries several points in the runtime and lets the caller keep the first non-flat one.
    /// Starts early enough to skip opening logos but late enough to pass title cards.
    /// </summary>
    private IEnumerable<double> SeekOffsets(int? durationMs)
    {
        if (durationMs == null) yield break;
        var primary = PrimarySeek(durationMs);
        yield return primary;

        if (durationMs <= 0)
        {
            foreach (var fallback in new double[] { 90, 180, 300 })
                if (fallback > primary) yield return fallback;
            yield break;
        }

        var seconds = durationMs.Value / 1000.0;
        var ceiling = Math.Max(10, seconds - 30);

        foreach (var fraction in new[] { 0.30, 0.55, 0.80 })
        {
            var offset = Math.Clamp(seconds * fraction, primary + 5, ceiling);
            if (offset > primary) yield return offset;
        }
    }

    /// <summary>
    /// Produces the wide 16:9 backdrop used behind an item's detail screen. Read from the video
    /// rather than the poster: the poster is a 2:3 crop, so widening it would slice off the
    /// subject's head. Blur it to match Plex's soft backdrop look.
    /// </summary>
    public async Task<string?> EnsureArtAsync(string videoPath, int? durationMs, string? posterPath, CancellationToken ct)
    {
        if (!_options.Enabled || durationMs == null) return null;

        var cacheDir = string.IsNullOrEmpty(_options.CacheDirectory) ? MediaArtOptions.DefaultCacheDirectory : _options.CacheDirectory;
        var outPath = Path.Combine(cacheDir, CacheKey(videoPath) + "-art.jpg");

        if (HasContent(outPath)) return outPath;

        var ffmpeg = ResolveFfmpeg();
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        await _throttle.WaitAsync(ct);
        try
        {
            if (HasContent(outPath)) return outPath;

            Directory.CreateDirectory(cacheDir);

            var ok = await RunAsync(ffmpeg, BuildArtArgs(videoPath, outPath, PrimarySeek(durationMs)), ct);

            if (ok && HasContent(outPath)) return outPath;

            // Fall back to stretching the poster. This works because a still has no timeline to seek.
            if (!string.IsNullOrEmpty(posterPath) && File.Exists(posterPath))
            {
                ok = await RunAsync(ffmpeg, BuildArtArgs(posterPath, outPath, -1), ct);
                if (ok && HasContent(outPath)) return outPath;
            }

            TryDelete(outPath);
            return "";
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string[] BuildArtArgs(string source, string outPath, double seek)
    {
        var args = new List<string>();

        // A still image has no timeline, so seeking past its single frame yields nothing at all.
        if (seek >= 0)
        {
            args.Add("-ss");
            args.Add(seek.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        args.AddRange(
        [
            "-i", source,
            "-frames:v", "1",
            "-vf", $"scale={ArtWidth}:{ArtHeight}:force_original_aspect_ratio=increase," +
                    $"crop={ArtWidth}:{ArtHeight},gblur=sigma=18",
            "-q:v", "4",
            "-y", outPath
        ]);

        return [.. args];
    }

    /// <summary>ffmpeg can exit 0 after writing nothing, so always confirm bytes landed on disk.</summary>
    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>
    /// The offset used for the first attempt, and the one the backdrop reuses so that the
    /// blurred art lines up with the poster's still.
    /// </summary>
    private double PrimarySeek(int? durationMs)
    {
        if (durationMs == null || durationMs <= 0) return 30;

        var fraction = Math.Clamp(_options.FrameSeekFraction, 0.01, 0.9);
        var seconds = durationMs.Value / 1000.0 * fraction;

        // Stay well clear of the very end, where trailers and credit cards live.
        return Math.Clamp(seconds, 10, Math.Max(10, durationMs.Value / 1000.0 - 30));
    }
    private static string CacheKey(string videoPath)
    {
        var info = new FileInfo(videoPath);
        var material = $"{videoPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24].ToLowerInvariant();
    }

    private async Task<bool> RunAsync(string ffmpeg, IReadOnlyList<string> args, CancellationToken ct)
    {
        var input = InputPathOf(args);
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
            timeout.CancelAfter(TimeSpan.FromMinutes(2));

            await process.WaitForExitAsync(timeout.Token);
            await drained;

            if (process.ExitCode != 0)
                _log.LogDebug("ffmpeg exited {Code} for {Video}", process.ExitCode, input);

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Timed out extracting artwork from {Video}", input);
            TryKill(process);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Artwork extraction failed for {Video}", input);
            TryKill(process);
            return false;
        }
    }

    private static string InputPathOf(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "-i") return args[i + 1];
        return "unknown";
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
