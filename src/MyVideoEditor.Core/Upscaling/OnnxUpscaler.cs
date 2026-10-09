using System.Collections.Concurrent;
using MyVideoEditor.Core.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace MyVideoEditor.Core.Upscaling;

public enum OnnxChannels
{
    /// <summary>Brightness only (Y of YUV): colour is enlarged separately, as the eye barely sees its detail.</summary>
    Luma,
    /// <summary>Red, green and blue.</summary>
    Rgb,
}

/// <summary>
/// An upscaling model run with ONNX Runtime, shipped in the app's "models" folder. Its input's sides
/// must be a multiple of <paramref name="Multiple"/> and at least <paramref name="MinimumSide"/>.
/// </summary>
public sealed record OnnxModel(string Id, string File, OnnxChannels Channels, int Scale, int TileSize, int Multiple = 1, int MinimumSide = 1)
{
    public static readonly OnnxModel ArtCnn = new("artcnn-c4f32", "ArtCNN_C4F32.onnx", OnnxChannels.Luma, 2, 1024);
    public static readonly OnnxModel RealEsrganAnimation = new("realesr-animevideov3", "realesr-animevideov3.onnx", OnnxChannels.Rgb, 4, 256);
    public static readonly OnnxModel RealEsrganGeneral = new("realesr-general-x4v3", "realesr-general-x4v3.onnx", OnnxChannels.Rgb, 4, 256);
    /// <summary>x4plus is large (RRDBNet): smaller tiles keep graphics-card memory use modest.</summary>
    public static readonly OnnxModel RealEsrganX4Plus = new("realesrgan-x4plus", "realesrgan-x4plus.onnx", OnnxChannels.Rgb, 4, 128);
    /// <summary>Real-CUGAN (conservative), a model per scale. It pads its input by reflection (hence the minimum side).</summary>
    public static readonly OnnxModel RealCugan2x = new("realcugan-x2", "realcugan-x2.onnx", OnnxChannels.Rgb, 2, 256, 2, 40);
    public static readonly OnnxModel RealCugan3x = new("realcugan-x3", "realcugan-x3.onnx", OnnxChannels.Rgb, 3, 256, 4, 40);
    public static readonly OnnxModel RealCugan4x = new("realcugan-x4", "realcugan-x4.onnx", OnnxChannels.Rgb, 4, 256, 2, 40);

    public string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "models", File);
    public bool IsPresent => System.IO.File.Exists(Path);
}

/// <summary>
/// Creates ONNX Runtime sessions: on Windows on the graphics card through DirectML when it can,
/// otherwise on the CPU (all cores). MYVIDEOEDITOR_ONNX_DEVICE=cpu forces the CPU; =gpu requires
/// DirectML. Sessions are cached, as loading a model takes a moment.
/// </summary>
public static class OnnxEngine
{
    public const string DeviceEnvironmentVariable = "MYVIDEOEDITOR_ONNX_DEVICE";

    private static readonly ConcurrentDictionary<string, Lazy<(InferenceSession Session, string Device)>> Sessions = new();

    /// <summary>"graphics card (DirectML)" or "CPU": where a model runs on this computer.</summary>
    public static string DeviceFor(OnnxModel model, Conversion.ResourceLimits? limits = null) => Get(model, limits ?? Conversion.ResourceLimits.Unlimited).Device;

    internal static InferenceSession SessionFor(OnnxModel model, Conversion.ResourceLimits limits) => Get(model, limits).Session;

    /// <summary>
    /// A session per model, thread limit, device and priority: worker threads are created with the
    /// session, so a low-priority session must be created on a low-priority thread (see OnnxUpscaleJob).
    /// </summary>
    private static (InferenceSession Session, string Device) Get(OnnxModel model, Conversion.ResourceLimits limits) =>
        Sessions.GetOrAdd($"{model.Id}|{limits.Threads}|{limits.Device}|{limits.LowPriority}",
            _ => new Lazy<(InferenceSession, string)>(() => Create(model, limits), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static (InferenceSession, string) Create(OnnxModel model, Conversion.ResourceLimits limits)
    {
        if (!model.IsPresent)
            throw new FileNotFoundException($"The {model.File} model is missing from the app's models folder.", model.Path);
        var preference = Environment.GetEnvironmentVariable(DeviceEnvironmentVariable)?.ToLowerInvariant()
                         ?? (limits.Device == Conversion.AiDevice.Cpu ? "cpu" : limits.Device == Conversion.AiDevice.Gpu ? "gpu" : null);
        if (OperatingSystem.IsWindows() && preference != "cpu")
        {
            try
            {
                var gpu = new SessionOptions
                {
                    // DirectML's requirements: no memory pattern, sequential execution.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                gpu.AppendExecutionProvider_DML(0); // the default graphics adapter
                var session = new InferenceSession(model.Path, gpu);
                Log.Info("onnx", $"{model.Id}: running on the graphics card (DirectML).");
                return (session, "graphics card (DirectML)");
            }
            catch (Exception ex)
            {
                if (preference == "gpu")
                    throw;
                Log.Warn("onnx", $"{model.Id}: DirectML isn't available; using the CPU.", ex);
            }
        }
        var cpu = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = limits.Threads,
            InterOpNumThreads = 1,
        };
        Log.Info("onnx", $"{model.Id}: running on the CPU ({limits.Threads} threads{(limits.LowPriority ? ", low priority" : "")}).");
        return (new InferenceSession(model.Path, cpu), "CPU");
    }
}

/// <summary>
/// Upscales single frames with an <see cref="OnnxModel"/>. Frames are processed in overlapping
/// tiles (so memory stays bounded at any size); each tile's border is discarded so the seams
/// don't show.
/// </summary>
public sealed class OnnxUpscaler
{
    private const int Overlap = 16;
    private const int SmallestTile = 32;

    private readonly OnnxModel _model;
    private readonly Conversion.ResourceLimits _limits;
    private InferenceSession _session;
    private string _device;
    private int _tile;
    private readonly string _inputName;
    private readonly string _outputName;

    private readonly ParallelOptions _parallel;

    public OnnxUpscaler(OnnxModel model, Conversion.ResourceLimits? limits = null)
    {
        limits ??= Conversion.ResourceLimits.Unlimited;
        _model = model;
        _limits = limits;
        _session = OnnxEngine.SessionFor(model, limits);
        _device = OnnxEngine.DeviceFor(model, limits);
        _tile = model.TileSize;
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = limits.Threads };
        _inputName = _session.InputNames[0];
        _outputName = _session.OutputNames[0];
    }

    public OnnxModel Model => _model;

    /// <summary>Where it's running now (it can move to the CPU if the graphics card runs out of memory).</summary>
    public string Device => _device;

    /// <summary>
    /// Runs the model, recovering from failures, which are almost always the graphics card running
    /// out of memory: smaller tiles first, then the CPU.
    /// </summary>
    private void RunWithFallback(byte[] planes, int channels, int width, int height, byte[] output, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                RunPlanes(planes, channels, width, height, output, cancellationToken);
                return;
            }
            catch (OnnxRuntimeException ex)
            {
                if (_tile > SmallestTile)
                {
                    _tile = Math.Max(SmallestTile, _tile / 2);
                    Log.Warn("onnx", $"{_model.Id} failed on the {_device}; retrying with {_tile}-pixel tiles.", ex);
                }
                else if (_device != "CPU")
                {
                    Log.Warn("onnx", $"{_model.Id} failed on the {_device} even with small tiles; continuing on the CPU.", ex);
                    var cpu = _limits with { Device = Conversion.AiDevice.Cpu };
                    _session = OnnxEngine.SessionFor(_model, cpu);
                    _device = "CPU";
                    _tile = _model.TileSize;
                }
                else
                {
                    throw new InvalidOperationException($"The {_model.Id} model failed: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>
    /// Luma model: <paramref name="yuv444"/> is planar Y, U, V (8-bit, w×h each). Returns planar YUV at
    /// (scale·w)×(scale·h): Y from the model, U and V enlarged smoothly.
    /// RGB model: <paramref name="rgb"/> is packed RGB (8-bit). Returns packed RGB at the larger size.
    /// </summary>
    public byte[] Upscale(byte[] frame, int width, int height, CancellationToken cancellationToken = default)
    {
        var s = _model.Scale;
        if (_model.Channels == OnnxChannels.Luma)
        {
            var output = new byte[width * s * height * s * 3];
            var plane = width * height;
            var outPlane = plane * s * s;
            RunWithFallback(frame.AsSpan(0, plane).ToArray(), 1, width, height, output, cancellationToken);
            UpsampleChroma(frame.AsSpan(plane, plane), width, height, s, output.AsSpan(outPlane, outPlane), _parallel);
            UpsampleChroma(frame.AsSpan(plane * 2, plane), width, height, s, output.AsSpan(outPlane * 2, outPlane), _parallel);
            return output;
        }
        else
        {
            // Packed RGB to planar R, G, B.
            var plane = width * height;
            var planar = new byte[plane * 3];
            Parallel.For(0, height, _parallel, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 3;
                    planar[y * width + x] = frame[i];
                    planar[plane + y * width + x] = frame[i + 1];
                    planar[plane * 2 + y * width + x] = frame[i + 2];
                }
            });
            var outPlanar = new byte[plane * s * s * 3];
            RunWithFallback(planar, 3, width, height, outPlanar, cancellationToken);
            var ow = width * s;
            var oh = height * s;
            var outPlane = ow * oh;
            var packed = new byte[outPlane * 3];
            Parallel.For(0, oh, _parallel, y =>
            {
                for (var x = 0; x < ow; x++)
                {
                    var i = y * ow + x;
                    packed[i * 3] = outPlanar[i];
                    packed[i * 3 + 1] = outPlanar[outPlane + i];
                    packed[i * 3 + 2] = outPlanar[outPlane * 2 + i];
                }
            });
            return packed;
        }
    }

    /// <summary>Runs the model over planar 8-bit channels, tile by tile, writing the first <paramref name="channels"/> planes of the output.</summary>
    private void RunPlanes(byte[] planes, int channels, int width, int height, byte[] output, CancellationToken cancellationToken)
    {
        var s = _model.Scale;
        var tile = _tile;
        var ow = width * s;
        var outPlane = ow * height * s;
        for (var ty = 0; ty < height; ty += tile)
        {
            for (var tx = 0; tx < width; tx += tile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The tile plus a margin (clamped to the frame), so the kept part has full context,
                // sized as the model needs (beyond the frame's edge, its edge pixels are repeated).
                var (x0, tw) = Span(tx, tile, width);
                var (y0, th) = Span(ty, tile, height);
                var input = new float[channels * tw * th];
                for (var c = 0; c < channels; c++)
                    for (var y = 0; y < th; y++)
                    {
                        var src = c * width * height + Math.Min(y0 + y, height - 1) * width;
                        var dst = (c * th + y) * tw;
                        for (var x = 0; x < tw; x++)
                            input[dst + x] = planes[src + Math.Min(x0 + x, width - 1)] * (1f / 255f);
                    }

                using var inputValue = OrtValue.CreateTensorValueFromMemory(input, [1, channels, th, tw]);
                using var results = _session.Run(new RunOptions(), [_inputName], [inputValue], [_outputName]);
                var result = results[0].GetTensorDataAsSpan<float>();

                // Keep only the tile itself (scaled), not the margin.
                var keepW = Math.Min(tile, width - tx);
                var keepH = Math.Min(tile, height - ty);
                var rw = tw * s;
                var rh = th * s;
                for (var c = 0; c < channels; c++)
                    for (var y = 0; y < keepH * s; y++)
                    {
                        var srcRow = (c * rh + (ty - y0) * s + y) * rw + (tx - x0) * s;
                        var dstRow = c * outPlane + (ty * s + y) * ow + tx * s;
                        for (var x = 0; x < keepW * s; x++)
                        {
                            var v = result[srcRow + x] * 255f + 0.5f;
                            output[dstRow + x] = v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
                        }
                    }
            }
        }
    }

    /// <summary>
    /// Where a tile's input starts along one side, and its length: the tile plus the overlap either
    /// side, rounded up to the model's multiple and minimum (moving back from the frame's far edge
    /// where it can; past the frame only when the frame itself is too small).
    /// </summary>
    internal (int Start, int Length) Span(int tileStart, int tile, int size)
    {
        var start = Math.Max(0, tileStart - Overlap);
        var end = Math.Min(size, tileStart + tile + Overlap);
        var multiple = _model.Multiple;
        var length = Math.Max(end - start, _model.MinimumSide);
        length = (length + multiple - 1) / multiple * multiple;
        start = Math.Max(0, Math.Min(start, size - length));
        return (start, length);
    }

    /// <summary>Bilinear enlargement of a colour plane (centre-aligned, as video scalers do).</summary>
    internal static void UpsampleChroma(ReadOnlySpan<byte> src, int width, int height, int scale, Span<byte> dst, ParallelOptions? parallel = null)
    {
        parallel ??= new ParallelOptions();
        var ow = width * scale;
        var oh = height * scale;
        var source = src.ToArray();
        var target = new byte[ow * oh];
        Parallel.For(0, oh, parallel, y =>
        {
            var fy = Math.Clamp((y + 0.5f) / scale - 0.5f, 0, height - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(height - 1, y0 + 1);
            var wy = fy - y0;
            for (var x = 0; x < ow; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / scale - 0.5f, 0, width - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(width - 1, x0 + 1);
                var wx = fx - x0;
                var top = source[y0 * width + x0] * (1 - wx) + source[y0 * width + x1] * wx;
                var bottom = source[y1 * width + x0] * (1 - wx) + source[y1 * width + x1] * wx;
                target[y * ow + x] = (byte)(top * (1 - wy) + bottom * wy + 0.5f);
            }
        });
        target.CopyTo(dst);
    }
}
