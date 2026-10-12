using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Upscaling;

public enum UpscaleKind
{
    /// <summary>ffmpeg's own scalers on the CPU: fast everywhere.</summary>
    Cpu,
    /// <summary>A libplacebo shader on a Vulkan GPU (integrated graphics are fine): close to real time.</summary>
    GpuShader,
    /// <summary>A neural network run in-process by ONNX Runtime: on the CPU anywhere, or a graphics card through DirectML on Windows.</summary>
    Onnx,
    /// <summary>An AI model run frame by frame on a Vulkan GPU: slow, best on a dedicated graphics card.</summary>
    AiFrames,
}

public enum ContentType
{
    /// <summary>Camera footage: people, places, nature.</summary>
    Camera,
    /// <summary>Animation, cartoons, games and screen recordings: flat colours and lines.</summary>
    Animation,
}

/// <summary>An upscaling method: what it's good for, where it runs, and what it needs.</summary>
public sealed record UpscaleMethod(
    ScaleAlgorithm Algorithm,
    string Label,
    string Description,
    UpscaleKind Kind,
    UpscalerComponent? Component,
    ContentType? BestFor,
    int Quality,
    OnnxModel? OnnxModel = null,
    bool IsAi = false,
    UpscalerComponent? VulkanAlternative = null,
    IReadOnlyList<OnnxModel>? OnnxScales = null,
    bool Mixable = false)
{
    /// <summary>Whether the strength setting applies: the output can be mixed with a Lanczos enlargement.</summary>
    public bool HasStrength => IsAi || Mixable;

    /// <summary>
    /// The ONNX model for enlarging by <paramref name="needed"/>: with a model per scale, the smallest
    /// that reaches it (the rest is Lanczos), otherwise the method's one model.
    /// </summary>
    public OnnxModel? OnnxModelFor(double needed) =>
        OnnxScales is { Count: > 0 } scales ? scales.FirstOrDefault(m => m.Scale >= needed - 0.01) ?? scales[^1] : OnnxModel;

    /// <summary>Whether this computer can run it at all (GPU, platform, ffmpeg features).</summary>
    public string? Unavailable
    {
        get
        {
            if (Kind == UpscaleKind.Cpu)
                return null;
            if (Kind == UpscaleKind.Onnx)
                return (OnnxScales ?? [OnnxModel!]).FirstOrDefault(m => !m.IsPresent) is { } missing
                    ? $"The {missing.File} model is missing from this installation." : null;
            if (Component is { IsSupportedHere: false })
                return $"Not available for {UpscalerComponents.RuntimeId}.";
            if (VulkanDevices.Best == null)
                return "Needs a graphics card with Vulkan support.";
            if (Kind == UpscaleKind.GpuShader && FfmpegLocator.Default.Capabilities.Filters.Count > 0 && !FfmpegLocator.Default.Capabilities.HasFilter("libplacebo"))
                return "This ffmpeg doesn't include libplacebo.";
            return null;
        }
    }

    public bool NeedsDownload => Component != null && !UpscalerComponents.IsInstalled(Component);

    public long DownloadSize => Component?.ForThisComputer?.Size ?? 0;
}

public static class UpscaleMethods
{
    public static IReadOnlyList<UpscaleMethod> All { get; } =
    [
        new(ScaleAlgorithm.Lanczos, "Lanczos", "Sharp, faithful resizing. Fast on any computer.", UpscaleKind.Cpu, null, null, 2),
        new(ScaleAlgorithm.Bicubic, "Bicubic", "Softer resizing, fewer halos around edges.", UpscaleKind.Cpu, null, null, 1),
        new(ScaleAlgorithm.Spline, "Spline", "Between bicubic and Lanczos.", UpscaleKind.Cpu, null, null, 1),
        new(ScaleAlgorithm.Enhanced, "Enhanced", "Lanczos with light noise reduction and sharpening. Fast on any computer.", UpscaleKind.Cpu, null, null, 3),
        new(ScaleAlgorithm.ArtCnn, "Sharper detail (ArtCNN)",
            "ArtCNN, a small neural network trained to recreate real detail rather than invent it: crisper than Lanczos and just as faithful. " +
            "Fast on any computer's CPU (on Windows it can use the graphics card). Good for any footage.",
            UpscaleKind.Onnx, null, null, 5, OnnxModel.ArtCnn),
        new(ScaleAlgorithm.GpuLines, "Cleaner lines (GPU)",
            "Anime4K GPU shaders: restores and sharpens lines at close to real-time speed. Best for animation, cartoons and screen recordings.",
            UpscaleKind.GpuShader, UpscalerComponents.Anime4K, ContentType.Animation, 4),
        // One pass (OnnxScales), then Lanczos: two 2x passes look too smooth.
        new(ScaleAlgorithm.CleanUp, "Clean-up (fast)",
            "SmallSR, a tiny neural network trained to remove compression blocks and blur while doubling the size; a standard resize does the rest. " +
            "Faster than Sharper detail on the CPU; best for low-resolution or heavily compressed footage. Lower the strength for a softer, more natural mix.",
            UpscaleKind.Onnx, null, null, 4, OnnxModel.SmallSr, OnnxScales: [OnnxModel.SmallSr], Mixable: true),
        new(ScaleAlgorithm.AiAnimation, "AI: animation and screen (Real-ESRGAN)",
            "Real-ESRGAN's video model: clean, sharp lines and flat colours. Best for animation, cartoons and screen recordings. " +
            "Runs on the CPU (about a second per frame), or a graphics card on Windows.",
            UpscaleKind.Onnx, null, ContentType.Animation, 6, OnnxModel.RealEsrganAnimation, IsAi: true, VulkanAlternative: UpscalerComponents.RealEsrgan),
        new(ScaleAlgorithm.AiGeneral, "AI: realistic detail (Real-ESRGAN general)",
            "Real-ESRGAN's compact general model: adds crisp, realistic detail to camera footage. Invents some texture; " +
            "lower the strength to keep more of the real picture. Runs on the CPU (a few seconds per frame), or a graphics card on Windows.",
            UpscaleKind.Onnx, null, ContentType.Camera, 6, OnnxModel.RealEsrganGeneral, IsAi: true),
        new(ScaleAlgorithm.AiAnime, "AI: anime, strongest (Real-CUGAN)",
            "Real-CUGAN: the strongest clean-up for anime. Smooths camera footage into a painted look, so use it for animation only. " +
            "Runs on the CPU (a few seconds per frame), or a graphics card on Windows.",
            UpscaleKind.Onnx, null, ContentType.Animation, 7, OnnxModel.RealCugan2x, IsAi: true, VulkanAlternative: UpscalerComponents.RealCugan,
            OnnxScales: [OnnxModel.RealCugan2x, OnnxModel.RealCugan3x, OnnxModel.RealCugan4x]),
        new(ScaleAlgorithm.AiPhoto, "AI: photo detail (Real-ESRGAN)",
            "Real-ESRGAN x4plus, the largest model: invents realistic texture for camera footage. Slow: tens of seconds per frame on a CPU, " +
            "much faster on a dedicated graphics card (Windows).",
            UpscaleKind.Onnx, null, ContentType.Camera, 7, OnnxModel.RealEsrganX4Plus, IsAi: true, VulkanAlternative: UpscalerComponents.RealEsrgan),
    ];

    public static UpscaleMethod Get(ScaleAlgorithm algorithm) => All.First(m => m.Algorithm == algorithm);

    /// <summary>
    /// The best method for the content that this computer can run, finishing within
    /// <paramref name="timeLimit"/> according to <paramref name="estimates"/> (methods without an
    /// estimate count as fast enough only if they're CPU or GPU-shader methods).
    /// </summary>
    public static UpscaleMethod Recommend(ContentType content, IReadOnlyDictionary<ScaleAlgorithm, UpscaleEstimate> estimates, TimeSpan timeLimit)
    {
        bool FastEnough(UpscaleMethod m) => estimates.TryGetValue(m.Algorithm, out var e)
            ? e.Error == null && e.Total <= timeLimit
            : !m.IsAi;
        return All.Where(m => m.Unavailable == null && (m.BestFor == null || m.BestFor == content) && FastEnough(m))
            .OrderByDescending(m => m.Quality)
            .First();
    }
}

/// <summary>Sizes for scaling a source to a target in fit, fill or stretch mode.</summary>
public readonly record struct ScaledSize(int Width, int Height, string? PostFilter)
{
    /// <summary>
    /// Where the source (srcW×srcH, after rotation) must be scaled to so that a pad (fit) or
    /// crop (fill) to the requested size follows. With only one dimension given, the other
    /// keeps the aspect ratio. Null target = unchanged.
    /// </summary>
    public static ScaledSize For(int srcW, int srcH, int? width, int? height, ResizeMode mode)
    {
        var w = width is > 0 ? width.Value & ~1 : 0;
        var h = height is > 0 ? height.Value & ~1 : 0;
        if (w == 0 && h == 0)
            return new(srcW & ~1, srcH & ~1, null);
        if (w == 0)
            return new(Even(srcW * (double)h / srcH), h, null);
        if (h == 0)
            return new(w, Even(srcH * (double)w / srcW), null);
        switch (mode)
        {
            case ResizeMode.Stretch:
                return new(w, h, null);
            case ResizeMode.Fill:
            {
                var s = Math.Max((double)w / srcW, (double)h / srcH);
                return new(Math.Max(w, Even(srcW * s)), Math.Max(h, Even(srcH * s)), $"crop={w}:{h}");
            }
            default:
            {
                var s = Math.Min((double)w / srcW, (double)h / srcH);
                var sw = Math.Min(w, Even(srcW * s));
                var sh = Math.Min(h, Even(srcH * s));
                return new(sw, sh, sw == w && sh == h ? null : $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black");
            }
        }
    }

    private static int Even(double v) => Math.Max(2, (int)Math.Round(v / 2) * 2);
}

/// <summary>
/// A source's colour description, with sensible guesses where the file doesn't say (HD: BT.709,
/// SD: BT.601; limited range). Used so every conversion to and from RGB (GPU shaders, AI
/// upscalers' PNG frames) uses the same matrix in both directions and the output is tagged.
/// </summary>
public sealed record ColorSpec(string Space, string Primaries, string Transfer, string Range)
{
    public static ColorSpec For(MediaInfo info)
    {
        var hd = info.Height >= 720 || info.Width >= 1280;
        return new(
            Nonempty(info.ColorSpace, hd ? "bt709" : "smpte170m"),
            Nonempty(info.ColorPrimaries, hd ? "bt709" : "smpte170m"),
            Nonempty(info.ColorTransfer, hd ? "bt709" : "smpte170m"),
            info.ColorRange == "pc" ? "pc" : "tv");
    }

    /// <summary>High dynamic range (PQ or HLG): AI upscalers work on 8-bit SDR frames and would ruin it.</summary>
    public bool IsHdr => Transfer is "smpte2084" or "arib-std-b67";

    /// <summary>The swscale matrix name ("bt709", "bt601", "bt2020").</summary>
    public string SwsMatrix => Space switch
    {
        "smpte170m" or "bt470bg" => "bt601",
        "bt2020nc" or "bt2020c" => "bt2020",
        "smpte240m" => "smpte240m",
        "fcc" => "fcc",
        _ => "bt709",
    };

    /// <summary>libplacebo options so input and output use exactly these colours.</summary>
    public string PlaceboOptions => $"colorspace={Space}:color_primaries={Primaries}:color_trc={Transfer}:range={Range}";

    /// <summary>Tags the frames (setparams), so filters don't guess differently.</summary>
    public string SetParams => $"setparams=colorspace={Space}:color_primaries={Primaries}:color_trc={Transfer}:range={Range}";

    /// <summary>Encoder options that tag the output file.</summary>
    public IReadOnlyList<string> OutputTags => ["-colorspace", Space, "-color_primaries", Primaries, "-color_trc", Transfer, "-color_range", Range];

    private static string Nonempty(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
}
