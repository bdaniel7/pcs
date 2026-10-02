using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Infrastructure.Scanning;

public sealed class FileSystemMediaScanner : IMediaScanner
{
    private readonly IMediaRepository _repository;

    public FileSystemMediaScanner(IMediaRepository repository) => _repository = repository;

    public Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct) =>
        _repository.SynchronizeAsync(libraries, ct);
}
