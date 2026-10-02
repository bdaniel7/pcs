# Plex Compatible Server

**Proof of concept**

Reverse-engineered by analyzing traffic captured with Wireshark.

A local, unauthenticated Plex-compatible media server for LG Plex clients.

## Scope

This project deliberately does **not** integrate with plex.tv or implement Plex authentication.
It exposes the local HTTP API needed for a Plex-like client workflow.

The first playback path is direct play with HTTP Range support.

## Requirements

- .NET 10 SDK
- Windows/Linux/macOS
- Media files readable by the service account

## Run

```bash
dotnet restore
dotnet run --project src/PlexCompatibleServer.Api
```

Default URL: `http://0.0.0.0:32400`

Set media roots in `appsettings.json`.

## Example

```json
{
  "Server": {
    "Name": "Living Room Media",
    "MachineIdentifier": "change-me"
  },
  "Media": {
    "Roots": [
      { "Name": "Movies", "Path": "D:\\Media\\Movies", "Type": "movie" },
      { "Name": "TV Shows", "Path": "D:\\Media\\TV", "Type": "show" }
    ]
  }
}
```

On startup the scanner synchronizes media files into SQLite. Call
`/library/sections/{id}/refresh` to rescan on demand.

Duration is read directly from the container header (Matroska/WebM and
MP4/QuickTime). Other formats report `duration="0"`.

## Notes

This is a compatibility implementation, not the official Plex server protocol implementation.
LG clients can request additional endpoints depending on webOS/Plex app version. Add those endpoints based on captured requests.

Direct play is intentionally separated from transcoding. FFmpeg can later be added behind `ITranscodeService`.


