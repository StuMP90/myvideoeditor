using System.Diagnostics;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Upscaling;

/// <summary>
/// The ffmpeg that turns upscaled frames (piped in) into the output: scaled to the exact size,
/// padded or cropped, with the source's sound, and for AI methods and Clean-up optionally mixed
/// with a faithful Lanczos enlargement of the same frame ("Strength"), which keeps real texture that
/// the model would otherwise paint over. Frames are paired by number, so they can't drift apart.
/// With film repair, the faithful layer must be the repaired frame (the source would bring the
/// scratches back), so it travels in the same piped frame, below the upscaled one (<see cref="StackedFrames"/>).
/// </summary>
internal static class UpscaleEncoder
{
    /// <summary>
    /// Piped frames that are <paramref name="Width"/>×(<paramref name="Height"/> + <paramref name="SourceHeight"/>):
    /// the upscaled frame on top, the repaired source-size frame below it on the left.
    /// </summary>
    public readonly record struct StackedFrames(int Width, int Height, int SourceWidth, int SourceHeight);

    /// <summary>Arguments for the encoder; <paramref name="frameInput"/> describes the piped frames (input 0).</summary>
    public static List<string> Arguments(FfmpegLocator locator, MediaInfo info, ConversionSettings s, ColorSpec color, ScaledSize target, string rate,
        IReadOnlyList<string> frameInput, bool framesAreRgb, string output, StackedFrames? stacked = null)
    {
        var withAudio = info.HasAudio && !s.RemoveAudio && s.Format.HasAudio;
        var strength = Math.Clamp(s.AiStrength, 0, 1);
        var blend = UpscaleMethods.Get(s.Algorithm).HasStrength && strength < 0.995;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        args.AddRange(frameInput);
        if (withAudio || (blend && stacked == null))
        {
            // The source again: its sound, and (when blending) the frames for the faithful layer.
            if (s.TrimStart is > 0)
                args.AddRange(["-ss", F(s.TrimStart.Value)]);
            args.AddRange(["-t", F(Converter.OutputLength(info, s)), "-i", info.Path]);
        }

        var index = $"setpts=N/({rate})/TB";
        // Only film repair alone pipes frames for a standard method (the rest are ONNX: Lanczos).
        var flags = s.Algorithm switch
        {
            ScaleAlgorithm.Bicubic => "bicubic",
            ScaleAlgorithm.Spline => "spline",
            _ => "lanczos",
        };
        var scale = $"scale={target.Width}:{target.Height}:flags={flags}+accurate_rnd+full_chroma_int";
        var toYuv = framesAreRgb ? $"{scale}:out_color_matrix={color.SwsMatrix}:out_range={color.Range}" : scale;
        if (s.Algorithm == ScaleAlgorithm.Enhanced && target.Width * (long)target.Height > info.Width * (long)info.Height)
            toYuv += ",hqdn3d=1.5:1.5:3:3,unsharp=5:5:0.8:3:3:0.3";
        var graph = new List<string>();
        var last = "up";
        if (blend && stacked is { } st)
        {
            graph.Add($"[0:v]split[top][bottom]");
            graph.Add($"[top]crop={st.Width}:{st.Height}:0:0,{toYuv},format=yuv444p,{index}[up]");
            graph.Add($"[bottom]crop={st.SourceWidth}:{st.SourceHeight}:0:{st.Height},{toYuv},format=yuv444p,{index}[base]");
            graph.Add($"[up][base]blend=all_expr='A*{F(strength)}+B*{F(1 - strength)}'[mixed]");
            last = "mixed";
        }
        else if (blend)
        {
            graph.Add($"[0:v]{toYuv},format=yuv444p,{index}[up]");
            var source = new List<string>();
            if (s.Deinterlace)
                source.Add("yadif");
            source.Add($"fps={rate}");
            source.AddRange(Converter.RotationFilters(s.Rotate));
            source.AddRange([scale, "format=yuv444p", index]);
            graph.Add($"[1:v]{string.Join(',', source)}[base]");
            graph.Add($"[up][base]blend=all_expr='A*{F(strength)}+B*{F(1 - strength)}'[mixed]");
            last = "mixed";
        }
        else
        {
            graph.Add($"[0:v]{toYuv},format=yuv444p,{index}[up]");
        }
        var tail = new List<string>();
        if (target.PostFilter != null)
            tail.Add(target.PostFilter);
        tail.AddRange(Looks.Filters(s.Look, target.Height));
        tail.Add(s.Format.IsStill ? Converter.StillToRgb(color) : "setsar=1");
        graph.Add($"[{last}]{string.Join(',', tail)}[v]");

        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[v]"]);
        if (withAudio)
            args.AddRange(["-map", "1:a:0"]);
        args.AddRange(s.Format.CodecArguments(locator.Capabilities, s.Quality, true, withAudio));
        if (!s.Format.IsStill)
            args.AddRange(color.OutputTags);
        if (!withAudio)
            args.Add("-an");
        args.AddRange(["-y", output]);
        return args;
    }

    /// <summary>Starts ffmpeg with stdin (encoder) or stdout (decoder) redirected; its errors go to the log.</summary>
    public static Process Launch(FfmpegLocator locator, List<string> args, bool redirectInput, ResourceLimits limits)
    {
        args = limits.ApplyToFfmpeg(args);
        var psi = new ProcessStartInfo(locator.FfmpegPath)
        {
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = !redirectInput,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Log.Info("upscale", "ffmpeg " + FfmpegRunner.FormatCommandLine(args));
        var p = ChildProcess.Start(psi, keepInput: redirectInput, isFfmpeg: true);
        limits.Apply(p);
        p.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Log.Warn("upscale", "ffmpeg: " + e.Data);
        };
        p.BeginErrorReadLine();
        return p;
    }
}
