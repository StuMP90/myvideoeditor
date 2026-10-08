#!/usr/bin/env bash
# Downloads a GPL build of FFmpeg (BtbN/FFmpeg-Builds, the builds linked from ffmpeg.org) and
# puts ffmpeg and ffprobe into <destination>/bin, their shared libraries into
# <destination>/lib (Linux; on Windows the DLLs sit next to the .exe files), and FFmpeg's
# licence into <destination>. The shared build is used because ffmpeg and ffprobe then share
# one copy of the libraries (~200 MB instead of ~350 MB for two static programs). The Linux
# programs find their libraries through their RPATH ($ORIGIN/../lib).
#
# Usage: tools/fetch-ffmpeg.sh <linux-x64|linux-arm64|win-x64|win-arm64> <destination>
# Environment: FFMPEG_SERIES (default 8.1), FFMPEG_URL (overrides the download URL entirely).
#
# The app runs ffmpeg as a separate program, so MyVideoEditor stays MIT-licensed while
# shipping FFmpeg under its own GPL. The packaging scripts add FFMPEG-SOURCE.txt, which says
# exactly which FFmpeg version is included and where its source is.
set -euo pipefail

RID="${1:?runtime identifier, e.g. linux-x64}"
DEST="${2:?destination folder}"
SERIES="${FFMPEG_SERIES:-8.1}"

case "$RID" in
  linux-x64)   NAME="ffmpeg-n${SERIES}-latest-linux64-gpl-shared-${SERIES}.tar.xz" ;;
  linux-arm64) NAME="ffmpeg-n${SERIES}-latest-linuxarm64-gpl-shared-${SERIES}.tar.xz" ;;
  win-x64)     NAME="ffmpeg-n${SERIES}-latest-win64-gpl-shared-${SERIES}.zip" ;;
  win-arm64)   NAME="ffmpeg-n${SERIES}-latest-winarm64-gpl-shared-${SERIES}.zip" ;;
  *) echo "Unsupported runtime: $RID" >&2; exit 1 ;;
esac
URL="${FFMPEG_URL:-https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$NAME}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
echo "Downloading $URL"
curl -fL --retry 3 -o "$WORK/$NAME" "$URL"
mkdir -p "$WORK/x"
case "$NAME" in
  *.tar.xz) tar -C "$WORK/x" -xf "$WORK/$NAME" ;;
  *.zip)    unzip -q "$WORK/$NAME" -d "$WORK/x" ;;
esac
BIN="$(dirname "$(find "$WORK/x" -type f \( -name ffmpeg -o -name ffmpeg.exe \) | head -1)")"
ROOT="$(dirname "$BIN")"

rm -rf "$DEST"
mkdir -p "$DEST/bin"
EXT=""
[[ "$RID" == win-* ]] && EXT=".exe"
cp "$BIN/ffmpeg$EXT" "$BIN/ffprobe$EXT" "$DEST/bin/"
chmod +x "$DEST/bin/ffmpeg$EXT" "$DEST/bin/ffprobe$EXT"
if [[ "$RID" == win-* ]]; then
  cp "$BIN"/*.dll "$DEST/bin/"
else
  # The versioned libraries and their soname links (libavcodec.so.62 -> libavcodec.so.62.x.y).
  mkdir -p "$DEST/lib"
  cp -a "$ROOT"/lib/*.so.* "$DEST/lib/"
fi
cp "$ROOT/LICENSE.txt" "$DEST/FFMPEG-LICENSE.txt"

# Which FFmpeg this is, and where its source is (the GPL requires it to be available).
VERSION="$(basename "$ROOT")"
cat > "$DEST/FFMPEG-SOURCE.txt" <<TXT
This folder contains ffmpeg, ffprobe and their libraries from FFmpeg (https://ffmpeg.org), licensed under the
GNU General Public License version 3 (see FFMPEG-LICENSE.txt). MyVideoEditor runs them as
separate programs; MyVideoEditor itself is MIT-licensed.

Build:  $VERSION
        from $URL
        built by https://github.com/BtbN/FFmpeg-Builds (build scripts and the exact
        configuration of every bundled library are in that repository)
Source: FFmpeg release branch release/$SERIES at https://github.com/FFmpeg/FFmpeg/tree/release/$SERIES
        (also https://ffmpeg.org/download.html#releases)

You may obtain the complete corresponding source code of this FFmpeg build, including the
libraries it was built with, from the locations above. If they are unavailable, ask via
https://github.com/StuMP90/myvideoeditor/issues and it will be provided, for at least three
years after this release.
TXT
echo "FFmpeg ($VERSION) for $RID is in $DEST"
