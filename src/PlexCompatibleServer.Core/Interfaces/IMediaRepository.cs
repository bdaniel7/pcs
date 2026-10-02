using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Core.Interfaces;

public interface IMediaRepository
{
    Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct);
    Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct);
    Task<MediaItem?> GetItemAsync(int id, CancellationToken ct);
    Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct);
}
