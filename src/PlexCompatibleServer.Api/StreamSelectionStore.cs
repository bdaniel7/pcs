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
    private readonly ConcurrentDictionary<int, (int Audio, int Subtitle)> _selected = new();

    public void Set(int partId, int? audioStreamId, int? subtitleStreamId) =>
        _selected.AddOrUpdate(
            partId,
            _ => (audioStreamId ?? 0, subtitleStreamId ?? 0),
            (_, current) => (audioStreamId ?? current.Audio, subtitleStreamId ?? current.Subtitle));

    public (int Audio, int Subtitle) Get(int partId) =>
        _selected.TryGetValue(partId, out var selected) ? selected : (0, 0);
}
