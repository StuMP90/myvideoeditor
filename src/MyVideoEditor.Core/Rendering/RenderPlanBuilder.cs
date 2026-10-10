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
        /// <summary>The clip's last frame held still (a dissolve with no footage to spare either side of the cut). Video only.</summary>
        public bool HoldsLastFrame { get; init; }
        /// <summary>One side of a styled transition (drawn by it, not on its own); null for ordinary items.</summary>
        public StyledTransition? Transition { get; init; }
        /// <summary>(start, duration, fade-in?) in seconds since <see cref="RenderStart"/>.</summary>
        public List<(double Start, double Duration, bool In)> VideoFades { get; } = [];
        public List<(double Start, double Duration, bool In)> AudioFades { get; } = [];
    }

    /// <summary>
    /// A transition drawn with xfade over [Start, Start + Duration]: the outgoing and incoming clips are
    /// each rendered over the whole of it (even when only part is shown, as in a preview frame), blended,
    /// and the part in range laid over the track.
    /// </summary>
    internal sealed class StyledTransition
    {
        public required double Start { get; init; }
        public required double Duration { get; init; }
        public required string Xfade { get; init; }
        public Item Outgoing { get; set; } = null!;
        public Item Incoming { get; set; } = null!;
    }

    /// <summary>
    /// The clips of a track as render items, with transitions turned into overlaps and fades. With
    /// <paramref name="styled"/> (video, when ffmpeg has xfade), transitions other than dissolves become
    /// <see cref="StyledTransition"/>s: the clips either side stop at its edges and two side items cover it.
    /// Sound always crossfades.
    /// </summary>
    internal static List<Item> Items(Project project, Track track, bool styled = false)
    {
        var clips = track.Clips.OrderBy(c => c.Start).ToList();
        var items = clips.Select(c => new Item { Clip = c, Track = track, RenderStart = c.Start, RenderEnd = c.End }).ToList();
        var holds = new List<Item>();
        for (var i = 1; i < items.Count; i++)
        {
            var a = items[i - 1];
            var b = items[i];
            var d = Math.Min(b.Clip.TransitionIn, Math.Min(a.Clip.Duration, b.Clip.Duration));
            if (d < 0.01 || Math.Abs(a.Clip.End - b.Clip.Start) > 0.002)
                continue;
            var headB = HeadHandle(project, b.Clip);
            var tailA = TailHandle(project, a.Clip);
            var xfade = Transitions.XfadeName(b.Clip.TransitionStyle, b.Clip.TransitionDirection);
            if (styled && xfade != null && Drawable(a.Clip) && Drawable(b.Clip))
            {
                // Before the cut when the incoming clip has footage to spare; otherwise after it, from the
                // outgoing clip's footage after its end, or its last frame held.
                var before = headB >= d;
                if (before)
                    d = Math.Min(d, a.RenderEnd - a.RenderStart);
                var start = before ? a.Clip.End - d : a.Clip.End;
                var transition = new StyledTransition { Start = start, Duration = d, Xfade = xfade };
                transition.Outgoing = new Item
                {
                    Clip = a.Clip, Track = track, RenderStart = start, RenderEnd = start + d, Transition = transition,
                    HoldsLastFrame = !before && tailA < d,
                };
                transition.Incoming = new Item { Clip = b.Clip, Track = track, RenderStart = start, RenderEnd = start + d, Transition = transition };
                if (before)
                    a.RenderEnd = start;
                else
                    b.RenderStart = start + d;
                holds.Add(transition.Outgoing);
                holds.Add(transition.Incoming);
                continue;
            }
            if (headB < d && tailA < d)
            {
                // No footage to spare either side (two whole clips): hold the outgoing clip's last frame
                // and dissolve from it, as other editors do. Its sound fades out before the cut instead.
                var hold = new Item { Clip = a.Clip, Track = track, RenderStart = a.Clip.End, RenderEnd = a.Clip.End + d, OnTop = true, HoldsLastFrame = true };
                hold.VideoFades.Add((0, d, false));
                holds.Add(hold);
                a.AudioFades.Add((a.Clip.End - d - a.RenderStart, d, false));
            }
            else if (headB >= d)
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
        items.AddRange(holds);
        foreach (var item in items.Where(i => !i.HoldsLastFrame && i.Transition == null))
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

    /// <summary>Clips a styled transition can draw: footage, titles and drawn annotations (not blur and the like, which process what's below).</summary>
    private static bool Drawable(Clip clip) => clip is MediaClip or TitleClip || clip is AnnotationClip { IsFilterShape: false };

    /// <summary>Seconds of footage available before a clip's in-point (unlimited for stills and graphics).</summary>
    private static double HeadHandle(Project project, Clip clip) => clip switch
    {
        MediaClip m when project.FindMedia(m.MediaId) is { Kind: not MediaKind.Image } => m.InPoint / m.Speed,
        _ => double.MaxValue,
    };

    /// <summary>Seconds of footage available after a clip's out-point.</summary>
    private static double TailHandle(Project project, Clip clip) => clip switch
    {
        MediaClip m when project.FindMedia(m.MediaId) is { Kind: not MediaKind.Image } media =>
            Math.Max(0, (media.VideoEnd > 0 ? Math.Min(media.VideoEnd, media.Duration) : media.Duration) - m.OutPoint) / m.Speed,
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

        var styled = c.Options.Capabilities is not { Filters.Count: > 0 } caps || caps.HasFilter("xfade");
        // Bottom track first; within a track, outgoing dissolve clips last (on top).
        foreach (var track in Enumerable.Reverse(c.Project.Tracks).Where(t => t.Kind == TrackKind.Video && !t.Hidden))
        {
            if (!styled && track.Clips.Any(x => x.TransitionIn > 0 && x.TransitionStyle != TransitionStyle.Dissolve))
                c.Warn("This ffmpeg has no xfade filter, so transitions are shown as dissolves.");
            foreach (var item in Items(c.Project, track, styled).OrderBy(i => i.OnTop).ThenBy(i => i.RenderStart))
            {
                if (item.Transition is { } transition)
                {
                    if (item == transition.Outgoing)
                        current = AddStyledTransition(c, transition, current);
                    continue;
                }
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
        var speed = c.Options.Speed;
        c.Graph.Add(Math.Abs(speed - 1) < 1e-6
            ? $"[{current}]setsar=1[{output}]"
            : $"[{current}]setsar=1,setpts=PTS/{F(speed)},fps={F(c.Fps)}[{output}]");
        return output;
    }

    private static string AddMediaVideo(BuildContext c, Item item, MediaClip clip, Span span, string current)
    {
        if (MediaLayer(c, item, clip, span) is not var (layer, x, y))
            return current;
        var next = c.Label("c");
        c.Graph.Add($"[{current}][{layer}]overlay=x={x}:y={y}:eof_action=pass[{next}]");
        return next;
    }

    /// <summary>A clip's picture as a layer (its own size, timed for the output), with where it goes; null if there's none.</summary>
    private static (string Layer, string X, string Y)? MediaLayer(BuildContext c, Item item, MediaClip clip, Span span)
    {
        var media = c.Project.FindMedia(clip.MediaId);
        if (media == null || !media.HasVideo)
            return null;
        if (!File.Exists(media.Path))
        {
            c.Warn($"{media.Name} is missing ({media.Path}); its clips are left out.");
            return null;
        }
        var input = item.HoldsLastFrame ? c.AddLastFrameInput(clip) : c.AddMediaInput(item, clip, media, span);

        var chain = item.HoldsLastFrame
            ? new List<string>
            {
                // The last frame of the clip (the end of a short read, reversed), held for the item's length.
                "reverse,trim=end_frame=1",
                $"setpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB",
                $"tpad=stop_mode=clone:stop_duration={F(span.Length)}",
                $"fps={F(c.Fps)}",
                $"trim=end={F(span.LocalOffset + span.Length)}",
            }
            : new List<string>
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
        var x = $"(main_w-overlay_w)/2{Signed(clip.OffsetX * c.PixelScale)}";
        var y = $"(main_h-overlay_h)/2{Signed(clip.OffsetY * c.PixelScale)}";
        return (layer, x, y);
    }

    private static string AddGraphic(BuildContext c, Item item, Span span, string current)
    {
        if (GraphicLayer(c, item, span) is not var (layer, x, y))
            return current;
        var next = c.Label("c");
        c.Graph.Add($"[{current}][{layer}]overlay=x={x}:y={y}:eof_action=pass[{next}]");
        return next;
    }

    private static (string Layer, string X, string Y)? GraphicLayer(BuildContext c, Item item, Span span)
    {
        var graphics = c.Options.Graphics ?? GraphicsRenderer.Default;
        if (item.Clip is TitleClip { Scroll: not TitleScroll.None } scrolling)
            return ScrollLayer(c, item, scrolling, span, graphics);
        if (item.Clip is TitleClip { IsAnimated: true } title)
        {
            // Drawn frame by frame (full frames, only for the part shown), read as a sequence.
            var from = item.RenderStart - title.Start + span.LocalOffset;
            var list = graphics.RenderAnimatedTitle(title, c.Width, c.Height, c.Project.Settings.Width, c.Project.Settings.Height, c.Fps, from, from + span.Length);
            var sequence = c.AddSequenceInput(list);
            var steps = new List<string> { $"fps={F(c.Fps)}", "format=yuva420p", $"setpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB" };
            AddOpacityAndFades(item.Clip, item, steps);
            steps.Add($"setpts=PTS-STARTPTS+{F(span.OutputOffset)}/TB");
            var animated = c.Label("g");
            c.Graph.Add($"[{sequence}:v]{string.Join(',', steps)}[{animated}]");
            return (animated, "0", "0");
        }
        var rendered = graphics.Render(item.Clip, c.Width, c.Height, c.Project.Settings.Width, c.Project.Settings.Height);
        if (rendered == null)
            return null;
        var input = c.AddStillInput(rendered.Path, span.Length);
        var chain = new List<string> { "format=yuva420p", $"setpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB" };
        AddOpacityAndFades(item.Clip, item, chain);
        chain.Add($"setpts=PTS-STARTPTS+{F(span.OutputOffset)}/TB");
        var layer = c.Label("g");
        c.Graph.Add($"[{input}:v]{string.Join(',', chain)}[{layer}]");
        return (layer, rendered.X.ToString(), rendered.Y.ToString());
    }

    /// <summary>
    /// A scrolling title: its text strip (drawn once) with a frame-sized window moving along it over
    /// the clip's length, tilted back and faded into the distance for a crawl, over its still background.
    /// </summary>
    private static (string Layer, string X, string Y)? ScrollLayer(BuildContext c, Item item, TitleClip title, Span span, GraphicsRenderer graphics)
    {
        var (projectW, projectH) = (c.Project.Settings.Width, c.Project.Settings.Height);
        var strip = graphics.RenderScrollStrip(title, c.Width, c.Height, projectW, projectH);
        var background = graphics.RenderScrollBackground(title, c.Width, c.Height, projectW, projectH);
        if (strip == null && background == null)
            return null;
        var (w, h) = (c.Width, c.Height);
        // The image repeated as frames from 0 for the part shown; "t" is then seconds since that part began.
        string Repeated(int input, string format) =>
            $"[{input}:v]loop=loop=-1:size=1,setpts=N/{F(c.Fps)}/TB,trim=duration={F(span.Length)},format={format}";
        string? layer = null;
        if (strip != null)
        {
            var clipTime = item.RenderStart - title.Start + span.LocalOffset; // the clip's time at the first frame shown
            var length = Math.Max(0.01, title.Duration);
            // How far along the strip the window is: at a set speed (scaled to this frame size), or
            // all of it over the clip's length. A looping ticker goes round its repeating part after the first pass.
            string Travel(int distance)
            {
                var speed = title.ScrollSpeed > 0 ? title.ScrollSpeed : strip.Period > 0 ? TitleClip.DefaultTickerSpeed : 0;
                var moved = speed > 0
                    ? $"{F(speed * h / 1080.0)}*({F(clipTime)}+t)"
                    : $"{distance}*({F(clipTime)}+t)/{F(length)}";
                return strip.Period > 0
                    ? $"'if(lt({moved}\\,{w})\\,{moved}\\,{w}+mod({moved}-{w}\\,{strip.Period}))'"
                    : $"'min({distance}\\,max(0\\,{moved}))'";
            }
            var crop = title.Scroll == TitleScroll.Ticker
                ? $"crop=w={w}:h={h}:x={Travel(strip.Width - w)}:y=0"
                : $"crop=w={w}:h={h}:x=0:y={Travel(strip.Height - h)}";
            layer = c.Label("s");
            c.Graph.Add($"{Repeated(c.AddImageInput(strip.Path), "yuva420p")},{crop}[{layer}]");
            if (title.Scroll == TitleScroll.Crawl)
            {
                // Tilted back (narrow at the top, wider than the frame at the bottom), then faded towards the top.
                var tilt = c.Label("crawl");
                c.Graph.Add($"[{layer}]perspective=x0={F(w * 0.36)}:y0={F(h * 0.1)}:x1={F(w * 0.64)}:y1={F(h * 0.1)}" +
                            $":x2={F(-w * 0.2)}:y2={h}:x3={F(w * 1.2)}:y3={h}:sense=destination,format=yuva420p,split[{tilt}c][{tilt}a]");
                c.Graph.Add($"{Repeated(c.AddImageInput(graphics.RenderCrawlMask(w, h)), "gray")}[{tilt}m]");
                c.Graph.Add($"[{tilt}a]alphaextract[{tilt}x]");
                c.Graph.Add($"[{tilt}x][{tilt}m]blend=all_mode=multiply[{tilt}f]");
                c.Graph.Add($"[{tilt}c][{tilt}f]alphamerge[{tilt}]");
                layer = tilt;
            }
        }
        if (background != null)
        {
            var canvas = c.Label("s");
            c.Graph.Add($"color=c=black@0:s={w}x{h}:r={F(c.Fps)}:d={F(span.Length)},format=yuva420p[{canvas}]");
            var still = c.Label("s");
            c.Graph.Add($"{Repeated(c.AddImageInput(background.Path), "yuva420p")}[{still}]");
            var behind = c.Label("s");
            c.Graph.Add($"[{canvas}][{still}]overlay=x={background.X}:y={background.Y}:format=auto,format=yuva420p[{behind}]");
            if (layer != null)
            {
                var both = c.Label("s");
                c.Graph.Add($"[{behind}][{layer}]overlay=0:0:eof_action=pass:format=auto,format=yuva420p[{both}]");
                layer = both;
            }
            else
            {
                layer = behind;
            }
        }
        // A ticker's picture stays at one end of the band, over the moving text.
        if (layer != null && graphics.RenderTickerCap(title, w, h, projectW, projectH) is { } cap)
        {
            var still = c.Label("s");
            c.Graph.Add($"{Repeated(c.AddImageInput(cap.Path), "yuva420p")}[{still}]");
            var capped = c.Label("s");
            c.Graph.Add($"[{layer}][{still}]overlay=x={cap.X}:y={cap.Y}:format=auto,format=yuva420p[{capped}]");
            layer = capped;
        }
        var steps = new List<string> { $"setpts=PTS-STARTPTS+{F(span.LocalOffset)}/TB" };
        AddOpacityAndFades(title, item, steps);
        steps.Add($"setpts=PTS-STARTPTS+{F(span.OutputOffset)}/TB");
        var timed = c.Label("g");
        c.Graph.Add($"[{layer}]{string.Join(',', steps)}[{timed}]");
        return (timed, "0", "0");
    }

    /// <summary>
    /// A styled transition: each side drawn over the whole transition onto a transparent full frame
    /// (so what's below shows through any space around it), blended by xfade, and the part in range
    /// laid over the track.
    /// </summary>
    private static string AddStyledTransition(BuildContext c, StyledTransition t, string current)
    {
        var shownStart = Math.Max(t.Start, c.RangeStart);
        var shownEnd = Math.Min(t.Start + t.Duration, c.RangeEnd);
        if (shownEnd - shownStart < 1e-4)
            return current;
        var whole = new Span(t.Start, t.Duration, 0, 0);
        string Side(Item item)
        {
            var layer = item.Clip is MediaClip m ? MediaLayer(c, item, m, whole) : GraphicLayer(c, item, whole);
            var canvas = c.Label("t");
            c.Graph.Add($"color=c=black@0:s={c.Width}x{c.Height}:r={F(c.Fps)}:d={F(t.Duration)},format=yuva420p[{canvas}]");
            if (layer is not var (l, x, y))
                return canvas;
            var side = c.Label("t");
            c.Graph.Add($"[{canvas}][{l}]overlay=x={x}:y={y}:eof_action=pass:format=auto,format=yuva420p[{side}]");
            return side;
        }
        var from = Side(t.Outgoing);
        var to = Side(t.Incoming);
        var blended = c.Label("x");
        c.Graph.Add($"[{from}][{to}]xfade=transition={t.Xfade}:duration={F(t.Duration)}:offset=0," +
                    $"trim=start={F(shownStart - t.Start)}:end={F(shownEnd - t.Start)}," +
                    $"setpts=PTS-STARTPTS+{F(shownStart - c.RangeStart)}/TB[{blended}]");
        var next = c.Label("c");
        c.Graph.Add($"[{current}][{blended}]overlay=x=0:y=0:eof_action=pass[{next}]");
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
                if (item.Clip is not MediaClip clip || item.HoldsLastFrame || !c.Visible(item, out var span))
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
        var tempo = string.Concat(Atempo(c.Options.Speed).Select(f => "," + f));
        if (layers.Count == 0)
        {
            c.Graph.Add($"[{silence}]anull{tempo}[{output}]");
        }
        else
        {
            var all = string.Concat(new[] { silence }.Concat(layers).Select(l => $"[{l}]"));
            c.Graph.Add($"{all}amix=inputs={layers.Count + 1}:duration=first:dropout_transition=0:normalize=0{tempo}[{output}]");
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
            if (media.Kind == MediaKind.Animation)
            {
                // Lottie: drawn frame by frame at the project's frame rate (in source time, as the speed is applied later).
                var count = Math.Max(1, (int)Math.Ceiling(span.Length * Fps - 1e-6));
                var list = LottieMedia.RenderSequence(media.Path, clip.TextOverrides, Math.Max(0, sourceStart), clip.Speed / Fps, count);
                _mediaInputs[key] = AddSequenceInput(list);
                return _mediaInputs[key];
            }
            Inputs.AddRange(["-ss", F(Math.Max(0, sourceStart)), "-t", F(span.Length * clip.Speed), "-i", media.Path]);
            _mediaInputs[key] = InputCount;
            return InputCount++;
        }

        /// <summary>
        /// The end of a clip's picture, to take its last frame from: the last half-second before the
        /// clip's out-point or the picture's end, whichever is first (the last 3 s when the picture's end
        /// isn't known, as in projects from before it was recorded).
        /// </summary>
        public int AddLastFrameInput(MediaClip clip)
        {
            var media = Project.FindMedia(clip.MediaId)!;
            var end = clip.OutPoint;
            if (media.VideoEnd > 0)
                end = Math.Min(end, media.VideoEnd);
            else if (media.Duration > 0)
                end = Math.Min(end, media.Duration);
            if (media.Kind == MediaKind.Animation)
                return AddSequenceInput(LottieMedia.RenderSequence(media.Path, clip.TextOverrides, end, 1 / Fps, 1));
            var window = Math.Min(media.VideoEnd > 0 ? 0.5 : 3, end);
            Inputs.AddRange(["-ss", F(Math.Max(0, end - window)), "-t", F(window), "-i", media.Path]);
            return InputCount++;
        }

        /// <summary>A single picture, read once (repeated by the graph's loop filter).</summary>
        public int AddImageInput(string path)
        {
            Inputs.AddRange(["-i", path]);
            return InputCount++;
        }

        /// <summary>A sequence of pictures listed in an ffconcat file (animated titles, Lottie animations).</summary>
        public int AddSequenceInput(string listPath)
        {
            Inputs.AddRange(["-f", "concat", "-safe", "0", "-i", listPath]);
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
