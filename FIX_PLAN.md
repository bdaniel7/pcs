# Fix Plan: PlexCompatibleServer

Ordered to reduce risk: establish a safety net, then fix criticals, then refactor, then optimize. Each phase is independently shippable.

## Phase 0 - Safety net & hygiene (low risk, do first)

Goal: make later refactors safe and clean the tree.

1. **Freeze current behavior with characterization tests.** Add tests around the highest-risk paths before touching them:
   - `ExternalMetadata.Apply` overlay output for a movie and an episode.
   - `LibraryController.ToVideo` / stream id encoding (`itemId*1000+index`) round-trip against `VideoController.GetSubtitle`.
   - `MediaRepository.SynchronizeAsync` add/rename/delete behavior (extend `MediaRepositorySyncTests`).
2. **Repo hygiene.** Delete `fix_external.py`, `README - Copy.md`, `backup/`; add `.idea/`, `backup/`, `src/**/wwwroot/plex-metadata.json` to `.gitignore`; decide whether the 4.3 MB `plex-metadata.json` should be a seeded sample or generated-only.
3. **Replace the trivial test** `BasicTests.ProjectIsAlive` with real ones (or remove).
4. **Add format/analyzer config:** `.editorconfig` with the Allman style, and flip `TreatWarningsAsErrors` on for `Core`/`Infrastructure` first. Run `dotnet format`.
5. **Move machine paths out of `appsettings.json`** into `appsettings.Development.json` / docs.

Verification: `dotnet test`, `dotnet format --verify-no-changes`.

## Phase 1 - Security & correctness criticals

1. **Redact tokens in logging** (`Program.cs:141-150`). Filter `X-Plex-Token` out of the logged identity list; add a unit test asserting a token never appears.
2. **Tighten CORS** (`Program.cs:66-75`). Replace `AllowAnyOrigin` with a configurable origin list (default localhost), still exposing the same headers. Document the POC tradeoff.
3. **Fix unstable ids** (`ExternalMetadata.GenerateId:481`). Replace `tag.GetHashCode()` with `StableGuidHex`-style SHA-256 over `(kind, tag)` so ids survive restarts.
4. **Remove the Oak Street hack** (`ExternalMetadata.cs:93`) and confirm tests don't depend on it; if a fixture depends on it, fix the fixture.
5. **Remove no-op/dead code**: `UpsertSidecar:1235-1238` `catch { throw; }`; `MediaStreamProbe.ProbeAsync` `null!` returns -> `return default;`/proper null.
6. **SQL hardening in `EnsureColumnsAsync`**: keep the fixed-list approach but route through a single validated helper; add a guard that throws on any non-allowlisted table/column instead of raw interpolation.

Verification: new unit tests for redaction and id stability; regression suite green.

## Phase 2 - Async & DI for metadata (biggest refactor)

Goal: kill sync-over-async and static global state without a big-bang rewrite. Do it incrementally.

1. **Introduce an interface** `IMetadataService` (async) in `Core`, with methods mirroring today's public surface: `ApplyAsync`, `TryGetRecord`, `BackfillAsync`, `EnsureAsync` (artwork). Keep `ExternalMetadata` as the implementation for now.
2. **Split the god class** along existing seams into internal collaborators (same namespace, incremental):
   - `SidecarStore` - sidecar/lookup/show-binding load + persist (`Get*`, `PersistLookup`, `UpsertSidecar`, `LoadShowBindings`, `SaveShowBinding`).
   - `PlexTvClient` - `Http`, `Fetch` (async), search/detail/children walks, `CaptureToken`.
   - `MetadataMatcher` - `GetKey`/`KeyOf*`/`GetFuzzy`/`PickCandidate`/`PickShow`/episode verification.
   - `MetadataMapper` - the field overlay in `Apply`.
3. **Make network I/O async.** Convert `Fetch` to `async Task<string?>` and propagate `await` through `LookupOnline`/`FetchRecord`/`FetchEpisodeRecord`/`WalkEpisodeShow`.
4. **Split the two call paths explicitly:**
   - List/grid path stays **cache-only and synchronous** (`allowNetwork:false`) - no signature churn; `ToVideoEnriched` keeps working.
   - Detail path (`MetadataController.Get`) becomes `await metadata.ApplyAsync(...)`.
5. **Register in DI** (`Program.cs`): `AddSingleton<IMetadataService, ...>`, inject into `MetadataController`, `LibraryController`, `VideoController`, `PhotoController`, `MetadataParity`, `MetadataSyncService`; delete `ExternalMetadata.ContentRoot` static in favor of an injected path option.
6. **Thread-safety:** move token/cache state onto the singleton instance; guard mutable caches with `ConcurrentDictionary` or `lock` consistently; remove mixed locking.

Verification: existing metadata/artwork tests rewritten against the interface; a test that the detail path awaits (add a grep-based check that no `.GetAwaiter().GetResult()` remains).

## Phase 3 - Performance & resource limits

1. **Bound the in-memory stores:** add TTL/size eviction to `PlaybackState`, `StreamSelectionStore`, `SidecarSubtitles.Listings` (e.g. `MemoryCache` with size limits).
2. **Kill O(N^2) lookups:** give `SidecarStore` a precomputed normalized-title index (built once on load) so `GetFuzzy` is a dictionary probe, not a full scan.
3. **Fix N+1 in hubs/recently-added:** add repository methods (`GetItemsByLibrariesAsync`, `GetRecentAsync(libraryIds, limit)`) that query in one round trip; use them in `HubController` and `LibraryController.RecentlyAddedAll`.
4. **Offload blocking probes:** make duration probing async (`Task.Run` or a channel worker) in `SynchronizeAsync`; enumerate files without blocking the scan thread.
5. **Cache reflection/serialization metadata:** add a `ConcurrentDictionary<Type, PropertyInfo[]>` in `PlexJson`; precompute the `HashSet` checks.
6. **Replace per-request regex** in `PhotoController`/`TimelineController` with cached `Regex`/`Contains`/span parsing.

Verification: add an assertion test for hub/library query counts; load test a large library if possible.

## Phase 4 - Code quality & consistency

1. **Extract mapping out of `LibraryController`** into a `VideoMapper`/`XmlVideoFactory` and move codec math (`H264Level`, `AspectRatio`, `VideoResolution`, `Bandwidths`) into a `MediaFormatting` helper shared with probes.
2. **Centralize extension/quality-token tables** in one place used by `ExternalMetadata`, `LibraryController`, `MediaRepository`.
3. **Replace empty `catch { }`** with either logged catches or narrowed exception filters; audit each of the ~15 sites in `ExternalMetadata`.
4. **Normalize formatting** across the outlier files and enable `TreatWarningsAsErrors` everywhere once clean.
5. **Fix no-op interfaces** (`FileSystemMediaScanner`, `PlaybackService`) - either give them real behavior or collapse them; low priority, may be intentional seams.

## Suggested execution order & checkpoints

| Step | Phase | Risk | Checkpoint |
|------|-------|------|------------|
| 1 | 0 | low | tree clean, `format`/tests green |
| 2 | 1 | low | redaction + id-stability tests pass |
| 3 | 2 | high | no blocking HTTP; metadata tests against interface |
| 4 | 3 | medium | eviction + index tests; query-count checks |
| 5 | 4 | low | format clean, warnings-as-errors on |

Phase 2 is the long pole and can be split into its own PRs (interface first, then collaborators, then async). Phases 1 and 4 are almost independent and can proceed in parallel.
