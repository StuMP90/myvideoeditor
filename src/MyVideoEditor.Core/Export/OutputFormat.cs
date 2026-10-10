using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Export;

public enum Quality
{
    /// <summary>Visually lossless, large files.</summary>
    High,
    /// <summary>Good quality, sensible size (the default).</summary>
    Standard,
    /// <summary>Smaller files for sharing (bug reports, chat).</summary>
    Small,
}

/// <summary>An encoder choice for a format, with its arguments per quality.</summary>
public sealed record EncoderChoice(string Encoder, Func<Quality, IReadOnlyList<string>> Arguments);

/// <summary>
/// A file format the app writes: container, video and audio encoders (in order of preference,
/// the first one the ffmpeg build has is used), and extra options.
/// </summary>
public sealed record OutputFormat
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Extension { get; init; }
    public string Description { get; init; } = "";
    public IReadOnlyList<EncoderChoice> VideoEncoders { get; init; } = [];
    public IReadOnlyList<EncoderChoice> AudioEncoders { get; init; } = [];
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
    public string? PixelFormat { get; init; } = "yuv420p";
    /// <summary>Animated GIF: needs a palette pass in the filter graph and no audio.</summary>
    public bool IsGif { get; init; }
    /// <summary>Lower frame rate cap (GIFs).</summary>
    public double? MaxFrameRate { get; init; }
    /// <summary>A single RGB picture (conversion previews), not a video.</summary>
    public bool IsStill { get; init; }
    /// <summary>DVD-Video: the frame size, rate and pixel shape are the DVD standard's (see <see cref="DvdVideo"/>), not the project's.</summary>
    public bool IsDvd { get; init; }

    public bool HasVideo => VideoEncoders.Count > 0 || IsGif;
    public bool HasAudio => AudioEncoders.Count > 0;

    public EncoderChoice? PickVideoEncoder(FfmpegCapabilities caps) =>
        VideoEncoders.FirstOrDefault(e => caps.Encoders.Count == 0 || caps.HasEncoder(e.Encoder));

    public EncoderChoice? PickAudioEncoder(FfmpegCapabilities caps) =>
        AudioEncoders.FirstOrDefault(e => caps.Encoders.Count == 0 || caps.HasEncoder(e.Encoder));

    /// <summary>Codec arguments for this format with the given ffmpeg build.</summary>
    public List<string> CodecArguments(FfmpegCapabilities caps, Quality quality, bool includeVideo = true, bool includeAudio = true)
    {
        var args = new List<string>();
        if (HasVideo && includeVideo && !IsGif)
        {
            var video = PickVideoEncoder(caps) ?? throw new InvalidOperationException(
                $"This ffmpeg can't write {Label}: it has none of these encoders: {string.Join(", ", VideoEncoders.Select(e => e.Encoder))}.");
            args.AddRange(["-c:v", video.Encoder]);
            args.AddRange(video.Arguments(quality));
            if (PixelFormat != null)
                args.AddRange(["-pix_fmt", PixelFormat]);
        }
        if (HasAudio && includeAudio)
        {
            var audio = PickAudioEncoder(caps) ?? throw new InvalidOperationException(
                $"This ffmpeg can't write {Label} audio: it has none of these encoders: {string.Join(", ", AudioEncoders.Select(e => e.Encoder))}.");
            args.AddRange(["-c:a", audio.Encoder]);
            args.AddRange(audio.Arguments(quality));
        }
        args.AddRange(ExtraArguments);
        return args;
    }
}

public static class OutputFormats
{
    private static IReadOnlyList<string> Q(Quality q, string[] high, string[] standard, string[] small) =>
        q switch { Quality.High => high, Quality.Small => small, _ => standard };

    private static readonly EncoderChoice X264 = new("libx264", q => Q(q,
        ["-preset", "slow", "-crf", "17"], ["-preset", "medium", "-crf", "21"], ["-preset", "medium", "-crf", "27"]));
    private static readonly EncoderChoice OpenH264 = new("libopenh264", q => Q(q,
        ["-b:v", "16M"], ["-b:v", "8M"], ["-b:v", "2M"]));
    private static readonly EncoderChoice Mpeg4 = new("mpeg4", q => Q(q, ["-q:v", "2"], ["-q:v", "4"], ["-q:v", "8"]));
    private static readonly EncoderChoice X265 = new("libx265", q => Q(q,
        ["-preset", "slow", "-crf", "20", "-tag:v", "hvc1"], ["-preset", "medium", "-crf", "24", "-tag:v", "hvc1"], ["-preset", "medium", "-crf", "30", "-tag:v", "hvc1"]));
    private static readonly EncoderChoice SvtAv1 = new("libsvtav1", q => Q(q,
        ["-preset", "6", "-crf", "24"], ["-preset", "8", "-crf", "32"], ["-preset", "8", "-crf", "40"]));
    private static readonly EncoderChoice AomAv1 = new("libaom-av1", q => Q(q,
        ["-cpu-used", "4", "-crf", "24", "-b:v", "0"], ["-cpu-used", "6", "-crf", "32", "-b:v", "0"], ["-cpu-used", "6", "-crf", "40", "-b:v", "0"]));
    private static readonly EncoderChoice Vp9 = new("libvpx-vp9", q => Q(q,
        ["-crf", "24", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "2"],
        ["-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4"],
        ["-crf", "40", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "5"]));
    private static readonly EncoderChoice ProRes = new("prores_ks", q => Q(q,
        ["-profile:v", "3"], ["-profile:v", "2"], ["-profile:v", "0"]));

    private static readonly EncoderChoice Aac = new("aac", q => Q(q, ["-b:a", "256k"], ["-b:a", "192k"], ["-b:a", "96k"]));
    private static readonly EncoderChoice Opus = new("libopus", q => Q(q, ["-b:a", "192k"], ["-b:a", "128k"], ["-b:a", "64k"]));
    private static readonly EncoderChoice Vorbis = new("libvorbis", q => Q(q, ["-q:a", "8"], ["-q:a", "5"], ["-q:a", "2"]));
    private static readonly EncoderChoice Lame = new("libmp3lame", q => Q(q, ["-q:a", "0"], ["-q:a", "2"], ["-q:a", "6"]));
    private static readonly EncoderChoice Pcm16 = new("pcm_s16le", _ => []);
    // DVD limits: video up to 9.8 Mbit/s (9 here, leaving room for sound), a 1.8 MB buffer; AC-3 sound at 48 kHz.
    private static readonly EncoderChoice DvdMpeg2 = new("mpeg2video", q => [
        "-b:v", Q(q, ["8000k"], ["6000k"], ["4000k"])[0], "-maxrate", "9000k", "-minrate", "0", "-bufsize", "1835k", "-bf", "2"]);
    private static readonly EncoderChoice DvdAc3 = new("ac3", q => ["-b:a", Q(q, ["448k"], ["384k"], ["224k"])[0], "-ar", "48000", "-ac", "2"]);
    private static readonly EncoderChoice Flac = new("flac", _ => []);

    /// <summary>
    /// An MPEG-2 program stream that meets the DVD-Video standard (NTSC or PAL, chosen by the project's
    /// frame rate; 4:3 or 16:9 by its shape), ready for DVD authoring.
    /// </summary>
    public static readonly OutputFormat DvdVideo = new()
    {
        Id = "dvd-video", Label = "DVD-Video (MPEG-2)", Extension = ".mpg",
        Description = "For burning to a DVD that plays in DVD players: MPEG-2 at DVD's own size and frame rate (NTSC 720×480 at 29.97 fps, " +
                      "or PAL 720×576 at 25 fps, from the project's frame rate), 4:3 or 16:9 by the project's shape, with AC-3 sound. " +
                      "About 2 hours fit on a single-layer disc at Small quality, 1½ at Standard, 1 at High.",
        VideoEncoders = [DvdMpeg2], AudioEncoders = [DvdAc3], IsDvd = true,
        ExtraArguments = ["-f", "dvd", "-muxrate", "10080000", "-packetsize", "2048"],
    };

    public static readonly OutputFormat Mp4H264 = new()
    {
        Id = "mp4-h264", Label = "MP4 (H.264)", Extension = ".mp4",
        Description = "Plays everywhere: browsers, phones, chat apps, bug trackers.",
        VideoEncoders = [X264, OpenH264, Mpeg4], AudioEncoders = [Aac],
        ExtraArguments = ["-movflags", "+faststart"],
    };

    public static readonly OutputFormat Mp4H265 = new()
    {
        Id = "mp4-h265", Label = "MP4 (H.265/HEVC)", Extension = ".mp4",
        Description = "About half the size of H.264 at the same quality; newer devices.",
        VideoEncoders = [X265], AudioEncoders = [Aac],
        ExtraArguments = ["-movflags", "+faststart"],
    };

    public static readonly OutputFormat Mp4Av1 = new()
    {
        Id = "mp4-av1", Label = "MP4 (AV1)", Extension = ".mp4",
        Description = "Smallest files; slow to encode, recent browsers and devices.",
        VideoEncoders = [SvtAv1, AomAv1], AudioEncoders = [Aac],
        ExtraArguments = ["-movflags", "+faststart"],
    };

    public static readonly OutputFormat WebmVp9 = new()
    {
        Id = "webm-vp9", Label = "WebM (VP9)", Extension = ".webm",
        Description = "Open format for the web.",
        VideoEncoders = [Vp9], AudioEncoders = [Opus, Vorbis],
    };

    public static readonly OutputFormat WebmAv1 = new()
    {
        Id = "webm-av1", Label = "WebM (AV1)", Extension = ".webm",
        Description = "Open format, smallest files.",
        VideoEncoders = [SvtAv1, AomAv1], AudioEncoders = [Opus, Vorbis],
    };

    public static readonly OutputFormat MkvH264 = new()
    {
        Id = "mkv-h264", Label = "MKV (H.264)", Extension = ".mkv",
        Description = "Matroska container; robust and flexible.",
        VideoEncoders = [X264, OpenH264, Mpeg4], AudioEncoders = [Aac, Opus],
    };

    public static readonly OutputFormat MovProRes = new()
    {
        Id = "mov-prores", Label = "MOV (ProRes 422)", Extension = ".mov",
        Description = "Editing intermediate for other editors; very large files.",
        VideoEncoders = [ProRes], AudioEncoders = [Pcm16],
        PixelFormat = "yuv422p10le",
    };

    public static readonly OutputFormat Gif = new()
    {
        Id = "gif", Label = "Animated GIF", Extension = ".gif",
        Description = "Short silent loops for docs, chat and tickets. Keep them short and small.",
        IsGif = true, PixelFormat = null, MaxFrameRate = 15,
    };

    public static readonly OutputFormat Mp3 = new()
    {
        Id = "mp3", Label = "MP3 audio", Extension = ".mp3",
        AudioEncoders = [Lame],
    };

    public static readonly OutputFormat M4a = new()
    {
        Id = "m4a", Label = "M4A (AAC) audio", Extension = ".m4a",
        AudioEncoders = [Aac],
        ExtraArguments = ["-movflags", "+faststart"],
    };

    public static readonly OutputFormat Wav = new()
    {
        Id = "wav", Label = "WAV audio (uncompressed)", Extension = ".wav",
        AudioEncoders = [Pcm16],
    };

    public static readonly OutputFormat FlacAudio = new()
    {
        Id = "flac", Label = "FLAC audio (lossless)", Extension = ".flac",
        AudioEncoders = [Flac],
    };

    public static readonly OutputFormat OggOpus = new()
    {
        Id = "opus", Label = "Opus audio", Extension = ".opus",
        AudioEncoders = [Opus],
    };

    /// <summary>One frame as a PNG: what the Convert tab's preview shows. Not offered as a format.</summary>
    internal static readonly OutputFormat PngFrame = new()
    {
        Id = "png-frame", Label = "PNG frame", Extension = ".png",
        VideoEncoders = [new EncoderChoice("png", _ => [])],
        PixelFormat = "rgb24", IsStill = true,
        ExtraArguments = ["-frames:v", "1", "-update", "1"],
    };

    public static IReadOnlyList<OutputFormat> All { get; } =
        [Mp4H264, Mp4H265, Mp4Av1, WebmVp9, WebmAv1, MkvH264, MovProRes, DvdVideo, Gif, Mp3, M4a, Wav, FlacAudio, OggOpus];

    public static IEnumerable<OutputFormat> VideoFormats => All.Where(f => f.HasVideo);
    public static IEnumerable<OutputFormat> AudioFormats => All.Where(f => !f.HasVideo);

    public static OutputFormat? Find(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>Formats this ffmpeg build can write.</summary>
    public static IEnumerable<OutputFormat> Available(FfmpegCapabilities caps) =>
        All.Where(f => (!f.HasVideo || f.IsGif || f.PickVideoEncoder(caps) != null) && (!f.HasAudio || f.PickAudioEncoder(caps) != null));
}
