using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MyVideoEditor.App.Controls;

/// <summary>
/// Shows a frame before and after conversion: a wipe (drag the divider) or side by side.
/// Zoom 0 fits the window; 1 and 2 are 100% and 200% of the converted frame's pixels (drag to
/// pan; both halves move together). The original is stretched to the same size without
/// smoothing, so its real pixels are visible.
/// </summary>
public sealed class ComparisonControl : Control
{
    public static readonly StyledProperty<Bitmap?> BeforeProperty = AvaloniaProperty.Register<ComparisonControl, Bitmap?>(nameof(Before));
    public static readonly StyledProperty<Bitmap?> AfterProperty = AvaloniaProperty.Register<ComparisonControl, Bitmap?>(nameof(After));
    public static readonly StyledProperty<bool> SideBySideProperty = AvaloniaProperty.Register<ComparisonControl, bool>(nameof(SideBySide));
    public static readonly StyledProperty<int> ZoomProperty =
        AvaloniaProperty.Register<ComparisonControl, int>(nameof(Zoom), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<string> BeforeLabelProperty = AvaloniaProperty.Register<ComparisonControl, string>(nameof(BeforeLabel), "Original");
    public static readonly StyledProperty<string> AfterLabelProperty = AvaloniaProperty.Register<ComparisonControl, string>(nameof(AfterLabel), "Converted");
    /// <summary>When set, this one picture is shown instead of the comparison (the frame being chosen).</summary>
    public static readonly StyledProperty<Bitmap?> SingleProperty = AvaloniaProperty.Register<ComparisonControl, Bitmap?>(nameof(Single));
    public static readonly StyledProperty<string> SingleLabelProperty = AvaloniaProperty.Register<ComparisonControl, string>(nameof(SingleLabel), "");
    /// <summary>While set, the picture is dimmed and this message shown over it (a render in progress).</summary>
    public static readonly StyledProperty<string?> OverlayProperty = AvaloniaProperty.Register<ComparisonControl, string?>(nameof(Overlay));

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IPen DividerPen = new Pen(Brushes.White, 2);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Colors.Black, 0.6);

    private double _split = 0.5;
    private Vector _pan;
    private enum Drag { None, Divider, Pan }
    private Drag _drag;
    private Point _dragStart;
    private Vector _panStart;

    static ComparisonControl()
    {
        AffectsRender<ComparisonControl>(BeforeProperty, AfterProperty, SideBySideProperty, ZoomProperty, BeforeLabelProperty, AfterLabelProperty, OverlayProperty, SingleProperty, SingleLabelProperty);
    }

    public ComparisonControl()
    {
        ClipToBounds = true;
    }

    public Bitmap? Before { get => GetValue(BeforeProperty); set => SetValue(BeforeProperty, value); }
    public Bitmap? After { get => GetValue(AfterProperty); set => SetValue(AfterProperty, value); }
    public bool SideBySide { get => GetValue(SideBySideProperty); set => SetValue(SideBySideProperty, value); }
    public int Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public string BeforeLabel { get => GetValue(BeforeLabelProperty); set => SetValue(BeforeLabelProperty, value); }
    public string AfterLabel { get => GetValue(AfterLabelProperty); set => SetValue(AfterLabelProperty, value); }
    public string? Overlay { get => GetValue(OverlayProperty); set => SetValue(OverlayProperty, value); }
    public Bitmap? Single { get => GetValue(SingleProperty); set => SetValue(SingleProperty, value); }
    public string SingleLabel { get => GetValue(SingleLabelProperty); set => SetValue(SingleLabelProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty || change.Property == AfterProperty || change.Property == SideBySideProperty)
            _pan = default;
    }

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>Where the frame is drawn within a pane (the whole control, or half of it side by side).</summary>
    private Rect FrameRect(Rect pane)
    {
        if (After is not { } after)
            return default;
        var w = after.PixelSize.Width;
        var h = after.PixelSize.Height;
        var scale = Zoom <= 0
            ? Math.Min(pane.Width / w, pane.Height / h)
            : Zoom / RenderScaling; // 100% = one converted pixel per screen pixel
        var size = new Size(w * scale, h * scale);
        var x = pane.X + (pane.Width - size.Width) / 2 + _pan.X;
        var y = pane.Y + (pane.Height - size.Height) / 2 + _pan.Y;
        return new Rect(new Point(x, y), size);
    }

    public override void Render(DrawingContext ctx)
    {
        RenderPictures(ctx);
        if (Overlay is { } message)
        {
            var bounds = new Rect(Bounds.Size);
            ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.65), bounds);
            var t = Text(message, 15, Brushes.White);
            t.MaxTextWidth = Math.Max(100, bounds.Width - 40);
            t.TextAlignment = TextAlignment.Center;
            ctx.DrawText(t, new Point(20, bounds.Height / 2 - t.Height / 2));
        }
    }

    private void RenderPictures(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        if (Single is { } single)
        {
            ctx.FillRectangle(Background, bounds);
            var fit = Math.Min(bounds.Width / single.Size.Width, bounds.Height / single.Size.Height);
            var size = single.Size * fit;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                ctx.DrawImage(single, new Rect(single.Size), new Rect(bounds.Center.X - size.Width / 2, bounds.Center.Y - size.Height / 2, size.Width, size.Height));
            if (SingleLabel.Length > 0)
                Label(ctx, SingleLabel, new Point(8, 8));
            return;
        }
        ctx.FillRectangle(Background, bounds);
        if (After is not { } after || Before is not { } before)
        {
            if (Overlay != null)
                return;
            var hint = Text("Choose a file and press Preview frame to compare the original with the converted result.", 13, new SolidColorBrush(Colors.White, 0.55));
            hint.MaxTextWidth = Math.Max(100, bounds.Width - 40);
            hint.TextAlignment = TextAlignment.Center;
            ctx.DrawText(hint, new Point(20, bounds.Height / 2 - hint.Height / 2));
            return;
        }

        // Real pixels when zoomed in; smooth when shrunk to fit.
        var afterMode = Zoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        if (SideBySide)
        {
            var left = new Rect(0, 0, bounds.Width / 2 - 1, bounds.Height);
            var right = new Rect(bounds.Width / 2 + 1, 0, bounds.Width / 2 - 1, bounds.Height);
            DrawPane(ctx, before, left, BitmapInterpolationMode.None);
            DrawPane(ctx, after, right, afterMode);
            ctx.FillRectangle(Brushes.White, new Rect(bounds.Width / 2 - 1, 0, 2, bounds.Height));
            Label(ctx, BeforeLabel, new Point(8, 8));
            Label(ctx, AfterLabel, new Point(right.X + 8, 8));
            return;
        }

        var frame = FrameRect(bounds);
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = afterMode }))
            ctx.DrawImage(after, new Rect(after.Size), frame);
        var splitX = bounds.Width * _split;
        using (ctx.PushClip(new Rect(0, 0, splitX, bounds.Height)))
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            ctx.DrawImage(before, new Rect(before.Size), frame);
        ctx.DrawLine(DividerPen, new Point(splitX, 0), new Point(splitX, bounds.Height));
        ctx.DrawEllipse(Brushes.White, null, new Point(splitX, bounds.Height / 2), 9, 9);
        ctx.DrawText(Text("⇔", 12, Brushes.Black), new Point(splitX - 6, bounds.Height / 2 - 8));
        Label(ctx, BeforeLabel, new Point(Math.Max(8, splitX - 8 - Text(BeforeLabel, 12, Brushes.White).Width - 8), 8));
        Label(ctx, AfterLabel, new Point(splitX + 8, 8));
    }

    private void DrawPane(DrawingContext ctx, Bitmap bitmap, Rect pane, BitmapInterpolationMode mode)
    {
        using (ctx.PushClip(pane))
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            ctx.DrawImage(bitmap, new Rect(bitmap.Size), FrameRect(pane));
    }

    private static void Label(DrawingContext ctx, string text, Point at)
    {
        var t = Text(text, 12, Brushes.White);
        ctx.FillRectangle(LabelBackground, new Rect(at.X - 4, at.Y - 2, t.Width + 8, t.Height + 4), 3);
        ctx.DrawText(t, at);
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Inter"), size, brush);

    // ---- input -------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (After == null)
            return;
        var p = e.GetPosition(this);
        _dragStart = p;
        _panStart = _pan;
        var nearDivider = !SideBySide && Math.Abs(p.X - Bounds.Width * _split) < 12;
        _drag = nearDivider || Zoom <= 0 && !SideBySide ? Drag.Divider : Drag.Pan;
        if (_drag == Drag.Divider)
            _split = Math.Clamp(p.X / Bounds.Width, 0, 1);
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        Cursor = !SideBySide && Math.Abs(p.X - Bounds.Width * _split) < 12 ? new Cursor(StandardCursorType.SizeWestEast)
            : Zoom > 0 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        switch (_drag)
        {
            case Drag.Divider:
                _split = Math.Clamp(p.X / Bounds.Width, 0, 1);
                break;
            case Drag.Pan:
                _pan = _panStart + (p - _dragStart);
                break;
            default:
                return;
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Zoom = Math.Clamp(Zoom + (e.Delta.Y > 0 ? 1 : -1), 0, 2);
        e.Handled = true;
    }
}
