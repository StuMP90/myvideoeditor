using MyVideoEditor.Core.Capture;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Tests;

/// <summary>
/// Records from the real display: on Linux this needs an X server (a desktop session, or
/// xvfb-run in CI); the tests do nothing without one.
/// </summary>
public class CaptureTests
{
    private static bool CanCapture =>
        OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
                                  && FfmpegLocator.Default.Capabilities.HasInputDevice("x11grab");

    [Fact]
    public async Task Records_a_region_to_mp4()
    {
        if (!CanCapture)
            return;
        var target = new CaptureTarget(CaptureTargetKind.Region, "", 0, 0, 321, 241);
        var output = Path.Combine(TestMedia.NewOutputDirectory(), "capture.mp4");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var progress = new List<TimeSpan>();
        var path = await new ScreenRecorder().RecordAsync(new CaptureSettings { Target = target, FrameRate = 15 }, output,
            new Progress<TimeSpan>(progress.Add), stop.Token);
        Assert.Equal(output, path);
        Assert.False(File.Exists(Path.ChangeExtension(output, ".recording.mkv")));
        var info = await MediaProbe.Default.ProbeAsync(path);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.InRange(info.Duration, 0.5, 3);
    }

    [Fact]
    public async Task Max_duration_stops_the_recording()
    {
        if (!CanCapture)
            return;
        var target = new CaptureTarget(CaptureTargetKind.Region, "", 0, 0, 200, 200);
        var output = Path.Combine(TestMedia.NewOutputDirectory(), "short.mp4");
        var path = await new ScreenRecorder().RecordAsync(new CaptureSettings { Target = target, FrameRate = 10, MaxDuration = TimeSpan.FromSeconds(1) }, output);
        Assert.InRange((await MediaProbe.Default.ProbeAsync(path)).Duration, 0.7, 1.3);
    }

    [Fact]
    public async Task Takes_a_screenshot()
    {
        if (!CanCapture)
            return;
        var output = Path.Combine(TestMedia.NewOutputDirectory(), "shot.png");
        await new ScreenRecorder().ScreenshotAsync(new CaptureTarget(CaptureTargetKind.Region, "", 10, 10, 100, 80), output);
        var info = await MediaProbe.Default.ProbeAsync(output);
        Assert.Equal(MediaKind.Image, info.Kind);
        Assert.Equal((100, 80), (info.Width, info.Height));
    }

    [Fact]
    public void Lists_windows_without_crashing()
    {
        var windows = WindowEnumerator.ListWindows();
        Assert.All(windows, w => Assert.True(w.Width > 0 && w.Height > 0 && w.Title.Length > 0));
    }

    [Fact]
    public void Window_capture_uses_the_window_id_on_linux()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var args = ScreenRecorder.InputArguments(new CaptureSettings { Target = new CaptureTarget(CaptureTargetKind.Window, "App", 5, 5, 400, 300, 0x3a00007) });
        Assert.Contains("-window_id", args);
        Assert.Contains(0x3a00007.ToString(), args);
    }
}
