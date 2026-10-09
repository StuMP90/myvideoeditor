using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Controls;

/// <summary>
/// Many versions of one frame side by side in a grid, sharing zoom and pan: dragging any picture
/// moves all of them to the same place, so the same detail is compared everywhere. Zoom 0 fits
/// each picture to its cell; 1, 2 and 3 are 100%, 200% and 400% (unsmoothed). Double-click a
/// picture to see it alone; double-click or Esc to go back.
/// </summary>
public sealed class MultiCompareControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<CompareTileViewModel>?> TilesProperty =
        AvaloniaProperty.Register<MultiCompareControl, IReadOnlyList<CompareTileViewModel>?>(nameof(Tiles));
    public static readonly StyledProperty<int> ZoomProperty =
        AvaloniaProperty.Register<MultiCompareControl, int>(nameof(Zoom), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B0D11"));
    private static readonly IBrush CellBackground = new SolidColorBrush(Color.Parse("#14171D"));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Colors.Black, 0.7);
    private static readonly IBrush Muted = new SolidColorBrush(Colors.White, 0.6);
    private const double Gap = 6;
    private const double LabelHeight = 22;

    /// <summary>The point of the frame (in picture pixels) shown at the middle of every cell.</summary>
    private Point? _centre;
    private Point _dragStart;
    private Point _centreAtDragStart;
    private bool _dragging;
    private int? _focused;

    public MultiCompareControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<CompareTileViewModel>? Tiles { get => GetValue(TilesProperty); set => SetValue(TilesProperty, value); }
    public int Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TilesProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldList)
                oldList.CollectionChanged -= OnTilesChanged;
            if (change.OldValue is IEnumerable<CompareTileViewModel> oldTiles)
                foreach (var t in oldTiles)
                    t.PropertyChanged -= OnTileChanged;
            if (change.NewValue is INotifyCollectionChanged newList)
                newList.CollectionChanged += OnTilesChanged;
            if (change.NewValue is IEnumerable<CompareTileViewModel> newTiles)
                foreach (var t in newTiles)
                    t.PropertyChanged += OnTileChanged;
        }
        if (change.Property == ZoomProperty && Zoom == 0)
            _centre = null;
        InvalidateVisual();
    }

    private void OnTilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
    private void OnTileChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private static double ZoomFactor(int zoom) => zoom switch { 1 => 1, 2 => 2, 3 => 4, _ => 0 };

    /// <summary>The cells, in tile order (one big cell when a picture is focused).</summary>
    private List<(int Index, Rect Cell)> Layout()
    {
        var list = new List<(int, Rect)>();
        var tiles = Tiles;
        if (tiles == null || tiles.Count == 0)
            return list;
        var bounds = new Rect(Bounds.Size);
        if (_focused is { } f && f < tiles.Count)
        {
            list.Add((f, bounds.Deflate(Gap)));
            return list;
        }
        // Columns chosen so the cells are closest to the pictures' shape.
        var picture = tiles.Select(t => t.Picture).FirstOrDefault(p => p != null);
        var aspect = picture != null ? picture.Size.Width / picture.Size.Height : 16.0 / 9;
        var n = tiles.Count;
        var best = 1;
        var bestScore = double.MaxValue;
        for (var cols = 1; cols <= n; cols++)
        {
            var rows = (int)Math.Ceiling((double)n / cols);
            var cw = (bounds.Width - Gap) / cols - Gap;
            var ch = (bounds.Height - Gap) / rows - Gap - LabelHeight;
            if (cw <= 0 || ch <= 0)
                continue;
            var shown = Math.Min(cw, ch * aspect) * Math.Min(cw / aspect, ch); // picture area shown per cell
            var score = -shown;
            if (score < bestScore)
            {
                bestScore = score;
                best = cols;
            }
        }
        var r = (int)Math.Ceiling((double)n / best);
        var w = (bounds.Width - Gap) / best - Gap;
        var h = (bounds.Height - Gap) / r - Gap;
        for (var i = 0; i < n; i++)
            list.Add((i, new Rect(Gap + i % best * (w + Gap), Gap + i / best * (h + Gap), w, h)));
        return list;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Background, new Rect(Bounds.Size));
        var tiles = Tiles;
        if (tiles == null)
            return;
        foreach (var (index, cell) in Layout())
        {
            var tile = tiles[index];
            ctx.FillRectangle(CellBackground, cell);
            var area = new Rect(cell.X, cell.Y + LabelHeight, cell.Width, Math.Max(0, cell.Height - LabelHeight));
            using (ctx.PushClip(area))
            {
                if (tile.Picture is { } picture)
                {
                    var dest = PictureRect(area, picture.Size);
                    using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = Zoom >= 1 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
                        ctx.DrawImage(picture, new Rect(picture.Size), dest);
                }
                else
                {
                    var t = Text(tile.Detail, 13, Muted);
                    t.MaxTextWidth = Math.Max(50, area.Width - 20);
                    t.TextAlignment = TextAlignment.Center;
                    ctx.DrawText(t, new Point(area.X + 10, area.Center.Y - t.Height / 2));
                }
            }
            using (ctx.PushClip(cell))
            {
                ctx.FillRectangle(LabelBackground, new Rect(cell.X, cell.Y, cell.Width, LabelHeight));
                var label = Text(tile.Label, 12, Brushes.White, FontWeight.SemiBold);
                ctx.DrawText(label, new Point(cell.X + 6, cell.Y + 3));
                if (tile.Picture != null || tile.State == TileState.Rendering)
                {
                    var detail = Text(tile.Detail, 11, Muted);
                    var x = Math.Max(cell.X + 12 + label.Width, cell.Right - detail.Width - 6);
                    ctx.DrawText(detail, new Point(x, cell.Y + 4));
                }
            }
        }
    }

    /// <summary>Where a picture goes in its area: fitted, or at the zoom with the shared centre in the middle.</summary>
    private Rect PictureRect(Rect area, Size picture)
    {
        var factor = ZoomFactor(Zoom);
        if (factor == 0)
        {
            var fit = Math.Min(area.Width / picture.Width, area.Height / picture.Height);
            var size = picture * fit;
            return new Rect(area.Center.X - size.Width / 2, area.Center.Y - size.Height / 2, size.Width, size.Height);
        }
        var scale = factor / RenderScaling;
        var centre = _centre ?? new Point(picture.Width / 2, picture.Height / 2);
        return new Rect(area.Center.X - centre.X * scale, area.Center.Y - centre.Y * scale, picture.Width * scale, picture.Height * scale);
    }

    private Size? PictureSize => Tiles?.Select(t => t.Picture).FirstOrDefault(p => p != null)?.Size;

    // ---- input -------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2)
        {
            _focused = _focused == null ? Layout().FirstOrDefault(c => c.Cell.Contains(p)).Index : null;
            InvalidateVisual();
            return;
        }
        if (Zoom == 0 || PictureSize is not { } size)
            return;
        _dragging = true;
        _dragStart = p;
        _centreAtDragStart = _centre ?? new Point(size.Width / 2, size.Height / 2);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = Zoom > 0 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;
        if (!_dragging || PictureSize is not { } size)
            return;
        var scale = ZoomFactor(Zoom) / RenderScaling;
        var d = (e.GetPosition(this) - _dragStart) / scale;
        _centre = new Point(Math.Clamp(_centreAtDragStart.X - d.X, 0, size.Width), Math.Clamp(_centreAtDragStart.Y - d.Y, 0, size.Height));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Zoom in towards the point under the mouse.
        if (Zoom == 0 && e.Delta.Y > 0 && PictureSize is { } size)
        {
            var hit = Layout().FirstOrDefault(c => c.Cell.Contains(e.GetPosition(this)));
            if (hit.Cell.Width > 0)
            {
                var area = new Rect(hit.Cell.X, hit.Cell.Y + LabelHeight, hit.Cell.Width, hit.Cell.Height - LabelHeight);
                var dest = PictureRect(area, size);
                var p = e.GetPosition(this);
                _centre = new Point(Math.Clamp((p.X - dest.X) / dest.Width * size.Width, 0, size.Width), Math.Clamp((p.Y - dest.Y) / dest.Height * size.Height, 0, size.Height));
            }
        }
        Zoom = Math.Clamp(Zoom + (e.Delta.Y > 0 ? 1 : -1), 0, 3);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _focused != null)
        {
            _focused = null;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private static FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Inter"), FontStyle.Normal, weight), size, brush);
}
