using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlexCompatibleServer.Api.Serialization;
using PlexCompatibleServer.Core.Models;

namespace PlexCompatibleServer.Api.Controllers;

/// <summary>
/// Owns the on-disk sidecar stores (scraped metadata, offline lookup cache, show bindings) and the
/// in-memory caches derived from them. Persistence, path resolution and the precomputed fuzzy index
/// live here; matching and HTTP concerns sit in the collaborators that consume this store.
/// </summary>
internal sealed class SidecarStore
{
    private const string SidecarPath = "wwwroot/plex-metadata.json";
    private const string LookupCacheFileName = "plex-lookup-cache.json";
    private const string ShowBindingsFileName = "plex-show-bindings.json";

    private readonly object _cacheLock = new();

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

    private ConcurrentDictionary<string, SidecarItem>? _cache;
    private int _cacheVersion;
    private FuzzyIndex? _fuzzyIndex;
    private int _fuzzyIndexVersion = -1;
    private Dictionary<string, SidecarItem>? _lookupCache;
    private Dictionary<string, SidecarItem>? _showBindings;

    internal SidecarItem? Get(string key)
    {
        var cache = _cache;

        if (cache is null)
        {
            var path = ResolveSidecarPath();

            var loaded = path is not null
                             ? JsonSerializer.Deserialize<Dictionary<string, SidecarItem>>(File.ReadAllText(path), Options)
                             : null;
            cache = new ConcurrentDictionary<string, SidecarItem>(loaded ?? new(), StringComparer.Ordinal);
            foreach (var kv in LoadLookupCache()) cache[kv.Key] = kv.Value;
            _cache = cache;
            _cacheVersion++;
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
        var cache = _cache;

        if (cache is null || string.IsNullOrEmpty(filename)) return null;

        var index = GetFuzzyIndex(cache);
        var fn = filename.ToLowerInvariant();

        // Exact stem hit is the common case (the file names its own sidecar): answer with one probe
        // before touching the precomputed entry list.
        if (index.ByStem.TryGetValue(fn, out var exact)) return exact;

        var fnorm = NormalizeFuzzy(fn);

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
    private FuzzyIndex GetFuzzyIndex(ConcurrentDictionary<string, SidecarItem> cache)
    {
        var index = _fuzzyIndex;
        if (index is not null && _fuzzyIndexVersion == _cacheVersion && ReferenceEquals(index.Cache, cache))
            return index;

        lock (_cacheLock)
        {
            index = _fuzzyIndex;
            if (index is not null && _fuzzyIndexVersion == _cacheVersion && ReferenceEquals(index.Cache, cache))
                return index;

            index = FuzzyIndex.Build(cache);
            _fuzzyIndex = index;
            _fuzzyIndexVersion = _cacheVersion;
            return index;
        }
    }

    private static string NormalizeFuzzy(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "[^a-z0-9]+", "");

    /// <summary>
    /// Per-record lookup data built once per cache revision: the lower-cased file stem (for the raw
    /// containment checks) and the stripped-alphanumeric title (so the query only pays for one
    /// normalization, not one per record).
    /// </summary>
    private sealed class FuzzyIndex
    {
        private FuzzyIndex(ConcurrentDictionary<string, SidecarItem> cache,
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
                var titleNorm = NormalizeFuzzy((pair.Value.Title ?? string.Empty).ToLowerInvariant());
                entries[i++] = new Entry(stem, titleNorm, pair.Value);

                if (stem.Length > 0 && !byStem.ContainsKey(stem)) byStem[stem] = pair.Value;
            }

            return new FuzzyIndex(cache, byStem, entries);
        }

        public readonly record struct Entry(string Stem, string TitleNorm, SidecarItem Record);
    }

    private string? ResolveSidecarPath()
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

        return File.Exists(SidecarPath) ? Path.GetFullPath(SidecarPath) : null;
    }

    private string PreferredSidecarPath() =>
        ContentRoot is not null
            ? Path.Combine(ContentRoot, "wwwroot", "plex-metadata.json")
            : Path.Combine(AppContext.BaseDirectory, "wwwroot", "plex-metadata.json");

    private string LookupCachePath()
    {
        var sidecar = ResolveSidecarPath();

        var dir = sidecar is not null
                      ? Path.GetDirectoryName(sidecar)!
                      : Path.GetDirectoryName(PreferredSidecarPath())!;

        return Path.Combine(dir, LookupCacheFileName);
    }

    private Dictionary<string, SidecarItem> LoadLookupCache()
    {
        try
        {
            var path = LookupCachePath();

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
        lock (_cacheLock)
        {
            _lookupCache ??= LoadLookupCache();
            _lookupCache[key] = rec;

            try
            {
                var path = LookupCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_lookupCache, Options));
                File.Move(tmp, path, true);
            }
            catch
            {
                // A failed cache write must not fail the response; the lookup still serves this request.
            }
            if (_cache is not null)
            {
                _cache[key] = rec;
                _cacheVersion++;
            }
        }
    }

    private Dictionary<string, SidecarItem> LoadShowBindings()
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(LookupCachePath())!, ShowBindingsFileName);

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
        lock (_cacheLock)
        {
            _showBindings ??= LoadShowBindings();

            return _showBindings.TryGetValue(key, out var show) ? show : null;
        }
    }

    internal void SaveShowBinding(string key,
                                  SidecarItem show)
    {
        lock (_cacheLock)
        {
            _showBindings ??= LoadShowBindings();
            _showBindings[key] = show;

            try
            {
                var path = Path.Combine(Path.GetDirectoryName(LookupCachePath())!, ShowBindingsFileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_showBindings, Options));
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
        lock (_cacheLock)
        {
            var path = ResolveSidecarPath() ?? PreferredSidecarPath();
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
            if (_cache is not null)
            {
                _cache[key] = rec;
                _cacheVersion++;
            }
        }
    }
}
