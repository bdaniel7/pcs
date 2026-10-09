using System.Globalization;
using System.Text;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Pure field mapping: writes a resolved <see cref="SidecarItem"/> onto an <see cref="XmlVideo"/>'s
/// metadata sections. Kept static and store-free so it can be reused on any response path.
/// </summary>
internal static class MetadataMapper
{
    /// <summary>Applies a resolved record's fields onto the video's metadata sections.</summary>
    internal static void Overlay(MediaItem item,
                                 XmlVideo video,
                                 SidecarItem rec)
    {
        // The sidecar carries the item's real Plex guid, which metadata.plex.tv can resolve.
        // Prefer it over the generated one; the detail screen reads this field directly.
        if (!string.IsNullOrEmpty(rec.Guid))
        {
            video.Guid = rec.Guid;

            if (video.Guids.Count > 0) video.Guids[0].Id = rec.Guid;
            else video.Guids.Add(new XmlGuid { Id = rec.Guid });
        }

        // External source ids (imdb://, tmdb://, tvdb://) as the real server publishes them,
        // with the item's own plex guid first - that is the shape that worked for sidecar items.
        if (rec.Guids is { Count: > 0 })
        {
            video.Guids.Clear();
            if (!string.IsNullOrEmpty(rec.Guid)) video.Guids.Add(new XmlGuid { Id = rec.Guid });

            foreach (var g in rec.Guids)
                if (!string.IsNullOrEmpty(g) && g != rec.Guid)
                    video.Guids.Add(new XmlGuid { Id = g });
        }

        // Adopt the matched record's real display title; until now the raw filename was served
        // as title even when the sidecar/lookup knew the proper one.
        if (!string.IsNullOrEmpty(rec.Title)) video.Title = rec.Title;
        if (!string.IsNullOrEmpty(rec.TitleSort)) video.TitleSort = rec.TitleSort;

        // Episode hierarchy: the SxxExx numbers and the show/season breadcrumb the info screen
        // renders above the title. Display strings only - parentKey/parentRatingKey/grandparentKey
        // are deliberately NOT emitted: they carry plex.tv's foreign rating keys, and the client
        // follows them (/library/metadata/{key}/children), which cannot resolve locally (404).
        if (!string.IsNullOrEmpty(rec.Index)) video.Index = rec.Index;
        if (!string.IsNullOrEmpty(rec.ParentIndex)) video.ParentIndex = rec.ParentIndex;
        if (!string.IsNullOrEmpty(rec.ParentTitle)) video.ParentTitle = rec.ParentTitle;

        if (!string.IsNullOrEmpty(rec.ParentTitle) && video.ParentType.Length == 0)
            video.ParentType = "season";

        if (!string.IsNullOrEmpty(rec.GrandparentTitle))
            video.GrandparentTitle = rec.GrandparentTitle;

        if (!string.IsNullOrEmpty(rec.GrandparentTitle) && video.GrandparentType.Length == 0)
            video.GrandparentType = "show";

        if (!string.IsNullOrEmpty(rec.Studio)) video.Studio = rec.Studio;
        if (!string.IsNullOrEmpty(rec.Year)) video.Year = rec.Year;
        if (!string.IsNullOrEmpty(rec.Summary)) video.Summary = rec.Summary;
        if (!string.IsNullOrEmpty(rec.Tagline)) video.Tagline = rec.Tagline;
        if (!string.IsNullOrEmpty(rec.ContentRating)) video.ContentRating = rec.ContentRating;
        if (rec.ContentRatingAge is > 0) video.ContentRatingAge = rec.ContentRatingAge.Value.ToString(CultureInfo.InvariantCulture);

        video.Ratings.Clear();

        if (rec.Ratings is not null)
        {
            foreach (var r in rec.Ratings)
            {
                if (string.IsNullOrEmpty(r.Image)) continue;

                video.Ratings.Add(new XmlRating
                {
                    Image = r.Image,
                    Type = r.Type ?? "audience",
                    Value = r.Value.HasValue ? r.Value.Value.ToString("0.0", CultureInfo.InvariantCulture) : "0"
                });
            }
        }

        if (video.Ratings.Count == 0)
        {
            video.Ratings.Add(new XmlRating { Image = "imdb://image.rating", Type = "audience", Value = "0" });
        }

        if (rec.AudienceRating.HasValue)
        {
            video.AudienceRating = rec.AudienceRating.Value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        if (rec.Genres is not null)
        {
            foreach (var g in rec.Genres)
            {
                if (string.IsNullOrEmpty(g)) continue;
                video.Genres.Add(new XmlTag { Id = generateId(item.Id, "genre", g), Tag = g, Filter = $"genre={g}" });
            }
        }

        if (rec.Countries is not null)
        {
            foreach (var c in rec.Countries)
            {
                if (string.IsNullOrEmpty(c)) continue;
                video.Countries.Add(new XmlTag { Id = generateId(item.Id, "country", c), Tag = c, Filter = $"country={c}" });
            }
        }

        if (rec.Directors is not null)
        {
            foreach (var d in rec.Directors)
            {
                video.Directors.Add(new XmlTag
                {
                    Id = d.TagKey ?? generateId(item.Id, "director", d.Tag),
                    Tag = d.Tag ?? "Unknown",
                    TagKey = d.TagKey,
                    Thumb = d.Thumb,
                    Filter = $"director={d.TagKey ?? d.Tag}"
                });
            }
        }

        if (rec.Writers is not null)
        {
            foreach (var w in rec.Writers)
            {
                video.Writers.Add(new XmlTag
                {
                    Id = w.TagKey ?? generateId(item.Id, "writer", w.Tag),
                    Tag = w.Tag ?? "Unknown",
                    TagKey = w.TagKey,
                    Thumb = w.Thumb,
                    Filter = $"writer={w.TagKey ?? w.Tag}"
                });
            }
        }

        if (rec.Producers is not null)
        {
            foreach (var p in rec.Producers)
            {
                video.Producers.Add(new XmlTag
                {
                    Id = p.TagKey ?? generateId(item.Id, "producer", p.Tag),
                    Tag = p.Tag ?? "Unknown",
                    TagKey = p.TagKey,
                    Thumb = p.Thumb,
                    Role = p.Role,
                    Filter = $"producer={p.TagKey ?? p.Tag}"
                });
            }
        }

        if (rec.Roles is not null)
        {
            foreach (var a in rec.Roles)
            {
                video.Roles.Add(new XmlTag
                {
                    Id = a.TagKey ?? generateId(item.Id, "actor", a.Tag),
                    Tag = a.Tag ?? "Unknown",
                    TagKey = a.TagKey,
                    Thumb = a.Thumb,
                    Role = a.Role,
                    Filter = $"actor={a.TagKey ?? a.Tag}"
                });
            }
        }

        if (rec.UltraBlur is not null)
        {
            video.UltraBlurColors = new XmlUltraBlurColors
            {
                TopLeft = rec.UltraBlur.TopLeft ?? "1c1c1c",
                TopRight = rec.UltraBlur.TopRight ?? "1c1c1c",
                BottomLeft = rec.UltraBlur.BottomLeft ?? "0d0d0d",
                BottomRight = rec.UltraBlur.BottomRight ?? "0d0d0d",
            };
        }

        if (rec.CommonSense is not null)
        {
            video.CommonSenseMedia.Clear();
            var cs = rec.CommonSense;
            var age = cs.AgeRatings is not null && cs.AgeRatings.Count > 0 ? cs.AgeRatings[0] : null;

            video.CommonSenseMedia.Add(new XmlCommonSenseMedia
            {
                Id = (item.Id * 7 + 1).ToString(),
                OneLiner = cs.OneLiner ?? "Unknown",
                AgeRatings = { new XmlAgeRating { Type = age?.Type ?? "official", Rating = age?.Rating ?? 0, Age = age?.Age ?? 0 } }
            });
        }

        if (rec.Reviews is not null)
        {
            video.Reviews.Clear();
            var idx = 0;

            foreach (var r in rec.Reviews)
            {
                video.Reviews.Add(new XmlTag
                {
                    Id = (item.Id * 100 + idx++).ToString(),
                    Tag = r.Source ?? "Unknown",
                    Filter = $"review={item.Id * 100 + idx}"
                });
            }
        }
    }

    private static string generateId(int seed,
                                     string kind,
                                     string? tag)
    {
        // SHA-256 over the stable inputs; string.GetHashCode() is randomised per process, so tag
        // ids used to change on every restart and churn client caches.
        var input = string.Create(CultureInfo.InvariantCulture, $"{seed}|{kind}|{tag}");
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToUInt32(hash, 0);

        return (value % 1_000_000).ToString(CultureInfo.InvariantCulture);
    }
}
