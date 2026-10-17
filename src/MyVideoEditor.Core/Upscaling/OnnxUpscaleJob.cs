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
///
/// Film repair (<see cref="ConversionSettings.RepairFilm"/>) runs first, on YUV frames, each restored
/// from itself and its neighbours; then the method's model if it has one (RGB models get the repaired
/// frame converted to RGB here), otherwise the encoder alone resizes with the method.
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
        var repair = settings.RepairFilm;
        var model = method.Kind == UpscaleKind.Onnx ? method.OnnxModelFor(needed) : null;
        if (model == null && !repair)
            throw new ArgumentException($"{method.Label} isn't an ONNX method.");
        var color = ColorSpec.For(info);
        if (model?.Channels == OnnxChannels.Rgb && color.IsHdr)
            throw new NotSupportedException($"{method.Label} works on standard (SDR) video; this video is HDR. Use Sharper detail or Lanczos.");
        if (repair && color.IsHdr)
            throw new NotSupportedException("Film repair works on standard (SDR) video; this video is HDR.");

        // A 2x model alone (ArtCNN) runs twice for more than 2x; a model per scale picks the right one
        // (SmallSR lists only itself, so it runs once: two passes look too smooth).
        var passes = model == null ? 0 : method.OnnxScales == null && model.Scale == 2 && needed > 2.05 ? 2 : 1;
        var factor = model == null ? 1 : (int)Math.Pow(model.Scale, passes);
        var (outW, outH) = (srcW * factor, srcH * factor);

        var rate = settings.FrameRate is > 0 ? F(settings.FrameRate.Value) : info.FrameRateText;
        var fps = settings.FrameRate ?? MediaProbe.ParseRatio(info.FrameRateText);
        var length = Converter.OutputLength(info, settings);
        var luma = model == null || model.Channels == OnnxChannels.Luma;
        var pixelFormat = luma ? "yuv444p" : "rgb24";
        var limits = settings.Limits;
        // With repair and a strength mix, the repaired frame goes along below the upscaled one (see UpscaleEncoder).
        var stacked = repair && model != null && method.HasStrength && Math.Clamp(settings.AiStrength, 0, 1) < 0.995
            ? new UpscaleEncoder.StackedFrames(outW, outH, srcW, srcH) : (UpscaleEncoder.StackedFrames?)null;
        var pipedH = stacked == null ? outH : outH + srcH;
        // A still (preview) is restored from its neighbours too: decode one either side, keep the middle.
        var stillFrom = repair && settings.Format.IsStill && settings.TrimStart is > 0 ? Math.Min(settings.TrimStart.Value, 1 / Math.Max(1, fps)) : 0;
        var decodeSettings = repair && settings.Format.IsStill
            ? settings with { TrimStart = settings.TrimStart - stillFrom, TrimEnd = settings.TrimEnd + 1 / Math.Max(1, fps) }
            : settings;
        using var decoder = UpscaleEncoder.Launch(_locator, DecoderArguments(info, decodeSettings, rate, color, repair ? "yuv444p" : pixelFormat, srcW, srcH,
            repair && settings.Format.IsStill ? 3 : 1), redirectInput: false, limits);
        using var encoder = UpscaleEncoder.Launch(_locator, UpscaleEncoder.Arguments(_locator, info, settings, color, target, rate,
            ["-f", "rawvideo", "-pix_fmt", pixelFormat, "-s", $"{outW}x{pipedH}", "-framerate", rate, "-i", "pipe:0"], framesAreRgb: !luma, output, stacked), redirectInput: true, limits);

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
                    var upscaler = model == null ? null : new OnnxUpscaler(model, limits);
                    var restorer = repair ? new OnnxUpscaler(OnnxModel.FilmRepair, limits) : null;
                    if (restorer != null)
                        Log.Info("upscale", $"Film repair on the {OnnxEngine.DeviceFor(OnnxModel.FilmRepair, limits)}: {srcW}x{srcH}.");
                    if (upscaler != null)
                        Log.Info("upscale", $"{method.Label} on the {OnnxEngine.DeviceFor(model!, limits)}: {srcW}x{srcH} -> {outW}x{outH} ({passes} pass(es)) -> {target.Width}x{target.Height}.");

                    byte[] Process(byte[] frame)
                    {
                        ThermalGuard.WaitWhileHot(cancellationToken);
                        var result = frame;
                        int w = srcW, h = srcH;
                        for (var p = 0; p < passes; p++)
                        {
                            result = upscaler!.Upscale(result, w, h, cancellationToken);
                            w *= model!.Scale;
                            h *= model.Scale;
                        }
                        return stacked == null ? result : Stack(result, frame, outW, outH, srcW, srcH, luma ? 3 : 1, luma);
                    }

                    void Emit(byte[] frame) => upscaled.Writer.WriteAsync(frame, cancellationToken).AsTask().GetAwaiter().GetResult();

                    // Repair needs the next frame: a window of three, the first and last frames standing in for their missing neighbours.
                    byte[]? previous = null, current = null;
                    var index = 0;
                    var keep = settings.Format.IsStill ? (stillFrom > 0 ? 1 : 0) : -1;
                    void Restore(byte[] next)
                    {
                        var repaired = restorer!.Restore(previous ?? current!, current!, next, srcW, srcH, cancellationToken);
                        if (!luma)
                            repaired = YuvToRgb(repaired, srcW, srcH, color);
                        if (keep < 0 || index == keep)
                            Emit(Process(repaired));
                        index++;
                    }
                    foreach (var frame in decoded.Reader.ReadAllAsync(cancellationToken).ToBlockingEnumerable(cancellationToken))
                    {
                        if (restorer == null)
                        {
                            Emit(Process(frame));
                            continue;
                        }
                        if (current != null)
                            Restore(frame);
                        (previous, current) = (current, frame);
                    }
                    if (restorer != null && current != null && (keep < 0 || index <= keep))
                    {
                        if (keep >= 0)
                            index = keep; // fewer frames than asked for (end of the video): keep the last
                        Restore(current);
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

    /// <summary>
    /// The upscaled frame (<paramref name="width"/>×<paramref name="height"/>) with the source-size frame
    /// below it, left-aligned, in one frame (planar YUV, or packed RGB when <paramref name="planes"/> is 1).
    /// </summary>
    internal static byte[] Stack(byte[] upscaled, byte[] source, int width, int height, int srcW, int srcH, int planes, bool planar)
    {
        var bytesPerPixel = planar ? 1 : 3;
        var rows = height + srcH;
        var stacked = new byte[width * rows * 3];
        var plane = width * rows;
        for (var c = 0; c < planes; c++)
        {
            var outBase = planar ? c * plane : 0;
            var upBase = planar ? c * width * height : 0;
            var srcBase = planar ? c * srcW * srcH : 0;
            upscaled.AsSpan(upBase, width * height * bytesPerPixel).CopyTo(stacked.AsSpan(outBase));
            for (var y = 0; y < srcH; y++)
                source.AsSpan(srcBase + y * srcW * bytesPerPixel, srcW * bytesPerPixel)
                    .CopyTo(stacked.AsSpan(outBase + (height + y) * width * bytesPerPixel));
        }
        return stacked;
    }

    /// <summary>
    /// Planar YUV 4:4:4 to packed RGB with the source's matrix and range (what the decoder's swscale
    /// would do for an RGB model, done here because repair needs the YUV frame first).
    /// </summary>
    internal static byte[] YuvToRgb(byte[] yuv, int width, int height, ColorSpec color)
    {
        var (kr, kb) = color.SwsMatrix switch
        {
            "bt601" => (0.299, 0.114),
            "bt2020" => (0.2627, 0.0593),
            "smpte240m" => (0.212, 0.087),
            "fcc" => (0.30, 0.11),
            _ => (0.2126, 0.0722),
        };
        var kg = 1 - kr - kb;
        var full = color.Range == "pc";
        var (ys, cs) = full ? (1.0, 1.0) : (255.0 / 219, 255.0 / 224);
        var yOffset = full ? 0 : 16;
        var plane = width * height;
        var rgb = new byte[plane * 3];
        static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var l = (yuv[i] - yOffset) * ys;
                var u = (yuv[plane + i] - 128) * cs;
                var v = (yuv[plane * 2 + i] - 128) * cs;
                var r = l + 2 * (1 - kr) * v;
                var b = l + 2 * (1 - kb) * u;
                var g = (l - kr * r - kb * b) / kg;
                rgb[i * 3] = Clamp(r);
                rgb[i * 3 + 1] = Clamp(g);
                rgb[i * 3 + 2] = Clamp(b);
            }
        });
        return rgb;
    }

    private static List<string> DecoderArguments(MediaInfo info, ConversionSettings s, string rate, ColorSpec color, string pixelFormat, int width, int height, int stillFrames = 1)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (s.TrimStart is > 0)
            args.AddRange(["-ss", F(s.TrimStart.Value)]);
        if (s.TrimEnd is > 0)
            args.AddRange(["-t", F(Math.Max(0.01, s.TrimEnd.Value - (s.TrimStart ?? 0)))]);
        args.AddRange(["-i", info.Path, "-map", "0:v:0"]);
        if (s.Format.IsStill)
            args.AddRange(["-frames:v", stillFrames.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var filters = new List<string>();
        if (s.Deinterlace)
            filters.Add("yadif");
        filters.Add($"fps={rate}");
        filters.AddRange(Converter.RotationFilters(s.Rotate));
        // An explicit size makes non-square pixels (anamorphic DV/DVD) square, matching the frame size the
        // job reads; the stored width would otherwise misalign every row. YUV stays as the source's values
        // (no conversion); RGB uses the source's own matrix.
        filters.Add(pixelFormat == "rgb24"
            ? $"scale={width}:{height}:in_color_matrix={color.SwsMatrix}:in_range={color.Range}:flags=lanczos+accurate_rnd+full_chroma_int,format=rgb24"
            : $"scale={width}:{height}:flags=lanczos+accurate_rnd+full_chroma_int,format=yuv444p");
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
