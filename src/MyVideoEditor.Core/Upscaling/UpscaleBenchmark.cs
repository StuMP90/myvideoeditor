using System.Diagnostics;
using System.Text.Json;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Upscaling;

/// <summary>How long a method would take for a job on this computer.</summary>
public sealed record UpscaleEstimate(ScaleAlgorithm Method, double SecondsPerFrame, double StartupSeconds, int Frames, string? Error = null)
{
    public TimeSpan Total => TimeSpan.FromSeconds(StartupSeconds + SecondsPerFrame * Frames);

    /// <summary>"about 4 min", "about 30 h", "under a minute".</summary>
    public string TotalText => Error != null ? "couldn't run" : Format(Total);

    public static string Format(TimeSpan t) => t.TotalSeconds switch
    {
        < 60 => "under a minute",
        < 3600 => $"about {Math.Ceiling(t.TotalMinutes)} min",
        < 48 * 3600 => $"about {t.TotalHours:0.#} h",
        _ => $"about {t.TotalDays:0} days",
    };
}

/// <summary>
/// Measures upscaling methods on a short sample of the user's own video, on this computer, and
/// estimates how long the whole job would take. Results are remembered (per method, GPU and
/// size) so the test only runs once. AI methods are timed for a limited budget; the time to
/// the first finished frame is the start-up cost, the pace after that the per-frame cost.
/// </summary>
public sealed class UpscaleBenchmark
{
    private const int SampleFrames = 30;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    private readonly FfmpegLocator _locator;
    private readonly string _cacheFile;

    public UpscaleBenchmark(FfmpegLocator? locator = null, string? cacheFile = null)
    {
        _locator = locator ?? FfmpegLocator.Default;
        _cacheFile = cacheFile ?? Path.Combine(AppPaths.DataDirectory, "upscale-speeds.json");
    }

    /// <summary>The time limit for timing an AI method (it stops early once it has two frames).</summary>
    public TimeSpan AiBudget { get; init; } = TimeSpan.FromSeconds(90);

    public async Task<UpscaleEstimate> EstimateAsync(MediaInfo info, ConversionSettings settings, CancellationToken cancellationToken = default)
    {
        var method = UpscaleMethods.Get(settings.Algorithm);
        var fps = settings.FrameRate ?? MediaProbe.ParseRatio(info.FrameRateText);
        var frames = Math.Max(1, (int)Math.Ceiling(Converter.OutputLength(info, settings) * fps));
        var (srcW, srcH) = settings.Rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
        var target = ScaledSize.For(srcW, srcH, settings.Width, settings.Height, settings.ResizeMode);
        var key = string.Join('|', method.Algorithm, VulkanDevices.Best?.Name ?? "no-gpu", _locator.Capabilities.Version, $"{srcW}x{srcH}", $"{target.Width}x{target.Height}");

        if (Lookup(key) is { } cached)
            return new UpscaleEstimate(method.Algorithm, cached.PerFrame, cached.Startup, frames);
        if (method.Unavailable is { } why)
            return new UpscaleEstimate(method.Algorithm, 0, 0, frames, why);

        try
        {
            var (perFrame, startup) = method.Kind == UpscaleKind.AiFrames
                ? await MeasureAiAsync(info, settings, cancellationToken).ConfigureAwait(false)
                : await MeasureConversionAsync(info, settings, cancellationToken).ConfigureAwait(false);
            Store(key, perFrame, startup);
            Log.Info("benchmark", $"{method.Label}: {perFrame:0.###} s/frame + {startup:0.#} s start-up ({srcW}x{srcH} -> {target.Width}x{target.Height}).");
            return new UpscaleEstimate(method.Algorithm, perFrame, startup, frames);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("benchmark", $"Timing {method.Label} failed.", ex);
            return new UpscaleEstimate(method.Algorithm, 0, 0, frames, ex.Message);
        }
    }

    /// <summary>CPU and GPU-shader methods: convert a 1-frame and a 30-frame sample; the difference is the per-frame cost.</summary>
    private async Task<(double PerFrame, double Startup)> MeasureConversionAsync(MediaInfo info, ConversionSettings settings, CancellationToken ct)
    {
        var dir = Path.Combine(AppPaths.CacheDirectory, "benchmark-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var fps = settings.FrameRate ?? MediaProbe.ParseRatio(info.FrameRateText);
            var start = SampleStart(info, settings, SampleFrames / fps);
            var converter = new Converter(_locator);
            async Task<double> Time(int frames)
            {
                var sample = settings with
                {
                    TrimStart = start,
                    TrimEnd = start + frames / fps,
                    Format = OutputFormats.MkvH264,
                    RemoveAudio = true,
                };
                var sw = Stopwatch.StartNew();
                await converter.ConvertAsync(info.Path, Path.Combine(dir, $"sample{frames}.mkv"), sample, null, ct).ConfigureAwait(false);
                return sw.Elapsed.TotalSeconds;
            }
            var one = await Time(1).ConfigureAwait(false);
            var many = await Time(SampleFrames).ConfigureAwait(false);
            var perFrame = Math.Max(0.0005, (many - one) / (SampleFrames - 1));
            return (perFrame, Math.Max(0, one - perFrame));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>AI methods: run the program on a few sample frames, timing the first result and the pace after it.</summary>
    private async Task<(double PerFrame, double Startup)> MeasureAiAsync(MediaInfo info, ConversionSettings settings, CancellationToken ct)
    {
        var method = UpscaleMethods.Get(settings.Algorithm);
        var program = UpscalerComponents.ProgramPath(method.Component!) ?? throw new InvalidOperationException($"{method.Component!.Label} isn't installed.");
        var gpu = VulkanDevices.Best ?? throw new InvalidOperationException("No usable Vulkan GPU.");
        var dir = Path.Combine(AppPaths.CacheDirectory, "benchmark-" + Guid.NewGuid().ToString("N")[..8]);
        var inDir = Path.Combine(dir, "in");
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(inDir);
        Directory.CreateDirectory(outDir);
        try
        {
            const int frames = 6;
            var fps = settings.FrameRate ?? MediaProbe.ParseRatio(info.FrameRateText);
            var start = SampleStart(info, settings, frames / fps);
            var (srcW, srcH) = settings.Rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
            var target = ScaledSize.For(srcW, srcH, settings.Width, settings.Height, settings.ResizeMode);
            var scale = AiUpscaleJob.ModelScale(settings.Algorithm, Math.Max((double)target.Width / srcW, (double)target.Height / srcH));
            var extract = new List<string> { "-ss", RenderPlanF(start), "-i", info.Path, "-frames:v", frames.ToString() };
            var rotation = Converter.RotationFilters(settings.Rotate).ToList();
            if (rotation.Count > 0)
                extract.AddRange(["-vf", string.Join(',', rotation)]);
            extract.Add(Path.Combine(inDir, "%08d.png"));
            await new FfmpegRunner(_locator).RunAsync(extract, cancellationToken: ct).ConfigureAwait(false);

            var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(program)! };
            foreach (var a in new[] { "-i", inDir, "-o", outDir, "-s", scale.ToString(), "-f", "png", "-g", gpu.Index.ToString(), "-j", "1:1:1" })
                psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add(settings.Algorithm switch { ScaleAlgorithm.AiAnimation => "realesr-animevideov3", ScaleAlgorithm.AiPhoto => "realesrgan-x4plus", _ => "-1" });
            var sw = Stopwatch.StartNew();
            using var p = Process.Start(psi)!;
            _ = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            double? first = null, last = null;
            var seen = 0;
            try
            {
                while (sw.Elapsed < AiBudget && !p.HasExited)
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                    var n = Directory.EnumerateFiles(outDir, "*.png").Count();
                    if (n > seen)
                    {
                        first ??= sw.Elapsed.TotalSeconds;
                        last = sw.Elapsed.TotalSeconds;
                        seen = n;
                    }
                    if (seen >= 3)
                        break;
                }
                if (p.HasExited)
                {
                    var n = Directory.EnumerateFiles(outDir, "*.png").Count();
                    if (n > seen)
                    {
                        first ??= sw.Elapsed.TotalSeconds;
                        last = sw.Elapsed.TotalSeconds;
                        seen = n;
                    }
                }
            }
            finally
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            if (first == null)
            {
                // Not even one frame in the time allowed: report at least that slow.
                return (AiBudget.TotalSeconds, AiBudget.TotalSeconds);
            }
            var perFrame = seen > 1 ? (last!.Value - first.Value) / (seen - 1) : first.Value * 0.5;
            return (Math.Max(0.001, perFrame), Math.Max(0, first.Value - perFrame));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A sample from the middle of the part being converted (beginnings are often black).</summary>
    private static double SampleStart(MediaInfo info, ConversionSettings s, double sampleLength)
    {
        var from = s.TrimStart ?? 0;
        var to = s.TrimEnd is > 0 ? s.TrimEnd.Value : info.Duration;
        return Math.Max(from, from + (to - from - sampleLength) / 2);
    }

    private static string RenderPlanF(double v) => Rendering.RenderPlan.F(v);

    // ---- cache -------------------------------------------------------------------------------

    private sealed record CacheEntry(double PerFrame, double Startup, DateTime MeasuredUtc);

    private readonly object _cacheLock = new();

    private CacheEntry? Lookup(string key)
    {
        lock (_cacheLock)
        {
            var all = Load();
            return all.TryGetValue(key, out var e) && DateTime.UtcNow - e.MeasuredUtc < CacheLifetime ? e : null;
        }
    }

    private void Store(string key, double perFrame, double startup)
    {
        lock (_cacheLock)
        {
            var all = Load();
            all[key] = new CacheEntry(perFrame, startup, DateTime.UtcNow);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                File.WriteAllText(_cacheFile, JsonSerializer.Serialize(all));
            }
            catch (IOException ex)
            {
                Log.Warn("benchmark", "Couldn't save timings.", ex);
            }
        }
    }

    /// <summary>Forgets all timings (after a driver or hardware change).</summary>
    public void Clear()
    {
        lock (_cacheLock)
            if (File.Exists(_cacheFile))
                File.Delete(_cacheFile);
    }

    private Dictionary<string, CacheEntry> Load()
    {
        try
        {
            if (File.Exists(_cacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(_cacheFile)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return [];
    }
}
