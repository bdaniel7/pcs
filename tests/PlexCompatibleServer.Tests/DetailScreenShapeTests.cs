using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Api.Options;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// Guards the two response shapes a working Plex-for-LG detail screen depends on, both read out of a
/// capture of the real server while that client played a movie:
///   /hubs/metadata/{id}/related -> one populated hub, type="movie", context="hub.movie",
///                                 no hubIdentifier, size = number of rows, allowSync as a boolean
///   /photo/:/transcode            -> the slot named in the url (/art/ vs /thumb/)
/// </summary>
[TestFixture]
public class DetailScreenShapeTests
{
    private string _artDir = "";

    [SetUp]
    public void SetUp()
    {
        _artDir = Path.Combine(Path.GetTempPath(), "plex-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_artDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_artDir)) Directory.Delete(_artDir, recursive: true);
    }

    [Test]
    public async Task Related_ReturnsPopulatedMovieHub()
    {
        var repo = BuildRepo("art.jpg", "poster.jpg");
        var controller = WithJsonAccept(new HubController(repo, new ServerOptions()));

        var result = await controller.Related(8, CancellationToken.None) as ContentResult;
        Assert.That(result, Is.Not.Null);

        using var doc = JsonDocument.Parse(result!.Content!);
        var container = doc.RootElement.GetProperty("MediaContainer");

        // The container carries library coordinates and size counts hubs, not rows.
        Assert.That(container.GetProperty("size").GetInt32(), Is.EqualTo(1));
        Assert.That(container.GetProperty("allowSync").ValueKind, Is.EqualTo(JsonValueKind.True));
        Assert.That(container.GetProperty("identifier").GetString(), Is.EqualTo("com.plexapp.plugins.library"));
        Assert.That(container.GetProperty("librarySectionID").GetInt32(), Is.EqualTo(1));
        Assert.That(container.TryGetProperty("librarySectionUUID", out _), Is.True);

        var hub = container.GetProperty("Hub").EnumerateArray().Single();

        // A hub typed "related" with context "movie" is not what the detail screen reads.
        Assert.That(hub.GetProperty("type").GetString(), Is.EqualTo("movie"));
        Assert.That(hub.GetProperty("context").GetString(), Is.EqualTo("hub.movie"));
        Assert.That(hub.TryGetProperty("identifier", out _), Is.False);

        var rows = hub.GetProperty("Metadata").EnumerateArray().ToList();
        Assert.That(hub.GetProperty("size").GetInt32(), Is.EqualTo(rows.Count));
        Assert.That(rows, Is.Not.Empty);

        // The movie being viewed is not its own recommendation.
        Assert.That(rows.Any(x => x.GetProperty("ratingKey").GetString() == "8"), Is.False);
    }

    [Test]
    public async Task Related_MoreIsEmittedAsBoolean()
    {
        var repo = BuildRepo("art.jpg", "poster.jpg");
        var controller = WithJsonAccept(new HubController(repo, new ServerOptions()));

        var result = await controller.Related(8, CancellationToken.None) as ContentResult;
        Assert.That(result, Is.Not.Null);
        using var doc = JsonDocument.Parse(result!.Content!);
        var hub = doc.RootElement.GetProperty("MediaContainer").GetProperty("Hub").EnumerateArray().Single();

        Assert.That(hub.GetProperty("more").ValueKind, Is.EqualTo(JsonValueKind.True));
    }

    [Test]
    public async Task Photo_ArtUrlServesArtwork()
    {
        var art = Path.Combine(_artDir, "art.jpg");
        File.WriteAllBytes(art, new byte[] { 1, 2, 3 });
        var poster = Path.Combine(_artDir, "poster.jpg");
        File.WriteAllBytes(poster, new byte[] { 1, 2, 3 });
        var controller = new PhotoController(BuildRepo(art, poster));

        var result = await controller.Transcode("/library/metadata/8/art/1790948816", 1232, 693, default)
            as PhysicalFileResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.FileName, Is.EqualTo(art));
    }

    [Test]
    public async Task Photo_ThumbUrlServesPoster()
    {
        var art = Path.Combine(_artDir, "art.jpg");
        File.WriteAllBytes(art, new byte[] { 1, 2, 3 });
        var poster = Path.Combine(_artDir, "poster.jpg");
        File.WriteAllBytes(poster, new byte[] { 1, 2, 3 });
        var controller = new PhotoController(BuildRepo(art, poster));

        var result = await controller.Transcode("/library/metadata/8/thumb/1790948816", 240, 360, default)
            as PhysicalFileResult;

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.FileName, Is.EqualTo(poster));
    }

    private static FakeRepo BuildRepo(string art, string poster)
    {
        var library = new MediaLibrary { Id = 1, Name = "Movies", Type = LibraryType.Movie };

        for (var id = 1; id <= 9; id++)
        {
            library.Items.Add(new MediaItem
            {
                Id = id,
                LibraryId = library.Id,
                Library = library,
                Title = $"Movie {id}",
                FilePath = $"/media/movie{id}.mkv",
                ArtPath = art,
                PosterPath = poster
            });
        }

        return new FakeRepo(library);
    }

    private static T WithJsonAccept<T>(T controller) where T : ControllerBase
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.Accept = "application/json";
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

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
    }
}