using System.Collections.Concurrent;
using System.Reflection;
using System.Xml.Linq;
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

public sealed class LibraryPagingTests
{
    [Test]
    public async Task All_honours_window_from_query_parameters()
    {
        var controller = BuildController();
        SetQuery(controller, "X-Plex-Container-Start=1&X-Plex-Container-Size=2");

        var container = ParseXml(await controller.All(1, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(container.Attribute("size")?.Value, Is.EqualTo("2"));
            Assert.That(container.Attribute("offset")?.Value, Is.EqualTo("1"));
            Assert.That(container.Attribute("totalSize")?.Value, Is.EqualTo("5"));
        });
        Assert.That(Titles(container), Is.EqualTo(new[] { "Movie 2", "Movie 3" }));
    }

    [Test]
    public async Task All_honours_window_from_headers()
    {
        var controller = BuildController();
        var http = new DefaultHttpContext();
        http.Request.Headers["X-Plex-Container-Start"] = "3";
        http.Request.Headers["X-Plex-Container-Size"] = "10";
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        var container = ParseXml(await controller.All(1, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(container.Attribute("size")?.Value, Is.EqualTo("2"));
            Assert.That(container.Attribute("offset")?.Value, Is.EqualTo("3"));
            Assert.That(container.Attribute("totalSize")?.Value, Is.EqualTo("5"));
        });
        Assert.That(Titles(container), Is.EqualTo(new[] { "Movie 4", "Movie 5" }));
    }

    [Test]
    public async Task All_size_zero_returns_no_items_but_states_total()
    {
        var controller = BuildController();
        SetQuery(controller, "X-Plex-Container-Start=0&X-Plex-Container-Size=0");

        var container = ParseXml(await controller.All(1, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(container.Attribute("size")?.Value, Is.EqualTo("0"));
            Assert.That(container.Attribute("totalSize")?.Value, Is.EqualTo("5"));
            Assert.That(container.Elements("Video"), Is.Empty);
        });
    }

    [Test]
    public async Task All_without_window_returns_everything()
    {
        var controller = BuildController();

        var container = ParseXml(await controller.All(1, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(container.Attribute("size")?.Value, Is.EqualTo("5"));
            Assert.That(container.Attribute("totalSize")?.Value, Is.EqualTo("5"));
            Assert.That(Titles(container).Count, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task All_shows_the_cached_official_title_instead_of_the_file_name()
    {
        ResetExternalCache();
        try
        {
            typeof(ExternalMetadata)
                .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
                {
                    ["movie2"] = new SidecarItem
                    {
                        Title = "Real Movie Two",
                        TitleSort = "Real Movie Two",
                        DetailChecked = true
                    }
                });

            var controller = BuildController();
            var container = ParseXml(await controller.All(1, CancellationToken.None));

            Assert.That(Titles(container), Is.EqualTo(new[]
            {
                "Movie 1", "Real Movie Two", "Movie 3", "Movie 4", "Movie 5"
            }));
        }
        finally
        {
            ResetExternalCache();
        }
    }

    [Test]
    public async Task All_states_episode_hierarchy_but_never_the_foreign_parent_keys()
    {
        ResetExternalCache();
        try
        {
            var library = new MediaLibrary { Id = 2, Name = "TV", Type = LibraryType.Show };
            library.Items.Add(new MediaItem
            {
                Id = 45,
                LibraryId = library.Id,
                Library = library,
                Title = "Slow Horses S06E02 Daddy Issues 1080p.mkv",
                FilePath = @"G:\Series\Slow Horses S06E02 Daddy Issues 1080p.mkv"
            });

            typeof(ExternalMetadata)
                .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
                {
                    ["slowhorsess06e02daddyissues1080p"] = new SidecarItem
                    {
                        Title = "Daddy Issues",
                        Index = "2",
                        ParentIndex = "6",
                        ParentTitle = "Season 6",
                        GrandparentTitle = "Slow Horses",
                        DetailChecked = true
                    }
                });

            var controller = new LibraryController(
                new FakeRepo(library),
                new MediaScanTrigger(),
                new ServerOptions(),
                new StreamSelectionStore());
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var container = ParseXml(await controller.All(2, CancellationToken.None));
            var video = container.Elements("Video").Single();

            Assert.Multiple(() =>
            {
                Assert.That(video.Attribute("title")?.Value, Is.EqualTo("Daddy Issues"));
                Assert.That(video.Attribute("index")?.Value, Is.EqualTo("2"));
                Assert.That(video.Attribute("parentIndex")?.Value, Is.EqualTo("6"));
                Assert.That(video.Attribute("parentTitle")?.Value, Is.EqualTo("Season 6"));
                Assert.That(video.Attribute("grandparentTitle")?.Value, Is.EqualTo("Slow Horses"));
                // plex.tv's foreign keys would send the client into /library/metadata/{key}/children 404s.
                Assert.That(video.Attribute("parentKey"), Is.Null);
                Assert.That(video.Attribute("grandparentKey"), Is.Null);
            });
        }
        finally
        {
            ResetExternalCache();
        }
    }

    private static void ResetExternalCache()
    {
        typeof(ExternalMetadata)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, null);
    }

    private static LibraryController BuildController()
    {
        var library = new MediaLibrary { Id = 1, Name = "Movies", Type = LibraryType.Movie };
        for (var id = 1; id <= 5; id++)
        {
            library.Items.Add(new MediaItem
            {
                Id = id,
                LibraryId = library.Id,
                Library = library,
                Title = $"Movie {id}",
                FilePath = $"/media/movie{id}.mkv"
            });
        }

        var controller = new LibraryController(
            new FakeRepo(library),
            new MediaScanTrigger(),
            new ServerOptions(),
            new StreamSelectionStore());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static void SetQuery(LibraryController controller, string query)
        => controller.HttpContext.Request.QueryString = new QueryString($"?{query}");

    private static XElement ParseXml(IActionResult result)
    {
        var content = result as ContentResult;
        Assert.That(content, Is.Not.Null);
        return XDocument.Parse(content!.Content!).Root!;
    }

    private static List<string?> Titles(XElement container)
        => container.Elements("Video").Select(x => (string?)x.Attribute("title")).ToList();

    private sealed class FakeRepo : IMediaRepository
    {
        private readonly List<MediaLibrary> _libraries;

        public FakeRepo(params MediaLibrary[] libraries) => _libraries = libraries.ToList();

        public Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaLibrary>>(_libraries);

        public Task<MediaLibrary?> GetLibraryAsync(int id, CancellationToken ct)
            => Task.FromResult(_libraries.FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<MediaItem>> GetItemsAsync(int libraryId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaItem>>(
                _libraries.Where(x => x.Id == libraryId).SelectMany(x => x.Items).ToList());

        public Task<MediaItem?> GetItemAsync(int id, CancellationToken ct)
            => Task.FromResult(_libraries.SelectMany(x => x.Items).FirstOrDefault(x => x.Id == id));

        public Task SynchronizeAsync(IReadOnlyList<MediaLibrary> libraries, CancellationToken ct)
            => Task.CompletedTask;

        public Task SaveProgressAsync(int id, long timeMs, long durationMs, DateTimeOffset viewedAt, CancellationToken ct)
            => Task.CompletedTask;

        public Task MarkWatchedAsync(int id, DateTimeOffset viewedAt, CancellationToken ct)
            => Task.CompletedTask;

        public Task ClearProgressAsync(int id, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MediaItem>> GetInProgressAsync(int? libraryId, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<MediaItem>>([]);
    }
}
