using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Capture;

public sealed record CaptureSettings
{
    public required CaptureTarget Target { get; init; }
    public double FrameRate { get; init; } = 30;
    public bool ShowCursor { get; init; } = true;
    /// <summary>Audio device ids (see <see cref="AudioDevices"/>) to record and mix; empty = no sound.</summary>
    public IReadOnlyList<string> AudioDevices { get; init; } = [];
    /// <summary>Stop by itself after this long (null = until stopped).</summary>
    public TimeSpan? MaxDuration { get; init; }
}

/// <summary>
/// Records the screen, a window or a region with ffmpeg (x11grab on Linux, gdigrab on
/// Windows), optionally with sound. Recording goes to a Matroska file, which stays playable
/// even if the app or computer crashes mid-recording; when stopped it's remuxed (no
/// re-encoding) to MP4 for easy sharing.
/// </summary>
public sealed class ScreenRecorder
{
    private readonly FfmpegLocator _locator;

    public ScreenRecorder(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    /// <summary>
    /// Records until <paramref name="stop"/> is cancelled (or the max duration), then finishes
    /// the file and returns its path (<paramref name="outputPath"/> with an .mp4 extension).
    /// Progress reports the time recorded so far.
    /// </summary>
    public async Task<string> RecordAsync(CaptureSettings settings, string outputPath, IProgress<TimeSpan>? progress = null,
        CancellationToken stop = default)
    {
        var mp4 = Path.ChangeExtension(Path.GetFullPath(outputPath), ".mp4");
        var mkv = Path.ChangeExtension(mp4, ".recording.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(mp4)!);
        var args = BuildArguments(settings, mkv, _locator.Capabilities);
        Log.Info("capture", $"Recording {settings.Target} at {settings.FrameRate} fps to {mkv}.");

        var runner = new FfmpegRunner(_locator);
        var progressAdapter = progress == null ? null : new Progress<FfmpegProgress>(p => progress.Report(p.Position));
        await runner.RunAsync(args, null, progressAdapter, stop, stopGracefully: true).ConfigureAwait(false);
        if (!File.Exists(mkv) || new FileInfo(mkv).Length == 0)
            throw new InvalidOperationException("Nothing was recorded.");

        // Remux to MP4 (seconds, no quality loss). If that fails the .mkv is kept and returned.
        try
        {
            await runner.RunAsync(["-i", mkv, "-c", "copy", "-movflags", "+faststart", "-y", mp4]).ConfigureAwait(false);
            File.Delete(mkv);
            return mp4;
        }
        catch (FfmpegException ex)
        {
            Log.Warn("capture", "Remux to MP4 failed; keeping the MKV recording.", ex);
            return mkv;
        }
    }

    /// <summary>Saves a single PNG of the target (a screenshot for bug reports).</summary>
    public async Task ScreenshotAsync(CaptureTarget target, string outputPath, bool showCursor = false, CancellationToken cancellationToken = default)
    {
        var settings = new CaptureSettings { Target = target, ShowCursor = showCursor, FrameRate = 10 };
        var args = InputArguments(settings);
        args.AddRange(["-frames:v", "1", "-update", "1", "-y", Path.GetFullPath(outputPath)]);
        await new FfmpegRunner(_locator).RunAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static List<string> BuildArguments(CaptureSettings s, string output, FfmpegCapabilities caps)
    {
        var args = InputArguments(s);
        var audioInputs = 0;
        foreach (var device in s.AudioDevices)
        {
            if (OperatingSystem.IsWindows())
                args.AddRange(["-thread_queue_size", "1024", "-f", "dshow", "-i", $"audio={device}"]);
            else
                args.AddRange(["-thread_queue_size", "1024", "-f", "pulse", "-i", device]);
            audioInputs++;
        }

        // Even dimensions for 4:2:0 H.264.
        args.AddRange(["-map", "0:v", "-vf", "crop=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p"]);
        if (audioInputs == 1)
        {
            args.AddRange(["-map", "1:a"]);
        }
        else if (audioInputs > 1)
        {
            var inputs = string.Concat(Enumerable.Range(1, audioInputs).Select(i => $"[{i}:a]"));
            args.AddRange(["-filter_complex", $"{inputs}amix=inputs={audioInputs}:duration=longest:normalize=0[rec_audio]", "-map", "[rec_audio]"]);
        }

        // Fast enough to keep up in real time on modest machines; high quality for text on screen.
        if (caps.Encoders.Count == 0 || caps.HasEncoder("libx264"))
            args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-tune", "stillimage", "-g", F(s.FrameRate * 2)]);
        else
            args.AddRange(["-c:v", "mpeg4", "-q:v", "2"]);
        if (audioInputs > 0)
            args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        if (s.MaxDuration is { } max)
            args.AddRange(["-t", F(max.TotalSeconds)]);
        args.AddRange(["-y", output]);
        return args;
    }

    internal static List<string> InputArguments(CaptureSettings s)
    {
        var t = s.Target;
        var args = new List<string> { "-thread_queue_size", "512" };
        var fps = F(s.FrameRate);
        var cursor = s.ShowCursor ? "1" : "0";
        if (OperatingSystem.IsWindows())
        {
            args.AddRange(["-f", "gdigrab", "-framerate", fps, "-draw_mouse", cursor]);
            if (t.Kind == CaptureTargetKind.Window && !string.IsNullOrEmpty(t.Title))
            {
                args.AddRange(["-i", $"title={t.Title}"]);
            }
            else
            {
                args.AddRange(["-offset_x", t.X.ToString(), "-offset_y", t.Y.ToString(), "-video_size", $"{t.Width}x{t.Height}", "-i", "desktop"]);
            }
        }
        else
        {
            var display = Environment.GetEnvironmentVariable("DISPLAY");
            if (string.IsNullOrEmpty(display))
                display = ":0";
            args.AddRange(["-f", "x11grab", "-framerate", fps, "-draw_mouse", cursor]);
            if (t.Kind == CaptureTargetKind.Window && t.WindowHandle != 0)
            {
                args.AddRange(["-window_id", t.WindowHandle.ToString(), "-i", display]);
            }
            else
            {
                args.AddRange(["-video_size", $"{t.Width & ~1}x{t.Height & ~1}", "-i", $"{display}+{t.X},{t.Y}"]);
            }
        }
        return args;
    }
}
