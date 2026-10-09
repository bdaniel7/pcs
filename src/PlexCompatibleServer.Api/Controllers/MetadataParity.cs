using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Interfaces;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Metadata every item response states regardless of what the request asked for.
///
/// The movie detail screen reads these fields unconditionally: an absent key or an empty credit
/// list throws on the client and the screen reports that content could not be loaded. Official
/// Plex always states them, even for items it knows nothing about, so we do too - the values are
/// neutral placeholders, not real data. The related hub rows go through the same path because the
/// client dereferences their credit lists the same way it does the detail item's.
/// </summary>
internal static class MetadataParity
{
    /// <param name="item">The media item being described.</param>
    /// <param name="video">The generated metadata built for it.</param>
    /// <param name="ratingKey">Rating key used to derive stable tag ids.</param>
    /// <param name="includeExtras">
    /// True to state the empty Extras container. Official Plex emits it on detail responses;
    /// hub rows omit it.
    /// </param>
    /// <param name="allowNetwork">
    /// True for detail responses, which may resolve a cache miss against plex.tv. False keeps the
    /// overlay strictly on the loaded stores and never waits on the network.
    /// </param>
    public static async Task ApplyAsync(IMetadataService metadata, MediaItem item, XmlVideo video,
                                        int ratingKey, bool includeExtras,
                                        bool allowNetwork = true, CancellationToken ct = default)
    {
        // Overlay real metadata from the official Plex sidecar when available.
        try
        {
            var rec = allowNetwork
                ? await metadata.ResolveAsync(item, video.Title, video.TitleSort, ct).ConfigureAwait(false)
                : metadata.ResolveLocal(item, video.Title, video.TitleSort);

            if (rec is not null) MetadataMapper.Overlay(item, video, rec);
        }
        catch (Exception)
        {
            // Ignore failures; fall back to generated/placeholder metadata.
        }

        video.EmitEmptyMetadataSections = true;
        if (includeExtras) video.Extras = new XmlExtras();

        // The two remaining fields official Plex states that we have no scraped data for. They are
        // stated rather than omitted for the same reason as the tag lists: the detail screen reads
        // them, and an absent key is not the same as an empty one. The values are neutral
        // placeholders, not real data.
        video.UltraBlurColors ??= new XmlUltraBlurColors
        {
            TopLeft = "1c1c1c",
            TopRight = "1c1c1c",
            BottomRight = "0d0d0d",
            BottomLeft = "0d0d0d",
        };
        if (video.CommonSenseMedia.Count == 0)
        {
            video.CommonSenseMedia.Add(new XmlCommonSenseMedia
            {
                Id = "0",
                OneLiner = "Unknown",
                AgeRatings = { new XmlAgeRating { Type = "official", Rating = 0, Age = 0 } },
            });
        }

        // Official Plex always states a release date here. We only know the year, so state the
        // year rather than omitting the field the detail screen reads.
        if (video.OriginallyAvailableAt.Length == 0 && video.Year.Length > 0)
        {
            video.OriginallyAvailableAt = $"{video.Year}-01-01";
        }

        SeedUnknownTags(video, ratingKey);
    }

    /// <summary>
    /// Gives every tag list on a detail response at least one entry.
    ///
    /// The client dereferences the first element of the credit lists (Director, Writer, Role) and
    /// the genre/country lists without checking that one exists. We carry no cast or crew metadata,
    /// so those lists are empty, the dereference throws, and the movie screen falls back to
    /// "content could not be loaded" - which is why TV Shows renders the same file without trouble:
    /// an episode screen never reads these lists.
    ///
    /// Seeding a single "Unknown" entry keeps every list non-empty for the client. It is a
    /// placeholder, not real metadata, and it renders on screen as an "Unknown" chip.
    ///
    /// Ids are derived from the item's rating key so a given entry keeps the same id across
    /// responses, matching the way official Plex numbers its tags.
    /// </summary>
    private static void SeedUnknownTags(XmlVideo video, int ratingKey)
    {
        XmlTag Seed(int offset, string filterName) => new()
        {
            Id = (ratingKey * 10 + offset).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Filter = $"{filterName}={ratingKey * 10 + offset}",
            Tag = "Unknown",
        };

        if (video.Directors.Count == 0) video.Directors.Add(Seed(1, "director"));
        if (video.Writers.Count == 0) video.Writers.Add(Seed(2, "writer"));
        // Role entries are filtered by "actor", not "role": that is the filter name the client uses to
        // build its person pages, so a "role=" filter would produce dead links.
        if (video.Roles.Count == 0) video.Roles.Add(Seed(3, "actor"));
        if (video.Genres.Count == 0) video.Genres.Add(Seed(4, "genre"));
        if (video.Countries.Count == 0) video.Countries.Add(Seed(5, "country"));
        if (video.Producers.Count == 0) video.Producers.Add(Seed(6, "producer"));
        if (video.Ratings.Count == 0)
        {
            video.Ratings.Add(new XmlRating
            {
                Image = "imdb://image.rating",
                Value = "0",
                Type = "audience",
            });
        }
    }
}
