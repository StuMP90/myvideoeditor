using System.Diagnostics;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Playback;

/// <summary>An ffmpeg process whose output is read from stdout (raw frames or samples).</summary>
internal sealed class FfmpegStream : IDisposable
{
    private readonly Process _process;
    private readonly Queue<string> _errors = new();

    public FfmpegStream(FfmpegLocator locator, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(locator.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffmpeg.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    lock (_errors)
                    {
                        _errors.Enqueue(line);
                        if (_errors.Count > 20)
                            _errors.Dequeue();
                    }
                }
            }
            catch (Exception) { /* process gone */ }
        });
    }

    public Stream Output => _process.StandardOutput.BaseStream;

    public string Errors
    {
        get
        {
            lock (_errors)
                return string.Join('\n', _errors);
        }
    }

    /// <summary>Reads exactly buffer.Length bytes; false at end of stream.</summary>
    public bool ReadExactly(Span<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Output.Read(buffer[total..]);
            if (n == 0)
                return false;
            total += n;
        }
        return true;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("playback", "Stopping ffmpeg failed.", ex);
        }
        _process.Dispose();
    }
}
