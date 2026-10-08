using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
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

public enum ScaleAlgorithm
{
    /// <summary>Sharp, general purpose (default).</summary>
    Lanczos,
    /// <summary>Softer, fewer ringing artefacts.</summary>
    Bicubic,
    /// <summary>Spline: between the two.</summary>
    Spline,
    /// <summary>Lanczos plus sharpening and light noise reduction: the best non-AI upscale.</summary>
    Enhanced,
    /// <summary>Real-ESRGAN (machine-learning super-resolution), if installed. Much slower; needs a Vulkan GPU.</summary>
    AiSuperResolution,
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

        if (settings.Algorithm == ScaleAlgorithm.AiSuperResolution && settings.Format.HasVideo && info.HasVideo && !settings.Remux)
        {
            await new AiUpscaler(_locator).UpscaleAsync(info, output, settings, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var partial = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".partial" + Path.GetExtension(full));
        var args = BuildArguments(info, partial, settings, _locator.Capabilities);
        var length = OutputLength(info, settings);
        Log.Info("convert", $"Converting {info.Path} to {full} ({settings.Format.Label}).");
        try
        {
            await _runner.RunAsync(args, length > 0 ? TimeSpan.FromSeconds(length) : null, progress, cancellationToken).ConfigureAwait(false);
            File.Move(partial, full, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
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
        switch (((s.Rotate % 360) + 360) % 360)
        {
            case 90: filters.Add("transpose=1"); break;
            case 180: filters.Add("transpose=1,transpose=1"); break;
            case 270: filters.Add("transpose=2"); break;
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
