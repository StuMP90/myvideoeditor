using System.Diagnostics;
using MyVideoEditor.Core.Editing;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Playback;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.Core.Tests;

public class PlaybackTests
{
    /// <summary>An audio "device" that plays in real time into nothing.</summary>
    private sealed class FakeAudioSink : IAudioSink
    {
        private readonly Stopwatch _clock = new();
        private long _written;
        private long _total;
        /// <summary>Everything ever written (not cleared by Reset).</summary>
        public long TotalWritten => Interlocked.Read(ref _total);
        public int SampleRate => 48000;
        public float Volume { get; set; } = 1;
        public long SamplesWritten => Interlocked.Read(ref _written);
        public bool Started => _clock.IsRunning;
        public double PlayedSeconds => Math.Min(_clock.Elapsed.TotalSeconds, SamplesWritten / 2.0 / SampleRate);

        public void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
        {
            Interlocked.Add(ref _written, samples.Length);
            Interlocked.Add(ref _total, samples.Length);
            if (!_clock.IsRunning)
                _clock.Start();
            // Block while more than half a second ahead, like a real device's buffer.
            while (SamplesWritten / 2.0 / SampleRate - _clock.Elapsed.TotalSeconds > 0.5 && !cancellationToken.IsCancellationRequested)
                Thread.Sleep(5);
        }

        public void Reset()
        {
            _clock.Reset();
            Interlocked.Exchange(ref _written, 0);
        }

        public void Dispose() { }
    }

    private static async Task<Project> ProjectWithPattern(double seconds)
    {
        var session = new EditSession(Project.CreateDefault("p", new ProjectSettings { Width = 320, Height = 240, FrameRate = 30 }));
        var media = session.Editor.ImportMedia(await MediaProbe.Default.ProbeAsync(await TestMedia.PatternAsync(seconds, 320, 240)));
        session.Editor.AddMediaClip(media, 0);
        return session.Project;
    }

    [Fact]
    public async Task Plays_in_real_time_with_sound_as_the_clock()
    {
        var project = await ProjectWithPattern(2);
        var sink = new FakeAudioSink();
        using var engine = new PlaybackEngine(sink);
        var frames = new List<double>();
        var stopped = new TaskCompletionSource<string?>();
        engine.FrameReady += f =>
        {
            lock (frames)
                frames.Add(f.Time);
            f.Dispose();
        };
        engine.Stopped += e => stopped.TrySetResult(e);

        var watch = Stopwatch.StartNew();
        engine.Play(project, 0.5, 160, 120);
        Assert.True(engine.IsPlaying);
        var finished = await Task.WhenAny(stopped.Task, Task.Delay(10_000));
        Assert.Same(stopped.Task, finished);
        Assert.Null(await stopped.Task);
        // 1.5 s of timeline: real time, not as fast as ffmpeg can go.
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.2, 4);
        Assert.True(frames.Count > 20, $"only {frames.Count} frames shown");
        Assert.Equal(0.5, frames[0], 2);
        Assert.True(sink.TotalWritten > 48000, "sound was played");
        Assert.False(engine.IsPlaying);
    }

    [Theory]
    [InlineData(2)] // with sound, sped up
    [InlineData(4)] // silent
    public async Task Fast_forward_plays_the_timeline_faster(double speed)
    {
        var project = await ProjectWithPattern(4);
        var sink = new FakeAudioSink();
        using var engine = new PlaybackEngine(sink);
        var frames = new List<double>();
        var stopped = new TaskCompletionSource<string?>();
        engine.FrameReady += f =>
        {
            lock (frames)
                frames.Add(f.Time);
            f.Dispose();
        };
        engine.Stopped += e => stopped.TrySetResult(e);

        var watch = Stopwatch.StartNew();
        engine.Play(project, 0, 160, 120, speed);
        Assert.Same(stopped.Task, await Task.WhenAny(stopped.Task, Task.Delay(10_000)));
        Assert.Null(await stopped.Task);
        // 4 s of timeline in 4/speed seconds (plus start-up).
        Assert.InRange(watch.Elapsed.TotalSeconds, 4 / speed * 0.8, 4 / speed + 2);
        Assert.True(frames[^1] > 3.5, $"reached {frames[^1]}");
        Assert.Equal(speed <= 2, sink.TotalWritten > 0);
    }

    [Fact]
    public async Task Stop_ends_playback_promptly()
    {
        var project = await ProjectWithPattern(4);
        using var engine = new PlaybackEngine();
        var shown = new TaskCompletionSource();
        engine.FrameReady += f =>
        {
            f.Dispose();
            shown.TrySetResult();
        };
        engine.Play(project, 0, 160, 120);
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.Stop();
        Assert.False(engine.IsPlaying);
    }

    [Fact]
    public async Task Renders_single_frames_for_scrubbing()
    {
        var project = await ProjectWithPattern(2);
        using var engine = new PlaybackEngine();
        using var frame = await engine.RenderStillAsync(project, 1, 160, 120);
        Assert.NotNull(frame);
        Assert.Equal(160, frame.Width);
        Assert.Contains(frame.Pixels.Take(frame.Length), b => b != 0);
        // Past the end shows the background.
        using var blank = await engine.RenderStillAsync(project, 50, 160, 120);
        Assert.NotNull(blank);
        Assert.Equal(0, blank.Pixels[0]);
    }

    [Fact]
    public async Task Stills_show_footage_under_titles_and_annotations()
    {
        var session = new EditSession(Project.CreateDefault("p", new ProjectSettings { Width = 640, Height = 360, FrameRate = 30 }));
        var media = session.Editor.ImportMedia(await MediaProbe.Default.ProbeAsync(await TestMedia.PatternAsync(4, 640, 360)));
        session.Editor.AddMediaClip(media, 0);
        session.Editor.AddTitle(1, new TitleClip { Text = "Jane Doe", Duration = 5 });
        session.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Arrow, Start = 1, Duration = 3 });
        session.Editor.AddAnnotation(new AnnotationClip { Shape = AnnotationShape.Blur, X1 = 0.6, Y1 = 0.1, X2 = 0.9, Y2 = 0.3, Start = 1, Duration = 3 });
        using var engine = new PlaybackEngine();
        using var frame = await engine.RenderStillAsync(session.Project, 1.5, 640, 360);
        Assert.NotNull(frame);
        // The test pattern's left bar is red/yellow-ish, never black.
        var i = (100 * frame.Width + 40) * 4;
        Assert.True(frame.Pixels[i] + frame.Pixels[i + 1] + frame.Pixels[i + 2] > 150, "footage visible under the overlays");
    }

    [Fact]
    public async Task Clips_with_one_keyframe_get_thumbnails()
    {
        // Thumbnails come from keyframes only (fast); a short clip may have just the first one.
        var strip = await new MediaPreviews(cacheDirectory: TestMedia.NewOutputDirectory()).GetFilmstripAsync(await TestMedia.OneKeyframeAsync(), 4, false);
        Assert.NotNull(strip);
        Assert.NotEmpty(strip.Frames);
    }

    [Fact]
    public async Task Filmstrips_and_waveforms_are_made_and_cached()
    {
        var file = await TestMedia.PatternAsync(4, 320, 240);
        var previews = new MediaPreviews(cacheDirectory: TestMedia.NewOutputDirectory());
        var strip = await previews.GetFilmstripAsync(file, 4, false);
        Assert.NotNull(strip);
        Assert.Equal(MediaPreviews.ThumbnailHeight, strip.Height);
        Assert.True(strip.Frames.Count >= 2);
        var wave = await previews.GetWaveformAsync(file);
        Assert.NotNull(wave);
        Assert.InRange(wave.Peaks.Length, 190, 210);
        Assert.True(wave.Peak(1, 2) > 0.3f);
        Assert.Same(strip, previews.TryGetFilmstrip(file, 4, false));
    }
}
