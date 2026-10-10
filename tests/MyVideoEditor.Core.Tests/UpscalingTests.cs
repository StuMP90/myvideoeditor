using System.Diagnostics;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Upscaling;

namespace MyVideoEditor.Core.Tests;

/// <summary>
/// Upscaling: GPU detection, sizes and colours, the download/verify/install of components, the
/// batched AI pipeline (with a stand-in program, so it runs anywhere), and every real method on
/// this computer's GPU when there is one. Real methods are checked for correctness (size, frame
/// count, colours kept, a picture that resembles the source), not for "beating" Lanczos: fidelity
/// scores penalise the very sharpening and detail these methods add.
/// </summary>
public class UpscalingTests
{
    // ---- detection, sizes, colours, escaping -------------------------------------------------

    [Fact]
    public void Gpu_list_is_parsed_and_software_renderers_are_never_chosen()
    {
        const string output = """
            [Vulkan @ 0x5754] GPU listing:
            [Vulkan @ 0x5754]     0: llvmpipe (LLVM 20.1.2, 256 bits) (software) (0x0)
            [Vulkan @ 0x5754]     1: Intel(R) Iris(R) Xe Graphics (ADL GT2) (integrated) (0x46a6)
            [Vulkan @ 0x5754]     2: NVIDIA GeForce RTX 4070 (discrete) (0x2786)
            """;
        var devices = VulkanDevices.Parse(output);
        Assert.Equal(3, devices.Count);
        Assert.Equal(GpuKind.Software, devices[0].Kind);
        Assert.Equal("NVIDIA GeForce RTX 4070", devices[2].Name);
        Assert.Equal(2, VulkanDevices.Choose(devices, allowSoftware: false)!.Index);
        Assert.Equal(1, VulkanDevices.Choose(devices.Take(2), allowSoftware: false)!.Index);
        Assert.Null(VulkanDevices.Choose(devices.Take(1), allowSoftware: false));
        Assert.Equal(0, VulkanDevices.Choose(devices.Take(1), allowSoftware: true)!.Index);
    }

    [Theory]
    [InlineData(640, 360, 1280, 720, ResizeMode.Fit, 1280, 720, null)]
    [InlineData(640, 480, 1920, 1080, ResizeMode.Fit, 1440, 1080, "pad=1920:1080")]
    [InlineData(640, 480, 1920, 1080, ResizeMode.Fill, 1920, 1440, "crop=1920:1080")]
    [InlineData(640, 480, 1920, 1080, ResizeMode.Stretch, 1920, 1080, null)]
    [InlineData(640, 480, 0, 960, ResizeMode.Fit, 1280, 960, null)]
    public void Scaled_sizes_for_each_mode(int sw, int sh, int w, int h, ResizeMode mode, int ew, int eh, string? post)
    {
        var size = ScaledSize.For(sw, sh, w == 0 ? null : w, h, mode);
        Assert.Equal((ew, eh), (size.Width, size.Height));
        if (post == null)
            Assert.Null(size.PostFilter);
        else
            Assert.StartsWith(post, size.PostFilter);
    }

    [Fact]
    public void Untagged_colours_are_guessed_by_size()
    {
        Assert.Equal("bt709", ColorSpec.For(new MediaInfo { Path = "x", Width = 1920, Height = 1080 }).Space);
        Assert.Equal("bt601", ColorSpec.For(new MediaInfo { Path = "x", Width = 640, Height = 480 }).SwsMatrix);
        Assert.True(ColorSpec.For(new MediaInfo { Path = "x", Width = 3840, Height = 2160, ColorTransfer = "smpte2084" }).IsHdr);
    }

    [Fact]
    public void Filter_paths_are_escaped_for_windows_drives_and_apostrophes()
    {
        Assert.Equal(@"C\\:/Users/O\\\'Brien/a.glsl", Converter.EscapeFilterPath(@"C:\Users\O'Brien\a.glsl"));
        Assert.Equal(@"/x/a\[1\]\,b\;c.glsl", Converter.EscapeFilterPath("/x/a[1],b;c.glsl"));
    }

    [Fact]
    public void Gpu_and_ai_methods_fall_back_to_lanczos_when_not_enlarging()
    {
        var info = new MediaInfo { Path = "x", Width = 1920, Height = 1080, HasVideo = true };
        var down = Converter.Effective(info, new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiPhoto, Width = 1280, Height = 720 });
        Assert.Equal(ScaleAlgorithm.Lanczos, down.Algorithm);
        var up = Converter.Effective(info, new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiPhoto, Width = 3840, Height = 2160 });
        Assert.Equal(ScaleAlgorithm.AiPhoto, up.Algorithm);
        Assert.Equal(4, AiUpscaleJob.ModelScale(ScaleAlgorithm.AiPhoto, 1.5));
        Assert.Equal(2, AiUpscaleJob.ModelScale(ScaleAlgorithm.AiAnimation, 1.5));
        Assert.Equal(3, AiUpscaleJob.ModelScale(ScaleAlgorithm.AiAnimation, 2.25));
        Assert.Equal(4, AiUpscaleJob.ModelScale(ScaleAlgorithm.AiAnime, 6));
    }

    // ---- components ---------------------------------------------------------------------------

    private static UpscalerComponent LocalComponent(string file, string sha) => new()
    {
        Id = "test-" + Guid.NewGuid().ToString("N")[..6],
        Label = "Test component",
        Licence = "MIT",
        Homepage = "https://example.invalid",
        Downloads = new Dictionary<string, ComponentDownload> { ["any"] = new(new Uri(file).AbsoluteUri, new FileInfo(file).Length, sha) },
    };

    [Fact]
    public async Task Components_install_only_when_the_checksum_matches()
    {
        var dir = TestMedia.NewOutputDirectory();
        var file = Path.Combine(dir, "thing.glsl");
        await File.WriteAllTextAsync(file, "// shader");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

        var bad = LocalComponent(file, new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpscalerComponents.InstallAsync(bad));
        Assert.False(UpscalerComponents.IsInstalled(bad));
        Assert.False(Directory.Exists(UpscalerComponents.InstallDirectory(bad)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(UpscalerComponents.Directory, bad.Id + "*"));

        var good = LocalComponent(file, sha);
        var progress = new List<double>();
        await UpscalerComponents.InstallAsync(good, new Progress<double>(progress.Add));
        Assert.True(UpscalerComponents.IsInstalled(good));
        Assert.True(File.Exists(Path.Combine(UpscalerComponents.InstallDirectory(good), "thing.glsl")));
        UpscalerComponents.Uninstall(good);
        Assert.False(UpscalerComponents.IsInstalled(good));
    }

    [Fact]
    public void Installed_copies_must_match_the_pinned_checksum()
    {
        var component = UpscalerComponents.RealEsrgan;
        if (component.ForThisComputer is not { } download)
            return;
        var tools = Path.Combine(TestMedia.NewOutputDirectory(), "tools");
        void Mark(string sha)
        {
            Directory.CreateDirectory(Path.Combine(tools, component.Id));
            File.WriteAllText(Path.Combine(tools, component.Id, ".installed"), sha);
        }
        Assert.Null(UpscalerComponents.Locate(component, tools));
        Mark(new string('f', 64)); // a different build: ignored
        Assert.Null(UpscalerComponents.Locate(component, tools));
        Mark(download.Sha256);
        Assert.Equal(Path.Combine(tools, component.Id), UpscalerComponents.Locate(component, tools));
    }

    [Fact]
    public void Every_bundled_model_has_a_licence_text_and_no_programs_are_bundled()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MyVideoEditor.sln")))
            root = Path.GetDirectoryName(root)!;
        var notices = string.Join("\n", Directory.GetFiles(Path.Combine(root, "tools", "notices", "upscalers"), "*.txt").Select(File.ReadAllText));
        var models = Directory.GetFiles(Path.Combine(root, "src", "MyVideoEditor.Core", "Upscaling", "Models"), "*.onnx");
        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.Contains(Path.GetFileName(m), notices));
        // Every AI model runs in-process (ONNX Runtime), so the Windows package carries no separate programs.
        Assert.DoesNotContain("upscalers", File.ReadAllText(Path.Combine(root, "packaging", "build-msix.ps1")));
        Assert.All(UpscalerComponents.All, c => Assert.False(c.Downloads.ContainsKey("win-x64"), c.Id));
    }

    [Fact]
    public void Every_component_has_a_pinned_download_where_it_is_used()
    {
        foreach (var c in UpscalerComponents.All)
        {
            var any = c.Downloads.ContainsKey("any");
            Assert.True(any || c.Downloads.ContainsKey("linux-x64"), c.Id);
            Assert.All(c.Downloads.Values, d =>
            {
                Assert.StartsWith("https://github.com/", d.Url);
                Assert.Matches("^[0-9a-f]{64}$", d.Sha256);
                Assert.True(d.Size > 0);
            });
        }
    }

    // ---- the AI pipeline, with a stand-in program ---------------------------------------------

    /// <summary>
    /// A stand-in for realesrgan/realcugan: takes the same arguments, enlarges each frame with
    /// ffmpeg, and (when asked) fails with automatic tiling, like a GPU running out of memory.
    /// </summary>
    private static string FakeUpscaler(string dir, bool failWithAutoTile, int sleepSeconds = 0)
    {
        var script = Path.Combine(dir, "fake-ncnn-vulkan");
        File.WriteAllText(script, $$"""
            #!/usr/bin/env bash
            set -e
            while [ $# -gt 0 ]; do
              case "$1" in
                -i) IN="$2"; shift 2;; -o) OUT="$2"; shift 2;; -s) SCALE="$2"; shift 2;; -t) TILE="$2"; shift 2;;
                *) shift;;
              esac
            done
            if [ "{{(failWithAutoTile ? "1" : "0")}}" = "1" ] && [ "$TILE" = "0" ]; then echo "vkAllocateMemory failed" >&2; exit 255; fi
            echo "$TILE" >> "{{dir}}/tiles.log"
            echo $$ > "{{dir}}/pid"
            ls "$IN" | wc -l >> "{{dir}}/frames.log"
            sleep {{sleepSeconds}}
            for f in "$IN"/*.png; do
              "{{TestMedia.Locator.FfmpegPath}}" -v error -i "$f" -vf "scale=iw*$SCALE:ih*$SCALE:flags=neighbor" -y "$OUT/$(basename "$f")"
            done
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact]
    public async Task Ai_pipeline_keeps_every_frame_the_sound_and_the_colours_across_batches()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var dir = TestMedia.NewOutputDirectory();
        // 29.97 fps: a rounded "29.97" would drift; 23 frames over batches of 5 tests the batch edges.
        var source = await TestMedia.NtscPatternAsync(23 / (30000.0 / 1001), 160, 120);
        var info = await MediaProbe.Default.ProbeAsync(source);
        Assert.Equal("30000/1001", info.FrameRateText);
        var output = Path.Combine(dir, "ai.mp4");
        var converter = new Converter
        {
            EngineOverride = UpscaleKind.AiFrames,
            AiJob = new AiUpscaleJob { ProgramOverride = FakeUpscaler(dir, failWithAutoTile: true), BatchFramesOverride = 5, WorkDirectory = Path.Combine(dir, "work") },
        };
        var progress = new List<FfmpegProgress>();
        await converter.ConvertAsync(source, output, new ConversionSettings
        {
            Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiAnime, Width = 320, Height = 240, Quality = Quality.High,
        }, new Progress<FfmpegProgress>(progress.Add));

        var result = await MediaProbe.Default.ProbeAsync(output);
        Assert.Equal((320, 240), (result.Width, result.Height));
        Assert.Equal(TestMedia.FrameCount(source), TestMedia.FrameCount(output));
        Assert.Equal("30000/1001", result.FrameRateText);
        Assert.True(result.HasAudio);
        Assert.InRange(result.Duration, info.Duration - 0.05, info.Duration + 0.05);
        Assert.Equal("bt601", ColorSpec.For(info).SwsMatrix);
        Assert.Equal("smpte170m", result.ColorSpace);
        // Same picture as a straight Lanczos enlargement (the stand-in uses nearest-neighbour, so not identical).
        var lanczos = Path.Combine(dir, "lanczos.mp4");
        await new Converter().ConvertAsync(source, lanczos, new ConversionSettings { Format = OutputFormats.Mp4H264, Width = 320, Height = 240, Quality = Quality.High });
        Assert.True(TestMedia.Ssim(output, lanczos) > 0.85);
        // The first batch failed with automatic tiling and was retried with tiles; later batches used the tile size that worked.
        var tiles = File.ReadAllLines(Path.Combine(dir, "tiles.log"));
        Assert.Equal(5, tiles.Length);
        Assert.All(tiles, t => Assert.Equal("256", t));
        Assert.False(Directory.Exists(Path.Combine(dir, "work")));
        Assert.False(File.Exists(Path.Combine(dir, "ai.partial.mp4")));
    }

    [Fact]
    public async Task Ai_pipeline_cleans_up_when_cancelled()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var dir = TestMedia.NewOutputDirectory();
        var source = await TestMedia.PatternAsync(4, 160, 120);
        var work = Path.Combine(dir, "work");
        var converter = new Converter { EngineOverride = UpscaleKind.AiFrames, AiJob = new AiUpscaleJob { ProgramOverride = FakeUpscaler(dir, false), BatchFramesOverride = 10, WorkDirectory = work } };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => converter.ConvertAsync(source, Path.Combine(dir, "x.mp4"),
            new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiAnime, Width = 320, Height = 240 }, null, cts.Token));
        Assert.False(Directory.Exists(work));
        Assert.False(File.Exists(Path.Combine(dir, "x.mp4")));
        Assert.False(File.Exists(Path.Combine(dir, "x.partial.mp4")));
    }

    // ---- real methods on this computer ---------------------------------------------------------

    /// <summary>Installs a component if needed; false (skip) when there's no GPU or no internet.</summary>
    private static async Task<bool> ReadyAsync(UpscaleMethod method)
    {
        if (method.Unavailable != null)
            return false;
        if (method.Component == null || UpscalerComponents.IsInstalled(method.Component))
            return true;
        try
        {
            await UpscalerComponents.InstallAsync(method.Component);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData(ScaleAlgorithm.ArtCnn)]
    [InlineData(ScaleAlgorithm.GpuLines)]
    [InlineData(ScaleAlgorithm.AiAnimation)]
    [InlineData(ScaleAlgorithm.AiGeneral)]
    [InlineData(ScaleAlgorithm.AiAnime)]
    [InlineData(ScaleAlgorithm.AiPhoto)]
    public async Task Real_methods_produce_a_correct_video(ScaleAlgorithm algorithm)
    {
        var method = UpscaleMethods.Get(algorithm);
        if (!await ReadyAsync(method))
            return;
        var ai = method.IsAi;
        // Small, so AI methods finish quickly even on modest GPUs (or a software one in CI).
        var source = await TestMedia.PatternAsync(ai ? 0.2 : 1, ai ? 64 : 320, ai ? 48 : 240);
        var red = await TestMedia.ColorAsync("red", 0.2, ai ? 64 : 320, ai ? 48 : 240);
        var dir = TestMedia.NewOutputDirectory();
        var (w, h) = ai ? (128, 96) : (640, 480);
        var settings = new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = algorithm, Width = w, Height = h, Quality = Quality.High };

        var output = Path.Combine(dir, "up.mp4");
        await new Converter().ConvertAsync(source, output, settings);
        var result = await MediaProbe.Default.ProbeAsync(output);
        Assert.Equal((w, h), (result.Width, result.Height));
        Assert.Equal(TestMedia.FrameCount(source), TestMedia.FrameCount(output));
        Assert.True(result.HasAudio);
        Assert.True(TestMedia.Ssim(output, source) > 0.6, "the picture resembles the source");

        // Colours survive the trip through RGB, the GPU and back.
        var redOut = Path.Combine(dir, "red.mp4");
        await new Converter().ConvertAsync(red, redOut, settings);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(redOut, 0, w / 2, h / 2), (255, 0, 0), 30), $"{TestMedia.PixelAt(redOut, 0, w / 2, h / 2)}");
    }

    [Fact]
    public async Task Gpu_shader_works_from_a_folder_with_awkward_characters()
    {
        var method = UpscaleMethods.Get(ScaleAlgorithm.GpuLines);
        if (!await ReadyAsync(method))
            return;
        var shader = UpscalerComponents.ShaderPath(method.Component!)!;
        var awkward = Path.Combine(TestMedia.NewOutputDirectory(), "we:ird [dir], o'brien; x");
        if (OperatingSystem.IsWindows())
            awkward = awkward.Replace(":", "");
        Directory.CreateDirectory(awkward);
        var copy = Path.Combine(awkward, "shader.glsl");
        File.Copy(shader, copy);
        var filters = string.Join(',', Converter.GpuShaderFilters(new MediaInfo { Path = "x", Width = 320, Height = 240 },
            new ConversionSettings { Format = OutputFormats.Mp4H264, Width = 640, Height = 480, Algorithm = ScaleAlgorithm.GpuLines }, method))
            .Replace(Converter.EscapeFilterPath(shader), Converter.EscapeFilterPath(copy));
        var gpu = VulkanDevices.Best!;
        await new FfmpegRunner().RunAsync(["-init_hw_device", $"vulkan=gpu:{gpu.Index}", "-filter_hw_device", "gpu",
            "-i", await TestMedia.PatternAsync(1, 320, 240), "-frames:v", "2", "-vf", filters, "-f", "null", "-"]);
    }

    [Fact]
    public async Task Benchmark_estimates_and_remembers()
    {
        var source = await TestMedia.PatternAsync(4, 320, 240);
        var info = await MediaProbe.Default.ProbeAsync(source);
        var cache = Path.Combine(TestMedia.NewOutputDirectory(), "speeds.json");
        var bench = new UpscaleBenchmark(cacheFile: cache);
        var settings = new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.Enhanced, Width = 640, Height = 480 };
        var estimate = await bench.EstimateAsync(info, settings);
        Assert.Null(estimate.Error);
        Assert.Equal(120, estimate.Frames);
        Assert.InRange(estimate.SecondsPerFrame, 0.0001, 1);
        Assert.True(File.Exists(cache));
        var sw = Stopwatch.StartNew();
        var again = await bench.EstimateAsync(info, settings with { TrimEnd = 2 });
        Assert.True(sw.ElapsedMilliseconds < 200, "second estimate comes from the cache");
        Assert.Equal(estimate.SecondsPerFrame, again.SecondsPerFrame);
        Assert.Equal(60, again.Frames);

        var gpu = UpscaleMethods.Get(ScaleAlgorithm.AiAnimation);
        if (await ReadyAsync(gpu))
        {
            var ai = await new UpscaleBenchmark(cacheFile: cache) { AiBudget = TimeSpan.FromSeconds(120) }
                .EstimateAsync(info, settings with { Algorithm = ScaleAlgorithm.AiAnimation });
            Assert.Null(ai.Error);
            Assert.True(ai.SecondsPerFrame > 0);
        }
    }

    [Theory]
    [InlineData(ScaleAlgorithm.Lanczos)]
    [InlineData(ScaleAlgorithm.ArtCnn)]
    [InlineData(ScaleAlgorithm.GpuLines)]
    [InlineData(ScaleAlgorithm.AiGeneral)]
    [InlineData(ScaleAlgorithm.AiAnime)]
    [InlineData(ScaleAlgorithm.AiAnime, true)]
    public async Task Previews_show_one_frame_before_and_after(ScaleAlgorithm algorithm, bool vulkanProgram = false)
    {
        var method = UpscaleMethods.Get(algorithm);
        if (vulkanProgram && !OperatingSystem.IsLinux())
            return;
        if (!vulkanProgram && !await ReadyAsync(method))
            return;
        var dir = TestMedia.NewOutputDirectory();
        // The Vulkan program pipeline uses a stand-in program, so this runs without a GPU.
        var converter = vulkanProgram
            ? new Converter { EngineOverride = UpscaleKind.AiFrames, AiJob = new AiUpscaleJob { ProgramOverride = FakeUpscaler(dir, false) } }
            : new Converter();
        var red = await TestMedia.ColorAsync("red", 2, 160, 120);
        var settings = new ConversionSettings { Format = OutputFormats.WebmVp9, Algorithm = algorithm, Width = 640, Height = 360, ResizeMode = ResizeMode.Fit };
        var before = Path.Combine(dir, "before.png");
        var after = Path.Combine(dir, "after.png");
        await converter.RenderPreviewAsync(red, settings, 1, before, after);

        var b = await MediaProbe.Default.ProbeAsync(before);
        var a = await MediaProbe.Default.ProbeAsync(after);
        // Both laid out the same way (the original enlarged into the same place), so they line up.
        Assert.Equal((640, 360), (b.Width, b.Height));
        Assert.Equal((640, 360), (a.Width, a.Height));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(before, 0, 320, 180), (255, 0, 0), 12), $"before {TestMedia.PixelAt(before, 0, 320, 180)}");
        Assert.True(TestMedia.Near(TestMedia.PixelAt(before, 0, 10, 180), (0, 0, 0), 12));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(after, 0, 320, 180), (255, 0, 0), 12), $"after {TestMedia.PixelAt(after, 0, 320, 180)}");
        // Fit: black bars at the sides.
        Assert.True(TestMedia.Near(TestMedia.PixelAt(after, 0, 10, 180), (0, 0, 0), 12));
    }

    [Fact]
    public async Task Ai_preview_upscales_exactly_one_frame()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var dir = TestMedia.NewOutputDirectory();
        var converter = new Converter { EngineOverride = UpscaleKind.AiFrames, AiJob = new AiUpscaleJob { ProgramOverride = FakeUpscaler(dir, false) } };
        // 29.97 fps, at a time between frames: the window must still yield just one.
        var source = await TestMedia.NtscPatternAsync(3, 160, 120);
        foreach (var time in new[] { 0.0, 1.017, 2.9 })
        {
            File.Delete(Path.Combine(dir, "frames.log"));
            await converter.RenderPreviewAsync(source, new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiAnime, Width = 320, Height = 240 },
                time, Path.Combine(dir, "b.png"), Path.Combine(dir, "a.png"));
            Assert.Equal(["1"], File.ReadAllLines(Path.Combine(dir, "frames.log")).Select(l => l.Trim()));
        }
    }

    [Fact]
    public async Task Cancelling_an_ai_preview_stops_everything()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var dir = TestMedia.NewOutputDirectory();
        var work = Path.Combine(dir, "work");
        var converter = new Converter { EngineOverride = UpscaleKind.AiFrames, AiJob = new AiUpscaleJob { ProgramOverride = FakeUpscaler(dir, false, sleepSeconds: 60), WorkDirectory = work } };
        var source = await TestMedia.PatternAsync(2, 160, 120);
        using var cts = new CancellationTokenSource();
        var preview = converter.RenderPreviewAsync(source, new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.AiAnime, Width = 320, Height = 240 },
            1, Path.Combine(dir, "b.png"), Path.Combine(dir, "a.png"), cts.Token);
        var pidFile = Path.Combine(dir, "pid");
        for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
            await Task.Delay(50);
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview);
        await Task.Delay(200);
        Assert.False(ProcessIsRunning(pid), "the AI program was stopped");
        Assert.False(Directory.Exists(work));
        Assert.False(File.Exists(Path.Combine(dir, "a.png")));
    }

    private static bool ProcessIsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // ---- ONNX ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("artcnn")]
    [InlineData("general")]
    public async Task Onnx_tiles_join_without_seams(string which)
    {
        var model = which == "artcnn" ? OnnxModel.ArtCnn : OnnxModel.RealEsrganGeneral;
        var luma = model.Channels == OnnxChannels.Luma;
        var (w, h) = (96, 72);
        // A real picture: a frame of the test pattern, as the model will get it.
        var source = await TestMedia.PatternAsync(1, w, h);
        var raw = Path.Combine(TestMedia.NewOutputDirectory(), "frame.raw");
        await new FfmpegRunner().RunAsync(["-i", source, "-frames:v", "1", "-pix_fmt", luma ? "yuv444p" : "rgb24", "-f", "rawvideo", "-y", raw]);
        var frame = await File.ReadAllBytesAsync(raw);
        var whole = new OnnxUpscaler(model with { TileSize = 4096 }).Upscale(frame, w, h);
        var tiled = new OnnxUpscaler(model with { TileSize = 40 }).Upscale(frame, w, h);
        Assert.Equal(whole.Length, tiled.Length);
        var worst = whole.Zip(tiled, (a, b) => Math.Abs(a - b)).Max();
        Assert.True(worst <= 3, $"tiles differ from the whole frame by up to {worst}");
    }

    [Theory]
    [InlineData(2, 98, 74)]
    [InlineData(3, 97, 73)] // odd sides: the 3x model needs multiples of 4
    [InlineData(4, 30, 22)] // smaller than the model's minimum
    public async Task Real_cugan_tiles_fit_the_model_at_any_size(int scale, int w, int h)
    {
        var method = UpscaleMethods.Get(ScaleAlgorithm.AiAnime);
        var model = method.OnnxModelFor(scale)!;
        Assert.Equal(scale, model.Scale);
        var source = await TestMedia.PatternAsync(1, w, h);
        var raw = Path.Combine(TestMedia.NewOutputDirectory(), "frame.raw");
        await new FfmpegRunner().RunAsync(["-i", source, "-frames:v", "1", "-vf", $"scale={w}:{h}", "-pix_fmt", "rgb24", "-f", "rawvideo", "-y", raw]);
        var frame = await File.ReadAllBytesAsync(raw);
        var whole = new OnnxUpscaler(model with { TileSize = 4096 }).Upscale(frame, w, h);
        var tiled = new OnnxUpscaler(model with { TileSize = 32 }).Upscale(frame, w, h);
        Assert.Equal(w * scale * h * scale * 3, whole.Length);
        Assert.Equal(whole.Length, tiled.Length);
        // Its squeeze-and-excitation blocks average over each tile, so tiles differ slightly from the whole frame.
        var mse = whole.Zip(tiled, (a, b) => (double)(a - b) * (a - b)).Average();
        var psnr = mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255 / mse);
        Assert.True(psnr > 35, $"tiles differ from the whole frame: {psnr:0.0} dB");
    }

    [Fact]
    public void Real_cugan_uses_the_smallest_model_that_reaches_the_size()
    {
        var method = UpscaleMethods.Get(ScaleAlgorithm.AiAnime);
        Assert.Equal(2, method.OnnxModelFor(1.5)!.Scale);
        Assert.Equal(2, method.OnnxModelFor(2)!.Scale);
        Assert.Equal(3, method.OnnxModelFor(2.25)!.Scale);
        Assert.Equal(4, method.OnnxModelFor(4)!.Scale);
        Assert.Equal(4, method.OnnxModelFor(6)!.Scale);
        Assert.Same(OnnxModel.ArtCnn, UpscaleMethods.Get(ScaleAlgorithm.ArtCnn).OnnxModelFor(4));
    }

    [Fact]
    public async Task Ai_strength_mixes_in_a_faithful_enlargement()
    {
        var source = await TestMedia.PatternAsync(0.2, 64, 48);
        var dir = TestMedia.NewOutputDirectory();
        async Task<string> Convert(ScaleAlgorithm algorithm, double strength)
        {
            var output = Path.Combine(dir, $"{algorithm}-{strength}.mp4");
            await new Converter().ConvertAsync(source, output, new ConversionSettings
            {
                Format = OutputFormats.Mp4H264, Algorithm = algorithm, AiStrength = strength, Width = 256, Height = 192, Quality = Quality.High,
            });
            return output;
        }
        var lanczos = await Convert(ScaleAlgorithm.Lanczos, 1);
        var none = await Convert(ScaleAlgorithm.AiGeneral, 0);
        var half = await Convert(ScaleAlgorithm.AiGeneral, 0.5);
        var full = await Convert(ScaleAlgorithm.AiGeneral, 1);
        Assert.True(TestMedia.Ssim(none, lanczos) > 0.985, "0% AI is a plain Lanczos enlargement");
        var halfToLanczos = TestMedia.Ssim(half, lanczos);
        Assert.True(halfToLanczos < TestMedia.Ssim(none, lanczos) && halfToLanczos > TestMedia.Ssim(full, lanczos), "50% lies in between");
        Assert.Equal(TestMedia.FrameCount(source), TestMedia.FrameCount(half));
    }

    [Fact]
    public async Task Onnx_runs_on_the_device_this_computer_offers()
    {
        // On Windows this is DirectML when there's a DirectX 12 adapter (CI: Microsoft's software one), else the CPU.
        var device = OnnxEngine.DeviceFor(OnnxModel.ArtCnn);
        Assert.Contains(device, new[] { "CPU", "graphics card (DirectML)" });
        if (!OperatingSystem.IsWindows())
            Assert.Equal("CPU", device);
        var output = Path.Combine(TestMedia.NewOutputDirectory(), "art.mp4");
        await new Converter().ConvertAsync(await TestMedia.ColorAsync("red", 0.2, 64, 48), output,
            new ConversionSettings { Format = OutputFormats.Mp4H264, Algorithm = ScaleAlgorithm.ArtCnn, Width = 256, Height = 192 });
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 0, 128, 96), (255, 0, 0), 30));
    }

    [Theory]
    [InlineData(ScaleAlgorithm.AiPhoto, 0.97)]
    // Real-CUGAN's program has the original 2022 weights; the ONNX models are converted from the updated ones.
    [InlineData(ScaleAlgorithm.AiAnime, 0.95)]
    public async Task Onnx_conversions_match_the_original_programs(ScaleAlgorithm algorithm, double minimumSsim)
    {
        // The ONNX conversion (half-precision weights) against the official Vulkan program, on the same frames.
        if (!Converter.VulkanAvailable(UpscaleMethods.Get(algorithm)))
            return;
        var source = await TestMedia.PatternAsync(0.2, 64, 48);
        var dir = TestMedia.NewOutputDirectory();
        async Task<string> Run(AiDevice device)
        {
            var output = Path.Combine(dir, $"{algorithm}-{device}.mp4");
            await new Converter().ConvertAsync(source, output, new ConversionSettings
            {
                Format = OutputFormats.Mp4H264, Algorithm = algorithm, Width = 256, Height = 192, Quality = Quality.High,
                Limits = new ResourceLimits(Device: device),
            });
            return output;
        }
        var onnx = await Run(AiDevice.Cpu);
        var vulkan = await Run(AiDevice.Gpu);
        Assert.True(TestMedia.Ssim(onnx, vulkan) > minimumSsim, $"SSIM {TestMedia.Ssim(onnx, vulkan):0.000}");
    }

    [Fact]
    public void Recommendation_follows_content_and_time()
    {
        var est = new Dictionary<ScaleAlgorithm, UpscaleEstimate>
        {
            [ScaleAlgorithm.AiAnimation] = new(ScaleAlgorithm.AiAnimation, 100, 0, 1000),
            [ScaleAlgorithm.AiAnime] = new(ScaleAlgorithm.AiAnime, 100, 0, 1000),
            [ScaleAlgorithm.AiPhoto] = new(ScaleAlgorithm.AiPhoto, 100, 0, 1000),
        };
        var camera = UpscaleMethods.Recommend(ContentType.Camera, est, TimeSpan.FromHours(1));
        // AI methods without a time estimate aren't assumed fast; ArtCNN (on the CPU) is.
        Assert.Equal(ScaleAlgorithm.ArtCnn, camera.Algorithm);
        est[ScaleAlgorithm.AiAnimation] = new(ScaleAlgorithm.AiAnimation, 0.1, 5, 1000);
        var animation = UpscaleMethods.Recommend(ContentType.Animation, est, TimeSpan.FromHours(1));
        Assert.Equal(ScaleAlgorithm.AiAnimation, animation.Algorithm);
    }
}
