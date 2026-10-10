using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.Core.Export;

public sealed record ExportSettings
{
    public required OutputFormat Format { get; init; }
    public Quality Quality { get; init; } = Quality.Standard;
    /// <summary>Output size; null = the project's size. Graphics are drawn at this size, so they stay sharp.</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>Part of the timeline to export; null = all of it.</summary>
    public double? RangeStart { get; init; }
    public double? RangeEnd { get; init; }
    /// <summary>DVD-Video: also make a disc folder (VIDEO_TS) next to the file, with dvdauthor.</summary>
    public bool MakeDvdFolder { get; init; }
    /// <summary>Chapters, for formats that hold them and DVD folders.</summary>
    public ChapterSource Chapters { get; init; }
    public double ChapterIntervalMinutes { get; init; } = 5;
}

/// <summary>Renders a project to a file.</summary>
public sealed class ProjectExporter
{
    private readonly FfmpegLocator _locator;
    private readonly FfmpegRunner _runner;

    public ProjectExporter(FfmpegLocator? locator = null)
    {
        _locator = locator ?? FfmpegLocator.Default;
        _runner = new FfmpegRunner(_locator);
    }

    /// <summary>
    /// Exports to <paramref name="outputPath"/>. The file is written under a temporary name and
    /// renamed when complete, so a cancelled or failed export never leaves a broken file behind.
    /// Returns warnings (e.g. missing media that was left out).
    /// </summary>
    public async Task<IReadOnlyList<string>> ExportAsync(Project project, string outputPath, ExportSettings settings,
        IProgress<FfmpegProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var caps = _locator.Capabilities;
        var format = settings.Format;
        // DVD-Video: drawn at DVD's shown size (the project fitted inside it), whatever size was asked for.
        var dvd = format.IsDvd ? DvdVideo.For(project.Settings) : null;
        var dvdSize = dvd?.RenderSize(project.Settings);
        var plan = RenderPlanBuilder.Build(project, new RenderOptions
        {
            RangeStart = settings.RangeStart ?? 0,
            RangeEnd = settings.RangeEnd,
            Width = dvdSize?.Width ?? settings.Width,
            Height = dvdSize?.Height ?? settings.Height,
            IncludeVideo = format.HasVideo,
            IncludeAudio = format.HasAudio,
            Capabilities = caps,
            ScaleFlags = "lanczos",
        });

        var output = new List<string>();
        if (format.IsGif)
        {
            plan = WithGifPalette(plan, format.MaxFrameRate ?? 15);
        }
        else if (dvd != null)
        {
            plan = dvd.Apply(plan);
            output.AddRange(dvd.OutputArguments());
        }
        else if (format.HasVideo && project.Settings.HasNonSquarePixels && plan.Width == project.Settings.Width)
        {
            plan = WithStoredWidth(plan, project.Settings.OutputWidth);
        }
        if (format.HasVideo && dvd == null && !format.IsGif)
        {
            output.AddRange(["-r", RenderPlan.F(plan.FrameRate)]);
        }
        // Chapters: a keyframe at each, so they start exactly there; in the file (MP4, MKV…) or the DVD folder.
        var rangeStart = settings.RangeStart ?? 0;
        var chapters = format.SupportsChapters || (format.IsDvd && settings.MakeDvdFolder)
            ? Export.Chapters.For(project, settings.Chapters, rangeStart, rangeStart + plan.Duration, settings.ChapterIntervalMinutes)
            : [];
        string? chapterFile = null;
        if (chapters.Count > 0)
        {
            output.AddRange(["-force_key_frames", Export.Chapters.KeyFrames(chapters)]);
            if (format.SupportsChapters)
            {
                chapterFile = Path.Combine(Path.GetTempPath(), $"myvideoeditor-chapters-{Guid.NewGuid():N}.txt");
                File.WriteAllText(chapterFile, Export.Chapters.Metadata(chapters, plan.Duration));
                output.AddRange(["-map_chapters", plan.InputCount.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                plan = plan with { InputArguments = [.. plan.InputArguments, "-f", "ffmetadata", "-i", chapterFile], InputCount = plan.InputCount + 1 };
            }
        }
        output.AddRange(format.CodecArguments(caps, settings.Quality));
        output.AddRange(["-t", RenderPlan.F(plan.Duration), "-y"]);

        var full = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var partial = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".partial" + Path.GetExtension(full));
        output.Add(partial);

        var args = plan.BuildArguments(output, caps);
        Log.Info("export", $"Exporting {project.Name} to {full} ({format.Label}, {settings.Quality}, {plan.Width}x{plan.Height}, {plan.Duration:0.##}s, {plan.InputCount} inputs).");
        try
        {
            await _runner.RunAsync(args, TimeSpan.FromSeconds(plan.Duration), progress, cancellationToken).ConfigureAwait(false);
            File.Move(partial, full, overwrite: true);
        }
        finally
        {
            TryDelete(partial);
            if (chapterFile != null)
                TryDelete(chapterFile);
        }
        if (dvd != null && settings.MakeDvdFolder)
            await dvd.MakeFolderAsync(full, DvdFolderFor(full), chapters, cancellationToken).ConfigureAwait(false);
        return plan.Warnings;
    }

    /// <summary>Where a DVD export's disc folder goes: next to the file, named after it.</summary>
    public static string DvdFolderFor(string mpegPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(mpegPath))!, Path.GetFileNameWithoutExtension(mpegPath) + " DVD");

    /// <summary>Saves one frame of the timeline as a PNG (for bug reports and thumbnails).</summary>
    public async Task ExportFrameAsync(Project project, double time, string outputPath, int? width = null, int? height = null,
        CancellationToken cancellationToken = default)
    {
        var plan = FramePlan(project, time, width, height);
        var args = plan.BuildArguments(["-frames:v", "1", "-update", "1", "-y", outputPath], _locator.Capabilities);
        await _runner.RunAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A plan rendering just the frame at <paramref name="time"/> (video only).</summary>
    public static RenderPlan FramePlan(Project project, double time, int? width, int? height, FfmpegCapabilities? caps = null)
    {
        var frame = 1.0 / Math.Max(1, project.Settings.FrameRate);
        var end = Math.Max(project.Duration, frame);
        var start = Math.Clamp(time, 0, Math.Max(0, end - frame));
        return RenderPlanBuilder.Build(project, new RenderOptions
        {
            RangeStart = start,
            RangeEnd = start + frame,
            Width = width,
            Height = height,
            IncludeAudio = false,
            Capabilities = caps,
            ScaleFlags = "lanczos",
        });
    }

    /// <summary>
    /// Stores the picture <paramref name="storedWidth"/> pixels wide, marked to be shown at its own width
    /// (non-square pixels, as on a DVD: 640×480 stored as 720×480).
    /// </summary>
    internal static RenderPlan WithStoredWidth(RenderPlan plan, int storedWidth)
    {
        var graph = plan.FilterGraph +
                    $";\n[{plan.VideoOutput}]scale={storedWidth}:{plan.Height}:flags=lanczos,setsar={plan.Width}/{storedWidth}[stored]";
        return plan with { FilterGraph = graph, VideoOutput = "stored" };
    }

    /// <summary>GIFs look far better with a palette made from the clip itself.</summary>
    internal static RenderPlan WithGifPalette(RenderPlan plan, double maxFps)
    {
        var fps = Math.Min(plan.FrameRate, maxFps);
        var graph = plan.FilterGraph +
                    $";\n[{plan.VideoOutput}]fps={RenderPlan.F(fps)},split[gif_a][gif_b];" +
                    "\n[gif_a]palettegen=stats_mode=diff[gif_palette];" +
                    "\n[gif_b][gif_palette]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle[gif]";
        return plan with { FilterGraph = graph, VideoOutput = "gif", AudioOutput = null };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
