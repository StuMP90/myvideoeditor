using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Media;

/// <summary>Reads media file details with ffprobe. Results are cached per path, size and modification time.</summary>
public sealed class MediaProbe
{
    private static readonly HashSet<string> ImageFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "image2", "png_pipe", "jpeg_pipe", "bmp_pipe", "webp_pipe", "tiff_pipe", "gif_pipe", "svg_pipe", "jpegls_pipe", "qoi_pipe",
    };

    private readonly FfmpegLocator _locator;
    private readonly ConcurrentDictionary<(string, long, DateTime), MediaInfo> _cache = new();

    public MediaProbe(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    public static MediaProbe Default { get; } = new();

    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException($"{path} doesn't exist.", path);
        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        if (LottieMedia.IsLottie(path))
        {
            var lottie = await Task.Run(() => LottieMedia.Probe(path), cancellationToken).ConfigureAwait(false);
            _cache[key] = lottie;
            return lottie;
        }

        var psi = new ProcessStartInfo(_locator.FfprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file.FullName })
            psi.ArgumentList.Add(a);

        using var process = ChildProcess.Start(psi);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var json = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            Log.Warn("probe", $"ffprobe failed on {file.Name}: {stderr.Trim()}");
            throw new FfmpegException($"{file.Name} isn't a media file ffmpeg can read: {stderr.Trim()}", process.ExitCode, stderr);
        }

        var info = Parse(file.FullName, json, file.Length);
        if (!info.HasVideo && !info.HasAudio)
            throw new FfmpegException($"{file.Name} has no video or audio.", 0, "");
        _cache[key] = info;
        return info;
    }

    internal static MediaInfo Parse(string path, string json, long size)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var formatName = Str(format, "format_name");
        var duration = Num(format, "duration");

        JsonElement? video = null, audio = null;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = Str(s, "codec_type");
                // Cover art inside audio files shows up as a one-frame "video" stream; ignore it.
                var attachedPic = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && video == null && !attachedPic)
                    video = s;
                else if (type == "audio" && audio == null)
                    audio = s;
            }
        }

        // Stills: PNG/JPEG/etc. (animated GIFs use the "gif" demuxer, so they count as video).
        var isImage = video != null && audio == null
                      && (ImageFormats.Contains(formatName) || formatName.EndsWith("_pipe", StringComparison.Ordinal));
        var info = new MediaInfo
        {
            Path = path,
            FormatName = formatName,
            Duration = isImage ? 0 : duration > 0 ? duration : Math.Max(video is { } vd ? Num(vd, "duration") : 0, audio is { } ad ? Num(ad, "duration") : 0),
            SizeBytes = size,
            BitRate = (long)Num(format, "bit_rate"),
            Kind = video == null ? MediaKind.Audio : isImage ? MediaKind.Image : MediaKind.Video,
        };

        if (video is { } v)
        {
            var width = (int)Num(v, "width");
            var height = (int)Num(v, "height");
            var sar = ParseRatio(Str(v, "sample_aspect_ratio"));
            if (sar > 0 && Math.Abs(sar - 1) > 0.001)
                width = (int)Math.Round(width * sar / 2) * 2;
            var rotation = Rotation(v);
            if (rotation is 90 or 270)
                (width, height) = (height, width);
            var fpsText = Str(v, "avg_frame_rate");
            var fps = ParseRatio(fpsText);
            if (fps is <= 0 or > 1000)
            {
                fpsText = Str(v, "r_frame_rate");
                fps = ParseRatio(fpsText);
            }
            static string Tag(string value) => value is "unknown" or "unspecified" or "reserved" ? "" : value;
            info = info with
            {
                HasVideo = true,
                Width = width,
                Height = height,
                Rotation = rotation,
                FrameRate = fps is > 0 and <= 1000 ? fps : 30,
                FrameRateText = fps is > 0 and <= 1000 ? fpsText.Replace(':', '/') : "30",
                ColorSpace = Tag(Str(v, "color_space")),
                ColorRange = Tag(Str(v, "color_range")),
                ColorPrimaries = Tag(Str(v, "color_primaries")),
                ColorTransfer = Tag(Str(v, "color_transfer")),
                VideoCodec = Str(v, "codec_name"),
                PixelFormat = Str(v, "pix_fmt"),
                VideoEnd = isImage || Num(v, "duration") <= 0 ? 0
                    : Math.Max(0, Num(v, "start_time") - Num(format, "start_time")) + Num(v, "duration"),
            };
        }
        if (audio is { } a)
        {
            info = info with
            {
                HasAudio = true,
                SampleRate = (int)Num(a, "sample_rate"),
                Channels = (int)Num(a, "channels"),
                AudioCodec = Str(a, "codec_name"),
            };
        }
        return info;
    }

    private static int Rotation(JsonElement stream)
    {
        double degrees = 0;
        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var sd in sideData.EnumerateArray())
                if (sd.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                    degrees = r.GetDouble();
        }
        if (degrees == 0 && stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate))
            double.TryParse(rotate.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
        var normalised = ((int)Math.Round(degrees / 90) * 90 % 360 + 360) % 360;
        return normalised;
    }

    internal static double ParseRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var parts = text.Split(':', '/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d == 0 ? 0 : n / d;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p))
            return 0;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String when double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }
}
