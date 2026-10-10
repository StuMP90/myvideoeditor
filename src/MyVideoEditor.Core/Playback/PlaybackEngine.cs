using System.Diagnostics;
using System.Threading.Channels;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Rendering;

namespace MyVideoEditor.Core.Playback;

/// <summary>
/// Plays the timeline for the preview. ffmpeg renders the project from the playhead with the
/// same filter graph as an export (at preview size), as raw frames and samples on stdout. Sound
/// goes to the <see cref="IAudioSink"/>, whose position is the clock; frames are shown when the
/// clock reaches them and dropped if late. Without a sound device a stopwatch is the clock.
/// Seeking or editing restarts the render from the new position.
/// </summary>
public sealed class PlaybackEngine : IDisposable
{
    private const int FrameQueue = 6;
    private const int AudioChunkFrames = 1024;

    private readonly FfmpegLocator _locator;
    private readonly IAudioSink? _audio;
    private readonly object _gate = new();
    private Session? _session;
    private CancellationTokenSource? _stillCts;

    public PlaybackEngine(IAudioSink? audio = null, FfmpegLocator? locator = null)
    {
        _audio = audio;
        _locator = locator ?? FfmpegLocator.Default;
    }

    /// <summary>A frame to show. Raised on a background thread; the handler owns (and disposes) the frame.</summary>
    public event Action<VideoFrame>? FrameReady;
    /// <summary>Playback reached the end, or failed (message), on a background thread.</summary>
    public event Action<string?>? Stopped;

    public bool IsPlaying => _session != null;
    public bool HasAudio => _audio != null;

    /// <summary>Current playback time while playing.</summary>
    public double Position => _session?.Clock() ?? 0;

    /// <summary>Plays from <paramref name="from"/>; <paramref name="speed"/> above 1 fast-forwards (silently above 2×).</summary>
    public void Play(Project project, double from, int width, int height, double speed = 1)
    {
        Stop();
        var duration = project.Duration;
        if (from >= duration - 0.01)
            from = 0;
        if (duration <= 0)
            return;
        var session = new Session(this, project, from, width & ~1, height & ~1, Math.Clamp(speed, 0.25, 16));
        lock (_gate)
            _session = session;
        session.Start();
    }

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        session?.Dispose();
    }

    /// <summary>
    /// Renders the single frame at <paramref name="time"/> (scrubbing, or when paused after an
    /// edit). The project is copied first, so it may be edited while the frame renders.
    /// Returns null if there's nothing to show or the engine is being disposed.
    /// </summary>
    public async Task<VideoFrame?> RenderStillAsync(Project project, double time, int width, int height)
    {
        project = ProjectSerializer.Clone(project);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _stillCts, cts);
        width &= ~1;
        height &= ~1;
        var token = cts.Token;
        return await Task.Run(() =>
        {
            if (token.IsCancellationRequested)
                return null;
            var plan = BuildFramePlan(project, time, width, height);
            if (plan == null)
                return BlankFrame(project, width, height, time);
            using var stream = new FfmpegStream(_locator, plan.BuildArguments(["-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"], _locator.Capabilities));
            using var reg = token.Register(stream.Dispose);
            var frame = new VideoFrame(width, height, time);
            try
            {
                if (stream.ReadExactly(frame.Pixels.AsSpan(0, frame.Length), token))
                    return frame;
                if (!token.IsCancellationRequested)
                    Log.Warn("playback", $"No frame at {time:0.###}s: {stream.Errors}");
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
            frame.Dispose();
            return null;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private RenderPlan? BuildFramePlan(Project project, double time, int width, int height)
    {
        if (project.Duration <= 0 || time >= project.Duration)
            return null;
        var frame = 1.0 / Math.Max(1, project.Settings.FrameRate);
        return RenderPlanBuilder.Build(project, new RenderOptions
        {
            RangeStart = time,
            RangeEnd = Math.Min(project.Duration, time + frame),
            Width = width,
            Height = height,
            IncludeAudio = false,
            Capabilities = _locator.Capabilities,
        });
    }

    /// <summary>The background colour (an empty timeline, or past the end).</summary>
    private static VideoFrame BlankFrame(Project project, int width, int height, double time)
    {
        var frame = new VideoFrame(width, height, time);
        Graphics.GraphicsRenderer.TryParseColor(project.Settings.BackgroundColor, out var c);
        if (string.IsNullOrEmpty(project.Settings.BackgroundColor))
            c = SkiaSharp.SKColors.Black;
        var pixels = frame.Pixels;
        for (var i = 0; i < frame.Length; i += 4)
        {
            pixels[i] = c.Blue;
            pixels[i + 1] = c.Green;
            pixels[i + 2] = c.Red;
            pixels[i + 3] = 255;
        }
        return frame;
    }

    public void Dispose()
    {
        Stop();
        _stillCts?.Cancel();
    }

    /// <summary>One run of playback, from a start time until stopped or the end.</summary>
    private sealed class Session : IDisposable
    {
        private readonly PlaybackEngine _engine;
        private readonly Project _project;
        private readonly double _from;
        private readonly int _width;
        private readonly int _height;
        private readonly CancellationTokenSource _cts = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly Channel<VideoFrame> _frames = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(FrameQueue) { SingleReader = true, SingleWriter = true });
        private FfmpegStream? _videoStream;
        private FfmpegStream? _audioStream;
        private readonly ManualResetEventSlim _firstFrame = new();
        private volatile bool _audioDone;
        private volatile bool _videoDone;

        private readonly double _speed;

        public Session(PlaybackEngine engine, Project project, double from, int width, int height, double speed)
        {
            _engine = engine;
            _speed = speed;
            _project = ProjectSerializer.Clone(project); // edits during playback don't touch this run
            _from = from;
            _width = width;
            _height = height;
        }

        private IAudioSink? Audio => _engine._audio;

        /// <summary>
        /// The playback clock: the sound device's position while there is sound (held at the start
        /// until sound comes out), otherwise a stopwatch started with the first frame.
        /// </summary>
        public double Clock()
        {
            if (Audio is { } audio && !_audioDone)
                return audio.Started ? _from + audio.PlayedSeconds * _speed : _from;
            return _from + (Volatile.Read(ref _stopwatchBase) + (_stopwatch.IsRunning ? _stopwatch.Elapsed.TotalSeconds : 0)) * _speed;
        }

        public void Start()
        {
            var token = _cts.Token;
            var duration = _project.Duration;
            var options = new RenderOptions
            {
                RangeStart = _from,
                RangeEnd = duration,
                Width = _width,
                Height = _height,
                Capabilities = _engine._locator.Capabilities,
                Speed = _speed,
            };
            try
            {
                var videoPlan = RenderPlanBuilder.Build(_project, options with { IncludeAudio = false });
                _videoStream = new FfmpegStream(_engine._locator,
                    videoPlan.BuildArguments(["-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"], _engine._locator.Capabilities));
                // Above 2× the sound is more noise than help (and decoding it costs time): fast-forward is silent.
                if (Audio != null && _speed <= 2)
                {
                    var audioPlan = RenderPlanBuilder.Build(_project, options with { IncludeVideo = false });
                    _audioStream = new FfmpegStream(_engine._locator,
                        audioPlan.BuildArguments(["-f", "f32le", "-ac", "2", "-ar", Audio.SampleRate.ToString(), "pipe:1"], _engine._locator.Capabilities));
                    Audio.Reset();
                }
                else
                {
                    _audioDone = true;
                }
            }
            catch (Exception ex)
            {
                Log.Error("playback", "Couldn't start playback.", ex);
                _engine.Stopped?.Invoke(ex.Message);
                return;
            }

            new Thread(() => ReadVideo(token)) { IsBackground = true, Name = "preview-video" }.Start();
            if (_audioStream != null)
                new Thread(() => ReadAudio(token)) { IsBackground = true, Name = "preview-audio" }.Start();
            new Thread(() => Present(token, duration)) { IsBackground = true, Name = "preview-present" }.Start();
        }

        private void ReadVideo(CancellationToken token)
        {
            var fps = _project.Settings.FrameRate > 0 ? _project.Settings.FrameRate : 30;
            var index = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var frame = new VideoFrame(_width, _height, _from + index * _speed / fps);
                    if (!_videoStream!.ReadExactly(frame.Pixels.AsSpan(0, frame.Length), token))
                    {
                        frame.Dispose();
                        if (index == 0 && !token.IsCancellationRequested)
                            Log.Warn("playback", "Preview produced no frames: " + _videoStream.Errors);
                        break;
                    }
                    index++;
                    _firstFrame.Set();
                    if (!_frames.Writer.TryWrite(frame))
                    {
                        // Wait for space (the presenter is pacing playback).
                        _frames.Writer.WriteAsync(frame, token).AsTask().GetAwaiter().GetResult();
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
            finally
            {
                _videoDone = true;
                _firstFrame.Set();
                _frames.Writer.TryComplete();
            }
        }

        private void ReadAudio(CancellationToken token)
        {
            var bytes = new byte[AudioChunkFrames * 2 * sizeof(float)];
            try
            {
                // Start the sound (and so the clock) with the first picture, not before it.
                _firstFrame.Wait(TimeSpan.FromSeconds(10), token);
                while (!token.IsCancellationRequested)
                {
                    var n = _audioStream!.Output.Read(bytes, 0, bytes.Length);
                    if (n <= 0)
                        break;
                    // Keep whole samples (ffmpeg may hand over partial reads).
                    while (n % (2 * sizeof(float)) != 0)
                    {
                        var more = _audioStream.Output.Read(bytes, n, (2 * sizeof(float)) - n % (2 * sizeof(float)));
                        if (more <= 0)
                            break;
                        n += more;
                    }
                    var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, n - n % 8));
                    Audio!.Write(samples, token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn("playback", "Audio output failed; continuing without sound.", ex);
            }
            finally
            {
                // The sound has ended (or failed): the stopwatch carries the clock on from here.
                Volatile.Write(ref _stopwatchBase, Audio?.Started == true ? Audio.PlayedSeconds : 0);
                _stopwatch.Restart();
                _audioDone = true;
            }
        }

        private double _stopwatchBase;

        private void Present(CancellationToken token, double duration)
        {
            var frameTime = _speed / Math.Max(1, _project.Settings.FrameRate); // timeline seconds between shown frames
            string? error = null;
            try
            {
                var reader = _frames.Reader;
                var first = true;
                while (!token.IsCancellationRequested)
                {
                    if (!reader.TryRead(out var frame))
                    {
                        if (_videoDone && reader.Completion.IsCompleted)
                            break;
                        // Hold the clock until the first frame arrives, so a slow start doesn't skip ahead.
                        reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult();
                        continue;
                    }
                    if (!_stopwatch.IsRunning && _audioDone)
                        _stopwatch.Start();
                    while (!token.IsCancellationRequested)
                    {
                        var now = Clock();
                        var wait = frame.Time - now;
                        if (wait <= 0.002)
                            break;
                        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(wait, 0.02)));
                    }
                    // Late by more than a frame and a newer one is ready: drop this one. Never the first,
                    // though: the sound's clock doesn't wait for the picture, and if the picture starts
                    // slowly, playback should still start on the frame where Play was pressed.
                    if (!first && Clock() - frame.Time > frameTime * 1.5 && reader.TryPeek(out var next) && next.Time <= Clock())
                    {
                        frame.Dispose();
                        continue;
                    }
                    if (token.IsCancellationRequested)
                    {
                        frame.Dispose();
                        break;
                    }
                    first = false;
                    var handler = _engine.FrameReady;
                    if (handler != null)
                        handler(frame);
                    else
                        frame.Dispose();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("playback", "Playback failed.", ex);
            }

            if (!token.IsCancellationRequested)
            {
                lock (_engine._gate)
                {
                    if (_engine._session == this)
                        _engine._session = null;
                }
                Dispose();
                _engine.Stopped?.Invoke(error);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _videoStream?.Dispose();
            _audioStream?.Dispose();
            Audio?.Reset();
            while (_frames.Reader.TryRead(out var f))
                f.Dispose();
        }
    }
}
