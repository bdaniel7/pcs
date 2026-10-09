using System.Globalization;
using System.Text.Json;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Talks to plex.tv: anonymous library search, detail/children walks, and the static parsers that
/// turn those JSON payloads into <see cref="SidecarItem"/> records. Records it resolves are handed
/// back to the caller to persist through the <see cref="SidecarStore"/>.
/// </summary>
internal sealed class PlexTvClient
{
    private const string DISCOVER_BASE = "https://discover.provider.plex.tv";

    private const string SEARCH_URL_FORMAT =
        "https://discover.provider.plex.tv/library/search?query={0}&type=1&limit=10&searchProviders=discover&searchTypes=movies";

    // Show search: searchTypes=tv is the only variant plex.tv accepts (shows/show and a missing
    // type all answer 400), and its results nest exactly like the movie search one.
    private const string TV_SEARCH_URL_FORMAT =
        "https://discover.provider.plex.tv/library/search?query={0}&type=2&limit=10&searchProviders=discover&searchTypes=tv";

    private const string DETAIL_URL_FORMAT = "https://discover.provider.plex.tv/library/metadata/{0}";
    private const string CHILDREN_URL_FORMAT = "https://discover.provider.plex.tv/library/metadata/{0}/children";

    // A single shared client is safe: HttpClient is immutable after construction. The DI path
    // supplies the app's configured client; tests fall back to this default.
    private static readonly HttpClient defaultHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly SidecarStore store;
    private readonly HttpClient http;
    private string? plexToken;

    internal PlexTvClient(SidecarStore store, HttpClient? http = null)
    {
        this.store = store;
        this.http = http ?? defaultHttp;
    }

    /// <summary>
    /// Captures the auth token the client sends (only on /identity). Full plex.tv metadata lookups
    /// need it; the search endpoint works anonymously, so a missing token still yields the guid.
    /// </summary>
    internal void CaptureToken(string? token)
    {
        if (!string.IsNullOrEmpty(token)) plexToken = token;
    }

    internal bool TokenKnown => plexToken is not null;

    /// <summary>
    /// Resolves an item's real plex.tv guid (and, when the client's token is known, full metadata)
    /// for items the sidecar has no record of. The LG client resolves the guid it receives against
    /// plex.tv and cannot open the detail page without one, so this runs synchronously once per
    /// item and is persisted to plex-lookup-cache.json. Movie libraries search + enrich from the
    /// movie search endpoint; TV libraries walk show search -> season children -> episode list.
    /// </summary>
    internal async Task<SidecarItem?> LookupOnlineAsync(MediaItem item,
                                                        CancellationToken ct)
    {
        try
        {
            var rec = await FetchRecordAsync(item, ct).ConfigureAwait(false);

            if (rec is null) return null;
            var key = MetadataMatcher.GetKey(item);
            if (!string.IsNullOrEmpty(key)) store.PersistLookup(key, rec);

            return rec;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads a record's detail response into the record itself (movies that predate artwork
    /// capture). Returns false when there is nothing to fetch or the fetch fails.
    /// </summary>
    internal async Task<bool> TryRefetchDetailAsync(SidecarItem rec,
                                                    CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rec.RatingKey)) return false;

        rec.DetailChecked = true;

        try
        {
            var detailJson = await fetchAsync(string.Format(DETAIL_URL_FORMAT, rec.RatingKey), true, ct)
                .ConfigureAwait(false);

            if (detailJson is null) return false;

            EnrichFromDetail(rec, detailJson);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Search + detail fetch without touching any store; shared by the runtime lookup and the
    /// backfill command. Episodes take the plex.tv show-children path instead of the movie one.
    /// </summary>
    internal async Task<SidecarItem?> FetchRecordAsync(MediaItem item,
                                                       CancellationToken ct)
    {
        if (item.Library is { Type: LibraryType.Show }) return await FetchEpisodeRecordAsync(item, ct).ConfigureAwait(false);
        if (item.Library is not { Type: LibraryType.Movie }) return null;
        var title = CleanSearchTitle(item.FilePath);

        if (string.IsNullOrWhiteSpace(title)) return null;

        var searchJson = await fetchAsync(string.Format(SEARCH_URL_FORMAT, Uri.EscapeDataString(title)), false, ct)
            .ConfigureAwait(false);

        if (searchJson is null) return null;

        var rec = PickCandidate(searchJson, title, item.Year);

        if (rec?.Guid is null) return null;

        if (!string.IsNullOrEmpty(rec.RatingKey) && plexToken is not null)
        {
            rec.DetailChecked = true;

            try
            {
                var detailJson = await fetchAsync(string.Format(DETAIL_URL_FORMAT, rec.RatingKey), true, ct)
                    .ConfigureAwait(false);
                if (detailJson is not null) EnrichFromDetail(rec, detailJson);
            }
            catch
            {
                // The guid-only record still fixes the page.
            }
        }

        return rec;
    }

    /// <summary>
    /// Full episode record from plex.tv: parse the filename for show/season/episode, find the show
    /// via search, then walk show -> season -> episode until the episode's own payload turns up
    /// (title, summary, credits, hierarchy). Show-level genres/studio are merged from the show
    /// detail because the episode payload does not carry them. Everything here works anonymously.
    ///
    /// Show picks must not be left to chance when two series share a name: an exact-title search
    /// for "Dark Matter" returns both the 2015 and the 2024 series, and titleless filenames
    /// ("dark.matter.s02e01...") lose the tie to the wrong one. Resolution order: a year in the
    /// filename picks by year; otherwise an existing binding answers outright; otherwise a
    /// filename episode title is verified against each candidate's actual episode; only when none
    /// of those signals exist does the plain exact-title pick apply - and that guess never binds.
    /// </summary>
    internal async Task<SidecarItem?> FetchEpisodeRecordAsync(MediaItem item,
                                                              CancellationToken ct)
    {
        var parsed = TvEpisodeName.Parse(item.FilePath);

        if (parsed is null) return null;

        var showNameHasYear = MetadataMatcher.ContainsYearToken(parsed.ShowName);
        var bindingKey = MetadataMatcher.ShowBindingKey(parsed.ShowName);

        if (!showNameHasYear && store.GetShowBinding(bindingKey) is { RatingKey: { Length: > 0 } } bound)
        {
            var viaBinding = await walkEpisodeShowAsync(parsed, bound, ct).ConfigureAwait(false);

            if (viaBinding is not null) return viaBinding;

            // A binding whose show no longer has the season is stale: fall through to a fresh search.
        }

        var searchJson = await fetchAsync(string.Format(TV_SEARCH_URL_FORMAT, Uri.EscapeDataString(parsed.ShowName)), false, ct)
            .ConfigureAwait(false);

        if (searchJson is null) return null;

        var show = PickShow(searchJson, parsed.ShowName);

        if (show?.RatingKey is null) return null;

        var confident = false;

        if (showNameHasYear)
        {
            var byYear = PickShowByYear(GetShowCandidates(searchJson, parsed.ShowName), parsed.ShowName);

            if (byYear?.RatingKey is not null)
            {
                show = byYear;
                confident = true;
            }
        }
        else if (parsed.EpisodeTitle.Length > 0)
        {
            var byTitle = await VerifyByEpisodeTitleAsync(
                                                          GetShowCandidates(searchJson, parsed.ShowName), parsed,
                                                          (url, token) => fetchAsync(url, true, token), ct)
                .ConfigureAwait(false);

            if (byTitle?.RatingKey is not null)
            {
                show = byTitle;
                confident = true;
            }
        }

        if (confident) store.SaveShowBinding(bindingKey, show);

        return await walkEpisodeShowAsync(parsed, show, ct).ConfigureAwait(false);
    }

    /// <summary>Show -> season -> episode walk that turns a show pick into the episode record.</summary>
    private async Task<SidecarItem?> walkEpisodeShowAsync(ParsedEpisodeName parsed,
                                                          SidecarItem show,
                                                          CancellationToken ct)
    {
        var seasonsJson = await fetchAsync(string.Format(CHILDREN_URL_FORMAT, show.RatingKey), true, ct)
            .ConfigureAwait(false);

        if (seasonsJson is null) return null;
        var seasonKey = PickSeasonKey(seasonsJson, parsed.Season);

        if (seasonKey is null) return null;

        var episodesJson = await fetchAsync(DISCOVER_BASE + seasonKey, true, ct).ConfigureAwait(false);

        if (episodesJson is null) return null;
        var rec = PickEpisodeRecord(episodesJson, parsed.Episode, parsed);

        if (rec is null) return null;

        var showJson = await fetchAsync(string.Format(DETAIL_URL_FORMAT, show.RatingKey), false, ct)
            .ConfigureAwait(false);
        if (showJson is not null) MergeShowDetail(rec, showJson);

        // The episode payload already is the detail: nothing left for the deferred enrich step.
        rec.DetailChecked = true;

        return rec;
    }

    /// <summary>
    /// Picks the show from the type=2 search results: exact normalised title (or slug) first, else
    /// the clearly-leading result above the 0.35 relevance floor. No year gate - filenames rarely
    /// carry one, and the season/episode walk right after verifies the pick anyway.
    /// </summary>
    internal static SidecarItem? PickShow(string searchJson,
                                          string showName)
    {
        using var doc = JsonDocument.Parse(searchJson);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return null;
        if (!mc.TryGetProperty("SearchResults", out var results)) return null;

        var candidates = flattenCandidates(results, "show");

        if (candidates.Count == 0) return null;

        var target = MetadataMatcher.NormalizeTitle(showName);
        JsonElement best = default;
        var haveBest = false;

        foreach (var (md, _) in candidates)
        {
            var t = getString(md, "title");
            var slug = getString(md, "slug");
            var tNorm = t is null ? null : MetadataMatcher.NormalizeTitle(t);
            var slugNorm = slug is null ? null : MetadataMatcher.NormalizeTitle(slug);

            if (tNorm == target || slugNorm == target)
            {
                best = md;
                haveBest = true;

                break;
            }
        }

        if (!haveBest && candidates[0].Score is >= 0.35 &&
            (candidates.Count == 1 || candidates[0].Score > candidates[1].Score))
        {
            best = candidates[0].Md;
            haveBest = true;
        }

        if (!haveBest) return null;

        var rec = new SidecarItem
        {
            Guid = getString(best, "guid"),
            Title = getString(best, "title"),
            RatingKey = getString(best, "ratingKey")
        };

        return rec.Guid is null ? null : rec;
    }

    /// <summary>
    /// Every search result that could plausibly be this show, in relevance order: exact normalised
    /// title/slug matches plus names that differ only by a year suffix ("Dark Matter (2024)"
    /// against "Dark Matter"). The set feeds the year and episode-title disambiguation, which
    /// needs the rivals that plain PickShow would never return.
    /// </summary>
    internal static List<SidecarItem> GetShowCandidates(string searchJson,
                                                        string showName)
    {
        var list = new List<SidecarItem>();
        var target = MetadataMatcher.NormalizeTitle(showName);

        if (target.Length == 0) return list;

        using var doc = JsonDocument.Parse(searchJson);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return list;
        if (!mc.TryGetProperty("SearchResults", out var results)) return list;

        foreach (var (md, _) in flattenCandidates(results, "show"))
        {
            var guid = getString(md, "guid");

            if (guid is null) continue;
            var t = MetadataMatcher.NormalizeTitle(getString(md, "title") ?? "");
            var slug = MetadataMatcher.NormalizeTitle(getString(md, "slug") ?? "");

            var matched =
                (t.Length > 0 && (t == target || t.Contains(target) || target.Contains(t))) ||
                (slug.Length > 0 && (slug == target || slug.Contains(target) || target.Contains(slug)));

            if (matched)
            {
                list.Add(new SidecarItem
                {
                    Guid = guid,
                    Title = getString(md, "title"),
                    RatingKey = getString(md, "ratingKey")
                });
            }
        }

        return list;
    }

    /// <summary>
    /// The single candidate whose title carries the year from the filename ("Dark Matter 2024" for
    /// a "dark.matter.2024.s02e06" file). Ambiguity (several, or none) answers null: the caller
    /// keeps the plain pick and records no binding.
    /// </summary>
    internal static SidecarItem? PickShowByYear(IReadOnlyList<SidecarItem> candidates,
                                                string showName)
    {
        var year = System.Text.RegularExpressions.Regex.Match(showName, @"(?:19|20)\d{2}");

        if (!year.Success) return null;

        SidecarItem? match = null;

        foreach (var c in candidates)
        {
            if (!(c.Title ?? "").Contains(year.Value)) continue;

            if (match is not null) return null;
            match = c;
        }

        return match;
    }

    /// <summary>Title of episode N in a season's episode list, or null when the number is absent.</summary>
    internal static string? PickEpisodeTitle(string episodesJson,
                                             int episode)
    {
        using var doc = JsonDocument.Parse(episodesJson);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return null;
        if (!mc.TryGetProperty("Metadata", out var arr)) return null;

        foreach (var m in asArray(arr))
            if (getInt(m, "index") == episode)
                return getString(m, "title");

        return null;
    }

    /// <summary>
    /// True when the filename's episode title and plex.tv's name the same episode: equal after
    /// normalisation, or one containing the other once both are long enough to be distinctive -
    /// a short fragment like "The" must not match every title it is a prefix of.
    /// </summary>
    internal static bool EpisodeTitleMatches(string parsedTitle,
                                             string actualTitle)
    {
        var p = MetadataMatcher.NormalizeTitle(parsedTitle);
        var a = MetadataMatcher.NormalizeTitle(actualTitle);

        if (p.Length == 0 || a.Length == 0) return false;
        if (p == a) return true;

        return p.Length >= 6 && a.Length >= 6 && (a.Contains(p) || p.Contains(a));
    }

    /// <summary>
    /// Walks each candidate show to the parsed season/episode and keeps the one whose real episode
    /// title matches the filename. Null when nothing matches or several do - the caller then falls
    /// back to the plain exact-title pick and records no binding, because choosing would be a guess.
    /// </summary>
    internal static async Task<SidecarItem?> VerifyByEpisodeTitleAsync(
        IReadOnlyList<SidecarItem> candidates,
        ParsedEpisodeName parsed,
        Func<string, CancellationToken, Task<string?>> fetch,
        CancellationToken ct)
    {
        SidecarItem? match = null;

        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c.RatingKey)) continue;
            string? title;

            try
            {
                var seasonsJson = await fetch(string.Format(CHILDREN_URL_FORMAT, c.RatingKey), ct).ConfigureAwait(false);
                var seasonKey = seasonsJson is null ? null : PickSeasonKey(seasonsJson, parsed.Season);
                var episodesJson = seasonKey is null
                                       ? null
                                       : await fetch(DISCOVER_BASE + seasonKey, ct).ConfigureAwait(false);
                title = episodesJson is null ? null : PickEpisodeTitle(episodesJson, parsed.Episode);
            }
            catch
            {
                continue;
            }

            if (title is null || !EpisodeTitleMatches(parsed.EpisodeTitle, title)) continue;

            if (match is not null && match.RatingKey != c.RatingKey) return null;
            match = c;
        }

        return match;
    }

    /// <summary>Season children key ("/library/metadata/{rk}/children") for the given season number.</summary>
    internal static string? PickSeasonKey(string seasonsJson,
                                          int season)
    {
        using var doc = JsonDocument.Parse(seasonsJson);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return null;
        if (!mc.TryGetProperty("Metadata", out var arr)) return null;

        foreach (var m in asArray(arr))
            if (getInt(m, "index") == season)
                return getString(m, "key");

        return null;
    }

    /// <summary>
    /// Builds the episode record from the season's episode list: everything the info screen shows
    /// comes from this payload. A filename without an episode title falls back to the parsed one
    /// and finally to "Episode N" - never the raw file stem.
    /// </summary>
    internal static SidecarItem? PickEpisodeRecord(string episodesJson,
                                                   int episodeNumber,
                                                   ParsedEpisodeName parsed)
    {
        using var doc = JsonDocument.Parse(episodesJson);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return null;
        if (!mc.TryGetProperty("Metadata", out var arr)) return null;
        JsonElement md = default;

        foreach (var m in asArray(arr))
            if (getInt(m, "index") == episodeNumber)
            {
                md = m;

                break;
            }

        if (md.ValueKind == JsonValueKind.Undefined) return null;

        var guid = getString(md, "guid");

        if (string.IsNullOrEmpty(guid)) return null;

        var title = getString(md, "title");
        if (string.IsNullOrEmpty(title)) title = parsed.EpisodeTitle;
        if (string.IsNullOrEmpty(title)) title = $"Episode {episodeNumber}";

        var rec = new SidecarItem
        {
            Guid = guid,
            RatingKey = getString(md, "ratingKey"),
            Title = title,
            TitleSort = title,
            Year = getInt(md, "year")?.ToString(CultureInfo.InvariantCulture),
            Summary = getString(md, "summary"),
            ContentRating = getString(md, "contentRating"),
            OriginallyAvailableAt = getString(md, "originallyAvailableAt"),
            AudienceRating = getDouble(md, "audienceRating"),
            Index = (getInt(md, "index") ?? episodeNumber).ToString(CultureInfo.InvariantCulture),
            ParentIndex = getInt(md, "parentIndex")?.ToString(CultureInfo.InvariantCulture),
            ParentTitle = getString(md, "parentTitle"),
            ParentKey = getString(md, "parentKey"),
            ParentRatingKey = getString(md, "parentRatingKey"),
            ParentGuid = getString(md, "parentGuid"),
            GrandparentTitle = getString(md, "grandparentTitle"),
            GrandparentKey = getString(md, "grandparentKey"),
            GrandparentRatingKey = getString(md, "grandparentRatingKey"),
            GrandparentGuid = getString(md, "grandparentGuid"),
            ParentThumbUrl = getString(md, "parentThumb"),
            GrandparentThumbUrl = getString(md, "grandparentThumb")
        };
        if (md.TryGetProperty("Rating", out var ratings)) rec.Ratings = mapRatings(ratings);
        if (md.TryGetProperty("Role", out var roles)) rec.Roles = mapPeople(roles);
        if (md.TryGetProperty("Director", out var dirs)) rec.Directors = mapPeople(dirs);
        if (md.TryGetProperty("Writer", out var ws)) rec.Writers = mapPeople(ws);
        if (md.TryGetProperty("Producer", out var ps)) rec.Producers = mapPeople(ps);
        if (md.TryGetProperty("Guid", out var gids)) rec.Guids = mapStrings(gids, "id");

        return rec;
    }

    /// <summary>
    /// Genres, countries and studio live on the show, not on the episode payload; merge them into
    /// the episode record so the info screen shows the show's tags.
    /// </summary>
    internal static void MergeShowDetail(SidecarItem rec,
                                         string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return;
            if (!mc.TryGetProperty("Metadata", out var arr)) return;
            JsonElement md = default;

            foreach (var m in asArray(arr))
            {
                md = m;

                break;
            }

            if (md.ValueKind == JsonValueKind.Undefined) return;

            if (string.IsNullOrEmpty(rec.Studio)) rec.Studio = getString(md, "studio");
            rec.Genres ??= md.TryGetProperty("Genre", out var gs) ? mapStrings(gs, "tag") : null;
            rec.Countries ??= md.TryGetProperty("Country", out var cs) ? mapStrings(cs, "tag") : null;

            // The episode payload already carries grandparentThumb; the show detail is the fallback
            // when it did not.
            if (string.IsNullOrEmpty(rec.GrandparentThumbUrl))
            {
                var showThumb = getString(md, "thumb");
                if (!string.IsNullOrEmpty(showThumb)) rec.GrandparentThumbUrl = showThumb;
            }
        }
        catch
        {
            // The episode record is already complete enough without the show-level tags.
        }
    }

    /// <summary>
    /// tokens up to the release year (scene names put it right after the title), falling back to
    /// the first quality token when the name carries no year.
    /// </summary>
    internal static string CleanSearchTitle(string filePath)
    {
        var file = filePath;
        try { file = Path.GetFileName(file); } catch { }
        var exts = MediaTokens.FilenameExtensions;

        for (var i = 0; i < 3; i++)
        {
            var lower = file.ToLowerInvariant();
            var stripped = false;

            foreach (var e in exts)
                if (lower.EndsWith(e))
                {
                    file = Path.GetFileNameWithoutExtension(file);
                    stripped = true;

                    break;
                }

            if (!stripped) break;
        }

        var tokens = new List<string>();

        foreach (var t in System.Text.RegularExpressions.Regex.Split(file, "[^A-Za-z0-9]+"))
            if (t.Length > 0)
                tokens.Add(t);

        var yearIdx = -1;

        for (var i = 1; i < tokens.Count; i++)
            if (isYearToken(tokens[i]))
            {
                yearIdx = i;

                break;
            }

        var cut = tokens.Count;

        if (yearIdx >= 0)
        {
            cut = yearIdx;
        }
        else
        {
            for (var i = 1; i < tokens.Count; i++)
                if (MediaTokens.IsSearchQualityToken(tokens[i]))
                {
                    cut = i;

                    break;
                }
        }

        return string.Join(' ', tokens.GetRange(0, cut));
    }

    private static bool isYearToken(string t) =>
        t.Length == 4 && int.TryParse(t, out var y) && y >= 1900 && y <= 2100;

    /// <summary>
    /// Picks the anonymous-search candidate whose normalized title matches and whose year is within
    /// one of the file's year; builds the minimal record (guid/title/year/date). When no candidate
    /// matches by name - distributors translate titles, e.g. "Zwei Staatsanwalte" is listed as
    /// "Two Prosecutors" - falls back to plex.tv's top-ranked result if its year checks out and it
    /// clearly leads the rest of the results (observed correct matches score 0.38+ while unrelated
    /// noise sits at 0.30 and below).
    /// </summary>
    internal static SidecarItem? PickCandidate(string searchJson,
                                               string title,
                                               int? year)
    {
        using var doc = JsonDocument.Parse(searchJson);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return null;
        if (!mc.TryGetProperty("SearchResults", out var results)) return null;

        var candidates = flattenCandidates(results);
        var target = MetadataMatcher.NormalizeTitle(title);
        JsonElement best = default;
        var haveBest = false;
        var bestDiff = int.MaxValue;

        foreach (var (md, _) in candidates)
        {
            // Distributors rename films across regions - "And Life Goes On" is listed as
            // "Life, and Nothing More…" but keeps the slug and-life-goes-on. Match the slug too.
            var t = getString(md, "title");
            var slug = getString(md, "slug");
            var tNorm = t is null ? null : MetadataMatcher.NormalizeTitle(t);
            var slugNorm = slug is null ? null : MetadataMatcher.NormalizeTitle(slug);

            if (tNorm != target && slugNorm != target) continue;

            var candYear = getInt(md, "year");
            var diff = (year is null || candYear is null) ? -1 : Math.Abs(candYear.Value - year.Value);

            if (diff > 1) continue;

            if (diff >= 0)
            {
                if (diff < bestDiff)
                {
                    best = md;
                    haveBest = true;
                    bestDiff = diff;
                }
            }
            else if (!haveBest)
            {
                best = md;
                haveBest = true;
            }
        }

        if (!haveBest && year is not null && candidates.Count > 0)
        {
            var (topMd, topScore) = candidates[0];
            var topYear = getInt(topMd, "year");

            var clearlyFirst = topScore is >= 0.35 &&
                               (candidates.Count == 1 || topScore > candidates[1].Score);

            if (clearlyFirst && topYear is not null && Math.Abs(topYear.Value - year.Value) <= 1)
            {
                best = topMd;
                haveBest = true;
            }
        }

        if (!haveBest) return null;

        var rec = new SidecarItem
        {
            Guid = getString(best, "guid"),
            Title = getString(best, "title"),
            TitleSort = getString(best, "title"),
            Year = getInt(best, "year")?.ToString(CultureInfo.InvariantCulture),
            OriginallyAvailableAt = getString(best, "originallyAvailableAt"),
            RatingKey = getString(best, "ratingKey"),
            ThumbUrl = getString(best, "thumb"),
            ArtUrl = getString(best, "art")
        };

        return rec.Guid is null ? null : rec;
    }

    /// <summary>
    /// Maps the rich plex.tv detail response onto a record produced by <see cref="PickCandidate"/>.
    /// Any failure leaves the caller with the guid-only record.
    /// </summary>
    internal static void EnrichFromDetail(SidecarItem rec,
                                          string json)
    {
        try
        {
            enrichCore(rec, json);
        }
        catch
        {
            // Keep whatever the guid-only record already holds.
        }
    }

    private static void enrichCore(SidecarItem rec,
                                   string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return;
        if (!mc.TryGetProperty("Metadata", out var arr)) return;
        JsonElement md = default;

        foreach (var m in asArray(arr))
        {
            md = m;

            break;
        }

        if (md.ValueKind == JsonValueKind.Undefined) return;

        var guid = getString(md, "guid");
        if (!string.IsNullOrEmpty(guid)) rec.Guid = guid;
        var title = getString(md, "title");

        if (!string.IsNullOrEmpty(title))
        {
            rec.Title = title;
            rec.TitleSort = title;
        }
        var y = getInt(md, "year");
        if (y.HasValue) rec.Year = y.Value.ToString(CultureInfo.InvariantCulture);

        var s = getString(md, "summary");
        if (!string.IsNullOrEmpty(s)) rec.Summary = s;
        s = getString(md, "tagline");
        if (!string.IsNullOrEmpty(s)) rec.Tagline = s;
        s = getString(md, "studio");
        if (!string.IsNullOrEmpty(s)) rec.Studio = s;
        s = getString(md, "contentRating");
        if (!string.IsNullOrEmpty(s)) rec.ContentRating = s;
        s = getString(md, "originallyAvailableAt");
        if (!string.IsNullOrEmpty(s)) rec.OriginallyAvailableAt = s;

        if (md.TryGetProperty("audienceRating", out var ar))
            rec.AudienceRating = ar.ValueKind switch
            {
                JsonValueKind.Number => ar.GetDouble(),
                JsonValueKind.String =>
                    double.TryParse(ar.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? d
                        : null,
                _ => null
            };

        if (md.TryGetProperty("Rating", out var ratings))
            rec.Ratings = mapRatings(ratings);
        if (md.TryGetProperty("Role", out var roles)) rec.Roles = mapPeople(roles);
        if (md.TryGetProperty("Director", out var dirs)) rec.Directors = mapPeople(dirs);
        if (md.TryGetProperty("Writer", out var ws)) rec.Writers = mapPeople(ws);
        if (md.TryGetProperty("Producer", out var ps)) rec.Producers = mapPeople(ps);
        if (md.TryGetProperty("Country", out var cs)) rec.Countries = mapStrings(cs, "tag");
        if (md.TryGetProperty("Genre", out var gs)) rec.Genres = mapStrings(gs, "tag");
        if (md.TryGetProperty("Guid", out var gids)) rec.Guids = mapStrings(gids, "id");

        // Detail is authoritative over the search payload PickCandidate may have filled in.
        var thumb = getString(md, "thumb");
        if (!string.IsNullOrEmpty(thumb)) rec.ThumbUrl = thumb;
        var backdrop = getString(md, "art");
        if (!string.IsNullOrEmpty(backdrop)) rec.ArtUrl = backdrop;
    }

    private async Task<string?> fetchAsync(string url,
                                           bool withToken,
                                           CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // Without this the provider answers XML (half-serialized), which cannot be parsed.
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        var token = plexToken;

        if (withToken && !string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flattens MediaContainer.SearchResults[].SearchResult[].Metadata[] in relevance order,
    /// carrying each SearchResult's score (present in live responses, absent in test fixtures).
    /// </summary>
    private static List<(JsonElement Md, double? Score)> flattenCandidates(JsonElement searchResults,
                                                                           string allowedType = "movie")
    {
        var list = new List<(JsonElement, double?)>();

        foreach (var sr in asArray(searchResults))
        {
            if (!sr.TryGetProperty("SearchResult", out var inner)) continue;

            foreach (var res in asArray(inner))
            {
                double? score = null;

                if (res.TryGetProperty("score", out var sc))
                {
                    if (sc.ValueKind == JsonValueKind.Number) score = sc.GetDouble();
                    else if (sc.ValueKind == JsonValueKind.String &&
                             double.TryParse(sc.GetString(), NumberStyles.Float,
                                             CultureInfo.InvariantCulture, out var sv))
                        score = sv;
                }

                if (!res.TryGetProperty("Metadata", out var md)) continue;

                foreach (var m in asArray(md))
                {
                    var t = getString(m, "type");

                    if (t is not null && t != allowedType) continue;
                    list.Add((m, score));
                }
            }
        }

        return list;
    }

    private static IEnumerable<JsonElement> asArray(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array) return el.EnumerateArray();
        if (el.ValueKind == JsonValueKind.Object) return new[] { el };

        return Array.Empty<JsonElement>();
    }

    private static string? getString(JsonElement el,
                                     string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? getInt(JsonElement el,
                               string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s)) return s;

        return null;
    }

    private static double? getDouble(JsonElement el,
                                     string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();

        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;

        return null;
    }

    private static List<TagRef>? mapPeople(JsonElement arr)
    {
        var list = new List<TagRef>();

        foreach (var m in asArray(arr))
        {
            var tag = getString(m, "tag");

            if (string.IsNullOrEmpty(tag)) continue;

            list.Add(new TagRef
            {
                Tag = tag,
                TagKey = getString(m, "id"),
                Thumb = getString(m, "thumb"),
                Role = getString(m, "role")
            });
        }

        return list.Count > 0 ? list : null;
    }

    private static List<string>? mapStrings(JsonElement arr,
                                            string field)
    {
        var list = new List<string>();

        foreach (var m in asArray(arr))
        {
            var v = m.ValueKind == JsonValueKind.String ? m.GetString() : getString(m, field);
            if (!string.IsNullOrEmpty(v)) list.Add(v!);
        }

        return list.Count > 0 ? list : null;
    }

    private static List<RatingRef>? mapRatings(JsonElement arr)
    {
        var list = new List<RatingRef>();

        foreach (var m in asArray(arr))
            list.Add(new RatingRef { Image = getString(m, "image"), Type = getString(m, "type"), Value = getDouble(m, "value") });

        return list.Count > 0 ? list : null;
    }
}
