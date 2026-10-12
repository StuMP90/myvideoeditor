# Microsoft Store listing

Text and answers for the MyVideoEditor submission in Partner Center, ready to paste in. Limits in
brackets are Partner Center's. Screenshots are in [`screenshots/`](screenshots/) and the Store logos
are next to this file.

## Product identity

| Field | Value |
|---|---|
| Product name | MyVideoEditor |
| Developed by / publisher display name | Stuart Millington (must match Partner Center) |
| Category | Photo & video |
| Pricing | Free, no in-app purchases, no ads |
| Markets | All |
| Package | `MyVideoEditor_<version>_x64.msix` from the release workflow, built with the Partner Center identity values (README → Packaging) |
| Minimum OS | Windows 10 version 1809 (build 17763) or later, x64 |

## Links

| Field | Value |
|---|---|
| Privacy policy URL | <https://github.com/StuMP90/myvideoeditor/blob/main/PRIVACY.md> |
| Website | <https://github.com/StuMP90/myvideoeditor> |
| Support contact | <https://github.com/StuMP90/myvideoeditor/issues> |

The privacy policy is needed because the app records the screen and microphone and downloads
optional components. It must be live at that URL, so push PRIVACY.md to `main` before submitting.

## Short description [1,000 characters]

> Edit, convert, upscale and record video, all on your own PC. MyVideoEditor is a free multi-track
> editor with titles, transitions and effects; a batch converter that resizes and upscales with
> local AI; and a screen recorder that turns recordings into annotated bug reports and demos.
> No account, no watermark, no cloud: your videos never leave your computer.

## Description [10,000 characters]

> MyVideoEditor is a free video editor, converter and screen recorder that does everything on your
> own computer. There is no account, no subscription, no watermark and no upload. It's also open
> source.
>
> **EDIT**
>
> Put videos, sound and pictures on a multi-track timeline. Drag to move and trim, split at the
> playhead, ripple-delete gaps, and undo anything. Upper tracks are drawn in front, so
> picture-in-picture, logos and overlays are easy. The preview uses the same engine as the export,
> so what you see is what you get. Space plays and pauses; J, K and L shuttle backwards and forwards.
>
> • Transitions: dissolve, dip to black or white, wipes, pushes, covers, circle, clock, barn
> doors, zoom, pixelate and blur. Sound crossfades automatically.
> • Titles: title cards, lower thirds, captions, chapter titles and end cards, in any installed
> font, with outlines, shadows, background boxes and a picture such as your logo.
> • Animated titles: slide, rise, grow, wipe, typewriter, word by word and line by line.
> • Scrolling titles: end credits, an opening crawl that recedes into the distance, and a news
> ticker that loops for as long as you like.
> • Lottie animations: import your own and change their text per clip.
> • Effects: colour correction, temperature, black and white, sepia, blur, sharpen, vignette,
> noise reduction, stabilisation and green screen. For sound: loudness levelling, noise
> reduction, bass, treble, compressor and echo.
> • Speed from 0.1× to 16× with the pitch kept, plus fades, opacity, crop, scale, position and
> rotation.
> • Autosave, with recovery after a crash.
>
> **EXPORT**
>
> Export to MP4 (H.264, H.265, AV1), WebM, MKV, MOV (ProRes), animated GIF or audio only (MP3,
> M4A, WAV, FLAC, Opus), at any size from vertical Shorts to 4K. You can add chapters, and export
> DVD-Video (MPEG-2, NTSC or PAL, 4:3 or 16:9) for burning to disc. Save any frame as a PNG.
>
> **CONVERT, RESIZE AND UPSCALE**
>
> Convert a batch of files between formats; resize, trim, rotate and deinterlace them; change the
> frame rate; or rewrap without re-encoding. Your originals are never changed.
>
> Upscale old or low-resolution video with built-in AI models: Real-ESRGAN, Real-CUGAN, ArtCNN and SmallSR.
> They run on your graphics card (DirectX 12) or on the processor. "Test speed on this PC"
> recommends the best method that finishes in reasonable time. "Compare all methods" shows one
> frame through every method side by side, so you can choose before a long conversion. A strength
> control blends the model with the real picture, so faces and textures stay natural. "CPU and heat"
> settings keep laptops cool on long jobs.
>
> **RECORD AND ANNOTATE**
>
> Record a window, a screen or a region, with the mouse pointer and your microphone. There's a
> countdown and a small Stop button. Take screenshots too. Then annotate them for bug reports,
> tutorials and demos: arrows, boxes, circles, highlights, a spotlight, callouts, numbered steps,
> zoom, and blur or pixelate to hide passwords and personal details. Export a small MP4 or GIF
> for the ticket, or a PNG.
>
> **PRIVATE BY DESIGN**
>
> MyVideoEditor collects nothing: no accounts, analytics, telemetry, ads or crash reports. Your
> media is never uploaded. The only network use is downloading an optional free upscaling
> component when you ask for one.
>
> MyVideoEditor is open source (MIT) and includes FFmpeg (GPL). Licences are under Help → Licences.

## What's new in this version [1,500 characters]

For the first submission:

> First Microsoft Store release.

## Product features [up to 20, 200 characters each]

1. Multi-track timeline with drag, trim, split, ripple delete and full undo
2. 14 transition styles, including dissolves, wipes, pushes, circles and zooms, with automatic sound crossfades
3. Titles, lower thirds and end cards with fonts, outlines, shadows and your logo
4. Animated titles: slide, rise, grow, wipe, typewriter, word by word
5. Scrolling end credits, opening crawls and looping news tickers
6. Colour, blur, sharpen, stabilisation, green screen and sound-cleanup effects
7. Export MP4, WebM, MKV, MOV, GIF and audio, from vertical video to 4K
8. Chapters and DVD-Video export (NTSC/PAL) for burning to disc
9. Batch convert, resize, trim and rotate without touching your originals
10. Upscale with local AI (Real-ESRGAN, Real-CUGAN, ArtCNN, SmallSR) on your graphics card or CPU
11. Compare every upscaling method side by side before converting
12. Record a window, screen or region with your microphone
13. Annotate recordings and screenshots with arrows, callouts, steps, zoom and blur
14. No account, no watermark, no cloud: nothing leaves your PC

## Search terms [up to 7, 30 characters each, 21 words in total]

1. video editor
2. screen recorder
3. video converter
4. AI upscaler
5. titles and transitions
6. DVD
7. annotate screenshots

That's 12 words.

## Copyright and trademark info [200 characters]

> © 2026 Stuart Millington. Open source under the MIT licence. Includes FFmpeg (GPL) and other
> third-party components; see Help → Licences.

## Additional licence terms

Leave this empty to use the standard Microsoft application licence terms. The app's own MIT licence
and the third-party notices ship inside the package (`THIRD-PARTY-NOTICES.txt`, Help → Licences).

## Screenshots [up to 10; PNG; 1366×768 or larger; captions 200 characters]

All are 1920×1080. They show only footage drawn by the test code (`DemoFootage.cs`), with no
third-party media, and made-up names (Jane Doe, "Example Notes", example.com).

| File | Caption |
|---|---|
| `01-editing.png` | Edit on a multi-track timeline with titles, lower thirds and chapter markers |
| `02-transitions.png` | 14 transition styles: dissolves, wipes, pushes, circles, zooms and more |
| `03-titles-crawl.png` | Animated and scrolling titles: credits, opening crawls and news tickers |
| `04-bug-report-annotations.png` | Annotate recordings: steps, arrows, callouts, highlights, and blur for private details |
| `05-convert-upscale.png` | Convert and upscale in batches with local AI, with a before-and-after preview |
| `06-compare-all-methods.png` | Compare every upscaling method on your own frame before converting |
| `07-capture.png` | Record a window, a screen or a region, with your microphone and computer sound |
| `08-export-dvd.png` | Export MP4, WebM, MKV, MOV, GIF, or DVD-Video with chapters |

Use 01 as the first screenshot; the Store uses it as the hero image.

The screenshots are rendered on Linux, so the theme matches Windows but the typeface may differ
slightly. One line of help text in 05 describes the Linux build ("On Linux the graphics card
applies to…"). On Windows that text describes DirectML instead. Retake 05 on Windows if a
reviewer objects.

To regenerate them after UI changes (about 1½ minutes; needs ffmpeg):

```bash
MYVIDEOEDITOR_STORE_SCREENSHOTS=packaging/store/screenshots \
    dotnet test tests/MyVideoEditor.App.Tests --filter StoreScreenshots
```

## Store logos

Partner Center → Store listing → Store logos: `StoreLogo-300x300.png` (required), plus the 150 and 71
sizes. The tile and taskbar icons are inside the package. All come from `tools/make_icons.py`.

## Additional system requirements

| | Minimum | Recommended |
|---|---|---|
| Memory | 8 GB | 16 GB |
| Graphics | — | DirectX 12 graphics card (for faster AI upscaling) |
| Disk | 500 MB for the app, plus space for your videos | |

## Product declarations

| Declaration | Answer |
|---|---|
| This app allows users to make purchases, but does not use the Microsoft Store commerce system | No |
| This app has been tested to meet accessibility guidelines | No; it hasn't been formally tested |
| Customers can install this app to alternate drives or removable storage | Yes |
| This app depends on non-Microsoft drivers or NT services | No |
| Windows can include this app's data in automatic backups to OneDrive | Yes |
| This app collects, stores or transmits personal information | No; see the privacy policy |

## Age rating (IARC questionnaire)

Choose the "Utility, productivity, communication or other" category, then answer:

- Violence, fear, sexuality, nudity, gambling, drugs, alcohol, tobacco, crude humour, bad
  language: **No**. The app contains no content of its own.
- Users can interact or exchange content with each other: **No**. Nothing is shared or uploaded.
- Shares the user's location: **No**.
- Digital purchases: **No**.
- Unrestricted internet access (a web browser or search): **No**.

The expected result is 3+ / Everyone.

## Capabilities and their justification

The package declares only two capabilities ([`AppxManifest.xml`](../msix/AppxManifest.xml)):

| Capability | Type | Why |
|---|---|---|
| `runFullTrust` | Restricted | MyVideoEditor is a .NET desktop (Win32) app packaged as MSIX, not a UWP app. Every packaged desktop app needs this capability to run. The app uses it to start its bundled FFmpeg programs for importing, previewing, exporting and converting video, and to record the screen. |
| `microphone` | Device (not restricted) | Records sound from the microphone with a screen recording, only while a recording runs and only when the user ticks the microphone. Windows controls access through Settings → Privacy → Microphone. |

Paste this into **Submission options → Restricted capabilities** for `runFullTrust`:

> MyVideoEditor is a desktop (Win32) .NET application packaged with MSIX. runFullTrust is required
> for any packaged desktop app. The app uses it to run the FFmpeg programs bundled in its package for
> reading, previewing, exporting and converting video, and to record the screen when the user starts
> a recording. It does not install services or drivers and does not change system settings.

**Not requested**, and why:

- **`broadFileSystemAccess`** ("all file system access"). That's for UWP apps. A full-trust desktop
  app already opens files through the normal Open and Save dialogs, and any file the user drags in,
  with the user's own permissions, so it isn't needed. Don't add it: it's a restricted capability
  that needs extra justification and puts off customers.
- **Screen capture** (`graphicsCapture*`). Those capabilities are also for UWP. The full-trust app
  records through FFmpeg's GDI screen grabber (standard desktop APIs), which needs no capability. Recording
  happens only after the user presses Start, with a Stop button on screen.
- **`internetClient`**. Full-trust apps have network access without it. The app's only network
  use is downloading an optional upscaling component, after the user chooses it and confirms.

## Notes for certification

Paste into **Submission options → Notes for certification**:

> No account or sign-in is needed, and the app is free with no purchases.
>
> To test: in the **Edit** tab, choose **Import media**, add any video and play it. **Title** adds a
> title; **Export video…** exports. The **Convert** tab converts and upscales files (try
> **Preview frame**). The **Capture** tab records the screen: choose a window or screen, then
> **Start recording**, and use the on-screen Stop button.
>
> The package includes FFmpeg (GPL, separate executables in the `ffmpeg` folder; its source location
> is in `ffmpeg/FFMPEG-SOURCE.txt` and under Help → Licences) and ONNX AI models, which run locally.
>
> Downloads: the "Cleaner lines" upscaler downloads small GLSL shader files (data, not executable
> code) from GitHub on first use, after asking the user, and checks them against a fixed SHA-256.
> The Windows app never downloads or runs executable code. Nothing about the user is sent anywhere,
> and the app has no telemetry.

## Before each submission

- [ ] Build with the Partner Center identity values (repository variables `MSIX_IDENTITY_NAME`,
      `MSIX_PUBLISHER`, `MSIX_PUBLISHER_DISPLAY_NAME`).
- [ ] Check that PRIVACY.md on `main` is current. Update its date if the data the app keeps has
      changed.
- [ ] Regenerate the screenshots if the UI has changed visibly.
- [ ] Fill in "What's new".
- [ ] Run the Windows App Certification Kit on the MSIX (optional; Partner Center runs its own).
