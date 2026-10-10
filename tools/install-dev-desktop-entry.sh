#!/usr/bin/env bash
# Gives `dotnet run` builds the MyVideoEditor icon in the Linux dock and window switcher.
#
# GNOME (and most other desktops) take a window's icon from the menu entry whose StartupWMClass
# matches the window's class, "myvideoeditor". An installed copy has that entry (publish-linux.sh
# writes it); a dev run from the repository doesn't, so it shows a generic icon. This writes a
# separate "MyVideoEditor (dev)" entry that runs the Debug build. It's harmless alongside an
# installed copy. Remove it with: tools/install-dev-desktop-entry.sh --remove
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APPS=~/.local/share/applications
ENTRY="$APPS/myvideoeditor-dev.desktop"

if [[ "${1:-}" == "--remove" ]]; then
    rm -f "$ENTRY"
    echo "Removed $ENTRY"
    exit 0
fi

mkdir -p "$APPS"
cat > "$ENTRY.tmp" <<DESKTOP
[Desktop Entry]
Type=Application
Name=MyVideoEditor (dev)
Comment=MyVideoEditor built from $REPO
Exec=dotnet run --project "$REPO/src/MyVideoEditor.App" -- %F
Path=$REPO
Icon=$REPO/src/MyVideoEditor.App/Assets/myvideoeditor.png
StartupWMClass=myvideoeditor
Terminal=false
Categories=AudioVideo;Video;AudioVideoEditing;
DESKTOP
mv "$ENTRY.tmp" "$ENTRY"
if command -v update-desktop-database >/dev/null; then update-desktop-database "$APPS" 2>/dev/null || true; fi
echo "Wrote $ENTRY. Dev runs now show the MyVideoEditor icon (restart a running copy to see it)."
