using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Resolves media items against the loaded <see cref="SidecarStore"/> using the exact-key, title
/// and fuzzy fallbacks, and owns the key/title normalization shared by matching and online lookup.
/// </summary>
internal sealed class MetadataMatcher
{
    private readonly SidecarStore _store;

    internal MetadataMatcher(SidecarStore store)
    {
        _store = store;
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

        var rec = _store.Get(key);

        if (rec is null)
        {
            rec ??= _store.Get(title ?? string.Empty);
            rec ??= _store.Get(title?.ToLowerInvariant() ?? string.Empty);
        }

        {
            var file = item.FilePath ?? string.Empty;
            try { file = Path.GetFileName(file); } catch { }
            var baseName = file;

            for (int i = 0; i < 3; i++)
            {
                var lower = baseName.ToLowerInvariant();
                var exts = new[] { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".ts", ".wmv", ".iso", ".webm", ".flv" };
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
            rec ??= _store.Get(KeyOfShort(baseName));
            rec ??= _store.Get(KeyOf(baseName));
            var loose = System.Text.RegularExpressions.Regex.Replace(baseName.ToLowerInvariant(), "[^a-z0-9]+", "");
            rec ??= _store.Get(loose);

            if (!isEpisode)
            {
                rec ??= _store.GetFuzzy(baseName);
                rec ??= _store.GetFuzzy(item.FilePath ?? string.Empty);

                if (rec is null && !string.IsNullOrEmpty(item.FilePath))
                {
                    try
                    {
                        rec ??= _store.GetFuzzy(Path.GetDirectoryName(item.FilePath) ?? string.Empty);
                        rec ??= _store.GetFuzzy(Path.Combine(Path.GetDirectoryName(item.FilePath) ?? string.Empty, baseName));
                    }
                    catch { }
                }
            }
        }

        if (rec is null && !isEpisode)
        {
            rec ??= _store.GetFuzzy(title ?? string.Empty);
            rec ??= _store.GetFuzzy(titleSort ?? string.Empty);
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

        if (!string.IsNullOrEmpty(key) && _store.Get(key) is { } byKey)
        {
            record = byKey;

            return true;
        }
        var title = item.Title ?? string.Empty;

        if (title.Length > 0)
        {
            if (_store.Get(title) is { } byTitle)
            {
                record = byTitle;

                return true;
            }

            if (_store.Get(title.ToLowerInvariant()) is { } byLower)
            {
                record = byLower;

                return true;
            }
        }

        if (item.Library is not { Type: LibraryType.Show } && _store.GetFuzzy(title) is { } fuzzy)
        {
            record = fuzzy;

            return true;
        }
        record = null!;

        return false;
    }

    internal static string GetKey(MediaItem item)
    {
        var file = item.FilePath ?? string.Empty;
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

        return KeyOf(baseName);
    }

    private static string KeyOf(string stem)
    {
        var s = (stem ?? string.Empty).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);

        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }

        return sb.ToString();
    }

    private static string KeyOfShort(string stem)
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
        var s = (stem ?? string.Empty).ToLowerInvariant().Replace("&", " and ");
        var pattern = @"\b(1080p|2160p|720p|480p|webrip|web|web-dl|webdl|bluray|brrip|bdremux|x264|x265|h264|h265|hevc|aac\d?(?:\.\d)?|ac3|eac3|dts(-hd)?|dd\+?|ddp?|atmos|truehd|hdr10\+?|hdr|sdr|remux|proper|repack|internal|limited|extended|unrated|multi|dual|subbed|dubbed|imax|10bit|8bit|yify|yts|retail|dksubs|gg|bz|lt)\b";
        s = System.Text.RegularExpressions.Regex.Replace(s, pattern, " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
    internal static string ShowBindingKey(string showName) => KeyOf(StripYearTokens(showName));

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
                TvEpisodeName.Parse(item.FilePath ?? string.Empty) is { } parsed &&
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
