using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.Core.Graphics;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.App.Controls;

/// <summary>
/// The preview picture, with on-picture editing: with an annotation tool chosen, drag to draw
/// it; otherwise click a title, annotation or video under the playhead to select it, drag to
/// move it, and drag an annotation's corner handles to resize it. Changes apply on release.
/// </summary>
public sealed class PreviewControl : Control
{
    private const double HandleSize = 10;

    private static readonly IBrush Letterbox = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.Parse("#4AA3FF")), 1.5, new DashStyle([5, 3], 0));
    private static readonly IPen GhostPen = new Pen(Brushes.White, 2, new DashStyle([5, 3], 0));
    private static readonly IBrush HandleBrush = Brushes.White;
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#4AA3FF")), 1.5);

    private EditorViewModel? _editor;
    private enum DragMode { None, Draw, Move, Handle1, Handle2 }
    private DragMode _drag;
    private Point _start;
    private Point _current;
    private string? _dragClipId;

    public PreviewControl()
    {
        ClipToBounds = true;
        Focusable = true;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragData.FilesOnly(e));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public EditorViewModel? Editor
    {
        get => _editor;
        set
        {
            if (_editor != null)
            {
                _editor.PreviewUpdated -= InvalidateVisual;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }
            _editor = value;
            if (_editor != null)
            {
                _editor.PreviewUpdated += InvalidateVisual;
                _editor.PropertyChanged += OnEditorPropertyChanged;
                _editor.SetPreviewSize(Bounds.Width, Bounds.Height, RenderScaling);
            }
            InvalidateVisual();
        }
    }

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.ActiveTool) or nameof(EditorViewModel.Inspector))
        {
            Cursor = _editor?.ActiveTool != null ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _editor?.SetPreviewSize(e.NewSize.Width, e.NewSize.Height, RenderScaling);
    }

    /// <summary>Where the frame is drawn: the project's aspect ratio, centred.</summary>
    private Rect FrameRect()
    {
        if (_editor == null)
            return default;
        var s = _editor.Project.Settings;
        var scale = Math.Min(Bounds.Width / s.Width, Bounds.Height / s.Height);
        var w = s.Width * scale;
        var h = s.Height * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    private Point ToFraction(Point p, Rect frame) =>
        new(Math.Clamp((p.X - frame.X) / frame.Width, 0, 1), Math.Clamp((p.Y - frame.Y) / frame.Height, 0, 1));

    private static Point FromFraction(double x, double y, Rect frame) => new(frame.X + x * frame.Width, frame.Y + y * frame.Height);

    // ---- drawing -----------------------------------------------------------------------------

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Letterbox, new Rect(Bounds.Size));
        if (_editor == null)
            return;
        var frame = FrameRect();
        if (_editor.PreviewBitmap is { } bitmap)
            ctx.DrawImage(bitmap, new Rect(bitmap.Size), frame);

        if (_editor.Project.Duration <= 0)
        {
            var text = new FormattedText("Import media or drop files here to begin.\nOr open the Capture tab to record your screen.",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Inter"), 15, new SolidColorBrush(Colors.White, 0.6))
            { TextAlignment = Avalonia.Media.TextAlignment.Center };
            ctx.DrawText(text, new Point(Bounds.Width / 2, Bounds.Height / 2 - text.Height / 2));
        }

        if (_drag == DragMode.Draw && _editor.ActiveTool is { } tool)
        {
            DrawShapeOutline(ctx, tool.Shape, _start, _current);
            return;
        }

        if (_editor.Inspector?.ClipId is { } id && _editor.Project.FindClip(id) is { } found && IsUnderPlayhead(found.Clip))
        {
            var (bounds, p1, p2) = Outline(found.Clip, frame);
            if (_drag is DragMode.Move or DragMode.Handle1 or DragMode.Handle2 && _dragClipId == id)
            {
                var d = _current - _start;
                if (_drag == DragMode.Move)
                {
                    bounds = bounds.Translate(d);
                    p1 += d;
                    p2 += d;
                }
                else if (_drag == DragMode.Handle1)
                    p1 += d;
                else
                    p2 += d;
                if (found.Clip is AnnotationClip a)
                {
                    DrawShapeOutline(ctx, a.Shape, p1, p2);
                    return;
                }
                ctx.DrawRectangle(null, GhostPen, bounds);
                return;
            }
            if (found.Clip is AnnotationClip { Shape: AnnotationShape.Arrow })
                ctx.DrawLine(SelectionPen, p1, p2);
            else
                ctx.DrawRectangle(null, SelectionPen, bounds);
            if (found.Clip is AnnotationClip)
            {
                DrawHandle(ctx, p1);
                DrawHandle(ctx, p2);
            }
        }
    }

    private static void DrawHandle(DrawingContext ctx, Point p) =>
        ctx.DrawRectangle(HandleBrush, HandlePen, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize), 2, 2);

    private static void DrawShapeOutline(DrawingContext ctx, AnnotationShape shape, Point a, Point b)
    {
        var rect = new Rect(a, b).Normalize();
        switch (shape)
        {
            case AnnotationShape.Arrow:
                ctx.DrawLine(GhostPen, a, b);
                ctx.DrawEllipse(Brushes.White, null, b, 4, 4);
                break;
            case AnnotationShape.Ellipse:
            case AnnotationShape.Step:
                ctx.DrawEllipse(null, GhostPen, rect.Center, rect.Width / 2, rect.Height / 2);
                break;
            default:
                ctx.DrawRectangle(new SolidColorBrush(Colors.White, 0.08), GhostPen, rect);
                break;
        }
    }

    private bool IsUnderPlayhead(Clip clip) =>
        _editor != null && clip.Start <= _editor.Playhead + 1e-6 && _editor.Playhead < clip.End;

    /// <summary>A clip's on-screen bounds and, for annotations, its two control points.</summary>
    private (Rect Bounds, Point P1, Point P2) Outline(Clip clip, Rect frame)
    {
        var s = _editor!.Project.Settings;
        switch (clip)
        {
            case AnnotationClip a:
            {
                var p1 = FromFraction(a.X1, a.Y1, frame);
                var p2 = FromFraction(a.X2, a.Y2, frame);
                return (new Rect(p1, p2).Normalize(), p1, p2);
            }
            case TitleClip t:
            {
                var w = Math.Max(2, (int)frame.Width);
                var h = Math.Max(2, (int)frame.Height);
                var r = GraphicsRenderer.Measure(t, w, h, s.Width);
                var rect = new Rect(frame.X + r.Left, frame.Y + r.Top, r.Width, r.Height);
                return (rect, rect.TopLeft, rect.BottomRight);
            }
            case MediaClip m when _editor.Project.FindMedia(m.MediaId) is { HasVideo: true } media:
            {
                var scale = frame.Width / s.Width;
                var srcW = Math.Max(2, media.Width - m.CropLeft - m.CropRight);
                var srcH = Math.Max(2, media.Height - m.CropTop - m.CropBottom);
                var (fw, fh) = RenderPlanBuilder.FittedSize(m.Fit, srcW, srcH, (int)frame.Width, (int)frame.Height, scale, m.Scale);
                var cx = frame.Center.X + m.OffsetX * scale;
                var cy = frame.Center.Y + m.OffsetY * scale;
                var rect = new Rect(cx - fw / 2.0, cy - fh / 2.0, fw, fh);
                return (rect, rect.TopLeft, rect.BottomRight);
            }
            default:
                return (default, default, default);
        }
    }

    // ---- input -------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var p = e.GetPosition(this);
        _start = _current = p;
        var frame = FrameRect();

        if (_editor.ActiveTool != null)
        {
            if (!frame.Contains(p))
                return;
            _editor.StopPlayback();
            _drag = DragMode.Draw;
            e.Pointer.Capture(this);
            return;
        }

        // Handles of the selected annotation first.
        if (_editor.Inspector?.ClipId is { } selectedId && _editor.Project.FindClip(selectedId) is { Clip: AnnotationClip sel } && IsUnderPlayhead(sel))
        {
            var (_, p1, p2) = Outline(sel, frame);
            if (Near(p, p1) || Near(p, p2))
            {
                _drag = Near(p, p1) ? DragMode.Handle1 : DragMode.Handle2;
                _dragClipId = sel.Id;
                e.Pointer.Capture(this);
                return;
            }
        }

        var hit = HitTest(p, frame);
        if (hit == null)
        {
            _editor.Select(null);
            return;
        }
        _editor.StopPlayback();
        _editor.Select(hit.Id);
        var track = _editor.Project.FindClip(hit.Id)?.Track;
        if (track is { Locked: false })
        {
            _drag = DragMode.Move;
            _dragClipId = hit.Id;
            e.Pointer.Capture(this);
        }
    }

    private static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) <= HandleSize && Math.Abs(a.Y - b.Y) <= HandleSize;

    /// <summary>The front-most visible clip under a point at the playhead.</summary>
    private Clip? HitTest(Point p, Rect frame)
    {
        foreach (var track in _editor!.Project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden))
        {
            foreach (var clip in track.Clips.Where(IsUnderPlayhead).Reverse())
            {
                var (bounds, p1, p2) = Outline(clip, frame);
                if (clip is AnnotationClip { Shape: AnnotationShape.Arrow })
                {
                    if (DistanceToSegment(p, p1, p2) < 12)
                        return clip;
                    continue;
                }
                if (bounds.Width > 0 && bounds.Inflate(4).Contains(p))
                    return clip;
            }
        }
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        var len = ab.X * ab.X + ab.Y * ab.Y;
        var t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len, 0, 1);
        var proj = a + ab * t;
        return Math.Sqrt(Math.Pow(p.X - proj.X, 2) + Math.Pow(p.Y - proj.Y, 2));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragMode.None)
            return;
        _current = e.GetPosition(this);
        if ((e.KeyModifiers & KeyModifiers.Shift) != 0 && _drag == DragMode.Draw && _editor?.ActiveTool?.Shape != AnnotationShape.Arrow)
        {
            // Shift: square / circle.
            var d = Math.Max(Math.Abs(_current.X - _start.X), Math.Abs(_current.Y - _start.Y));
            _current = new Point(_start.X + Math.Sign(_current.X - _start.X) * d, _start.Y + Math.Sign(_current.Y - _start.Y) * d);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        var drag = _drag;
        _drag = DragMode.None;
        if (_editor == null)
            return;
        var frame = FrameRect();
        var a = ToFraction(_start, frame);
        var b = ToFraction(_current, frame);
        var moved = Math.Abs(_current.X - _start.X) > 3 || Math.Abs(_current.Y - _start.Y) > 3;

        switch (drag)
        {
            case DragMode.Draw when _editor.ActiveTool is { } tool:
                if (!moved)
                    (a, b) = DefaultShape(tool.Shape, a);
                _editor.AddAnnotation(tool.Shape, a.X, a.Y, b.X, b.Y);
                // One annotation per tool pick, then back to selecting (Shift keeps the tool).
                if ((e.KeyModifiers & KeyModifiers.Shift) == 0)
                    _editor.ActiveTool = null;
                break;
            case DragMode.Move or DragMode.Handle1 or DragMode.Handle2 when moved && _dragClipId != null:
                ApplyDrag(drag, _dragClipId, b.X - a.X, b.Y - a.Y, frame);
                break;
        }
        _dragClipId = null;
        InvalidateVisual();
    }

    /// <summary>A sensible shape where the user just clicked (no drag).</summary>
    private static (Point A, Point B) DefaultShape(AnnotationShape shape, Point at) => shape switch
    {
        AnnotationShape.Arrow => (new Point(Math.Max(0, at.X - 0.12), Math.Min(1, at.Y + 0.12)), at),
        AnnotationShape.Step => (new Point(at.X - 0.025, at.Y - 0.045), new Point(at.X + 0.025, at.Y + 0.045)),
        _ => (new Point(Math.Max(0, at.X - 0.12), Math.Max(0, at.Y - 0.08)), new Point(Math.Min(1, at.X + 0.12), Math.Min(1, at.Y + 0.08))),
    };

    private void ApplyDrag(DragMode drag, string clipId, double dx, double dy, Rect frame)
    {
        var editor = _editor!.Session.Editor;
        var projectScale = _editor.Project.Settings.Width / frame.Width;
        editor.UpdateClip(clipId, drag == DragMode.Move ? "Move on screen" : "Resize", clip =>
        {
            switch (clip)
            {
                case AnnotationClip an when drag == DragMode.Move:
                    // Keep the whole shape on the frame.
                    dx = Math.Clamp(dx, -Math.Min(an.X1, an.X2), 1 - Math.Max(an.X1, an.X2));
                    dy = Math.Clamp(dy, -Math.Min(an.Y1, an.Y2), 1 - Math.Max(an.Y1, an.Y2));
                    an.X1 += dx;
                    an.X2 += dx;
                    an.Y1 += dy;
                    an.Y2 += dy;
                    break;
                case AnnotationClip an when drag == DragMode.Handle1:
                    an.X1 = Math.Clamp(an.X1 + dx, 0, 1);
                    an.Y1 = Math.Clamp(an.Y1 + dy, 0, 1);
                    break;
                case AnnotationClip an:
                    an.X2 = Math.Clamp(an.X2 + dx, 0, 1);
                    an.Y2 = Math.Clamp(an.Y2 + dy, 0, 1);
                    break;
                case TitleClip t:
                    t.X = Math.Clamp(t.X + dx, 0, 1);
                    t.Y = Math.Clamp(t.Y + dy, 0, 1);
                    break;
                case MediaClip m:
                    m.OffsetX = Math.Round(m.OffsetX + dx * frame.Width * projectScale);
                    m.OffsetY = Math.Round(m.OffsetY + dy * frame.Height * projectScale);
                    break;
            }
        });
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = DragData.Files(e);
        if (_editor != null && files.Count > 0)
            await _editor.ImportFilesAsync(files, addToTimeline: true);
    }
}
