using System.Buffers.Binary;
using System.Diagnostics;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Upscaling;

/// <summary>
/// Upscales a video with an AI program (Real-ESRGAN or Real-CUGAN, ncnn/Vulkan builds), which
/// work on folders of images. To keep disk use bounded on long videos:
///
///   ffmpeg (decode) ──PNG stream──▶ batch folder ──AI program──▶ upscaled PNGs ──▶ ffmpeg (encode)
///
/// one decoder streams frames at the exact output frame rate; they're cut into batches sized to
/// the free disk space; each batch is upscaled, then its frames are piped to one encoder and
/// deleted. Colour conversion to and from RGB uses the same matrix both ways, and the output
/// keeps the source's sound. If the GPU runs out of memory, smaller tiles are tried.
/// </summary>
public sealed class AiUpscaleJob
{
    private static readonly int[] TileSizes = [0, 256, 128, 64];

    private readonly FfmpegLocator _locator;

    public AiUpscaleJob(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    /// <summary>Use this program instead of the installed one (tests use a stand-in).</summary>
    internal string? ProgramOverride { get; init; }
    /// <summary>Frames per batch instead of the disk-based size (tests).</summary>
    internal int? BatchFramesOverride { get; init; }
    /// <summary>Where batches are worked on (default: the cache folder).</summary>
    internal string? WorkDirectory { get; init; }

    public async Task RunAsync(MediaInfo info, string output, ConversionSettings settings, IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken)
    {
        var method = UpscaleMethods.Get(settings.Algorithm);
        if (method.Kind != UpscaleKind.AiFrames && Converter.EngineFor(settings) != UpscaleKind.AiFrames && ProgramOverride == null)
            throw new ArgumentException($"{method.Label} doesn't run as an AI program.");
        // A Real-ESRGAN ONNX model's Vulkan alternative is the Real-ESRGAN program.
        var component = method.Component ?? method.VulkanAlternative ?? UpscalerComponents.RealEsrgan;
        var program = ProgramOverride ?? UpscalerComponents.ProgramPath(component)
            ?? throw new InvalidOperationException($"{component.Label} isn't installed.");
        var gpu = VulkanDevices.Best;
        if (gpu == null && ProgramOverride == null)
            throw new InvalidOperationException("AI upscaling needs a graphics card with Vulkan support.");
        var color = ColorSpec.For(info);
        if (color.IsHdr)
            throw new NotSupportedException("AI upscaling works on standard (SDR) video; this video is HDR. Use Sharper detail (GPU) or Lanczos.");

        var (srcW, srcH) = settings.Rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
        var target = ScaledSize.For(srcW, srcH, settings.Width, settings.Height, settings.ResizeMode);
        var needed = Math.Max((double)target.Width / srcW, (double)target.Height / srcH);
        var modelScale = ModelScale(settings.Algorithm, needed);

        var rate = settings.FrameRate is > 0 ? F(settings.FrameRate.Value) : info.FrameRateText;
        var fps = settings.FrameRate ?? MediaProbe.ParseRatio(info.FrameRateText);
        var length = Converter.OutputLength(info, settings);
        var totalFrames = Math.Max(1, (int)Math.Ceiling(length * fps - 1e-6));

        var work = WorkDirectory ?? Path.Combine(AppPaths.CacheDirectory, "upscale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var batchFrames = BatchFramesOverride ?? BatchSize(work, srcW, srcH, srcW * modelScale, srcH * modelScale);
        Log.Info("upscale", $"{method.Label}: {info.Path} {srcW}x{srcH} -> {target.Width}x{target.Height} (model x{modelScale}), {totalFrames} frames at {rate}, batches of {batchFrames}, GPU {gpu?.ToString() ?? "(override)"}.");

        using var decoder = StartDecoder(info, settings, rate, color);
        using var encoder = StartEncoder(info, settings, output, rate, color, target);
        var stopwatch = Stopwatch.StartNew();
        var done = 0;
        var tile = 0;
        try
        {
            for (var batch = 0; ; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inDir = Path.Combine(work, $"in{batch}");
                var outDir = Path.Combine(work, $"out{batch}");
                Directory.CreateDirectory(inDir);
                Directory.CreateDirectory(outDir);
                var count = 0;
                while (count < batchFrames && await ReadPngAsync(decoder.StandardOutput.BaseStream, Path.Combine(inDir, $"{done + count + 1:D8}.png"), cancellationToken).ConfigureAwait(false))
                    count++;
                if (count == 0)
                    break;

                tile = await UpscaleBatchAsync(program, inDir, outDir, modelScale, settings.Algorithm, gpu, tile, count, settings.Limits, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < count; i++)
                {
                    var name = $"{done + i + 1:D8}.png";
                    var bytes = await File.ReadAllBytesAsync(Path.Combine(outDir, name), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await encoder.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException) when (encoder.HasExited)
                    {
                        // The encoder stopped early (an error, logged above): say so, not "broken pipe".
                        throw new FfmpegException($"Encoding the upscaled video stopped unexpectedly (exit code {encoder.ExitCode}).", encoder.ExitCode, "");
                    }
                }
                done += count;
                Directory.Delete(inDir, recursive: true);
                Directory.Delete(outDir, recursive: true);
                var position = TimeSpan.FromSeconds(done / fps);
                var speed = stopwatch.Elapsed.TotalSeconds > 0 ? position.TotalSeconds / stopwatch.Elapsed.TotalSeconds : 0;
                progress?.Report(new FfmpegProgress(position, TimeSpan.FromSeconds(length), speed));
                if (count < batchFrames)
                    break;
            }

            encoder.StandardInput.Close();
            await encoder.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await decoder.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (encoder.ExitCode != 0)
                throw new FfmpegException("Encoding the upscaled video failed.", encoder.ExitCode, "");
            if (done == 0)
                throw new InvalidOperationException("No frames were read from the video.");
            Log.Info("upscale", $"Upscaled {done} frames in {stopwatch.Elapsed.TotalSeconds:0.#}s.");
        }
        catch
        {
            Kill(decoder);
            Kill(encoder);
            throw;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The model's scale: the smallest it offers that reaches the size needed (the rest is Lanczos).</summary>
    internal static int ModelScale(ScaleAlgorithm algorithm, double needed)
    {
        int[] scales = algorithm == ScaleAlgorithm.AiPhoto ? [4] : [2, 3, 4];
        return scales.FirstOrDefault(s => s >= needed - 0.01, scales[^1]);
    }

    /// <summary>Frames per batch: about 1.5 GB of PNGs (or less if the disk is short), 24 to 600.</summary>
    internal static int BatchSize(string work, int inW, int inH, int outW, int outH)
    {
        var perFrame = (inW * (long)inH + outW * (long)outH) * 3 / 2; // PNG of video frames: ~half of raw RGB
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work))!).AvailableFreeSpace;
        var budget = Math.Min(1_500_000_000L, (long)(free * 0.5));
        if (budget < perFrame * 24)
            throw new IOException($"Not enough free disk space for AI upscaling: about {perFrame * 24 / 1_000_000 + 1} MB is needed for working files, " +
                                  $"and {free / 1_000_000} MB is free on the drive holding {work}.");
        return (int)Math.Clamp(budget / perFrame, 24, 600);
    }

    private Process StartDecoder(MediaInfo info, ConversionSettings s, string rate, ColorSpec color)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (s.TrimStart is > 0)
            args.AddRange(["-ss", F(s.TrimStart.Value)]);
        if (s.TrimEnd is > 0)
            args.AddRange(["-t", F(Math.Max(0.01, s.TrimEnd.Value - (s.TrimStart ?? 0)))]);
        args.AddRange(["-i", info.Path, "-map", "0:v:0"]);
        // A still preview is exactly one frame: never upscale more than is shown.
        if (s.Format.IsStill)
            args.AddRange(["-frames:v", "1"]);
        var filters = new List<string>();
        if (s.Deinterlace)
            filters.Add("yadif");
        filters.Add($"fps={rate}");
        filters.AddRange(Converter.RotationFilters(s.Rotate));
        filters.Add($"scale=in_color_matrix={color.SwsMatrix}:in_range={color.Range}:flags=accurate_rnd+full_chroma_int,format=rgb24");
        args.AddRange(["-vf", string.Join(',', filters), "-f", "image2pipe", "-c:v", "png", "-compression_level", "1", "pipe:1"]);
        return UpscaleEncoder.Launch(_locator, args, redirectInput: false, s.Limits);
    }

    private Process StartEncoder(MediaInfo info, ConversionSettings s, string output, string rate, ColorSpec color, ScaledSize target) =>
        UpscaleEncoder.Launch(_locator, UpscaleEncoder.Arguments(_locator, info, s, color, target, rate,
            ["-f", "image2pipe", "-framerate", rate, "-c:v", "png", "-i", "pipe:0"], framesAreRgb: true, output), redirectInput: true, s.Limits);

    /// <summary>Runs the AI program on a batch, retrying with smaller tiles if it fails (usually: out of GPU memory). Returns the tile size that worked.</summary>
    private async Task<int> UpscaleBatchAsync(string program, string inDir, string outDir, int scale, ScaleAlgorithm algorithm, GpuDevice? gpu,
        int tile, int expected, ResourceLimits limits, CancellationToken cancellationToken)
    {
        var errors = "";
        foreach (var size in TileSizes.SkipWhile(t => tile != 0 && t != tile))
        {
            var args = new List<string> { "-i", inDir, "-o", outDir, "-s", scale.ToString(), "-f", "png", "-t", size.ToString() };
            if (gpu != null)
                args.AddRange(["-g", gpu.Index.ToString()]);
            args.AddRange(algorithm switch
            {
                ScaleAlgorithm.AiAnimation => ["-n", "realesr-animevideov3"],
                ScaleAlgorithm.AiPhoto => ["-n", "realesrgan-x4plus"],
                _ => ["-n", "-1"], // Real-CUGAN: conservative (keeps the most detail)
            });
            var psi = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.WorkingDirectory = Path.GetDirectoryName(program)!; // the programs find their models relative to the working folder
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            limits.Apply(p);
            var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
            _ = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
            try
            {
                await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(p);
                throw;
            }
            var produced = Directory.EnumerateFiles(outDir, "*.png").Count();
            if (p.ExitCode == 0 && produced == expected)
                return size;
            errors = (await stderr.ConfigureAwait(false)).Trim();
            Log.Warn("upscale", $"{Path.GetFileName(program)} with tile size {size} failed (exit {p.ExitCode}, {produced}/{expected} frames): {Tail(errors)}");
            foreach (var f in Directory.EnumerateFiles(outDir))
                File.Delete(f);
        }
        throw new InvalidOperationException("The AI upscaler failed, even with the smallest tiles. The graphics card may not have enough memory, or its driver may not support Vulkan properly. " + Tail(errors));
    }

    /// <summary>Reads one PNG from a stream of concatenated PNGs (signature, then chunks up to IEND).</summary>
    internal static async Task<bool> ReadPngAsync(Stream stream, string file, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return false;
        await using var target = File.Create(file);
        await target.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        var chunkHeader = new byte[8];
        while (true)
        {
            if (!await ReadExactAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The decoder's PNG stream ended mid-image.");
            await target.WriteAsync(chunkHeader, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader);
            var body = new byte[length + 4]; // data + CRC
            if (!await ReadExactAsync(stream, body, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The decoder's PNG stream ended mid-image.");
            await target.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            if (chunkHeader[4] == 'I' && chunkHeader[5] == 'E' && chunkHeader[6] == 'N' && chunkHeader[7] == 'D')
                return true;
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return total == 0 ? false : throw new InvalidDataException("The decoder's PNG stream ended mid-image.");
            total += n;
        }
        return true;
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited)
                p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }

    private static string Tail(string text) => text.Length > 400 ? "…" + text[^400..] : text;
}
