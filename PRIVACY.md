# MyVideoEditor privacy policy

_Last updated: 8 October 2026_

MyVideoEditor is a video editor, converter and screen recorder. It is developed by Stuart
Millington and released as open source under the MIT licence. This policy covers the MyVideoEditor
app, whether you install it from the Microsoft Store or from a release on GitHub.

## In short

MyVideoEditor does not collect, sell or share your personal data. It has no accounts, analytics,
telemetry, advertising, crash reporting or update checks. It makes no network connections at all.
The developer receives nothing from the app.

## Your media and recordings

Everything happens on your computer. The videos, sounds and pictures you import are read from where
they are, and are never uploaded. Projects, exports, conversions, recordings and screenshots are
saved only where you choose (recordings and screenshots default to a `MyVideoEditor` folder in your
Videos folder).

**Screen recording** captures only what you choose (a window, a screen or a region), only while a
recording is running, and only after you press Start. A Stop button is shown on screen while it
records. Sound is recorded only from the devices you tick (a microphone and/or the computer's
sound). Recordings can contain anything that was on screen, so check them before sharing; the
editor's Blur and Pixelate tools hide private details.

To list windows you can record, MyVideoEditor reads the titles and positions of open windows when
you open the Capture tab or press Refresh. This list is shown to you and not stored.

## Data stored on your computer

MyVideoEditor keeps a small amount of data in a folder in your user profile:

- **Windows:** `%LOCALAPPDATA%\myvideoeditor`, which Windows keeps in the app's private storage when
  it is installed from the Store.
- **Linux:** `~/.config/myvideoeditor`.

That folder holds:

- **Settings (`settings.json`):** your preferences and the list of recently opened projects (their
  file paths).
- **Autosave (`autosave/`):** a copy of the project you're working on, saved every minute while it
  has unsaved changes, so it can be restored if the app closes unexpectedly. It's deleted when you
  save or discard the project.
- **Caches (`cache/`):** thumbnails, sound waveforms and rendered titles, made from your media so the
  editor is fast. They can be deleted at any time; they are made again when needed.
- **Diagnostic logs (`logs/`):** plain-text logs of what the app did, such as imports, exports and
  errors. They can include file names and paths, and the ffmpeg commands used. Logs older than 14
  days are deleted automatically. They stay on your computer: MyVideoEditor never sends them
  anywhere.

## Deleting your data

Uninstalling the Store version removes its data folder. To remove the data yourself, for example
after a Linux install, delete the folder listed above. Your projects, exports and recordings are
wherever you saved them, and are yours to keep or delete.

## Children

MyVideoEditor is a general-purpose tool. It is not aimed at children and collects no data from
anyone.

## Third parties

MyVideoEditor includes FFmpeg, which it runs on your computer to read and write media. FFmpeg makes
no network connections in the way MyVideoEditor uses it.

When you install MyVideoEditor from the Microsoft Store, Microsoft handles the download, licensing
and updates under its own privacy statement: <https://privacy.microsoft.com/privacystatement>.

## Changes

If this policy changes, the new version will be published at this address with a new date. The
full history of this file is kept in the repository.

## Contact

Questions about privacy can be raised as an issue at <https://github.com/StuMP90/myvideoeditor/issues>.
