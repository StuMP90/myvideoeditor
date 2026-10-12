using System.Diagnostics;
using System.Threading.Channels;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Upscaling;

/// <summary>
/// Upscales a video with an ONNX model, in-process and streaming (no working files):
///
///   ffmpeg (decode, raw frames) ──▶ model (CPU or DirectML) ──▶ ffmpeg (encode, see <see cref="UpscaleEncoder"/>)
///
/// Brightness-only models (ArtCNN, SmallSR) get YUV frames and keep the source's colour values exactly;
/// RGB models (Real-ESRGAN, Real-CUGAN) get RGB converted with the source's own matrix. ArtCNN doubles the
/// size per pass, so it runs twice when more than 2× is needed; SmallSR always runs once. The encoder
/// scales to the exact size. Decoding, upscaling and encoding run at the same time.
/// </summary>
public sealed class OnnxUpscaleJob
{
    private readonly FfmpegLocator _locator;

    public OnnxUpscaleJob(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    public async Task RunAsync(MediaInfo info, string output, ConversionSettings settings, IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken)
    {
        var method = UpscaleMethods.Get(settings.Algorithm);
        var (srcW, srcH) = settings.Rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
        var target = ScaledSize.For(srcW, srcH, settings.Width, settings.Height, settings.ResizeMode);
        var needed = Math.Max((double)target.Width / srcW, (double)target.Height / srcH);
        var model = method.OnnxModelFor(needed) ?? throw new ArgumentException($"{method.Label} isn't an ONNX method.");
        var color = ColorSpec.For(info);
        if (model.Channels == OnnxChannels.Rgb && color.IsHdr)
            throw new NotSupportedException($"{method.Label} works on standard (SDR) video; this video is HDR. Use Sharper detail or Lanczos.");

        // A 2x model alone (ArtCNN) runs twice for more than 2x; a model per scale picks the right one
        // (SmallSR lists only itself, so it runs once: two passes look too smooth).
        var passes = method.OnnxScales == null && model.Scale == 2 && needed > 2.05 ? 2 : 1;
        var factor = (int)Math.Pow(model.Scale, passes);
        var (outW, outH) = (srcW * factor, srcH * factor);

        var rate = settings.FrameRate is > 0 ? F(settings.FrameRate.Value) : info.FrameRateText;
        var fps = settings.FrameRate ?? MediaProbe.ParseRatio(info.FrameRateText);
        var length = Converter.OutputLength(info, settings);
        var luma = model.Channels == OnnxChannels.Luma;
        var pixelFormat = luma ? "yuv444p" : "rgb24";
        var limits = settings.Limits;
        using var decoder = UpscaleEncoder.Launch(_locator, DecoderArguments(info, settings, rate, color, pixelFormat), redirectInput: false, limits);
        using var encoder = UpscaleEncoder.Launch(_locator, UpscaleEncoder.Arguments(_locator, info, settings, color, target, rate,
            ["-f", "rawvideo", "-pix_fmt", pixelFormat, "-s", $"{outW}x{outH}", "-framerate", rate, "-i", "pipe:0"], framesAreRgb: !luma, output), redirectInput: true, limits);

        // Three stages at once: read → upscale → write, a couple of frames in flight between them.
        var decoded = Channel.CreateBounded<byte[]>(2);
        var upscaled = Channel.CreateBounded<byte[]>(2);
        var stopwatch = Stopwatch.StartNew();
        var frameBytes = srcW * srcH * 3;
        try
        {
            var read = Task.Run(async () =>
            {
                try
                {
                    var stream = decoder.StandardOutput.BaseStream;
                    while (true)
                    {
                        var frame = new byte[frameBytes];
                        if (!await ReadExactAsync(stream, frame, cancellationToken).ConfigureAwait(false))
                            break;
                        await decoded.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    decoded.Writer.TryComplete();
                }
            }, cancellationToken);

            // Inference runs on a thread of its own: at low priority it's lowered first, and the ONNX session
            // (with its worker threads) is created on it, so those threads are low priority too (Linux).
            var infer = RunOnDedicatedThread(() =>
            {
                try
                {
                    limits.LowerDedicatedThread();
                    var upscaler = new OnnxUpscaler(model, limits);
                    Log.Info("upscale", $"{method.Label} on the {OnnxEngine.DeviceFor(model, limits)}: {srcW}x{srcH} -> {outW}x{outH} ({passes} pass(es)) -> {target.Width}x{target.Height}.");
                    foreach (var frame in decoded.Reader.ReadAllAsync(cancellationToken).ToBlockingEnumerable(cancellationToken))
                    {
                        var result = frame;
                        int w = srcW, h = srcH;
                        for (var p = 0; p < passes; p++)
                        {
                            result = upscaler.Upscale(result, w, h, cancellationToken);
                            w *= model.Scale;
                            h *= model.Scale;
                        }
                        upscaled.Writer.WriteAsync(result, cancellationToken).AsTask().GetAwaiter().GetResult();
                    }
                }
                finally
                {
                    upscaled.Writer.TryComplete();
                }
            });

            var done = 0;
            await foreach (var frame in upscaled.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await encoder.StandardInput.BaseStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException) when (encoder.HasExited)
                {
                    throw new FfmpegException($"Encoding the upscaled video stopped unexpectedly (exit code {encoder.ExitCode}).", encoder.ExitCode, "");
                }
                done++;
                var position = TimeSpan.FromSeconds(done / fps);
                progress?.Report(new FfmpegProgress(position, TimeSpan.FromSeconds(length),
                    stopwatch.Elapsed.TotalSeconds > 0 ? position.TotalSeconds / stopwatch.Elapsed.TotalSeconds : 0));
            }
            await Task.WhenAll(read, infer).ConfigureAwait(false);

            encoder.StandardInput.Close();
            await encoder.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await decoder.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (encoder.ExitCode != 0)
                throw new FfmpegException("Encoding the upscaled video failed.", encoder.ExitCode, "");
            if (done == 0)
                throw new InvalidOperationException("No frames were read from the video.");
            Log.Info("upscale", $"Upscaled {done} frames in {stopwatch.Elapsed.TotalSeconds:0.#}s ({stopwatch.Elapsed.TotalSeconds / done:0.###} s/frame).");
        }
        catch
        {
            Kill(decoder);
            Kill(encoder);
            throw;
        }
    }

    private static Task RunOnDedicatedThread(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                work();
                done.SetResult();
            }
            catch (OperationCanceledException ex)
            {
                done.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }) { IsBackground = true, Name = "onnx-upscale" };
        thread.Start();
        return done.Task;
    }

    private static List<string> DecoderArguments(MediaInfo info, ConversionSettings s, string rate, ColorSpec color, string pixelFormat)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (s.TrimStart is > 0)
            args.AddRange(["-ss", F(s.TrimStart.Value)]);
        if (s.TrimEnd is > 0)
            args.AddRange(["-t", F(Math.Max(0.01, s.TrimEnd.Value - (s.TrimStart ?? 0)))]);
        args.AddRange(["-i", info.Path, "-map", "0:v:0"]);
        if (s.Format.IsStill)
            args.AddRange(["-frames:v", "1"]);
        var filters = new List<string>();
        if (s.Deinterlace)
            filters.Add("yadif");
        filters.Add($"fps={rate}");
        filters.AddRange(Converter.RotationFilters(s.Rotate));
        // YUV stays as the source's values (no conversion); RGB uses the source's own matrix.
        filters.Add(pixelFormat == "rgb24"
            ? $"scale=in_color_matrix={color.SwsMatrix}:in_range={color.Range}:flags=accurate_rnd+full_chroma_int,format=rgb24"
            : "scale=flags=accurate_rnd+full_chroma_int,format=yuv444p");
        args.AddRange(["-vf", string.Join(',', filters), "-f", "rawvideo", "pipe:1"]);
        return args;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return false;
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
}
