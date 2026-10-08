using System.Diagnostics;
using System.Globalization;
using System.Text;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Media;

/// <summary>Progress of a running ffmpeg job.</summary>
public readonly record struct FfmpegProgress(TimeSpan Position, TimeSpan? Total, double Speed)
{
    /// <summary>0..1, or null when the total length isn't known (e.g. a capture).</summary>
    public double? Fraction => Total is { TotalSeconds: > 0 } t ? Math.Clamp(Position.TotalSeconds / t.TotalSeconds, 0, 1) : null;

    public TimeSpan? Remaining => Total is { } t && Speed > 0.01
        ? TimeSpan.FromSeconds(Math.Max(0, (t - Position).TotalSeconds / Speed))
        : null;
}

public sealed class FfmpegException : Exception
{
    public int ExitCode { get; }
    public string ErrorOutput { get; }

    public FfmpegException(string message, int exitCode, string errorOutput) : base(message)
    {
        ExitCode = exitCode;
        ErrorOutput = errorOutput;
    }

    /// <summary>The most useful line of ffmpeg's output for showing to a person.</summary>
    internal static string Summarise(string errorOutput, string fallback)
    {
        var lines = errorOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var error = lines.LastOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("No such", StringComparison.OrdinalIgnoreCase)
                                             || l.Contains("not ", StringComparison.OrdinalIgnoreCase));
        return error ?? lines.LastOrDefault() ?? fallback;
    }
}

/// <summary>
/// Runs ffmpeg as a child process: arguments are passed as a list (never through a shell),
/// progress is read from "-progress pipe:1", and cancelling asks ffmpeg to stop cleanly ("q" on
/// stdin, so recordings and exports are finalised and playable) before killing it.
/// </summary>
public sealed class FfmpegRunner
{
    private const int StderrTailLines = 60;
    private readonly FfmpegLocator _locator;

    public FfmpegRunner(FfmpegLocator? locator = null) => _locator = locator ?? FfmpegLocator.Default;

    /// <summary>
    /// Runs ffmpeg with the given arguments. Throws <see cref="FfmpegException"/> on failure and
    /// <see cref="OperationCanceledException"/> if cancelled (after ffmpeg has stopped).
    /// When <paramref name="stopGracefully"/> is true, cancellation finishes the output file
    /// instead of abandoning it (used to end a screen recording).
    /// </summary>
    public async Task RunAsync(IReadOnlyList<string> args, TimeSpan? total = null, IProgress<FfmpegProgress>? progress = null,
        CancellationToken cancellationToken = default, bool stopGracefully = false, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(_locator.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-nostats");
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Log.Info("ffmpeg", "Run: " + FormatCommandLine(psi.ArgumentList));
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new FfmpegException($"Couldn't start ffmpeg ({_locator.FfmpegPath}): {ex.Message}", -1, ex.Message);
        }

        var tail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (tail)
                {
                    tail.Enqueue(line);
                    if (tail.Count > StderrTailLines)
                        tail.Dequeue();
                }
            }
        }, CancellationToken.None);

        var stdoutTask = Task.Run(async () =>
        {
            var position = TimeSpan.Zero;
            var speed = 0.0;
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = line[..eq];
                var value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "out_time_us" or "out_time_ms": // both are microseconds (a long-standing ffmpeg quirk)
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                            position = TimeSpan.FromTicks(us * 10);
                        break;
                    case "speed":
                        double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out speed);
                        break;
                    case "progress":
                        progress?.Report(new FfmpegProgress(position, total, speed));
                        break;
                }
            }
        }, CancellationToken.None);

        var cancelled = false;
        await using (cancellationToken.Register(() =>
                     {
                         cancelled = true;
                         _ = StopAsync(process, stopGracefully);
                     }))
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);
        }

        string errorOutput;
        lock (tail)
            errorOutput = string.Join('\n', tail);

        if (cancelled && !stopGracefully)
            throw new OperationCanceledException(cancellationToken);
        // A graceful stop (q) exits with 0, or 255 on some platforms; the output is complete either way.
        if (process.ExitCode != 0 && !(cancelled && stopGracefully))
        {
            Log.Warn("ffmpeg", $"ffmpeg exited with {process.ExitCode}:\n{errorOutput}");
            throw new FfmpegException(FfmpegException.Summarise(errorOutput, $"ffmpeg failed (exit code {process.ExitCode})."),
                process.ExitCode, errorOutput);
        }
        progress?.Report(new FfmpegProgress(total ?? TimeSpan.Zero, total, 0));
    }

    /// <summary>Asks ffmpeg to finish ("q"); kills it if it hasn't within a few seconds.</summary>
    internal static async Task StopAsync(Process process, bool graceful)
    {
        try
        {
            if (process.HasExited)
                return;
            if (graceful)
            {
                await process.StandardInput.WriteAsync('q').ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                var exited = await Task.Run(() => process.WaitForExit(8000)).ConfigureAwait(false);
                if (exited)
                    return;
            }
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Already gone.
        }
    }

    internal static string FormatCommandLine(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length == 0 || a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'' or ';' or '[')
            ? "\"" + a.Replace("\"", "\\\"") + "\""
            : a));
}
