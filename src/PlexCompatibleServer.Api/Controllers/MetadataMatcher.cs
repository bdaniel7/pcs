using System.Text;
using PlexCompatibleServer.Core.Models;
using PlexCompatibleServer.Infrastructure.Media;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Resolves media items against the loaded <see cref="SidecarStore"/> using the exact-key, title
/// and fuzzy fallbacks, and owns the key/title normalization shared by matching and online lookup.
/// </summary>
internal sealed class MetadataMatcher
{
    private readonly SidecarStore store;

    internal MetadataMatcher(SidecarStore store)
    {
        this.store = store;
    }

    /// <summary>
    /// Resolves an item against the loaded stores only, with the same exact-key / title / fuzzy
    /// fallbacks the overlay uses. Null means no loaded store covers the item.
    /// </summary>
    internal SidecarItem? ResolveLocal(MediaItem item,
                                       string? title,
                                       string? titleSort)
    {
        var key = GetKey(item);

        if (string.IsNullOrEmpty(key)) return null;

        // Fuzzy matching walks every cached record by title containment, which happily binds an
        // episode filename to some unrelated movie. Episodes get exact-key lookups plus their own
        // plex.tv lookup instead.
        var isEpisode = item.Library is { Type: LibraryType.Show };

        var rec = store.Get(key);

        if (rec is null)
        {
            rec ??= store.Get(title ?? string.Empty);
            rec ??= store.Get(title?.ToLowerInvariant() ?? string.Empty);
        }

        {
            var file = item.FilePath;
            try { file = Path.GetFileName(file); } catch { }
            var baseName = file;

            for (int i = 0; i < 3; i++)
            {
                var lower = baseName.ToLowerInvariant();
                var exts = MediaTokens.FilenameExtensions;
                bool hit = false;

                foreach (var e in exts)
                {
                    if (lower.EndsWith(e))
                    {
                        hit = true;

                        break;
                    }
                }

                if (hit) baseName = Path.GetFileNameWithoutExtension(baseName);
                else break;
            }
            rec ??= store.Get(keyOfShort(baseName));
            rec ??= store.Get(keyOf(baseName));
            var loose = System.Text.RegularExpressions.Regex.Replace(baseName.ToLowerInvariant(), "[^a-z0-9]+", "");
            rec ??= store.Get(loose);

            if (!isEpisode)
            {
                rec ??= store.GetFuzzy(baseName);
                rec ??= store.GetFuzzy(item.FilePath);

                if (rec is null && !string.IsNullOrEmpty(item.FilePath))
                {
                    try
                    {
                        rec ??= store.GetFuzzy(Path.GetDirectoryName(item.FilePath) ?? string.Empty);
                        rec ??= store.GetFuzzy(Path.Combine(Path.GetDirectoryName(item.FilePath) ?? string.Empty, baseName));
                    }
                    catch { }
                }
            }
        }

        if (rec is null && !isEpisode)
        {
            rec ??= store.GetFuzzy(title ?? string.Empty);
            rec ??= store.GetFuzzy(titleSort ?? string.Empty);
        }

        return rec;
    }

    /// <summary>
    /// True when any loaded store (sidecar or lookup cache) already covers this item.
    /// </summary>
    internal bool HasRecord(MediaItem item) => TryGetRecord(item, out _);

    /// <summary>
    /// Finds the covering record (exact key first, then title/fuzzy fallbacks like Apply).
    /// The fuzzy walk is movie-only: it matches by title containment and would happily bind an
    /// episode filename to an unrelated film.
    /// </summary>
    internal bool TryGetRecord(MediaItem item,
                               out SidecarItem record)
    {
        var key = GetKey(item);

        if (!string.IsNullOrEmpty(key) && store.Get(key) is { } byKey)
        {
            record = byKey;

            return true;
        }
        var title = item.Title;

        if (title.Length > 0)
        {
            if (store.Get(title) is { } byTitle)
            {
                record = byTitle;

                return true;
            }

            if (store.Get(title.ToLowerInvariant()) is { } byLower)
            {
                record = byLower;

                return true;
            }
        }

        if (item.Library is not { Type: LibraryType.Show } && store.GetFuzzy(title) is { } fuzzy)
        {
            record = fuzzy;

            return true;
        }
        record = null!;

        return false;
    }

    internal static string GetKey(MediaItem item)
    {
        var file = item.FilePath;
        try { file = Path.GetFileName(file); } catch { }
        var baseName = file;

        // Strip known media extensions repeatedly (handles cases like .mkv, .mp4, etc.)
        for (int i = 0; i < 3; i++)
        {
            var lower = baseName.ToLowerInvariant();

            if (lower.EndsWith(".mkv") || lower.EndsWith(".mp4") || lower.EndsWith(".avi") ||
                lower.EndsWith(".m4v") || lower.EndsWith(".mov") || lower.EndsWith(".ts") ||
                lower.EndsWith(".wmv") || lower.EndsWith(".iso") || lower.EndsWith(".webm") ||
                lower.EndsWith(".flv"))
            {
                baseName = Path.GetFileNameWithoutExtension(baseName);

                continue;
            }

            break;
        }

        return keyOf(baseName);
    }

    private static string keyOf(string stem)
    {
        var s = (stem).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);

        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }

        return sb.ToString();
    }

    private static string keyOfShort(string stem)
    {
        // Work with just the filename to match sidecar keys derived from media basenames
        if (!string.IsNullOrEmpty(stem))
        {
            try
            {
                stem = Path.GetFileName(stem);
            }
            catch
            {
                // ignore
            }
        }
        var s = stem.ToLowerInvariant().Replace("&", " and ");
        s = MediaTokens.QualityTokens.Replace(s, " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9]+", " ");
        var tokens = s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var keep = new List<string>(tokens.Length);

        foreach (var t in tokens)
        {
            if (t.Length > 2) keep.Add(t);
        }

        // Find year
        foreach (var t in tokens)
        {
            if (t.Length == 4 && char.IsDigit(t[0]) && int.TryParse(t, out var y) && y >= 1900 && y <= 2100)
            {
                if (!keep.Contains(t)) keep.Add(t);
            }
        }

        return string.Concat(keep);
    }

    /// <summary>
    /// Binding key for a show name with any year removed: "Dark Matter 2024" (from a dated
    /// filename) and "Dark Matter" (from its titleless siblings) must address the same binding.
    /// </summary>
    internal static string ShowBindingKey(string showName) => keyOf(StripYearTokens(showName));

    internal static string StripYearTokens(string showName) =>
        System.Text.RegularExpressions.Regex.Replace(showName, @"\b(?:19|20)\d{2}\b", " ").Trim();

    internal static bool ContainsYearToken(string showName) =>
        System.Text.RegularExpressions.Regex.IsMatch(showName, @"(?<!\d)(?:19|20)\d{2}(?!\d)");

    internal static string NormalizeTitle(string t) =>
        System.Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant(), "[^a-z0-9]+", "");

    /// <summary>
    /// Episodes whose filename carries a year or an episode title can be disambiguated with
    /// confidence; they run first so their show binding exists before the titleless siblings of
    /// the same show are looked up. The other way round, the titleless files would settle on the
    /// plain exact-title pick first - and the wrong series wins that tie ("Dark Matter" 2015 vs
    /// the 2024 one). Movies and unparsable names keep their relative order at the end.
    /// </summary>
    internal static List<MediaItem> OrderLookupPasses(IReadOnlyList<MediaItem> items)
    {
        var confident = new List<MediaItem>();
        var rest = new List<MediaItem>();

        foreach (var item in items)
        {
            if (item.Library is { Type: LibraryType.Show } &&
                TvEpisodeName.Parse(item.FilePath) is { } parsed &&
                (parsed.EpisodeTitle.Length > 0 || ContainsYearToken(parsed.ShowName)))
            {
                confident.Add(item);
            }
            else
            {
                rest.Add(item);
            }
        }
        confident.AddRange(rest);

        return confident;
    }
}
