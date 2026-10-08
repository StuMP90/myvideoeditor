using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Playback;
using Silk.NET.OpenAL;

namespace MyVideoEditor.App.Services;

/// <summary>
/// Preview sound through OpenAL Soft (bundled for Windows and Linux; on Linux it talks to
/// PipeWire/PulseAudio/ALSA). A small ring of buffers is kept queued; how much has been
/// played is the playback clock.
/// </summary>
public sealed unsafe class OpenAlAudioSink : IAudioSink
{
    private const int BufferCount = 8;
    private const int FramesPerBuffer = 2048; // ~43 ms at 48 kHz: ~340 ms queued at most

    private readonly AL _al;
    private readonly ALContext _alc;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly Queue<uint> _free = new();
    private readonly Dictionary<uint, int> _queuedFrames = [];
    private readonly short[] _pcm = new short[FramesPerBuffer * 2];
    private readonly object _lock = new();
    private long _playedFrames;
    private bool _started;
    private float _volume = 1;
    private bool _disposed;

    private OpenAlAudioSink(AL al, ALContext alc, Device* device, Context* context)
    {
        _al = al;
        _alc = alc;
        _device = device;
        _context = context;
        _source = _al.GenSource();
        _buffers = _al.GenBuffers(BufferCount);
        foreach (var b in _buffers)
            _free.Enqueue(b);
    }

    public int SampleRate => 48000;

    /// <summary>Opens the default sound device, or returns null (playback then runs silently on a stopwatch).</summary>
    public static OpenAlAudioSink? TryCreate()
    {
        try
        {
            var alc = ALContext.GetApi(soft: true);
            var al = AL.GetApi(soft: true);
            var device = alc.OpenDevice("");
            if (device == null)
            {
                Log.Warn("audio", "No sound output device; preview will be silent.");
                return null;
            }
            var context = alc.CreateContext(device, null);
            alc.MakeContextCurrent(context);
            Log.Info("audio", "OpenAL sound output opened.");
            return new OpenAlAudioSink(al, alc, device, context);
        }
        catch (Exception ex)
        {
            Log.Warn("audio", "OpenAL couldn't be loaded; preview will be silent.", ex);
            return null;
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 2);
            lock (_lock)
                if (!_disposed)
                    _al.SetSourceProperty(_source, SourceFloat.Gain, _volume);
        }
    }

    public bool Started
    {
        get
        {
            lock (_lock)
                return _started;
        }
    }

    public double PlayedSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_disposed)
                    return 0;
                Reclaim();
                _al.GetSourceProperty(_source, GetSourceInteger.SampleOffset, out var offset);
                return (_playedFrames + Math.Max(0, offset)) / (double)SampleRate;
            }
        }
    }

    public void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
    {
        var position = 0;
        while (position < samples.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            uint buffer;
            lock (_lock)
            {
                if (_disposed)
                    return;
                Reclaim();
                if (!_free.TryDequeue(out buffer))
                    buffer = 0;
            }
            if (buffer == 0)
            {
                Thread.Sleep(5);
                continue;
            }

            var count = Math.Min(_pcm.Length, samples.Length - position);
            count -= count % 2;
            for (var i = 0; i < count; i++)
                _pcm[i] = (short)Math.Clamp(samples[position + i] * 32767f, short.MinValue, short.MaxValue);
            position += count;

            lock (_lock)
            {
                if (_disposed)
                    return;
                fixed (short* data = _pcm)
                    _al.BufferData(buffer, BufferFormat.Stereo16, data, count * sizeof(short), SampleRate);
                _al.SourceQueueBuffers(_source, 1, &buffer);
                _queuedFrames[buffer] = count / 2;
                // (Re)start once there's a little queued: at the beginning, and after an underrun.
                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
                if (state != (int)SourceState.Playing && _queuedFrames.Count >= 2)
                {
                    _al.SetSourceProperty(_source, SourceFloat.Gain, _volume);
                    _al.SourcePlay(_source);
                    _started = true;
                }
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _al.SourceStop(_source);
            Reclaim();
            // Anything still queued after stopping counts as processed; unqueue it all.
            _al.SetSourceProperty(_source, SourceInteger.Buffer, 0u);
            foreach (var b in _queuedFrames.Keys)
                _free.Enqueue(b);
            _queuedFrames.Clear();
            _playedFrames = 0;
            _started = false;
        }
    }

    /// <summary>Takes back buffers that have finished playing. Call with the lock held.</summary>
    private void Reclaim()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
        while (processed-- > 0)
        {
            uint buffer;
            _al.SourceUnqueueBuffers(_source, 1, &buffer);
            if (_queuedFrames.Remove(buffer, out var frames))
                _playedFrames += frames;
            _free.Enqueue(buffer);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
            _alc.MakeContextCurrent((Context*)null);
            _alc.DestroyContext(_context);
            _alc.CloseDevice(_device);
        }
    }
}
