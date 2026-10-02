using System.Collections.Concurrent;

namespace PlexCompatibleServer.Api;

/// <summary>
/// Minimal in-memory stand-in for the Plex play queue. The client creates a queue before playback
/// (POST /playQueues), then re-reads it (GET /playQueues/{id}) to restore state after a launch or
/// when the "next up" panel opens, so both directions have to agree on the same identifiers.
/// Plex hands out three distinct ids: the queue itself, the play-queue item, and the library
/// rating key it points at.
/// </summary>
public sealed class PlaybackState
{
    /// <summary>
    /// Plex rotates this as its media-flag bundles change. The client uses it to invalidate cached
    /// watch state, so a value stable for the lifetime of the process is what matters.
    /// </summary>
    public static string MediaTagVersion { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

    private readonly ConcurrentDictionary<int, PlayQueue> _queues = new();
    private int _nextQueueId;
    private int _nextItemId;

    /// <summary>
    /// Creates a single-item queue for the given rating key. Repeated creation for the same source is
    /// expected (the client re-seeds on every launch), so each call yields fresh ids.
    /// </summary>
    public PlayQueue Create(int ratingKey, string sourceUri, string sourceTitle)
    {
        var queueId = Interlocked.Increment(ref _nextQueueId);
        var itemId = Interlocked.Increment(ref _nextItemId);

        var queue = new PlayQueue
        {
            Id = queueId,
            Version = 1,
            SourceUri = sourceUri,
            SourceTitle = sourceTitle,
            Items = { new PlayQueueItem(itemId, ratingKey) },
            SelectedItemId = itemId,
            SelectedRatingKey = ratingKey
        };

        _queues[queueId] = queue;
        return queue;
    }

    public PlayQueue? Get(int id) => _queues.TryGetValue(id, out var queue) ? queue : null;

    public bool SetOffset(int id, long offset)
    {
        if (!_queues.TryGetValue(id, out var queue)) return false;
        queue.SelectedOffset = Math.Max(0, offset);
        queue.Version++;
        return true;
    }

    public sealed class PlayQueue
    {
        public required int Id { get; init; }
        public int Version { get; set; } = 1;
        public required string SourceUri { get; init; }
        public string SourceTitle { get; init; } = "";
        public List<PlayQueueItem> Items { get; init; } = new();
        public required int SelectedItemId { get; init; }
        public required int SelectedRatingKey { get; init; }
        public long SelectedOffset { get; set; }
    }

    public sealed record PlayQueueItem(int Id, int RatingKey);
}
