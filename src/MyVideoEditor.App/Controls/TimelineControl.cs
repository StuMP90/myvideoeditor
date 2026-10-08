using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.App.Controls;

/// <summary>
/// The timeline: a time ruler, track headers (mute / hide / lock), and clips drawn with
/// thumbnails and waveforms. Click the ruler to move the playhead; drag clips to move them
/// (snapping to clip edges and the playhead), drag their edges to trim; Ctrl+wheel zooms.
/// Edits are applied when the mouse is released, as a single undo step.
/// </summary>
public sealed class TimelineControl : Control
{
    private const double HeaderWidth = 128;
    private const double RulerHeight = 28;
    private const double VideoTrackHeight = 64;
    private const double AudioTrackHeight = 48;
    private const double TrackGap = 2;
    private const double EdgeGrab = 7;
    private const double SnapPixels = 9;

    private static readonly Typeface UiFont = new("Inter");
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#14171D"));
    private static readonly IBrush HeaderBackground = new SolidColorBrush(Color.Parse("#1B1F27"));
    private static readonly IBrush RulerBackground = new SolidColorBrush(Color.Parse("#1E232C"));
    private static readonly IBrush TrackBackground = new SolidColorBrush(Color.Parse("#191D24"));
    private static readonly IBrush TrackBackgroundAlt = new SolidColorBrush(Color.Parse("#1C2129"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#D6DCE6"));
    private static readonly IBrush MutedText = new SolidColorBrush(Color.Parse("#7D8796"));
    private static readonly IBrush VideoClip = new SolidColorBrush(Color.Parse("#2F5F9E"));
    private static readonly IBrush AudioClip = new SolidColorBrush(Color.Parse("#2E7D5B"));
    private static readonly IBrush ImageClip = new SolidColorBrush(Color.Parse("#5B4E9E"));
    private static readonly IBrush TitleClipBrush = new SolidColorBrush(Color.Parse("#8A4FA8"));
    private static readonly IBrush AnnotationClipBrush = new SolidColorBrush(Color.Parse("#B8642E"));
    private static readonly IBrush RedactClipBrush = new SolidColorBrush(Color.Parse("#7A7F8A"));
    private static readonly IBrush MissingClip = new SolidColorBrush(Color.Parse("#7A2E2E"));
    private static readonly IBrush WaveBrush = new SolidColorBrush(Color.Parse("#B5F0D2"), 0.85);
    private static readonly IBrush FadeBrush = new SolidColorBrush(Colors.Black, 0.35);
    private static readonly IBrush ToggleOn = new SolidColorBrush(Color.Parse("#E0A030"));
    private static readonly IBrush ToggleOff = new SolidColorBrush(Color.Parse("#2A303B"));
    private static readonly IPen SelectedPen = new Pen(Brushes.White, 2);
    private static readonly IPen ClipBorder = new Pen(new SolidColorBrush(Colors.Black, 0.5), 1);
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.Parse("#FF5C48")), 1.5);
    private static readonly IPen SnapPen = new Pen(new SolidColorBrush(Color.Parse("#FFE14D")), 1, new DashStyle([4, 3], 0));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#5A6372")), 1);
    private static readonly IPen GhostPen = new Pen(new SolidColorBrush(Colors.White, 0.9), 1.5, new DashStyle([5, 3], 0));
    private static readonly IPen TransitionPen = new Pen(new SolidColorBrush(Colors.White, 0.5), 1);

    private readonly Dictionary<(string, int), Bitmap> _thumbnails = [];
    private EditorViewModel? _editor;
    private double _pixelsPerSecond = 60;
    private double _scrollSeconds;
    private double _scrollY;
    private double _lastDuration;
    private string? _lastSelected;

    // Drag state.
    private enum DragMode { None, Scrub, Move, TrimStart, TrimEnd }
    private DragMode _drag;
    private Point _dragOrigin;
    private string? _dragClipId;
    private double _dragOriginalStart;
    private double _dragOriginalEnd;
    private double _dragValue;
    private string? _dragTrackId;
    private double? _snapLine;
    private bool _dragMoved;
    private (double Time, string? TrackId)? _dropHint;

    public TimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => { _dropHint = null; InvalidateVisual(); });
    }

    public EditorViewModel? Editor
    {
        get => _editor;
        set
        {
            if (_editor != null)
            {
                _editor.TimelineInvalidated -= OnTimelineInvalidated;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
                _editor.SessionReplaced -= OnSessionReplaced;
            }
            _editor = value;
            if (_editor != null)
            {
                _editor.TimelineInvalidated += OnTimelineInvalidated;
                _editor.PropertyChanged += OnEditorPropertyChanged;
                _editor.SessionReplaced += OnSessionReplaced;
            }
            InvalidateVisual();
        }
    }

    /// <summary>Horizontal scroll position or extent changed (the view keeps its scrollbar in step).</summary>
    public event Action? ScrollChanged;

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set
        {
            _pixelsPerSecond = Math.Clamp(value, 2, 2000);
            ScrollChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public double ScrollSeconds
    {
        get => _scrollSeconds;
        set
        {
            _scrollSeconds = Math.Max(0, value);
            ScrollChanged?.Invoke();
            InvalidateVisual();
        }
    }

    /// <summary>Vertical scroll (pixels) when there are more tracks than fit.</summary>
    public double ScrollY
    {
        get => _scrollY;
        set
        {
            _scrollY = Math.Clamp(value, 0, Math.Max(0, ContentHeight - TracksViewportHeight));
            ScrollChanged?.Invoke();
            InvalidateVisual();
        }
    }

    public double TracksViewportHeight => Math.Max(0, Bounds.Height - RulerHeight);

    /// <summary>Seconds visible across the clip area.</summary>
    public double ViewportSeconds => Math.Max(0.1, (Bounds.Width - HeaderWidth) / _pixelsPerSecond);

    /// <summary>How far the timeline can scroll: the project plus some room to add more.</summary>
    public double ExtentSeconds => Math.Max((_editor?.Project.Duration ?? 0) + ViewportSeconds * 0.5, ViewportSeconds);

    private Project? Project => _editor?.Project;

    private void OnSessionReplaced()
    {
        _thumbnails.Clear();
        _scrollSeconds = 0;
        ZoomToFit();
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditorViewModel.Playhead))
            return;
        // Keep the playhead in view while playing.
        if (_editor!.IsPlaying && _drag == DragMode.None)
        {
            var x = TimeToX(_editor.Playhead);
            if (x > Bounds.Width - 20 || x < HeaderWidth)
                ScrollSeconds = Math.Max(0, _editor.Playhead - ViewportSeconds * 0.1);
        }
        InvalidateVisual();
    }

    public void ZoomBy(double factor, double? aroundTime = null)
    {
        var anchor = aroundTime ?? _editor?.Playhead ?? 0;
        var anchorX = TimeToX(anchor);
        PixelsPerSecond = _pixelsPerSecond * factor;
        // Keep the anchor where it was on screen.
        ScrollSeconds = anchor - (anchorX - HeaderWidth) / _pixelsPerSecond;
    }

    public void ZoomToFit()
    {
        var duration = Project?.Duration ?? 0;
        var width = Bounds.Width - HeaderWidth - 20;
        PixelsPerSecond = duration > 0 && width > 50 ? width / duration : 60;
        ScrollSeconds = 0;
    }

    // ---- geometry ----------------------------------------------------------------------------

    private double TimeToX(double t) => HeaderWidth + (t - _scrollSeconds) * _pixelsPerSecond;
    private double XToTime(double x) => Math.Max(0, _scrollSeconds + (x - HeaderWidth) / _pixelsPerSecond);
    private static double TrackHeight(Track t) => t.Kind == TrackKind.Video ? VideoTrackHeight : AudioTrackHeight;

    private IEnumerable<(Track Track, Rect Row)> TrackRows()
    {
        if (Project == null)
            yield break;
        var y = RulerHeight - _scrollY;
        foreach (var track in Project.Tracks)
        {
            var h = TrackHeight(track);
            yield return (track, new Rect(0, y, Bounds.Width, h));
            y += h + TrackGap;
        }
    }

    public double ContentHeight => Project?.Tracks.Sum(t => TrackHeight(t) + TrackGap) ?? 0;

    private void OnTimelineInvalidated()
    {
        var duration = Project?.Duration ?? 0;
        // The first clips in an empty project: show them all.
        if (_lastDuration <= 0 && duration > 0)
            ZoomToFit();
        _lastDuration = duration;
        var selected = _editor?.Inspector?.ClipId;
        if (selected != null && selected != _lastSelected)
            EnsureVisible(selected);
        _lastSelected = selected;
        ScrollChanged?.Invoke();
        InvalidateVisual();
    }

    /// <summary>Scrolls so the clip's track is in view (e.g. a new annotation on a new track).</summary>
    private void EnsureVisible(string clipId)
    {
        if (Project?.FindClip(clipId) is not { } found)
            return;
        var top = 0.0;
        foreach (var track in Project.Tracks)
        {
            if (track == found.Track)
                break;
            top += TrackHeight(track) + TrackGap;
        }
        var bottom = top + TrackHeight(found.Track);
        if (top < _scrollY)
            ScrollY = top;
        else if (bottom > _scrollY + TracksViewportHeight)
            ScrollY = bottom - TracksViewportHeight;
    }

    private (Track Track, Rect Row)? TrackAt(double y)
    {
        foreach (var row in TrackRows())
            if (y >= row.Row.Top && y < row.Row.Bottom + TrackGap)
                return row;
        return null;
    }

    private Rect ClipRect(Clip clip, Rect row) =>
        new(TimeToX(clip.Start), row.Top + 2, Math.Max(2, clip.Duration * _pixelsPerSecond), row.Height - 4);

    private (Track Track, Clip Clip, Rect Rect)? ClipAt(Point p)
    {
        if (p.X < HeaderWidth)
            return null;
        foreach (var (track, row) in TrackRows())
        {
            if (p.Y < row.Top || p.Y >= row.Bottom)
                continue;
            foreach (var clip in track.Clips)
            {
                var r = ClipRect(clip, row);
                if (r.Inflate(new Thickness(2, 0)).Contains(p))
                    return (track, clip, r);
            }
        }
        return null;
    }

    // ---- drawing -----------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(Background, bounds);
        if (Project == null)
            return;

        var index = 0;
        foreach (var (track, row) in TrackRows())
        {
            if (row.Bottom < RulerHeight || row.Top > bounds.Height)
            {
                index++;
                continue;
            }
            using (ctx.PushClip(new Rect(HeaderWidth, RulerHeight, Math.Max(0, bounds.Width - HeaderWidth), Math.Max(0, bounds.Height - RulerHeight))))
            {
                ctx.FillRectangle(index++ % 2 == 0 ? TrackBackground : TrackBackgroundAlt, new Rect(HeaderWidth, row.Top, bounds.Width - HeaderWidth, row.Height));
                foreach (var clip in track.Clips)
                    DrawClip(ctx, track, clip, row);
                if (_dropHint is { } hint && hint.TrackId == track.Id)
                {
                    var x = TimeToX(hint.Time);
                    ctx.DrawLine(GhostPen, new Point(x, row.Top), new Point(x, row.Bottom));
                }
            }
            DrawTrackHeader(ctx, track, row);
        }

        DrawDragGhost(ctx);
        DrawRuler(ctx, bounds);

        using (ctx.PushClip(new Rect(HeaderWidth, 0, Math.Max(0, bounds.Width - HeaderWidth), bounds.Height)))
        {
            if (_snapLine is { } snap)
                ctx.DrawLine(SnapPen, new Point(TimeToX(snap), RulerHeight), new Point(TimeToX(snap), bounds.Height));
            var px = TimeToX(_editor!.Playhead);
            ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, bounds.Height));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(px - 6, 0), true);
                g.LineTo(new Point(px + 6, 0));
                g.LineTo(new Point(px + 6, 10));
                g.LineTo(new Point(px, 16));
                g.LineTo(new Point(px - 6, 10));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(PlayheadPen.Brush, null, head);
        }

        if (!Project.Tracks.Any(t => t.Clips.Count > 0))
        {
            var hint = Text("Drag files here or from the media bin, or double-click a file in the bin to add it at the playhead.", 13, MutedText);
            ctx.DrawText(hint, new Point(HeaderWidth + 16, RulerHeight + 20));
        }
    }

    private void DrawRuler(DrawingContext ctx, Rect bounds)
    {
        ctx.FillRectangle(RulerBackground, new Rect(0, 0, bounds.Width, RulerHeight));
        ctx.FillRectangle(HeaderBackground, new Rect(0, 0, HeaderWidth, RulerHeight));
        ctx.DrawText(Text(Timecode.Format(_editor!.Playhead), 12, TextBrush), new Point(10, 7));

        var (major, minor) = TickSpacing();
        var start = Math.Floor(_scrollSeconds / minor) * minor;
        var end = _scrollSeconds + ViewportSeconds;
        using (ctx.PushClip(new Rect(HeaderWidth, 0, Math.Max(0, bounds.Width - HeaderWidth), RulerHeight)))
        {
            for (var t = start; t <= end + minor; t += minor)
            {
                var x = TimeToX(t);
                var isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
                ctx.DrawLine(TickPen, new Point(x, isMajor ? 12 : 20), new Point(x, RulerHeight));
                if (isMajor)
                    ctx.DrawText(Text(RulerLabel(t, major), 10, MutedText), new Point(x + 3, 2));
            }
        }
        ctx.DrawLine(TickPen, new Point(0, RulerHeight - 0.5), new Point(bounds.Width, RulerHeight - 0.5));
    }

    private (double Major, double Minor) TickSpacing()
    {
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        var major = steps.FirstOrDefault(s => s * _pixelsPerSecond >= 90, 3600);
        var minor = major switch { 0.1 => 0.02, 0.2 => 0.05, 0.5 => 0.1, 1 => 0.2, 2 => 0.5, 5 => 1, 10 => 2, 15 => 5, 30 => 5, 60 => 10, 120 => 30, 300 => 60, 600 => 120, _ => major / 4 };
        return (major, minor);
    }

    private static string RulerLabel(double t, double major)
    {
        var whole = major >= 1;
        var minutes = (int)(t / 60);
        var seconds = t - minutes * 60;
        return whole ? $"{minutes}:{(int)Math.Round(seconds):00}" : $"{minutes}:{seconds:00.0}";
    }

    private void DrawTrackHeader(DrawingContext ctx, Track track, Rect row)
    {
        var header = new Rect(0, row.Top, HeaderWidth, row.Height);
        using (ctx.PushClip(new Rect(0, RulerHeight, HeaderWidth, Math.Max(0, Bounds.Height - RulerHeight))))
        {
            ctx.FillRectangle(HeaderBackground, header);
            ctx.DrawText(Text(track.Name, 12, TextBrush, FontWeight.SemiBold), new Point(10, row.Top + 6));
            var x = 10.0;
            foreach (var (label, on, _) in HeaderToggles(track))
            {
                var r = new Rect(x, row.Bottom - 24, 24, 18);
                ctx.DrawRectangle(on ? ToggleOn : ToggleOff, null, r, 3, 3);
                var t = Text(label, 10, on ? Brushes.Black : TextBrush, FontWeight.Bold);
                ctx.DrawText(t, new Point(r.X + (r.Width - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
                x += 28;
            }
        }
    }

    private static List<(string Label, bool On, string Key)> HeaderToggles(Track track)
    {
        var list = new List<(string, bool, string)> { ("M", track.Muted, "mute") };
        if (track.Kind == TrackKind.Video)
            list.Add(("H", track.Hidden, "hide"));
        list.Add(("L", track.Locked, "lock"));
        return list;
    }

    private void DrawClip(DrawingContext ctx, Track track, Clip clip, Rect row)
    {
        var r = ClipRect(clip, row);
        if (r.Right < HeaderWidth || r.Left > Bounds.Width)
            return;
        var media = clip is MediaClip mc ? Project!.FindMedia(mc.MediaId) : null;
        var brush = clip switch
        {
            MediaClip when media == null || media.IsMissing => MissingClip,
            MediaClip when track.Kind == TrackKind.Audio || media!.Kind == MediaKind.Audio => AudioClip,
            MediaClip when media!.Kind == MediaKind.Image => ImageClip,
            MediaClip => VideoClip,
            TitleClip => TitleClipBrush,
            AnnotationClip { IsFilterShape: true } => RedactClipBrush,
            _ => AnnotationClipBrush,
        };
        var dim = track.Hidden && track.Kind == TrackKind.Video;
        using (ctx.PushOpacity(dim ? 0.45 : 1))
        {
            ctx.DrawRectangle(brush, ClipBorder, r, 4, 4);
            using (ctx.PushClip(r.Deflate(1)))
            {
                if (clip is MediaClip m && media is { IsMissing: false })
                {
                    if (track.Kind == TrackKind.Video && media.HasVideo)
                        DrawFilmstrip(ctx, m, media, r);
                    var showWave = media.HasAudio && media.Kind != MediaKind.Image && (track.Kind == TrackKind.Audio || !m.AudioMuted);
                    if (showWave)
                    {
                        var waveHeight = track.Kind == TrackKind.Audio ? r.Height - 16 : 16;
                        DrawWaveform(ctx, m, media, new Rect(r.X, r.Bottom - waveHeight, r.Width, waveHeight), m.Volume * track.Volume);
                    }
                }
                DrawFades(ctx, clip, r);
                var label = clip switch
                {
                    MediaClip mm when mm.Speed != 1 => $"{clip.Name}  ({mm.Speed:0.##}×)",
                    MediaClip mm when mm.AudioMuted && track.Kind == TrackKind.Video && media?.HasAudio == true => clip.Name + "  (muted)",
                    MediaClip when media?.IsMissing != false => clip.Name + "  (missing)",
                    _ => string.IsNullOrEmpty(clip.Name) ? clip.GetType().Name.Replace("Clip", "") : clip.Name,
                };
                if (clip.Effects.Count > 0)
                    label += "  fx";
                var text = Text(label, 11, Brushes.White, FontWeight.SemiBold);
                var textX = Math.Max(r.X + 6, HeaderWidth + 4);
                ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.35), new Rect(textX - 3, r.Y + 2, Math.Min(text.Width + 6, Math.Max(0, r.Right - textX)), text.Height + 2));
                ctx.DrawText(text, new Point(textX, r.Y + 3));
            }
            if (_editor!.IsSelected(clip.Id))
                ctx.DrawRectangle(null, SelectedPen, r.Deflate(1), 4, 4);
        }
    }

    private void DrawFilmstrip(DrawingContext ctx, MediaClip clip, MediaReference media, Rect r)
    {
        var strip = _editor!.Previews.TryGetFilmstrip(media.Path, media.Duration, media.Kind == MediaKind.Image);
        if (strip == null || strip.Frames.Count == 0)
            return;
        var height = r.Height - 18;
        if (height < 12)
            return;
        var width = height * strip.Width / strip.Height;
        var visibleLeft = Math.Max(r.Left, HeaderWidth);
        var visibleRight = Math.Min(r.Right, Bounds.Width);
        // Tiles at fixed positions relative to the clip, so they don't shimmer while scrolling.
        var first = Math.Max(0, (int)((visibleLeft - r.Left) / width));
        for (var x = r.Left + first * width; x < visibleRight; x += width)
        {
            var timelineTime = clip.Start + (x - r.Left) / _pixelsPerSecond;
            var sourceTime = clip.InPoint + (timelineTime - clip.Start) * clip.Speed;
            var frameIndex = media.Kind == MediaKind.Image ? 0 : Math.Clamp((int)(sourceTime / strip.Interval), 0, strip.Frames.Count - 1);
            var bitmap = Thumbnail(media.Path, frameIndex, strip);
            ctx.DrawImage(bitmap, new Rect(0, 0, strip.Width, strip.Height), new Rect(x, r.Top + 17, width, height));
        }
    }

    private Bitmap Thumbnail(string path, int index, Filmstrip strip)
    {
        if (_thumbnails.TryGetValue((path, index), out var bitmap))
            return bitmap;
        if (_thumbnails.Count > 3000)
            _thumbnails.Clear();
        bitmap = EditorViewModel.ToBitmap(strip.Frames[index], strip.Width, strip.Height);
        _thumbnails[(path, index)] = bitmap;
        return bitmap;
    }

    private void DrawWaveform(DrawingContext ctx, MediaClip clip, MediaReference media, Rect area, double gain)
    {
        var wave = _editor!.Previews.TryGetWaveform(media.Path);
        if (wave == null || area.Height < 4)
            return;
        ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.25), area);
        var mid = area.Center.Y;
        var visibleLeft = Math.Max(area.Left, HeaderWidth);
        var visibleRight = Math.Min(area.Right, Bounds.Width);
        var pen = new Pen(WaveBrush, 1);
        const double step = 2;
        for (var x = visibleLeft; x < visibleRight; x += step)
        {
            var t0 = clip.InPoint + (x - area.Left) / _pixelsPerSecond * clip.Speed;
            var t1 = t0 + step / _pixelsPerSecond * clip.Speed;
            var peak = Math.Min(1, wave.Peak(t0, t1) * Math.Sqrt(Math.Max(0, gain)));
            var h = Math.Max(0.5, peak * (area.Height / 2 - 1));
            ctx.DrawLine(pen, new Point(x, mid - h), new Point(x, mid + h));
        }
    }

    private void DrawFades(DrawingContext ctx, Clip clip, Rect r)
    {
        void Triangle(double x0, double x1, bool fadeIn)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, r.Top), true);
                c.LineTo(new Point(x1, r.Top));
                c.LineTo(fadeIn ? new Point(x0, r.Bottom) : new Point(x1, r.Bottom));
                c.EndFigure(true);
            }
            ctx.DrawGeometry(FadeBrush, null, g);
        }

        if (clip.FadeIn > 0)
            Triangle(r.Left, r.Left + clip.FadeIn * _pixelsPerSecond, true);
        if (clip.FadeOut > 0)
            Triangle(r.Right - clip.FadeOut * _pixelsPerSecond, r.Right, false);
        if (clip.TransitionIn > 0)
        {
            var w = clip.TransitionIn * _pixelsPerSecond;
            var band = new Rect(r.Left, r.Top, w, r.Height);
            ctx.FillRectangle(new SolidColorBrush(Colors.White, 0.12), band);
            using (ctx.PushClip(band))
                for (var x = r.Left - r.Height; x < r.Left + w; x += 7)
                    ctx.DrawLine(TransitionPen, new Point(x, r.Bottom), new Point(x + r.Height, r.Top));
        }
    }

    private void DrawDragGhost(DrawingContext ctx)
    {
        if (!_dragMoved || _dragClipId == null || Project?.FindClip(_dragClipId) is not { } found)
            return;
        double start = found.Clip.Start, end = found.Clip.End;
        var trackId = found.Track.Id;
        switch (_drag)
        {
            case DragMode.Move:
                start = _dragValue;
                end = _dragValue + found.Clip.Duration;
                trackId = _dragTrackId ?? trackId;
                break;
            case DragMode.TrimStart:
                start = _dragValue;
                break;
            case DragMode.TrimEnd:
                end = _dragValue;
                break;
            default:
                return;
        }
        var rows = TrackRows().ToList();
        var targets = _drag == DragMode.Move && _editor!.Selection.Count > 1
            ? _editor.Selection.Select(id => Project.FindClip(id)).OfType<(Track Track, Clip Clip)>()
                .Select(x => (x.Track.Id, x.Clip.Start + (_dragValue - _dragOriginalStart), x.Clip.End + (_dragValue - _dragOriginalStart))).ToList()
            : [(trackId, start, end)];
        using (ctx.PushClip(new Rect(HeaderWidth, RulerHeight, Math.Max(0, Bounds.Width - HeaderWidth), Math.Max(0, Bounds.Height - RulerHeight))))
        {
            foreach (var (id, s, e) in targets)
            {
                var row = rows.FirstOrDefault(r => r.Track.Id == id).Row;
                if (row == default)
                    continue;
                var rect = new Rect(TimeToX(s), row.Top + 2, Math.Max(2, (e - s) * _pixelsPerSecond), row.Height - 4);
                ctx.DrawRectangle(new SolidColorBrush(Colors.White, 0.12), GhostPen, rect, 4, 4);
            }
            var label = Text(_drag == DragMode.Move ? Timecode.Format(start) : $"{Timecode.Format(end - start)}", 11, Brushes.White);
            var lx = TimeToX(_drag == DragMode.TrimEnd ? end : start);
            ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.75), new Rect(lx, RulerHeight + 2, label.Width + 8, label.Height + 4));
            ctx.DrawText(label, new Point(lx + 4, RulerHeight + 4));
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(UiFont.FontFamily, FontStyle.Normal, weight), size, brush);

    // ---- input -------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor == null)
            return;
        Focus();
        var p = e.GetPosition(this);
        var point = e.GetCurrentPoint(this);
        var right = point.Properties.IsRightButtonPressed;
        _dragOrigin = p;
        _dragMoved = false;

        if (p.X < HeaderWidth)
        {
            if (p.Y >= RulerHeight && TrackAt(p.Y) is { } row)
                OnHeaderPressed(row.Track, row.Row, p, right);
            return;
        }
        if (p.Y < RulerHeight)
        {
            _editor.StopPlayback();
            _drag = DragMode.Scrub;
            _editor.Seek(XToTime(p.X));
            e.Pointer.Capture(this);
            return;
        }

        var hit = ClipAt(p);
        if (hit == null)
        {
            if (!right)
            {
                _editor.Select(null);
                _editor.Seek(XToTime(p.X));
            }
            else
            {
                ShowTrackMenu(TrackAt(p.Y)?.Track);
            }
            return;
        }

        var (track, clip, rect) = hit.Value;
        var additive = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0;
        if (additive)
            _editor.Select(clip.Id, add: true);
        else if (!_editor.IsSelected(clip.Id) || right)
            _editor.Select(clip.Id);

        if (right)
        {
            ShowClipMenu(clip);
            return;
        }
        if (track.Locked || additive)
            return;

        _dragClipId = clip.Id;
        _dragOriginalStart = clip.Start;
        _dragOriginalEnd = clip.End;
        _dragTrackId = track.Id;
        var nearLeft = p.X - rect.Left <= EdgeGrab && rect.Width > EdgeGrab * 3;
        var nearRight = rect.Right - p.X <= EdgeGrab && rect.Width > EdgeGrab * 3;
        _drag = nearLeft ? DragMode.TrimStart : nearRight ? DragMode.TrimEnd : DragMode.Move;
        _dragValue = _drag == DragMode.TrimEnd ? clip.End : clip.Start;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_editor == null)
            return;
        var p = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            UpdateCursor(p);
            return;
        }
        if (_drag == DragMode.Scrub)
        {
            _editor.Seek(XToTime(p.X));
            return;
        }
        if (!_dragMoved && Math.Abs(p.X - _dragOrigin.X) < 3 && Math.Abs(p.Y - _dragOrigin.Y) < 3)
            return;
        _dragMoved = true;
        var delta = (p.X - _dragOrigin.X) / _pixelsPerSecond;
        var snap = _editor.SnapEnabled && (e.KeyModifiers & KeyModifiers.Alt) == 0;
        var exclude = _editor.Selection.Count > 0 ? _editor.Selection : [_dragClipId!];
        var points = snap ? _editor.Session.Editor.SnapPoints(exclude).Append(_editor.Playhead).ToList() : [];
        _snapLine = null;

        switch (_drag)
        {
            case DragMode.Move:
            {
                var duration = _dragOriginalEnd - _dragOriginalStart;
                var start = Math.Max(0, _dragOriginalStart + delta);
                if (snap)
                {
                    var (s1, d1) = Snap(start, points);
                    var (s2, d2) = Snap(start + duration, points);
                    if (d1 <= d2 && d1 < double.MaxValue)
                    {
                        start = s1;
                        _snapLine = s1;
                    }
                    else if (d2 < double.MaxValue)
                    {
                        start = s2 - duration;
                        _snapLine = s2;
                    }
                }
                _dragValue = Math.Max(0, start);
                if (_editor.Selection.Count <= 1 && TrackAt(p.Y) is { } row && Project!.FindClip(_dragClipId!) is { } found
                    && _editor.Session.Editor.CanHold(row.Track, found.Clip) && !row.Track.Locked)
                    _dragTrackId = row.Track.Id;
                break;
            }
            case DragMode.TrimStart:
            {
                var t = _dragOriginalStart + delta;
                if (snap && Snap(t, points) is var (s, d) && d < double.MaxValue)
                {
                    t = s;
                    _snapLine = s;
                }
                _dragValue = _editor.Session.Editor.ClampTrimStart(_dragClipId!, t);
                break;
            }
            case DragMode.TrimEnd:
            {
                var t = _dragOriginalEnd + delta;
                if (snap && Snap(t, points) is var (s, d) && d < double.MaxValue)
                {
                    t = s;
                    _snapLine = s;
                }
                _dragValue = _editor.Session.Editor.ClampTrimEnd(_dragClipId!, t);
                break;
            }
        }
        // Scroll when dragging past the edges.
        if (p.X > Bounds.Width - 30)
            ScrollSeconds += 8 / _pixelsPerSecond;
        else if (p.X < HeaderWidth + 30 && _scrollSeconds > 0)
            ScrollSeconds -= 8 / _pixelsPerSecond;
        InvalidateVisual();
    }

    /// <summary>The nearest snap point within reach, and its distance (MaxValue if none).</summary>
    private (double Point, double Distance) Snap(double t, List<double> points)
    {
        var best = (t, double.MaxValue);
        foreach (var p in points)
        {
            var d = Math.Abs(p - t) * _pixelsPerSecond;
            if (d <= SnapPixels && d < best.Item2)
                best = (p, d);
        }
        return best;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var drag = _drag;
        _drag = DragMode.None;
        _snapLine = null;
        if (_editor == null || drag == DragMode.Scrub || !_dragMoved || _dragClipId == null)
        {
            _dragMoved = false;
            InvalidateVisual();
            return;
        }
        _dragMoved = false;
        var editor = _editor.Session.Editor;
        switch (drag)
        {
            case DragMode.Move when _editor.Selection.Count > 1:
                editor.MoveClips(_editor.Selection.ToList(), _dragValue - _dragOriginalStart);
                break;
            case DragMode.Move:
                editor.MoveClip(_dragClipId, _dragValue, _dragTrackId);
                break;
            case DragMode.TrimStart:
                editor.TrimStart(_dragClipId, _dragValue);
                break;
            case DragMode.TrimEnd:
                editor.TrimEnd(_dragClipId, _dragValue);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag != DragMode.None)
        {
            _drag = DragMode.None;
            _dragMoved = false;
            _snapLine = null;
            InvalidateVisual();
        }
    }

    private void UpdateCursor(Point p)
    {
        var hit = ClipAt(p);
        if (hit is { } h && !h.Track.Locked)
        {
            var nearEdge = (p.X - h.Rect.Left <= EdgeGrab || h.Rect.Right - p.X <= EdgeGrab) && h.Rect.Width > EdgeGrab * 3;
            Cursor = new Cursor(nearEdge ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
        }
        else
        {
            Cursor = p.Y < RulerHeight && p.X > HeaderWidth ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var p = e.GetPosition(this);
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            ZoomBy(e.Delta.Y > 0 ? 1.25 : 0.8, XToTime(p.X));
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || p.X < HeaderWidth)
        {
            ScrollY = _scrollY - e.Delta.Y * 40;
        }
        else
        {
            var amount = (Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y) * 60 / _pixelsPerSecond;
            ScrollSeconds = Math.Clamp(_scrollSeconds - amount, 0, Math.Max(0, ExtentSeconds - ViewportSeconds * 0.5));
        }
        e.Handled = true;
    }

    // ---- headers and menus -------------------------------------------------------------------

    private void OnHeaderPressed(Track track, Rect row, Point p, bool right)
    {
        if (right)
        {
            ShowTrackMenu(track);
            return;
        }
        var x = 10.0;
        foreach (var (_, on, key) in HeaderToggles(track))
        {
            if (new Rect(x, row.Bottom - 24, 24, 18).Contains(p))
            {
                var editor = _editor!.Session.Editor;
                switch (key)
                {
                    case "mute": editor.UpdateTrack(track.Id, on ? "Unmute track" : "Mute track", t => t.Muted = !on); break;
                    case "hide": editor.UpdateTrack(track.Id, on ? "Show track" : "Hide track", t => t.Hidden = !on); break;
                    case "lock": editor.UpdateTrack(track.Id, on ? "Unlock track" : "Lock track", t => t.Locked = !on); break;
                }
                return;
            }
            x += 28;
        }
    }

    private void ShowTrackMenu(Track? track)
    {
        if (_editor == null)
            return;
        var editor = _editor.Session.Editor;
        var items = new List<Control>
        {
            Item("Add video track", () => editor.AddTrack(TrackKind.Video)),
            Item("Add audio track", () => editor.AddTrack(TrackKind.Audio)),
        };
        if (track != null)
        {
            items.Add(new Separator());
            items.Add(Item($"Rename {track.Name}…", async () =>
            {
                var name = await _editor.Dialogs.PromptTextAsync("Rename track", "Track name:", track.Name);
                if (!string.IsNullOrWhiteSpace(name))
                    editor.UpdateTrack(track.Id, "Rename track", t => t.Name = name.Trim());
            }));
            items.Add(Item($"Remove {track.Name}", async () =>
            {
                if (track.Clips.Count == 0 || await _editor.Dialogs.ConfirmAsync("Remove track", $"Remove {track.Name} and its {track.Clips.Count} clip(s)?", "Remove"))
                    editor.RemoveTrack(track.Id);
            }));
        }
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private void ShowClipMenu(Clip clip)
    {
        var vm = _editor!;
        var items = new List<Control>
        {
            Item("Split at playhead (S)", () => vm.SplitCommand.Execute(null)),
            Item("Duplicate (Ctrl+D)", () => vm.DuplicateCommand.Execute(null)),
            Item("Add dissolve from previous clip", () => vm.AddDissolveCommand.Execute(null)),
        };
        if (clip is MediaClip)
            items.Add(Item("Detach audio to its own track", () => vm.DetachAudioCommand.Execute(null)));
        items.Add(new Separator());
        items.Add(Item("Delete (Del)", () => vm.DeleteCommand.Execute(null)));
        items.Add(Item("Ripple delete: close the gap (Shift+Del)", () => vm.RippleDeleteCommand.Execute(null)));
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---- drag and drop -----------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasMedia = e.DataTransfer.Contains(DragData.MediaId);
        var hasFiles = DragData.HasFiles(e);
        if (!hasMedia && !hasFiles)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = DragDropEffects.Copy;
        var p = e.GetPosition(this);
        _dropHint = (XToTime(Math.Max(HeaderWidth, p.X)), TrackAt(p.Y)?.Track.Id);
        InvalidateVisual();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_editor == null)
            return;
        var p = e.GetPosition(this);
        var time = XToTime(Math.Max(HeaderWidth, p.X));
        var trackId = TrackAt(p.Y)?.Track.Id;
        _dropHint = null;
        InvalidateVisual();
        if (e.DataTransfer.TryGetValue(DragData.MediaId) is { } mediaId)
        {
            var id = _editor.Session.Editor.AddMediaClip(mediaId, time, trackId);
            _editor.Select(id);
            return;
        }
        var files = DragData.Files(e);
        if (files.Count > 0)
            await _editor.ImportFilesAsync(files, addToTimeline: true, time, trackId);
    }
}
