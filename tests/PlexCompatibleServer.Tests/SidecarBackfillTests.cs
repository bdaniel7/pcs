using System.Reflection;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

[TestFixture]
public class SidecarBackfillTests
{
    private ExternalMetadata metadata = null!;

    [SetUp]
    public void SetUp() => metadata = new ExternalMetadata();

    private void resetSidecarCache()
    {
        typeof(SidecarStore)
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(metadata.Store, null);
    }

    [Test]
    public void UpsertSidecar_merges_record_and_it_is_served_back()
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var sidecar = Path.Combine(wwwroot, "plex-metadata.json");
        resetSidecarCache();
        try
        {
            var rec = new SidecarItem
            {
                Title = "Backfill Test",
                Guid = "plex://movie/aaaaaaaaaaaaaaaaaaaaaaaa",
                Year = "2024",
                Summary = "A summary written by the backfill."
            };

            metadata.Store.UpsertSidecar("backfilltestkey", rec);

            Assert.That(File.Exists(sidecar), Is.True, "sidecar file not written");
            var raw = File.ReadAllText(sidecar);
            Assert.That(raw, Does.Contain("backfilltestkey"));
            Assert.That(raw, Does.Contain("plex://movie/aaaaaaaaaaaaaaaaaaaaaaaa"));

            resetSidecarCache();
            var item = new MediaItem
            {
                Id = 991,
                Title = "Backfill Test",
                FilePath = @"G:\Movies\Backfill.Test.2024.mp4",
                Year = 2024
            };
            Assert.That(metadata.HasRecord(item), Is.True,
                "record written by the backfill must be served");
        }
        finally
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { }
            try
            {
                if (Directory.Exists(wwwroot) && !Directory.EnumerateFileSystemEntries(wwwroot).Any())
                    Directory.Delete(wwwroot);
            }
            catch { }
            resetSidecarCache();
        }
    }

    [Test]
    public async Task Backfill_skips_covered_items_and_gates_items_without_a_library()
    {
        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var sidecar = Path.Combine(wwwroot, "plex-metadata.json");
        resetSidecarCache();
        try
        {
            var covered = new MediaItem
            {
                Id = 1,
                Title = "Backfill Test",
                FilePath = @"G:\Movies\Backfill.Test.2024.mp4",
                Year = 2024
            };
            metadata.Store.UpsertSidecar("backfilltestkey", new SidecarItem
            {
                Title = "Backfill Test",
                Guid = "plex://movie/aaaaaaaaaaaaaaaaaaaaaaaa"
            });

            // Library is null -> the movie-only gate must reject it before any network call.
            var gated = new MediaItem { Id = 2, Title = "Whatever", FilePath = @"G:\Movies\Whatever.2024.mp4", Year = 2024 };

            var result = await metadata.BackfillAsync(new[] { covered, gated }, CancellationToken.None);

            Assert.That(result.Scanned, Is.EqualTo(2));
            Assert.That(result.Present, Is.EqualTo(1), "covered item must be skipped");
            Assert.That(result.Created, Is.EqualTo(0));
            Assert.That(result.Failed, Does.Contain("Whatever"));
        }
        finally
        {
            try { if (File.Exists(sidecar)) File.Delete(sidecar); } catch { }
            try
            {
                if (Directory.Exists(wwwroot) && !Directory.EnumerateFileSystemEntries(wwwroot).Any())
                    Directory.Delete(wwwroot);
            }
            catch { }
            resetSidecarCache();
        }
    }
}

