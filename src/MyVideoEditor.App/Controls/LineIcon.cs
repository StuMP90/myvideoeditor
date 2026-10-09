using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MyVideoEditor.App.Controls;

/// <summary>
/// A small line icon drawn from geometry on a 24×24 grid (see <see cref="Icons"/>), in the
/// surrounding text colour, so it dims with a disabled button and looks the same on every system
/// (unlike emoji and symbol characters, which come from whichever font each system has).
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));
    /// <summary>Fill the shape as well as outlining it (play, record…).</summary>
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<LineIcon, bool>(nameof(Filled));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 16);

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, FilledProperty, TextElement.ForegroundProperty);
        AffectsMeasure<LineIcon>(SizeProperty);
    }

    public LineIcon()
    {
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data)
            return;
        var brush = GetValue(TextElement.ForegroundProperty) ?? Brushes.White;
        var scale = Size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            var pen = new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(Filled ? brush : null, pen, data);
        }
    }
}

/// <summary>The app's icons: simple shapes on a 24×24 grid, drawn by <see cref="LineIcon"/>.</summary>
public static class Icons
{
    private static Geometry G(string path) => StreamGeometry.Parse(path);

    public static readonly Geometry New = G("M6 3 H14 L19 8 V21 H6 Z M14 3 V8 H19");
    public static readonly Geometry Open = G("M3 6 H9 L11 8 H21 V19 H3 Z");
    public static readonly Geometry Save = G("M5 4 H16 L20 8 V20 H5 Z M8 4 V9 H15 V4 M8 20 V14 H16 V20");
    public static readonly Geometry Import = G("M12 4 V15 M7 10 L12 15 L17 10 M5 20 H19");
    public static readonly Geometry Undo = G("M9 14 L4 9 L9 4 M4 9 H14 A6 6 0 0 1 14 21 H11");
    public static readonly Geometry Redo = G("M15 14 L20 9 L15 4 M20 9 H10 A6 6 0 0 0 10 21 H13");
    public static readonly Geometry Split = G("M6 3 A3 3 0 1 0 6 9 A3 3 0 1 0 6 3 Z M6 15 A3 3 0 1 0 6 21 A3 3 0 1 0 6 15 Z M20 4 L8.1 15.9 M14.5 14.5 L20 20 M8.1 8.1 L11.5 11.5");
    public static readonly Geometry Delete = G("M4 6 H20 M9 6 V4 H15 V6 M6 6 L7 20 H17 L18 6 M10 10 V16 M14 10 V16");
    public static readonly Geometry Title = G("M5 5 H19 M12 5 V20 M9 20 H15");
    public static readonly Geometry ChevronDown = G("M7 10 L12 15 L17 10");
    public static readonly Geometry Settings = G("M4 7 H14 M18 7 H20 M14 5 V9 M4 17 H6 M10 17 H20 M10 15 V19 M4 12 H9 M13 12 H20 M9 10 V14");
    public static readonly Geometry Picture = G("M3 5 H21 V19 H3 Z M3 16 L9 11 L13 14 L16 12 L21 16 M15 8.5 A1.5 1.5 0 1 0 15 8.6");
    public static readonly Geometry Export = G("M12 15 V4 M7 9 L12 4 L17 9 M5 14 V20 H19 V14");

    // Annotation tools.
    public static readonly Geometry Arrow = G("M5 19 L19 5 M10 5 H19 V14");
    public static readonly Geometry Box = G("M4 6 H20 V18 H4 Z");
    public static readonly Geometry Ellipse = G("M3 12 A9 6.5 0 1 0 21 12 A9 6.5 0 1 0 3 12 Z");
    public static readonly Geometry Highlight = G("M9 15 L4 20 H10 L12 18 M9 15 L16 6 L20 9.5 L12 18 Z");
    public static readonly Geometry Spotlight = G("M12 8 A4 4 0 1 0 12 16 A4 4 0 1 0 12 8 Z M12 2 V4 M12 20 V22 M2 12 H4 M20 12 H22 M4.9 4.9 L6.3 6.3 M17.7 17.7 L19.1 19.1 M4.9 19.1 L6.3 17.7 M17.7 6.3 L19.1 4.9");
    public static readonly Geometry Callout = G("M4 5 H20 V16 H11 L6 20 V16 H4 Z");
    public static readonly Geometry Step = G("M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M10.5 9 L12.5 7.5 V16.5 M10.5 16.5 H14.5");
    public static readonly Geometry Blur = G("M12 3 C12 3 6 10 6 14 A6 6 0 0 0 18 14 C18 10 12 3 12 3 Z");
    public static readonly Geometry Pixelate = G("M4 4 H20 V20 H4 Z M4 9.33 H20 M4 14.67 H20 M9.33 4 V20 M14.67 4 V20");
    public static readonly Geometry Zoom = G("M10.5 4 A6.5 6.5 0 1 0 10.5 17 A6.5 6.5 0 1 0 10.5 4 Z M15.5 15.5 L20 20 M10.5 8 V13 M8 10.5 H13");

    // Transport (drawn filled).
    public static readonly Geometry Play = G("M7 4 L20 12 L7 20 Z");
    public static readonly Geometry Pause = G("M6 5 H10 V19 H6 Z M14 5 H18 V19 H14 Z");
    public static readonly Geometry SkipStart = G("M5 5 H7 V19 H5 Z M19 5 V19 L9 12 Z");
    public static readonly Geometry SkipEnd = G("M17 5 H19 V19 H17 Z M5 5 V19 L15 12 Z");
    public static readonly Geometry PreviousCut = G("M11 5 V19 L3 12 Z M20 5 V19 L12 12 Z");
    public static readonly Geometry NextCut = G("M4 5 V19 L12 12 Z M13 5 V19 L21 12 Z");
    public static readonly Geometry PreviousFrame = G("M15 5 L8 12 L15 19");
    public static readonly Geometry NextFrame = G("M9 5 L16 12 L9 19");
    public static readonly Geometry Volume = G("M4 9 H8 L13 5 V19 L8 15 H4 Z M16.5 9 A4 4 0 0 1 16.5 15 M19 6.5 A7.5 7.5 0 0 1 19 17.5");

    // Timeline and elsewhere.
    public static readonly Geometry Plus = G("M12 5 V19 M5 12 H19");
    public static readonly Geometry Minus = G("M5 12 H19");
    public static readonly Geometry Magnet = G("M5 3 H9 V12 A3 3 0 0 0 15 12 V3 H19 V12 A7 7 0 0 1 5 12 Z M5 7 H9 M15 7 H19");
    public static readonly Geometry Close = G("M6 6 L18 18 M18 6 L6 18");
    public static readonly Geometry Reset = G("M4 12 A8 8 0 1 0 6.3 6.3 M4 4 V9 H9");
    public static readonly Geometry Refresh = G("M20 12 A8 8 0 1 1 17.7 6.3 M20 4 V9 H15");
    public static readonly Geometry Record = G("M12 6 A6 6 0 1 0 12 18 A6 6 0 1 0 12 6 Z");
    public static readonly Geometry Stop = G("M6 6 H18 V18 H6 Z");
    public static readonly Geometry Camera = G("M3 8 H7 L9 5 H15 L17 8 H21 V19 H3 Z M12 10 A3.5 3.5 0 1 0 12 17 A3.5 3.5 0 1 0 12 10 Z");
    public static readonly Geometry Region = G("M4 8 V4 H8 M16 4 H20 V8 M20 16 V20 H16 M8 20 H4 V16");

    public static Geometry ForShape(MyVideoEditor.Core.Model.AnnotationShape shape) => shape switch
    {
        MyVideoEditor.Core.Model.AnnotationShape.Arrow => Arrow,
        MyVideoEditor.Core.Model.AnnotationShape.Rectangle => Box,
        MyVideoEditor.Core.Model.AnnotationShape.Ellipse => Ellipse,
        MyVideoEditor.Core.Model.AnnotationShape.Highlight => Highlight,
        MyVideoEditor.Core.Model.AnnotationShape.Spotlight => Spotlight,
        MyVideoEditor.Core.Model.AnnotationShape.Callout => Callout,
        MyVideoEditor.Core.Model.AnnotationShape.Step => Step,
        MyVideoEditor.Core.Model.AnnotationShape.Blur => Blur,
        MyVideoEditor.Core.Model.AnnotationShape.Pixelate => Pixelate,
        _ => Zoom,
    };
}
