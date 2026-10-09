using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Editing;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Playback;

namespace MyVideoEditor.App.ViewModels;

/// <summary>A drawing tool for the preview: click-and-drag on the picture to add an annotation.</summary>
public sealed record AnnotationTool(AnnotationShape Shape, string Label, string Glyph, string Tip);

/// <summary>A ready-made title style.</summary>
public sealed record TitleTemplate(string Label, TitleClip Clip);

/// <summary>
/// The editing workspace: the open project (with undo), the media bin, the timeline selection,
/// the playhead and preview playback.
/// </summary>
public sealed partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly DialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly PlaybackEngine _playback;
    private readonly IAudioSink? _audio;
    private readonly DispatcherTimer _playheadTimer;
    private readonly DispatcherTimer _stillTimer;
    private int _rewind; // > 0 while rewinding: the current rewind's number (a new one ends the old loop)
    private readonly DispatcherTimer _autosaveTimer;
    private readonly HashSet<string> _selection = [];
    private bool _restartPlaybackAfterEdit;
    private bool _stillRunning;
    private bool _stillAgain;

    public EditorViewModel(DialogService dialogs, AppSettings settings, IAudioSink? audio)
    {
        _dialogs = dialogs;
        _settings = settings;
        _audio = audio;
        _playback = new PlaybackEngine(audio);
        if (audio != null)
            audio.Volume = (float)settings.PreviewVolume;
        _playback.FrameReady += frame => Dispatcher.UIThread.Post(() => ShowFrame(frame));
        _playback.Stopped += error => Dispatcher.UIThread.Post(() => OnPlaybackStopped(error));
        Previews.Ready += () => Dispatcher.UIThread.Post(() =>
        {
            TimelineInvalidated?.Invoke();
            LoadThumbnails();
        });

        _playheadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
        {
            if (_playback.IsPlaying)
                SetPlayheadFromPlayback(_playback.Position);
        });
        _stillTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
        {
            _stillTimer!.Stop();
            _ = RenderStillAsync();
        });
        _autosaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => Autosave());
        _autosaveTimer.Start();

        Session = NewSession(Project.CreateDefault());
    }

    public MediaPreviews Previews { get; } = MediaPreviews.Default;
    public AppSettings Settings => _settings;
    public DialogService Dialogs => _dialogs;

    /// <summary>The open project. Replaced on New/Open; subscribe to <see cref="SessionReplaced"/>.</summary>
    public EditSession Session { get; private set; }

    public Project Project => Session.Project;
    public ObservableCollection<MediaItemViewModel> Media { get; } = [];

    /// <summary>The timeline needs redrawing (project changed, selection, previews ready).</summary>
    public event Action? TimelineInvalidated;
    /// <summary>A new preview picture (UI thread).</summary>
    public event Action? PreviewUpdated;
    public event Action? SessionReplaced;

    [ObservableProperty]
    private double _playhead;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private ClipInspectorViewModel? _inspector;

    [ObservableProperty]
    private string _title = "Untitled";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private AnnotationTool? _activeTool;

    [ObservableProperty]
    private bool _snapEnabled = true;

    [ObservableProperty]
    private MediaItemViewModel? _selectedMedia;

    /// <summary>The current preview picture.</summary>
    public WriteableBitmap? PreviewBitmap { get; private set; }

    /// <summary>Timeline time of the picture shown.</summary>
    public double PreviewTime { get; private set; } = -1;

    /// <summary>True when the preview shows the playhead with the latest edits (no render pending).</summary>
    public bool PreviewIsCurrent => !_stillRunning && !_stillTimer.IsEnabled && Math.Abs(PreviewTime - Playhead) < 1e-6;

    /// <summary>Size the preview is rendered at (set by the view from its size).</summary>
    public (int Width, int Height) PreviewSize { get; private set; } = (640, 360);

    public string PlayheadText => Timecode.FormatFrames(Playhead, Project.Settings.FrameRate);
    public string DurationText => Timecode.FormatFrames(Project.Duration, Project.Settings.FrameRate);
    public string ProjectSummary => $"{Project.Settings.Width}×{Project.Settings.Height} · {Project.Settings.FrameRate:0.##} fps";
    public bool CanUndo => Session.CanUndo;
    public bool CanRedo => Session.CanRedo;
    public string UndoTip => Session.UndoDescription is { } d ? $"Undo {d.ToLowerInvariant()} (Ctrl+Z)" : "Undo (Ctrl+Z)";
    public string RedoTip => Session.RedoDescription is { } d ? $"Redo {d.ToLowerInvariant()} (Ctrl+Shift+Z)" : "Redo (Ctrl+Shift+Z)";
    public bool HasSelection => _selection.Count > 0;
    public bool HasAudioOutput => _playback.HasAudio;
    public IReadOnlyCollection<string> Selection => _selection;

    public double PreviewVolume
    {
        get => _settings.PreviewVolume * 100;
        set
        {
            _settings.PreviewVolume = Math.Clamp(value / 100, 0, 2);
            _settings.Save();
            if (_audio != null)
                _audio.Volume = (float)_settings.PreviewVolume;
            OnPropertyChanged();
        }
    }

    public static IReadOnlyList<AnnotationTool> Tools { get; } =
    [
        new(AnnotationShape.Arrow, "Arrow", "➜", "Draw an arrow: drag from the tail to the point"),
        new(AnnotationShape.Rectangle, "Box", "▢", "Draw a box around something"),
        new(AnnotationShape.Ellipse, "Circle", "◯", "Circle something"),
        new(AnnotationShape.Highlight, "Highlight", "▆", "Translucent highlighter over an area"),
        new(AnnotationShape.Spotlight, "Spotlight", "◉", "Darken everything except an area"),
        new(AnnotationShape.Callout, "Callout", "💬", "A note in a box"),
        new(AnnotationShape.Step, "Step", "①", "Numbered step marker (1, 2, 3…)"),
        new(AnnotationShape.Blur, "Blur", "▒", "Blur an area: hide passwords and personal data"),
        new(AnnotationShape.Pixelate, "Pixelate", "▦", "Pixelate an area"),
        new(AnnotationShape.Zoom, "Zoom", "🔍", "Zoom the picture into an area"),
    ];

    public static IReadOnlyList<TitleTemplate> TitleTemplates { get; } =
    [
        new("Title", new TitleClip { Text = "Title", FontSize = 96, Y = 0.5 }),
        new("Title card", new TitleClip { Text = "Title\nSubtitle", FontSize = 80, BackgroundColor = "#101418", Shadow = false }),
        new("Lower third", new TitleClip { Text = "Name\nRole", FontSize = 44, X = 0.06, Y = 0.82, Alignment = Core.Model.TextAlignment.Left, BoxColor = "#000000", BoxOpacity = 0.6, Shadow = false }),
        new("Caption", new TitleClip { Text = "Caption text", FontSize = 42, Y = 0.9, Bold = false, BoxColor = "#000000", BoxOpacity = 0.55, Shadow = false }),
        new("Chapter", new TitleClip { Text = "Chapter 1", FontSize = 64, Y = 0.12, OutlineWidth = 3 }),
        new("End card", new TitleClip { Text = "Thanks for watching", FontSize = 80, BackgroundColor = "#000000", Shadow = false }),
    ];

    // ---- session -----------------------------------------------------------------------------

    private EditSession NewSession(Project project, string? path = null)
    {
        var session = new EditSession(project, path);
        session.Changed += OnSessionChanged;
        return session;
    }

    private void ReplaceSession(EditSession session)
    {
        StopPlayback();
        Session.Changed -= OnSessionChanged;
        Session = session;
        _selection.Clear();
        Inspector = null;
        Playhead = 0;
        SyncMedia();
        UpdateTitle();
        OnPropertyChanged(string.Empty);
        SessionReplaced?.Invoke();
        TimelineInvalidated?.Invoke();
        RequestStill();
    }

    private void OnSessionChanged()
    {
        SyncMedia();
        _selection.RemoveWhere(id => Project.FindClip(id) == null);
        if (Inspector != null && Project.FindClip(Inspector.ClipId) == null)
            Inspector = null;
        Inspector?.Refresh();
        UpdateTitle();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoTip));
        OnPropertyChanged(nameof(RedoTip));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(ProjectSummary));
        OnPropertyChanged(nameof(HasSelection));
        TimelineInvalidated?.Invoke();
        if (IsPlaying)
        {
            // Pick up the edit: restart playback from here (after a short pause, so dragging stays smooth).
            _restartPlaybackAfterEdit = true;
            _stillTimer.Stop();
            _stillTimer.Start();
        }
        else
        {
            RequestStill();
        }
    }

    private void UpdateTitle()
    {
        var name = Session.FilePath != null ? Path.GetFileNameWithoutExtension(Session.FilePath) : Project.Name;
        Title = (Session.IsDirty ? "• " : "") + name;
    }

    private void SyncMedia()
    {
        var ids = Project.Media.Select(m => m.Id).ToList();
        if (Media.Select(m => m.Id).SequenceEqual(ids))
            return;
        var existing = Media.ToDictionary(m => m.Id);
        Media.Clear();
        foreach (var media in Project.Media)
            Media.Add(existing.TryGetValue(media.Id, out var vm) ? vm : new MediaItemViewModel(media));
        LoadThumbnails();
    }

    private void LoadThumbnails()
    {
        foreach (var item in Media.Where(m => m.Thumbnail == null && !m.IsMissing && m.Kind != MediaKind.Audio))
        {
            var media = Project.FindMedia(item.Id);
            if (media == null)
                continue;
            var strip = Previews.TryGetFilmstrip(media.Path, media.Duration, media.Kind == MediaKind.Image);
            if (strip?.FrameAt(Math.Min(1, media.Duration / 2)) is { } pixels)
                item.Thumbnail = ToBitmap(pixels, strip.Width, strip.Height);
        }
    }

    internal static WriteableBitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var bitmap = new WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        for (var y = 0; y < height; y++)
            System.Runtime.InteropServices.Marshal.Copy(bgra, y * width * 4, fb.Address + y * fb.RowBytes, width * 4);
        return bitmap;
    }

    [RelayCommand]
    private async Task NewProject()
    {
        if (!await ConfirmDiscardAsync())
            return;
        ReplaceSession(NewSession(Project.CreateDefault()));
        Status = "New project.";
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (!await ConfirmDiscardAsync())
            return;
        var files = await _dialogs.OpenFilesAsync("Open project", false, DialogService.ProjectFiles);
        if (files.Count > 0)
            await OpenProjectFileAsync(files[0]);
    }

    public async Task OpenProjectFileAsync(string path)
    {
        try
        {
            var project = ProjectSerializer.Load(path);
            ReplaceSession(NewSession(project, path));
            _settings.AddRecentProject(path);
            var missing = project.Media.Count(m => m.IsMissing);
            Status = missing > 0 ? $"Opened. {missing} media file(s) are missing; their clips are left out of the preview and export." : "Opened " + Path.GetFileName(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Log.Warn("editor", $"Opening {path} failed.", ex);
            await _dialogs.AlertAsync("Couldn't open the project", ex.Message);
        }
    }

    [RelayCommand]
    public async Task<bool> Save() => Session.FilePath == null ? await SaveAsInternal() : SaveTo(Session.FilePath);

    [RelayCommand]
    private Task<bool> SaveAs() => SaveAsInternal();

    private async Task<bool> SaveAsInternal()
    {
        var folder = Session.FilePath != null ? Path.GetDirectoryName(Session.FilePath) : _settings.LastMediaFolder;
        var path = await _dialogs.SaveFileAsync("Save project", Project.Name, Core.Model.Project.FileExtension, "MyVideoEditor project", folder);
        return path != null && SaveTo(path);
    }

    private bool SaveTo(string path)
    {
        try
        {
            if (Project.Name == "Untitled")
                Project.Name = Path.GetFileNameWithoutExtension(path);
            ProjectSerializer.Save(Project, path);
            Session.MarkSaved(path);
            _settings.AddRecentProject(path);
            DeleteAutosave();
            Status = "Saved " + Path.GetFileName(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _dialogs.AlertAsync("Couldn't save the project", ex.Message);
            return false;
        }
    }

    /// <summary>Asks to save unsaved changes. False = cancel whatever was about to happen.</summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!Session.IsDirty || Project.AllClips().Any() == false && Project.Media.Count == 0)
            return true;
        var (choice, _) = await _dialogs.ChooseAsync("Unsaved changes", $"Save the changes to {Title.TrimStart('•', ' ')}?", ["Save", "Don't save", "Cancel"]);
        return choice switch
        {
            0 => await Save(),
            1 => DeleteAutosaveAndContinue(),
            _ => false,
        };
    }

    private bool DeleteAutosaveAndContinue()
    {
        DeleteAutosave();
        return true;
    }

    private string AutosavePath => Path.Combine(AppPaths.RecoveryDirectory, "recovery" + Core.Model.Project.FileExtension);

    private void Autosave()
    {
        if (!Session.IsDirty || (!Project.AllClips().Any() && Project.Media.Count == 0))
            return;
        try
        {
            ProjectSerializer.Save(Project, AutosavePath);
            File.WriteAllText(AutosavePath + ".source", Session.FilePath ?? "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("editor", "Autosave failed.", ex);
        }
    }

    private void DeleteAutosave()
    {
        try
        {
            foreach (var f in new[] { AutosavePath, AutosavePath + ".bak", AutosavePath + ".source" })
                if (File.Exists(f))
                    File.Delete(f);
        }
        catch (IOException) { }
    }

    /// <summary>At startup: offers to restore a project that wasn't saved before the app closed unexpectedly.</summary>
    public async Task OfferRecoveryAsync()
    {
        if (!File.Exists(AutosavePath))
            return;
        var when = File.GetLastWriteTime(AutosavePath);
        var restore = await _dialogs.ConfirmAsync("Restore unsaved work?",
            $"MyVideoEditor closed without saving a project (last autosaved {when:g}). Restore it?", "Restore", "Discard");
        if (!restore)
        {
            DeleteAutosave();
            return;
        }
        try
        {
            var project = ProjectSerializer.Load(AutosavePath);
            var source = File.Exists(AutosavePath + ".source") ? File.ReadAllText(AutosavePath + ".source") : "";
            var session = NewSession(project, string.IsNullOrEmpty(source) ? null : source);
            ReplaceSession(session);
            // Mark as changed, so it's saved deliberately.
            Session.Apply("Restore", _ => { });
            Status = "Restored unsaved work.";
        }
        catch (Exception ex)
        {
            Log.Warn("editor", "Recovery failed.", ex);
            await _dialogs.AlertAsync("Couldn't restore", ex.Message);
        }
    }

    // ---- media -------------------------------------------------------------------------------

    [RelayCommand]
    private async Task ImportMedia()
    {
        var files = await _dialogs.OpenFilesAsync("Import media", true, DialogService.MediaFiles);
        await ImportFilesAsync(files, addToTimeline: false);
    }

    /// <summary>Imports files into the bin; optionally puts them on the timeline one after another.</summary>
    public async Task<List<string>> ImportFilesAsync(IReadOnlyList<string> paths, bool addToTimeline, double? at = null, string? trackId = null)
    {
        var added = new List<string>();
        var failed = new List<string>();
        var position = at ?? Playhead;
        foreach (var path in paths)
        {
            if (Path.GetExtension(path).Equals(Core.Model.Project.FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                await OpenProjectFileAsync(path);
                return added;
            }
            try
            {
                Status = "Reading " + Path.GetFileName(path) + "…";
                var info = await MediaProbe.Default.ProbeAsync(path);
                var mediaId = Session.Editor.ImportMedia(info);
                added.Add(mediaId);
                _settings.LastMediaFolder = Path.GetDirectoryName(path);
                if (addToTimeline)
                {
                    // An empty project takes its size and frame rate from the first video.
                    AdoptSettingsFrom(info);
                    var clipId = Session.Editor.AddMediaClip(mediaId, position, trackId);
                    position = Project.FindClip(clipId)?.Clip.End ?? position;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("editor", $"Import of {path} failed.", ex);
                failed.Add($"{Path.GetFileName(path)}: {(ex is FfmpegException fe ? fe.Message : ex.Message)}");
            }
        }
        _settings.Save();
        Status = added.Count > 0 ? $"Imported {added.Count} file(s)." : "";
        if (failed.Count > 0)
        {
            var hint = FfmpegLocator.Default.IsAvailable ? "" : "\n\nffmpeg wasn't found. Install it, or set MYVIDEOEDITOR_FFMPEG_DIR to its folder.";
            await _dialogs.AlertAsync("Some files couldn't be imported", string.Join("\n", failed) + hint);
        }
        return added;
    }

    /// <summary>The first video added to an empty project sets the project's size and frame rate.</summary>
    private void AdoptSettingsFrom(MediaInfo info)
    {
        if (Project.AllClips().Any() || !info.HasVideo || info.Kind == MediaKind.Image || info.Width < 16 || info.Height < 16)
            return;
        var s = Project.Settings.Clone();
        s.Width = info.Width & ~1;
        s.Height = info.Height & ~1;
        s.FrameRate = Math.Round(info.FrameRate, 3) is > 0 and <= 120 ? Math.Round(info.FrameRate, 3) : 30;
        Session.Editor.UpdateSettings(s);
        Status = $"Project set to {s.Width}×{s.Height} at {s.FrameRate:0.##} fps to match {Path.GetFileName(info.Path)}.";
    }

    [RelayCommand]
    private void AddMediaToTimeline(MediaItemViewModel? item)
    {
        item ??= SelectedMedia;
        if (item == null)
            return;
        if (Project.FindMedia(item.Id) is { } media && !Project.AllClips().Any() && media.HasVideo && media.Kind == MediaKind.Video)
            AdoptSettingsFrom(new MediaInfo { Path = media.Path, HasVideo = true, Kind = media.Kind, Width = media.Width, Height = media.Height, FrameRate = media.FrameRate });
        var id = Session.Editor.AddMediaClip(item.Id, Playhead);
        Select(id);
    }

    [RelayCommand]
    private async Task RemoveMedia(MediaItemViewModel? item)
    {
        item ??= SelectedMedia;
        if (item == null)
            return;
        var used = Project.AllClips().Count(x => x.Clip is MediaClip m && m.MediaId == item.Id);
        if (used > 0 && !await _dialogs.ConfirmAsync("Remove media", $"{item.Name} is used by {used} clip(s), which will be removed too.", "Remove"))
            return;
        Session.Editor.RemoveMedia(item.Id);
    }

    // ---- selection ---------------------------------------------------------------------------

    public bool IsSelected(string clipId) => _selection.Contains(clipId);

    public void Select(string? clipId, bool add = false)
    {
        if (!add)
            _selection.Clear();
        if (clipId != null)
        {
            if (add && !_selection.Add(clipId))
                _selection.Remove(clipId);
            else
                _selection.Add(clipId);
        }
        var primary = clipId != null && _selection.Contains(clipId) ? clipId : _selection.FirstOrDefault();
        if (Inspector?.ClipId != primary)
            Inspector = primary == null ? null : new ClipInspectorViewModel(Session, primary);
        OnPropertyChanged(nameof(HasSelection));
        TimelineInvalidated?.Invoke();
        PreviewUpdated?.Invoke();
    }

    // ---- editing commands --------------------------------------------------------------------

    [RelayCommand]
    private void Undo() => Session.Undo();

    [RelayCommand]
    private void Redo() => Session.Redo();

    [RelayCommand]
    private void Split()
    {
        var count = Session.Editor.Split(Playhead, _selection.Count > 0 ? _selection.ToList() : null);
        Status = count == 0 ? "Nothing under the playhead to split." : $"Split {count} clip(s).";
    }

    [RelayCommand]
    private void Delete() => Session.Editor.Delete(_selection.ToList());

    [RelayCommand]
    private void RippleDelete() => Session.Editor.Delete(_selection.ToList(), ripple: true);

    [RelayCommand]
    private void Duplicate()
    {
        if (Inspector?.ClipId is { } id && Session.Editor.Duplicate(id) is { } copy)
            Select(copy);
    }

    [RelayCommand]
    private void DetachAudio()
    {
        if (Inspector?.ClipId is { } id)
            Status = Session.Editor.DetachAudio(id) != null ? "Sound moved to an audio track." : "This clip has no sound to detach.";
    }

    [RelayCommand]
    private void AddDissolve()
    {
        if (Inspector?.ClipId is not { } id || Project.FindClip(id) is not { } found)
            return;
        var track = found.Track.Id;
        var why = Session.Editor.AddDissolve(id, Math.Min(1, found.Clip.Duration / 2));
        Status = why ?? (Project.FindClip(id)?.Track.Id != track
            ? "Transition added; the clip moved onto the same track as the one before it, so they can blend. Choose its style in the clip's settings."
            : "Transition added. Choose its style in the clip's settings.");
    }

    [RelayCommand]
    private void AddTitle(TitleTemplate? template)
    {
        var id = Session.Editor.AddTitle(Playhead, (template ?? TitleTemplates[0]).Clip);
        Select(id);
    }

    [RelayCommand]
    private void ToggleTool(AnnotationTool? tool) => ActiveTool = ActiveTool == tool ? null : tool;

    /// <summary>Adds an annotation drawn on the preview (coordinates as fractions of the frame).</summary>
    public string AddAnnotation(AnnotationShape shape, double x1, double y1, double x2, double y2)
    {
        var steps = Project.AllClips().Count(x => x.Clip is AnnotationClip { Shape: AnnotationShape.Step });
        var clip = new AnnotationClip
        {
            Shape = shape,
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Start = Playhead,
            Duration = DefaultAnnotationDuration(),
            Strength = AnnotationClip.DefaultStrength(shape),
            Text = shape switch { AnnotationShape.Step => (steps + 1).ToString(), AnnotationShape.Callout => "Note", _ => "" },
            Color = shape switch { AnnotationShape.Highlight => "#FFE14D", AnnotationShape.Callout => "#FFE14D", _ => "#FF3B30" },
            FadeIn = shape == AnnotationShape.Zoom ? 0.3 : 0,
            FadeOut = shape == AnnotationShape.Zoom ? 0.3 : 0,
        };
        var id = Session.Editor.AddAnnotation(clip);
        Select(id);
        return id;
    }

    /// <summary>Annotations last 5 s, or to the end of the footage under the playhead if sooner.</summary>
    private double DefaultAnnotationDuration()
    {
        var under = Project.AllClips()
            .Where(x => x.Clip is MediaClip && x.Track.Kind == TrackKind.Video && x.Clip.Start <= Playhead && x.Clip.End > Playhead)
            .Select(x => x.Clip.End - Playhead)
            .DefaultIfEmpty(TimelineEditor.DefaultStillDuration)
            .Max();
        return Math.Max(0.5, Math.Min(TimelineEditor.DefaultStillDuration, under));
    }

    [RelayCommand]
    private void AddTrack(string kind) => Session.Editor.AddTrack(kind == "audio" ? TrackKind.Audio : TrackKind.Video);

    // ---- playhead and playback ---------------------------------------------------------------

    partial void OnPlayheadChanged(double value)
    {
        OnPropertyChanged(nameof(PlayheadText));
        if (!IsPlaying)
            RequestStill();
    }

    private bool _settingFromPlayback;

    private void SetPlayheadFromPlayback(double t)
    {
        _settingFromPlayback = true;
        Playhead = t;
        _settingFromPlayback = false;
    }

    /// <summary>Moves the playhead (stopping playback if the user seeks while playing, then resuming from there).</summary>
    public void Seek(double seconds)
    {
        StopRewind();
        var t = Math.Clamp(Timecode.SnapToFrame(seconds, Project.Settings.FrameRate), 0, Math.Max(0, Project.Duration));
        if (IsPlaying && !_settingFromPlayback)
        {
            Playhead = t;
            StartPlayback();
            return;
        }
        Playhead = t;
    }

    /// <summary>Plays at normal speed, or pauses normal playback. From fast-forward or rewind it goes back to normal speed.</summary>
    [RelayCommand]
    private void PlayPause()
    {
        if (IsPlaying && PlaybackSpeed == 1)
        {
            StopPlayback();
            return;
        }
        StopRewind();
        PlaybackSpeed = 1;
        StartPlayback();
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        // Keep the status line in step with the shuttle (and clear its message once it's back to normal).
        if (value is > 1 or < 0)
            Status = value > 1 ? $"Fast-forward {value:0}× (Space: normal speed, K: stop)." : $"Rewinding {-value:0}× (Space: play, K: stop).";
        else if (Status.StartsWith("Fast-forward", StringComparison.Ordinal) || Status.StartsWith("Rewinding", StringComparison.Ordinal))
            Status = "";
    }

    /// <summary>Playback speed: 1 normal, 2/4/8 fast-forward (L), -1/-2/-4/-8 rewind (J); 0 when stopped.</summary>
    [ObservableProperty]
    private double _playbackSpeed;

    public bool IsRewinding => _rewind > 0;

    /// <summary>
    /// J/K/L shuttle, as in other editors: L plays, and each further press doubles the speed (to 8×);
    /// J rewinds the same way; K stops. Pressing the opposite key while shuttling slows down first.
    /// </summary>
    [RelayCommand]
    private void Shuttle(int direction)
    {
        if (direction == 0)
        {
            StopPlayback();
            return;
        }
        var current = IsPlaying || IsRewinding ? PlaybackSpeed : 0;
        double next;
        if (current == 0 || Math.Sign(current) != direction && Math.Abs(current) <= 1)
            next = direction; // start (or reverse from 1×)
        else if (Math.Sign(current) == direction)
            next = Math.Min(8, Math.Abs(current) * 2) * direction;
        else
            next = current / 2; // the other way: slow down first
        if (next < 0)
        {
            _playback.Stop();
            _playheadTimer.Stop();
            IsPlaying = false;
            PlaybackSpeed = next;
            if (!IsRewinding)
                RewindLoop();
        }
        else
        {
            StopRewind();
            PlaybackSpeed = next;
            StartPlayback();
        }
    }

    /// <summary>Rewind: there's no playing backwards, so the playhead steps back and frames are shown as fast as they render.</summary>
    private async void RewindLoop()
    {
        var id = ++_rewindCount;
        _rewind = id;
        OnPropertyChanged(nameof(IsRewinding));
        var last = DateTime.UtcNow;
        while (_rewind == id)
        {
            await Task.Delay(40);
            if (_rewind != id)
                break;
            var now = DateTime.UtcNow;
            var t = Math.Max(0, Playhead + PlaybackSpeed * (now - last).TotalSeconds);
            last = now;
            Playhead = t;
            // Straight away rather than through the short delay (which these steady steps would keep putting off);
            // requests while a frame renders collapse into one more of the latest time.
            _stillTimer.Stop();
            _ = RenderStillAsync();
            if (t <= 0)
                StopPlayback();
        }
    }

    private int _rewindCount;

    private void StopRewind()
    {
        if (_rewind == 0)
            return;
        _rewind = 0;
        OnPropertyChanged(nameof(IsRewinding));
    }

    private void StartPlayback()
    {
        if (Project.Duration <= 0)
            return;
        StopRewind();
        if (Playhead >= Project.Duration - 0.05)
            Playhead = 0;
        if (PlaybackSpeed <= 0)
            PlaybackSpeed = 1;
        var (w, h) = PreviewSize;
        _playback.Play(Project, Playhead, w, h, PlaybackSpeed);
        IsPlaying = true;
        _playheadTimer.Start();
    }

    public void StopPlayback()
    {
        StopRewind();
        PlaybackSpeed = 0;
        _playback.Stop();
        _playheadTimer.Stop();
        if (IsPlaying)
        {
            IsPlaying = false;
            RequestStill();
        }
    }

    private void OnPlaybackStopped(string? error)
    {
        _playheadTimer.Stop();
        if (!IsPlaying)
            return;
        IsPlaying = false;
        PlaybackSpeed = 0;
        if (error != null)
            Status = "Playback stopped: " + error;
        else if (Playhead >= Project.Duration - 0.2)
            Playhead = Project.Duration;
        RequestStill();
    }

    [RelayCommand]
    private void StepFrames(int frames)
    {
        StopPlayback();
        Seek(Playhead + frames / Math.Max(1, Project.Settings.FrameRate));
    }

    [RelayCommand]
    private void StepSeconds(double seconds)
    {
        StopPlayback();
        Seek(Playhead + seconds);
    }

    [RelayCommand]
    private void GoToStart() => Seek(0);

    /// <summary>Goes to the end (stopping playback: playing on from the end would start again from the beginning).</summary>
    [RelayCommand]
    private void GoToEnd()
    {
        StopPlayback();
        Seek(Project.Duration);
    }

    /// <summary>Jumps to the previous or next clip edge.</summary>
    [RelayCommand]
    private void JumpToEdge(int direction)
    {
        var points = Session.Editor.SnapPoints().Append(Project.Duration).Distinct().Order().ToList();
        // While playing, the playhead has just passed the last edge: going back skips it if it's within the last second,
        // as pressing again would otherwise keep landing on the same cut.
        var behind = IsPlaying ? 1.0 : 0.001;
        var target = direction < 0
            ? points.LastOrDefault(p => p < Playhead - behind, 0)
            : points.FirstOrDefault(p => p > Playhead + 0.001, Project.Duration);
        if (direction > 0 && target >= Project.Duration - 0.001)
        {
            GoToEnd();
            return;
        }
        Seek(target);
    }

    /// <summary>Called by the preview when its size changes.</summary>
    public void SetPreviewSize(double width, double height, double renderScale)
    {
        var s = Project.Settings;
        // Never bigger than the project, and capped for smooth playback.
        var maxW = Math.Min(s.Width, Math.Min(1280, width * renderScale));
        var maxH = Math.Min(s.Height, Math.Min(720, height * renderScale));
        var scale = Math.Min(maxW / s.Width, maxH / s.Height);
        var w = Math.Max(64, (int)(s.Width * scale) & ~1);
        var h = Math.Max(36, (int)(s.Height * scale) & ~1);
        if ((w, h) == PreviewSize)
            return;
        PreviewSize = (w, h);
        if (!IsPlaying)
            RequestStill();
    }

    /// <summary>Renders a pending preview now instead of after the short delay (tests).</summary>
    internal async Task FlushPreviewAsync()
    {
        _stillTimer.Stop();
        await RenderStillAsync();
        while (_stillRunning)
            await Task.Delay(10);
    }

    public void RequestStill()
    {
        _stillTimer.Stop();
        _stillTimer.Start();
    }

    private async Task RenderStillAsync()
    {
        if (_restartPlaybackAfterEdit)
        {
            _restartPlaybackAfterEdit = false;
            if (IsPlaying)
            {
                StartPlayback();
                return;
            }
        }
        if (IsPlaying)
            return;
        // One render at a time; requests while it runs collapse into one more render of the
        // latest state, so scrubbing keeps updating instead of cancelling itself.
        if (_stillRunning)
        {
            _stillAgain = true;
            return;
        }
        _stillRunning = true;
        try
        {
            do
            {
                _stillAgain = false;
                var (w, h) = PreviewSize;
                var frame = await _playback.RenderStillAsync(Project, Playhead, w, h);
                if (frame != null && !IsPlaying)
                    ShowFrame(frame);
                else
                    frame?.Dispose();
            } while (_stillAgain && !IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Warn("editor", "Preview frame failed.", ex);
            Status = "Preview failed: " + (ex is FfmpegException fe ? fe.Message : ex.Message);
        }
        finally
        {
            _stillRunning = false;
        }
    }

    private void ShowFrame(VideoFrame frame)
    {
        PreviewTime = frame.Time;
        using (frame)
        {
            if (PreviewBitmap == null || PreviewBitmap.PixelSize.Width != frame.Width || PreviewBitmap.PixelSize.Height != frame.Height)
                PreviewBitmap = new WriteableBitmap(new Avalonia.PixelSize(frame.Width, frame.Height), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using (var fb = PreviewBitmap.Lock())
            {
                for (var y = 0; y < frame.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(frame.Pixels, y * frame.Stride, fb.Address + y * fb.RowBytes, frame.Stride);
            }
        }
        PreviewUpdated?.Invoke();
    }

    // ---- export ------------------------------------------------------------------------------

    /// <summary>Raised to open the export window (handled by the view).</summary>
    public event Action<ExportViewModel>? ExportRequested;
    public event Action? ProjectSettingsRequested;

    [RelayCommand]
    private void Export()
    {
        if (Project.Duration <= 0)
        {
            Status = "Add something to the timeline first.";
            return;
        }
        StopPlayback();
        ExportRequested?.Invoke(new ExportViewModel(ProjectSerializer.Clone(Project), _settings, _dialogs, Session.FilePath));
    }

    [RelayCommand]
    private async Task ExportFrame()
    {
        if (Project.Duration <= 0)
            return;
        var folder = _settings.LastExportFolder ?? AppPaths.VideosDirectory;
        var path = await _dialogs.SaveFileAsync("Save frame as PNG", $"{Project.Name}-{Playhead:0.00}s.png", ".png", "PNG image", folder);
        if (path == null)
            return;
        try
        {
            await new Core.Export.ProjectExporter().ExportFrameAsync(ProjectSerializer.Clone(Project), Playhead, path);
            Status = "Saved " + path;
        }
        catch (Exception ex)
        {
            await _dialogs.AlertAsync("Couldn't save the frame", ex.Message);
        }
    }

    [RelayCommand]
    private void ProjectSettings() => ProjectSettingsRequested?.Invoke();

    public void ApplySettings(ProjectSettings settings) => Session.Editor.UpdateSettings(settings);

    /// <summary>Starts a new project from a recording or screenshot (Capture tab → "Edit").</summary>
    public async Task NewProjectFromFileAsync(string path)
    {
        if (!await ConfirmDiscardAsync())
            return;
        var info = await MediaProbe.Default.ProbeAsync(path);
        var settings = new ProjectSettings
        {
            Width = Math.Max(2, info.Width & ~1),
            Height = Math.Max(2, info.Height & ~1),
            FrameRate = info.Kind == MediaKind.Image ? 30 : info.FrameRate is > 0 and <= 120 ? Math.Round(info.FrameRate, 3) : 30,
        };
        var project = Project.CreateDefault(Path.GetFileNameWithoutExtension(path), settings);
        project.Tracks[0].Name = "Annotations";
        ReplaceSession(NewSession(project));
        var id = Session.Editor.ImportMedia(info);
        Session.Editor.AddMediaClip(id, 0, Project.Tracks.First(t => t.Name == "V1").Id);
        Status = info.Kind == MediaKind.Image
            ? "Screenshot ready: pick a tool above the preview and drag on the picture to annotate, then use Export frame."
            : "Recording ready: pick a tool above the preview and drag on the picture to annotate.";
    }

    public void Dispose()
    {
        _playheadTimer.Stop();
        _autosaveTimer.Stop();
        _playback.Dispose();
    }
}
