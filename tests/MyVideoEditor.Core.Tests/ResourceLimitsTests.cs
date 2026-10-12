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

    /// <summary>The processors a thread or process may run on (Linux), from /proc.</summary>
    private static SortedSet<int> Cpus(string statusPath)
    {
        var list = File.ReadLines(statusPath).First(l => l.StartsWith("Cpus_allowed_list:"))["Cpus_allowed_list:".Length..].Trim();
        var cpus = new SortedSet<int>();
        foreach (var range in list.Split(','))
        {
            var ends = range.Split('-').Select(int.Parse).ToArray();
            for (var cpu = ends[0]; cpu <= ends[^1]; cpu++)
                cpus.Add(cpu);
        }
        return cpus;
    }

    [Fact]
    public void Cap_keeps_the_app_and_its_processes_on_that_many_processors()
    {
        if (!OperatingSystem.IsLinux() || Environment.ProcessorCount < 4)
            return;
        var before = Cpus("/proc/self/status");
        // A thread that already exists, as ONNX Runtime's and ffmpeg readers' are by the time a job starts.
        using var started = new ManualResetEventSlim();
        using var check = new ManualResetEventSlim();
        SortedSet<int>? existing = null;
        var worker = new Thread(() => { started.Set(); check.Wait(); existing = Cpus("/proc/thread-self/status"); });
        worker.Start();
        started.Wait();

        using (new ResourceLimits(MaxThreads: 2).PinApp())
        {
            var pinned = Cpus("/proc/self/status");
            Assert.Equal(before.Take(2), pinned); // the first of those it was allowed, never others
            Assert.Equal(pinned, Cpus("/proc/thread-self/status"));
            check.Set();
            worker.Join();
            Assert.Equal(pinned, existing);
            SortedSet<int>? fresh = null;
            var later = new Thread(() => fresh = Cpus("/proc/thread-self/status"));
            later.Start();
            later.Join();
            Assert.Equal(pinned, fresh);
            using (var sleeper = Process.Start(new ProcessStartInfo("sleep", "5") { UseShellExecute = false })!)
            {
                Assert.Equal(pinned, Cpus($"/proc/{sleeper.Id}/status"));
                sleeper.Kill();
            }

            // Overlapping jobs: the larger cap applies while both run, then the remaining one's.
            using (new ResourceLimits(MaxThreads: 3).PinApp())
                Assert.Equal(before.Take(3), Cpus("/proc/self/status"));
            Assert.Equal(pinned, Cpus("/proc/self/status"));
            // An uncapped job isn't held to another job's cap.
            using (ResourceLimits.Unlimited.PinApp())
                Assert.Equal(before, Cpus("/proc/self/status"));
            Assert.Equal(pinned, Cpus("/proc/self/status"));
        }
        Assert.Equal(before, Cpus("/proc/self/status"));
        Assert.Equal(before, Cpus("/proc/thread-self/status"));

        // No cap: nothing changes.
        using (ResourceLimits.Unlimited.PinApp())
            Assert.Equal(before, Cpus("/proc/self/status"));
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

public class ChildProcessTests
{
    [Fact]
    public void Helper_programs_never_get_the_terminal_as_input()
    {
        // A killed ffmpeg that had the terminal as input leaves it without echo (typing invisible).
        // Every program the app starts goes through ChildProcess (or keeps a pipe as input itself).
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MyVideoEditor.sln")))
            root = Path.GetDirectoryName(root)!;
        var offenders = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith("ChildProcess.cs"))
            .Where(f =>
            {
                var code = File.ReadAllText(f);
                if (!code.Contains("ProcessStartInfo"))
                    return false;
                // Process.Start(...) is only allowed for UseShellExecute (opening a file with its app).
                var direct = System.Text.RegularExpressions.Regex.Matches(code, @"(?<!Child)Process\.Start\((?!new ProcessStartInfo\(path\) \{ UseShellExecute = true)");
                var ownProcess = code.Contains(".Start();") && !code.Contains("RedirectStandardInput = true");
                return direct.Count > 0 || ownProcess;
            })
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Ffmpeg_started_without_input_is_told_not_to_read_it()
    {
        var psi = new System.Diagnostics.ProcessStartInfo(TestMedia.Locator.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-version" })
            psi.ArgumentList.Add(a);
        using var p = ChildProcess.Start(psi, isFfmpeg: true);
        await p.WaitForExitAsync();
        Assert.Equal("-nostdin", psi.ArgumentList[0]);
        Assert.True(psi.RedirectStandardInput);
        Assert.Equal(0, p.ExitCode);
    }
}
