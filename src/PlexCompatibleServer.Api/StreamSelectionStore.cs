using System.Collections.Concurrent;

namespace PlexCompatibleServer.Api;

/// <summary>
/// The audio/subtitle pair a viewer chose, written through PUT /library/parts/{partId} and read
/// back when the library publishes its stream list. The client pauses, PUTs the track it wants,
/// then re-reads metadata for the track flagged selected so it knows which key to fetch; a PUT
/// that answers 404 (an unimplemented route) makes it abandon the choice, and playback carries on
/// with no subtitle at all.
/// Keyed by part id, which is the item's rating key in this server's single-part model. A stored
/// id of 0 means "no subtitle", and an omitted parameter leaves that half of the pair alone, the
/// way the real endpoint treats audioStreamID and subtitleStreamID as independent.
/// </summary>
public sealed class StreamSelectionStore
{
    /// <summary>
    /// A viewer only carries a handful of active selections, but a long-running process sees every
    /// part it ever played. Cap the store and forget the least-recently-written choice once it
    /// grows past the limit.
    /// </summary>
    internal const int MAX_ENTRIES = 1024;

    private readonly ConcurrentDictionary<int, Entry> selected = new();
    private long sequence;

    public void Set(int partId, int? audioStreamId, int? subtitleStreamId)
    {
        var touched = Interlocked.Increment(ref sequence);

        selected.AddOrUpdate(
            partId,
            _ => new Entry(audioStreamId ?? 0, subtitleStreamId ?? 0, touched),
            (_, current) => new Entry(audioStreamId ?? current.Audio, subtitleStreamId ?? current.Subtitle, touched));

        trim();
    }

    public (int Audio, int Subtitle) Get(int partId) =>
        this.selected.TryGetValue(partId, out var selected) ? (selected.Audio, selected.Subtitle) : (0, 0);

    private void trim()
    {
        var excess = selected.Count - MAX_ENTRIES;
        if (excess <= 0) return;

        foreach (var stale in selected.OrderBy(static x => x.Value.Sequence).Take(excess))
        {
            selected.TryRemove(stale.Key, out _);
        }
    }

    private readonly record struct Entry(int Audio, int Subtitle, long Sequence);
}
