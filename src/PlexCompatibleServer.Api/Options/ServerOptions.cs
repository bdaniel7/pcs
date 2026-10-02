namespace PlexCompatibleServer.Api.Options;

public sealed class ServerOptions
{
    public string Name { get; set; } = "Plex Compatible Server";
    public string MachineIdentifier { get; set; } = "b1c8f0a34d7e42f9a65b0c3d8e17f4a2b";
    public string Version { get; set; } = "1.43.4.10903-e5521bd8c";
    public string ApiVersion { get; set; } = "1.2.3";

    /// <summary>Plex derives stable per-library UUIDs; derive them from the machine identifier.</summary>
    public string LibraryUuid(int libraryId, string libraryName, string libraryType)
    {
        var seed = $"{MachineIdentifier}:{libraryId}:{libraryType}:{libraryName}";
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return $"{Convert.ToHexString(hash, 0, 4).ToLowerInvariant()}-{Convert.ToHexString(hash, 4, 2).ToLowerInvariant()}-{Convert.ToHexString(hash, 6, 2).ToLowerInvariant()}-{Convert.ToHexString(hash, 8, 2).ToLowerInvariant()}-{Convert.ToHexString(hash, 10, 6).ToLowerInvariant()}";
    }
}

public sealed class MediaOptions
{
    public List<MediaRootOptions> Roots { get; set; } = new();
}

public sealed class MediaRootOptions
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "movie";
}
