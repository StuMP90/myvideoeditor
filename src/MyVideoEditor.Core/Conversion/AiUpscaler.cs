using System.Diagnostics;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Conversion;

/// <summary>
/// Machine-learning upscaling with Real-ESRGAN (realesrgan-ncnn-vulkan, BSD-3-Clause), when it
/// is installed: on PATH, in MYVIDEOEDITOR_REALESRGAN_DIR, or in a "realesrgan" folder next to
/// the app. Frames are extracted, upscaled 4× on the GPU, scaled to the exact target size and
/// re-encoded with the original sound.
/// </summary>
public sealed class AiUpscaler
{
    public const string DirEnvironmentVariable = "MYVIDEOEDITOR_REALESRGAN_DIR";

    private readonly FfmpegLocator _locator;

    public AiUpscaler(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    /// <summary>Path to realesrgan-ncnn-vulkan, or null if it isn't installed.</summary>
    public static string? FindTool()
    {
        var exe = "realesrgan-ncnn-vulkan" + (OperatingSystem.IsWindows() ? ".exe" : "");
        var dirs = new List<string>();
        if (Environment.GetEnvironmentVariable(DirEnvironmentVariable) is { Length: > 0 } env)
            dirs.Add(env);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "realesrgan"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);
    }

    public static bool IsAvailable => FindTool() != null;

    public async Task UpscaleAsync(MediaInfo info, string output, ConversionSettings settings,
        IProgress<FfmpegProgress>? progress, CancellationToken cancellationToken)
    {
        var tool = FindTool() ?? throw new InvalidOperationException(
            "AI upscaling needs Real-ESRGAN (realesrgan-ncnn-vulkan). Download it from github.com/xinntao/Real-ESRGAN/releases " +
            "and put it on your PATH, or choose another upscaling method.");
        var work = Path.Combine(AppPaths.CacheDirectory, "upscale-" + Guid.NewGuid().ToString("N")[..8]);
        var framesIn = Path.Combine(work, "in");
        var framesOut = Path.Combine(work, "out");
        Directory.CreateDirectory(framesIn);
        Directory.CreateDirectory(framesOut);
        var runner = new FfmpegRunner(_locator);
        var length = Converter.OutputLength(info, settings);
        var fps = settings.FrameRate ?? info.FrameRate;
        var expectedFrames = Math.Max(1, (int)Math.Ceiling(length * fps));
        try
        {
            // 1. Extract frames (trimmed, at the output frame rate).
            var extract = new List<string>();
            if (settings.TrimStart is > 0)
                extract.AddRange(["-ss", F(settings.TrimStart.Value)]);
            extract.AddRange(["-t", F(length), "-i", info.Path, "-vf", $"fps={F(fps)}", "-fps_mode", "passthrough", Path.Combine(framesIn, "%08d.png")]);
            await runner.RunAsync(extract, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 2. Upscale 4x, reporting progress as frames appear.
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            psi.WorkingDirectory = Path.GetDirectoryName(tool)!;
            foreach (var a in new[] { "-i", framesIn, "-o", framesOut, "-n", "realesrgan-x4plus", "-s", "4", "-f", "png" })
                psi.ArgumentList.Add(a);
            Log.Info("upscale", $"Running {tool} on {expectedFrames} frames.");
            using (var process = Process.Start(psi)!)
            {
                _ = process.StandardError.ReadToEndAsync(cancellationToken);
                _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        process.Kill(true);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    var done = Directory.EnumerateFiles(framesOut, "*.png").Count();
                    // The AI pass is the slow part: report it as 0-90%.
                    progress?.Report(new FfmpegProgress(TimeSpan.FromSeconds(length * 0.9 * done / expectedFrames), TimeSpan.FromSeconds(length), 0));
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Real-ESRGAN failed (exit code {process.ExitCode}). It needs a GPU with Vulkan support.");
            }

            // 3. Re-encode at the exact target size, with the original sound.
            var scale = Converter.ScaleFilter(settings.Width ?? info.Width * 2, settings.Height, settings.ResizeMode, "lanczos") ?? "null";
            var encode = new List<string> { "-framerate", F(fps), "-i", Path.Combine(framesOut, "%08d.png") };
            var withAudio = info.HasAudio && !settings.RemoveAudio && settings.Format.HasAudio;
            if (withAudio)
            {
                if (settings.TrimStart is > 0)
                    encode.AddRange(["-ss", F(settings.TrimStart.Value)]);
                encode.AddRange(["-t", F(length), "-i", info.Path, "-map", "0:v", "-map", "1:a:0"]);
            }
            encode.AddRange(["-vf", scale + ",setsar=1"]);
            encode.AddRange(settings.Format.CodecArguments(_locator.Capabilities, settings.Quality, true, withAudio));
            if (!withAudio)
                encode.Add("-an");
            encode.AddRange(["-shortest", "-y", output]);
            var tail = new Progress<FfmpegProgress>(p => progress?.Report(new FfmpegProgress(
                TimeSpan.FromSeconds(length * 0.9 + p.Position.TotalSeconds * 0.1), TimeSpan.FromSeconds(length), p.Speed)));
            await runner.RunAsync(encode, TimeSpan.FromSeconds(length), tail, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
