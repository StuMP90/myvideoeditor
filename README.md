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
- **Clips**: fade in/out, transitions between touching clips (dissolve, dip to black or white,
  wipe, soft wipe, push and cover in four directions, circle open/close, clock wipe, barn doors,
  zoom, pixelate, blur; sound always crossfades), opacity, volume (to 400%),
  speed (0.1×–16×, pitch kept), fit / fill / stretch / original size, scale, position, rotation
  and cropping. Drag a video in the preview to reposition it.
- **Titles**: title, title card, lower third, caption, chapter and end-card styles; any installed
  font, size, colour, outline, shadow, background box, full-frame background. Drag them in the
  preview. Text sizes are relative to the frame, so a title looks the same in 720p and 4K.
  A **picture** (a logo, say) can sit above, below, left or right of the text, or fill the frame
  behind it. **Animations** in and out: slide from any side, rise/sink, grow/shrink, wipe,
  typewriter, word by word, line by line, each with its own length (drawn frame by frame with the
  same code as still titles, so the preview matches the export). **Scrolling** titles, with
  templates: an end **credits roll** (lines written `Role | Name` form two columns), a Star
  Wars-style **opening crawl** (justified text receding into the distance and fading), and a news
  **ticker**, which can loop seamlessly for as long as the title lasts (stretch it over the video:
  one click does it). A speed setting (pixels per second) is independent of the length; "Fit" scrolls
  once over the title's length. The text is drawn once as a long strip and ffmpeg
  moves a frame-sized window along it (tilting it back for the crawl), so long credits stay fast.
- **Lottie animations** (`.json` or `.lottie`) import like any other media and play on a video
  track over the footage; their text layers can be given new text per clip in its settings.
  Pictures inside an animation (image layers) aren't shown yet, only its shapes and text: the app
  says so when importing one (SkiaSharp 2.88's Lottie player can't load images). The
  app ships no animations (to avoid licensing questions): use your own, or ones whose licence
  allows it, made for example with After Effects + Bodymovin or the free Glaxnimate.
- **Effects** (stackable, per clip): colour correction (brightness, contrast, saturation, gamma),
  colour temperature, black and white, sepia, invert, blur, sharpen, vignette, noise reduction,
  stabilisation, mirror/flip, green screen (chroma key). For sound: loudness normalisation, noise
  reduction, rumble and hiss filters, bass, treble, compressor and echo.
- **Preview**: plays the timeline with sound, rendered by the same engine as the export, so what
  you see is what you get. Frame stepping, jump to cuts, scrubbing on the ruler.
- **Projects**: saved as `.mveproj` (JSON) that refer to your media. Media moved together with a
  project is found again. Unsaved work is autosaved every minute and offered back after a crash.
  The first video added sets the project's size and frame rate; presets include 4K, 1080p, 720p,
  DVD (NTSC 720×480 and PAL 720×576, in 4:3 and 16:9), vertical (Shorts/Reels/TikTok) and square.
  DVD video has non-square pixels: the project is edited at the size it's shown (640×480 for NTSC
  4:3, 854×480 for 16:9, 768×576 and 1024×576 for PAL) and exported at 720 across, marked to be
  shown at that size. A DVD clip added first sets this up by itself.
- **Export**: MP4 (H.264, H.265, AV1), WebM (VP9, AV1), MKV, MOV (ProRes 422), **DVD-Video**
  (MPEG-2 and AC-3 sound at DVD's own size and frame rate, NTSC or PAL from the project's frame
  rate, 4:3 or 16:9 from its shape, other shapes with black bars; with dvdauthor installed it also
  makes the disc's VIDEO_TS folder, ready to burn), animated GIF (with a proper palette), and audio only (MP3, M4A, WAV, FLAC, Opus). Three quality levels, any output
  size (exporting larger upscales with Lanczos; titles are redrawn sharp at the new size), and
  part of the timeline only. Single frames save as PNG.

### Convert

A batch queue for files that don't need a timeline: change format; resize with **fit** (black
bars), **fill** (crop) or **stretch**; upscale (below); change the frame rate; trim; rotate;
deinterlace; remove the sound; or copy streams into a new container without re-encoding (fast and
lossless). Originals are never changed. **Preview frame** converts one frame of the selected file
with exactly the current settings and shows it against the original, with a draggable divider or
side by side, zoomable to 100% and 200%, so methods can be compared before committing to a long
conversion.

### Upscaling for every kind of PC

All methods are free and run on your own computer; nothing is sent anywhere. **Test speed on this
PC** times each one on a few frames of your own video, estimates the whole job, and recommends the
best that finishes within an hour for the kind of video (camera footage, or animation and screen
recordings). **Preview frame** shows one frame before and after; **Compare all methods** shows that
frame through every method this computer can run, side by side in a full window, with a shared
zoom (to 400%) and pan. Dragging the frame slider shows the original frame straight away, to find
the moment worth checking.

| Method | Runs on | Best for | 360p→720p on an Intel laptop (Iris Xe, 20 threads) |
|---|---|---|---|
| Lanczos, bicubic, spline, Enhanced | CPU | anything; fast | ~30 ms per frame |
| **Sharper detail** (ArtCNN C4F32, ONNX) | CPU anywhere; graphics card on Windows (DirectML) | anything | ~0.1–0.2 s per frame |
| **Cleaner lines** (Anime4K shaders via libplacebo) | any Vulkan GPU | animation, screen recordings | ~0.1 s per frame |
| **AI: animation and screen** (Real-ESRGAN animevideov3, ONNX) | CPU; graphics card on Windows | animation, screen recordings | ~1 s per frame |
| **AI: realistic detail** (Real-ESRGAN general-x4v3, ONNX) | CPU; graphics card on Windows | camera footage | ~2 s per frame |
| **AI: anime, strongest** (Real-CUGAN, ONNX) | CPU; graphics card on Windows | anime | ~1.5 s per frame |
| **AI: photo detail** (Real-ESRGAN x4plus, ONNX) | CPU; graphics card on Windows; dedicated GPU recommended | camera footage | ~16 s per frame |

- **ArtCNN** is trained to recreate real detail rather than invent it. Measured against the real
  720p frames of a camera clip, it was the most faithful method tested (SSIM 0.9911, PSNR 46.8 dB,
  against Lanczos's 0.9907 / 46.2 dB) and visibly crisper. It replaced FSRCNNX, which scored below
  Lanczos.
- **AI strength** (AI methods): the AI's picture is mixed with a faithful Lanczos enlargement of the
  same frame. GAN-trained models such as Real-ESRGAN invent texture and can paint over real detail;
  30–50% keeps most of the real picture while sharpening edges (on the camera clip, 30% raised
  fidelity from 37.9 to 44.8 dB).
- **ONNX models** (ArtCNN, the two compact Real-ESRGAN models and Real-CUGAN's three, 15.6 MB together) ship with the app
  in a `models` folder and run in-process through ONNX Runtime: on the CPU anywhere, and on Windows
  on any DirectX 12 graphics card through DirectML, falling back to the CPU if that isn't possible.
  `MYVIDEOEDITOR_ONNX_DEVICE=cpu` forces the CPU. On integrated graphics, ONNX Runtime's CPU kernels
  can be faster than the GPU (ArtCNN ran 8–150× faster on the Iris Xe laptop's CPU than as a GPU
  shader). Frames are processed in overlapping tiles, so memory stays bounded at any size. The
  Real-ESRGAN models were converted from the official weights by `tools/convert-realesrgan-onnx.py`,
  and Real-CUGAN's by `tools/convert-realcugan-onnx.py` (checksums pinned); ONNX Runtime uses the DirectML build on Windows and the CPU build elsewhere,
  both 1.24.4 (the last DirectML release; Microsoft now steers new work to Windows ML).
- **x4plus** is the largest model (RRDBNet, 16.7M parameters). Its weights are stored at half
  precision (33.7 MB instead of 67 MB) and converted back as it loads, so it computes at full
  precision; the output differs from the full-size file by under one 8-bit level, and matches the
  official Real-ESRGAN program (a test compares them). On the Iris Xe laptop it runs about 4× faster
  on the CPU through ONNX than through the Vulkan program on the integrated graphics.
- **Real-CUGAN** has a model per scale (2×, 3×, 4×, the "conservative" ones, which keep the most
  detail); the smallest that reaches the size is used, then Lanczos for the rest. They're converted
  from bilibili's updated weights, and match the official PyTorch code to within one 8-bit level
  (half-precision storage, 2.6–2.8 MB each). The Real-CUGAN Vulkan program ships the original 2022
  weights, so the two differ slightly (SSIM 0.97–0.99); a test compares them. Its
  squeeze-and-excitation blocks average over the whole input, so tiles change the result very
  slightly (about 50 dB PSNR against an untiled frame, invisible; the program tiles too), but whole
  frames would need gigabytes (15 GB for 1080p at 2×). On the Iris Xe laptop it runs 2–4× faster on
  the CPU than the Vulkan program on the integrated graphics.
- If a model fails on the graphics card (almost always running out of memory), it's retried with
  smaller tiles, then finished on the CPU, rather than failing.
- **Windows (MSIX)** runs every AI model in-process (DirectML or CPU), so the package contains no
  separate AI programs, and the Windows app never downloads one (the Store restricts apps that
  download and run programs, and Smart App Control blocks unsigned downloaded programs). The small
  Anime4K shaders (data, not code) download on first use. **Linux** can optionally download the
  Real-ESRGAN and Real-CUGAN Vulkan programs (after asking), to run those models on the graphics
  card (Convert tab → CPU and heat, with the method selected).
- Every downloadable component is pinned to an exact file and SHA-256 checksum in
  `src/MyVideoEditor.Core/Upscaling/components.json`. Downloads go to the data folder under
  `tools/`. Licences: ArtCNN MIT, Anime4K MIT, Real-ESRGAN MIT (program) and BSD-3-Clause (models),
  Real-CUGAN MIT, ncnn BSD-3-Clause, ONNX Runtime MIT, DirectML Microsoft redistributable licence;
  the texts of everything shipped go into `THIRD-PARTY-NOTICES.txt`.
- Vulkan GPUs (for Anime4K and the ncnn programs) are chosen dedicated first, then integrated;
  software renderers (llvmpipe) are never used for real work.
- The ncnn AI programs work on frames in batches sized to the free disk space, piped straight back
  into the encoder, so even long videos need only about 1.5 GB of working space; if the graphics card
  runs out of memory, smaller tiles are tried automatically. Before a long AI job the app estimates
  the time and asks.
- GPU and AI methods are only used when the picture gets bigger; otherwise Lanczos is used.
- **CPU and heat** (Convert tab): a thread limit (Full, Balanced = half, Light = 2, or a number) for
  ffmpeg, ONNX Runtime and the frame code, to keep laptops cooler on long jobs; **low priority** (on
  by default) for every process and thread a conversion uses, so other programs come first (on
  Linux the ONNX worker threads are created on a lowered thread so they inherit it; on Windows the
  app's priority class is lowered while converting); and **AI runs on** Automatic / CPU / graphics
  card. On Linux, "graphics card" runs the Real-ESRGAN and Real-CUGAN models through their Vulkan
  programs (when downloaded) instead of ONNX on the CPU; Automatic uses whichever the speed test found faster.
- **Convert again** (per file) and **Convert all again** redo conversions with the current
  settings; earlier results are kept, as outputs are named after their settings
  (`clip-1280x720-ArtCNN.mp4`, `clip-1080p-AI-general-40%.mp4`, numbered if repeated), and each file
  in the list shows what it was converted with.
- How they compare: fidelity scores (SSIM/PSNR) reward faithfulness and VMAF rewards any
  sharpening, so neither can judge invented detail on its own; the comparisons above use both, plus
  looking at the pictures. The AI models made for animation turn camera footage into a painted look,
  so they're offered for animation; on animation, Real-ESRGAN's video model was clearly the best.

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

- `MyVideoEditor_1.0.7.0_x64.msix` (x64 only; about 210 MB with FFmpeg, ONNX Runtime with DirectML,
  and the models)
- `myvideoeditor-1.0.7-linux-x64.tar.gz`

The tag must be `vMAJOR.MINOR.PATCH`. Other pushes build `0.0.<run number>` as workflow artifacts
only, kept for 14 days. Local builds report `0.1.0`.

```bash
packaging/publish-linux.sh 0.1.0 x64      # → artifacts/linux/myvideoeditor-0.1.0-linux-x64.tar.gz
```

```powershell
# Windows, with the Windows 10/11 SDK. Store submission (the Store signs it):
./packaging/build-msix.ps1 -Version 1.0.0.0 `
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
| J / K / L | Rewind / stop / play; press J or L again for 2×, 4×, 8× (silent above 2×; rewind shows frames as fast as they render) |
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
- Transitions other than dissolves use ffmpeg's xfade filter, rendering both clips across the whole
  transition (even for a preview frame in the middle of one). Blur, pixelate and zoom annotations
  can't take part in one; they dissolve instead.
- The preview renders at up to 720p; very complex projects may drop preview frames (export is
  unaffected). Editing a title or annotation re-renders the preview when the mouse is released.
- Encoding is software only (x264/x265/SVT-AV1); hardware encoders aren't used yet.
- The Windows build is compiled and packaged by CI but isn't exercised by the automated tests,
  which run on Linux.

## Licence

MyVideoEditor is released under the [MIT License](LICENSE). Bundled components keep their own
licences, chiefly MIT (Avalonia, SkiaSharp and its Skottie Lottie player, CommunityToolkit.Mvvm, Silk.NET), LGPL (OpenAL Soft)
and GPL (FFmpeg, run as separate programs). Release builds include `THIRD-PARTY-NOTICES.txt` with
every bundled component's copyright and licence, generated per platform by
`tools/generate-third-party-notices.py`; the build fails if any package has no licence text.
