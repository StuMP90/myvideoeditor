using System.Diagnostics;
using static MyVideoEditor.Core.Rendering.RenderPlan;

namespace MyVideoEditor.Core.Media;

/// <summary>A decoded picture: BGRA pixels.</summary>
public sealed record GrabbedFrame(int Width, int Height, byte[] Bgra);

/// <summary>Fetches single frames from a file quickly (scrubbing in the Convert tab's preview).</summary>
public sealed class FrameGrabber
{
    private readonly FfmpegLocator _locator;

    public FrameGrabber(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    /// <summary>
    /// The frame at <paramref name="time"/>, rotated as given and shrunk to fit within
    /// <paramref name="maxWidth"/>×<paramref name="maxHeight"/>. Null if there's no frame there.
    /// </summary>
    public async Task<GrabbedFrame?> GrabAsync(MediaInfo info, double time, int rotate, int maxWidth, int maxHeight, CancellationToken cancellationToken = default)
    {
        var (w, h) = rotate % 180 == 0 ? (info.Width, info.Height) : (info.Height, info.Width);
        var scale = Math.Min(1, Math.Min((double)maxWidth / w, (double)maxHeight / h));
        var ow = Math.Max(2, (int)(w * scale) & ~1);
        var oh = Math.Max(2, (int)(h * scale) & ~1);
        var filters = Conversion.Converter.RotationFilters(rotate).Append($"scale={ow}:{oh}:flags=bicubic").Append("format=bgra");
        var psi = new ProcessStartInfo(_locator.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-ss", F(Math.Max(0, time)), "-i", info.Path, "-frames:v", "1",
                     "-vf", string.Join(',', filters), "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await using var reg = cancellationToken.Register(() =>
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
        });
        var buffer = new byte[ow * oh * 4];
        var total = 0;
        try
        {
            while (total < buffer.Length)
            {
                var n = await p.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                    break;
                total += n;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return total == buffer.Length ? new GrabbedFrame(ow, oh, buffer) : null;
    }
}
