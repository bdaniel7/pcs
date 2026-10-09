using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Infrastructure.Data;

public sealed class PlaybackService : IPlaybackService
{
    private readonly IMediaRepository repository;

    public PlaybackService(IMediaRepository repository) => this.repository = repository;

    public Task<MediaItem?> GetMediaAsync(int id, CancellationToken ct) =>
        repository.GetItemAsync(id, ct);
}
