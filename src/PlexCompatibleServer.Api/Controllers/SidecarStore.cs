using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Owns the on-disk sidecar stores (scraped metadata, offline lookup cache, show bindings) and the
/// in-memory caches derived from them. Persistence, path resolution and the precomputed fuzzy index
/// live here; matching and HTTP concerns sit in the collaborators that consume this store.
/// </summary>
internal sealed class SidecarStore
{
    const string SIDECAR_PATH = "wwwroot/plex-metadata.json";
    const string LOOKUP_CACHE_FILE_NAME = "plex-lookup-cache.json";
    const string SHOW_BINDINGS_FILE_NAME = "plex-show-bindings.json";

    readonly object cacheLock = new();

    /// <summary>
    /// Content root of the running app. Dev runs from the project directory while binaries land in
    /// bin/ - without this, a wwwroot copy under the output directory shadows the real files that
    /// static serving and tests read from the project directory.
    /// </summary>
    internal string? ContentRoot { get; }

    internal SidecarStore(string? contentRoot = null)
    {
        ContentRoot = contentRoot;
    }

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The scraped sidecar mixes value types - "year" is a JSON number while the model
        // declares string, and numeric fields arrive as quoted strings. Deserialization used to
        // throw on the first entry, which silently disabled the entire sidecar.
        Converters = {
                                                                                      new FlexibleStringConverter(),
                                                                                      new FlexibleNumberConverter<double>(),
                                                                                      new FlexibleNumberConverter<int>()
                                                                                  }
    };

    ConcurrentDictionary<string, SidecarItem>? cache;
    int cacheVersion;
    FuzzyIndex? fuzzyIndex;
    int fuzzyIndexVersion = -1;
    Dictionary<string, SidecarItem>? lookupCache;
    Dictionary<string, SidecarItem>? showBindings;

    internal SidecarItem? Get(string key)
    {
        var cache = this.cache;

        if (cache is null)
        {
            var path = resolveSidecarPath();

            var loaded = path is not null
                             ? JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(File.ReadAllText(path), Options)
                             : null;
            cache = new ConcurrentDictionary<string, SidecarItem>(loaded ?? new(), StringComparer.Ordinal);
            foreach (var kv in loadLookupCache()) cache[kv.Key] = kv.Value;
            this.cache = cache;
            cacheVersion++;
        }

        if (cache!.TryGetValue(key, out var v)) return v;

        if (!string.IsNullOrEmpty(key))
        {
            foreach (var kv in cache)
            {
                if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
        }

        return null;
    }

    internal SidecarItem? GetFuzzy(string filename)
    {
        var cache = this.cache;

        if (cache is null || string.IsNullOrEmpty(filename)) return null;

        var index = getFuzzyIndex(cache);
        var fn = filename.ToLowerInvariant();

        // Exact stem hit is the common case (the file names its own sidecar): answer with one probe
        // before touching the precomputed entry list.
        if (index.ByStem.TryGetValue(fn, out var exact)) return exact;

        var fnorm = normalizeFuzzy(fn);

        foreach (var entry in index.Entries)
        {
            if (entry.Stem.Length == 0) continue;

            if (entry.TitleNorm.Length > 0 &&
                (fnorm.Contains(entry.TitleNorm) || entry.TitleNorm.Contains(fnorm))) return entry.Record;

            if (fn.Contains(entry.Stem) || entry.Stem.Contains(fn)) return entry.Record;

            if (fn.Length > 12 && entry.Stem.Length > 12)
            {
                var tfn = fn.Length >= 20 ? fn[..20] : fn;
                var tstem = entry.Stem.Length >= 20 ? entry.Stem[..20] : entry.Stem;

                if (tfn.Contains(tstem) || tstem.Contains(tfn)) return entry.Record;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the precomputed fuzzy index for the current cache. Rebuilding it costs one pass over
    /// the sidecar; doing so per lookup would re-normalize every title on every row of a library
    /// page (an O(n^2) walk). The index is reused until the cache is loaded or a record is written.
    /// </summary>
    FuzzyIndex getFuzzyIndex(ConcurrentDictionary<string, SidecarItem> cache)
    {
        var index = fuzzyIndex;
        if (index is not null && fuzzyIndexVersion == cacheVersion && ReferenceEquals(index.Cache, cache))
            return index;

        lock (cacheLock)
        {
            index = fuzzyIndex;
            if (index is not null && fuzzyIndexVersion == cacheVersion && ReferenceEquals(index.Cache, cache))
                return index;

            index = FuzzyIndex.Build(cache);
            fuzzyIndex = index;
            fuzzyIndexVersion = cacheVersion;
            return index;
        }
    }

    static string normalizeFuzzy(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "[^a-z0-9]+", "");

    /// <summary>
    /// Per-record lookup data built once per cache revision: the lower-cased file stem (for the raw
    /// containment checks) and the stripped-alphanumeric title (so the query only pays for one
    /// normalization, not one per record).
    /// </summary>
    sealed class FuzzyIndex
    {
        FuzzyIndex(ConcurrentDictionary<string, SidecarItem> cache,
                   Dictionary<string, SidecarItem> byStem,
                   Entry[] entries)
        {
            Cache = cache;
            ByStem = byStem;
            Entries = entries;
        }

        public ConcurrentDictionary<string, SidecarItem> Cache { get; }
        public Dictionary<string, SidecarItem> ByStem { get; }
        public Entry[] Entries { get; }

        public static FuzzyIndex Build(ConcurrentDictionary<string, SidecarItem> cache)
        {
            var byStem = new Dictionary<string, SidecarItem>(StringComparer.Ordinal);
            var entries = new Entry[cache.Count];
            var i = 0;

            foreach (var pair in cache)
            {
                var stem = (pair.Value.FileStem ?? pair.Key ?? string.Empty).ToLowerInvariant();
                var titleNorm = normalizeFuzzy((pair.Value.Title ?? string.Empty).ToLowerInvariant());
                entries[i++] = new Entry(stem, titleNorm, pair.Value);

                if (stem.Length > 0 && !byStem.ContainsKey(stem)) byStem[stem] = pair.Value;
            }

            return new FuzzyIndex(cache, byStem, entries);
        }

        public readonly record struct Entry(string Stem, string TitleNorm, SidecarItem Record);
    }

    string? resolveSidecarPath()
    {
        if (ContentRoot is not null)
        {
            var rooted = Path.Combine(ContentRoot, "wwwroot", "plex-metadata.json");

            return File.Exists(rooted) ? rooted : null;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "plex-metadata.json");

        if (File.Exists(path)) return path;
        path = Path.Combine(AppContext.BaseDirectory, "..", "wwwroot", "plex-metadata.json");

        if (File.Exists(path)) return path;

        return File.Exists(SIDECAR_PATH) ? Path.GetFullPath(SIDECAR_PATH) : null;
    }

    string preferredSidecarPath() =>
        ContentRoot is not null
            ? Path.Combine(ContentRoot, "wwwroot", "plex-metadata.json")
            : Path.Combine(AppContext.BaseDirectory, "wwwroot", "plex-metadata.json");

    string lookupCachePath()
    {
        var sidecar = resolveSidecarPath();

        var dir = sidecar is not null
                      ? Path.GetDirectoryName(sidecar)!
                      : Path.GetDirectoryName(preferredSidecarPath())!;

        return Path.Combine(dir, LOOKUP_CACHE_FILE_NAME);
    }

    Dictionary<string, SidecarItem> loadLookupCache()
    {
        try
        {
            var path = lookupCachePath();

            if (!File.Exists(path)) return new Dictionary<string, SidecarItem>();

            return JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(File.ReadAllText(path), Options)
                ?? new Dictionary<string, SidecarItem>();
        }
        catch
        {
            return new Dictionary<string, SidecarItem>();
        }
    }

    internal void PersistLookup(string key,
                                SidecarItem rec)
    {
        lock (cacheLock)
        {
            lookupCache ??= loadLookupCache();
            lookupCache[key] = rec;

            try
            {
                var path = lookupCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(lookupCache, Options));
                File.Move(tmp, path, true);
            }
            catch
            {
                // A failed cache write must not fail the response; the lookup still serves this request.
            }
            if (cache is not null)
            {
                cache[key] = rec;
                cacheVersion++;
            }
        }
    }

    Dictionary<string, SidecarItem> loadShowBindings()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(lookupCachePath())!, SHOW_BINDINGS_FILE_NAME);

            if (!File.Exists(path)) return new Dictionary<string, SidecarItem>();

            return JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(
                                                                               File.ReadAllText(path), Options) ?? new Dictionary<string, SidecarItem>();
        }
        catch
        {
            return new Dictionary<string, SidecarItem>();
        }
    }

    internal SidecarItem? GetShowBinding(string key)
    {
        lock (cacheLock)
        {
            showBindings ??= loadShowBindings();

            return showBindings.TryGetValue(key, out var show) ? show : null;
        }
    }

    internal void SaveShowBinding(string key,
                                  SidecarItem show)
    {
        lock (cacheLock)
        {
            showBindings ??= loadShowBindings();
            showBindings[key] = show;

            try
            {
                var path = Path.Combine(Path.GetDirectoryName(lookupCachePath())!, SHOW_BINDINGS_FILE_NAME);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(showBindings, Options));
                File.Move(tmp, path, true);
            }
            catch
            {
                // A failed binding write degrades to the un-bound behaviour on the next run.
            }
        }
    }

    /// <summary>
    /// Merges one record into plex-metadata.json (fresh read, indented, atomic) and the
    /// in-memory cache.
    /// </summary>
    internal void UpsertSidecar(string key,
                                SidecarItem rec)
    {
        lock (cacheLock)
        {
            var path = resolveSidecarPath() ?? preferredSidecarPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dict = new Dictionary<string, SidecarItem>(StringComparer.Ordinal);

            if (File.Exists(path))
            {
                // Let a malformed sidecar throw rather than overwriting it with a partial cache:
                // losing the file would be worse than a failed write.
                dict = JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(
                                                                                   File.ReadAllText(path), Options) ?? dict;
            }
            dict[key] = rec;
            var writeOptions = new JsonSerializerOptions(Options) { WriteIndented = true };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dict, writeOptions));
            File.Move(tmp, path, true);
            if (cache is not null)
            {
                cache[key] = rec;
                cacheVersion++;
            }
        }
    }
}
