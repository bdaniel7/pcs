using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Lifts a text subtitle track out of a container into a standalone SRT on demand. A
/// direct-playing client can only overlay an external subtitle file - it cannot read a track out
/// of an MKV itself - so an embedded track is only usable if the server extracts the text and
/// serves it like a sidecar. Results are cached in the temp folder, keyed by the video's path,
/// size and modification time plus the track position, so a replaced file invalidates them.
/// </summary>
public static class EmbeddedSubtitles
{
    /// <summary>
    /// Codecs that carry text ffmpeg can convert to SRT. Bitmap subtitles (PGS, DVB, VobSub)
    /// have no text to extract and are left inside the container.
    /// </summary>
    public static bool IsTextCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        "subrip" or "srt" or "mov_text" or "text" or "webvtt" or "ass" or "ssa"
            or "eia_608" or "eia_708" => true,
        _ => false
    };

    /// <summary>Where extracted subtitle files are cached.</summary>
    public static string CacheDirectory =>
        Path.Combine(Path.GetTempPath(), "PlexCompatibleServer", "subs");

    /// <summary>
    /// Extracts the subtitle at <paramref name="trackPosition"/> (0-based among the container's
    /// subtitle streams, which is the order ffprobe reported them in) and returns the SRT path,
    /// or null when the track is bitmap, ffmpeg is unavailable, or extraction fails.
    /// </summary>
    public static async Task<string?> ExtractAsync(
        string videoPath, int trackPosition, string? codec, CancellationToken ct = default)
    {
        if (!IsTextCodec(codec) || trackPosition < 0) return null;

        var ffmpeg = FfmpegLocator.Executable;
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        FileInfo info;
        try
        {
            info = new FileInfo(videoPath);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var key = cacheKey(videoPath, info.Length, info.LastWriteTimeUtc.Ticks, trackPosition);
        Directory.CreateDirectory(CacheDirectory);
        var finalPath = Path.Combine(CacheDirectory, key + ".srt");
        if (File.Exists(finalPath))
            return new FileInfo(finalPath).Length > 0 ? finalPath : null;

        var tempPath = Path.Combine(CacheDirectory, key + "." + Environment.ProcessId + ".tmp");

        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[]
            {
                "-v", "error", "-y",
                "-i", videoPath,
                "-map", $"0:s:{trackPosition}",
                "-c:s", "srt",
                "-f", "srt",
                tempPath
            })
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stderr = process.StandardError.ReadToEndAsync(ct);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                tryKill(process);
                return null;
            }

            try { await Task.WhenAll(stdout, stderr); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }

            if (process.ExitCode != 0 || !File.Exists(tempPath)) return null;
            if (new FileInfo(tempPath).Length == 0) return null;

            File.Move(tempPath, finalPath, overwrite: true);
            return finalPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string cacheKey(string path, long length, long mtimeTicks, int trackPosition)
    {
        var material = Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{path}|{length}|{mtimeTicks}|{trackPosition}"));
        return Convert.ToHexString(SHA256.HashData(material))[..24];
    }

    private static void tryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
