#!/usr/bin/env bash
#
# Installs Plex Compatible Server on Debian / Ubuntu / Raspberry Pi OS:
# publishes the build to /opt/plex-compatible-server, creates a service
# account, registers a systemd unit and starts it.
#
# Dependencies are NOT installed by this script - missing ones are
# reported with install instructions and the script exits.
#
# Usage:
#   sudo ./install.sh [--movies /path/to/Movies] [--tv /path/to/Shows]
#
# Re-running upgrades in place: media.db, the artwork cache and
# appsettings.Production.json are kept.

set -euo pipefail

INSTALL_DIR=/opt/plex-compatible-server
SERVICE_USER=plexcompat
SERVICE_HOME=/var/lib/plexcompat
UNIT_NAME=plex-compatible-server
PORT=32400

MOVIES=""
TV=""
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

usage() {
    cat <<'EOF'
Usage: sudo ./install.sh [--movies PATH] [--tv PATH]

Installs Plex Compatible Server as a systemd service on Debian, Ubuntu or
Raspberry Pi OS.

  --movies PATH   movie library root (prompted for when omitted)
  --tv PATH       TV library root (prompted for when omitted)
  -h, --help      show this help

Library roots are written to appsettings.Production.json, which survives
upgrades. Without them the server starts with empty libraries; edit that
file and "systemctl restart plex-compatible-server" to configure later.

Dependencies (ffmpeg, ffprobe, .NET 10 SDK) are checked, never installed:
if any are missing the script prints install instructions and exits.
EOF
}

err()  { printf 'ERROR: %s\n' "$*" >&2; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
info() { printf '%s\n' "$*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --movies) [ $# -ge 2 ] || { err "--movies needs a path"; exit 2; }; MOVIES=$2; shift 2 ;;
        --tv)     [ $# -ge 2 ] || { err "--tv needs a path"; exit 2; }; TV=$2; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) err "unknown argument: $1"; usage >&2; exit 2 ;;
    esac
done

[ "$(id -u)" -eq 0 ] || { err "this installer must run as root: sudo ./install.sh"; exit 1; }

if [ ! -r /etc/os-release ]; then
    err "/etc/os-release not found; cannot identify the distribution"
    exit 1
fi
# shellcheck source=/dev/null
. /etc/os-release
case " ${ID:-} ${ID_LIKE:-} " in
    *" debian "*|*" ubuntu "*|*" raspbian "*) ;;
    *) err "unsupported distribution '${ID:-unknown}' - need Debian, Ubuntu or Raspberry Pi OS"; exit 1 ;;
esac

command -v systemctl >/dev/null 2>&1 \
    || { err "systemd not found; this installer targets systemd-based systems"; exit 1; }

# --- dependency check (report, never install) -------------------------------

missing=()

FFMPEG=$(command -v ffmpeg || true)
[ -n "$FFMPEG" ] || missing+=("ffmpeg - not found on PATH")

FFPROBE=$(command -v ffprobe || true)
[ -n "$FFPROBE" ] || missing+=("ffprobe - not found on PATH")

DOTNET=$(command -v dotnet || true)
if [ -z "$DOTNET" ]; then
    missing+=(".NET 10 SDK - dotnet not found on PATH")
elif ! dotnet --list-sdks 2>/dev/null | grep -qE '^10\.'; then
    found=$(dotnet --list-sdks 2>/dev/null | tr '\n' ' ' || true)
    missing+=(".NET 10 SDK - required, found: ${found:-none}")
fi

if [ ${#missing[@]} -gt 0 ]; then
    {
        err "missing required dependencies:"
        for m in "${missing[@]}"; do printf '  - %s\n' "$m"; done
        cat <<'EOF'

Install them yourself, then re-run this script:

  ffmpeg / ffprobe:
      sudo apt update && sudo apt install -y ffmpeg

  .NET 10 SDK (Debian / Ubuntu / Raspberry Pi OS, amd64 or arm64):
      # configure the Microsoft package feed for your distro/version
      # (e.g. debian/12, debian/13, ubuntu/24.04):
      wget https://packages.microsoft.com/config/<distro>/<version>/packages-microsoft-prod.deb
      sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
      sudo apt update && sudo apt install -y dotnet-sdk-10.0
      Full instructions: https://learn.microsoft.com/dotnet/core/install/linux
      Note (32-bit ARM / armhf): .NET 10 is not packaged for every
      Raspberry Pi OS release - check the link above for supported
      architectures; 64-bit Raspberry Pi OS (arm64) is recommended.

  curl or wget (optional, used for the post-install health check):
      sudo apt install -y curl
EOF
    } >&2
    exit 1
fi

CSPROJ="$SRC_DIR/src/PlexCompatibleServer.Api/PlexCompatibleServer.Api.csproj"
[ -f "$CSPROJ" ] \
    || { err "not a repository checkout (missing $CSPROJ) - run the script from the extracted/cloned source"; exit 1; }

# --- library roots ----------------------------------------------------------

if [ -t 0 ]; then
    if [ -z "$MOVIES" ]; then
        read -r -p "Movie library path (Enter to skip): " MOVIES || MOVIES=""
    fi
    if [ -z "$TV" ]; then
        read -r -p "TV library path (Enter to skip): " TV || TV=""
    fi
fi

if [ -n "$MOVIES" ] && [ ! -d "$MOVIES" ]; then
    warn "movie library path does not exist on this machine: $MOVIES"
fi
if [ -n "$TV" ] && [ ! -d "$TV" ]; then
    warn "TV library path does not exist on this machine: $TV"
fi

# --- publish ----------------------------------------------------------------

info "Publishing to $INSTALL_DIR ..."
mkdir -p "$INSTALL_DIR"
if ! DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 \
        dotnet publish "$CSPROJ" -c Release -o "$INSTALL_DIR" --nologo; then
    err "dotnet publish failed (the first run needs network access to nuget.org)"
    exit 1
fi
[ -f "$INSTALL_DIR/PlexCompatibleServer.Api.dll" ] \
    || { err "publish reported success but $INSTALL_DIR/PlexCompatibleServer.Api.dll is missing"; exit 1; }

# appsettings.Production.json overrides only the keys it declares, so the
# roots survive upgrades while appsettings.json tracks the repository.
json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }

if [ -n "$MOVIES" ] || [ -n "$TV" ]; then
    entries=""
    if [ -n "$MOVIES" ]; then
        entries=$(printf '{ "Name": "Movies", "Path": "%s", "Type": "movie" }' "$(json_escape "$MOVIES")")
    fi
    if [ -n "$TV" ]; then
        tv_entry=$(printf '{ "Name": "TV Shows", "Path": "%s", "Type": "show" }' "$(json_escape "$TV")")
        if [ -n "$entries" ]; then
            entries="$entries,
      $tv_entry"
        else
            entries="$tv_entry"
        fi
    fi
    cat > "$INSTALL_DIR/appsettings.Production.json" <<EOF
{
  "Media": {
    "Roots": [
      $entries
    ]
  }
}
EOF
    info "Library roots written to $INSTALL_DIR/appsettings.Production.json"
else
    info "No library paths given - the server starts with empty libraries."
    info "Configure $INSTALL_DIR/appsettings.Production.json and restart the service."
fi

# --- service account --------------------------------------------------------

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    NOLOGIN=$(command -v nologin || echo /usr/sbin/nologin)
    useradd --system --user-group --home-dir "$SERVICE_HOME" --create-home \
        --shell "$NOLOGIN" "$SERVICE_USER"
    info "Created service account $SERVICE_USER"
fi
chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR" "$SERVICE_HOME"

case "$DOTNET" in
    /home/*)
        warn "dotnet is at $DOTNET, under another user's home - the $SERVICE_USER"
        warn "service may not be able to run it. The apt package dotnet-sdk-10.0 is safest."
        ;;
esac

# --- systemd unit -----------------------------------------------------------

# PLEX_FFMPEG/PLEX_FFPROBE are required: the server only probes those two
# environment variables plus "*.exe" file names, never bare "ffmpeg".
cat > "/etc/systemd/system/$UNIT_NAME.service" <<EOF
[Unit]
Description=Plex Compatible Server - Plex-compatible media server
After=network.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
Environment=HOME=$SERVICE_HOME
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=PLEX_FFMPEG=$FFMPEG
Environment=PLEX_FFPROBE=$FFPROBE
ExecStart=$DOTNET $INSTALL_DIR/PlexCompatibleServer.Api.dll
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
    ufw allow "$PORT/tcp" >/dev/null
    info "Firewall: allowed $PORT/tcp through ufw"
fi

systemctl daemon-reload
systemctl enable "$UNIT_NAME" >/dev/null
systemctl restart "$UNIT_NAME"

# --- health check -----------------------------------------------------------

info "Waiting for the server on port $PORT ..."
health="failed"
probe=""
if command -v curl >/dev/null 2>&1; then
    probe=curl
elif command -v wget >/dev/null 2>&1; then
    probe=wget
fi

if [ -z "$probe" ]; then
    health="skipped"
    warn "neither curl nor wget found - skipping the health check"
else
    for _ in $(seq 1 30); do
        if [ "$probe" = curl ]; then
            if curl -fsS -m 2 "http://127.0.0.1:$PORT/" >/dev/null 2>&1; then health=ok; break; fi
        else
            if wget -q -T 2 -O /dev/null "http://127.0.0.1:$PORT/" 2>/dev/null; then health=ok; break; fi
        fi
        sleep 1
    done
fi

if [ "$health" = failed ]; then
    err "the server did not answer on port $PORT"
    if command -v journalctl >/dev/null 2>&1; then
        journalctl -u "$UNIT_NAME" -n 40 --no-pager >&2 || true
    fi
    exit 1
fi

# --- summary ----------------------------------------------------------------

info ""
info "Installed Plex Compatible Server."
info "  status : systemctl status $UNIT_NAME"
info "  logs   : journalctl -u $UNIT_NAME -f"
info "  config : $INSTALL_DIR/appsettings.Production.json"
info "  server : http://<this-machine-ip>:$PORT/"
info ""
info "The service runs as '$SERVICE_USER'. It needs read access to your media:"
info "  sudo usermod -aG <media-group> $SERVICE_USER   # then: sudo systemctl restart $UNIT_NAME"
info "  ...or grant read access:  chmod -R o+rX <library-path>"
info ""
info "Add the server in a Plex client at <this-machine-ip>:$PORT"
