using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Core.Interfaces;

public interface IMediaScanner
{
    Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct);
}
