using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;
using System.Text.RegularExpressions;

namespace PlexCompatibleServer.Infrastructure.Data;

public sealed class MediaRepository : IMediaRepository
{
    private readonly IDbContextFactory<MediaDbContext> _factory;
    private readonly PosterGenerator? _posterGenerator;
    private readonly ILogger<MediaRepository> _log;

    public MediaRepository(
        IDbContextFactory<MediaDbContext> factory,
        PosterGenerator? posterGenerator = null,
        ILogger<MediaRepository>? log = null)
    {
        _factory = factory;
        _posterGenerator = posterGenerator;
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaRepository>.Instance;
    }

    public async Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Libraries.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
    }

    public async Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Libraries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Items.AsNoTracking().Include(x => x.Library)
            .Where(x => x.LibraryId == libraryId)
            .OrderBy(x => x.SortTitle).ToListAsync(ct);
    }

    public async Task<MediaItem?> GetItemAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Items.AsNoTracking().Include(x => x.Library)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);

        foreach (var config in libraries)
        {
            var library = await db.Libraries.FirstOrDefaultAsync(
                x => x.Name == config.Name && x.RootPath == config.RootPath, ct);

            if (library is null)
            {
                library = new MediaLibrary
                {
                    Name = config.Name,
                    RootPath = config.RootPath,
                    Type = config.Type
                };
                db.Libraries.Add(library);
                await db.SaveChangesAsync(ct);
            }

            var existing = await db.Items.Where(x => x.LibraryId == library.Id)
                .ToDictionaryAsync(x => x.FilePath, ct);

            if (!Directory.Exists(library.RootPath))
                continue;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm"
            };

            foreach (var file in Directory.EnumerateFiles(library.RootPath, "*.*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(file)))
                    continue;

                seen.Add(file);

                var info = new FileInfo(file);
                if (!info.Exists) continue;

                MediaItem item;
                if (existing.TryGetValue(file, out var found))
                {
                    item = found;
                }
                else
                {
                    item = new MediaItem { LibraryId = library.Id, FilePath = file };
                    db.Items.Add(item);
                }

                var previousSize = item.FileSize;

                item.Title = Path.GetFileNameWithoutExtension(file);
                item.SortTitle = item.Title;
                item.Year = FilenameYear.Parse(item.Title);
                item.FileSize = info.Length;
                item.MimeType = MimeTypes.ForExtension(Path.GetExtension(file));
                item.UpdatedAt = DateTimeOffset.UtcNow;

                var wasNew = !existing.ContainsKey(file);
                if (wasNew || item.DurationMs == null || previousSize != info.Length)
                    item.DurationMs = MediaDurationProbe.GetDurationMs(file);

                // Re-probe when the stream list is missing too, so a transient ffprobe failure
                // (or an item scanned before probing existed) heals on a later scan.
                if (wasNew || previousSize != info.Length || string.IsNullOrEmpty(item.StreamsJson))
                    await RefreshStreamsAsync(item, ct);

                await RefreshArtworkAsync(item, info, wasNew || string.IsNullOrEmpty(item.PosterPath), ct);
            }

            // Rows whose file vanished (deleted or renamed) must go too - a renamed movie would
            // otherwise leave a ghost item serving under its old path and identity.
            foreach (var kv in existing)
            {
                if (!seen.Contains(kv.Key))
                    db.Items.Remove(kv.Value);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SaveProgressAsync(int id, long timeMs, long durationMs, DateTimeOffset viewedAt, CancellationToken ct)
    {
        // A buffering heartbeat at 0 arrives right when playback restarts for a resume; storing
        // it would erase exactly the point we are about to resume from.
        if (timeMs <= 0) return;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;

        var effectiveDuration = durationMs > 0 ? durationMs : item.DurationMs ?? 0;
        var clamped = effectiveDuration > 0 ? Math.Min(timeMs, effectiveDuration) : timeMs;
        var previousOffset = item.ViewOffset ?? 0;
        var lastViewed = item.LastViewedAt;

        item.ViewOffset = (int)Math.Min(clamped, int.MaxValue);
        item.LastViewedAt = viewedAt;
        // Playing the item again reverses a "Remove from continue watching" dismissal: a
        // buffering heartbeat at 0 never reaches this line, only a real position report does.
        item.ContinueWatchingDismissedAt = null;

        // Crossing 90% counts a completed view - the LG client never sends /:/scrobble in
        // captured sessions, so this is the only thing that ever marks an item watched. The
        // same-session guard keeps the heartbeats that follow the crossing from counting the
        // watch again; a genuinely new completion arrives more than 10 minutes after the last
        // report or from an offset below the threshold.
        if (effectiveDuration > 0 && clamped >= effectiveDuration * 0.9)
        {
            var sameSession = previousOffset >= effectiveDuration * 0.9
                && lastViewed is { } last
                && viewedAt - last < TimeSpan.FromMinutes(10);
            if (!sameSession) item.ViewCount++;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task MarkWatchedAsync(int id, DateTimeOffset viewedAt, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;

        var duration = item.DurationMs ?? 0;
        var previousOffset = item.ViewOffset ?? 0;
        var lastViewed = item.LastViewedAt;

        // The client scrobbling right after the 90% timeline rule already counted the watch
        // must not count it twice (see SaveProgressAsync).
        var sameSession = duration > 0 && previousOffset >= duration * 0.9
            && lastViewed is { } last
            && viewedAt - last < TimeSpan.FromMinutes(10);
        if (!sameSession) item.ViewCount++;

        item.LastViewedAt = viewedAt;
        if (duration > 0) item.ViewOffset = duration;

        await db.SaveChangesAsync(ct);
    }

    public async Task ClearProgressAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;

        item.ViewOffset = null;
        item.LastViewedAt = null;
        item.ViewCount = 0;
        item.ContinueWatchingDismissedAt = null;

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MediaItem>> GetInProgressAsync(int? libraryId, int limit, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.Items.AsNoTracking().Include(x => x.Library)
            .Where(x => x.ViewOffset != null && x.ViewOffset > 0
                && x.DurationMs != null && x.DurationMs > 0
                && x.ViewOffset < x.DurationMs * 0.9
                && x.ContinueWatchingDismissedAt == null);

        if (libraryId is { } wanted) query = query.Where(x => x.LibraryId == wanted);

        // Ordering by DateTimeOffset has no SQLite translation, so the sort happens in memory.
        // The match set is a handful of rows out of a personal library, so this costs nothing.
        var matches = await query.ToListAsync(ct);
        return matches
            .OrderByDescending(x => x.LastViewedAt)
            .Take(limit)
            .ToList();
    }

    public async Task DismissFromContinueWatchingAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;

        item.ContinueWatchingDismissedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveOfficialArtworkAsync(int id, string? posterPath, string? artPath, string? parentPosterPath,
                                               string? grandparentPosterPath, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var item = await db.Items.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (item is null) return;

        if (!string.IsNullOrEmpty(posterPath)) item.OfficialPosterPath = posterPath;
        if (!string.IsNullOrEmpty(artPath)) item.OfficialArtPath = artPath;
        if (!string.IsNullOrEmpty(parentPosterPath)) item.OfficialParentPosterPath = parentPosterPath;
        if (!string.IsNullOrEmpty(grandparentPosterPath)) item.OfficialGrandparentPosterPath = grandparentPosterPath;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads codec/resolution detail with ffprobe. When ffprobe is unavailable the managed duration
    /// probe still leaves DurationMs populated, so the library lists correctly even without it.
    /// </summary>
    private async Task RefreshStreamsAsync(MediaItem item, CancellationToken ct)
    {
        var info = await MediaStreamProbe.ProbeAsync(item.FilePath, ct);
        if (info is null || info.Width == 0)
        {
            _log.LogWarning("ffprobe returned no data for {Path}: {Failure}",
                item.FilePath, MediaStreamProbe.ProbeFailure);
            return;
        }

        item.Width = info.Width;
        item.Height = info.Height;
        item.Bitrate = info.Bitrate is > 0 ? info.Bitrate / 1000 : 0;
        item.VideoCodec = info.VideoCodec;
        item.VideoProfile = info.VideoProfile;
        item.AudioCodec = info.AudioCodec;
        item.AudioChannels = info.AudioChannels;
        item.FrameRate = info.FrameRate;

        if (info.DurationMs is > 0) item.DurationMs = info.DurationMs;

        // Prefer the probed container over the extension guess.
        item.Container = string.IsNullOrEmpty(info.Container)
            ? Path.GetExtension(item.FilePath).TrimStart('.').ToLowerInvariant()
            : info.Container;

        item.StreamsJson = System.Text.Json.JsonSerializer.Serialize(info.Streams);
    }

    /// <summary>
    /// Keeps <see cref="MediaItem.PosterPath"/> and <see cref="MediaItem.ArtPath"/> pointed at
    /// artwork that exists. Artwork generation shells out to ffmpeg, so it only runs when the
    /// paths actually need rebuilding rather than on every scan.
    /// </summary>
    private async Task RefreshArtworkAsync(MediaItem item, FileInfo info, bool stale, CancellationToken ct)
    {
        if (_posterGenerator is null) return;

        var posterMissing = string.IsNullOrEmpty(item.PosterPath) || !File.Exists(item.PosterPath);
        var artMissing = string.IsNullOrEmpty(item.ArtPath) || !File.Exists(item.ArtPath);

        // Both must be present before we skip: checking only the poster meant a library whose
        // posters were already cached never got its backdrops generated.
        if (!stale && !posterMissing && !artMissing) return;

        if (posterMissing)
        {
            item.PosterPath = await _posterGenerator.EnsurePosterAsync(item.FilePath, item.DurationMs, ct);
        }

        if (artMissing)
        {
            item.ArtPath = await _posterGenerator.EnsureArtAsync(item.FilePath, item.DurationMs, item.PosterPath, ct);
        }
    }
}

/// <summary>
/// Recovers the release year from a media filename, e.g. "Movie (2025) [1080p]" or
/// "Movie.2025.1080p". Prefers a parenthesised year, since bare 4-digit runs also
/// appear in resolutions ("2160") and release groups ("YTS.BZ").
/// </summary>
public static class FilenameYear
{
    private static readonly Regex Parenthetical = new(@"\((?<year>(?:19|20)\d{2})\)", RegexOptions.Compiled);
    private static readonly Regex Bracketed = new(@"\[(?<year>(?:19|20)\d{2})\]", RegexOptions.Compiled);
    private static readonly Regex Loose = new(@"(?:^|[^\d])(?<year>(?:19|20)\d{2})(?:[^\d]|$)", RegexOptions.Compiled);

    /// <summary>Returns the recovered year, or null when the name carries no plausible one.</summary>
    public static int? Parse(string filename)
    {
        foreach (var pattern in new[] { Parenthetical, Bracketed, Loose })
        {
            var match = pattern.Match(filename);
            if (match.Success && int.TryParse(match.Groups["year"].Value, out var year))
                return year;
        }

        return null;
    }
}

internal static class MimeTypes
{    public static string ForExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".ts" or ".m2ts" => "video/mp2t",
        _ => "video/x-matroska"
    };
}
