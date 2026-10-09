using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Infrastructure.Scanning;

public sealed class FileSystemMediaScanner : IMediaScanner
{
    private readonly IMediaRepository repository;

    public FileSystemMediaScanner(IMediaRepository repository) => this.repository = repository;

    public Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct) =>
        repository.SynchronizeAsync(libraries, ct);
}
