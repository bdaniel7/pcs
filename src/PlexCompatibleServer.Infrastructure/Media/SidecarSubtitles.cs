using System.Collections.Concurrent;

namespace PlexCompatibleServer.Infrastructure.Media;

/// <summary>An external .srt file that belongs to a video file.</summary>
/// <param name="FilePath">Full path of the subtitle file.</param>
/// <param name="LanguageCode">ISO 639-2 code, e.g. "eng".</param>
/// <param name="LanguageTag">ISO 639-1 tag, e.g. "en".</param>
/// <param name="Language">Display name, e.g. "English".</param>
/// <param name="Forced">True when the file name marks the track as forced.</param>
/// <param name="Caption">True when the file name marks the track as SDH/CC.</param>
public sealed record SidecarSubtitle(
    string FilePath,
    string LanguageCode,
    string LanguageTag,
    string Language,
    bool Forced,
    bool Caption);

/// <summary>
/// Finds the subtitle files that belong to a video. Sidecars are named after the video with the
/// language either bare ("Movie.srt", "Movie.mp4.srt") or as a tag ("Movie.en.srt",
/// "Movie.eng.srt", "Movie.en.forced.srt"). Matching only the bare forms leaves a library that
/// follows the tagged convention - the overwhelmingly common one - with no subtitle track at all.
/// </summary>
public static class SidecarSubtitles
{
    /// <summary>Untagged sidecars carry no language; report them the way the client expects.</summary>
    private static readonly LanguageCode DefaultLanguage = new("en", "eng", "English");

    private static readonly string[] Qualifiers =
        ["forced", "sdh", "cc", "hi", "deaf", "default"];

    /// <summary>
    /// The directory listing is cached because a list request calls <see cref="Find"/> once per
    /// item: re-enumerating for every row would turn a library page into a filesystem walk. The
    /// cache expires when the directory itself changes or after a short TTL, so a sidecar dropped
    /// in while the server runs still shows up without a rescan.
    /// </summary>
    private static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, Listing> Listings = new(StringComparer.Ordinal);

    private sealed record Listing(long Stamp, DateTime LoadedAt, string[] Files);

    /// <summary>Returns every sidecar for the video, plain names first and forced tracks last.</summary>
    public static IReadOnlyList<SidecarSubtitle> Find(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        var fileName = Path.GetFileName(videoPath);
        if (string.IsNullOrEmpty(directory) || stem.Length == 0) return [];

        var matches = new List<(int Order, string Name, SidecarSubtitle Sub)>();

        foreach (var file in ListDirectory(directory))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) continue;

            if (name.Equals(stem + ".srt", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(fileName + ".srt", StringComparison.OrdinalIgnoreCase))
            {
                matches.Add((0, name, new SidecarSubtitle(file,
                    DefaultLanguage.Three, DefaultLanguage.Two, DefaultLanguage.Name, false, false)));
                continue;
            }

            if (ParseTagged(name, stem, fileName, file) is { } tagged)
                matches.Add((1, name, tagged));
        }

        return matches
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Sub.Forced)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Sub)
            .ToList();
    }

    private static SidecarSubtitle? ParseTagged(string name, string stem, string fileName, string path)
    {
        // The container-qualified prefix is tried first: "Movie.mp4.en.srt" has to be read as
        // "en" against "Movie.mp4", where reading it against the stem would yield "mp4.en".
        foreach (var prefix in new[] { fileName, stem })
        {
            var rest = Slice(name, prefix);
            if (rest is null) continue;
            if (ParseTokens(rest, path) is { } parsed) return parsed;
        }

        return null;
    }

    private static string? Slice(string name, string prefix)
    {
        prefix += ".";
        if (name.Length <= prefix.Length) return null;
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : null;
    }

    private static SidecarSubtitle? ParseTokens(string rest, string path)
    {
        if (!rest.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) return null;

        var tokens = rest[..^".srt".Length].Split('.');
        if (tokens.Length is 0 or > 3 || tokens.Any(t => t.Length == 0)) return null;

        LanguageCode? language = null;
        var start = 0;

        if (!Qualifiers.Contains(tokens[0], StringComparer.OrdinalIgnoreCase))
        {
            language = ResolveLanguage(tokens[0]);
            if (language is null) return null;
            start = 1;
        }

        var forced = false;
        var caption = false;

        for (var i = start; i < tokens.Length; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "forced":
                    forced = true;
                    break;
                case "sdh":
                case "cc":
                case "hi":
                case "deaf":
                    caption = true;
                    break;
                case "default":
                    break;
                default:
                    return null;
            }
        }

        language ??= DefaultLanguage;
        return new SidecarSubtitle(path, language.Three, language.Two, language.Name, forced, caption);
    }

    private static LanguageCode? ResolveLanguage(string token)
    {
        // Regional spellings such as "pt-BR" or "en-US" keep the region in the tag, the way the
        // client reports it back, but the base language supplies the code and the display name.
        var tag = token;
        var region = "";
        var dash = token.IndexOf('-');
        if (dash > 0)
        {
            tag = token[..dash];
            region = token[(dash + 1)..];
            if (region.Length is not (2 or 3) || !region.All(char.IsAsciiLetter)) return null;
            region = region.ToUpperInvariant();
        }

        if (LanguageLookup.TryGetValue(tag, out var known))
        {
            if (region.Length == 0) return known;
            return new LanguageCode($"{known.Two}-{region}", known.Three,
                $"{known.Name} ({region})");
        }

        // An ISO-looking code we do not have a display name for is passed through unchanged:
        // the client shows the code, which is still more useful than dropping the track.
        if (tag.Length is 2 or 3 && tag.All(char.IsAsciiLetter))
        {
            var code = tag.ToLowerInvariant();
            return new LanguageCode(region.Length > 0 ? $"{code}-{region}" : code, code,
                region.Length > 0 ? $"{tag.ToUpperInvariant()}-{region}" : tag.ToUpperInvariant());
        }

        return null;
    }

    /// <summary>
    /// The name clients render in the subtitle menu. Container tags often carry a bare code
    /// ("eng"), which menu would show as-is; the language table turns it back into "English".
    /// A real name ("English", "Français canadien") is passed through untouched.
    /// </summary>
    public static string DisplayName(string? language, string? languageCode)
    {
        var candidate = string.IsNullOrWhiteSpace(language) ? languageCode : language;
        if (string.IsNullOrWhiteSpace(candidate)) return "";

        var trimmed = candidate.Trim();
        if (trimmed.Length is 2 or 3 && trimmed.All(char.IsAsciiLetter) &&
            LanguageLookup.TryGetValue(trimmed, out var known))
        {
            return known.Name;
        }

        return trimmed;
    }

    /// <summary>
    /// The two-letter tag Plex reports as languageTag. Probes hand back the three-letter code,
    /// so "eng" has to become "en" for the client's language grouping to behave.
    /// </summary>
    public static string LanguageTag(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return "";

        var trimmed = languageCode.Trim();
        if (LanguageLookup.TryGetValue(trimmed, out var known)) return known.Two;
        if (trimmed.Length is 2 or 3 && trimmed.All(char.IsAsciiLetter)) return trimmed.ToLowerInvariant();
        return trimmed;
    }

    private static string[] ListDirectory(string directory)
    {
        DateTime loadedAt;
        try
        {
            loadedAt = DateTime.UtcNow;
            var stamp = Directory.GetLastWriteTimeUtc(directory).Ticks;

            if (Listings.TryGetValue(directory, out var cached) &&
                cached.Stamp == stamp &&
                loadedAt - cached.LoadedAt < ListingTtl)
            {
                return cached.Files;
            }

            var files = Directory.GetFiles(directory);
            Listings[directory] = new Listing(stamp, loadedAt, files);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A listing that cannot be read is better served from cache than as no subtitles.
            return Listings.TryGetValue(directory, out var stale) ? stale.Files : [];
        }
    }

    private sealed record LanguageCode(string Two, string Three, string Name);

    // ISO 639-1 / 639-2 pairs plus the display name clients render in the subtitle menu.
    // The 639-2/B spellings (fre/ger/dut/chi/...) are the ones Plex reports as languageCode.
    private static readonly (string Two, string Three, string Name)[] KnownLanguages =
    [
        ("en", "eng", "English"), ("es", "spa", "Spanish"), ("fr", "fre", "French"),
        ("de", "ger", "German"), ("it", "ita", "Italian"), ("pt", "por", "Portuguese"),
        ("nl", "dut", "Dutch"), ("ru", "rus", "Russian"), ("ja", "jpn", "Japanese"),
        ("zh", "chi", "Chinese"), ("ko", "kor", "Korean"), ("ar", "ara", "Arabic"),
        ("hi", "hin", "Hindi"), ("tr", "tur", "Turkish"), ("sv", "swe", "Swedish"),
        ("no", "nor", "Norwegian"), ("da", "dan", "Danish"), ("fi", "fin", "Finnish"),
        ("pl", "pol", "Polish"), ("cs", "cze", "Czech"), ("hu", "hun", "Hungarian"),
        ("ro", "rum", "Romanian"), ("bg", "bul", "Bulgarian"), ("el", "gre", "Greek"),
        ("he", "heb", "Hebrew"), ("th", "tha", "Thai"), ("vi", "vie", "Vietnamese"),
        ("id", "ind", "Indonesian"), ("ms", "msa", "Malay"), ("uk", "ukr", "Ukrainian"),
        ("fa", "per", "Persian"), ("ta", "tam", "Tamil"), ("te", "tel", "Telugu"),
        ("mr", "mar", "Marathi"), ("bn", "ben", "Bengali"), ("pa", "pan", "Punjabi"),
        ("ca", "cat", "Catalan"), ("eu", "baq", "Basque"), ("gl", "gal", "Galician"),
        ("sk", "slo", "Slovak"), ("sl", "slv", "Slovenian"), ("hr", "hrv", "Croatian"),
        ("sr", "srp", "Serbian"), ("lt", "lit", "Lithuanian"), ("lv", "lav", "Latvian"),
        ("et", "est", "Estonian"), ("is", "ice", "Icelandic"), ("ga", "gle", "Irish"),
        ("cy", "wel", "Welsh"), ("sq", "alb", "Albanian"), ("mk", "mac", "Macedonian"),
        ("kk", "kaz", "Kazakh"), ("uz", "uzb", "Uzbek"), ("be", "bel", "Belarusian"),
        ("ka", "geo", "Georgian"), ("hy", "arm", "Armenian"), ("my", "bur", "Burmese"),
        ("km", "khm", "Khmer"), ("lo", "lao", "Lao"), ("mn", "mon", "Mongolian"),
        ("ne", "nep", "Nepali"), ("si", "sin", "Sinhala"), ("sw", "swa", "Swahili"),
        ("af", "afr", "Afrikaans"), ("az", "aze", "Azerbaijani"), ("bs", "bos", "Bosnian"),
        ("fo", "fao", "Faroese"), ("or", "ori", "Oriya"),
        ("ml", "mal", "Malayalam"), ("kn", "kan", "Kannada"), ("gu", "guj", "Gujarati"),
        ("ur", "urd", "Urdu"), ("yi", "yid", "Yiddish")
    ];

    private static readonly Dictionary<string, LanguageCode> LanguageLookup = BuildLanguageLookup();

    private static Dictionary<string, LanguageCode> BuildLanguageLookup()
    {
        var lookup = new Dictionary<string, LanguageCode>(StringComparer.OrdinalIgnoreCase);

        foreach (var (two, three, name) in KnownLanguages)
        {
            var entry = new LanguageCode(two, three, name);
            lookup[two] = entry;
            lookup[three] = entry;
            lookup[name] = entry;
        }

        // Regional and platform spellings that show up in real file names.
        lookup["chs"] = lookup["zh"];
        lookup["cht"] = lookup["zh"];
        lookup["ptbr"] = lookup["pt"];

        return lookup;
    }
}
