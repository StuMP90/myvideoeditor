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
    /// <summary>
    /// A colour clip whose sound runs <paramref name="soundLonger"/> seconds past its picture, as in
    /// many downloaded videos (the file's duration is the sound's).
    /// </summary>
    public static Task<string> ColorWithLongerSoundAsync(string color, double seconds, double soundLonger) =>
        CachedAsync($"color-{color}-{seconds}-longer-sound-{soundLonger}.mp4", path =>
        [
            "-f", "lavfi", "-i", $"color=c={color}:s=320x240:r=30:d={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=48000:duration={(seconds + soundLonger).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            .. VideoCodec(), "-c:a", "aac", "-b:a", "128k", "-y", path,
        ]);

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

    /// <summary>A moving pattern at NTSC 29.97 fps (30000/1001), with a tone: catches frame-rate rounding drift.</summary>
    public static Task<string> NtscPatternAsync(double seconds, int width, int height) =>
        CachedAsync($"ntsc-{seconds}-{width}x{height}.mp4", path =>
        [
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30000/1001:duration={seconds}",
            "-f", "lavfi", "-i", $"sine=frequency=330:sample_rate=48000:duration={seconds}",
            .. VideoCodec(), "-c:a", "aac", "-shortest", "-y", path,
        ]);

    /// <summary>Number of video frames in a file (decoded and counted).</summary>
    public static int FrameCount(string file)
    {
        var psi = new ProcessStartInfo(Locator.FfprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", file })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return int.Parse(text.Trim().TrimEnd(','));
    }

    /// <summary>
    /// SSIM of two videos compared frame by frame in order (timestamps ignored: files written
    /// by different tools rarely share start times, and ffmpeg pairs frames by timestamp).
    /// </summary>
    public static double Ssim(string a, string b)
    {
        var psi = new ProcessStartInfo(Locator.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-hide_banner", "-i", a, "-i", b, "-lavfi",
                     "[0:v]settb=AVTB,setpts=N/30/TB,format=yuv420p[x];[1:v]settb=AVTB,setpts=N/30/TB,format=yuv420p[y];[x][y]scale2ref=flags=bicubic[x2][y2];[x2][y2]ssim", "-f", "null", "-" })
            psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var line = err.Split('\n').Last(l => l.Contains("All:"));
        var value = line[(line.IndexOf("All:", StringComparison.Ordinal) + 4)..].Split(' ')[0];
        return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

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
