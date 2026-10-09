using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Facade over the external-metadata collaborators. Owns the resolution orchestration and the
/// <see cref="SidecarStore"/> lookup path; storage sits in <see cref="SidecarStore"/>, local
/// matching in <see cref="MetadataMatcher"/>, plex.tv access in <see cref="PlexTvClient"/> and the
/// video mapping in <see cref="MetadataMapper"/>.
/// </summary>
internal sealed class ExternalMetadata : IMetadataService
{
    private readonly MetadataMatcher _matcher;
    private readonly PlexTvClient _plex;

    internal SidecarStore Store { get; }

    internal PlexTvClient Plex => _plex;

    internal ExternalMetadata(HttpClient? http = null, string? contentRoot = null)
    {
        Store = new SidecarStore(contentRoot);
        _matcher = new MetadataMatcher(Store);
        _plex = new PlexTvClient(Store, http);
    }

    /// <summary>
    /// Captures the auth token the client sends (only on /identity). Full plex.tv metadata lookups
    /// need it; the search endpoint works anonymously, so a missing token still yields the guid.
    /// </summary>
    public void CaptureToken(string? token) => _plex.CaptureToken(token);

    public bool TokenKnown => _plex.TokenKnown;

    /// <summary>
    /// Cache-only overlay used by list/grid responses: a page of titles must never wait on
    /// plex.tv, and the client re-requests lists constantly. A miss keeps the generated title.
    /// </summary>
    internal void Apply(MediaItem item,
                        XmlVideo video)
    {
        var rec = _matcher.ResolveLocal(item, video.Title, video.TitleSort);

        if (rec is not null) MetadataMapper.Overlay(item, video, rec);
    }

    /// <summary>
    /// Detail-path overlay with the online fallback: an item without a real guid cannot open its
    /// detail page at all, so a cache miss is resolved against plex.tv. Every network wait is
    /// awaited, so no request thread is blocked.
    /// </summary>
    internal async Task ApplyAsync(MediaItem item,
                                   XmlVideo video,
                                   CancellationToken ct = default)
    {
        var rec = await ResolveAsync(item, video.Title, video.TitleSort, ct).ConfigureAwait(false);

        if (rec is not null) MetadataMapper.Overlay(item, video, rec);
    }

    /// <summary>
    /// Resolves the record for an item, falling back to plex.tv when the loaded stores do not
    /// cover it. Existing detail-path enrichment (guid-only fill-in, episode hierarchy re-walk)
    /// lives here so the mapper can stay a pure overlay.
    /// </summary>
    public async Task<SidecarItem?> ResolveAsync(MediaItem item,
                                                 string? title,
                                                 string? titleSort,
                                                 CancellationToken ct = default)
    {
        var isEpisode = item.Library is { Type: LibraryType.Show };
        var rec = _matcher.ResolveLocal(item, title, titleSort);

        // No local record: look the movie up on plex.tv. The client resolves the guid it gets
        // against plex.tv, so an item without a real guid cannot open its detail page at all.
        if (rec is null) rec = await _plex.LookupOnlineAsync(item, ct).ConfigureAwait(false);

        // A lookup that ran before the client sent its token (identity precedes metadata calls)
        // only has the guid; fill in the rich fields once the token is known.
        if (rec is not null && !rec.DetailChecked && !string.IsNullOrEmpty(rec.RatingKey) &&
            _plex.TokenKnown)
        {
            if (await _plex.TryRefetchDetailAsync(rec, ct).ConfigureAwait(false))
            {
                var enrichedKey = MetadataMatcher.GetKey(item);
                if (!string.IsNullOrEmpty(enrichedKey)) Store.PersistLookup(enrichedKey, rec);
            }
        }

        // A record written by the old scrape (or a pre-token lookup) carries the episode's content
        // but no season/episode hierarchy - the info screen reads those fields - so re-walk the
        // show once, after the token is known, and keep serving the old record if that fails.
        if (rec is not null && isEpisode && _plex.TokenKnown &&
            (rec.Index is null || rec.ParentTitle is null || rec.GrandparentTitle is null))
        {
            var fresh = await _plex.FetchRecordAsync(item, ct).ConfigureAwait(false);

            if (fresh is not null)
            {
                rec = fresh;
                var freshKey = MetadataMatcher.GetKey(item);
                if (!string.IsNullOrEmpty(freshKey)) Store.PersistLookup(freshKey, rec);
            }
        }

        return rec;
    }

    /// <summary>
    /// Resolves an item against the loaded stores only, with the same exact-key / title / fuzzy
    /// fallbacks the overlay uses. Null means no loaded store covers the item.
    /// </summary>
    public SidecarItem? ResolveLocal(MediaItem item,
                                     string? title,
                                     string? titleSort) => _matcher.ResolveLocal(item, title, titleSort);

    internal bool HasRecord(MediaItem item) => _matcher.HasRecord(item);

    public bool TryGetRecord(MediaItem item,
                             out SidecarItem record) => _matcher.TryGetRecord(item, out record);

    /// <summary>
    /// Fills sidecar gaps for every item no store covers: plex.tv search + detail per movie,
    /// show/season/episode walk per episode, written back into plex-metadata.json (atomic replace)
    /// and the in-memory cache. Already-covered items are skipped without network traffic.
    /// </summary>
    public async Task<BackfillResult> BackfillAsync(IReadOnlyList<MediaItem> items,
                                                    CancellationToken ct)
    {
        var result = new BackfillResult();

        foreach (var item in MetadataMatcher.OrderLookupPasses(items))
        {
            ct.ThrowIfCancellationRequested();
            result.Scanned++;
            var label = item.Title ?? item.FilePath ?? $"item {item.Id}";

            try
            {
                if (_matcher.TryGetRecord(item, out var existing))
                {
                    // Episodes the old scrape covered have content but no season/episode
                    // hierarchy, and a guid-only record still awaits its detail: re-fetch both
                    // once. A failed re-fetch keeps the existing record (Present), never Failed.
                    var episodeMissingHierarchy = item.Library is { Type: LibraryType.Show } &&
                                                  (existing.Index is null || existing.ParentTitle is null ||
                                                   existing.GrandparentTitle is null);

                    // Records captured before artwork capture existed carry no remote URLs: refresh
                    // them so the poster/backdrop downloads have something to fetch. Movies only need
                    // their detail re-read (anonymous, no re-binding through search); episodes must
                    // re-walk the show to pick up parentThumb/grandparentThumb.
                    var movieMissingArtwork = item.Library is { Type: LibraryType.Movie } &&
                                              (existing.ThumbUrl is null || existing.ArtUrl is null);

                    var episodeMissingArtwork = item.Library is { Type: LibraryType.Show } &&
                                                (existing.ParentThumbUrl is null ||
                                                 existing.GrandparentThumbUrl is null);

                    if (movieMissingArtwork && !string.IsNullOrEmpty(existing.RatingKey))
                    {
                        var refreshed = await _plex.TryRefetchDetailAsync(existing, ct).ConfigureAwait(false);
                        var artKey = MetadataMatcher.GetKey(item);

                        if (refreshed && !string.IsNullOrEmpty(artKey))
                        {
                            Store.UpsertSidecar(artKey, existing);
                            result.Enriched++;

                            continue;
                        }
                        result.Present++;

                        continue;
                    }

                    // Anything still reaching this branch is a movie whose record has no rating key
                    // (the detail-only path above already handled the others): the full search path
                    // re-binds it and captures the artwork URLs too.
                    if (episodeMissingHierarchy ||
                        episodeMissingArtwork ||
                        movieMissingArtwork ||
                        (!existing.DetailChecked && !string.IsNullOrEmpty(existing.RatingKey) &&
                         _plex.TokenKnown))
                    {
                        var enriched = await _plex.FetchRecordAsync(item, ct).ConfigureAwait(false);
                        var existingKey = MetadataMatcher.GetKey(item);

                        if (enriched?.Guid is not null && !string.IsNullOrEmpty(existingKey))
                        {
                            Store.UpsertSidecar(existingKey, enriched);
                            result.Enriched++;

                            continue;
                        }
                    }
                    result.Present++;

                    continue;
                }
                var rec = await _plex.FetchRecordAsync(item, ct).ConfigureAwait(false);
                var key = MetadataMatcher.GetKey(item);

                if (rec?.Guid is null || string.IsNullOrEmpty(key))
                {
                    result.Failed.Add(label);

                    continue;
                }
                Store.UpsertSidecar(key, rec);
                result.Created++;
                result.CreatedTitles.Add(rec.Title ?? label);
            }
            catch
            {
                result.Failed.Add(label);
            }
        }

        return result;
    }
}
