using MyVideoEditor.Core.Editing;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.Core.Tests;

/// <summary>End-to-end: build projects, export them with ffmpeg, and check the pixels and sound of the result.</summary>
public class RenderTests
{
    private static readonly (int, int, int) Red = (255, 0, 0);
    private static readonly (int, int, int) Blue = (0, 0, 255);
    private static readonly (int, int, int) Black = (0, 0, 0);

    private static ProjectSettings Small => new() { Width = 320, Height = 240, FrameRate = 30 };

    private static async Task<(EditSession Session, string MediaId)> SessionWith(string file)
    {
        var session = new EditSession(Project.CreateDefault("test", Small));
        var info = await MediaProbe.Default.ProbeAsync(file);
        return (session, session.Editor.ImportMedia(info));
    }

    private static async Task<string> ImportAsync(EditSession session, string file) =>
        session.Editor.ImportMedia(await MediaProbe.Default.ProbeAsync(file));

    private static async Task<string> ExportAsync(EditSession session, OutputFormat? format = null, ExportSettings? settings = null)
    {
        var dir = TestMedia.NewOutputDirectory();
        var path = Path.Combine(dir, "out" + (format ?? OutputFormats.Mp4H264).Extension);
        await new ProjectExporter().ExportAsync(session.Project, path, settings ?? new ExportSettings { Format = format ?? OutputFormats.Mp4H264 });
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(dir, "out.partial.mp4")));
        return path;
    }

    [Fact]
    public async Task Probe_reads_video_and_audio_details()
    {
        var info = await MediaProbe.Default.ProbeAsync(await TestMedia.PatternAsync(4, 640, 360));
        Assert.Equal(MediaKind.Video, info.Kind);
        Assert.Equal(640, info.Width);
        Assert.Equal(360, info.Height);
        Assert.Equal(30, info.FrameRate, 1);
        Assert.InRange(info.Duration, 3.9, 4.2);
        Assert.True(info.HasAudio);

        var image = await MediaProbe.Default.ProbeAsync(await TestMedia.ImageAsync("green"));
        Assert.Equal(MediaKind.Image, image.Kind);
        Assert.Equal(0, image.Duration);

        var tone = await MediaProbe.Default.ProbeAsync(await TestMedia.ToneAsync(2));
        Assert.Equal(MediaKind.Audio, tone.Kind);
        Assert.False(tone.HasVideo);
    }

    [Fact]
    public async Task Clips_play_in_sequence_with_the_background_in_gaps()
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 2));
        var blue = await ImportAsync(session, await TestMedia.ColorAsync("blue", 2));
        session.Editor.AddMediaClip(red, 0);
        session.Editor.AddMediaClip(blue, 3); // a one-second gap at 2..3

        var output = await ExportAsync(session);
        var info = await MediaProbe.Default.ProbeAsync(output);
        Assert.InRange(info.Duration, 4.9, 5.15);
        Assert.Equal(320, info.Width);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1, 160, 120), Red));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 2.5, 160, 120), Black));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 4, 160, 120), Blue));
    }

    [Fact]
    public async Task Upper_tracks_are_drawn_over_lower_ones_and_fit_keeps_aspect_ratio()
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 2));
        // A 160x240 (tall) blue clip on the top track is pillarboxed in the middle, showing red at the sides.
        var tall = await ImportAsync(session, await TestMedia.ColorAsync("blue", 2, 160, 240, audio: false));
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        var v2 = session.Project.Tracks.First(t => t.Name == "V2").Id;
        session.Editor.AddMediaClip(red, 0, v1);
        session.Editor.AddMediaClip(tall, 0, v2);

        var output = await ExportAsync(session);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1, 160, 120), Blue));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1, 20, 120), Red));
    }

    [Fact]
    public async Task Titles_and_annotations_render()
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 2));
        session.Editor.AddMediaClip(red, 0);
        session.Editor.AddTitle(0, new TitleClip { Text = "Hello", FontSize = 60, Color = "#FFFFFF", Shadow = false, BackgroundColor = "", Duration = 2, Y = 0.5 });
        session.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Highlight, Color = "#00FF00", Strength = 1, X1 = 0, Y1 = 0, X2 = 0.2, Y2 = 0.2, Duration = 2 });

        var output = await ExportAsync(session);
        var (width, height, rgb) = TestMedia.Frame(output, 1);
        // Some white text pixels around the middle.
        var white = 0;
        for (var y = height / 2 - 20; y < height / 2 + 20; y++)
            for (var x = 60; x < 260; x++)
            {
                var i = (y * width + x) * 3;
                if (rgb[i] > 220 && rgb[i + 1] > 220 && rgb[i + 2] > 220)
                    white++;
            }
        Assert.True(white > 100, $"expected title text, found {white} white pixels");
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1, 20, 20), (0, 255, 0)));
    }

    [Theory]
    // Incoming clip has 1.5s of footage before its in-point: the dissolve happens before the cut (1..2).
    [InlineData(3.5, 1.5, 0.5, 2.5)]
    // Incoming clip has only 0.5s: the outgoing clip runs on and dissolves after the cut (2..3).
    [InlineData(2.5, 2.5, 1.5, 3.5)]
    public async Task Dissolves_blend_between_clips(double blueTrimTo, double mixAt, double redAt, double blueAt)
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 3));
        var blue = await ImportAsync(session, await TestMedia.ColorAsync("blue", 5));
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        session.Editor.AddMediaClip(red, 0, v1);
        var b = session.Editor.AddMediaClip(blue, 2, v1); // same track: overwrites the red clip from 2s
        // Trim the blue clip's start (giving it footage before its in-point), then put it back at 2s.
        session.Editor.TrimStart(b, blueTrimTo);
        session.Editor.MoveClip(b, 2);
        session.Editor.UpdateClip(b, "dissolve", c => c.TransitionIn = 1);

        var output = await ExportAsync(session);
        var mid = TestMedia.PixelAt(output, mixAt, 160, 120);
        Assert.InRange(mid.R, 70, 190);
        Assert.InRange(mid.B, 70, 190);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, redAt, 160, 120), Red));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, blueAt, 160, 120), Blue));
    }

    [Fact]
    public async Task Dissolves_between_whole_clips_hold_the_last_frame()
    {
        // Neither clip has footage to spare beyond the cut: the red clip's last frame is held and dissolves into blue.
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 3));
        var blue = await ImportAsync(session, await TestMedia.ColorAsync("blue", 3));
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        session.Editor.AddMediaClip(red, 0, v1);
        var b = session.Editor.AddMediaClip(blue, 3, v1);
        session.Editor.UpdateClip(b, "dissolve", c => c.TransitionIn = 1);

        var output = await ExportAsync(session);
        Assert.InRange(TestMedia.FrameCount(output), 179, 181); // the timeline's length is unchanged (6 s at 30 fps)
        var mid = TestMedia.PixelAt(output, 3.5, 160, 120);
        Assert.InRange(mid.R, 70, 190);
        Assert.InRange(mid.B, 70, 190);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 2.5, 160, 120), Red));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 4.5, 160, 120), Blue));

        // The editor's preview frames show it too, including one taken part-way through the dissolve.
        using var engine = new MyVideoEditor.Core.Playback.PlaybackEngine();
        using var frame = await engine.RenderStillAsync(session.Project, 3.5, 160, 120);
        var i = (60 * frame!.Width + 80) * 4; // BGRA
        Assert.InRange(frame.Pixels[i + 2], 70, 190);
        Assert.InRange(frame.Pixels[i], 70, 190);
    }

    [Fact]
    public async Task Dissolves_hold_the_last_picture_when_the_sound_runs_longer()
    {
        // As in many downloaded videos: the red file's sound runs a second past its picture.
        var (session, red) = await SessionWith(await TestMedia.ColorWithLongerSoundAsync("red", 3, 1));
        var blue = await ImportAsync(session, await TestMedia.ColorAsync("blue", 3));
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        var a = session.Editor.AddMediaClip(red, 0, v1);
        var end = session.Project.FindClip(a)!.Value.Clip.End;
        var b = session.Editor.AddMediaClip(blue, end, v1);
        session.Editor.UpdateClip(b, "dissolve", c => c.TransitionIn = 1);

        var output = await ExportAsync(session);
        var mid = TestMedia.PixelAt(output, end + 0.5, 160, 120);
        Assert.InRange(mid.R, 70, 190);
        Assert.InRange(mid.B, 70, 190);
    }

    /// <summary>Red (0–3 s) then blue (3–6 s) on one track, with a 1 s transition between them.</summary>
    private static async Task<EditSession> TwoClipsWith(TransitionStyle style, TransitionDirection direction = TransitionDirection.Left, bool blueHasHandle = false)
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 3));
        var blue = await ImportAsync(session, await TestMedia.ColorAsync("blue", blueHasHandle ? 5 : 3));
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        session.Editor.AddMediaClip(red, 0, v1);
        var b = session.Editor.AddMediaClip(blue, 3, v1);
        if (blueHasHandle)
        {
            // Footage before its in-point, so the transition happens before the cut (2–3 s).
            session.Editor.TrimStart(b, 4);
            session.Editor.MoveClip(b, 3);
            session.Editor.TrimEnd(b, 6);
        }
        session.Editor.UpdateClip(b, "transition", c =>
        {
            c.TransitionIn = 1;
            c.TransitionStyle = style;
            c.TransitionDirection = direction;
        });
        return session;
    }

    public static TheoryData<TransitionStyle> AllStyles() => [.. Enum.GetValues<TransitionStyle>()];

    [Theory]
    [MemberData(nameof(AllStyles))]
    public async Task Every_transition_renders_and_keeps_the_timing(TransitionStyle style)
    {
        var session = await TwoClipsWith(style);
        var output = await ExportAsync(session);
        Assert.InRange(TestMedia.FrameCount(output), 179, 181);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 2.5, 160, 120), Red));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 4.5, 160, 120), Blue));
        // A preview frame part-way through (its own short render) works too.
        using var engine = new MyVideoEditor.Core.Playback.PlaybackEngine();
        using var frame = await engine.RenderStillAsync(session.Project, 3.5, 160, 120);
        Assert.NotNull(frame);
    }

    [Theory]
    [InlineData(false, 3.5)] // whole clips: after the cut, from red's held last frame
    [InlineData(true, 2.5)]  // blue has footage to spare: before the cut
    public async Task Wipes_move_across_the_picture(bool blueHasHandle, double middle)
    {
        // Wipe left: the edge moves leftwards, so blue comes in from the right.
        var session = await TwoClipsWith(TransitionStyle.Wipe, TransitionDirection.Left, blueHasHandle);
        var output = await ExportAsync(session);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, middle, 40, 120), Red), $"left {TestMedia.PixelAt(output, middle, 40, 120)}");
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, middle, 280, 120), Blue), $"right {TestMedia.PixelAt(output, middle, 280, 120)}");
        // The same in a preview frame taken at that moment.
        using var engine = new MyVideoEditor.Core.Playback.PlaybackEngine();
        using var frame = await engine.RenderStillAsync(session.Project, middle, 320, 240);
        int R(int x) => frame!.Pixels[(120 * frame.Width + x) * 4 + 2];
        int B(int x) => frame!.Pixels[(120 * frame.Width + x) * 4];
        Assert.True(R(40) > 200 && B(40) < 60, $"preview left {R(40)},{B(40)}");
        Assert.True(B(280) > 200 && R(280) < 60, $"preview right {R(280)},{B(280)}");
    }

    [Fact]
    public async Task Dip_to_black_goes_through_black()
    {
        var output = await ExportAsync(await TwoClipsWith(TransitionStyle.DipToBlack));
        // xfade's curve reaches black about a third of the way through, then brings the next clip up.
        var mid = TestMedia.PixelAt(output, 3.25, 160, 120);
        Assert.True(mid.R + mid.G + mid.B < 60, $"middle {mid}");
    }

    [Fact]
    public async Task Fade_in_starts_from_the_background()
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 2));
        var clip = session.Editor.AddMediaClip(red, 0);
        session.Editor.UpdateClip(clip, "fade", c => c.FadeIn = 1);
        var output = await ExportAsync(session);
        Assert.InRange(TestMedia.PixelAt(output, 0.5, 160, 120).R, 70, 190);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1.5, 160, 120), Red));

        // A preview starting part-way into the fade picks up at the right level.
        var part = await ExportAsync(session, settings: new ExportSettings { Format = OutputFormats.Mp4H264, RangeStart = 0.5, RangeEnd = 1.5 });
        Assert.InRange(TestMedia.PixelAt(part, 0.0, 160, 120).R, 70, 190);
    }

    [Fact]
    public async Task Audio_is_mixed_muted_and_delayed()
    {
        var (session, tone) = await SessionWith(await TestMedia.ToneAsync(1));
        var silentVideo = await ImportAsync(session, await TestMedia.ColorAsync("red", 3, audio: false));
        session.Editor.AddMediaClip(silentVideo, 0);
        session.Editor.AddMediaClip(tone, 1.5);

        var output = await ExportAsync(session);
        Assert.True(TestMedia.MeanVolume(output, 0.2, 1.3) < -60, "silence before the tone");
        Assert.True(TestMedia.MeanVolume(output, 1.6, 2.4) > -30, "tone where it was placed");

        var audioTrack = session.Project.Tracks.First(t => t.Kind == TrackKind.Audio).Id;
        session.Editor.UpdateTrack(audioTrack, "mute", t => t.Muted = true);
        var muted = await ExportAsync(session);
        Assert.True(TestMedia.MeanVolume(muted, 1.6, 2.4) < -60, "muted track is silent");
    }

    [Fact]
    public async Task Speed_changes_duration_and_ranges_export_part_of_the_timeline()
    {
        var (session, pattern) = await SessionWith(await TestMedia.PatternAsync(4, 320, 240));
        var clip = session.Editor.AddMediaClip(pattern, 0);
        session.Editor.UpdateClip(clip, "speed", c =>
        {
            var m = (MediaClip)c;
            m.Speed = 2;
            m.Duration /= 2;
        });
        var fast = await ExportAsync(session);
        Assert.InRange((await MediaProbe.Default.ProbeAsync(fast)).Duration, 1.9, 2.15);

        var part = await ExportAsync(session, settings: new ExportSettings { Format = OutputFormats.Mp4H264, RangeStart = 0.5, RangeEnd = 1.5, Width = 160, Height = 120 });
        var info = await MediaProbe.Default.ProbeAsync(part);
        Assert.InRange(info.Duration, 0.95, 1.1);
        Assert.Equal(160, info.Width);
    }

    [Fact]
    public async Task Blur_pixelate_and_zoom_annotations_render()
    {
        var (session, pattern) = await SessionWith(await TestMedia.PatternAsync(2, 320, 240));
        session.Editor.AddMediaClip(pattern, 0);
        session.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Blur, X1 = 0.1, Y1 = 0.1, X2 = 0.4, Y2 = 0.4, Start = 0, Duration = 1 });
        session.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Pixelate, X1 = 0.5, Y1 = 0.5, X2 = 0.9, Y2 = 0.9, Start = 0, Duration = 1 });
        session.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Zoom, X1 = 0.25, Y1 = 0.25, X2 = 0.75, Y2 = 0.75, Start = 1, Duration = 1 });
        var output = await ExportAsync(session);
        Assert.InRange((await MediaProbe.Default.ProbeAsync(output)).Duration, 1.9, 2.15);
    }

    [Fact]
    public async Task Effects_images_and_gif_export_work()
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 1));
        var image = await ImportAsync(session, await TestMedia.ImageAsync("blue"));
        var clip = session.Editor.AddMediaClip(red, 0);
        session.Editor.UpdateClip(clip, "bw", c => c.Effects.Add(EffectCatalog.Find("grayscale")!.CreateDefault()));
        var still = session.Editor.AddMediaClip(image, 1);
        session.Editor.TrimEnd(still, 2);

        var output = await ExportAsync(session);
        var grey = TestMedia.PixelAt(output, 0.5, 160, 120);
        Assert.True(Math.Abs(grey.R - grey.G) < 20 && Math.Abs(grey.G - grey.B) < 20, $"grayscale, got {grey}");
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1.5, 160, 120), Blue));

        var gif = await ExportAsync(session, OutputFormats.Gif);
        Assert.Equal("gif", (await MediaProbe.Default.ProbeAsync(gif)).VideoCodec);
    }

    [Fact]
    public async Task Long_timelines_pass_the_graph_in_a_file()
    {
        var (session, pattern) = await SessionWith(await TestMedia.PatternAsync(4, 320, 240));
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        for (var i = 0; i < 40; i++)
        {
            var id = session.Editor.AddMediaClip(pattern, i * 0.1, v1);
            session.Editor.TrimEnd(id, i * 0.1 + 0.1);
        }
        var plan = RenderPlanBuilder.Build(session.Project);
        Assert.True(plan.FilterGraph.Length > 6000, $"graph is only {plan.FilterGraph.Length} characters");
        var output = await ExportAsync(session);
        Assert.InRange((await MediaProbe.Default.ProbeAsync(output)).Duration, 3.9, 4.15);
    }

    [Fact]
    public async Task Frame_export_writes_a_png()
    {
        var (session, red) = await SessionWith(await TestMedia.ColorAsync("red", 1));
        session.Editor.AddMediaClip(red, 0);
        var path = Path.Combine(TestMedia.NewOutputDirectory(), "frame.png");
        await new ProjectExporter().ExportFrameAsync(session.Project, 0.5, path);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(path, 0, 10, 10), Red));
    }
}
