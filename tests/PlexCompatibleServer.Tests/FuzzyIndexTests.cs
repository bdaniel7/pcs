using System.Collections.Concurrent;
using System.Reflection;
using NUnit.Framework;
using PlexCompatibleServer.Api.Controllers;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// Fuzzy lookups run off a precomputed index. These pin that the index still resolves the
/// title-containment match and that it is rebuilt when the underlying cache is replaced.
/// </summary>
[TestFixture]
public class FuzzyIndexTests
{
    private static readonly FieldInfo CacheField = typeof(SidecarStore)
        .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetCache(ExternalMetadata metadata, ConcurrentDictionary<string, SidecarItem> cache)
        => CacheField.SetValue(metadata.Store, cache);

    [Test]
    public void ResolveLocal_matches_a_record_by_filename_stem_containment()
    {
        var metadata = new ExternalMetadata();
        SetCache(metadata, new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
        {
            ["first"] = new SidecarItem { Title = "First Movie", FileStem = "first.movie.2019" }
        });

        var item = new MediaItem
        {
            Id = 1,
            LibraryId = 1,
            Library = new MediaLibrary { Id = 1, Type = LibraryType.Movie },
            Title = "first.movie.2019",
            FilePath = @"G:\Movies\first.movie.2019.mkv"
        };

        Assert.That(metadata.ResolveLocal(item, item.Title, item.Title)?.Title,
            Is.EqualTo("First Movie"));
    }

    [Test]
    public void Fuzzy_index_is_rebuilt_when_the_cache_is_replaced()
    {
        var metadata = new ExternalMetadata();
        var library = new MediaLibrary { Id = 1, Type = LibraryType.Movie };

        SetCache(metadata, new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
        {
            ["first"] = new SidecarItem { Title = "First Movie", FileStem = "first.movie.2019" }
        });

        var first = new MediaItem
        {
            Id = 1,
            LibraryId = 1,
            Library = library,
            Title = "first.movie.2019",
            FilePath = @"G:\Movies\first.movie.2019.mkv"
        };
        Assert.That(metadata.ResolveLocal(first, first.Title, first.Title)?.Title, Is.EqualTo("First Movie"),
            "the first cache revision must resolve through the index");

        SetCache(metadata, new ConcurrentDictionary<string, SidecarItem>(StringComparer.Ordinal)
        {
            ["second"] = new SidecarItem { Title = "Second Movie", FileStem = "second.movie.2020" }
        });

        var second = new MediaItem
        {
            Id = 2,
            LibraryId = 1,
            Library = library,
            Title = "second.movie.2020",
            FilePath = @"G:\Movies\second.movie.2020.mkv"
        };
        Assert.Multiple(() =>
        {
            Assert.That(metadata.ResolveLocal(second, second.Title, second.Title)?.Title, Is.EqualTo("Second Movie"),
                "a replaced cache must be re-indexed, not served stale");
            Assert.That(metadata.ResolveLocal(first, first.Title, first.Title), Is.Null,
                "the old record is gone after the cache swap");
        });
    }
}
