using System.Text.Json.Serialization;

namespace PlexCompatibleServer.Core.Models;

/// <summary>
/// One plex.tv metadata record, keyed by the media file's normalized stem in the sidecar and the
/// lookup cache. Field names line up with the scraped sidecar JSON, so the serialized shape is
/// unchanged by the move out of the Api layer.
/// </summary>
public sealed class SidecarItem
{
    public string? Title { get; set; }

    public string? FileStem { get; set; }

    public string? TitleSort { get; set; }

    public string? Studio { get; set; }

    public string? Summary { get; set; }

    public string? Tagline { get; set; }

    public string? Year { get; set; }

    public string? OriginallyAvailableAt { get; set; }

    public string? ContentRating { get; set; }

    public int? ContentRatingAge { get; set; }

    public double? AudienceRating { get; set; }

    public string? AudienceRatingImage { get; set; }

    public string? Guid { get; set; }

    public string? RatingKey { get; set; }

    public bool DetailChecked { get; set; }

    public List<string>? Guids { get; set; }

    public List<string>? Genres { get; set; }

    public List<string>? Countries { get; set; }

    public List<TagRef>? Directors { get; set; }

    public List<TagRef>? Writers { get; set; }

    public List<TagRef>? Roles { get; set; }

    public List<TagRef>? Producers { get; set; }

    public List<RatingRef>? Ratings { get; set; }

    public UltraBlurRef? UltraBlur { get; set; }

    public CommonSenseRef? CommonSense { get; set; }

    public List<ReviewRef>? Reviews { get; set; }

    // The episode hierarchy: season/episode numbers plus the show/season breadcrumb the info
    // screen renders. Movie records leave these null.
    public string? Index { get; set; }

    public string? ParentIndex { get; set; }

    public string? ParentTitle { get; set; }

    public string? ParentKey { get; set; }

    public string? ParentRatingKey { get; set; }

    public string? ParentGuid { get; set; }

    public string? GrandparentTitle { get; set; }

    public string? GrandparentKey { get; set; }

    public string? GrandparentRatingKey { get; set; }

    public string? GrandparentGuid { get; set; }

    // Remote artwork URLs from plex.tv: thumb/art are the movie's own poster/backdrop, while
    // parentThumbUrl/grandparentThumbUrl are the season and show posters carried by episode
    // payloads. The backfill sync downloads them into the art cache; nothing serves URLs directly.
    public string? ThumbUrl { get; set; }

    public string? ArtUrl { get; set; }

    public string? ParentThumbUrl { get; set; }

    public string? GrandparentThumbUrl { get; set; }
}

/// <summary>A named credit (director, writer, actor, producer) with its plex.tv tag key.</summary>
public sealed class TagRef
{
    public string? Tag { get; set; }

    public string? TagKey { get; set; }

    public string? Thumb { get; set; }

    public string? Role { get; set; }
}

/// <summary>A single audience/critic rating with its source image.</summary>
public sealed class RatingRef
{
    public string? Image { get; set; }

    public string? Type { get; set; }

    public double? Value { get; set; }
}

/// <summary>The four-corner colors the detail screen paints behind the poster.</summary>
public sealed class UltraBlurRef
{
    [JsonPropertyName("topLeft")]
    public string? TopLeft { get; set; }

    [JsonPropertyName("topRight")]
    public string? TopRight { get; set; }

    [JsonPropertyName("bottomLeft")]
    public string? BottomLeft { get; set; }

    [JsonPropertyName("bottomRight")]
    public string? BottomRight { get; set; }
}

/// <summary>Common Sense Media one-liner plus its age ratings.</summary>
public sealed class CommonSenseRef
{
    public string? OneLiner { get; set; }

    public List<AgeRatingRef>? AgeRatings { get; set; }
}

public sealed class AgeRatingRef
{
    public string? Type { get; set; }

    public int Rating { get; set; }

    public int Age { get; set; }
}

/// <summary>A single critic review carried by the sidecar.</summary>
public sealed class ReviewRef
{
    public string? Source { get; set; }

    public string? Quote { get; set; }

    public string? Link { get; set; }

    public string? Image { get; set; }

    public string? TagKey { get; set; }

    public string? Thumb { get; set; }
}

/// <summary>Counters from one plex.tv backfill pass.</summary>
public sealed class BackfillResult
{
    public int Scanned { get; set; }

    public int Present { get; set; }

    public int Created { get; set; }

    public int Enriched { get; set; }

    public List<string> CreatedTitles { get; set; } = new();

    public List<string> Failed { get; set; } = new();
}
