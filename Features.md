# Features

A local, unauthenticated, Plex-compatible media server built with .NET 10 / ASP.NET Core and
SQLite. Developed as a proof of concept by reverse-engineering the Plex wire protocol, and
verified against the official Plex app on LG webOS (OLED48C41LA, Plex for LG 5.94.3).

## Media Library

- **Configured roots** — libraries are defined in `appsettings.json` (`Media:Roots`), each with a
  name, filesystem path and type (`movie` / `show`).
- **Startup scan** — files are synchronized into SQLite (`media.db`) on start; removed files are
  pruned so the database always matches disk.
- **On-demand rescan** — `GET|PUT /library/sections/{id}/refresh`.
- **Rebuild from scratch** — deleting `media.db` triggers a full metadata rebuild on the next run.
- **Duration probing** — container headers are parsed directly for Matroska/WebM and MP4/QuickTime;
  other formats report `0`.
- **ffprobe integration** — stream details (codecs, resolutions, languages, channels, frame rates)
  are read from media files for accurate track listings.

## Plex API Surface

Implements the HTTP endpoints the Plex client workflow needs, answering in both XML and JSON
(content negotiation):

- **Discovery** — `/`, `/identity`, `/media/providers` (search/metadata/image-transcoder feature
  declarations), `/status/sessions`, `/player/proxy/poll`, websocket notifications stub.
- **Libraries** — section listing, item listing with paging/sort/filter (`/library/sections/{id}/all`),
  `recentlyAdded`, detail (`/library/metadata/{id}`), season/episode children, and the image slots
  (`thumb`, `art`, `squareArt`, `clearLogo`, with cache busters).
- **Hubs** — home hubs, section hubs, promoted hub, and related-items hub, all enriched with real
  cached metadata (official titles, episode hierarchy). *Continue Watching* lists items with saved
  playback progress (newest first); *On Deck* stays empty (episode ordering lives only in the
  sidecar cache, and a wrong next-episode is worse than none).
- **Play queues** — created and served in memory (`POST|PUT|GET /playQueues`).
- **Artwork resolver** — `/photo/:/transcode` returns the correct slot (poster vs. wide backdrop)
  per request and never 404s; a 1×1 placeholder stands in when no artwork exists.
- **Server identity** — `/` and `/identity` expose the configured name, machine identifier and a
  Plex-compatible version string; `X-Plex-Token` arriving at `/identity` is captured in memory
  (never persisted) for later metadata lookups.

## Metadata Enrichment (plex.tv discover)

- **Movies** — filename-based title/year search against plex.tv discover, best-candidate selection,
  detail enrichment (summary, genres, cast/crew, ratings, content rating), and a translated-title
  fallback.
- **TV episodes** — filenames are parsed for season/episode numbers and episode titles
  (`TvEpisodeName`); the matching show is located on plex.tv and the real episode record is fetched
  through season → episode children walks (show title, episode title, synopsis, credits, air year).
- **Show disambiguation** — shows that share a name (e.g. *Dark Matter* 2015 vs. 2024) are
  resolved by, in order of confidence: a year in the filename, an episode title in the filename
  verified against each candidate's real episode data, or a persistent **show binding**
  (`plex-show-bindings.json`) established by an earlier confident lookup. Backfill processes
  confident episodes first so they establish the binding for their titleless siblings.
- **Caches** — `plex-metadata.json` (all enriched items), `plex-lookup-cache.json` (movie lookup
  fallbacks), `plex-show-bindings.json` (show identity). Tolerant deserialization survives schema
  drift between releases.
- **Cache-only list rows** — grid/list/hub/play-queue responses never touch the network: they only
  paint titles from the local cache, so a lookup miss falls back to the filename instead of
  blocking or slowing the UI. Detail pages are allowed full parity enrichment.
- **Backfill pipeline** — runs automatically after every media scan and on demand via
  `GET|POST /admin/metadata/backfill`; reports scanned / present / created / enriched / failed.

## Playback Progress

- **Timeline persistence** — `GET /:/timeline` stores the reported position (`time`/`duration`,
  milliseconds) per item; zero-time buffering heartbeats are ignored so a resume-seek cannot
  clobber the saved position. Positions survive server restarts and rescans.
- **Metadata emission** — detail, grid, hub and play-queue responses expose `viewOffset` (ms),
  `viewCount` and `lastViewedAt` (unix seconds) as unquoted JSON numbers / XML attributes.
- **Watched detection** — the client never sends explicit watched flags beyond position reports, so
  an item is marked watched when progress crosses 90% of its duration; a 10-minute same-session
  guard prevents double counting from overlapping signals. `/:/scrobble` and `/:/unscrobble` are
  honored as well (the LG client simply never calls them).
- **Continue Watching** — home and section hub surfaces list in-progress items ordered by
  `lastViewedAt`, rendered through the same enriched list-row pipeline as the grids.

## Playback (Direct Play)

- **HTTP range streaming** — `GET /library/parts/{id}/...` and stream endpoints serve the file with
  `Accept-Ranges` and range processing; `HEAD` returns identical headers with no body.
- **Transcode decision** — `/video/:/transcode/universal/decision` always answers `directplay`.
  If a client still attempts to transcode, `/video/:/transcode/universal/start` returns a clean
  `501` explaining that only direct play is supported.
- **Track selection** — `PUT /library/parts/{partId}` stores the chosen audio/subtitle stream for
  the session and is honored by subtitle requests.

## Subtitles

- **External sidecars** — `.srt` files next to the video are discovered with language/forced tag
  support (`Movie.en.srt`, `Movie.eng.forced.srt`, bare `Movie.srt`, …).
- **Embedded text extraction** — text tracks inside the container (SubRip, ASS/SSA, mov_text,
  WebVTT) are extracted to SRT on demand with ffmpeg and cached; bitmap codecs (PGS, DVB, VobSub)
  are not extractable and stay in the container.
- **Delivery** — subtitles are served via `/library/parts/.../subtitles/{streamId}.srt` and the
  universal subtitle start endpoint resolves the currently selected track.

## Artwork Generation

- **Poster/backdrop extraction** — ffmpeg captures a frame at a configurable fraction of the
  duration (default 10%), cached on disk with a concurrency limit.
- **Cache** — generated images live under the configured `Media:Art:CacheDirectory`.

## Operations & Diagnostics

- **Request logging** — every request/response is logged with client identity
  (`Diagnostics:LogRequests`).
- **Configuration** — server name, machine identifier, advertised version, library roots, art
  options and connection string all live in `appsettings.json`.
- **Static caches** — metadata caches are served from `wwwroot` for easy inspection.
- **Tests** — 129 automated tests (NUnit) covering paging, XML serialization, subtitle discovery,
  sidecar parsing, filename parsing, metadata lookup, disambiguation, caching behavior and playback
  progress (persistence, watched threshold, hubs).

## Scope & Limitations

- **No authentication** — the server is open on the local network; Plex account sign-in is not
  implemented (the optional `X-Plex-Token` is only used as a plex.tv metadata lookup credential).
- **No transcoding** — direct play only; `ITranscodeService` is the intended future hook.
- **On Deck** — empty by design: computing the "next episode" requires episode ordering that only
  exists in the sidecar metadata cache, and guessing wrong would resume at the wrong episode.
- **Proof of concept** — additional endpoints can be added from captured client traffic as needed.
