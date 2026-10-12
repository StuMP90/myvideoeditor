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
/// </summary>
internal static class UpscaleEncoder
{
    /// <summary>Arguments for the encoder; <paramref name="frameInput"/> describes the piped frames (input 0).</summary>
    public static List<string> Arguments(FfmpegLocator locator, MediaInfo info, ConversionSettings s, ColorSpec color, ScaledSize target, string rate,
        IReadOnlyList<string> frameInput, bool framesAreRgb, string output)
    {
        var withAudio = info.HasAudio && !s.RemoveAudio && s.Format.HasAudio;
        var strength = Math.Clamp(s.AiStrength, 0, 1);
        var blend = UpscaleMethods.Get(s.Algorithm).HasStrength && strength < 0.995;
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        args.AddRange(frameInput);
        if (withAudio || blend)
        {
            // The source again: its sound, and (when blending) the frames for the faithful layer.
            if (s.TrimStart is > 0)
                args.AddRange(["-ss", F(s.TrimStart.Value)]);
            args.AddRange(["-t", F(Converter.OutputLength(info, s)), "-i", info.Path]);
        }

        var index = $"setpts=N/({rate})/TB";
        var scale = $"scale={target.Width}:{target.Height}:flags=lanczos+accurate_rnd+full_chroma_int";
        var toYuv = framesAreRgb ? $"{scale}:out_color_matrix={color.SwsMatrix}:out_range={color.Range}" : scale;
        var graph = new List<string> { $"[0:v]{toYuv},format=yuv444p,{index}[up]" };
        var last = "up";
        if (blend)
        {
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
        var tail = new List<string>();
        if (target.PostFilter != null)
            tail.Add(target.PostFilter);
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
