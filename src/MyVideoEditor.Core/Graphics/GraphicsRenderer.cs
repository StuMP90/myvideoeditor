using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MyVideoEditor.Core.Model;
using SkiaSharp;

namespace MyVideoEditor.Core.Graphics;

/// <summary>A rendered title or annotation: a transparent PNG and where it goes on the frame.</summary>
public sealed record RenderedGraphic(string Path, int X, int Y, int Width, int Height);

/// <summary>
/// Draws titles and annotations into transparent PNGs, at the size of the output (export or
/// preview). Images are cached on disk by their content, so an unchanged title is drawn once.
/// The same drawing code produces what the editor shows and what is exported.
///
/// Sizes (font sizes, outlines, line widths) are in pixels of a 1080-pixel-high frame and
/// scale with the frame, so a title looks the same in a 720p, 1080p or 4K project, and in the
/// small preview.
/// </summary>
public sealed class GraphicsRenderer
{
    private readonly string _cacheDirectory;

    public GraphicsRenderer(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.CacheDirectory, "graphics");
    }

    public static GraphicsRenderer Default { get; } = new();

    /// <summary>
    /// Renders a title or (non-filter) annotation for a frame of <paramref name="width"/>×<paramref name="height"/>,
    /// where the project is <paramref name="projectWidth"/>×<paramref name="projectHeight"/>. Returns null if nothing would be visible.
    /// </summary>
    public RenderedGraphic? Render(Clip clip, int width, int height, int projectWidth, int projectHeight)
    {
        var key = CacheKey(clip, width, height, projectWidth, projectHeight);
        var path = Path.Combine(_cacheDirectory, key + ".png");
        var meta = path + ".txt";
        if (File.Exists(path) && File.Exists(meta) && TryReadMeta(meta, path, out var cached))
            return cached;

        var scale = SizeScale(height);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var bounds = clip switch
        {
            TitleClip t => DrawTitle(canvas, t, width, height, scale),
            AnnotationClip a => DrawAnnotation(canvas, a, width, height, scale),
            _ => SKRectI.Empty,
        };
        bounds.Intersect(new SKRectI(0, 0, width, height));
        if (bounds.IsEmpty)
            return null;
        // Even sizes and offsets: the overlay is converted to 4:2:0 video.
        bounds = new SKRectI(bounds.Left & ~1, bounds.Top & ~1, Math.Min(width, (bounds.Right + 1) & ~1), Math.Min(height, (bounds.Bottom + 1) & ~1));
        if (bounds.Width < 2 || bounds.Height < 2)
            return null;

        Directory.CreateDirectory(_cacheDirectory);
        using (var image = surface.Snapshot(bounds))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            using (var file = File.Create(temp))
                data.SaveTo(file);
            File.Move(temp, path, overwrite: true);
        }
        var result = new RenderedGraphic(path, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        File.WriteAllText(meta, string.Create(CultureInfo.InvariantCulture, $"{result.X} {result.Y} {result.Width} {result.Height}"));
        return result;
    }

    /// <summary>Converts sizes given for a 1080-pixel-high frame to a frame of this height.</summary>
    public static double SizeScale(int frameHeight) => frameHeight / 1080.0;

    /// <summary>Draws straight onto a canvas (for the editor's on-screen overlay), at the given output size.</summary>
    public static void Draw(SKCanvas canvas, Clip clip, int width, int height, int projectWidth)
    {
        var scale = SizeScale(height);
        switch (clip)
        {
            case TitleClip t:
                DrawTitle(canvas, t, width, height, scale);
                break;
            case AnnotationClip a:
                DrawAnnotation(canvas, a, width, height, scale);
                break;
        }
    }

    /// <summary>Where a title or annotation is drawn on a frame of the given size (for hit-testing in the editor).</summary>
    public static SKRectI Measure(Clip clip, int width, int height, int projectWidth)
    {
        using var canvas = new SKNoDrawCanvas(width, height);
        var scale = SizeScale(height);
        var bounds = clip switch
        {
            // A title card's background fills the frame; the text block is what matters for picking it.
            TitleClip t => DrawTitle(canvas, WithoutBackground(t), width, height, scale),
            AnnotationClip a => DrawAnnotation(canvas, a, width, height, scale),
            _ => SKRectI.Empty,
        };
        return bounds;
    }

    private static TitleClip WithoutBackground(TitleClip title)
    {
        if (string.IsNullOrEmpty(title.BackgroundColor))
            return title;
        var copy = (TitleClip)title.CloneWithNewId();
        copy.BackgroundColor = "";
        return copy;
    }

    // ---- titles ------------------------------------------------------------------------------

    internal static SKRectI DrawTitle(SKCanvas canvas, TitleClip title, int width, int height, double scale)
    {
        var dirty = SKRect.Empty;
        if (TryParseColor(title.BackgroundColor, out var background))
        {
            canvas.Clear(background);
            dirty = new SKRect(0, 0, width, height);
        }
        var text = (title.Text ?? "").Replace("\r\n", "\n");
        if (text.Trim().Length == 0)
            return Round(dirty);

        var typeface = FontResolver.Resolve(title.FontFamily, title.Bold, title.Italic);
        var fontSize = (float)Math.Max(4, title.FontSize * scale);
        using var font = new SKFont(typeface, fontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var maxWidth = width * 0.92f;
        var lines = Wrap(text, font, maxWidth);
        var metrics = font.Metrics;
        var lineHeight = (-metrics.Ascent + metrics.Descent) * 1.15f;
        var lineWidths = lines.Select(l => MeasureWithFallback(l, font, typeface)).ToList();
        var blockWidth = lineWidths.DefaultIfEmpty(0).Max();
        var blockHeight = lineHeight * lines.Count;
        // X is the left edge, centre or right edge of the text, following its alignment.
        var anchorX = (float)(title.X * width);
        var cy = (float)(title.Y * height);
        var left = title.Alignment switch
        {
            TextAlignment.Left => anchorX,
            TextAlignment.Right => anchorX - blockWidth,
            _ => anchorX - blockWidth / 2,
        };
        var cx = left + blockWidth / 2;
        var top = cy - blockHeight / 2;

        if (TryParseColor(title.BoxColor, out var boxColor))
        {
            var pad = fontSize * 0.35f;
            var box = new SKRect(left - pad, top - pad * 0.6f, left + blockWidth + pad, top + blockHeight + pad * 0.6f);
            using var paint = new SKPaint { Color = boxColor.WithAlpha((byte)(Math.Clamp(title.BoxOpacity, 0, 1) * 255)), IsAntialias = true };
            canvas.DrawRoundRect(box, pad * 0.5f, pad * 0.5f, paint);
            dirty = Union(dirty, box);
        }

        TryParseColor(title.Color, out var color);
        var outline = (float)(title.OutlineWidth * scale);
        TryParseColor(title.OutlineColor, out var outlineColor);
        for (var i = 0; i < lines.Count; i++)
        {
            var x = title.Alignment switch
            {
                TextAlignment.Left => left,
                TextAlignment.Right => left + blockWidth - lineWidths[i],
                _ => cx - lineWidths[i] / 2,
            };
            var baseline = top + i * lineHeight + (-metrics.Ascent) + (lineHeight - (-metrics.Ascent + metrics.Descent)) / 2;
            if (title.Shadow)
            {
                using var shadow = new SKPaint
                {
                    Color = new SKColor(0, 0, 0, 160),
                    IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1, fontSize * 0.06f)),
                };
                DrawTextWithFallback(canvas, lines[i], x + fontSize * 0.04f, baseline + fontSize * 0.05f, font, typeface, shadow);
            }
            if (outline > 0)
            {
                using var stroke = new SKPaint
                {
                    Color = outlineColor,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = outline * 2,
                    StrokeJoin = SKStrokeJoin.Round,
                };
                DrawTextWithFallback(canvas, lines[i], x, baseline, font, typeface, stroke);
            }
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            DrawTextWithFallback(canvas, lines[i], x, baseline, font, typeface, fill);
        }
        var margin = outline + fontSize * 0.2f;
        dirty = Union(dirty, new SKRect(left - margin, top - margin, left + blockWidth + margin, top + blockHeight + margin));
        return Round(dirty);
    }

    /// <summary>Splits text into lines at newlines, and wraps lines wider than <paramref name="maxWidth"/> at spaces.</summary>
    internal static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ');
            var current = new StringBuilder();
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && Measure(font, candidate) > maxWidth)
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(word);
                }
                else
                {
                    current.Clear().Append(candidate);
                }
            }
            lines.Add(current.ToString());
        }
        return lines;
    }

    /// <summary>Runs of text, each with a font that has its characters (so emoji and other scripts show).</summary>
    private static IEnumerable<(string Text, SKTypeface Typeface)> Runs(string text, SKTypeface primary)
    {
        var run = new StringBuilder();
        SKTypeface? runFace = null;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            var codepoint = char.ConvertToUtf32(element, 0);
            var face = primary.ContainsGlyph(codepoint) || char.IsWhiteSpace(element[0])
                ? primary
                : FontResolver.ForCharacter(codepoint, primary) ?? primary;
            if (runFace != null && face != runFace)
            {
                yield return (run.ToString(), runFace);
                run.Clear();
            }
            runFace = face;
            run.Append(element);
        }
        if (run.Length > 0)
            yield return (run.ToString(), runFace ?? primary);
    }

    private static float MeasureWithFallback(string text, SKFont font, SKTypeface primary)
    {
        float total = 0;
        foreach (var (run, face) in Runs(text, primary))
        {
            using var f = new SKFont(face, font.Size);
            total += Measure(f, run);
        }
        return total;
    }

    private static void DrawTextWithFallback(SKCanvas canvas, string text, float x, float y, SKFont font, SKTypeface primary, SKPaint paint)
    {
        foreach (var (run, face) in Runs(text, primary))
        {
            using var f = new SKFont(face, font.Size) { Subpixel = true, Edging = SKFontEdging.Antialias };
            canvas.DrawText(run, x, y, f, paint);
            x += Measure(f, run);
        }
    }

    // ---- annotations -------------------------------------------------------------------------

    internal static SKRectI DrawAnnotation(SKCanvas canvas, AnnotationClip a, int width, int height, double scale)
    {
        TryParseColor(a.Color, out var color);
        var stroke = (float)Math.Max(1, a.StrokeWidth * scale);
        var rect = new SKRect((float)(a.Left * width), (float)(a.Top * height), (float)(a.Right * width), (float)(a.Bottom * height));
        var pad = stroke * 2 + 4;

        switch (a.Shape)
        {
            case AnnotationShape.Arrow:
            {
                var tail = new SKPoint((float)(a.X1 * width), (float)(a.Y1 * height));
                var head = new SKPoint((float)(a.X2 * width), (float)(a.Y2 * height));
                DrawArrow(canvas, tail, head, stroke, color);
                var r = new SKRect(Math.Min(tail.X, head.X), Math.Min(tail.Y, head.Y), Math.Max(tail.X, head.X), Math.Max(tail.Y, head.Y));
                r.Inflate(stroke * 5 + 4, stroke * 5 + 4);
                return Round(r);
            }
            case AnnotationShape.Rectangle:
            {
                using var shadow = ShadowPaint(stroke);
                canvas.DrawRoundRect(rect, stroke, stroke, shadow);
                using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke };
                canvas.DrawRoundRect(rect, stroke, stroke, paint);
                rect.Inflate(pad, pad);
                return Round(rect);
            }
            case AnnotationShape.Ellipse:
            {
                using var shadow = ShadowPaint(stroke);
                canvas.DrawOval(rect, shadow);
                using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke };
                canvas.DrawOval(rect, paint);
                rect.Inflate(pad, pad);
                return Round(rect);
            }
            case AnnotationShape.Highlight:
            {
                var alpha = (byte)(Math.Clamp(a.Strength is > 0 and <= 1 ? a.Strength : 0.35, 0, 1) * 255);
                using var paint = new SKPaint { Color = color.WithAlpha(alpha), IsAntialias = true };
                canvas.DrawRoundRect(rect, 4, 4, paint);
                return Round(rect);
            }
            case AnnotationShape.Spotlight:
            {
                var darkness = Math.Clamp(a.Strength is > 0 and <= 1 ? a.Strength : 0.6, 0, 1);
                using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
                path.AddRect(new SKRect(0, 0, width, height));
                path.AddRoundRect(rect, (float)(12 * scale), (float)(12 * scale));
                using var paint = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(darkness * 255)), IsAntialias = true };
                canvas.DrawPath(path, paint);
                return new SKRectI(0, 0, width, height);
            }
            case AnnotationShape.Step:
            {
                var center = new SKPoint(rect.MidX, rect.MidY);
                var radius = Math.Max(stroke * 2, Math.Min(rect.Width, rect.Height) / 2);
                using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, radius * 0.08f) };
                canvas.DrawCircle(center.X, center.Y + radius * 0.06f, radius, shadow);
                using var fill = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawCircle(center, radius, fill);
                using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1, radius * 0.1f) };
                canvas.DrawCircle(center, radius, ring);
                var label = string.IsNullOrWhiteSpace(a.Text) ? "1" : a.Text.Trim();
                using var font = new SKFont(FontResolver.Resolve(null, true, false), radius * (label.Length > 1 ? 0.9f : 1.15f));
                using var textPaint = new SKPaint { Color = ContrastingText(color), IsAntialias = true };
                var w = Measure(font, label);
                var m = font.Metrics;
                canvas.DrawText(label, center.X - w / 2, center.Y - (m.Ascent + m.Descent) / 2, font, textPaint);
                var r = new SKRect(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
                r.Inflate(radius * 0.2f + 2, radius * 0.2f + 2);
                return Round(r);
            }
            case AnnotationShape.Callout:
            {
                var fontSize = (float)Math.Max(6, a.FontSize * scale);
                var typeface = FontResolver.Resolve(null, true, false);
                using var font = new SKFont(typeface, fontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
                var textPad = fontSize * 0.5f;
                var lines = Wrap(string.IsNullOrWhiteSpace(a.Text) ? "Note" : a.Text, font, Math.Max(fontSize * 4, rect.Width - textPad * 2));
                var lineHeight = (-font.Metrics.Ascent + font.Metrics.Descent) * 1.2f;
                var boxHeight = Math.Max(rect.Height, lines.Count * lineHeight + textPad * 2);
                var box = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + boxHeight);
                using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 100), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, fontSize * 0.25f) };
                var shadowBox = box;
                shadowBox.Offset(0, fontSize * 0.12f);
                canvas.DrawRoundRect(shadowBox, textPad, textPad, shadow);
                using var fill = new SKPaint { Color = color, IsAntialias = true };
                canvas.DrawRoundRect(box, textPad, textPad, fill);
                using var textPaint = new SKPaint { Color = ContrastingText(color), IsAntialias = true };
                for (var i = 0; i < lines.Count; i++)
                    DrawTextWithFallback(canvas, lines[i], box.Left + textPad, box.Top + textPad + i * lineHeight - font.Metrics.Ascent, font, typeface, textPaint);
                box.Inflate(fontSize * 0.5f, fontSize * 0.5f);
                return Round(box);
            }
            default:
                // Blur, pixelate and zoom are ffmpeg filters, not drawn.
                return SKRectI.Empty;
        }
    }

    private static void DrawArrow(SKCanvas canvas, SKPoint tail, SKPoint head, float stroke, SKColor color)
    {
        var dx = head.X - tail.X;
        var dy = head.Y - tail.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1)
            return;
        var ux = dx / length;
        var uy = dy / length;
        var headLength = Math.Min(length * 0.6f, stroke * 4.5f);
        var headWidth = headLength * 0.75f;
        var baseX = head.X - ux * headLength;
        var baseY = head.Y - uy * headLength;

        using var arrow = new SKPath();
        arrow.MoveTo(head);
        arrow.LineTo(baseX - uy * headWidth, baseY + ux * headWidth);
        arrow.LineTo(baseX + uy * headWidth, baseY - ux * headWidth);
        arrow.Close();

        // A soft shadow keeps the arrow visible on any background.
        using var shadow = ShadowPaint(stroke);
        canvas.DrawLine(tail.X, tail.Y, baseX + ux * stroke * 0.5f, baseY + uy * stroke * 0.5f, shadow);
        using var shadowFill = new SKPaint { Color = shadow.Color, IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, stroke * 0.6f) };
        canvas.DrawPath(arrow, shadowFill);

        using var line = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(tail.X, tail.Y, baseX + ux * stroke * 0.5f, baseY + uy * stroke * 0.5f, line);
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(arrow, fill);
    }

    private static SKPaint ShadowPaint(float stroke) => new()
    {
        Color = new SKColor(0, 0, 0, 120),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = stroke * 1.6f,
        StrokeCap = SKStrokeCap.Round,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, stroke * 0.6f),
    };

    private static SKColor ContrastingText(SKColor background) =>
        0.299 * background.Red + 0.587 * background.Green + 0.114 * background.Blue > 160 ? SKColors.Black : SKColors.White;

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB". Empty or invalid gives false (and white).</summary>
    public static bool TryParseColor(string? text, out SKColor color)
    {
        color = SKColors.White;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return SKColor.TryParse(text.Trim(), out color);
    }

    internal static float Measure(SKFont font, string text)
    {
        using var paint = new SKPaint(font);
        return paint.MeasureText(text);
    }

    private static SKRect Union(SKRect a, SKRect b) => a.IsEmpty ? b : b.IsEmpty ? a : SKRect.Union(a, b);

    private static SKRectI Round(SKRect r) => r.IsEmpty
        ? SKRectI.Empty
        : new SKRectI((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom));

    private static string CacheKey(Clip clip, int width, int height, int projectWidth, int projectHeight)
    {
        // Only what affects the picture: timing, opacity and fades are applied by ffmpeg.
        var visual = clip.CloneWithNewId();
        visual.Id = "";
        visual.Name = "";
        visual.Start = visual.Duration = visual.FadeIn = visual.FadeOut = visual.TransitionIn = 0;
        visual.Opacity = 1;
        visual.Effects = [];
        var json = System.Text.Json.JsonSerializer.Serialize(visual, ProjectSerializer.Options);
        var text = string.Create(CultureInfo.InvariantCulture, $"v2|{width}x{height}|{projectWidth}x{projectHeight}|{FontResolver.DefaultFamily}|{json}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();
    }

    private static bool TryReadMeta(string metaPath, string pngPath, out RenderedGraphic? graphic)
    {
        graphic = null;
        try
        {
            var parts = File.ReadAllText(metaPath).Split(' ');
            graphic = new RenderedGraphic(pngPath,
                int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException)
        {
            return false;
        }
    }
}
