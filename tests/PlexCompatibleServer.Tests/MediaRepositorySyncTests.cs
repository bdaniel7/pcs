using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Data;

namespace PlexCompatibleServer.Tests;

public class MediaRepositorySyncTests
{
    private sealed class TestFactory(DbContextOptions<MediaDbContext> options)
        : IDbContextFactory<MediaDbContext>
    {
        public MediaDbContext CreateDbContext() => new(options);
    }

    [Test]
    public async Task Synchronize_adds_new_files_and_prunes_vanished_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcs-sync-" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(Path.GetTempPath(), "pcs-sync-" + Guid.NewGuid().ToString("N") + ".db");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Movie.One.2020.mp4");
        try
        {
            await File.WriteAllBytesAsync(file, Array.Empty<byte>());
            var options = new DbContextOptionsBuilder<MediaDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            var factory = new TestFactory(options);
            await using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();

            var repo = new MediaRepository(factory);
            var libraries = new[]
            {
                new MediaLibrary { Name = "Movies", RootPath = root, Type = LibraryType.Movie }
            };

            await repo.SynchronizeAsync(libraries, CancellationToken.None);
            await using (var db = factory.CreateDbContext())
                Assert.That(await db.Items.CountAsync(), Is.EqualTo(1), "the new file must be added");

            File.Delete(file);
            await repo.SynchronizeAsync(libraries, CancellationToken.None);
            await using (var db = factory.CreateDbContext())
                Assert.That(await db.Items.CountAsync(), Is.EqualTo(0),
                    "rows whose file disappeared (deleted or renamed) must be pruned");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
            foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(p); } catch { }
        }
    }

    [Test]
    public async Task GetItemsByLibrariesAsync_returns_items_from_every_library_in_one_query()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "pcs-batch-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var options = new DbContextOptionsBuilder<MediaDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            var factory = new TestFactory(options);

            int moviesId;
            int showsId;
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                var movies = new MediaLibrary { Name = "Movies", RootPath = "C:\\m", Type = LibraryType.Movie };
                var shows = new MediaLibrary { Name = "Shows", RootPath = "C:\\s", Type = LibraryType.Show };
                db.Libraries.AddRange(movies, shows);
                await db.SaveChangesAsync();
                moviesId = movies.Id;
                showsId = shows.Id;

                db.Items.AddRange(
                    new MediaItem { LibraryId = moviesId, FilePath = "a", Title = "A", SortTitle = "A" },
                    new MediaItem { LibraryId = moviesId, FilePath = "b", Title = "B", SortTitle = "B" },
                    new MediaItem { LibraryId = showsId, FilePath = "c", Title = "C", SortTitle = "C" });
                await db.SaveChangesAsync();
            }

            var repo = new MediaRepository(factory);

            var across = await repo.GetItemsByLibrariesAsync([moviesId, showsId], CancellationToken.None);
            Assert.That(across.Select(x => x.Title), Is.EqualTo(new[] { "A", "B", "C" }),
                "items must span both libraries, ordered by library then id");

            var moviesOnly = await repo.GetItemsByLibrariesAsync([moviesId], CancellationToken.None);
            Assert.That(moviesOnly.Select(x => x.Title), Is.EqualTo(new[] { "A", "B" }));

            Assert.That(await repo.GetItemsByLibrariesAsync([], CancellationToken.None), Is.Empty,
                "an empty request must not round trip");
            Assert.That(await repo.GetItemsByLibrariesAsync([999], CancellationToken.None), Is.Empty,
                "an unknown id contributes nothing");
        }
        finally
        {
            foreach (var p in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(p); } catch { }
        }
    }
}
