using System.Collections;
using System.Reflection;
using NUnit.Framework;
using PlexCompatibleServer.Api;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Tests;

/// <summary>
/// The in-memory stores are singletons that outlive every request, so they have to evict instead
/// of growing without bound. These pin the size caps and the oldest-first eviction order.
/// </summary>
[TestFixture]
public class StoreBoundTests
{
    [Test]
    public void PlaybackState_evicts_the_oldest_queue_once_the_cap_is_hit()
    {
        var state = new PlaybackState();

        for (var id = 1; id <= PlaybackState.MaxQueues + 1; id++)
        {
            state.Create(id, $"/library/metadata/{id}", $"Title {id}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(state.Get(1), Is.Null, "the oldest queue must be evicted");
            Assert.That(state.Get(PlaybackState.MaxQueues + 1), Is.Not.Null,
                "the newest queue must survive");
        });
    }

    [Test]
    public void StreamSelectionStore_evicts_the_oldest_selection_once_the_cap_is_hit()
    {
        var store = new StreamSelectionStore();

        for (var partId = 1; partId <= StreamSelectionStore.MaxEntries + 1; partId++)
        {
            store.Set(partId, partId, partId);
        }

        Assert.Multiple(() =>
        {
            Assert.That(store.Get(1), Is.EqualTo((0, 0)), "the oldest selection must be evicted");
            Assert.That(store.Get(StreamSelectionStore.MaxEntries + 1),
                Is.EqualTo((StreamSelectionStore.MaxEntries + 1, StreamSelectionStore.MaxEntries + 1)),
                "the newest selection must survive");
        });
    }

    [Test]
    public void SidecarSubtitles_evicts_oldest_listings_once_the_cap_is_hit()
    {
        var previous = SidecarSubtitles.MaxListings;
        SidecarSubtitles.MaxListings = 4;
        var root = Path.Combine(Path.GetTempPath(), "plexcompat-storebound-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var directory = Path.Combine(root, i.ToString());
                Directory.CreateDirectory(directory);
                SidecarSubtitles.Find(Path.Combine(directory, "Movie.mkv"));
            }

            Assert.That(ListingCount(), Is.LessThanOrEqualTo(SidecarSubtitles.MaxListings),
                "the directory-listing cache must not exceed its cap");
        }
        finally
        {
            SidecarSubtitles.MaxListings = previous;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    private static int ListingCount()
    {
        var field = typeof(SidecarSubtitles).GetField("Listings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dict = (IDictionary)field.GetValue(null)!;
        return dict.Count;
    }
}
