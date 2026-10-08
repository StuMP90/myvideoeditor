using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Tests;

public class ConversionTests
{
    private static async Task<MediaInfo> ConvertAsync(string input, string extension, ConversionSettings settings)
    {
        var output = Path.Combine(TestMedia.NewOutputDirectory(), "converted" + extension);
        await new Converter().ConvertAsync(input, output, settings);
        return await MediaProbe.Default.ProbeAsync(output);
    }

    [Fact]
    public async Task Converts_between_formats()
    {
        var input = await TestMedia.PatternAsync(2, 320, 240);
        var webm = await ConvertAsync(input, ".webm", new ConversionSettings { Format = OutputFormats.WebmVp9, Quality = Quality.Small });
        Assert.Equal("vp9", webm.VideoCodec);
        Assert.True(webm.HasAudio);

        var mp3 = await ConvertAsync(input, ".mp3", new ConversionSettings { Format = OutputFormats.Mp3 });
        Assert.False(mp3.HasVideo);
        Assert.Equal("mp3", mp3.AudioCodec);
    }

    [Fact]
    public async Task Resizes_with_each_mode_and_upscales()
    {
        var input = await TestMedia.PatternAsync(1, 320, 240);
        var fit = await ConvertAsync(input, ".mp4", new ConversionSettings { Format = OutputFormats.Mp4H264, Width = 640, Height = 360, ResizeMode = ResizeMode.Fit });
        Assert.Equal((640, 360), (fit.Width, fit.Height));
        var oneSide = await ConvertAsync(input, ".mp4", new ConversionSettings { Format = OutputFormats.Mp4H264, Width = 960, Algorithm = ScaleAlgorithm.Enhanced });
        Assert.Equal((960, 720), (oneSide.Width, oneSide.Height));
        var fill = await ConvertAsync(input, ".mp4", new ConversionSettings { Format = OutputFormats.Mp4H264, Width = 200, Height = 200, ResizeMode = ResizeMode.Fill, FrameRate = 15 });
        Assert.Equal((200, 200), (fill.Width, fill.Height));
        Assert.Equal(15, fill.FrameRate, 1);
    }

    [Fact]
    public async Task Trims_rotates_and_remuxes()
    {
        var input = await TestMedia.PatternAsync(4, 320, 240);
        var trimmed = await ConvertAsync(input, ".mp4", new ConversionSettings { Format = OutputFormats.Mp4H264, TrimStart = 1, TrimEnd = 2.5, Rotate = 90 });
        Assert.InRange(trimmed.Duration, 1.4, 1.6);
        Assert.Equal((240, 320), (trimmed.Width, trimmed.Height));

        var remuxed = await ConvertAsync(input, ".mkv", new ConversionSettings { Format = OutputFormats.MkvH264, Remux = true });
        var original = await MediaProbe.Default.ProbeAsync(input);
        Assert.Equal(original.VideoCodec, remuxed.VideoCodec);
        Assert.Contains("matroska", remuxed.FormatName);
    }

    [Fact]
    public async Task Refuses_to_overwrite_the_input()
    {
        var input = await TestMedia.PatternAsync(1, 320, 240);
        await Assert.ThrowsAsync<ArgumentException>(() => new Converter().ConvertAsync(input, input, new ConversionSettings { Format = OutputFormats.Mp4H264 }));
    }

    [Fact]
    public void Scale_filters_for_each_mode()
    {
        Assert.Null(Converter.ScaleFilter(null, null, ResizeMode.Fit, "lanczos"));
        Assert.Equal("scale=-2:720:flags=lanczos", Converter.ScaleFilter(null, 720, ResizeMode.Fit, "lanczos"));
        Assert.Contains("pad=1920:1080", Converter.ScaleFilter(1920, 1080, ResizeMode.Fit, "lanczos"));
        Assert.Contains("crop=1080:1080", Converter.ScaleFilter(1080, 1080, ResizeMode.Fill, "lanczos"));
        // Odd sizes are made even (4:2:0 video needs them).
        Assert.Equal("scale=1279:-2:flags=bicubic".Replace("1279", "1278"), Converter.ScaleFilter(1279, null, ResizeMode.Fit, "bicubic"));
    }
}
