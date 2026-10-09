using MyVideoEditor.Core.Editing;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.Core.Tests;

public class EditingTests
{
    private static (EditSession Session, string Video, string Audio) NewSession()
    {
        var session = new EditSession(Project.CreateDefault("t", new ProjectSettings { FrameRate = 25 }));
        var video = session.Editor.ImportMedia(new MediaInfo { Path = "/media/video.mp4", Kind = MediaKind.Video, Duration = 10, HasVideo = true, HasAudio = true, Width = 1920, Height = 1080, FrameRate = 25 });
        var audio = session.Editor.ImportMedia(new MediaInfo { Path = "/media/music.mp3", Kind = MediaKind.Audio, Duration = 60, HasAudio = true });
        return (session, video, audio);
    }

    private static Track V1(EditSession s) => s.Project.Tracks.First(t => t.Name == "V1");

    [Fact]
    public void Media_goes_on_the_right_kind_of_track()
    {
        var (s, video, audio) = NewSession();
        var v = s.Editor.AddMediaClip(video, 0);
        var a = s.Editor.AddMediaClip(audio, 0);
        Assert.Equal(TrackKind.Video, s.Project.FindClip(v)!.Value.Track.Kind);
        Assert.Equal("V1", s.Project.FindClip(v)!.Value.Track.Name);
        Assert.Equal(TrackKind.Audio, s.Project.FindClip(a)!.Value.Track.Kind);
        Assert.Equal(60, s.Project.Duration); // the longest track
        Assert.Equal(60, s.Project.FindClip(a)!.Value.Clip.Duration);
    }

    [Fact]
    public void Placing_over_a_clip_overwrites_it()
    {
        var (s, video, _) = NewSession();
        var v1 = V1(s).Id;
        var first = s.Editor.AddMediaClip(video, 0, v1);       // 0..10
        s.Editor.AddMediaClip(video, 4, v1);                   // 4..14, trims the first to 0..4
        var clip = (MediaClip)s.Project.FindClip(first)!.Value.Clip;
        Assert.Equal(4, clip.Duration, 6);
        Assert.Equal(2, V1(s).Clips.Count);

        // A short clip dropped in the middle of a long one splits it in two.
        var (s2, video2, _) = NewSession();
        var long1 = s2.Editor.AddMediaClip(video2, 0, V1(s2).Id);
        var shortId = s2.Editor.AddTitle(3, new TitleClip { Duration = 2 }, V1(s2).Id);
        Assert.Equal(3, V1(s2).Clips.Count);
        var right = (MediaClip)V1(s2).Clips[2];
        Assert.Equal(5, right.Start, 6);
        Assert.Equal(5, right.InPoint, 6);
        Assert.Equal(5, right.Duration, 6);
        Assert.Equal(3, s2.Project.FindClip(long1)!.Value.Clip.Duration, 6);
        _ = shortId;
    }

    [Fact]
    public void Split_and_undo_redo()
    {
        var (s, video, _) = NewSession();
        var id = s.Editor.AddMediaClip(video, 1);
        Assert.Equal(1, s.Editor.Split(4));
        var clips = V1(s).Clips.Cast<MediaClip>().ToList();
        Assert.Equal(2, clips.Count);
        Assert.Equal((1.0, 3.0, 0.0), (clips[0].Start, clips[0].Duration, clips[0].InPoint));
        Assert.Equal((4.0, 7.0, 3.0), (clips[1].Start, clips[1].Duration, clips[1].InPoint));
        Assert.True(s.IsDirty);

        s.Undo();
        Assert.Single(V1(s).Clips);
        Assert.Equal(id, V1(s).Clips[0].Id);
        s.Redo();
        Assert.Equal(2, V1(s).Clips.Count);
        // Splitting outside every clip does nothing.
        Assert.Equal(0, s.Editor.Split(30));
    }

    [Fact]
    public void Trims_stay_within_the_source_and_neighbours()
    {
        var (s, video, _) = NewSession();
        var v1 = V1(s).Id;
        var a = s.Editor.AddMediaClip(video, 0, v1);   // 0..10
        var b = s.Editor.AddMediaClip(video, 12, v1);  // 12..22
        // Can't extend past the end of the file, or into the next clip.
        Assert.Equal(10, s.Editor.ClampTrimEnd(a, 15), 6);
        s.Editor.TrimEnd(a, 6);
        Assert.Equal(6, s.Editor.ClampTrimEnd(a, 6), 6);
        // Can't extend the start before the file's beginning, or into the previous clip.
        Assert.Equal(12, s.Editor.ClampTrimStart(b, 0), 6);
        s.Editor.TrimStart(b, 15);
        var clip = (MediaClip)s.Project.FindClip(b)!.Value.Clip;
        Assert.Equal((15.0, 7.0, 3.0), (clip.Start, clip.Duration, clip.InPoint));
        Assert.Equal(12, s.Editor.ClampTrimStart(b, 0), 6);
        // Never shorter than a frame.
        Assert.Equal(22 - 1.0 / 25, s.Editor.ClampTrimStart(b, 40), 6);
    }

    [Fact]
    public void Ripple_delete_closes_the_gap()
    {
        var (s, video, _) = NewSession();
        var v1 = V1(s).Id;
        var a = s.Editor.AddMediaClip(video, 0, v1);
        var b = s.Editor.AddMediaClip(video, 10, v1);
        var c = s.Editor.AddMediaClip(video, 20, v1);
        s.Editor.Delete([b], ripple: true);
        Assert.Equal(10, s.Project.FindClip(c)!.Value.Clip.Start, 6);
        s.Editor.Delete([a]);
        Assert.Equal(10, s.Project.FindClip(c)!.Value.Clip.Start, 6);
        Assert.Single(V1(s).Clips);
    }

    [Fact]
    public void Locked_tracks_are_left_alone()
    {
        var (s, video, _) = NewSession();
        var id = s.Editor.AddMediaClip(video, 0);
        s.Editor.UpdateTrack(V1(s).Id, "lock", t => t.Locked = true);
        s.Editor.Delete([id]);
        s.Editor.MoveClip(id, 5);
        Assert.Equal(0, s.Editor.Split(5));
        Assert.Equal(0, s.Project.FindClip(id)!.Value.Clip.Start);
    }

    [Fact]
    public void Detach_audio_moves_the_sound_to_an_audio_track()
    {
        var (s, video, _) = NewSession();
        var id = s.Editor.AddMediaClip(video, 2);
        s.Editor.UpdateClip(id, "fx", c =>
        {
            c.Effects.Add(EffectCatalog.Find("grayscale")!.CreateDefault());
            c.Effects.Add(EffectCatalog.Find("normalize")!.CreateDefault());
        });
        var audioId = s.Editor.DetachAudio(id)!;
        var (track, clip) = s.Project.FindClip(audioId)!.Value;
        Assert.Equal(TrackKind.Audio, track.Kind);
        Assert.Equal(2, clip.Start);
        Assert.Equal(["normalize"], clip.Effects.Select(e => e.Type));
        Assert.True(((MediaClip)s.Project.FindClip(id)!.Value.Clip).AudioMuted);
        Assert.Null(s.Editor.DetachAudio(id));
    }

    [Fact]
    public void Slider_drags_undo_as_one_step()
    {
        var (s, video, _) = NewSession();
        var id = s.Editor.AddMediaClip(video, 0);
        for (var i = 1; i <= 10; i++)
            s.Editor.UpdateClip(id, "Opacity", c => c.Opacity = 1 - i * 0.05, mergeKey: "opacity:" + id);
        Assert.Equal(0.5, s.Project.FindClip(id)!.Value.Clip.Opacity, 6);
        s.Undo();
        Assert.Equal(1, s.Project.FindClip(id)!.Value.Clip.Opacity, 6);
    }

    [Fact]
    public void Projects_round_trip_through_json()
    {
        var (s, video, _) = NewSession();
        s.Editor.AddMediaClip(video, 0);
        s.Editor.AddTitle(1, new TitleClip { Text = "Hi\nthere" });
        s.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Arrow, X1 = 0.1, Start = 2 });
        var dir = TestMedia.NewOutputDirectory();
        var path = Path.Combine(dir, "p" + Project.FileExtension);
        ProjectSerializer.Save(s.Project, path);
        ProjectSerializer.Save(s.Project, path); // second save keeps a .bak
        Assert.True(File.Exists(path + ".bak"));
        var loaded = ProjectSerializer.Load(path);
        Assert.Equal(ProjectSerializer.Serialize(s.Project), ProjectSerializer.Serialize(loaded));
        Assert.Contains(loaded.AllClips(), x => x.Clip is AnnotationClip { Shape: AnnotationShape.Arrow, X1: 0.1 });
        Assert.Contains("\"type\": \"title\"", File.ReadAllText(path));

        var newer = ProjectSerializer.Serialize(s.Project).Replace("\"formatVersion\": 1", "\"formatVersion\": 99");
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize(newer));
    }

    [Fact]
    public void Media_found_next_to_a_moved_project()
    {
        var dir = TestMedia.NewOutputDirectory();
        File.WriteAllText(Path.Combine(dir, "clip.mp4"), "x");
        var project = new Project { Media = [new MediaReference { Path = "/elsewhere/clip.mp4" }] };
        ProjectSerializer.ResolveRelativePaths(project, dir);
        Assert.Equal(Path.Combine(dir, "clip.mp4"), project.Media[0].Path);
    }

    [Theory]
    [InlineData(0, "00:00.00")]
    [InlineData(83.456, "01:23.46")]
    [InlineData(3723.5, "1:02:03.50")]
    public void Timecodes_format(double seconds, string expected) => Assert.Equal(expected, Timecode.Format(seconds));

    [Theory]
    [InlineData("83.5", 0, 83.5)]
    [InlineData("1:23.5", 0, 83.5)]
    [InlineData("1:01:23.5", 0, 3683.5)]
    [InlineData("00:00:02:15", 30, 2.5)]
    public void Timecodes_parse(string text, double fps, double expected)
    {
        Assert.True(Timecode.TryParse(text, out var seconds, fps));
        Assert.Equal(expected, seconds, 6);
    }

    [Fact]
    public void Atempo_chains_for_big_speed_changes()
    {
        Assert.Empty(RenderPlanBuilder.Atempo(1));
        Assert.Equal(["atempo=2", "atempo=2", "atempo=1.5"], RenderPlanBuilder.Atempo(6));
        Assert.Equal(["atempo=0.5", "atempo=0.5"], RenderPlanBuilder.Atempo(0.25));
    }

    [Fact]
    public void Zoom_region_keeps_the_frame_aspect_ratio()
    {
        var (x, y, w, h) = RenderPlanBuilder.Region(new AnnotationClip { X1 = 0.4, Y1 = 0.1, X2 = 0.6, Y2 = 0.9 }, 1920, 1080, zoom: true);
        Assert.Equal(16.0 / 9, (double)w / h, 1);
        Assert.InRange(x, 0, 1920 - w);
        Assert.InRange(y, 0, 1080 - h);
    }

    [Fact]
    public void Dissolves_join_clips_that_touch_on_another_track_only_if_there_is_room()
    {
        var session = new EditSession(Project.CreateDefault("p", new ProjectSettings { Width = 320, Height = 240, FrameRate = 30 }));
        var media = session.Editor.ImportMedia(new MediaInfo { Path = "/x.mp4", Kind = MediaKind.Video, Duration = 10, HasVideo = true, Width = 320, Height = 240, FrameRate = 30 });
        var v1 = session.Project.Tracks.First(t => t.Name == "V1").Id;
        var v2 = session.Project.Tracks.First(t => t.Name == "V2").Id;
        var a = session.Editor.AddMediaClip(media, 0, v1);
        session.Editor.TrimEnd(a, 2);
        var blocker = session.Editor.AddMediaClip(media, 3, v1);
        var b = session.Editor.AddMediaClip(media, 2, v2);
        session.Editor.TrimEnd(b, 4);
        // V1 is taken from 3 s, inside the second clip's 2–4 s: it can't move there without overwriting.
        Assert.NotNull(session.Editor.AddDissolve(b, 1));
        Assert.Equal(v2, session.Project.FindClip(b)!.Value.Track.Id);
        session.Editor.Delete([blocker]);
        Assert.Null(session.Editor.AddDissolve(b, 1));
        Assert.Equal(v1, session.Project.FindClip(b)!.Value.Track.Id);
        Assert.Equal(a, session.Editor.DissolvePartner(b)!.Id);
    }
}
