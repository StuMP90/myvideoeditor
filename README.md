# MyVideoEditor

A cross-platform (Windows + Linux) multi-track video editor, converter and screen recorder. Edit
video and audio on a timeline, add titles and effects, convert between formats, resize and upscale,
and record windows or screens and annotate them for bug reports and demos. Built with C#/.NET 10
and [Avalonia](https://avaloniaui.net/) on top of [FFmpeg](https://ffmpeg.org), MIT licensed, and
packaged as MSIX for the Microsoft Store and as a self-contained Linux tarball.

## Get MyVideoEditor

- **Windows:** the MSIX from the [latest release](https://github.com/StuMP90/myvideoeditor/releases/latest)
  (Microsoft Store listing to follow).
- **Linux (x64):** download `myvideoeditor-<version>-linux-x64.tar.gz` from the latest release,
  extract it and run `./install.sh`. That installs it for your user under `~/.local` and adds
  MyVideoEditor to your applications menu.

Both include FFmpeg, so there's nothing else to install.

## Features

### Edit

- **Multi-track timeline**: any number of video and audio tracks. Upper video tracks are drawn in
  front of lower ones (picture-in-picture, overlays, logos). Each track can be muted (**M**),
  hidden (**H**) or locked (**L**).
- **Editing**: drag clips to move them (also between tracks), drag their edges to trim, split at
  the playhead (**S**), delete or ripple-delete (close the gap), duplicate, multi-select and move
  together, detach a video's sound to its own track. Clips snap to clip edges and the playhead.
  Placing a clip over another trims or splits what's underneath, like most editors. Everything is
  undoable.
- **Clips**: fade in/out, cross-dissolves between touching clips, opacity, volume (to 400%),
  speed (0.1×–16×, pitch kept), fit / fill / stretch / original size, scale, position, rotation
  and cropping. Drag a video in the preview to reposition it.
- **Titles**: title, title card, lower third, caption, chapter and end-card styles; any installed
  font, size, colour, outline, shadow, background box, full-frame background. Drag them in the
  preview. Text sizes are relative to the frame, so a title looks the same in 720p and 4K.
- **Effects** (stackable, per clip): colour correction (brightness, contrast, saturation, gamma),
  colour temperature, black and white, sepia, invert, blur, sharpen, vignette, noise reduction,
  stabilisation, mirror/flip, green screen (chroma key). For sound: loudness normalisation, noise
  reduction, rumble and hiss filters, bass, treble, compressor and echo.
- **Preview**: plays the timeline with sound, rendered by the same engine as the export, so what
  you see is what you get. Frame stepping, jump to cuts, scrubbing on the ruler.
- **Projects**: saved as `.mveproj` (JSON) that refer to your media. Media moved together with a
  project is found again. Unsaved work is autosaved every minute and offered back after a crash.
  The first video added sets the project's size and frame rate; presets include 4K, 1080p, 720p,
  vertical (Shorts/Reels/TikTok) and square.
- **Export**: MP4 (H.264, H.265, AV1), WebM (VP9, AV1), MKV, MOV (ProRes 422), animated GIF (with
  a proper palette), and audio only (MP3, M4A, WAV, FLAC, Opus). Three quality levels, any output
  size (exporting larger upscales with Lanczos; titles are redrawn sharp at the new size), and
  part of the timeline only. Single frames save as PNG.

### Convert

A batch queue for files that don't need a timeline: change format; resize with **fit** (black
bars), **fill** (crop) or **stretch**; upscale (below); change the frame rate; trim; rotate;
deinterlace; remove the sound; or copy streams into a new container without re-encoding (fast and
lossless). Originals are never changed.

### Upscaling for every kind of PC

All methods are free and run on your own computer; nothing is sent anywhere. They're grouped by
what they need, and **Test speed on this PC** times each one on a few frames of your own video,
estimates the whole job, and recommends the best that finishes within an hour for the kind of
video (camera footage, or animation and screen recordings).

| Method | Runs on | Best for | 360p→720p on an Intel Iris Xe laptop |
|---|---|---|---|
| Lanczos, bicubic, spline, Enhanced | CPU | anything; fast | ~30 ms per frame |
| **Sharper detail** (FSRCNNX shader via libplacebo) | any Vulkan GPU, integrated is fine | camera footage | ~0.1 s per frame |
| **Cleaner lines** (Anime4K shaders via libplacebo) | any Vulkan GPU | animation, screen recordings | ~0.1 s per frame |
| **AI: animation and screen** (Real-ESRGAN animevideov3) | Vulkan GPU | animation, screen recordings | ~1.5 s per frame |
| **AI: anime, strongest** (Real-CUGAN) | Vulkan GPU | anime | ~8 s per frame |
| **AI: photo detail** (Real-ESRGAN x4plus) | dedicated (gaming) GPU | camera footage | ~60 s per frame |

- The GPU and AI components are downloaded on first use (70 KB to 47 MB, after asking), from each
  project's GitHub release, pinned to an exact file and SHA-256 checksum. They're kept in the data
  folder under `tools/`. Licences: FSRCNNX GPL-3.0, Anime4K MIT, Real-ESRGAN BSD-3-Clause,
  Real-CUGAN MIT. They aren't bundled, so they don't add to the download for people who won't use
  them.
- The GPU is found through Vulkan: a dedicated card is preferred, then integrated graphics.
  Software renderers (llvmpipe) are never used for real work.
- AI methods work on frames in batches sized to the free disk space, piped straight back into the
  encoder, so even long videos need only about 1.5 GB of working space. If the graphics card runs
  out of memory, smaller tiles are tried automatically. Before a long AI job the app estimates the
  time and asks.
- GPU and AI methods are only used when the picture gets bigger; otherwise Lanczos is used.
- How they compare: on camera footage, FSRCNNX gives slightly crisper edges than Lanczos; the AI
  models designed for animation turn camera footage into a painted look, so they're offered for
  animation. On animation, Real-ESRGAN's video model was clearly the best (clean lines, blur
  removed). Fidelity scores (SSIM/PSNR) always favour plain Lanczos and VMAF rewards any sharpening,
  so the choice and the tests rely on what each method is for, not on beating Lanczos at a score.

### Capture and annotate

- **Record** a window, a whole screen or a dragged-out region, with the mouse pointer and
  optionally the microphone and/or the computer's sound, with a countdown. The app can hide itself
  while recording and shows a small always-on-top Stop button. Recordings go to
  `Videos/MyVideoEditor` as MP4. They are written as Matroska while recording, so a crash mid-way
  still leaves a playable file, then remuxed to MP4 (no quality loss) when stopped.
- **Screenshots** of the same targets, as PNG.
- **Annotate**: *Edit & annotate* opens a recording or screenshot in the editor. Pick a tool and
  drag on the preview: **arrow**, **box**, **circle**, **highlight**, **spotlight** (darken
  everything else), **callout**, numbered **step** markers, **blur** and **pixelate** (hide
  passwords and personal details), and **zoom** into an area. Each annotation is a clip with its
  own timing, fades and settings; drag it in the preview to move it and drag its handles to resize.
  Export a small MP4 or GIF for the ticket, or a PNG for a screenshot.

## How it works

```
src/MyVideoEditor.Core/  No UI: model, editing, rendering, export, conversion, capture, playback
  Model/                 Project, tracks, clips (media, title, annotation), effects; JSON format
  Editing/               Undoable timeline operations (EditSession, TimelineEditor)
  Rendering/             RenderPlanBuilder: project → FFmpeg inputs + filter graph; effect catalogue
  Graphics/              Titles and annotations drawn with SkiaSharp into transparent PNGs
  Export/ Conversion/    Export formats and encoders; single-file converter; AI upscaler hook
  Capture/               Screen/window/region recording (x11grab / gdigrab), window and sound device lists
  Playback/              Preview engine: raw frames and samples streamed from FFmpeg
  Media/                 FFmpeg discovery and runner, ffprobe, thumbnails and waveforms
src/MyVideoEditor.App/   Avalonia app: views, view models, timeline and preview controls, OpenAL sound
tests/                   Core tests (render real media and check pixels and sound), headless UI tests
tools/ packaging/        FFmpeg download, licence notices, icons; MSIX and Linux tarball scripts
```

- **One compiler, three uses.** `RenderPlanBuilder` turns a project, or any time range of it,
  into FFmpeg inputs and one filter graph. Export, preview playback and single preview frames all
  use it, so the preview matches the export. Each clip is opened with `-ss`/`-t` at exactly the
  part needed, so previewing an hour into a long recording doesn't decode the hour before it.
- **Titles and annotations** are drawn with SkiaSharp (the library Avalonia renders with) into
  transparent PNGs, cached by content, and overlaid. Fonts look identical on Windows and Linux.
  Blur, pixelate and zoom are FFmpeg filters applied to the picture beneath them.
- **Preview** reads raw BGRA frames and float samples from FFmpeg over pipes, renders at up to
  720p for smoothness, plays sound through OpenAL Soft (bundled) and uses the sound's position as
  the clock, dropping late frames. Without a sound device it runs silently on a stopwatch.
- **FFmpeg** runs as a separate program, with arguments passed as a list (never through a shell).
  Cancelling an export stops it cleanly and removes the partial file; exports and conversions are
  written under a temporary name and renamed when complete.
- **Undo** keeps a snapshot of the project (small JSON) before each change; dragging a slider
  undoes as one step.

## FFmpeg and licensing

MyVideoEditor is MIT-licensed. Release builds bundle a **GPL** build of FFmpeg (the shared
`gpl-shared` builds from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), linked from
ffmpeg.org), which includes x264 and x265 for the best H.264/H.265 quality. FFmpeg runs as separate
programs, so the app's own licence isn't affected. The package's `ffmpeg/` folder has FFmpeg's
licence (`FFMPEG-LICENSE.txt`) and `FFMPEG-SOURCE.txt`, which records the exact build and where its
complete source is, as the GPL requires; the same information is in `THIRD-PARTY-NOTICES.txt`
(**Help → Licences** in the app). The FFmpeg version is set by `FFMPEG_SERIES` in
`tools/fetch-ffmpeg.sh` and `-FfmpegSeries` in `packaging/build-msix.ps1` (currently 8.1).

FFmpeg is found in this order: `MYVIDEOEDITOR_FFMPEG_DIR`, the `ffmpeg` folder next to the app,
then `PATH`. Development builds normally use the system's ffmpeg (6.1 or later).

## Building and running

Requires the .NET 10 SDK (`global.json` asks for 10.0.100 or a later 10.0 feature band) and ffmpeg
on `PATH` (Ubuntu: `sudo apt install dotnet-sdk-10.0 ffmpeg`; Windows: `winget install
Microsoft.DotNet.SDK.10 Gyan.FFmpeg`), or `MYVIDEOEDITOR_FFMPEG_DIR` pointing at a folder with
ffmpeg and ffprobe (`tools/fetch-ffmpeg.sh linux-x64 ~/ffmpeg` fetches the release build into
`~/ffmpeg/bin`).

```bash
dotnet run --project src/MyVideoEditor.App
```

Upscaling components download into the data folder's `tools/` (override with
`MYVIDEOEDITOR_TOOLS_DIR`).

## Tests

```bash
dotnet test
```

- `MyVideoEditor.Core.Tests` generates its own media with FFmpeg's test sources (nothing to
  download), builds projects, exports them and checks the result: pixel colours where clips, gaps,
  overlays, titles, dissolves, fades, effects and stills should be; silence and sound where audio
  was placed or muted; durations after speed changes and range exports; GIF, WebM and MP3 output;
  conversions (resize modes, upscaling, trim, rotate, remux); every upscaling method on the GPU
  (correct size, every frame kept, colours kept, a picture resembling the source), the AI pipeline
  with a stand-in program (batches, a 29.97 fps source kept in sync, sound, tile fallback after an
  out-of-memory failure, clean-up on cancel), GPU detection, checksum-verified installs, paths with
  awkward characters, the speed test and its cache; preview playback in real time with a
  sound clock; still frames; thumbnails and waveforms; editing rules (overwrite, split, trims within
  the footage and neighbours, ripple delete, locked tracks, detach audio, undo merging); project
  files. Capture tests record the real X display when there is one (CI uses Xvfb). GPU tests use
  the computer's GPU; CI has none, so it sets `MYVIDEOEDITOR_ALLOW_SOFTWARE_GPU=1` and runs them on
  Mesa's software Vulkan (llvmpipe), slowly but end to end.
- `MyVideoEditor.App.Tests` drives the real UI headlessly: importing, the timeline, the inspector,
  titles and annotations (including drawing one on the preview with the mouse), keyboard shortcuts,
  the export dialog, the convert queue, the capture tab, saving and reopening, and the OpenAL sound
  output. Set `MYVIDEOEDITOR_SCREENSHOT_DIR` to keep screenshots.
- Tests use their own data folder (`myvideoeditor-test-data` in the temp folder), so they never
  touch your settings or logs. Set `MYVIDEOEDITOR_FFMPEG_DIR` to test against a particular FFmpeg
  build; CI uses the bundled one.

## Packaging

Version numbers come from git tags, as in zSSH: push a tag like `v1.0.7` and the workflow
(`.github/workflows/build.yml`) runs the tests, builds that version and creates the GitHub release
with the binaries attached:

- `MyVideoEditor_1.0.7.0_x64.msix` and `MyVideoEditor_1.0.7.0_arm64.msix`
- `myvideoeditor-1.0.7-linux-x64.tar.gz`

The tag must be `vMAJOR.MINOR.PATCH`. Other pushes build `0.0.<run number>` as workflow artifacts
only. Local builds report `0.1.0`.

```bash
packaging/publish-linux.sh 0.1.0 x64      # → artifacts/linux/myvideoeditor-0.1.0-linux-x64.tar.gz
```

```powershell
# Windows, with the Windows 10/11 SDK. Store submission (the Store signs it):
./packaging/build-msix.ps1 -Version 1.0.0.0 -Architecture x64,arm64 `
    -IdentityName "<Package/Identity/Name>" -Publisher "<Package/Identity/Publisher>" `
    -PublisherDisplayName "<PublisherDisplayName>"
# Local sideload test: ./packaging/build-msix.ps1 -SelfSign
```

For Store-ready packages from CI, set the repository variables `MSIX_IDENTITY_NAME`,
`MSIX_PUBLISHER` and `MSIX_PUBLISHER_DISPLAY_NAME` to the values from Partner Center. The icons
and Store logos come from `tools/make_icons.py`.

## Keyboard

The same list is in the app: **Help**, or **F1**.

| Keys | Action |
|---|---|
| Space | Play / pause |
| ← / → | Previous / next frame |
| Shift+← / Shift+→ | Back / forward one second |
| ↑ / ↓ | Previous / next cut |
| Home / End | Start / end |
| S | Split at the playhead |
| Delete / Shift+Delete | Delete / ripple delete |
| T | Add a title |
| Esc | Put the annotation tool down / clear the selection |
| Ctrl+Z / Ctrl+Shift+Z or Ctrl+Y | Undo / redo |
| Ctrl+D | Duplicate |
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S | New / open / save / save as |
| Ctrl+I | Import media |
| Ctrl+E | Export |
| Ctrl+wheel, Ctrl+= / Ctrl+− / Ctrl+0 | Zoom the timeline / fit |
| Shift+wheel | Scroll tracks up and down |
| Alt while dragging | Don't snap |

## Privacy

MyVideoEditor collects nothing. Its only network use is downloading an optional upscaler when you
ask for one. Recordings happen only when you start them. See the [privacy policy](PRIVACY.md).

## Known limitations and roadmap

- **Wayland**: recording uses X11, so in a Wayland session only XWayland apps can be captured.
  Use an Xorg session to record everything. A PipeWire/portal capture path is on the roadmap.
- **Windows system sound** can only be recorded through a loopback device such as Stereo Mix;
  native WASAPI loopback is on the roadmap. Window capture on Windows finds the window by its title
  when recording starts.
- No keyframe animation yet: position, scale and zoom are fixed for a clip's length (with fades).
  Dissolves are the only transition.
- The preview renders at up to 720p; very complex projects may drop preview frames (export is
  unaffected). Editing a title or annotation re-renders the preview when the mouse is released.
- Encoding is software only (x264/x265/SVT-AV1); hardware encoders aren't used yet.
- The Windows build is compiled and packaged by CI but isn't exercised by the automated tests,
  which run on Linux.

## Licence

MyVideoEditor is released under the [MIT License](LICENSE). Bundled components keep their own
licences, chiefly MIT (Avalonia, SkiaSharp, CommunityToolkit.Mvvm, Silk.NET), LGPL (OpenAL Soft)
and GPL (FFmpeg, run as separate programs). Release builds include `THIRD-PARTY-NOTICES.txt` with
every bundled component's copyright and licence, generated per platform by
`tools/generate-third-party-notices.py`; the build fails if any package has no licence text.
