using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MyVideoEditor.Core.Diagnostics;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Media;

/// <summary>Small thumbnails along a video, for drawing clips on the timeline.</summary>
public sealed class Filmstrip
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Seconds between thumbnails.</summary>
    public required double Interval { get; init; }
    /// <summary>BGRA pixels of each thumbnail, in time order.</summary>
    public required IReadOnlyList<byte[]> Frames { get; init; }

    /// <summary>The thumbnail nearest a source time.</summary>
    public byte[]? FrameAt(double seconds) =>
        Frames.Count == 0 ? null : Frames[Math.Clamp((int)(seconds / Interval), 0, Frames.Count - 1)];
}

/// <summary>Peak levels of a file's sound, for drawing waveforms on the timeline.</summary>
public sealed class Waveform
{
    public const int PeaksPerSecond = 50;

    /// <summary>Peak level 0..1 for each 1/50 s.</summary>
    public required float[] Peaks { get; init; }

    /// <summary>The loudest peak between two source times.</summary>
    public float Peak(double from, double to)
    {
        var a = Math.Clamp((int)(from * PeaksPerSecond), 0, Peaks.Length);
        var b = Math.Clamp((int)Math.Ceiling(to * PeaksPerSecond), a + 1, Peaks.Length);
        float max = 0;
        for (var i = a; i < b; i++)
            max = Math.Max(max, Peaks[i]);
        return max;
    }
}

/// <summary>
/// Makes and caches filmstrips and waveforms: one cheap ffmpeg pass per file (keyframes only
/// for pictures, low-rate mono for sound), kept in memory and on disk.
/// </summary>
public sealed class MediaPreviews
{
    public const int ThumbnailHeight = 54;
    private const int MaxThumbnails = 240;

    private readonly FfmpegLocator _locator;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Task<Filmstrip?>> _filmstrips = new();
    private readonly ConcurrentDictionary<string, Task<Waveform?>> _waveforms = new();
    private readonly SemaphoreSlim _workers = new(2, 2);

    public MediaPreviews(FfmpegLocator? locator = null, string? cacheDirectory = null)
    {
        _locator = locator ?? FfmpegLocator.Default;
        _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.CacheDirectory, "previews");
    }

    public static MediaPreviews Default { get; } = new();

    /// <summary>The filmstrip if it's ready (null while it's being made: <see cref="Ready"/> fires when it is).</summary>
    public Filmstrip? TryGetFilmstrip(string path, double duration, bool isImage)
    {
        var task = _filmstrips.GetOrAdd(path, p => Task.Run(() => MakeFilmstripAsync(p, duration, isImage)));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    public Waveform? TryGetWaveform(string path)
    {
        var task = _waveforms.GetOrAdd(path, p => Task.Run(() => MakeWaveformAsync(p)));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }

    public Task<Filmstrip?> GetFilmstripAsync(string path, double duration, bool isImage) =>
        _filmstrips.GetOrAdd(path, p => Task.Run(() => MakeFilmstripAsync(p, duration, isImage)));

    public Task<Waveform?> GetWaveformAsync(string path) =>
        _waveforms.GetOrAdd(path, p => Task.Run(() => MakeWaveformAsync(p)));

    /// <summary>Raised (on a background thread) when a filmstrip or waveform has been made.</summary>
    public event Action? Ready;

    private async Task<Filmstrip?> MakeFilmstripAsync(string path, double duration, bool isImage)
    {
        try
        {
            var info = await MediaProbe.Default.ProbeAsync(path).ConfigureAwait(false);
            if (!info.HasVideo)
                return null;
            var height = ThumbnailHeight;
            var width = Math.Max(2, (int)Math.Round(height * (double)info.Width / Math.Max(1, info.Height)) & ~1);
            var interval = isImage || duration <= 0 ? 1 : Math.Max(1, duration / MaxThumbnails);
            var cache = CacheFile(path, $"strip-{width}x{height}-{interval:0.###}");
            byte[] data;
            // (An empty one is from before short clips with one keyframe got thumbnails: made again.)
            if (File.Exists(cache) && new FileInfo(cache).Length > 0)
            {
                data = await File.ReadAllBytesAsync(cache).ConfigureAwait(false);
            }
            else
            {
                await _workers.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (info.Kind == MediaKind.Animation)
                    {
                        // Lottie: drawn by Skottie (ffmpeg can't read it).
                        data = LottieMedia.Thumbnails(path, width, height, interval, info.Duration);
                    }
                    else
                    {
                        var args = new List<string>();
                        if (!isImage)
                            args.AddRange(["-skip_frame", "nokey"]);
                        // eof_action=pass: a short clip may have only one keyframe (the first), which the fps filter
                        // would otherwise drop, leaving no thumbnails at all.
                        args.AddRange(["-i", path, "-an", "-vf", isImage ? $"scale={width}:{height}" : $"fps=1/{F(interval)}:eof_action=pass,scale={width}:{height}"]);
                        if (isImage)
                            args.AddRange(["-frames:v", "1"]);
                        args.AddRange(["-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
                        data = await RunToBytesAsync(args).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _workers.Release();
                }
                await WriteCacheAsync(cache, data).ConfigureAwait(false);
            }
            var frameSize = width * height * 4;
            var frames = new List<byte[]>();
            for (var offset = 0; offset + frameSize <= data.Length; offset += frameSize)
                frames.Add(data.AsSpan(offset, frameSize).ToArray());
            if (frames.Count == 0)
                return null;
            return new Filmstrip { Width = width, Height = height, Interval = interval, Frames = frames };
        }
        catch (Exception ex)
        {
            Log.Warn("previews", $"Couldn't make thumbnails for {path}.", ex);
            return null;
        }
        finally
        {
            Ready?.Invoke();
        }
    }

    private async Task<Waveform?> MakeWaveformAsync(string path)
    {
        try
        {
            var info = await MediaProbe.Default.ProbeAsync(path).ConfigureAwait(false);
            if (!info.HasAudio)
                return null;
            const int sampleRate = 4000;
            var cache = CacheFile(path, "wave-v1");
            float[] peaks;
            if (File.Exists(cache))
            {
                var bytes = await File.ReadAllBytesAsync(cache).ConfigureAwait(false);
                peaks = new float[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                    peaks[i] = bytes[i] / 255f;
            }
            else
            {
                byte[] pcm;
                await _workers.WaitAsync().ConfigureAwait(false);
                try
                {
                    pcm = await RunToBytesAsync(["-i", path, "-vn", "-ac", "1", "-ar", sampleRate.ToString(), "-f", "s16le", "pipe:1"]).ConfigureAwait(false);
                }
                finally
                {
                    _workers.Release();
                }
                var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, pcm.Length & ~1));
                var per = sampleRate / Waveform.PeaksPerSecond;
                peaks = new float[(samples.Length + per - 1) / per];
                for (var i = 0; i < peaks.Length; i++)
                {
                    var max = 0;
                    var end = Math.Min(samples.Length, (i + 1) * per);
                    for (var j = i * per; j < end; j++)
                        max = Math.Max(max, Math.Abs((int)samples[j]));
                    // A gentle curve so quiet speech is still visible.
                    peaks[i] = (float)Math.Sqrt(max / 32768.0);
                }
                await WriteCacheAsync(cache, peaks.Select(p => (byte)Math.Clamp(p * 255, 0, 255)).ToArray()).ConfigureAwait(false);
            }
            return new Waveform { Peaks = peaks };
        }
        catch (Exception ex)
        {
            Log.Warn("previews", $"Couldn't make a waveform for {path}.", ex);
            return null;
        }
        finally
        {
            Ready?.Invoke();
        }
    }

    private string CacheFile(string path, string kind)
    {
        var file = new FileInfo(path);
        var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{kind}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        return Path.Combine(_cacheDirectory, hash + ".bin");
    }

    private static async Task WriteCacheAsync(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N")[..6];
            await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { /* cache is optional */ }
    }

    private async Task<byte[]> RunToBytesAsync(List<string> args)
    {
        var psi = new ProcessStartInfo(_locator.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = ChildProcess.Start(psi, isFfmpeg: true);
        var errors = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        await p.StandardOutput.BaseStream.CopyToAsync(ms).ConfigureAwait(false);
        await p.WaitForExitAsync().ConfigureAwait(false);
        if (p.ExitCode != 0 && ms.Length == 0)
            throw new FfmpegException((await errors.ConfigureAwait(false)).Trim(), p.ExitCode, "");
        return ms.ToArray();
    }
}
