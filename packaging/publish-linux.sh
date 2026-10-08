#!/usr/bin/env bash
# Builds a self-contained Linux tarball, FFmpeg included:
#   artifacts/linux/myvideoeditor-<version>-linux-<arch>.tar.gz
# Usage: packaging/publish-linux.sh [version] [x64|arm64]
# Set FFMPEG_DIR to use an FFmpeg folder already fetched by tools/fetch-ffmpeg.sh.
set -euo pipefail

VERSION="${1:-0.1.0}"
ARCH="${2:-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/linux"
STAGE="$OUT/myvideoeditor-$VERSION-linux-$ARCH"

rm -rf "$STAGE"
dotnet publish "$ROOT/src/MyVideoEditor.App/MyVideoEditor.App.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:Version="$VERSION" -o "$STAGE/lib"

# FFmpeg (GPL build) next to the app, where it's found first.
if [[ -n "${FFMPEG_DIR:-}" ]]; then
  cp -a "$FFMPEG_DIR" "$STAGE/lib/ffmpeg"
else
  "$ROOT/tools/fetch-ffmpeg.sh" "linux-$ARCH" "$STAGE/lib/ffmpeg"
fi
"$STAGE/lib/ffmpeg/bin/ffmpeg" -hide_banner -version | head -1

# Licence texts for everything shipped (shown in the app under Help → Licences).
python3 "$ROOT/tools/generate-third-party-notices.py" --rid "linux-$ARCH" --ffmpeg "$STAGE/lib/ffmpeg" --output "$STAGE/lib/THIRD-PARTY-NOTICES.txt"
cp "$ROOT/LICENSE" "$STAGE/lib/LICENSE.txt"
cp "$STAGE/lib/THIRD-PARTY-NOTICES.txt" "$STAGE/lib/LICENSE.txt" "$STAGE/"

cp "$ROOT/src/MyVideoEditor.App/Assets/myvideoeditor.png" "$STAGE/myvideoeditor.png"

cat > "$STAGE/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Installs for the current user into ~/.local
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
APPS=~/.local/share/applications
ICONS=~/.local/share/icons/hicolor/256x256/apps
mkdir -p ~/.local/lib ~/.local/bin "$APPS" "$ICONS"
rm -rf ~/.local/lib/myvideoeditor && cp -r "$HERE/lib" ~/.local/lib/myvideoeditor
ln -sf ~/.local/lib/myvideoeditor/myvideoeditor ~/.local/bin/myvideoeditor

# The icon goes in first: the desktop reads a new menu entry at once, and if its icon isn't there
# yet it shows a generic one until the next login. Exec and Icon are absolute, so the entry works
# even when ~/.local/bin isn't on the session's PATH (often the case on a first install).
cp "$HERE/myvideoeditor.png" "$ICONS/myvideoeditor.png"
touch ~/.local/share/icons/hicolor
cat > "$APPS/myvideoeditor.desktop.tmp" <<DESKTOP
[Desktop Entry]
Type=Application
Name=MyVideoEditor
GenericName=Video Editor
Comment=Edit, convert, upscale and record video
Exec=$HOME/.local/lib/myvideoeditor/myvideoeditor %F
Icon=$ICONS/myvideoeditor.png
StartupWMClass=myvideoeditor
Terminal=false
Categories=AudioVideo;Video;AudioVideoEditing;
MimeType=video/mp4;video/x-matroska;video/webm;video/quicktime;video/x-msvideo;video/mpeg;
Keywords=video;editor;convert;upscale;screen;record;capture;annotate;
DESKTOP
mv "$APPS/myvideoeditor.desktop.tmp" "$APPS/myvideoeditor.desktop"
if command -v update-desktop-database >/dev/null; then update-desktop-database "$APPS" 2>/dev/null || true; fi
echo "Installed. Run 'myvideoeditor' or find MyVideoEditor in your applications menu."
INSTALL
chmod +x "$STAGE/install.sh"

tar -C "$OUT" -czf "$STAGE.tar.gz" "$(basename "$STAGE")"
echo "Created $STAGE.tar.gz"
