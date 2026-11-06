#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PREFIX="${PREFIX:-$HOME/.local}"
DEST="$PREFIX/opt/tuxviewer"
SELF_CONTAINED="${SELF_CONTAINED:-false}"

echo "==> publishing TuxViewer ($(date '+%F %T'))"
dotnet publish src/TuxViewer -c Release -r linux-x64 --self-contained "$SELF_CONTAINED" -o "$ROOT/dist"

echo "==> installing to $DEST"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$ROOT/dist/." "$DEST/"

install -d "$PREFIX/bin"
ln -sfn "$DEST/TuxViewer" "$PREFIX/bin/tuxviewer"

install -Dm644 "$ROOT/packaging/tuxviewer.desktop" "$PREFIX/share/applications/tuxviewer.desktop"
install -Dm644 "$ROOT/packaging/tuxviewer.svg" "$PREFIX/share/icons/hicolor/scalable/apps/tuxviewer.svg"

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$PREFIX/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo "==> installed: $PREFIX/bin/tuxviewer"
echo "    desktop entry: $PREFIX/share/applications/tuxviewer.desktop"
