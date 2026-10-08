using System.Diagnostics;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Tests;

/// <summary>
/// Generates small media files with ffmpeg's built-in sources (so the tests need no fixtures),
/// and reads pixels and loudness back out of rendered files.
/// </summary>
public static class TestMedia
{
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "myvideoeditor-test-media");
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static FfmpegLocator Locator => FfmpegLocator.Default;

    public static string NewOutputDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "myvideoeditor-test-out", Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A solid-colour video with a 440 Hz tone (or silent), e.g. Color("red", 2).</summary>
    public static Task<string> ColorAsync(string color, double seconds, int width = 320, int height = 240, bool audio = true, int toneHz = 440) =>
        CachedAsync($"color-{color}-{seconds}-{width}x{height}-{audio}-{toneHz}.mp4", path =>
        {
            var args = new List<string> { "-f", "lavfi", "-i", $"color=c={color}:s={width}x{height}:r=30:d={seconds}" };
            if (audio)
                args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={toneHz}:sample_rate=48000:duration={seconds}"]);
            args.AddRange(VideoCodec());
            if (audio)
                args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
            args.AddRange(["-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-y", path]);
            return args;
        });

    /// <summary>A moving test pattern with a frame counter and a tone.</summary>
    public static Task<string> PatternAsync(double seconds = 4, int width = 640, int height = 360) =>
        CachedAsync($"pattern-{seconds}-{width}x{height}.mp4", path =>
        [
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=330:sample_rate=48000:duration={seconds}",
            .. VideoCodec(), "-c:a", "aac", "-shortest", "-y", path,
        ]);

    public static Task<string> ToneAsync(double seconds, int hz = 440) =>
        CachedAsync($"tone-{seconds}-{hz}.wav", path =>
            ["-f", "lavfi", "-i", $"sine=frequency={hz}:sample_rate=48000:duration={seconds}", "-ac", "2", "-y", path]);

    public static Task<string> ImageAsync(string color, int width = 400, int height = 300) =>
        CachedAsync($"image-{color}-{width}x{height}.png", path =>
            ["-f", "lavfi", "-i", $"color=c={color}:s={width}x{height}:d=1", "-frames:v", "1", "-y", path]);

    private static string[] VideoCodec() =>
        Locator.Capabilities.HasEncoder("libx264")
            ? ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-g", "15"]
            : ["-c:v", "mpeg4", "-q:v", "3", "-g", "15"];

    private static async Task<string> CachedAsync(string name, Func<string, List<string>> args)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, name);
        await Lock.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                var temp = Path.Combine(Directory, "tmp-" + Guid.NewGuid().ToString("N")[..6] + Path.GetExtension(name));
                await new FfmpegRunner().RunAsync(args(temp));
                File.Move(temp, path, overwrite: true);
            }
            return path;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>The RGB colour at (x, y) of the frame at <paramref name="seconds"/> in a video file.</summary>
    public static (int R, int G, int B) PixelAt(string file, double seconds, int x, int y)
    {
        var (width, _, data) = Frame(file, seconds);
        var i = (y * width + x) * 3;
        return (data[i], data[i + 1], data[i + 2]);
    }

    public static (int Width, int Height, byte[] Rgb) Frame(string file, double seconds)
    {
        var info = MediaProbe.Default.ProbeAsync(file).GetAwaiter().GetResult();
        var psi = new ProcessStartInfo(Locator.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", file, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return (info.Width, info.Height, ms.ToArray());
    }

    /// <summary>Mean volume (dBFS) of a file between two times; about -91 for silence.</summary>
    public static double MeanVolume(string file, double from, double to)
    {
        var psi = new ProcessStartInfo(Locator.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-ss", from.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", (to - from).ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", file, "-af", "volumedetect", "-vn", "-f", "null", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var line = err.Split('\n').FirstOrDefault(l => l.Contains("mean_volume:"));
        if (line == null)
            return -91;
        var value = line[(line.IndexOf("mean_volume:", StringComparison.Ordinal) + 12)..].Replace("dB", "").Trim();
        return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool Near((int R, int G, int B) actual, (int R, int G, int B) expected, int tolerance = 40) =>
        Math.Abs(actual.R - expected.R) <= tolerance && Math.Abs(actual.G - expected.G) <= tolerance && Math.Abs(actual.B - expected.B) <= tolerance;
}
