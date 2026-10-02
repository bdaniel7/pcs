namespace PlexCompatibleServer.Core.Models;

public sealed class PlexServerInfo
{
    public string MachineIdentifier { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Platform { get; init; } = "C#";
    public string PlatformVersion { get; init; } = "";
    public string Title { get; init; } = "";
    public string Vendor { get; init; } = "PlexCompatibleServer";
    public string Device { get; init; } = "Server";
    public string DeviceName { get; init; } = "";
    public string Protocol { get; init; } = "http";
    public string Protocols { get; init; } = "http";
}

public sealed class PlexMediaContainer
{
    public int Size { get; init; }
    public string? Title1 { get; init; }
    public string? Title2 { get; init; }
    public string? LibrarySectionID { get; init; }
    public string? LibrarySectionTitle { get; init; }
    public string? LibrarySectionUUID { get; init; }
    public IReadOnlyList<PlexDirectory> Directories { get; init; } = Array.Empty<PlexDirectory>();
    public IReadOnlyList<PlexVideo> Videos { get; init; } = Array.Empty<PlexVideo>();
}

public sealed class PlexDirectory
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Type { get; init; } = "";
    public string Agent { get; init; } = "com.plexapp.agents.local";
    public string Scanner { get; init; } = "Plex";
    public string Thumb { get; init; } = "";
}

public sealed class PlexVideo
{
    public int RatingKey { get; init; }
    public string Key { get; init; } = "";
    public string Type { get; init; } = "movie";
    public string Title { get; init; } = "";
    public string? SortTitle { get; init; }
    public int? Year { get; init; }
    public string? Summary { get; init; }
    public string? Thumb { get; init; }
    public string? Art { get; init; }
    public int Duration { get; init; }
    public int ViewOffset { get; init; }
    public long AddedAt { get; init; }
    public long UpdatedAt { get; init; }
    public IReadOnlyList<PlexMedia> Media { get; init; } = Array.Empty<PlexMedia>();
}

public sealed class PlexMedia
{
    public int Id { get; init; }
    public int Duration { get; init; }
    public long Bitrate { get; init; }
    public string Container { get; init; } = "";
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    public IReadOnlyList<PlexPart> Parts { get; init; } = Array.Empty<PlexPart>();
}

public sealed class PlexPart
{
    public int Id { get; init; }
    public string Key { get; init; } = "";
    public string Container { get; init; } = "";
    public long Size { get; init; }
}
