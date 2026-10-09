using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MyVideoEditor.Core.Capture;

namespace MyVideoEditor.App.Views;

/// <summary>
/// A full-screen overlay on each monitor: drag out the area to record. Esc or right-click
/// cancels. It shows a dimmed screenshot of the monitor rather than relying on window
/// transparency, which isn't available everywhere (X11 without a compositor). Everything is
/// drawn by a control inside the window: a window's own Render is painted over by its background.
/// </summary>
public sealed class RegionSelectWindow : Window
{
    private readonly TaskCompletionSource<CaptureTarget?> _result;
    private readonly PixelRect _screenBounds;
    private Point? _start;
    private Point _current;

    private readonly Bitmap? _screenshot;

    internal RegionSelectWindow(PixelRect screenBounds, double scaling, Bitmap? screenshot, TaskCompletionSource<CaptureTarget?> result)
    {
        Content = new Surface(this);
        _screenBounds = screenBounds;
        _screenshot = screenshot;
        _result = result;
        SystemDecorations = SystemDecorations.None;
        Background = screenshot == null ? new SolidColorBrush(Colors.Black, 0.25) : Brushes.Black;
        TransparencyLevelHint = screenshot == null ? [WindowTransparencyLevel.Transparent] : [WindowTransparencyLevel.None];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Position = screenBounds.Position;
        Width = screenBounds.Width / scaling;
        Height = screenBounds.Height / scaling;
        Cursor = new Cursor(StandardCursorType.Cross);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Finish(null);
        };
    }

    public static async Task<CaptureTarget?> SelectAsync(Window owner)
    {
        var screens = owner.Screens.All;
        if (screens.Count == 0)
            return null;
        var result = new TaskCompletionSource<CaptureTarget?>();
        // Get the app out of the way, then photograph each monitor to draw the overlay on.
        WindowHider.Hide(owner);
        await Task.Delay(400);
        var shots = new List<Bitmap?>();
        foreach (var s in screens)
            shots.Add(await ScreenshotAsync(s.Bounds));
        var windows = screens.Select((s, i) => new RegionSelectWindow(s.Bounds, s.Scaling, shots[i], result)).ToList();
        foreach (var w in windows)
            w.Show();
        windows.FirstOrDefault()?.Activate();
        CaptureTarget? region;
        try
        {
            region = await result.Task;
        }
        finally
        {
            foreach (var w in windows)
                w.Close();
            foreach (var shot in shots)
                shot?.Dispose();
            WindowHider.Restore(owner);
        }
        return region;
    }

    private static async Task<Bitmap?> ScreenshotAsync(PixelRect bounds)
    {
        var file = Path.Combine(Path.GetTempPath(), $"myvideoeditor-region-{Guid.NewGuid():N}.png");
        try
        {
            var target = new CaptureTarget(CaptureTargetKind.Region, "", bounds.X, bounds.Y, bounds.Width, bounds.Height);
            await new ScreenRecorder().ScreenshotAsync(target, file);
            await using var stream = File.OpenRead(file);
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Warn("capture", "Screenshot for region selection failed; using a see-through overlay.", ex);
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>Draws the overlay (the window's own Render would be covered by its background).</summary>
    private sealed class Surface(RegionSelectWindow owner) : Control
    {
        public override void Render(DrawingContext ctx) => owner.Draw(ctx);
    }

    internal bool HasScreenshot => _screenshot != null;

    private void Draw(DrawingContext ctx)
    {
        if (_screenshot != null)
        {
            ctx.DrawImage(_screenshot, new Rect(_screenshot.Size), new Rect(Bounds.Size));
            ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.35), new Rect(Bounds.Size));
            if (_start is { } sel)
            {
                // The chosen area undimmed.
                var r = new Rect(sel, _current).Normalize();
                var sx = _screenshot.Size.Width / Bounds.Width;
                var sy = _screenshot.Size.Height / Bounds.Height;
                ctx.DrawImage(_screenshot, new Rect(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy), r);
            }
        }
        var hint = new FormattedText("Drag to choose the area to record · Esc to cancel", System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 18, Brushes.White);
        ctx.FillRectangle(new SolidColorBrush(Colors.Black, 0.6), new Rect(Bounds.Width / 2 - hint.Width / 2 - 12, 30, hint.Width + 24, hint.Height + 12), 6);
        ctx.DrawText(hint, new Point(Bounds.Width / 2 - hint.Width / 2, 36));
        if (_start is { } s)
        {
            var r = new Rect(s, _current).Normalize();
            ctx.FillRectangle(new SolidColorBrush(Colors.White, 0.15), r);
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#FF5C48")), 2), r);
            var scaling = RenderScaling;
            var label = new FormattedText($"{(int)(r.Width * scaling)} × {(int)(r.Height * scaling)}", System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Inter"), 13, Brushes.White);
            ctx.DrawText(label, new Point(r.X + 4, r.Bottom + 4));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            Finish(null);
            return;
        }
        _start = _current = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_start == null)
            return;
        _current = e.GetPosition(this);
        (Content as Control)?.InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_start is not { } s)
            return;
        var r = new Rect(s, e.GetPosition(this)).Normalize();
        if (r.Width < 16 || r.Height < 16)
        {
            _start = null;
            (Content as Control)?.InvalidateVisual();
            return;
        }
        var scaling = RenderScaling;
        var x = _screenBounds.X + (int)Math.Round(r.X * scaling);
        var y = _screenBounds.Y + (int)Math.Round(r.Y * scaling);
        var w = (int)Math.Round(r.Width * scaling) & ~1;
        var h = (int)Math.Round(r.Height * scaling) & ~1;
        Finish(new CaptureTarget(CaptureTargetKind.Region, "Region", x, y, w, h));
    }

    private void Finish(CaptureTarget? target) => _result.TrySetResult(target);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Finish(null);
    }
}
