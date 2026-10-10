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

    /// <summary>A 3 s title over 3 s of black: a green box (fully opaque) with white text, in the middle.</summary>
    private static async Task<EditSession> TitleSession(Action<TitleClip> setUp)
    {
        var (session, black) = await SessionWith(await TestMedia.ColorAsync("black", 3));
        session.Editor.AddMediaClip(black, 0);
        var title = new TitleClip { Text = "Hello", FontSize = 150, Shadow = false, BoxColor = "#00FF00", BoxOpacity = 1, Duration = 3 };
        setUp(title);
        session.Editor.AddTitle(0, title);
        return session;
    }

    private static int Count(string video, double time, Func<byte, byte, byte, bool> match)
    {
        var (width, height, rgb) = TestMedia.Frame(video, time);
        var count = 0;
        for (var i = 0; i < width * height; i++)
            if (match(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]))
                count++;
        return count;
    }

    private static int Green(string video, double time) => Count(video, time, (r, g, b) => g > 180 && r < 80 && b < 80);

    [Fact]
    public async Task Titles_slide_in_from_the_side()
    {
        var session = await TitleSession(t => { t.AnimationIn = TitleMotion.SlideLeft; t.AnimationInDuration = 1; });
        var output = await ExportAsync(session);
        Assert.InRange(TestMedia.FrameCount(output), 89, 91);
        // At first the box is off the left edge, then it moves in (quickly at first, easing into place).
        var (early, settled) = (Green(output, 0), Green(output, 1.5));
        Assert.True(early < settled / 4 && settled > 500, $"green pixels {early} → {settled}");
        int Rightmost(double time)
        {
            var (width, height, rgb) = TestMedia.Frame(output, time);
            var right = -1;
            for (var i = 0; i < width * height; i++)
                if (rgb[i * 3 + 1] > 180 && rgb[i * 3] < 80 && rgb[i * 3 + 2] < 80)
                    right = Math.Max(right, i % width);
            return right;
        }
        Assert.True(Rightmost(0.1) < Rightmost(0.3) && Rightmost(0.3) < Rightmost(1.5), $"edge at {Rightmost(0.1)}, {Rightmost(0.3)}, {Rightmost(1.5)}");
        // A preview frame part-way through shows the same as the export.
        using var engine = new MyVideoEditor.Core.Playback.PlaybackEngine();
        int PreviewGreen(double time)
        {
            using var f = engine.RenderStillAsync(session.Project, time, 320, 240).GetAwaiter().GetResult()!;
            var n = 0;
            for (var i = 0; i < f.Width * f.Height; i++)
                if (f.Pixels[i * 4 + 1] > 180 && f.Pixels[i * 4] < 80 && f.Pixels[i * 4 + 2] < 80)
                    n++;
            return n;
        }
        var (previewEarly, previewSettled) = (PreviewGreen(0), PreviewGreen(1.5));
        Assert.True(previewEarly < previewSettled / 4 && Math.Abs(previewSettled - settled) < settled / 5, $"preview {previewEarly} → {previewSettled}");
    }

    [Fact]
    public async Task Typewriter_titles_build_up_the_text()
    {
        var session = await TitleSession(t => { t.BoxColor = ""; t.Text = "Hello there"; t.FontSize = 100; t.AnimationIn = TitleMotion.Typewriter; t.AnimationInDuration = 2; });
        var output = await ExportAsync(session);
        int White(double time)
        {
            var (width, height, frame) = TestMedia.Frame(output, time);
            var count = 0;
            for (var i = 0; i < width * height; i++)
                if (frame[i * 3] > 200 && frame[i * 3 + 1] > 200 && frame[i * 3 + 2] > 200)
                    count++;
            return count;
        }
        var (a, b, c) = (White(0.5), White(1.2), White(2.5));
        Assert.True(a < b && b < c && a > 0, $"white pixels {a}, {b}, {c}");
    }

    [Fact]
    public async Task Titles_fade_out_at_the_end()
    {
        var session = await TitleSession(t => { t.AnimationOut = TitleMotion.Rise; t.AnimationOutDuration = 1; });
        var output = await ExportAsync(session);
        var (before, late) = (Green(output, 1.5), Green(output, 2.95));
        Assert.True(before > 500 && late < before / 10, $"green pixels {before} → {late}");
    }

    [Theory]
    [InlineData(TitleImagePlacement.Above, 160, 80)]
    [InlineData(TitleImagePlacement.Background, 10, 10)]
    public async Task Titles_show_pictures(TitleImagePlacement placement, int x, int y)
    {
        var picture = await TestMedia.ImageAsync("red", 200, 100);
        var session = await TitleSession(t => { t.BoxColor = ""; t.ImagePath = picture; t.ImagePlacement = placement; t.ImageSize = 300; });
        var output = await ExportAsync(session);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1.5, x, y), Red), $"{placement}: {TestMedia.PixelAt(output, 1.5, x, y)}");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // a .lottie archive
    public async Task Lottie_files_import_as_animations(bool archive)
    {
        var info = await MediaProbe.Default.ProbeAsync(TestMedia.Lottie(archive));
        Assert.Equal(MediaKind.Animation, info.Kind);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.Equal(2, info.Duration, 2);
        Assert.Equal(30, info.FrameRate, 1);
        Assert.True(info.HasVideo);
        Assert.False(info.HasAudio);
        Assert.False(LottieMedia.IsLottie(await TestMedia.ImageAsync("red")));
    }

    [Fact]
    public void Lottie_text_layers_can_be_given_new_text()
    {
        var json = LottieMedia.ReadJson(TestMedia.Lottie());
        Assert.Equal([("Title", "Hello")], LottieMedia.TextLayers(json));
        var changed = LottieMedia.WithText(json, new Dictionary<string, string> { ["Title"] = "Welcome\nback" });
        Assert.Equal([("Title", "Welcome\nback")], LottieMedia.TextLayers(changed));
        Assert.Same(json, LottieMedia.WithText(json, new Dictionary<string, string>()));
        // Pictures inside an animation are detected (they aren't drawn, so the user is told).
        Assert.False(LottieMedia.HasPictures(json));
        Assert.True(LottieMedia.HasPictures(LottieMedia.ReadJson(TestMedia.LottieWithPicture())));
    }

    [Fact]
    public async Task Lottie_animations_play_on_the_timeline_with_their_own_text()
    {
        var (session, black) = await SessionWith(await TestMedia.ColorAsync("black", 2));
        session.Editor.AddMediaClip(black, 0);
        var animation = await ImportAsync(session, TestMedia.Lottie());
        var v2 = session.Project.Tracks.First(t => t.Name == "V2").Id;
        var clip = session.Editor.AddMediaClip(animation, 0, v2);
        var output = await ExportAsync(session);
        Assert.InRange(TestMedia.FrameCount(output), 59, 61);
        // The green square moves left to right.
        double MeanX(double time)
        {
            var (width, height, rgb) = TestMedia.Frame(output, time);
            var (sum, n) = (0.0, 0);
            for (var i = 0; i < width * height; i++)
                if (rgb[i * 3 + 1] > 180 && rgb[i * 3] < 80 && rgb[i * 3 + 2] < 80)
                    (sum, n) = (sum + i % width, n + 1);
            return n == 0 ? double.NaN : sum / n;
        }
        var (early, late) = (MeanX(0.2), MeanX(1.8));
        Assert.True(early < 100 && late > 220, $"square at {early:0} then {late:0}");

        // New text for the "Title" layer (when this computer has the layer's font family).
        int White(string video) => Count(video, 1, (r, g, b) => r > 200 && g > 200 && b > 200);
        var before = White(output);
        session.Editor.UpdateClip(clip, "text", c => ((MediaClip)c).TextOverrides["Title"] = "Hello and welcome");
        var changed = await ExportAsync(session);
        if (before > 0)
            Assert.True(White(changed) > before * 1.5, $"white pixels {before} → {White(changed)}");

        // Preview frames and timeline thumbnails come from the same drawing.
        using var engine = new MyVideoEditor.Core.Playback.PlaybackEngine();
        using var frame = await engine.RenderStillAsync(session.Project, 1, 160, 120);
        Assert.NotNull(frame);
        var strip = await new MediaPreviews(cacheDirectory: TestMedia.NewOutputDirectory()).GetFilmstripAsync(TestMedia.Lottie(), 2, false);
        Assert.NotNull(strip);
    }

    /// <summary>Where white-ish (or yellow, for a crawl) pixels are: their count, mean x and y, and the x-extent of the top and bottom rows that have any.</summary>
    private static (int Count, double X, double Y, int TopWidth, int BottomWidth) Text(string video, double time, bool yellow = false)
    {
        var (width, height, rgb) = TestMedia.Frame(video, time);
        var (n, sx, sy) = (0, 0.0, 0.0);
        var rows = new Dictionary<int, (int Min, int Max)>();
        for (var i = 0; i < width * height; i++)
        {
            var (r, g, b) = (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            if (yellow ? r > 150 && g > 120 && b < 90 : r > 200 && g > 200 && b > 200)
            {
                var (x, y) = (i % width, i / width);
                (n, sx, sy) = (n + 1, sx + x, sy + y);
                rows[y] = rows.TryGetValue(y, out var e) ? (Math.Min(e.Min, x), Math.Max(e.Max, x)) : (x, x);
            }
        }
        if (n == 0)
            return (0, double.NaN, double.NaN, 0, 0);
        var top = rows[rows.Keys.Min()];
        var bottom = rows[rows.Keys.Max()];
        return (n, sx / n, sy / n, top.Max - top.Min, bottom.Max - bottom.Min);
    }

    private static async Task<EditSession> ScrollSession(TitleScroll scroll, string text, string background = "", string color = "#FFFFFF")
    {
        var (session, black) = await SessionWith(await TestMedia.ColorAsync("black", 4));
        session.Editor.AddMediaClip(black, 0);
        session.Editor.AddTitle(0, new TitleClip
        {
            Text = text, FontSize = 70, Shadow = false, Duration = 4, Scroll = scroll, BackgroundColor = background, Color = color, Y = 0.85,
        });
        return session;
    }

    [Fact]
    public async Task Credits_roll_up_through_the_frame()
    {
        var credits = "THE END\n\nDirected by | Jane Doe\nEdited by | John Smith\nMusic | The Band\n\nThanks for watching";
        var session = await ScrollSession(TitleScroll.Credits, credits, background: "#0000FF");
        var output = await ExportAsync(session);
        Assert.InRange(TestMedia.FrameCount(output), 119, 121);
        var (start, early, middle, late, end) = (Text(output, 0), Text(output, 1.2), Text(output, 2), Text(output, 2.8), Text(output, 3.95));
        // From below the frame, up through it, and off the top: nothing at either end.
        Assert.True(start.Count == 0 && end.Count == 0, $"text at the ends: {start.Count}, {end.Count}");
        Assert.True(early.Y > middle.Y && middle.Y > late.Y, $"text at y {early.Y:0}, {middle.Y:0}, {late.Y:0}");
        // The background stays put.
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 0, 5, 5), (0, 0, 255), 30));
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 3.95, 5, 5), (0, 0, 255), 30));
        // A preview frame part-way through matches the export.
        using var engine = new MyVideoEditor.Core.Playback.PlaybackEngine();
        using var frame = await engine.RenderStillAsync(session.Project, 2, 320, 240);
        var (n, sy) = (0, 0.0);
        for (var i = 0; i < frame!.Width * frame.Height; i++)
            if (frame.Pixels[i * 4] > 200 && frame.Pixels[i * 4 + 1] > 200 && frame.Pixels[i * 4 + 2] > 200)
                (n, sy) = (n + 1, sy + i / frame.Width);
        Assert.True(n > 0 && Math.Abs(sy / n - middle.Y) < 12, $"preview text at y {sy / Math.Max(1, n):0}, export {middle.Y:0}");
    }

    [Fact]
    public async Task Credits_lines_with_a_bar_form_two_columns()
    {
        var session = await ScrollSession(TitleScroll.Credits, "Director | Jane Doe");
        var output = await ExportAsync(session);
        var (width, height, rgb) = TestMedia.Frame(output, 2);
        var (left, right) = (0, 0);
        for (var i = 0; i < width * height; i++)
            if (rgb[i * 3] > 150 && rgb[i * 3 + 1] > 150 && rgb[i * 3 + 2] > 150)
            {
                if (i % width < width / 2 - 4) left++;
                if (i % width > width / 2 + 4) right++;
            }
        Assert.True(left > 20 && right > 20, $"left {left}, right {right}");
        // No "|" drawn between them: the middle column is empty.
        for (var y = 0; y < height; y++)
            Assert.True(rgb[(y * width + width / 2) * 3] < 150, $"something drawn in the middle at y {y}");
    }

    [Fact]
    public async Task Crawls_recede_into_the_distance()
    {
        var crawl = string.Join(" ", Enumerable.Repeat("It is a period of civil war in the editor, and the titles scroll away.", 4));
        var session = await ScrollSession(TitleScroll.Crawl, crawl, color: "#FFE81F");
        var output = await ExportAsync(session);
        var (early, late) = (Text(output, 1.0, yellow: true), Text(output, 2.0, yellow: true));
        Assert.True(early.Count > 50 && late.Count > 50, $"yellow pixels {early.Count}, {late.Count}");
        Assert.True(late.Y < early.Y, $"moving up: y {early.Y:0} → {late.Y:0}");
        // Perspective: lines nearer the top are narrower.
        Assert.True(early.TopWidth < early.BottomWidth, $"top {early.TopWidth} px wide, bottom {early.BottomWidth}");
        // And it fades out before reaching the top of the frame.
        var (width, _, rgb) = TestMedia.Frame(output, 3.5);
        for (var i = 0; i < width * 15; i++)
            Assert.True(rgb[i * 3] < 120, "nothing bright at the very top");
    }

    [Fact]
    public async Task Tickers_run_right_to_left()
    {
        var session = await ScrollSession(TitleScroll.Ticker, "Breaking news\nTitles now scroll");
        var output = await ExportAsync(session);
        var (early, late) = (Text(output, 1), Text(output, 2.5));
        Assert.True(early.Count > 0 && late.Count > 0);
        Assert.True(late.X < early.X, $"x {early.X:0} → {late.X:0}");
        Assert.True(early.Y > 180, $"at the title's height (85%): y {early.Y:0}");
    }

    [Fact]
    public async Task Looping_tickers_run_on_seamlessly_at_a_steady_speed()
    {
        // A short text on a long clip: without looping it would pass once and leave the band empty.
        var (session, black) = await SessionWith(await TestMedia.ColorAsync("black", 12));
        session.Editor.AddMediaClip(black, 0);
        session.Editor.AddTitle(0, new TitleClip
        {
            Text = "News", FontSize = 70, Shadow = false, Duration = 12, Scroll = TitleScroll.Ticker, Y = 0.5,
            ScrollLoop = true, ScrollSpeed = 400,
        });
        var output = await ExportAsync(session);
        // The first pass comes in from the right; after that the band is never empty.
        Assert.Equal(0, Text(output, 0).Count);
        var counts = Enumerable.Range(0, 20).Select(i => Text(output, 1 + i * 0.5).Count).ToList();
        Assert.All(counts, n => Assert.True(n > 0, $"an empty band: {string.Join(", ", counts)}"));
        // Steady speed: 400 px/s at 1080p is 400 × 240 / 1080 ≈ 89 px/s here, so ≈ 44 px in 0.5 s, late on as early on.
        int[] Columns(double time)
        {
            var (width, height, rgb) = TestMedia.Frame(output, time);
            var columns = new int[width];
            for (var i = 0; i < width * height; i++)
                if (rgb[i * 3] > 200)
                    columns[i % width]++;
            return columns;
        }
        int Shift(double time)
        {
            var (a, b) = (Columns(time), Columns(time + 0.5));
            return Enumerable.Range(0, 80).MinBy(d => Enumerable.Range(80, a.Length - 160).Sum(x => Math.Abs(a[x] - b[x - d])));
        }
        Assert.InRange(Shift(3), 38, 50);
        Assert.InRange(Shift(10), 38, 50);
    }

    [Fact]
    public async Task Speed_sets_how_fast_credits_roll()
    {
        // At a set speed, the same text reaches the same place at the same time whatever the clip's length.
        async Task<double> MiddleY(double length)
        {
            var (session, black) = await SessionWith(await TestMedia.ColorAsync("black", 4));
            session.Editor.AddMediaClip(black, 0);
            session.Editor.AddTitle(0, new TitleClip { Text = "Credits", FontSize = 70, Shadow = false, Duration = length, Scroll = TitleScroll.Credits, ScrollSpeed = 300 });
            return Text(await ExportAsync(session), 2).Y;
        }
        var (short4, long30) = (await MiddleY(4), await MiddleY(30));
        Assert.True(Math.Abs(short4 - long30) < 3, $"y {short4:0} and {long30:0}");
    }

    [Theory]
    [InlineData(TitleImagePlacement.Left, 12)]
    [InlineData(TitleImagePlacement.Right, 308)]
    public async Task Ticker_pictures_stay_at_one_end_of_the_band(TitleImagePlacement placement, int x)
    {
        var picture = await TestMedia.ImageAsync("red", 100, 100);
        var (session, black) = await SessionWith(await TestMedia.ColorAsync("black", 4));
        session.Editor.AddMediaClip(black, 0);
        session.Editor.AddTitle(0, new TitleClip
        {
            Text = "Breaking news\nTitles now scroll", FontSize = 70, Shadow = false, Duration = 4, Scroll = TitleScroll.Ticker, Y = 0.5,
            BoxColor = "#0000FF", BoxOpacity = 1, ImagePath = picture, ImagePlacement = placement, ImageSize = 60,
        });
        var output = await ExportAsync(session);
        // The picture is in the same place all the way through, while the text moves under it.
        foreach (var time in new[] { 0.5, 2.0, 3.5 })
            Assert.True(TestMedia.Near(TestMedia.PixelAt(output, time, x, 120), Red), $"{time} s: {TestMedia.PixelAt(output, time, x, 120)}");
        var (early, late) = (Text(output, 1), Text(output, 2.5));
        Assert.True(late.X < early.X, $"text moves: x {early.X:0} → {late.X:0}");
    }

    [Theory]
    [InlineData("8/9", 480, 640)]    // NTSC 4:3
    [InlineData("32/27", 480, 854)]  // NTSC 16:9
    [InlineData("16/15", 576, 768)]  // PAL 4:3
    [InlineData("64/45", 576, 1024)] // PAL 16:9
    public async Task Dvd_clips_are_seen_at_their_shown_size_and_remember_the_stored_one(string sar, int height, int shownWidth)
    {
        var info = await MediaProbe.Default.ProbeAsync(await TestMedia.DvdAsync(sar, height));
        Assert.Equal((shownWidth, height), (info.Width, info.Height));
        Assert.Equal(720, info.StoredWidth);
        Assert.Equal(0, (await MediaProbe.Default.ProbeAsync(await TestMedia.ColorAsync("red", 1))).StoredWidth);
    }

    [Fact]
    public async Task Dvd_projects_export_at_720_with_dvd_shaped_pixels()
    {
        var session = new EditSession(Project.CreateDefault("dvd", new ProjectSettings { Width = 640, Height = 480, OutputWidth = 720, FrameRate = 29.97 }));
        var red = await ImportAsync(session, await TestMedia.DvdAsync("8/9"));
        session.Editor.AddMediaClip(red, 0);
        var output = await ExportAsync(session);
        var info = await MediaProbe.Default.ProbeAsync(output);
        Assert.Equal((640, 480), (info.Width, info.Height)); // shown 4:3
        Assert.Equal(720, info.StoredWidth);                  // stored 720 across
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 0.5, 700, 240), Red), "filled to the edge, no bars");

        // Exported at another size, it's square pixels again.
        var smaller = await ExportAsync(session, settings: new ExportSettings { Format = OutputFormats.Mp4H264, Width = 480, Height = 360 });
        Assert.Equal(0, (await MediaProbe.Default.ProbeAsync(smaller)).StoredWidth);
        // The setting is kept with the project.
        Assert.Equal(720, ProjectSerializer.Deserialize(ProjectSerializer.Serialize(session.Project)).Settings.OutputWidth);
    }

    /// <summary>ffprobe's view of a file's first video and audio streams (key=value lines).</summary>
    private static Dictionary<string, string> Streams(string file)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(TestMedia.Locator.FfprobePath) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=format_name:stream=codec_type,codec_name,width,height,sample_aspect_ratio,display_aspect_ratio,r_frame_rate,sample_rate", "-of", "flat", file })
            psi.ArgumentList.Add(a);
        using var p = ChildProcess.Start(psi);
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        // Streams are numbered in file order (a DVD file starts with navigation data); name them by type.
        var raw = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => (Key: line[..line.IndexOf('=')], Value: line[(line.IndexOf('=') + 1)..].Trim('"'))).ToList();
        var types = raw.Where(r => r.Key.EndsWith(".codec_type")).ToDictionary(r => r.Key[..^"codec_type".Length], r => r.Value);
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in raw)
        {
            var prefix = types.Keys.FirstOrDefault(key.StartsWith);
            result[prefix != null ? types[prefix] + "." + key[prefix.Length..] : key.Replace("format.", "")] = value;
        }
        return result;
    }

    [Theory]
    [InlineData(1280, 720, 30, "720", "480", "32:27", "16:9", "30000/1001")] // a 720p 30 fps project: NTSC widescreen
    [InlineData(640, 480, 25, "720", "576", "16:15", "4:3", "25/1")]         // a 4:3 25 fps project: PAL 4:3
    public async Task Dvd_video_exports_meet_the_dvd_standard(int w, int h, double fps, string width, string height, string sar, string dar, string rate)
    {
        var session = new EditSession(Project.CreateDefault("dvd", new ProjectSettings { Width = w, Height = h, FrameRate = fps }));
        var red = await ImportAsync(session, await TestMedia.ColorAsync("red", 2, w, h));
        session.Editor.AddMediaClip(red, 0);
        var output = await ExportAsync(session, OutputFormats.DvdVideo);
        Assert.EndsWith(".mpg", output);
        var info = Streams(output);
        Assert.Equal("mpeg", info["format_name"]);
        Assert.Equal(("mpeg2video", width, height, sar, dar, rate),
            (info["video.codec_name"], info["video.width"], info["video.height"], info["video.sample_aspect_ratio"], info["video.display_aspect_ratio"], info["video.r_frame_rate"]));
        Assert.Equal(("ac3", "48000"), (info["audio.codec_name"], info["audio.sample_rate"]));
        // The picture fills DVD's frame (same shape, so no bars).
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 1, 4, 100), Red, 40), $"left edge {TestMedia.PixelAt(output, 1, 4, 100)}");
    }

    [Fact]
    public async Task Other_shapes_go_onto_a_dvd_with_bars_rather_than_stretched()
    {
        // A vertical video: pillarboxed into a 4:3 NTSC DVD, red in the middle, black at the sides.
        var session = new EditSession(Project.CreateDefault("tall", new ProjectSettings { Width = 360, Height = 640, FrameRate = 30 }));
        var red = await ImportAsync(session, await TestMedia.ColorAsync("red", 1, 360, 640));
        session.Editor.AddMediaClip(red, 0);
        var output = await ExportAsync(session, OutputFormats.DvdVideo);
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 0.5, 360, 240), Red, 40), $"middle {TestMedia.PixelAt(output, 0.5, 360, 240)}");
        Assert.True(TestMedia.Near(TestMedia.PixelAt(output, 0.5, 20, 240), (0, 0, 0), 30), $"side {TestMedia.PixelAt(output, 0.5, 20, 240)}");
        Assert.Equal((DvdVideo.StoredWidth, 480), ((await MediaProbe.Default.ProbeAsync(output)).StoredWidth, 480));
    }

    [Theory]
    [InlineData(1280, 720, 30, "16:9", 480)] // NTSC widescreen
    [InlineData(640, 480, 25, "4:3", 576)]   // PAL 4:3
    public async Task Dvd_folders_are_made_with_dvdauthor_when_installed(int w, int h, double fps, string aspect, int dvdHeight)
    {
        if (DvdVideo.AuthorProgram == null)
            return; // dvdauthor isn't installed here
        var session = new EditSession(Project.CreateDefault("disc", new ProjectSettings { Width = w, Height = h, FrameRate = fps }));
        var red = await ImportAsync(session, await TestMedia.ColorAsync("red", 2, w, h));
        session.Editor.AddMediaClip(red, 0);
        var dir = TestMedia.NewOutputDirectory();
        var mpeg = Path.Combine(dir, "Holiday.mpg");
        await new ProjectExporter().ExportAsync(session.Project, mpeg, new ExportSettings { Format = OutputFormats.DvdVideo, MakeDvdFolder = true });
        var folder = ProjectExporter.DvdFolderFor(mpeg);
        Assert.Equal(Path.Combine(dir, "Holiday DVD"), folder);
        Assert.True(File.Exists(Path.Combine(folder, "VIDEO_TS", "VIDEO_TS.IFO")));
        var vob = Assert.Single(Directory.GetFiles(Path.Combine(folder, "VIDEO_TS"), "VTS_01_*.VOB"));
        Assert.True(Directory.Exists(Path.Combine(folder, "AUDIO_TS")));
        // The disc's video is the DVD-standard stream.
        var info = Streams(vob);
        Assert.Equal(("mpeg2video", dvdHeight.ToString(), aspect, "ac3"), (info["video.codec_name"], info["video.height"], info["video.display_aspect_ratio"], info["audio.codec_name"]));
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
