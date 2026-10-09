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

/// <summary>An upscaling model run with ONNX Runtime, shipped in the app's "models" folder.</summary>
public sealed record OnnxModel(string Id, string File, OnnxChannels Channels, int Scale, int TileSize)
{
    public static readonly OnnxModel ArtCnn = new("artcnn-c4f32", "ArtCNN_C4F32.onnx", OnnxChannels.Luma, 2, 1024);
    public static readonly OnnxModel RealEsrganAnimation = new("realesr-animevideov3", "realesr-animevideov3.onnx", OnnxChannels.Rgb, 4, 256);
    public static readonly OnnxModel RealEsrganGeneral = new("realesr-general-x4v3", "realesr-general-x4v3.onnx", OnnxChannels.Rgb, 4, 256);

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

    private readonly OnnxModel _model;
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    private readonly ParallelOptions _parallel;

    public OnnxUpscaler(OnnxModel model, Conversion.ResourceLimits? limits = null)
    {
        limits ??= Conversion.ResourceLimits.Unlimited;
        _model = model;
        _session = OnnxEngine.SessionFor(model, limits);
        _parallel = new ParallelOptions { MaxDegreeOfParallelism = limits.Threads };
        _inputName = _session.InputNames[0];
        _outputName = _session.OutputNames[0];
    }

    public OnnxModel Model => _model;

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
            RunPlanes(frame.AsSpan(0, plane).ToArray(), 1, width, height, output, cancellationToken);
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
            RunPlanes(planar, 3, width, height, outPlanar, cancellationToken);
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
        var tile = _model.TileSize;
        var ow = width * s;
        var outPlane = ow * height * s;
        for (var ty = 0; ty < height; ty += tile)
        {
            for (var tx = 0; tx < width; tx += tile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The tile plus a margin (clamped to the frame), so the kept part has full context.
                var x0 = Math.Max(0, tx - Overlap);
                var y0 = Math.Max(0, ty - Overlap);
                var x1 = Math.Min(width, tx + tile + Overlap);
                var y1 = Math.Min(height, ty + tile + Overlap);
                var tw = x1 - x0;
                var th = y1 - y0;
                var input = new float[channels * tw * th];
                for (var c = 0; c < channels; c++)
                    for (var y = 0; y < th; y++)
                    {
                        var src = c * width * height + (y0 + y) * width + x0;
                        var dst = (c * th + y) * tw;
                        for (var x = 0; x < tw; x++)
                            input[dst + x] = planes[src + x] * (1f / 255f);
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
