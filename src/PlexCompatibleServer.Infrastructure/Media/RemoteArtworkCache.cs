using System.Security.Cryptography;
using System.Text;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>
/// Downloads remote artwork (the poster/backdrop URLs plex.tv carries) into the art cache
/// directory. Files are keyed by URL hash, so a season poster shared by every episode of a show
/// is fetched once, and anything already on disk is never re-downloaded. Only payloads that
/// sniff as a real image are kept - an error page or a login redirect must never reach the
/// photo route, which serves whatever path it is handed. HTTP goes through IHttpClientFactory
/// (registered in Program.cs) so the handler pool manages socket/DNS lifetime instead of this
/// singleton pinning one HttpClient for the life of the process.
/// </summary>
public sealed class RemoteArtworkCache
{
    /// <summary>Named client registered with <c>AddHttpClient()</c> in Program.cs.</summary>
    public const string HttpClientName = "remote-artwork";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(20);

    // A genuine poster is tens of kilobytes at minimum; smaller bodies are error pixels.
    private const int MinImageBytes = 512;

    private readonly MediaArtOptions _options;
    private readonly Func<HttpClient> _newClient;

    public RemoteArtworkCache(MediaArtOptions options, IHttpClientFactory clients)
        : this(options, () => clients.CreateClient(HttpClientName))
    {
    }

    // Handler injection keeps the tests off the network.
    internal RemoteArtworkCache(MediaArtOptions options, HttpMessageHandler handler)
        : this(options, () => new HttpClient(handler))
    {
    }

    private RemoteArtworkCache(MediaArtOptions options, Func<HttpClient> newClient)
    {
        _options = options;
        _newClient = newClient;
    }

    public string CacheDirectory =>
        string.IsNullOrEmpty(_options.CacheDirectory)
            ? MediaArtOptions.DefaultCacheDirectory
            : _options.CacheDirectory;

    /// <summary>
    /// Ensures the image behind <paramref name="url"/> exists in the cache and returns its full
    /// path, or null when the download failed, was cancelled by a non-cancellation error, or the
    /// body was not one of the formats the image routes can serve (jpg/png/webp).
    /// </summary>
    public async Task<string?> EnsureAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var stem = "official-" + UrlHash(url);
        var dir = CacheDirectory;

        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
        {
            var existing = Path.Combine(dir, stem + ext);

            if (File.Exists(existing) && new FileInfo(existing).Length > 0) return existing;
        }

        try
        {
            // A fresh client per download: factory clients are cheap, and reusing one forever
            // would pin the same handler for the whole process - exactly what the factory exists
            // to avoid. Each instance is brand new, so setting Timeout here is safe.
            var client = _newClient();
            client.Timeout = DownloadTimeout;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            // Some of the CDNs behind plex.tv answer a default client with 403.
            req.Headers.TryAddWithoutValidation("User-Agent", "PlexCompatibleServer/1.0");
            req.Headers.TryAddWithoutValidation("Accept", "image/*");

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            var ext = SniffExtension(bytes);
            if (ext is null) return null;

            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, stem + ext);
            var tmp = Path.Combine(dir, $".{stem}.{Guid.NewGuid():N}.tmp");

            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, target, overwrite: true);

            return target;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Transient network or disk failure: the caller falls back to frame artwork and
            // the next backfill tries again.
            return null;
        }
    }

    internal static string UrlHash(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));

        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>Extension matching the actual image format, or null for anything unservable.</summary>
    internal static string? SniffExtension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < MinImageBytes) return null;

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 &&
            bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";

        if (bytes.Length >= 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' &&
            bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
            bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' &&
            bytes[11] == (byte)'P')
            return ".webp";

        return null;
    }
}
