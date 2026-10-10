using System.Diagnostics;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Tests;
using SkiaSharp;

namespace MyVideoEditor.App.Tests;

/// <summary>
/// Footage for the Store screenshots, drawn here frame by frame (so there's nothing third-party in
/// them): scenery (hills at dusk, sailing, a city at night, a forest morning) and a made-up app
/// window for the bug-report shots. Each clip has a soft pink-noise sound so waveforms show.
/// </summary>
internal static class DemoFootage
{
    public const int Width = 1280;
    public const int Height = 720;
    private const int Fps = 30;

    public static string Hills(string dir) => Make(dir, "Hills at dusk.mp4", 6, DrawHills);
    public static string Sailing(string dir) => Make(dir, "Sailing.mp4", 6, DrawSea);
    public static string City(string dir) => Make(dir, "City lights.mp4", 6, DrawCity);
    public static string Forest(string dir) => Make(dir, "Forest morning.mp4", 6, DrawForest);
    public static string BugReport(string dir) => Make(dir, "Recording - save fails.mp4", 6, DrawApp);

    /// <summary>A smaller copy (for the Convert tab's upscaling shots).</summary>
    public static string Smaller(string source, int height, string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(source)!, name);
        if (!File.Exists(path))
            Run(TestMedia.Locator.FfmpegPath, ["-v", "error", "-i", source, "-vf", $"scale=-2:{height}:flags=area", "-c:v", "libx264", "-crf", "20", "-c:a", "copy", "-y", path]);
        return path;
    }

    private static string Make(string dir, string name, double seconds, Action<SKCanvas, double> draw)
    {
        var path = Path.Combine(dir, name);
        if (File.Exists(path))
            return path;
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(TestMedia.Locator.FfmpegPath) { RedirectStandardError = true };
        foreach (var a in new[]
                 {
                     "-v", "error", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{Width}x{Height}", "-r", Fps.ToString(), "-i", "pipe:0",
                     "-f", "lavfi", "-i", "anoisesrc=c=pink:a=0.06,lowpass=f=700,volume=0.8",
                     "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", path,
                 })
            psi.ArgumentList.Add(a);
        using var ffmpeg = ChildProcess.Start(psi, keepInput: true, isFfmpeg: true);
        _ = ffmpeg.StandardError.ReadToEndAsync();
        using var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var input = ffmpeg.StandardInput.BaseStream;
        for (var i = 0; i < seconds * Fps; i++)
        {
            canvas.Clear(SKColors.Black);
            draw(canvas, i / (double)Fps);
            canvas.Flush();
            input.Write(bitmap.GetPixelSpan());
        }
        input.Close();
        ffmpeg.WaitForExit();
        if (ffmpeg.ExitCode != 0)
            throw new InvalidOperationException("Making " + name + " failed.");
        return path;
    }

    private static void Run(string program, string[] args)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = ChildProcess.Start(psi, isFfmpeg: true);
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    // ---- scenes ---------------------------------------------------------------------------------

    private static void Sky(SKCanvas c, float bottom, params SKColor[] colors)
    {
        using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, bottom), colors, SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, Width, Height, paint);
    }

    private static void Glow(SKCanvas c, float x, float y, float r, SKColor core, SKColor halo)
    {
        using var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(x, y), r * 4, [halo, halo.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawCircle(x, y, r * 4, glow);
        using var disc = new SKPaint { IsAntialias = true, Color = core };
        c.DrawCircle(x, y, r, disc);
    }

    /// <summary>A ridge line: the sum of a few sines, scrolling with time; filled below.</summary>
    private static void Ridge(SKCanvas c, float baseY, float amplitude, float scale, float scroll, SKColor color, int seed)
    {
        using var path = new SKPath();
        path.MoveTo(0, Height);
        for (var x = 0; x <= Width; x += 8)
        {
            var u = (x + scroll) / scale;
            var y = baseY - amplitude * (MathF.Sin(u + seed) * 0.6f + MathF.Sin(u * 2.3f + seed * 1.7f) * 0.3f + MathF.Sin(u * 5.1f + seed * 0.4f) * 0.1f);
            path.LineTo(x, y);
        }
        path.LineTo(Width, Height);
        path.Close();
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        c.DrawPath(path, paint);
    }

    private static void DrawHills(SKCanvas c, double t)
    {
        var s = (float)t;
        Sky(c, Height * 0.75f, new SKColor(0x23, 0x1E, 0x52), new SKColor(0x8E, 0x3B, 0x6E), new SKColor(0xF2, 0x8A, 0x4B), new SKColor(0xFF, 0xD3, 0x7E));
        Glow(c, Width * 0.66f, Height * 0.5f + s * 6, 56, new SKColor(0xFF, 0xF1, 0xC4), new SKColor(0xFF, 0xB8, 0x6A, 90));
        using (var cloud = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xC9, 0xA8, 70) })
            for (var i = 0; i < 4; i++)
                c.DrawOval(new SKRect(-200 + i * 380 + s * 14, 110 + i * 37, -200 + i * 380 + s * 14 + 260, 110 + i * 37 + 26), cloud);
        Ridge(c, Height * 0.68f, 60, 210, s * 6, new SKColor(0x9C, 0x4E, 0x6A), 1);
        Ridge(c, Height * 0.76f, 55, 170, s * 14, new SKColor(0x5E, 0x2F, 0x5C), 2);
        Ridge(c, Height * 0.86f, 45, 140, s * 28, new SKColor(0x2A, 0x17, 0x38), 3);
    }

    private static void DrawSea(SKCanvas c, double t)
    {
        var s = (float)t;
        var horizon = Height * 0.58f;
        Sky(c, horizon, new SKColor(0x3C, 0x8D, 0xD6), new SKColor(0xBF, 0xE3, 0xFA));
        Glow(c, Width * 0.2f, Height * 0.18f, 44, new SKColor(0xFF, 0xFD, 0xEE), new SKColor(0xFF, 0xF6, 0xC8, 80));
        Ridge(c, horizon + 6, 26, 120, 40, new SKColor(0x6E, 0x93, 0xA8), 5); // a distant island
        using (var sea = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, horizon), new SKPoint(0, Height), [new SKColor(0x2A, 0x7F, 0xB8), new SKColor(0x0B, 0x3B, 0x63)], SKShaderTileMode.Clamp) })
            c.DrawRect(0, horizon, Width, Height - horizon, sea);
        using (var wave = new SKPaint { IsAntialias = true, Color = new SKColor(0xD8, 0xF0, 0xFF, 90), StrokeWidth = 2, Style = SKPaintStyle.Stroke })
            for (var row = 0; row < 14; row++)
            {
                var y = horizon + 12 + row * row * 1.6f;
                var len = 20 + row * 6;
                for (var x = -100 + (row * 37 + s * (20 + row * 4)) % 140; x < Width; x += 140)
                    c.DrawLine(x, y, x + len, y + MathF.Sin(x * 0.02f + s * 2) * 2, wave);
            }
        // A sailing boat moving across.
        var bx = 260 + s * 60;
        var by = horizon + 70 + MathF.Sin(s * 2.4f) * 3;
        using var hull = new SKPaint { IsAntialias = true, Color = new SKColor(0x2B, 0x2D, 0x42) };
        using var sail = new SKPaint { IsAntialias = true, Color = new SKColor(0xFA, 0xFA, 0xF5) };
        using (var p = new SKPath())
        {
            p.MoveTo(bx - 70, by); p.LineTo(bx + 80, by); p.LineTo(bx + 55, by + 22); p.LineTo(bx - 50, by + 22); p.Close();
            c.DrawPath(p, hull);
        }
        using (var p = new SKPath())
        {
            p.MoveTo(bx, by - 150); p.LineTo(bx + 70, by - 6); p.LineTo(bx, by - 6); p.Close();
            c.DrawPath(p, sail);
            p.Reset();
            p.MoveTo(bx - 6, by - 130); p.LineTo(bx - 6, by - 6); p.LineTo(bx - 62, by - 6); p.Close();
            c.DrawPath(p, sail);
        }
    }

    private static void DrawCity(SKCanvas c, double t)
    {
        var s = (float)t;
        Sky(c, Height * 0.8f, new SKColor(0x04, 0x07, 0x1A), new SKColor(0x13, 0x22, 0x55), new SKColor(0x3A, 0x2C, 0x6B));
        var random = new Random(7);
        using (var star = new SKPaint { IsAntialias = true })
            for (var i = 0; i < 140; i++)
            {
                var (x, y) = (random.Next(Width), random.Next((int)(Height * 0.6)));
                star.Color = SKColors.White.WithAlpha((byte)(120 + 100 * Math.Abs(Math.Sin(t * 1.5 + i))));
                c.DrawCircle(x, y, random.Next(1, 3) * 0.7f, star);
            }
        Glow(c, Width * 0.82f, Height * 0.17f, 34, new SKColor(0xF4, 0xF1, 0xE0), new SKColor(0xC9, 0xD8, 0xFF, 50));
        foreach (var (layer, shade, scroll) in new[] { (0, new SKColor(0x1D, 0x24, 0x48), 6f), (1, new SKColor(0x0C, 0x10, 0x26), 16f) })
        {
            var r = new Random(11 + layer);
            using var block = new SKPaint { Color = shade };
            using var window = new SKPaint();
            for (float x = -40 - s * scroll % 80; x < Width + 80;)
            {
                var w = r.Next(50, 120);
                var h = r.Next(layer == 0 ? 220 : 120, layer == 0 ? 460 : 340);
                var top = Height - h;
                c.DrawRect(x, top, w - 6, h, block);
                for (var wy = top + 14; wy < Height - 16; wy += 22)
                    for (var wx = x + 8; wx < x + w - 18; wx += 16)
                    {
                        var lit = r.NextDouble() < 0.42 && Math.Sin(t * 0.7 + wx * 0.13 + wy * 0.07) > -0.8;
                        window.Color = lit ? new SKColor(0xFF, 0xD5, 0x80, (byte)(layer == 0 ? 120 : 220)) : new SKColor(0x30, 0x38, 0x60, 120);
                        c.DrawRect(wx, wy, 7, 10, window);
                    }
                x += w;
            }
        }
    }

    private static void DrawForest(SKCanvas c, double t)
    {
        var s = (float)t;
        Sky(c, Height * 0.7f, new SKColor(0x9F, 0xD3, 0xC7), new SKColor(0xF6, 0xEF, 0xD2));
        Glow(c, Width * 0.5f, Height * 0.2f, 50, new SKColor(0xFF, 0xFB, 0xE6), new SKColor(0xFF, 0xF2, 0xC0, 110));
        using (var ray = new SKPaint { IsAntialias = true, Color = new SKColor(0xFF, 0xF8, 0xDC, 26) })
            for (var i = 0; i < 7; i++)
            {
                using var p = new SKPath();
                var a = -0.9f + i * 0.3f + MathF.Sin(s * 0.4f) * 0.03f;
                p.MoveTo(Width * 0.5f, Height * 0.2f);
                p.LineTo(Width * 0.5f + MathF.Sin(a) * 1400, Height * 0.2f + MathF.Cos(a) * 1400);
                p.LineTo(Width * 0.5f + MathF.Sin(a + 0.08f) * 1400, Height * 0.2f + MathF.Cos(a + 0.08f) * 1400);
                p.Close();
                c.DrawPath(p, ray);
            }
        foreach (var (layer, color, scale, scroll) in new[]
                 {
                     (0, new SKColor(0x7F, 0xB0, 0x95), 0.55f, 8f), (1, new SKColor(0x3E, 0x7D, 0x5E), 0.8f, 18f), (2, new SKColor(0x1B, 0x44, 0x34), 1.15f, 32f),
                 })
        {
            var r = new Random(20 + layer);
            using var tree = new SKPaint { IsAntialias = true, Color = color };
            var ground = Height * (0.62f + layer * 0.12f);
            c.DrawRect(0, ground, Width, Height - ground, tree);
            for (var x = -120 - s * scroll % 90; x < Width + 120; x += 70 * scale + r.Next(0, 40))
            {
                var h = (140 + r.Next(0, 90)) * scale;
                for (var tier = 0; tier < 3; tier++)
                {
                    using var p = new SKPath();
                    var tw = (46 - tier * 10) * scale;
                    var ty = ground - tier * h * 0.28f;
                    p.MoveTo(x - tw, ty); p.LineTo(x, ty - h * 0.5f); p.LineTo(x + tw, ty); p.Close();
                    c.DrawPath(p, tree);
                }
            }
            if (layer == 1)
                using (var mist = new SKPaint { Color = new SKColor(0xF5, 0xF5, 0xEE, 70) })
                    c.DrawRect(0, ground - 40 + MathF.Sin(s) * 6, Width, 60, mist);
        }
    }

    /// <summary>A made-up notes app ("Example Notes") failing to save: something to annotate for a bug report.</summary>
    private static void DrawApp(SKCanvas c, double t)
    {
        c.Clear(new SKColor(0x2B, 0x30, 0x3B));
        using var font = new SKFont(SKTypeface.FromFamilyName("Inter") ?? SKTypeface.Default, 18);
        using var bold = new SKFont(SKTypeface.FromFamilyName("Inter", SKFontStyle.Bold) ?? SKTypeface.Default, 18);
        void Box(float x, float y, float w, float h, SKColor color, float radius = 6)
        {
            using var p = new SKPaint { IsAntialias = true, Color = color };
            c.DrawRoundRect(x, y, w, h, radius, radius, p);
        }
        void Text(string s, float x, float y, SKColor color, SKFont? f = null, float size = 18)
        {
            using var p = new SKPaint { IsAntialias = true, Color = color };
            var used = f ?? font;
            used.Size = size;
            c.DrawText(s, x, y, used, p);
        }
        // Window and title bar.
        Box(60, 40, 1160, 640, new SKColor(0xF7, 0xF8, 0xFA), 10);
        Box(60, 40, 1160, 44, new SKColor(0xE6, 0xE9, 0xEF), 10);
        Text("Example Notes", 90, 69, new SKColor(0x33, 0x3A, 0x48), bold, 17);
        foreach (var (i, col) in new[] { (0, new SKColor(0xFF, 0x5F, 0x57)), (1, new SKColor(0xFE, 0xBC, 0x2E)), (2, new SKColor(0x28, 0xC8, 0x40)) })
            Box(1140 + i * 24, 56, 13, 13, col, 7);
        // Sidebar.
        Box(60, 84, 240, 596, new SKColor(0xEE, 0xF0, 0xF4), 0);
        string[] notes = ["Shopping list", "Meeting notes", "Holiday plans", "Recipe ideas", "Book club"];
        for (var i = 0; i < notes.Length; i++)
        {
            if (i == 1)
                Box(70, 104 + i * 46, 220, 38, new SKColor(0xD5, 0xE3, 0xFF));
            Text(notes[i], 88, 129 + i * 46, new SKColor(0x2A, 0x30, 0x3C));
        }
        // Editor form.
        Text("Meeting notes", 330, 132, new SKColor(0x1F, 0x24, 0x2E), bold, 28);
        Text("Share with", 330, 182, new SKColor(0x6A, 0x72, 0x82), size: 15);
        Box(330, 192, 420, 40, SKColors.White);
        Text("jane.doe@example.com", 344, 218, new SKColor(0x2A, 0x30, 0x3C));
        Box(330, 250, 860, 250, SKColors.White);
        string[] body = ["Agenda for Thursday:", "•  Review the timeline for the spring launch", "•  Agree the budget for the video series", "•  Next steps and owners"];
        for (var i = 0; i < body.Length; i++)
            Text(body[i], 350, 284 + i * 30, new SKColor(0x2A, 0x30, 0x3C));
        Box(1070, 520, 120, 40, new SKColor(0x25, 0x63, 0xEB));
        Text("Save", 1108, 546, SKColors.White, bold);
        // After a couple of seconds: the error.
        if (t > 2)
        {
            Box(430, 300, 520, 170, new SKColor(0x00, 0x00, 0x00, 40), 12);
            Box(424, 292, 520, 170, SKColors.White, 12);
            Box(424, 292, 520, 6, new SKColor(0xE5, 0x48, 0x4D), 3);
            Text("Couldn't save your note", 448, 336, new SKColor(0x1F, 0x24, 0x2E), bold, 21);
            Text("Error 0x1F: the server didn't respond.", 448, 372, new SKColor(0x4A, 0x52, 0x60));
            Text("Your changes are kept on this computer.", 448, 400, new SKColor(0x4A, 0x52, 0x60));
            Box(824, 418, 100, 32, new SKColor(0xE6, 0xE9, 0xEF));
            Text("OK", 862, 440, new SKColor(0x2A, 0x30, 0x3C), bold);
        }
        // The pointer, moving to Save.
        var k = (float)Math.Min(1, t / 1.8);
        var (px, py) = (700 + (1130 - 700) * k, 420 + (540 - 420) * k);
        using var arrow = new SKPath();
        arrow.MoveTo(px, py); arrow.LineTo(px, py + 22); arrow.LineTo(px + 6, py + 17); arrow.LineTo(px + 11, py + 27); arrow.LineTo(px + 14, py + 25); arrow.LineTo(px + 9, py + 16); arrow.LineTo(px + 16, py + 16); arrow.Close();
        using var fill = new SKPaint { IsAntialias = true, Color = SKColors.Black };
        using var edge = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        c.DrawPath(arrow, fill);
        c.DrawPath(arrow, edge);
    }
}
