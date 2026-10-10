using System.Text.Json.Serialization;

namespace MyVideoEditor.Core.Model;

/// <summary>Something placed on a track for a span of time.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MediaClip), "media")]
[JsonDerivedType(typeof(TitleClip), "title")]
[JsonDerivedType(typeof(AnnotationClip), "annotation")]
public abstract class Clip
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    /// <summary>Where the clip starts on the timeline (seconds).</summary>
    public double Start { get; set; }
    /// <summary>How long it lasts on the timeline (seconds).</summary>
    public double Duration { get; set; }
    /// <summary>Fade from transparent (video) and silence (audio), seconds.</summary>
    public double FadeIn { get; set; }
    /// <summary>Fade to transparent and silence at the end, seconds.</summary>
    public double FadeOut { get; set; }
    public double Opacity { get; set; } = 1.0;
    /// <summary>Transition from the clip before it on the same track (seconds; 0 = a cut).</summary>
    public double TransitionIn { get; set; }
    /// <summary>How that transition looks.</summary>
    public TransitionStyle TransitionStyle { get; set; }
    /// <summary>Which way it moves (wipes, pushes and covers).</summary>
    public TransitionDirection TransitionDirection { get; set; }
    public List<Effect> Effects { get; set; } = [];

    [JsonIgnore]
    public double End => Start + Duration;

    /// <summary>A deep copy with a new id.</summary>
    public Clip CloneWithNewId()
    {
        var copy = ProjectSerializer.Clone(this);
        copy.Id = Ids.New();
        return copy;
    }
}

public enum FitMode
{
    /// <summary>Scale to fit inside the frame, keeping the aspect ratio (letterbox/pillarbox).</summary>
    Fit,
    /// <summary>Scale to cover the whole frame, keeping the aspect ratio (crops the edges).</summary>
    Fill,
    /// <summary>Scale to exactly the frame size, ignoring the aspect ratio.</summary>
    Stretch,
    /// <summary>Original pixel size.</summary>
    Original,
}

/// <summary>A piece of a video, audio or image file.</summary>
public sealed class MediaClip : Clip
{
    public string MediaId { get; set; } = "";
    /// <summary>Where in the source file the clip starts (seconds). Ignored for images.</summary>
    public double InPoint { get; set; }
    /// <summary>Playback speed: 2 = twice as fast. Audio pitch is kept.</summary>
    public double Speed { get; set; } = 1.0;
    public double Volume { get; set; } = 1.0;
    /// <summary>The clip's own sound is not used (video clips with audio).</summary>
    public bool AudioMuted { get; set; }
    public FitMode Fit { get; set; } = FitMode.Fit;
    /// <summary>Extra scale on top of <see cref="Fit"/> (1 = as fitted).</summary>
    public double Scale { get; set; } = 1.0;
    /// <summary>Offset from centred, in project pixels.</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    /// <summary>Degrees clockwise.</summary>
    public double Rotation { get; set; }
    /// <summary>Pixels cropped from each edge of the source before scaling.</summary>
    public int CropLeft { get; set; }
    public int CropTop { get; set; }
    public int CropRight { get; set; }
    public int CropBottom { get; set; }
    /// <summary>Lottie animations: replacement text for text layers, by layer name.</summary>
    public Dictionary<string, string> TextOverrides { get; set; } = [];

    /// <summary>Source time used at the end of the clip.</summary>
    [JsonIgnore]
    public double OutPoint => InPoint + Duration * Speed;
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Text drawn over the video: titles, captions, lower thirds, end cards.</summary>
public sealed class TitleClip : Clip
{
    public string Text { get; set; } = "Title";
    public string FontFamily { get; set; } = "";
    /// <summary>Font size in pixels of a 1080-pixel-high frame (scales with the project's size).</summary>
    public double FontSize { get; set; } = 72;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public TextAlignment Alignment { get; set; } = TextAlignment.Center;
    /// <summary>
    /// Position as a fraction of the frame (0..1): X is the left edge, centre or right edge of
    /// the text (following <see cref="Alignment"/>), Y the vertical centre.
    /// </summary>
    public double X { get; set; } = 0.5;
    public double Y { get; set; } = 0.5;
    /// <summary>Outline thickness in pixels of a 1080p frame (0 = none).</summary>
    public double OutlineWidth { get; set; }
    public string OutlineColor { get; set; } = "#000000";
    public bool Shadow { get; set; } = true;
    /// <summary>Box behind the text ("" = none), e.g. "#000000".</summary>
    public string BoxColor { get; set; } = "";
    public double BoxOpacity { get; set; } = 0.6;
    /// <summary>Fill the whole frame with this colour behind the text ("" = transparent): title cards.</summary>
    public string BackgroundColor { get; set; } = "";
    /// <summary>A chapter starts here (exports with chapters: DVD, MP4, MKV…), named after the text's first line.</summary>
    public bool StartsChapter { get; set; }

    /// <summary>A picture shown with the text, such as a logo ("" = none): PNG, JPEG, WebP, BMP or GIF.</summary>
    public string ImagePath { get; set; } = "";
    public TitleImagePlacement ImagePlacement { get; set; } = TitleImagePlacement.Above;
    /// <summary>The picture's height in pixels of a 1080-pixel-high frame (not for a background, which fills the frame).</summary>
    public double ImageSize { get; set; } = 240;

    /// <summary>How the title comes in, over <see cref="AnimationInDuration"/> seconds from its start.</summary>
    public TitleMotion AnimationIn { get; set; }
    public double AnimationInDuration { get; set; } = 0.8;
    /// <summary>How it goes out, over the last <see cref="AnimationOutDuration"/> seconds.</summary>
    public TitleMotion AnimationOut { get; set; }
    public double AnimationOutDuration { get; set; } = 0.6;

    /// <summary>
    /// Scrolls the text through the frame over the clip's whole length (its length sets the speed).
    /// In and out animations don't apply to a scrolling title.
    /// </summary>
    public TitleScroll Scroll { get; set; }
    /// <summary>Scrolling speed in pixels per second of a 1080-pixel-high frame; 0 = fit the scroll to the clip's length.</summary>
    public double ScrollSpeed { get; set; }
    /// <summary>Tickers: repeat the text seamlessly for as long as the clip lasts (at <see cref="ScrollSpeed"/>).</summary>
    public bool ScrollLoop { get; set; }

    /// <summary>A looping ticker's speed when none is set (pixels per second at 1080p).</summary>
    public const double DefaultTickerSpeed = 150;

    [JsonIgnore]
    public bool IsAnimated => Scroll == TitleScroll.None
                              && (AnimationIn != TitleMotion.None && AnimationInDuration > 0.01
                                  || AnimationOut != TitleMotion.None && AnimationOutDuration > 0.01);
}

public enum TitleScroll
{
    None,
    /// <summary>End credits: up from below the frame and off the top. Lines written "Role | Name" form two columns.</summary>
    Credits,
    /// <summary>An opening crawl: justified text receding up into the distance, fading as it goes.</summary>
    Crawl,
    /// <summary>A news ticker: one line from right to left, at the title's height.</summary>
    Ticker,
}

public enum TitleImagePlacement
{
    Above,
    Below,
    Left,
    Right,
    /// <summary>Fills the frame behind the text (cropped to fit), as on a title card.</summary>
    Background,
}

/// <summary>
/// Title animations. Coming in, a slide arrives from that side; going out, it leaves towards it.
/// Typewriter, words and lines build the text up (in) or take it away in reverse (out).
/// </summary>
public enum TitleMotion
{
    None,
    SlideLeft,
    SlideRight,
    SlideTop,
    SlideBottom,
    Rise,
    Grow,
    Wipe,
    Typewriter,
    Words,
    Lines,
}

public enum AnnotationShape
{
    Arrow,
    Rectangle,
    Ellipse,
    /// <summary>Translucent marker over an area.</summary>
    Highlight,
    /// <summary>Darkens everything except the area.</summary>
    Spotlight,
    /// <summary>Blurs the area (hide passwords, personal data).</summary>
    Blur,
    /// <summary>Pixelates the area.</summary>
    Pixelate,
    /// <summary>Zooms the frame into the area.</summary>
    Zoom,
    /// <summary>Numbered circle, for step-by-step instructions.</summary>
    Step,
    /// <summary>Text with a speech-bubble style box.</summary>
    Callout,
}

/// <summary>
/// A shape over the video for bug reports and demos. Coordinates are fractions of the frame
/// (0..1), so annotations stay in place when the project size changes. For arrows, (X1,Y1) is
/// the tail and (X2,Y2) the head; for the other shapes they are opposite corners.
/// </summary>
public sealed class AnnotationClip : Clip
{
    public AnnotationShape Shape { get; set; } = AnnotationShape.Rectangle;
    public double X1 { get; set; } = 0.35;
    public double Y1 { get; set; } = 0.35;
    public double X2 { get; set; } = 0.65;
    public double Y2 { get; set; } = 0.65;
    public string Color { get; set; } = "#FF3B30";
    /// <summary>Line width in pixels of a 1080p frame.</summary>
    public double StrokeWidth { get; set; } = 6;
    /// <summary>
    /// Blur radius / pixel block size (in pixels of a 1080p frame), or highlight opacity /
    /// spotlight darkness (0..1), depending on the shape.
    /// </summary>
    public double Strength { get; set; } = 20;
    /// <summary>Text for Callout, number for Step.</summary>
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 40;

    [JsonIgnore]
    public double Left => Math.Min(X1, X2);
    [JsonIgnore]
    public double Top => Math.Min(Y1, Y2);
    [JsonIgnore]
    public double Right => Math.Max(X1, X2);
    [JsonIgnore]
    public double Bottom => Math.Max(Y1, Y2);

    /// <summary>Shapes ffmpeg applies to the picture beneath, rather than drawn graphics.</summary>
    [JsonIgnore]
    public bool IsFilterShape => Shape is AnnotationShape.Blur or AnnotationShape.Pixelate or AnnotationShape.Zoom;

    public static double DefaultStrength(AnnotationShape shape) => shape switch
    {
        AnnotationShape.Blur => 20,
        AnnotationShape.Pixelate => 16,
        AnnotationShape.Spotlight => 0.6,
        AnnotationShape.Highlight => 0.35,
        _ => 0,
    };
}

/// <summary>An effect on a clip: a type from <see cref="Rendering.EffectCatalog"/> and its settings.</summary>
public sealed class Effect
{
    public string Type { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Parameters { get; set; } = [];

    public double Get(string name, double fallback) => Parameters.TryGetValue(name, out var v) ? v : fallback;
}

/// <summary>Transitions between two clips that touch on a track. All but Dissolve use ffmpeg's xfade filter.</summary>
public enum TransitionStyle
{
    Dissolve,
    DipToBlack,
    DipToWhite,
    Wipe,
    SoftWipe,
    Push,
    Cover,
    CircleOpen,
    CircleClose,
    Clock,
    BarnDoors,
    Zoom,
    Pixelate,
    Blur,
}

/// <summary>The way a wipe, push or cover moves: the incoming clip comes from the opposite side.</summary>
public enum TransitionDirection
{
    Left,
    Right,
    Up,
    Down,
}

public static class Transitions
{
    /// <summary>Names for the UI, in menu order.</summary>
    public static IReadOnlyList<(TransitionStyle Style, string Label)> All { get; } =
    [
        (TransitionStyle.Dissolve, "Dissolve"),
        (TransitionStyle.DipToBlack, "Dip to black"),
        (TransitionStyle.DipToWhite, "Dip to white"),
        (TransitionStyle.Wipe, "Wipe"),
        (TransitionStyle.SoftWipe, "Soft wipe"),
        (TransitionStyle.Push, "Push"),
        (TransitionStyle.Cover, "Cover"),
        (TransitionStyle.CircleOpen, "Circle open"),
        (TransitionStyle.CircleClose, "Circle close"),
        (TransitionStyle.Clock, "Clock wipe"),
        (TransitionStyle.BarnDoors, "Barn doors"),
        (TransitionStyle.Zoom, "Zoom"),
        (TransitionStyle.Pixelate, "Pixelate"),
        (TransitionStyle.Blur, "Blur"),
    ];

    public static bool HasDirection(TransitionStyle style) =>
        style is TransitionStyle.Wipe or TransitionStyle.SoftWipe or TransitionStyle.Push or TransitionStyle.Cover;

    /// <summary>The xfade transition name; null for a dissolve (drawn with fades, no xfade needed).</summary>
    public static string? XfadeName(TransitionStyle style, TransitionDirection direction)
    {
        var dir = direction.ToString().ToLowerInvariant();
        return style switch
        {
            TransitionStyle.DipToBlack => "fadeblack",
            TransitionStyle.DipToWhite => "fadewhite",
            TransitionStyle.Wipe => "wipe" + dir,
            TransitionStyle.SoftWipe => "smooth" + dir,
            TransitionStyle.Push => "slide" + dir,
            TransitionStyle.Cover => "cover" + dir,
            TransitionStyle.CircleOpen => "circleopen",
            TransitionStyle.CircleClose => "circleclose",
            TransitionStyle.Clock => "radial",
            TransitionStyle.BarnDoors => "vertopen",
            TransitionStyle.Zoom => "zoomin",
            TransitionStyle.Pixelate => "pixelize",
            TransitionStyle.Blur => "hblur",
            _ => null,
        };
    }
}
