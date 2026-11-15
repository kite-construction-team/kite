#!/usr/bin/env bash
set -euo pipefail

REPO="kite-construction-team/kite"
VERSION="${KITE_VERSION:-latest}"
PREFIX=""
ARCHIVE_PATH=""
CHECKSUM_FILE=""
NO_MODIFY_PATH=0
WITH_SYSTEMD=0
GRANT_LOW_PORTS=0
SYSTEM_INSTALL=0
UNINSTALL=0
SKIP_VERIFY=0
FORCE_RID=""

usage() {
  cat <<'EOF'
Kite Edge Server Linux Installer

Usage:
  curl -fsSL https://raw.githubusercontent.com/kite-construction-team/kite/main/scripts/install.sh | bash
  curl -fsSL https://raw.githubusercontent.com/kite-construction-team/kite/main/scripts/install.sh | sudo bash -s -- --with-systemd --grant-low-ports

Options:
  --version <ver>      Install a specific tag (e.g. v1.0.0) or "latest" (default: latest)
  --rid <rid>          Override target RID (linux-x64 or linux-arm64)
  --prefix <dir>       Custom installation directory for binaries/payload
  --archive <path>     Install from a local .tar.gz archive instead of downloading
  --sums <path>        Local SHA256SUMS.txt file (used with --archive)
  --skip-verify        Skip SHA-256 checksum verification
  --system             Perform system-wide install (/opt/kite, /usr/local/bin, /var/lib/kite)
  --with-systemd       Install /etc/systemd/system/kite.service (requires root)
  --grant-low-ports    Grant cap_net_bind_service=+ep so non-root can bind :80/:443 (requires root)
  --no-modify-path     Do not edit shell profile files (~/.bashrc, ~/.zshrc, ~/.profile)
  --uninstall          Remove installed Kite binaries and shims (preserves site data)
  -h, --help           Show this help message
EOF
}

log() { printf '[kite-install] %s\n' "$*"; }
warn() { printf '[kite-install] WARN: %s\n' "$*" >&2; }
die() { printf '[kite-install] ERROR: %s\n' "$*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --version) [ $# -ge 2 ] || die "--version requires an argument"; VERSION="$2"; shift 2 ;;
    --rid) [ $# -ge 2 ] || die "--rid requires an argument"; FORCE_RID="$2"; shift 2 ;;
    --prefix) [ $# -ge 2 ] || die "--prefix requires an argument"; PREFIX="$2"; shift 2 ;;
    --archive) [ $# -ge 2 ] || die "--archive requires an argument"; ARCHIVE_PATH="$2"; shift 2 ;;
    --sums) [ $# -ge 2 ] || die "--sums requires an argument"; CHECKSUM_FILE="$2"; shift 2 ;;
    --skip-verify) SKIP_VERIFY=1; shift ;;
    --system) SYSTEM_INSTALL=1; shift ;;
    --with-systemd) WITH_SYSTEMD=1; SYSTEM_INSTALL=1; shift ;;
    --grant-low-ports) GRANT_LOW_PORTS=1; shift ;;
    --no-modify-path) NO_MODIFY_PATH=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "Unknown option: $1 (use --help for usage)" ;;
  esac
done

OS_NAME="$(uname -s)"
if [ "$OS_NAME" != "Linux" ]; then
  if [ "$OS_NAME" = "Darwin" ]; then
    die "macOS detected. Use Homebrew instead: brew install kite-construction-team/tap/kite"
  fi
  die "Unsupported OS '$OS_NAME'. This script targets Linux."
fi

IS_ROOT=0
if [ "$(id -u)" -eq 0 ]; then
  IS_ROOT=1
fi

if [ "$IS_ROOT" -eq 1 ]; then
  SYSTEM_INSTALL=1
fi

if [ "$SYSTEM_INSTALL" -eq 1 ] && [ "$IS_ROOT" -ne 1 ]; then
  die "--system / --with-systemd require root privileges (re-run with sudo)."
fi

if [ "$GRANT_LOW_PORTS" -eq 1 ] && [ "$IS_ROOT" -ne 1 ]; then
  die "--grant-low-ports requires root privileges (re-run with sudo)."
fi

if [ -n "$PREFIX" ]; then
  INSTALL_APP_DIR="$PREFIX/kite"
  BIN_DIR="$PREFIX/bin"
  DATA_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/kite/kite-data"
elif [ "$SYSTEM_INSTALL" -eq 1 ]; then
  INSTALL_APP_DIR="/opt/kite"
  BIN_DIR="/usr/local/bin"
  DATA_DIR="/var/lib/kite"
  ETC_DIR="/etc/kite"
else
  XDG_DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
  INSTALL_APP_DIR="$XDG_DATA/kite/app"
  BIN_DIR="$HOME/.local/bin"
  DATA_DIR="$XDG_DATA/kite/kite-data"
fi

if [ "$UNINSTALL" -eq 1 ]; then
  log "Removing Kite binaries from $INSTALL_APP_DIR and $BIN_DIR..."
  for name in kite kite-server Kite.Server; do
    if [ -L "$BIN_DIR/$name" ] || [ -f "$BIN_DIR/$name" ]; then
      rm -f "$BIN_DIR/$name"
    fi
  done
  if [ -d "$INSTALL_APP_DIR" ]; then
    rm -rf "$INSTALL_APP_DIR"
  fi
  if [ "$IS_ROOT" -eq 1 ] && [ -f "/etc/systemd/system/kite.service" ]; then
    if command -v systemctl >/dev/null 2>&1; then
      systemctl stop kite.service >/dev/null 2>&1 || true
      systemctl disable kite.service >/dev/null 2>&1 || true
      rm -f /etc/systemd/system/kite.service
      systemctl daemon-reload >/dev/null 2>&1 || true
    else
      rm -f /etc/systemd/system/kite.service
    fi
  fi
  log "Uninstalled Kite. Data directory '$DATA_DIR' was preserved."
  exit 0
fi

if [ -n "$FORCE_RID" ]; then
  RID="$FORCE_RID"
else
  ARCH="$(uname -m)"
  case "$ARCH" in
    x86_64|amd64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) die "Unsupported CPU architecture '$ARCH'. Supported: x86_64, aarch64." ;;
  esac
fi

command -v tar >/dev/null 2>&1 || die "'tar' is required."

download_file() {
  local url="$1"
  local dest="$2"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url" -o "$dest"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$dest" "$url"
  else
    die "Neither 'curl' nor 'wget' was found."
  fi
}

fetch_text() {
  local url="$1"
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL "$url"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO- "$url"
  else
    die "Neither 'curl' nor 'wget' was found."
  fi
}

TMP_DIR="$(mktemp -d)"
cleanup() { rm -rf "$TMP_DIR"; }
trap cleanup EXIT

if [ -n "$ARCHIVE_PATH" ]; then
  [ -f "$ARCHIVE_PATH" ] || die "Archive not found: $ARCHIVE_PATH"
  TARBALL="$ARCHIVE_PATH"
  ARCHIVE_NAME="$(basename "$ARCHIVE_PATH")"
  SUMS_LOCAL="$CHECKSUM_FILE"
else
  if [ "$VERSION" = "latest" ]; then
    log "Resolving latest Kite release version..."
    TAG="$(fetch_text "https://api.github.com/repos/$REPO/releases/latest" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n1)"
    [ -n "$TAG" ] || die "Failed to determine latest release tag from GitHub API. Pass --version v1.0.0 explicitly."
  else
    case "$VERSION" in
      v*) TAG="$VERSION" ;;
      *) TAG="v$VERSION" ;;
    esac
  fi

  ARCHIVE_NAME="kite-${TAG}-${RID}.tar.gz"
  BASE_URL="https://github.com/$REPO/releases/download/${TAG}"
  TARBALL="$TMP_DIR/$ARCHIVE_NAME"
  SUMS_LOCAL="$TMP_DIR/SHA256SUMS.txt"

  log "Downloading $ARCHIVE_NAME..."
  download_file "$BASE_URL/$ARCHIVE_NAME" "$TARBALL"

  if [ "$SKIP_VERIFY" -eq 0 ]; then
    download_file "$BASE_URL/SHA256SUMS.txt" "$SUMS_LOCAL" || warn "SHA256SUMS.txt unavailable; skipping verification."
  fi
fi

if [ "$SKIP_VERIFY" -eq 0 ] && [ -n "${SUMS_LOCAL:-}" ] && [ -f "$SUMS_LOCAL" ]; then
  EXPECTED_HASH="$(tr -d '\r' < "$SUMS_LOCAL" | awk -v target="$ARCHIVE_NAME" '$2 == target {print $1; exit}')"
  if [ -n "$EXPECTED_HASH" ]; then
    if command -v sha256sum >/dev/null 2>&1; then
      ACTUAL_HASH="$(sha256sum "$TARBALL" | awk '{print $1}')"
    elif command -v shasum >/dev/null 2>&1; then
      ACTUAL_HASH="$(shasum -a 256 "$TARBALL" | awk '{print $1}')"
    else
      die "Neither sha256sum nor shasum found to verify archive."
    fi
    [ "$ACTUAL_HASH" = "$EXPECTED_HASH" ] || die "SHA-256 mismatch for $ARCHIVE_NAME (expected $EXPECTED_HASH, got $ACTUAL_HASH)."
    log "Verified SHA-256 checksum for $ARCHIVE_NAME."
  else
    warn "No checksum entry for '$ARCHIVE_NAME' found in $SUMS_LOCAL."
  fi
fi

log "Installing Kite into $INSTALL_APP_DIR..."
STAGE_EXTRACT="$TMP_DIR/extract"
mkdir -p "$STAGE_EXTRACT"
tar -xzf "$TARBALL" -C "$STAGE_EXTRACT"

rm -rf "$INSTALL_APP_DIR"
mkdir -p "$INSTALL_APP_DIR" "$BIN_DIR" "$DATA_DIR"
cp -a "$STAGE_EXTRACT"/. "$INSTALL_APP_DIR"/

for bin in kite Kite.Server Kite.Worker kite-server; do
  if [ -f "$INSTALL_APP_DIR/$bin" ]; then
    chmod 0755 "$INSTALL_APP_DIR/$bin"
  fi
done

# Create tiny exec wrappers in BIN_DIR so AppContext.BaseDirectory and co-located binaries always resolve cleanly
cat > "$BIN_DIR/kite" <<EOF
#!/usr/bin/env sh
exec "$INSTALL_APP_DIR/kite" "\$@"
EOF
chmod 0755 "$BIN_DIR/kite"

cat > "$BIN_DIR/kite-server" <<EOF
#!/usr/bin/env sh
exec "$INSTALL_APP_DIR/Kite.Server" "\$@"
EOF
chmod 0755 "$BIN_DIR/kite-server"

cat > "$BIN_DIR/Kite.Server" <<EOF
#!/usr/bin/env sh
exec "$INSTALL_APP_DIR/Kite.Server" "\$@"
EOF
chmod 0755 "$BIN_DIR/Kite.Server"

if [ "$SYSTEM_INSTALL" -eq 1 ] && [ -n "${ETC_DIR:-}" ]; then
  mkdir -p "$ETC_DIR"
fi

if [ "$GRANT_LOW_PORTS" -eq 1 ]; then
  if command -v setcap >/dev/null 2>&1; then
    setcap 'cap_net_bind_service=+ep' "$INSTALL_APP_DIR/kite"
    setcap 'cap_net_bind_service=+ep' "$INSTALL_APP_DIR/Kite.Server"
    log "Granted cap_net_bind_service=+ep to kite and Kite.Server."
  else
    warn "'setcap' (libcap2-bin) not found; skipped capability assignment."
  fi
fi

if [ "$WITH_SYSTEMD" -eq 1 ]; then
  if ! id -u kite >/dev/null 2>&1; then
    useradd --system --no-create-home --shell /usr/sbin/nologin kite
    log "Created system user 'kite'."
  fi
  chown -R kite:kite "$DATA_DIR"
  cat > /etc/systemd/system/kite.service <<EOF
[Unit]
Description=Kite Edge Server (.NET 10)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=kite
Group=kite
Environment=KITE_HOME=$DATA_DIR
ExecStart=$BIN_DIR/kite run --home $DATA_DIR
Restart=on-failure
RestartSec=3
LimitNOFILE=1048576
AmbientCapabilities=CAP_NET_BIND_SERVICE
CapabilityBoundingSet=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
EOF
  if command -v systemctl >/dev/null 2>&1; then
    systemctl daemon-reload
    log "Installed systemd unit at /etc/systemd/system/kite.service."
    log "Enable and start with: sudo systemctl enable --now kite"
  fi
fi

if [ "$SYSTEM_INSTALL" -eq 0 ] && [ "$NO_MODIFY_PATH" -eq 0 ]; then
  case ":${PATH}:" in
    *":${BIN_DIR}:"*) ;;
    *)
      EXPORT_LINE="export PATH=\"$BIN_DIR:\$PATH\""
      UPDATED_PROFILES=""
      for rc in "$HOME/.profile" "$HOME/.bashrc" "$HOME/.zshrc"; do
        if [ -f "$rc" ] || [ "$rc" = "$HOME/.profile" ]; then
          touch "$rc"
          if ! grep -Fq "$BIN_DIR" "$rc" 2>/dev/null; then
            printf '\n# Added by Kite installer\n%s\n' "$EXPORT_LINE" >> "$rc"
            UPDATED_PROFILES="$UPDATED_PROFILES $rc"
          fi
        fi
      done
      if [ -n "$UPDATED_PROFILES" ]; then
        log "Added $BIN_DIR to PATH in:$UPDATED_PROFILES"
        log "Reload your shell or run: export PATH=\"$BIN_DIR:\$PATH\""
      fi
      ;;
  esac
fi

log "Installed successfully:"
"$BIN_DIR/kite" --version || true
log "Run 'kite paths' to inspect directories or 'kite init && kite run' to start."
