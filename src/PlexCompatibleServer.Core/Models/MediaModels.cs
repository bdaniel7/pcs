namespace PlexCompatibleServer.Core.Models;

public enum LibraryType
{
    Movie = 1,
    Show = 2
}

public sealed class MediaLibrary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public LibraryType Type { get; set; }

    /// <summary>Stable per-library identifier, equivalent to Plex's librarySectionUUID.</summary>
    public string Uuid { get; set; } = Guid.NewGuid().ToString();

    public ICollection<MediaItem> Items { get; set; } = new List<MediaItem>();
}

public sealed class MediaItem
{
    public int Id { get; set; }
    public int LibraryId { get; set; }
    public MediaLibrary Library { get; set; } = null!;

    public string Title { get; set; } = "";
    public string? SortTitle { get; set; }
    public int? Year { get; set; }
    public string? Summary { get; set; }

    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public string MimeType { get; set; } = "video/x-matroska";

    public int? DurationMs { get; set; }
    public string? PosterPath { get; set; }
    public string? ArtPath { get; set; }

    // Official artwork downloaded from plex.tv (poster/backdrop for movies, season/show posters
    // for episodes). Null until the backfill sync has fetched them; serving prefers these over
    // the frame-extracted PosterPath/ArtPath and falls back when the file has vanished.
    public string? OfficialPosterPath { get; set; }
    public string? OfficialArtPath { get; set; }
    public string? OfficialParentPosterPath { get; set; }
    public string? OfficialGrandparentPosterPath { get; set; }

    // Stream details from ffprobe. Kept on the item because the player needs them to decide
    // whether it can direct play a file without probing it again on every request.
    public int Bitrate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? VideoCodec { get; set; }
    public string? VideoProfile { get; set; }
    public string? AudioCodec { get; set; }
    public int AudioChannels { get; set; }
    public double FrameRate { get; set; }
    public string? Container { get; set; }

    /// <summary>Elementary streams serialised as JSON: subtitle tracks especially, since Plex exposes each one.</summary>
    public string? StreamsJson { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Playback progress, reported by the client through /:/timeline and persisted so a stopped
    // session can resume where it left off after a server or TV restart.
    // Null = never progressed. In milliseconds, like Plex's viewOffset.
    public int? ViewOffset { get; set; }
    // Last time the viewer touched the item (every accepted timeline report or scrobble).
    // Drives Continue Watching ordering; emitted as lastViewedAt.
    public DateTimeOffset? LastViewedAt { get; set; }
    // Number of completed views (scrobbles / crossing the 90% watched threshold).
    public int ViewCount { get; set; }

    // Plex-compatible metadata fields
    public int? LibrarySectionId { get; set; }
    public int? ParentId { get; set; }
    public int? MetadataType { get; set; }
    public string? Guid { get; set; }
    public int? MediaItemCount { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Studio { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }
    public string? Tagline { get; set; }
    public string? Trivia { get; set; }
    public string? Quotes { get; set; }
    public string? ContentRating { get; set; }
    public int? ContentRatingAge { get; set; }
    public int? Index { get; set; }
    public int? AbsoluteIndex { get; set; }
    public string? UserThumbUrl { get; set; }
    public string? UserArtUrl { get; set; }
    public string? UserBannerUrl { get; set; }
    public string? UserMusicUrl { get; set; }
    public string? UserFields { get; set; }
    public string? TagsGenre { get; set; }
    public string? TagsCollection { get; set; }
    public string? TagsDirector { get; set; }
    public string? TagsWriter { get; set; }
    public string? TagsStar { get; set; }
    public DateTimeOffset? OriginallyAvailableAt { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RefreshedAt { get; set; }
    public string? TagsCountry { get; set; }
    public string? ExtraData { get; set; }
    public string? Hash { get; set; }
    public double? AudienceRating { get; set; }
    public long? ChangedAt { get; set; }
    public long? ResourcesChangedAt { get; set; }
    public int? Remote { get; set; }
    public string? EditionTitle { get; set; }
    public string? Slug { get; set; }
    public string? UserClearLogoUrl { get; set; }
    public int? IsAdult { get; set; }
    public int? MetadataAgentProviderGroupId { get; set; }
    public string? UserSquareArtUrl { get; set; }

}