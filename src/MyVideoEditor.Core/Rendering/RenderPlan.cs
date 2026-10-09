using System.Globalization;
using MyVideoEditor.Core.Graphics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.Core.Rendering;

/// <summary>What part of a project to render, and at what size.</summary>
public sealed record RenderOptions
{
    /// <summary>Timeline time the output starts at (seconds).</summary>
    public double RangeStart { get; init; }
    /// <summary>Timeline time the output ends at; null = the end of the project.</summary>
    public double? RangeEnd { get; init; }
    /// <summary>Output frame size; null = the project's size. Smaller sizes render faster (preview).</summary>
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool IncludeVideo { get; init; } = true;
    public bool IncludeAudio { get; init; } = true;
    public GraphicsRenderer? Graphics { get; init; }
    /// <summary>Effects needing filters this ffmpeg lacks are skipped (with a warning) rather than failing.</summary>
    public FfmpegCapabilities? Capabilities { get; init; }
    /// <summary>ffmpeg scaler for resizing footage: "bicubic" is quick (preview), "lanczos" is sharper (export, upscaling).</summary>
    public string ScaleFlags { get; init; } = "bicubic";
    /// <summary>
    /// Plays the range this many times faster (preview fast-forward): the picture keeps its frame
    /// rate by dropping frames, and the sound is sped up without changing its pitch.
    /// </summary>
    public double Speed { get; init; } = 1;
}

/// <summary>
/// A project compiled for ffmpeg: one input per clip piece, and a filter graph that composites
/// the video tracks over the background and mixes the audio. The output streams are labelled
/// [vout] and [aout]. The same plan drives exporting, preview playback and single frames.
/// </summary>
public sealed record RenderPlan
{
    /// <summary>Above this, the graph goes in a file (Windows command lines are limited to 32k characters).</summary>
    private const int MaxInlineGraph = 6000;

    public required IReadOnlyList<string> InputArguments { get; init; }
    public required string FilterGraph { get; init; }
    public string? VideoOutput { get; init; }
    public string? AudioOutput { get; init; }
    public double Duration { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    public int SampleRate { get; init; }
    public int InputCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Full ffmpeg arguments: the inputs, the graph, the maps, then <paramref name="outputArguments"/>.
    /// A long graph is written to <paramref name="scriptDirectory"/> (a temp folder by default).
    /// </summary>
    public List<string> BuildArguments(IEnumerable<string> outputArguments, FfmpegCapabilities? capabilities = null, string? scriptDirectory = null)
    {
        var args = new List<string>(InputArguments);
        if (FilterGraph.Length > MaxInlineGraph)
        {
            var dir = scriptDirectory ?? Path.Combine(Path.GetTempPath(), "myvideoeditor");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"graph-{Guid.NewGuid():N}.txt");
            File.WriteAllText(file, FilterGraph);
            // ffmpeg 7 replaced -filter_complex_script with the generic "-/option file" form.
            if ((capabilities?.MajorVersion ?? 0) >= 7)
                args.AddRange(["-/filter_complex", file]);
            else
                args.AddRange(["-filter_complex_script", file]);
        }
        else
        {
            args.AddRange(["-filter_complex", FilterGraph]);
        }
        if (VideoOutput != null)
            args.AddRange(["-map", $"[{VideoOutput}]"]);
        if (AudioOutput != null)
            args.AddRange(["-map", $"[{AudioOutput}]"]);
        args.AddRange(outputArguments);
        return args;
    }

    internal static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
