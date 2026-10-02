using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Infrastructure.Data;

public sealed class PlaybackService : IPlaybackService
{
    private readonly IMediaRepository _repository;

    public PlaybackService(IMediaRepository repository) => _repository = repository;

    public Task<MediaItem?> GetMediaAsync(int id, CancellationToken ct) =>
        _repository.GetItemAsync(id, ct);
}