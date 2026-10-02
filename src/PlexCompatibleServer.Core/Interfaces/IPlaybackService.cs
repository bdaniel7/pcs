using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Core.Interfaces;

public interface IPlaybackService
{
    Task<MediaItem?> GetMediaAsync(int id, CancellationToken ct);
}
