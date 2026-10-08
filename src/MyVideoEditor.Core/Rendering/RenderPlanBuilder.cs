using System.Text;
using MyVideoEditor.Core.Graphics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Rendering;

/// <summary>
/// Compiles a project into a <see cref="RenderPlan"/>.
///
/// Each clip becomes its own input, opened with -ss/-t at exactly the part needed, so a
/// preview that starts deep into a long recording doesn't decode everything before it. Inside
/// the graph each clip's timestamps are first set to "time since the clip started rendering",
/// where fades and dissolves are applied, then shifted to its place in the output and
/// overlaid (video) or delayed and mixed (audio).
/// </summary>
public static class RenderPlanBuilder
{
    private const double Eps = 1e-6;

    public static RenderPlan Build(Project project, RenderOptions? options = null)
    {
        options ??= new RenderOptions();
        var s = project.Settings;
        var rangeStart = Math.Max(0, options.RangeStart);
        var rangeEnd = options.RangeEnd ?? project.Duration;
        if (rangeEnd - rangeStart < Eps)
            throw new InvalidOperationException("There's nothing to render: the timeline is empty.");
        var duration = rangeEnd - rangeStart;
        var width = Even(options.Width ?? s.Width);
        var height = Even(options.Height ?? s.Height);
        var fps = s.FrameRate > 0 ? s.FrameRate : 30;
        var sampleRate = s.SampleRate > 0 ? s.SampleRate : 48000;
        var context = new BuildContext(project, options, rangeStart, rangeEnd, width, height, (double)width / s.Width, fps, sampleRate);

        string? videoOut = null, audioOut = null;
        if (options.IncludeVideo)
            videoOut = BuildVideo(context);
        if (options.IncludeAudio)
            audioOut = BuildAudio(context);

        return new RenderPlan
        {
            InputArguments = context.Inputs,
            FilterGraph = string.Join(";\n", context.Graph),
            VideoOutput = videoOut,
            AudioOutput = audioOut,
            Duration = duration,
            Width = width,
            Height = height,
            FrameRate = fps,
            SampleRate = sampleRate,
            InputCount = context.InputCount,
            Warnings = context.Warnings,
        };
    }

    // ---- items: clips with their render span, after transitions ------------------------------

    internal sealed class Item
    {
        public required Clip Clip { get; init; }
        public required Track Track { get; init; }
        public double RenderStart { get; set; }
        public double RenderEnd { get; set; }
        /// <summary>Drawn above the other clips on its track (the outgoing clip of a dissolve).</summary>
        public bool OnTop { get; set; }
        /// <summary>(start, duration, fade-in?) in seconds since <see cref="RenderStart"/>.</summary>
        public List<(double Start, double Duration, bool In)> VideoFades { get; } = [];
        public List<(double Start, double Duration, bool In)> AudioFades { get; } = [];
    }

    /// <summary>The clips of a track as render items, with dissolves turned into overlaps and fades.</summary>
    internal static List<Item> Items(Project project, Track track)
    {
        var clips = track.Clips.OrderBy(c => c.Start).ToList();
        var items = clips.Select(c => new Item { Clip = c, Track = track, RenderStart = c.Start, RenderEnd = c.End }).ToList();
        for (var i = 1; i < items.Count; i++)
        {
            var a = items[i - 1];
            var b = items[i];
            var d = Math.Min(b.Clip.TransitionIn, Math.Min(a.Clip.Duration, b.Clip.Duration));
            if (d < Eps || Math.Abs(a.Clip.End - b.Clip.Start) > 0.002)
                continue;
            var headB = HeadHandle(project, b.Clip);
            var tailA = TailHandle(project, a.Clip);
            if (headB < d && tailA < d)
                d = Math.Max(headB, tailA);
            if (d < 0.01)
                continue;
            if (headB >= d)
            {
                // The incoming clip starts early and fades in over the outgoing one.
                b.RenderStart -= d;
                b.VideoFades.Add((0, d, true));
                a.AudioFades.Add((a.Clip.End - d - a.RenderStart, d, false));
            }
            else
            {
                // Not enough footage before the incoming clip: the outgoing clip runs on and fades out on top.
                a.RenderEnd += d;
                a.OnTop = true;
                a.VideoFades.Add((a.Clip.End - a.RenderStart, d, false));
                a.AudioFades.Add((a.Clip.End - a.RenderStart, d, false));
            }
            // Sound crossfades over the same overlap.
            b.AudioFades.Add((0, d, true));
        }
        foreach (var item in items)
        {
            var c = item.Clip;
            var clipOffset = c.Start - item.RenderStart;
            if (c.FadeIn > Eps)
            {
                item.VideoFades.Add((clipOffset, c.FadeIn, true));
                item.AudioFades.Add((clipOffset, c.FadeIn, true));
            }
            if (c.FadeOut > Eps)
            {
                item.VideoFades.Add((clipOffset + c.Duration - c.FadeOut, c.FadeOut, false));
                item.AudioFades.Add((clipOffset + c.Duration - c.FadeOut, c.FadeOut, false));
            }
        }
        return items;
    }

    /// <summary>Seconds of footage available before a clip's in-point (unlimited for stills and graphics).</summary>
    private static double HeadHandle(Project project, Clip clip) => clip switch
    {
        MediaClip m when project.FindMedia(m.MediaId) is { Kind: not MediaKind.Image } => m.InPoint / m.Speed,
        _ => double.MaxValue,
    };

    /// <summary>Seconds of footage available after a clip's out-point.</summary>
    private static double TailHandle(Project project, Clip clip) => clip switch
    {
        MediaClip m when project.FindMedia(m.MediaId) is { Kind: not MediaKind.Image } media => Math.Max(0, media.Duration - m.OutPoint) / m.Speed,
        _ => double.MaxValue,
    };

    // ---- video -------------------------------------------------------------------------------

    private static string BuildVideo(BuildContext c)
    {
        var bg = GraphicsRenderer.TryParseColor(c.Project.Settings.BackgroundColor, out var color)
            ? $"0x{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
            : "0x000000";
        var current = c.Label("bg");
        c.Graph.Add($"color=c={bg}:s={c.Width}x{c.Height}:r={F(c.Fps)}:d={F(c.Duration)},format=yuv420p,setsar=1[{current}]");

        // Bottom track first; within a track, outgoing dissolve clips last (on top).
        foreach (var track in Enumerable.Reverse(c.Project.Tracks).Where(t => t.Kind == TrackKind.Video && !t.Hidden))
        {
            foreach (var item in Items(c.Project, track).OrderBy(i => i.OnTop).ThenBy(i => i.RenderStart))
            {
                if (!c.Visible(item, out var span))
                    continue;
                current = item.Clip switch
                {
                    MediaClip m => AddMediaVideo(c, item, m, span, current),
                    AnnotationClip { IsFilterShape: true } a => AddFilterAnnotation(c, item, a, span, current),
                    TitleClip or AnnotationClip => AddGraphic(c, item, span, current),
                    _ => current,
                };
            }
        }
        var output = "vout";
        c.Graph.Add($"[{current}]setsar=1[{output}]");
        return output;
    }

    private static string AddMediaVideo(BuildContext c, Item item, MediaClip clip, Span span, string current)
    {
        var media = c.Project.FindMedia(clip.MediaId);
        if (media == null || !media.HasVideo)
            return current;
        if (!File.Exists(media.Path))
        {
            c.Warn($"{media.Name} is missing ({media.Path}); its clips are left out.");
            return current;
        }
        var input = c.AddMediaInput(item, clip, media, span);

        var chain = new List<string>
        {
            // Clip time: seconds since the clip started rendering.
            $"setpts=(PTS-STARTPTS)/{F(clip.Speed)}+{F(span.LocalOffset)}/TB",
            $"fps={F(c.Fps)}",
        };
        var srcW = Math.Max(2, media.Width - clip.CropLeft - clip.CropRight);
        var srcH = Math.Max(2, media.Height - clip.CropTop - clip.CropBottom);
        if (clip.CropLeft + clip.CropRight + clip.CropTop + clip.CropBottom > 0)
            chain.Add($"crop={srcW}:{srcH}:{Math.Max(0, clip.CropLeft)}:{Math.Max(0, clip.CropTop)}");

        var (w, h) = FittedSize(clip.Fit, srcW, srcH, c.Width, c.Height, c.PixelScale, clip.Scale);
        chain.Add($"scale={w}:{h}:flags={c.Options.ScaleFlags},setsar=1");
        AddVideoEffects(c, clip, chain);
        chain.Add("format=yuva420p");
        if (Math.Abs(clip.Rotation % 360) > 0.01)
        {
            var rad = F(clip.Rotation * Math.PI / 180);
            chain.Add($"rotate={rad}:c=none:ow=rotw({rad}):oh=roth({rad})");
        }
        AddOpacityAndFades(clip, item, chain);
        chain.Add($"setpts=PTS-STARTPTS+{F(span.OutputOffset)}/TB");

        var layer = c.Label("v");
        c.Graph.Add($"[{input}:v]{string.Join(',', chain)}[{layer}]");
        var next = c.Label("c");
        var x = $"(main_w-overlay_w)/2{Signed(clip.OffsetX * c.PixelScale)}";
        var y = $"(main_h-overlay_h)/2{Signed(clip.OffsetY * c.PixelScale)}";
        c.Graph.Add($"[{current}][{layer}]overlay=x={x}:y={y}:eof_action=pass[{next}]");
        return next;
    }

    private static string AddGraphic(BuildContext c, Item item, Span span, string current)
    {
        var graphics = c.Options.Graphics ?? GraphicsRenderer.Default;
        var rendered = graphics.Render(item.Clip, c.Width, c.Height, c.Project.Settings.Width, c.Project.Settings.Height);
        if (rendered == null)
            return current;
        var input = c.AddStillInput(rendered.Path, span.Length);
        var chain = new List<string> { "format=yuva420p", $"setpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB" };
        AddOpacityAndFades(item.Clip, item, chain);
        chain.Add($"setpts=PTS-STARTPTS+{F(span.OutputOffset)}/TB");
        var layer = c.Label("g");
        c.Graph.Add($"[{input}:v]{string.Join(',', chain)}[{layer}]");
        var next = c.Label("c");
        c.Graph.Add($"[{current}][{layer}]overlay=x={rendered.X}:y={rendered.Y}:eof_action=pass[{next}]");
        return next;
    }

    /// <summary>Blur, pixelate and zoom: a processed copy of a region of everything below, laid back on top.</summary>
    private static string AddFilterAnnotation(BuildContext c, Item item, AnnotationClip a, Span span, string current)
    {
        var (rx, ry, rw, rh) = Region(a, c.Width, c.Height, zoom: a.Shape == AnnotationShape.Zoom);
        if (rw < 4 || rh < 4)
            return current;
        var main = c.Label("m");
        var copy = c.Label("s");
        c.Graph.Add($"[{current}]split=2[{main}][{copy}]");

        var start = span.VisibleStart - c.RangeStart;
        var chain = new List<string>
        {
            $"trim=start={F(start)}:end={F(start + span.Length)}",
            $"setpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB",
            $"crop={rw}:{rh}:{rx}:{ry}",
        };
        int ox = rx, oy = ry;
        switch (a.Shape)
        {
            case AnnotationShape.Blur:
                var sigma = Math.Max(1, (a.Strength > 0 ? a.Strength : 20) * GraphicsRenderer.SizeScale(c.Height));
                chain.Add($"gblur=sigma={F(sigma)}:steps=3");
                break;
            case AnnotationShape.Pixelate:
                var block = Math.Max(2, (a.Strength > 1 ? a.Strength : 16) * GraphicsRenderer.SizeScale(c.Height));
                chain.Add($"scale={Math.Max(1, (int)(rw / block))}:{Math.Max(1, (int)(rh / block))}:flags=area");
                chain.Add($"scale={rw}:{rh}:flags=neighbor");
                break;
            case AnnotationShape.Zoom:
                chain.Add($"scale={c.Width}:{c.Height}:flags=lanczos,setsar=1");
                ox = oy = 0;
                break;
        }
        chain.Add("format=yuva420p");
        AddOpacityAndFades(a, item, chain);
        chain.Add($"setpts=PTS-STARTPTS+{F(span.OutputOffset)}/TB");
        var layer = c.Label("f");
        c.Graph.Add($"[{copy}]{string.Join(',', chain)}[{layer}]");
        var next = c.Label("c");
        c.Graph.Add($"[{main}][{layer}]overlay=x={ox}:y={oy}:eof_action=pass[{next}]");
        return next;
    }

    /// <summary>
    /// The region of an annotation in output pixels (even values for 4:2:0). For zoom the region
    /// is widened to the frame's aspect ratio, so the zoomed picture isn't stretched.
    /// </summary>
    internal static (int X, int Y, int W, int H) Region(AnnotationClip a, int width, int height, bool zoom)
    {
        double x = a.Left * width, y = a.Top * height, w = (a.Right - a.Left) * width, h = (a.Bottom - a.Top) * height;
        if (zoom && w > 0 && h > 0)
        {
            var aspect = (double)width / height;
            if (w / h < aspect)
            {
                var nw = h * aspect;
                x -= (nw - w) / 2;
                w = nw;
            }
            else
            {
                var nh = w / aspect;
                y -= (nh - h) / 2;
                h = nh;
            }
            w = Math.Min(w, width);
            h = Math.Min(h, height);
            x = Math.Clamp(x, 0, width - w);
            y = Math.Clamp(y, 0, height - h);
        }
        var ix = Math.Clamp((int)Math.Round(x) & ~1, 0, width - 2);
        var iy = Math.Clamp((int)Math.Round(y) & ~1, 0, height - 2);
        var iw = Math.Min(width - ix, Even((int)Math.Round(w)));
        var ih = Math.Min(height - iy, Even((int)Math.Round(h)));
        return (ix, iy, iw & ~1, ih & ~1);
    }

    /// <summary>The size a source of srcW×srcH is scaled to inside a width×height frame.</summary>
    internal static (int W, int H) FittedSize(FitMode fit, int srcW, int srcH, int width, int height, double pixelScale, double extraScale)
    {
        double sx, sy;
        switch (fit)
        {
            case FitMode.Fill:
                sx = sy = Math.Max((double)width / srcW, (double)height / srcH);
                break;
            case FitMode.Stretch:
                sx = (double)width / srcW;
                sy = (double)height / srcH;
                break;
            case FitMode.Original:
                sx = sy = pixelScale;
                break;
            default:
                sx = sy = Math.Min((double)width / srcW, (double)height / srcH);
                break;
        }
        var scale = extraScale > 0 ? extraScale : 1;
        return (Math.Max(2, Even((int)Math.Round(srcW * sx * scale))), Math.Max(2, Even((int)Math.Round(srcH * sy * scale))));
    }

    private static void AddVideoEffects(BuildContext c, Clip clip, List<string> chain)
    {
        foreach (var effect in clip.Effects.Where(e => e.Enabled))
        {
            var def = EffectCatalog.Find(effect.Type);
            if (def == null || def.IsAudio)
                continue;
            if (def.RequiredFilter != null && c.Options.Capabilities is { } caps && caps.Filters.Count > 0 && !caps.HasFilter(def.RequiredFilter))
            {
                c.Warn($"\"{def.Label}\" needs the ffmpeg filter {def.RequiredFilter}, which this ffmpeg doesn't have; it was skipped.");
                continue;
            }
            chain.Add(def.Filter(effect, new EffectContext(c.PixelScale, c.SampleRate)));
        }
    }

    private static void AddOpacityAndFades(Clip clip, Item item, List<string> chain)
    {
        if (clip.Opacity < 0.999)
            chain.Add($"colorchannelmixer=aa={F(Math.Clamp(clip.Opacity, 0, 1))}");
        foreach (var (start, duration, fadeIn) in item.VideoFades)
            chain.Add($"fade=t={(fadeIn ? "in" : "out")}:st={F(Math.Max(0, start))}:d={F(duration)}:alpha=1");
    }

    // ---- audio -------------------------------------------------------------------------------

    private static string BuildAudio(BuildContext c)
    {
        var layers = new List<string>();
        foreach (var track in c.Project.Tracks.Where(t => !t.Muted))
        {
            foreach (var item in Items(c.Project, track))
            {
                if (item.Clip is not MediaClip clip || !c.Visible(item, out var span))
                    continue;
                if (track.Kind == TrackKind.Video && clip.AudioMuted)
                    continue;
                var media = c.Project.FindMedia(clip.MediaId);
                if (media == null || !media.HasAudio || media.Kind == MediaKind.Image || !File.Exists(media.Path))
                    continue;
                var volume = clip.Volume * track.Volume;
                if (volume <= 0.0001)
                    continue;
                var input = c.AddMediaInput(item, clip, media, span);

                var chain = new List<string> { "asetpts=PTS-STARTPTS" };
                chain.AddRange(Atempo(clip.Speed));
                chain.Add($"asetpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB");
                foreach (var effect in clip.Effects.Where(e => e.Enabled))
                {
                    var def = EffectCatalog.Find(effect.Type);
                    if (def is { IsAudio: true })
                        chain.Add(def.Filter(effect, new EffectContext(c.PixelScale, c.SampleRate)));
                }
                if (Math.Abs(volume - 1) > 0.0001)
                    chain.Add($"volume={F(volume)}");
                foreach (var (start, duration, fadeIn) in item.AudioFades)
                    chain.Add($"afade=t={(fadeIn ? "in" : "out")}:st={F(Math.Max(0, start))}:d={F(duration)}");
                chain.Add($"aresample={c.SampleRate}");
                chain.Add("aformat=sample_fmts=fltp:channel_layouts=stereo");
                chain.Add("asetpts=PTS-STARTPTS");
                var delay = (long)Math.Round(span.OutputOffset * c.SampleRate);
                if (delay > 0)
                    chain.Add($"adelay=delays={delay}S:all=1");
                var label = c.Label("a");
                c.Graph.Add($"[{input}:a]{string.Join(',', chain)}[{label}]");
                layers.Add(label);
            }
        }

        // Silence the full length underneath, so the output is always exactly as long as the range.
        var silence = c.Label("silence");
        c.Graph.Add($"anullsrc=r={c.SampleRate}:cl=stereo,atrim=duration={F(c.Duration)}[{silence}]");
        var output = "aout";
        if (layers.Count == 0)
        {
            c.Graph.Add($"[{silence}]anull[{output}]");
        }
        else
        {
            var all = string.Concat(new[] { silence }.Concat(layers).Select(l => $"[{l}]"));
            c.Graph.Add($"{all}amix=inputs={layers.Count + 1}:duration=first:dropout_transition=0:normalize=0[{output}]");
        }
        return output;
    }

    /// <summary>atempo only goes from 0.5× to 2× per filter, so larger changes are chained.</summary>
    internal static IEnumerable<string> Atempo(double speed)
    {
        if (Math.Abs(speed - 1) < 0.0001)
            yield break;
        var remaining = speed;
        while (remaining > 2.0 + 1e-9)
        {
            yield return "atempo=2";
            remaining /= 2;
        }
        while (remaining < 0.5 - 1e-9)
        {
            yield return "atempo=0.5";
            remaining /= 0.5;
        }
        yield return $"atempo={F(remaining)}";
    }

    // ---- shared ------------------------------------------------------------------------------

    /// <summary>The part of an item inside the render range.</summary>
    internal readonly record struct Span(double VisibleStart, double Length, double LocalOffset, double OutputOffset);

    private sealed class BuildContext(
        Project project, RenderOptions options, double rangeStart, double rangeEnd,
        int width, int height, double pixelScale, double fps, int sampleRate)
    {
        private int _labels;
        private readonly Dictionary<string, int> _mediaInputs = [];

        public Project Project { get; } = project;
        public RenderOptions Options { get; } = options;
        public double RangeStart { get; } = rangeStart;
        public double RangeEnd { get; } = rangeEnd;
        public double Duration => RangeEnd - RangeStart;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public double PixelScale { get; } = pixelScale;
        public double Fps { get; } = fps;
        public int SampleRate { get; } = sampleRate;
        public List<string> Inputs { get; } = [];
        public List<string> Graph { get; } = [];
        public List<string> Warnings { get; } = [];
        public int InputCount { get; private set; }

        public string Label(string prefix) => $"{prefix}{_labels++}";

        public void Warn(string message)
        {
            if (!Warnings.Contains(message))
                Warnings.Add(message);
        }

        public bool Visible(Item item, out Span span)
        {
            var start = Math.Max(item.RenderStart, RangeStart);
            var end = Math.Min(item.RenderEnd, RangeEnd);
            span = new Span(start, end - start, start - item.RenderStart, start - RangeStart);
            return end - start > 1e-4;
        }

        /// <summary>One input per clip piece (shared by its video and audio), seeked to exactly what's needed.</summary>
        public int AddMediaInput(Item item, MediaClip clip, MediaReference media, Span span)
        {
            var key = $"{clip.Id}|{item.RenderStart}";
            if (_mediaInputs.TryGetValue(key, out var existing))
                return existing;
            if (media.Kind == MediaKind.Image)
            {
                var still = AddStillInput(media.Path, span.Length);
                _mediaInputs[key] = still;
                return still;
            }
            var sourceStart = clip.InPoint - (clip.Start - item.RenderStart) * clip.Speed + span.LocalOffset * clip.Speed;
            Inputs.AddRange(["-ss", F(Math.Max(0, sourceStart)), "-t", F(span.Length * clip.Speed), "-i", media.Path]);
            _mediaInputs[key] = InputCount;
            return InputCount++;
        }

        /// <summary>A still image repeated for <paramref name="length"/> seconds.</summary>
        public int AddStillInput(string path, double length)
        {
            Inputs.AddRange(["-loop", "1", "-framerate", F(Fps), "-t", F(length), "-i", path]);
            return InputCount++;
        }
    }

    private static int Even(int value) => Math.Max(2, value & ~1);

    private static string Signed(double value) => value >= 0 ? "+" + F(Math.Round(value)) : F(Math.Round(value));
}
