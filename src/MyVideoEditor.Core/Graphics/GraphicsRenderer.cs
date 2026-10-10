using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MyVideoEditor.Core.Model;
using SkiaSharp;

namespace MyVideoEditor.Core.Graphics;

/// <summary>A rendered title or annotation: a transparent PNG and where it goes on the frame.</summary>
public sealed record RenderedGraphic(string Path, int X, int Y, int Width, int Height);

/// <summary>
/// A scrolling title's text as one long transparent PNG. A looping ticker repeats every <paramref name="Period"/>
/// pixels after its first frame-width (0 when it doesn't loop).
/// </summary>
public sealed record ScrollStrip(string Path, int Width, int Height, int Period = 0);

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

    /// <summary>
    /// Renders an animated title for clip times [<paramref name="from"/>, <paramref name="to"/>) at
    /// <paramref name="fps"/>, as full-frame transparent PNGs (each drawn once and cached), and returns an
    /// ffconcat list of them for ffmpeg. Frames where the title is still share one picture.
    /// </summary>
    public string RenderAnimatedTitle(TitleClip title, int width, int height, int projectWidth, int projectHeight, double fps, double from, double to)
    {
        Directory.CreateDirectory(_cacheDirectory);
        // The timing (length, animation lengths) matters here, unlike for a still title.
        var key = CacheKey(title, width, height, projectWidth, projectHeight) + string.Create(CultureInfo.InvariantCulture, $"-{title.Duration:0.####}-{fps:0.###}");
        var first = Math.Max(0, (int)Math.Floor(from * fps + 1e-6));
        var last = Math.Max(first, (int)Math.Ceiling(to * fps - 1e-6) - 1);
        var entries = new List<(string File, int Frames)>();
        for (var k = first; k <= last; k++)
        {
            var state = TitleFrame.At(title, k / fps);
            var file = Path.Combine(_cacheDirectory, state.IsStill ? $"{key}-still.png" : $"{key}-{k:00000}.png");
            if (!File.Exists(file))
                RenderFullFrame(title, width, height, state, file);
            if (entries.Count > 0 && entries[^1].File == file)
                entries[^1] = (file, entries[^1].Frames + 1);
            else
                entries.Add((file, 1));
        }
        var list = new StringBuilder("ffconcat version 1.0\n");
        foreach (var (file, frames) in entries)
            list.Append(CultureInfo.InvariantCulture, $"file '{file.Replace("'", "'\\''")}'\nduration {frames / fps:0.######}\n");
        // ffmpeg ignores the last entry's duration unless the file is listed once more.
        list.Append(CultureInfo.InvariantCulture, $"file '{entries[^1].File.Replace("'", "'\\''")}'\n");
        var path = Path.Combine(_cacheDirectory, $"{key}-{first}-{last}.ffconcat");
        File.WriteAllText(path, list.ToString());
        return path;
    }

    private static void RenderFullFrame(TitleClip title, int width, int height, TitleFrame state, string path)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        DrawTitle(surface.Canvas, title, width, height, SizeScale(height), state);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        using (var file = File.Create(temp))
            data.SaveTo(file);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// A scrolling title's text drawn once, with a frame's worth of empty space before and after it
    /// (below and above for credits and crawls, right and left for a ticker): ffmpeg moves a
    /// frame-sized window along it, so long credits cost no more per frame than a still title.
    /// The background (colour or picture) isn't in it: see <see cref="RenderScrollBackground"/>.
    /// </summary>
    public ScrollStrip? RenderScrollStrip(TitleClip title, int width, int height, int projectWidth, int projectHeight)
    {
        var key = CacheKey(title, width, height, projectWidth, projectHeight) + "-strip";
        var path = Path.Combine(_cacheDirectory, key + ".png");
        var meta = path + ".txt";
        if (File.Exists(path) && File.Exists(meta))
        {
            var parts = File.ReadAllText(meta).Split(' ');
            if (parts.Length == 3 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var w0) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var h0)
                && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var p0))
                return new ScrollStrip(path, w0, h0, p0);
        }

        var scale = SizeScale(height);
        var typeface = FontResolver.Resolve(title.FontFamily, title.Bold, title.Italic);
        var fontSize = (float)Math.Max(4, title.FontSize * scale);
        using var font = new SKFont(typeface, fontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var metrics = font.Metrics;
        var lineHeight = (-metrics.Ascent + metrics.Descent) * 1.15f;
        var outline = (float)(title.OutlineWidth * scale);
        var text = (title.Text ?? "").Replace("\r\n", "\n");

        SKSurface surface;
        int stripW, stripH, period = 0;
        if (title.Scroll == TitleScroll.Ticker)
        {
            // One line, items separated by bullets, starting just off the right edge.
            const string bullet = "   •   ";
            var line = string.Join(bullet, text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            var textW = MeasureWithFallback(line, font, typeface);
            if (textW <= 0)
                return null;
            var copies = 1;
            if (title.ScrollLoop)
            {
                // Looping: the line and a bullet, repeated whole-pixel steps apart, enough times that a
                // frame-wide window starting anywhere in the first copy is always full.
                line += bullet;
                period = (int)Math.Ceiling(MeasureWithFallback(line, font, typeface));
                copies = (int)Math.Ceiling((double)width / period) + 2;
                stripW = Even(width + period * copies);
            }
            else
            {
                stripW = Even((int)Math.Ceiling(width * 2 + textW + fontSize));
            }
            stripH = height;
            surface = SKSurface.Create(new SKImageInfo(stripW, stripH, SKColorType.Rgba8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            var cy = (float)(title.Y * height);
            if (TryParseColor(title.BoxColor, out var band))
            {
                // A band right across: the same all along, so it looks still while the text moves.
                using var paint = new SKPaint { Color = band.WithAlpha((byte)(Math.Clamp(title.BoxOpacity, 0, 1) * 255)) };
                surface.Canvas.DrawRect(new SKRect(0, cy - lineHeight * 0.65f, stripW, cy + lineHeight * 0.65f), paint);
            }
            for (var copy = 0; copy < copies; copy++)
                DrawStyledText(surface.Canvas, title, line, width + copy * period, cy - (metrics.Ascent + metrics.Descent) / 2, font, typeface, outline);
        }
        else
        {
            var crawl = title.Scroll == TitleScroll.Crawl;
            var maxW = width * (crawl ? 0.7f : 0.86f);
            // Lines, whether each ends a paragraph (a crawl justifies all lines but those), and whether
            // it's a paragraph on its own (a crawl centres those, like "Episode IV" and its title).
            var lines = new List<(string Text, bool Last, bool Alone)>();
            foreach (var paragraph in text.Split('\n'))
            {
                var wrapped = !crawl && paragraph.Contains('|') ? [paragraph] : Wrap(paragraph, font, maxW);
                for (var i = 0; i < wrapped.Count; i++)
                    lines.Add((wrapped[i], i == wrapped.Count - 1, wrapped.Count == 1));
            }
            var picture = title.ImagePlacement != TitleImagePlacement.Background ? TitleImages.Get(title.ImagePath) : null;
            float imgW = 0, imgH = 0, gap = 0;
            if (picture != null)
            {
                imgH = (float)Math.Max(2, title.ImageSize * scale);
                imgW = imgH * picture.Width / picture.Height;
                gap = lineHeight * 0.6f;
            }
            var contentH = lines.Count * lineHeight + imgH + gap;
            if (contentH <= 0)
                return null;
            stripW = width;
            stripH = Even((int)Math.Ceiling(height * 2 + contentH));
            surface = SKSurface.Create(new SKImageInfo(stripW, stripH, SKColorType.Rgba8888, SKAlphaType.Premul))
                      ?? throw new InvalidOperationException("The scrolling title is too long to draw.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            var y = (float)height;
            var below = title.ImagePlacement == TitleImagePlacement.Below;
            void DrawPicture()
            {
                using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
                canvas.DrawImage(picture, SKRect.Create((width - imgW) / 2, y, imgW, imgH), paint);
                y += imgH + gap;
            }
            if (picture != null && !below)
                DrawPicture();
            var blockLeft = (width - maxW) / 2;
            var columnGap = fontSize * 0.8f;
            foreach (var (line, last, alone) in lines)
            {
                var baseline = y + (-metrics.Ascent) + (lineHeight - (-metrics.Ascent + metrics.Descent)) / 2;
                if (!crawl && line.Contains('|'))
                {
                    // Credits columns: role on the left (right-aligned, a little dimmer), name on the right.
                    var parts = line.Split('|', 2);
                    var role = parts[0].Trim();
                    var name = parts[1].Trim();
                    DrawStyledText(canvas, title, role, width / 2f - columnGap / 2 - MeasureWithFallback(role, font, typeface), baseline, font, typeface, outline, 0.75f);
                    DrawStyledText(canvas, title, name, width / 2f + columnGap / 2, baseline, font, typeface, outline);
                }
                else if (crawl && !last && line.Contains(' '))
                {
                    // Justified: the words spread to fill the column.
                    var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var widths = words.Select(w => MeasureWithFallback(w, font, typeface)).ToList();
                    var space = words.Length > 1 ? (maxW - widths.Sum()) / (words.Length - 1) : 0;
                    var x = blockLeft;
                    for (var i = 0; i < words.Length; i++)
                    {
                        DrawStyledText(canvas, title, words[i], x, baseline, font, typeface, outline);
                        x += widths[i] + space;
                    }
                }
                else
                {
                    var lineW = MeasureWithFallback(line, font, typeface);
                    var x = crawl && !alone ? blockLeft : crawl ? (width - lineW) / 2 : title.Alignment switch
                    {
                        TextAlignment.Left => blockLeft,
                        TextAlignment.Right => blockLeft + maxW - lineW,
                        _ => (width - lineW) / 2,
                    };
                    DrawStyledText(canvas, title, line, x, baseline, font, typeface, outline);
                }
                y += lineHeight;
            }
            if (picture != null && below)
            {
                y += gap - lineHeight * 0.2f;
                DrawPicture();
            }
        }

        using (surface)
        {
            Directory.CreateDirectory(_cacheDirectory);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            using (var file = File.Create(temp))
                data.SaveTo(file);
            File.Move(temp, path, overwrite: true);
        }
        File.WriteAllText(meta, string.Create(CultureInfo.InvariantCulture, $"{stripW} {stripH} {period}"));
        return new ScrollStrip(path, stripW, stripH, period);
    }

    /// <summary>
    /// A ticker's picture (a logo, say), still at the left end of the band (the right with "Right of
    /// the text"), drawn over the moving text on a block of the band's colour so the text passes
    /// under it. Null if there's no picture (or it fills the background).
    /// </summary>
    public RenderedGraphic? RenderTickerCap(TitleClip title, int width, int height, int projectWidth, int projectHeight)
    {
        if (title.Scroll != TitleScroll.Ticker || title.ImagePlacement == TitleImagePlacement.Background || TitleImages.Get(title.ImagePath) is not { } picture)
            return null;
        var key = CacheKey(title, width, height, projectWidth, projectHeight) + "-cap";
        var path = Path.Combine(_cacheDirectory, key + ".png");
        var meta = path + ".txt";
        if (File.Exists(path) && File.Exists(meta) && TryReadMeta(meta, path, out var cached))
            return cached;

        var scale = SizeScale(height);
        var typeface = FontResolver.Resolve(title.FontFamily, title.Bold, title.Italic);
        using var font = new SKFont(typeface, (float)Math.Max(4, title.FontSize * scale));
        var lineHeight = (-font.Metrics.Ascent + font.Metrics.Descent) * 1.15f;
        var cy = (float)(title.Y * height);
        var imgH = (float)Math.Max(2, title.ImageSize * scale);
        var imgW = imgH * picture.Width / picture.Height;
        var pad = lineHeight * 0.3f;
        var right = title.ImagePlacement == TitleImagePlacement.Right;
        var block = right
            ? new SKRect(width - imgW - pad * 2, cy - lineHeight * 0.65f, width, cy + lineHeight * 0.65f)
            : new SKRect(0, cy - lineHeight * 0.65f, imgW + pad * 2, cy + lineHeight * 0.65f);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (TryParseColor(title.BoxColor, out var band))
        {
            using var paint = new SKPaint { Color = band }; // opaque, so the text is hidden under it
            canvas.DrawRect(block, paint);
        }
        var imageRect = SKRect.Create(block.MidX - imgW / 2, cy - imgH / 2, imgW, imgH);
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            canvas.DrawImage(picture, imageRect, paint);
        var bounds = Round(SKRect.Union(block, imageRect));
        bounds.Intersect(new SKRectI(0, 0, width, height));
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

    /// <summary>A scrolling title's still background (colour, or a filling picture), or null if it has none.</summary>
    public RenderedGraphic? RenderScrollBackground(TitleClip title, int width, int height, int projectWidth, int projectHeight)
    {
        var background = (TitleClip)title.CloneWithNewId();
        background.Text = "";
        background.Scroll = TitleScroll.None;
        background.AnimationIn = background.AnimationOut = TitleMotion.None;
        background.BoxColor = "";
        if (background.ImagePlacement != TitleImagePlacement.Background)
            background.ImagePath = "";
        if (string.IsNullOrEmpty(background.BackgroundColor) && string.IsNullOrEmpty(background.ImagePath))
            return null;
        return Render(background, width, height, projectWidth, projectHeight);
    }

    /// <summary>
    /// A crawl's fade into the distance: a grey mask, black (hidden) at the top of the frame and
    /// white (shown) from about halfway down.
    /// </summary>
    public string RenderCrawlMask(int width, int height)
    {
        var path = Path.Combine(_cacheDirectory, $"crawl-mask-{width}x{height}.png");
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(_cacheDirectory);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, height * 0.14f), new SKPoint(0, height * 0.5f),
                [SKColors.Black, SKColors.White], SKShaderTileMode.Clamp);
            canvas.DrawRect(new SKRect(0, 0, width, height), paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        using (var file = File.Create(temp))
            data.SaveTo(file);
        File.Move(temp, path, overwrite: true);
        return path;
    }

    private static int Even(int value) => Math.Max(2, (value + 1) & ~1);

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

    internal static SKRectI DrawTitle(SKCanvas canvas, TitleClip title, int width, int height, double scale, TitleFrame? frame = null)
    {
        var anim = frame ?? TitleFrame.Still;
        var dirty = SKRect.Empty;
        if (TryParseColor(title.BackgroundColor, out var background))
        {
            canvas.Clear(background);
            dirty = new SKRect(0, 0, width, height);
        }
        var image = TitleImages.Get(title.ImagePath);
        if (image != null && title.ImagePlacement == TitleImagePlacement.Background)
        {
            // Fill the frame, cropping the picture's edges to keep its shape.
            var fill = Math.Max(width / (float)image.Width, height / (float)image.Height);
            var w = image.Width * fill;
            var h = image.Height * fill;
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawImage(image, new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), paint);
            dirty = new SKRect(0, 0, width, height);
        }
        var picture = image != null && title.ImagePlacement != TitleImagePlacement.Background ? image : null;
        var text = (title.Text ?? "").Replace("\r\n", "\n");
        var hasText = text.Trim().Length > 0;
        if (!hasText && picture == null)
            return Round(dirty);

        var typeface = FontResolver.Resolve(title.FontFamily, title.Bold, title.Italic);
        var fontSize = (float)Math.Max(4, title.FontSize * scale);
        using var font = new SKFont(typeface, fontSize) { Subpixel = true, Edging = SKFontEdging.Antialias };
        var lines = hasText ? Wrap(text, font, width * 0.92f) : [];
        var metrics = font.Metrics;
        var lineHeight = (-metrics.Ascent + metrics.Descent) * 1.15f;
        var lineWidths = lines.Select(l => MeasureWithFallback(l, font, typeface)).ToList();
        var blockWidth = lineWidths.DefaultIfEmpty(0).Max();
        var blockHeight = lineHeight * lines.Count;

        // The text and its picture are laid out as one group, placed by X/Y and the alignment.
        float imgW = 0, imgH = 0;
        if (picture != null)
        {
            imgH = (float)Math.Max(2, title.ImageSize * scale);
            imgW = imgH * picture.Width / picture.Height;
        }
        var gap = hasText && picture != null ? fontSize * 0.35f : 0;
        var beside = picture != null && title.ImagePlacement is TitleImagePlacement.Left or TitleImagePlacement.Right;
        var groupW = beside ? imgW + gap + blockWidth : Math.Max(imgW, blockWidth);
        var groupH = beside ? Math.Max(imgH, blockHeight) : imgH + gap + blockHeight;
        var anchorX = (float)(title.X * width);
        var cy = (float)(title.Y * height);
        var groupLeft = title.Alignment switch
        {
            TextAlignment.Left => anchorX,
            TextAlignment.Right => anchorX - groupW,
            _ => anchorX - groupW / 2,
        };
        var groupTop = cy - groupH / 2;
        float Aligned(float w) => title.Alignment switch
        {
            TextAlignment.Left => groupLeft,
            TextAlignment.Right => groupLeft + groupW - w,
            _ => groupLeft + (groupW - w) / 2,
        };
        float left, top;
        SKRect imageRect;
        switch (picture == null ? TitleImagePlacement.Above : title.ImagePlacement)
        {
            case TitleImagePlacement.Left:
                imageRect = SKRect.Create(groupLeft, groupTop + (groupH - imgH) / 2, imgW, imgH);
                left = groupLeft + imgW + gap;
                top = groupTop + (groupH - blockHeight) / 2;
                break;
            case TitleImagePlacement.Right:
                left = groupLeft;
                top = groupTop + (groupH - blockHeight) / 2;
                imageRect = SKRect.Create(groupLeft + blockWidth + gap, groupTop + (groupH - imgH) / 2, imgW, imgH);
                break;
            case TitleImagePlacement.Below:
                left = Aligned(blockWidth);
                top = groupTop;
                imageRect = SKRect.Create(Aligned(imgW), groupTop + blockHeight + gap, imgW, imgH);
                break;
            default:
                imageRect = SKRect.Create(Aligned(imgW), groupTop, imgW, imgH);
                left = Aligned(blockWidth);
                top = groupTop + imgH + gap;
                break;
        }
        var cx = left + blockWidth / 2;
        var outline = (float)(title.OutlineWidth * scale);
        var margin = outline + fontSize * 0.2f;
        var pad = fontSize * 0.35f;
        var group = new SKRect(groupLeft, groupTop, groupLeft + groupW, groupTop + groupH);

        // Animation: the whole group moves, grows or is revealed; or its letters, words or lines appear in turn.
        var shown = (float)Math.Clamp(anim.Shown, 0, 1);
        var away = (float)Math.Pow(1 - shown, 3); // eases into place, and out of it
        var piecewise = anim.Motion is TitleMotion.Typewriter or TitleMotion.Words or TitleMotion.Lines && shown < 1;
        var opacity = 1f;
        var saves = canvas.Save();
        var reach = margin + pad;
        switch (anim.Motion)
        {
            case TitleMotion.SlideLeft:
                canvas.Translate(-(group.Right + reach) * away, 0);
                break;
            case TitleMotion.SlideRight:
                canvas.Translate((width - group.Left + reach) * away, 0);
                break;
            case TitleMotion.SlideTop:
                canvas.Translate(0, -(group.Bottom + reach) * away);
                break;
            case TitleMotion.SlideBottom:
                canvas.Translate(0, (height - group.Top + reach) * away);
                break;
            case TitleMotion.Rise:
                canvas.Translate(0, height * 0.06f * away);
                opacity = shown;
                break;
            case TitleMotion.Grow:
                var s = 0.5f + 0.5f * (1 - away);
                canvas.Scale(s, s, group.MidX, group.MidY);
                opacity = shown;
                break;
            case TitleMotion.Wipe:
                var from = group.Left - reach;
                canvas.ClipRect(new SKRect(from, group.Top - reach - height, from + (group.Width + 2 * reach) * (1 - away), group.Bottom + reach + height));
                break;
        }
        if (opacity < 0.999f)
        {
            using var layer = new SKPaint { Color = SKColors.White.WithAlpha((byte)(opacity * 255)) };
            canvas.SaveLayer(layer);
        }
        // With piecewise motions the box and picture fade in quickly, ahead of the text.
        var lead = piecewise ? Math.Min(1, shown * 3) : 1;

        if (TryParseColor(title.BoxColor, out var boxColor))
        {
            var box = new SKRect(group.Left - pad, group.Top - pad * 0.6f, group.Right + pad, group.Bottom + pad * 0.6f);
            using var paint = new SKPaint { Color = boxColor.WithAlpha((byte)(Math.Clamp(title.BoxOpacity, 0, 1) * 255 * lead)), IsAntialias = true };
            canvas.DrawRoundRect(box, pad * 0.5f, pad * 0.5f, paint);
            dirty = Union(dirty, box);
        }
        if (picture != null)
        {
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * lead)) };
            canvas.DrawImage(picture, imageRect, paint);
            dirty = Union(dirty, imageRect);
        }

        void DrawPiece(string piece, float x, float baseline, float alpha, float dy) =>
            DrawStyledText(canvas, title, piece, x, baseline + dy, font, typeface, outline, alpha);

        // Staggered pieces: piece i of n starts a share of the way through and takes the rest to arrive.
        float Arrival(int i, int n)
        {
            if (!piecewise)
                return 1;
            var start = n > 1 ? 0.6f * i / (n - 1) : 0;
            var length = n > 1 ? 0.4f : 1;
            return Math.Clamp((shown - start) / length, 0, 1);
        }
        var totalWords = lines.Sum(l => l.Split(' ').Length);
        var totalLetters = lines.Sum(l => new StringInfo(l).LengthInTextElements);
        var lettersLeft = anim.Motion == TitleMotion.Typewriter && piecewise ? (int)Math.Floor(totalLetters * shown + 1e-6) : int.MaxValue;
        var wordIndex = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var x = title.Alignment switch
            {
                TextAlignment.Left => left,
                TextAlignment.Right => left + blockWidth - lineWidths[i],
                _ => cx - lineWidths[i] / 2,
            };
            var baseline = top + i * lineHeight + (-metrics.Ascent) + (lineHeight - (-metrics.Ascent + metrics.Descent)) / 2;
            var line = lines[i];
            switch (piecewise ? anim.Motion : TitleMotion.None)
            {
                case TitleMotion.Typewriter:
                {
                    var info = new StringInfo(line);
                    var count = Math.Min(lettersLeft, info.LengthInTextElements);
                    lettersLeft -= count;
                    DrawPiece(info.SubstringByTextElements(0, count), x, baseline, 1, 0);
                    break;
                }
                case TitleMotion.Words:
                {
                    var at = 0;
                    foreach (var word in line.Split(' '))
                    {
                        var p = Arrival(wordIndex++, totalWords);
                        var e = 1 - (float)Math.Pow(1 - p, 3);
                        DrawPiece(word, x + MeasureWithFallback(line[..at], font, typeface), baseline, p, fontSize * 0.4f * (1 - e));
                        at += word.Length + 1;
                    }
                    break;
                }
                case TitleMotion.Lines:
                {
                    var p = Arrival(i, lines.Count);
                    var e = 1 - (float)Math.Pow(1 - p, 3);
                    DrawPiece(line, x, baseline, p, lineHeight * 0.6f * (1 - e));
                    break;
                }
                default:
                    DrawPiece(line, x, baseline, 1, 0);
                    break;
            }
        }
        canvas.RestoreToCount(saves);
        if (hasText)
            dirty = Union(dirty, new SKRect(left - margin, top - margin, left + blockWidth + margin, top + blockHeight + margin));
        return Round(dirty);
    }

    /// <summary>Text in a title's style (shadow, outline and fill), at an opacity.</summary>
    private static void DrawStyledText(SKCanvas canvas, TitleClip title, string text, float x, float baseline, SKFont font, SKTypeface typeface, float outline, float alpha = 1)
    {
        if (alpha <= 0.001f || text.Length == 0)
            return;
        var a = (byte)(255 * Math.Clamp(alpha, 0, 1));
        var fontSize = font.Size;
        TryParseColor(title.Color, out var color);
        TryParseColor(title.OutlineColor, out var outlineColor);
        if (title.Shadow)
        {
            using var shadow = new SKPaint
            {
                Color = new SKColor(0, 0, 0, (byte)(160 * a / 255)),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(1, fontSize * 0.06f)),
            };
            DrawTextWithFallback(canvas, text, x + fontSize * 0.04f, baseline + fontSize * 0.05f, font, typeface, shadow);
        }
        if (outline > 0)
        {
            using var stroke = new SKPaint
            {
                Color = outlineColor.WithAlpha((byte)(outlineColor.Alpha * a / 255)),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = outline * 2,
                StrokeJoin = SKStrokeJoin.Round,
            };
            DrawTextWithFallback(canvas, text, x, baseline, font, typeface, stroke);
        }
        using var fill = new SKPaint { Color = color.WithAlpha((byte)(color.Alpha * a / 255)), IsAntialias = true };
        DrawTextWithFallback(canvas, text, x, baseline, font, typeface, fill);
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
        var picture = clip is TitleClip { ImagePath.Length: > 0 } t ? TitleImages.Stamp(t.ImagePath) : "";
        var text = string.Create(CultureInfo.InvariantCulture, $"v3|{width}x{height}|{projectWidth}x{projectHeight}|{FontResolver.DefaultFamily}|{picture}|{json}");
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
