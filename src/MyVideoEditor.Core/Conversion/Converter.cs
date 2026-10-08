using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Upscaling;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Conversion;

public enum ResizeMode
{
    /// <summary>Keep the aspect ratio and add black bars to reach the exact size.</summary>
    Fit,
    /// <summary>Keep the aspect ratio and crop the edges to fill the exact size.</summary>
    Fill,
    /// <summary>Stretch to the exact size.</summary>
    Stretch,
}

/// <summary>How to resize; see <see cref="UpscaleMethods"/> for what each is good for and needs.</summary>
public enum ScaleAlgorithm
{
    /// <summary>Sharp, general purpose (default).</summary>
    Lanczos,
    /// <summary>Softer, fewer ringing artefacts.</summary>
    Bicubic,
    /// <summary>Spline: between the two.</summary>
    Spline,
    /// <summary>Lanczos plus sharpening and light noise reduction.</summary>
    Enhanced,
    /// <summary>FSRCNNX shader on the GPU (libplacebo): camera footage.</summary>
    GpuDetail,
    /// <summary>Anime4K shaders on the GPU (libplacebo): animation and screen recordings.</summary>
    GpuLines,
    /// <summary>Real-ESRGAN animevideov3: animation and screen recordings.</summary>
    AiAnimation,
    /// <summary>Real-CUGAN: anime.</summary>
    AiAnime,
    /// <summary>Real-ESRGAN x4plus: camera footage, very slow.</summary>
    AiPhoto,
}

public sealed record ConversionSettings
{
    public required OutputFormat Format { get; init; }
    public Quality Quality { get; init; } = Quality.Standard;
    /// <summary>Copy the streams without re-encoding (container change, or trimming at keyframes). Very fast and lossless.</summary>
    public bool Remux { get; init; }
    /// <summary>New size; set one of them to keep the aspect ratio. Null = unchanged.</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
    public ResizeMode ResizeMode { get; init; } = ResizeMode.Fit;
    public ScaleAlgorithm Algorithm { get; init; } = ScaleAlgorithm.Lanczos;
    public double? FrameRate { get; init; }
    public double? TrimStart { get; init; }
    public double? TrimEnd { get; init; }
    public bool RemoveAudio { get; init; }
    /// <summary>Clockwise rotation: 0, 90, 180 or 270.</summary>
    public int Rotate { get; init; }
    public bool Deinterlace { get; init; }
    public double Volume { get; init; } = 1.0;
}

/// <summary>Converts single files between formats, sizes and frame rates (no timeline needed).</summary>
public sealed class Converter
{
    private readonly FfmpegLocator _locator;
    private readonly FfmpegRunner _runner;

    public Converter(FfmpegLocator? locator = null)
    {
        _locator = locator ?? FfmpegLocator.Default;
        _runner = new FfmpegRunner(_locator);
    }

    public async Task ConvertAsync(string input, string output, ConversionSettings settings,
        IProgress<FfmpegProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var info = await MediaProbe.Default.ProbeAsync(input, cancellationToken).ConfigureAwait(false);
        if (Path.GetFullPath(input) == Path.GetFullPath(output))
            throw new ArgumentException("Choose a different file to save to: converting a file onto itself would destroy it.");

        settings = Effective(info, settings);
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var partial = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".partial" + Path.GetExtension(full));
        var length = OutputLength(info, settings);
        Log.Info("convert", $"Converting {info.Path} to {full} ({settings.Format.Label}, {settings.Algorithm}).");
        try
        {
            if (UpscaleMethods.Get(settings.Algorithm).Kind == UpscaleKind.AiFrames)
            {
                await AiJob.RunAsync(info, partial, settings, progress, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var args = BuildArguments(info, partial, settings, _locator.Capabilities);
                await _runner.RunAsync(args, length > 0 ? TimeSpan.FromSeconds(length) : null, progress, cancellationToken).ConfigureAwait(false);
            }
            File.Move(partial, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    /// <summary>The AI pipeline to use (tests swap in a stand-in program).</summary>
    internal AiUpscaleJob AiJob { get; init; } = new();

    /// <summary>
    /// GPU and AI methods only make sense when the picture gets bigger and is re-encoded; otherwise
    /// (downscaling, same size, remux, audio only) Lanczos is used.
    /// </summary>
    internal static ConversionSettings Effective(MediaInfo info, ConversionSettings s)
    {
        var method = UpscaleMethods.Get(s.Algorithm);
        if (method.Kind == UpscaleKind.Cpu)
            return s;
        var (srcW, srcH) = s.Rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
        var target = ScaledSize.For(Math.Max(2, srcW), Math.Max(2, srcH), s.Width, s.Height, s.ResizeMode);
        var upscaling = info.HasVideo && target.Width * (long)target.Height > srcW * (long)srcH * 1.05;
        return s.Remux || !s.Format.HasVideo || s.Format.IsGif || !upscaling ? s with { Algorithm = ScaleAlgorithm.Lanczos } : s;
    }

    internal static double OutputLength(MediaInfo info, ConversionSettings s)
    {
        var start = s.TrimStart ?? 0;
        var end = s.TrimEnd is > 0 ? Math.Min(s.TrimEnd.Value, info.Duration > 0 ? info.Duration : double.MaxValue) : info.Duration;
        return Math.Max(0, end - start);
    }

    internal static List<string> BuildArguments(MediaInfo info, string output, ConversionSettings s, FfmpegCapabilities caps)
    {
        var args = new List<string>();
        if (s.TrimStart is > 0)
            args.AddRange(["-ss", F(s.TrimStart.Value)]);
        if (s.TrimEnd is > 0)
            args.AddRange(["-t", F(Math.Max(0.01, s.TrimEnd.Value - (s.TrimStart ?? 0)))]);
        args.AddRange(["-i", info.Path]);

        var wantVideo = s.Format.HasVideo && info.HasVideo;
        var wantAudio = s.Format.HasAudio && info.HasAudio && !s.RemoveAudio;

        if (s.Remux)
        {
            args.AddRange(["-map", "0:v:0?", "-map", "0:a:0?"]);
            if (!wantVideo)
                args.Add("-vn");
            if (!wantAudio)
                args.Add("-an");
            args.AddRange(["-c", "copy", "-y", output]);
            return args;
        }

        var filters = wantVideo ? VideoFilters(info, s) : [];
        if (wantVideo && UpscaleMethods.Get(s.Algorithm).Kind == UpscaleKind.GpuShader)
        {
            // The GPU device has to be set up before the input.
            var gpu = VulkanDevices.Best ?? throw new InvalidOperationException("GPU upscaling needs a graphics card with Vulkan support.");
            args.InsertRange(0, ["-init_hw_device", $"vulkan=gpu:{gpu.Index}", "-filter_hw_device", "gpu"]);
        }
        if (s.Format.IsGif)
        {
            var fps = Math.Min(s.FrameRate ?? info.FrameRate, s.Format.MaxFrameRate ?? 15);
            var chain = string.Join(',', filters.Append($"fps={F(fps)}"));
            args.AddRange(["-filter_complex",
                $"[0:v]{chain},split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[gif]",
                "-map", "[gif]", "-y", output]);
            return args;
        }

        if (wantVideo)
        {
            args.AddRange(["-map", "0:v:0"]);
            if (filters.Count > 0)
                args.AddRange(["-vf", string.Join(',', filters)]);
        }
        else
        {
            args.Add("-vn");
        }
        if (wantAudio)
        {
            args.AddRange(["-map", "0:a:0"]);
            if (Math.Abs(s.Volume - 1) > 0.001)
                args.AddRange(["-af", $"volume={F(s.Volume)}"]);
        }
        else
        {
            args.Add("-an");
        }
        args.AddRange(s.Format.CodecArguments(caps, s.Quality, wantVideo, wantAudio));
        args.AddRange(["-y", output]);
        return args;
    }

    internal static List<string> VideoFilters(MediaInfo info, ConversionSettings s)
    {
        var filters = new List<string>();
        if (s.Deinterlace)
            filters.Add("yadif");
        filters.AddRange(RotationFilters(s.Rotate));
        var method = UpscaleMethods.Get(s.Algorithm);
        if (method.Kind == UpscaleKind.GpuShader)
        {
            filters.AddRange(GpuShaderFilters(info, s, method));
            if (s.FrameRate is > 0)
                filters.Add($"fps={F(s.FrameRate.Value)}");
            filters.Add("setsar=1");
            return filters;
        }
        var flags = s.Algorithm switch
        {
            ScaleAlgorithm.Bicubic => "bicubic",
            ScaleAlgorithm.Spline => "spline",
            _ => "lanczos",
        };
        var scale = ScaleFilter(s.Width, s.Height, s.ResizeMode, flags);
        if (scale != null)
        {
            filters.Add(scale);
            var upscaling = (s.Width ?? 0) > info.Width || (s.Height ?? 0) > info.Height;
            if (s.Algorithm == ScaleAlgorithm.Enhanced && upscaling)
                filters.Add("hqdn3d=1.5:1.5:3:3,unsharp=5:5:0.8:3:3:0.3");
        }
        if (s.FrameRate is > 0)
            filters.Add($"fps={F(s.FrameRate.Value)}");
        filters.Add("setsar=1");
        return filters;
    }

    internal static IEnumerable<string> RotationFilters(int rotate) => (((rotate % 360) + 360) % 360) switch
    {
        90 => ["transpose=1"],
        180 => ["transpose=1", "transpose=1"],
        270 => ["transpose=2"],
        _ => [],
    };

    /// <summary>
    /// Upscaling on the GPU with libplacebo and a shader (FSRCNNX or Anime4K). The frames are
    /// tagged with the source's colours first, and libplacebo told to keep them, so the GPU
    /// path doesn't shift colours on files that don't say what they use.
    /// </summary>
    internal static IEnumerable<string> GpuShaderFilters(MediaInfo info, ConversionSettings s, UpscaleMethod method)
    {
        var shader = UpscalerComponents.ShaderPath(method.Component!)
                     ?? throw new InvalidOperationException($"{method.Component!.Label} isn't installed.");
        var color = ColorSpec.For(info);
        var (srcW, srcH) = s.Rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
        var target = ScaledSize.For(srcW, srcH, s.Width, s.Height, s.ResizeMode);
        yield return color.SetParams;
        yield return $"libplacebo=w={target.Width}:h={target.Height}:upscaler=ewa_lanczossharp:downscaler=mitchell" +
                     $":custom_shader_path={EscapeFilterPath(shader)}:{color.PlaceboOptions}";
        yield return "format=yuv420p";
        if (target.PostFilter != null)
            yield return target.PostFilter;
    }

    /// <summary>
    /// A file path as a filter option value. Backslashes become forward slashes (Windows), then
    /// it's escaped twice: for the option (\ ' :) and for the filter graph (\ ' [ ] , ;).
    /// Quoting can't express an apostrophe ("C:/Users/O'Brien/…"), escaping can.
    /// </summary>
    internal static string EscapeFilterPath(string path)
    {
        static string Escape(string text, string specials) =>
            string.Concat(text.Select(c => specials.Contains(c) ? "\\" + c : c.ToString()));
        return Escape(Escape(path.Replace('\\', '/'), "\\':"), "\\'[],;");
    }

    /// <summary>The scale (and pad/crop) filter for a target size, or null if unchanged.</summary>
    internal static string? ScaleFilter(int? width, int? height, ResizeMode mode, string flags)
    {
        var w = width is > 0 ? width.Value & ~1 : 0;
        var h = height is > 0 ? height.Value & ~1 : 0;
        if (w == 0 && h == 0)
            return null;
        if (w == 0)
            return $"scale=-2:{h}:flags={flags}";
        if (h == 0)
            return $"scale={w}:-2:flags={flags}";
        return mode switch
        {
            ResizeMode.Fill => $"scale={w}:{h}:force_original_aspect_ratio=increase:flags={flags},crop={w}:{h}",
            ResizeMode.Stretch => $"scale={w}:{h}:flags={flags}",
            _ => $"scale={w}:{h}:force_original_aspect_ratio=decrease:flags={flags},pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black",
        };
    }
}
