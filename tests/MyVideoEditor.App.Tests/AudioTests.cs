using MyVideoEditor.App.Services;

namespace MyVideoEditor.App.Tests;

public class AudioTests
{
    /// <summary>The bundled OpenAL Soft loads and its clock advances (when the machine has a sound device).</summary>
    [Fact]
    public void OpenAl_plays_and_reports_its_position()
    {
        using var sink = OpenAlAudioSink.TryCreate();
        if (sink == null)
            return; // no sound device (CI)
        sink.Volume = 0;
        var silence = new float[sink.SampleRate / 10 * 2]; // 0.1 s of stereo
        for (var i = 0; i < 5; i++)
            sink.Write(silence, CancellationToken.None);
        Assert.True(sink.Started);
        Thread.Sleep(300);
        Assert.InRange(sink.PlayedSeconds, 0.05, 0.6);
        sink.Reset();
        Assert.False(sink.Started);
        Assert.Equal(0, sink.PlayedSeconds);
    }
}
