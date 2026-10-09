using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using PlexCompatibleServer.Api;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Hosted;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// Guards the Phase 3 N+1 fix: the hub and recently-added shelves load their rows with one
/// query across all libraries instead of one round trip per library.
/// </summary>
[TestFixture]
public class HubQueryCountTests
{
    [Test]
    public async Task Hubs_loads_movie_and_show_rows_with_batched_queries()
    {
        var repo = buildRepo();
        var controller = withRequest(new HubController(repo, new ServerOptions(), new ExternalMetadata()));

        await controller.Hubs(CancellationToken.None);

        // One batch for the movie libraries and one for the show libraries; no per-library calls.
        Assert.That(repo.BatchCalls, Is.EqualTo(2));
        Assert.That(repo.PerLibraryCalls, Is.Zero);
    }

    [Test]
    public async Task HomeRecentlyAdded_loads_movie_rows_with_a_batched_query()
    {
        var repo = buildRepo();
        var controller = withRequest(new HubController(repo, new ServerOptions(), new ExternalMetadata()));

        await controller.HomeRecentlyAdded(type: 1, CancellationToken.None);

        Assert.That(repo.BatchCalls, Is.EqualTo(1));
        Assert.That(repo.PerLibraryCalls, Is.Zero);
    }

    [Test]
    public async Task RecentlyAddedAll_loads_every_library_with_a_batched_query()
    {
        var repo = buildRepo();
        var controller = withRequest(new LibraryController(
            repo, new MediaScanTrigger(), new ServerOptions(), new StreamSelectionStore(), new ExternalMetadata()));

        await controller.RecentlyAddedAll(CancellationToken.None);

        Assert.That(repo.BatchCalls, Is.EqualTo(1));
        Assert.That(repo.PerLibraryCalls, Is.Zero);
    }

    private static T withRequest<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static CountingRepo buildRepo()
    {
        var now = DateTimeOffset.UtcNow;
        var moviesOne = new MediaLibrary { Id = 1, Name = "Movies", Type = LibraryType.Movie };
        var moviesTwo = new MediaLibrary { Id = 2, Name = "More Movies", Type = LibraryType.Movie };
        var shows = new MediaLibrary { Id = 3, Name = "TV", Type = LibraryType.Show };

        moviesOne.Items.Add(item(1, moviesOne, "A", now));
        moviesTwo.Items.Add(item(2, moviesTwo, "B", now));
        shows.Items.Add(item(3, shows, "C", now));

        return new CountingRepo([moviesOne, moviesTwo, shows]);
    }

    private static MediaItem item(int id, MediaLibrary library, string title, DateTimeOffset updatedAt) => new()
    {
        Id = id,
        LibraryId = library.Id,
        Library = library,
        Title = title,
        SortTitle = title,
        FilePath = $"/media/{title}.mkv",
        UpdatedAt = updatedAt
    };

    private sealed class CountingRepo(params MediaLibrary[] libraries) : IMediaRepository
    {
        private readonly List<MediaLibrary> libraries = libraries.ToList();

        public int PerLibraryCalls { get; private set; }
        public int BatchCalls { get; private set; }

        public Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaLibrary>>(libraries);

        public Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct)
            => Task.FromResult(libraries.FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct)
        {
            PerLibraryCalls++;
            return Task.FromResult<IReadOnlyList<MediaItem>>(
                libraries.Where(x => x.Id == libraryId).SelectMany(x => x.Items).ToList());
        }

        public Task<MediaItem?> GetItemAsync(int id, CancellationToken ct)
            => Task.FromResult(libraries.SelectMany(x => x.Items).FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<MediaItem>> GetItemsByLibrariesAsync(IReadOnlyList<int> libraryIds, CancellationToken ct)
        {
            BatchCalls++;
            return Task.FromResult<IReadOnlyList<MediaItem>>(
                libraries.Where(x => libraryIds.Contains(x.Id))
                    .OrderBy(x => x.Id)
                    .SelectMany(x => x.Items)
                    .ToList());
        }

        public Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct)
            => Task.CompletedTask;

        public Task SaveProgressAsync(int id, long timeMs, long durationMs, DateTimeOffset viewedAt, CancellationToken ct)
            => Task.CompletedTask;

        public Task MarkWatchedAsync(int id, DateTimeOffset viewedAt, CancellationToken ct)
            => Task.CompletedTask;

        public Task ClearProgressAsync(int id, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<MediaItem>> GetInProgressAsync(int? libraryId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaItem>>([]);

        public Task DismissFromContinueWatchingAsync(int id, CancellationToken ct) => Task.CompletedTask;

        public Task SaveOfficialArtworkAsync(int id, string? posterPath, string? artPath, string? parentPosterPath,
                                             string? grandparentPosterPath, CancellationToken ct)
            => Task.CompletedTask;
    }
}
