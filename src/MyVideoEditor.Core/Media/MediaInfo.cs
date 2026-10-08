namespace MyVideoEditor.Core.Media;

public enum MediaKind
{
    Video,
    Audio,
    Image,
}

/// <summary>What ffprobe found in a media file.</summary>
public sealed record MediaInfo
{
    public required string Path { get; init; }
    public MediaKind Kind { get; init; }
    public string FormatName { get; init; } = "";
    /// <summary>Length in seconds; 0 for still images.</summary>
    public double Duration { get; init; }
    public long SizeBytes { get; init; }
    public long BitRate { get; init; }

    public bool HasVideo { get; init; }
    /// <summary>Display size: after the rotation metadata (phone videos) and sample aspect ratio are applied.</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public double FrameRate { get; init; }
    /// <summary>The exact frame rate as ffmpeg writes it ("30000/1001"); use this in filters, not <see cref="FrameRate"/>, to avoid drift.</summary>
    public string FrameRateText { get; init; } = "30";
    /// <summary>Colour tags as ffprobe reports them ("bt709", "tv"…); empty when the file doesn't say.</summary>
    public string ColorSpace { get; init; } = "";
    public string ColorRange { get; init; } = "";
    public string ColorPrimaries { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string VideoCodec { get; init; } = "";
    public string PixelFormat { get; init; } = "";
    /// <summary>Rotation in degrees (0, 90, 180, 270) from the file's display matrix. ffmpeg applies it when decoding.</summary>
    public int Rotation { get; init; }

    public bool HasAudio { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public string AudioCodec { get; init; } = "";

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HasVideo)
                parts.Add(Kind == MediaKind.Image ? $"{Width}×{Height} image" : $"{Width}×{Height} {FrameRate:0.##} fps {VideoCodec}");
            if (HasAudio)
                parts.Add($"{SampleRate / 1000.0:0.#} kHz {(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : Channels + " ch")} {AudioCodec}");
            if (Kind != MediaKind.Image)
                parts.Add(Timecode.Format(Duration));
            return string.Join(" · ", parts);
        }
    }
}
