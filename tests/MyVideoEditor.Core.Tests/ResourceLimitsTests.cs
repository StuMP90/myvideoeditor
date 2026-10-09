using System.Diagnostics;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Upscaling;

namespace MyVideoEditor.Core.Tests;

/// <summary>
/// CPU limits (threads, priority) and the choice of AI device. Runs on its own (not alongside other
/// tests), because the thread-cap test measures this whole process's CPU use.
/// </summary>
[Collection(nameof(RunAlone))]
public class ResourceLimitsTests
{
    [Fact]
    public void Ffmpeg_gets_thread_options_before_inputs_and_output()
    {
        var limits = new ResourceLimits(MaxThreads: 2);
        var args = limits.ApplyToFfmpeg(["-ss", "1", "-i", "a.mp4", "-i", "b.wav", "-c:v", "libx264", "out.mp4"]);
        Assert.Equal(["-filter_threads", "2", "-filter_complex_threads", "2",
            "-ss", "1", "-threads", "2", "-i", "a.mp4", "-threads", "2", "-i", "b.wav", "-c:v", "libx264", "-threads", "2", "out.mp4"], args);
        // No cap: ffmpeg decides.
        Assert.Equal(["-i", "a", "o"], ResourceLimits.Unlimited.ApplyToFfmpeg(["-i", "a", "o"]));
        Assert.Equal(Environment.ProcessorCount, new ResourceLimits(MaxThreads: 999).Threads);
    }

    /// <summary>Niceness of a thread or process (Linux), from /proc.</summary>
    private static int Nice(string statPath)
    {
        var stat = File.ReadAllText(statPath);
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(fields[16]); // field 19 of stat, after pid and (comm)
    }

    [Fact]
    public void Low_priority_lowers_child_processes_and_the_job_thread()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var limits = new ResourceLimits(LowPriority: true);
        using var sleeper = Process.Start(new ProcessStartInfo("sleep", "5") { UseShellExecute = false })!;
        limits.Apply(sleeper);
        Assert.Equal(10, Nice($"/proc/{sleeper.Id}/stat"));
        sleeper.Kill();

        int? threadNice = null, childThreadNice = null;
        var thread = new Thread(() =>
        {
            limits.LowerDedicatedThread();
            threadNice = Nice("/proc/thread-self/stat");
            // Threads it creates (as ONNX Runtime's workers are) inherit it.
            var child = new Thread(() => childThreadNice = Nice("/proc/thread-self/stat"));
            child.Start();
            child.Join();
        });
        thread.Start();
        thread.Join();
        Assert.Equal(10, threadNice);
        Assert.Equal(10, childThreadNice);
        // The calling (test) thread is untouched.
        Assert.Equal(0, Nice("/proc/thread-self/stat"));
    }

    [Fact]
    public async Task Thread_cap_limits_onnx_cpu_use()
    {
        if (Environment.ProcessorCount < 4 || OperatingSystem.IsWindows())
            return;
        var source = await TestMedia.PatternAsync(2, 320, 240);
        var dir = TestMedia.NewOutputDirectory();
        // ONNX work runs in this process, so this process's CPU time against wall time is its parallelism.
        var me = Process.GetCurrentProcess();
        var cpu0 = me.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await new Converter().ConvertAsync(source, Path.Combine(dir, "capped.mp4"), new ConversionSettings
        {
            Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiGeneral, Width = 640, Height = 480,
            Limits = new ResourceLimits(MaxThreads: 2),
        });
        me.Refresh();
        var parallelism = (me.TotalProcessorTime - cpu0).TotalSeconds / wall.Elapsed.TotalSeconds;
        Assert.True(parallelism < 3.0, $"used {parallelism:0.0} cores with a cap of 2");
        Assert.Equal(TestMedia.FrameCount(source), TestMedia.FrameCount(Path.Combine(dir, "capped.mp4")));
    }

    [Fact]
    public async Task Ai_device_choice_picks_the_engine()
    {
        var cpu = new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiAnimation, Limits = new ResourceLimits(Device: AiDevice.Cpu) };
        Assert.Equal(UpscaleKind.Onnx, Converter.EngineFor(cpu));
        var gpu = cpu with { Limits = new ResourceLimits(Device: AiDevice.Gpu) };
        // On Linux the graphics card means the Vulkan program, when it's installed; otherwise ONNX on the CPU.
        var vulkan = Converter.VulkanAvailable(UpscaleMethods.Get(ScaleAlgorithm.AiAnimation));
        Assert.Equal(vulkan ? UpscaleKind.AiFrames : UpscaleKind.Onnx, Converter.EngineFor(gpu));
        // Other ONNX methods have no Vulkan alternative.
        Assert.Equal(UpscaleKind.Onnx, Converter.EngineFor(gpu with { Algorithm = ScaleAlgorithm.ArtCnn }));

        if (!vulkan)
            return;
        var source = await TestMedia.PatternAsync(0.2, 64, 48);
        var output = Path.Combine(TestMedia.NewOutputDirectory(), "vulkan.mp4");
        await new Converter().ConvertAsync(source, output, gpu with { Width = 128, Height = 96, Limits = new ResourceLimits(MaxThreads: 2, LowPriority: true, Device: AiDevice.Gpu) });
        Assert.Equal((128, 96), ((await MediaProbe.Default.ProbeAsync(output)).Width, (await MediaProbe.Default.ProbeAsync(output)).Height));
        Assert.Equal(TestMedia.FrameCount(source), TestMedia.FrameCount(output));
    }
}

[CollectionDefinition(nameof(RunAlone), DisableParallelization = true)]
public sealed class RunAlone;
