using System.Buffers;

namespace MyVideoEditor.Core.Playback;

/// <summary>A decoded preview frame: BGRA pixels (premultiplied is irrelevant: always opaque).</summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _pixels;

    public VideoFrame(int width, int height, double time)
    {
        Width = width;
        Height = height;
        Time = time;
        _pixels = ArrayPool<byte>.Shared.Rent(width * height * 4);
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>Timeline time of this frame (seconds).</summary>
    public double Time { get; }
    public int Stride => Width * 4;
    public int Length => Width * Height * 4;

    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels != null)
            ArrayPool<byte>.Shared.Return(pixels);
    }
}

/// <summary>Where preview sound goes (OpenAL in the app). Writes block while its buffer is full.</summary>
public interface IAudioSink : IDisposable
{
    int SampleRate { get; }
    /// <summary>Queues interleaved stereo float samples, blocking while the queue is full.</summary>
    void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken);
    /// <summary>Seconds of sound actually played since <see cref="Reset"/>: the playback clock.</summary>
    double PlayedSeconds { get; }
    /// <summary>True once sound has started coming out (the clock is running).</summary>
    bool Started { get; }
    /// <summary>Stops and discards anything queued.</summary>
    void Reset();
    float Volume { get; set; }
}
