# Code Review: PlexCompatibleServer

Review of the whole solution (Api, Core, Infrastructure, tests, and loose files in the repo root), grouped by severity. References are `file:line`.

## High impact

### Sync-over-async in the request path
`ExternalMetadata.cs:1481,1485` calls `Http.SendAsync(...).GetAwaiter().GetResult()` and `ReadAsStringAsync().GetAwaiter().GetResult()`. These are reached synchronously from `Apply` -> `LookupOnline` -> `FetchRecord` inside controller actions, so a slow/blocked plex.tv call ties up a thread-pool thread and can deadlock. Use `async` all the way up.

### Static mutable global state
`ExternalMetadata` (`ExternalMetadata.cs:39-66`) holds `Http`, `_plexToken`, `_cache`, `_lookupCache`, `_showBindings`, `ContentRoot` as process-wide statics. The auth token is set from one request thread (`CaptureToken`) and read by background/other threads without synchronization; the sidecar/cache paths are resolved from a static `ContentRoot` set in `Program.cs:81`. This bypasses DI, makes testing order-dependent, and is a concurrency hazard.

### Token leakage into logs
`Program.cs:123-155` logs every `X-Plex-*` header *and query parameter* (lines 144-146) when `Diagnostics:LogRequests` is on, which it is by default (`appsettings.json:26`). `X-Plex-Token` is commonly passed as a query param, so tokens are written to logs. Redact `X-Plex-Token`.

### Wide-open CORS + hard-coded environment config
`Program.cs:66-75` allows any origin/method/header. Combined with the deliberately unauthenticated design that is a POC choice, but it means any web page can drive the server. Also `appsettings.json:12,17` bakes in machine-specific absolute paths (`G:\usr\local\etc\transmission\...`).

## Medium

### God class / SRP violation
`ExternalMetadata.cs` is 1,804 lines mixing the sidecar store, an HTTP client, JSON parsing, tag mapping, fuzzy matching, backfill orchestration and show-binding persistence. `LibraryController.cs` similarly mixes HTTP handling with video/stream mapping and codec math (`H264Level`, `AspectRatio`, `VideoResolution`, `ReadStreams`).

### Brittle shared encoding
The stream id scheme `itemId * 1000 + index` is duplicated in `LibraryController.cs:379,489` and decoded in `VideoController.cs:238,248`. A change on one side silently breaks the other with no shared type/helper.

### Unbounded in-memory stores (memory leaks)
`PlaybackState._queues`, `StreamSelectionStore._selected`, and `SidecarSubtitles.Listings` grow forever with no eviction/TTL. On a long-running server these leak.

### Unstable ids
`ExternalMetadata.GenerateId` (`:481`) uses `tag.GetHashCode()`, which .NET randomizes per process. The code goes out of its way to make guids process-stable elsewhere (`LibraryController.StableGuidHex`, `:190`), so tag ids changing every restart is inconsistent and will churn client caches.

### Test-specific hack in production code
`ExternalMetadata.cs:93` hard-codes a match on titles containing `"oak"` and `"street"` ("The End of Oak Street"). This is debug scaffolding left in shipping logic.

### O(N^2) list rendering
`ExternalMetadata.Get`/`GetFuzzy` (`:424-476`) linear-scan the whole cache once per item; grids call this per row, so a library page is quadratic in cache size. Also `HubController.RecentHub` and `LibraryController.RecentlyAddedAll` loop libraries and call `GetItemsAsync` per library, loading and sorting full lists in memory repeatedly.

### Blocking I/O inside async scan
`MediaRepository.SynchronizeAsync` calls the synchronous `MediaDurationProbe.GetDurationMs(file)` (`:119`) and blocking `Directory.EnumerateFiles(...AllDirectories)` (`:86`) inside an `async` method.

### Dead / no-op code
`ExternalMetadata.UpsertSidecar` (`:1235-1238`) catches an exception only to `throw;` - a no-op that adds noise. `MediaStreamProbe.ProbeAsync` returns `null!` (`:120,125`) on a `Task<MediaFileInfo?>`.

### Empty exception swallowing
Many bare `catch { }` blocks in `ExternalMetadata.cs` (`:108,140,168,361,399,525,542,567,592,614,644,917,1040,...`) and broad `catch (Exception)` in `MetadataParity.cs:32`, `LibraryController.cs:335` with no logging. Failures become invisible.

### Reflection on every serialize
`PlexJson.WriteObject` (`:114-119`) reflects properties/attributes per call with no cached metadata, on every list/detail response.

## Low / hygiene

- **Repo clutter**: `backup/media.db*`, `backup/plex-metadata.json` (4 MB), `fix_external.py` (a throwaway script that rewrites source), `README - Copy.md`, `.idea/`, and `wwwroot/plex-metadata.json` (4.3 MB untracked). `.gitignore` covers the lookup caches but not `plex-metadata.json` or `backup/`, so the large generated file is at risk of being committed.
- **Meaningless test**: `BasicTests.ProjectIsAlive` asserts `2 + 2 == 4` (`BasicTests.cs:10`).
- **Version spoofing**: `ServerOptions`/`appsettings` hard-code a real Plex version string and use MD5 for library UUIDs; harmless but misleading.
- **Repeated regex/strings**: extension lists and quality-token regexes are duplicated across `ExternalMetadata`, `LibraryController`, `MediaRepository`. `PhotoController` (`:55,83`) builds regex work per request instead of cached `RegexOptions.Compiled` / simple `Contains`.
- **Inconsistent formatting**: `ExternalMetadata.cs`, `PlexJson.cs`, `MetadataParity.cs`, `PhotoController.cs` use a different brace/indent/alignment style (1-space, K&R, column-aligned initializers) than the rest of the Allman-formatted codebase; `PhotoController.cs:22-24` has a mis-indented comment and block. No analyzer/format config (`Directory.Build.props` sets `TreatWarningsAsErrors=false`).

## Recommended fix order

1. Remove the sync-over-async HTTP calls.
2. Move `ExternalMetadata`'s statics behind DI.
3. Redact tokens in the request logger.
4. Split the god class.
