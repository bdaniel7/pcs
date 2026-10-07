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
}
