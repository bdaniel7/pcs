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
}
