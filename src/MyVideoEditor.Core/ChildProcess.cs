using System.Diagnostics;

namespace MyVideoEditor.Core;

/// <summary>
/// Starts helper programs (ffmpeg, ffprobe, upscalers, pactl…) without giving them the terminal.
///
/// ffmpeg switches a terminal it's given as input into a mode without echo, so it can read a "q"
/// key, and restores it when it exits. The app stops ffmpeg early all the time (scrubbing,
/// previews, cancelled jobs), and a killed ffmpeg never restores it: run from a terminal, the
/// app would leave it with typing invisible. So every child gets a pipe as its input instead, closed
/// straight away unless the caller writes to it, and ffmpeg is also told not to read input.
/// </summary>
public static class ChildProcess
{
    /// <summary>
    /// Starts <paramref name="psi"/> with its input a pipe. Unless <paramref name="keepInput"/>, the pipe
    /// is closed (and ffmpeg gets -nostdin, if <paramref name="isFfmpeg"/>).
    /// </summary>
    public static Process Start(ProcessStartInfo psi, bool keepInput = false, bool isFfmpeg = false)
    {
        psi.RedirectStandardInput = true;
        psi.UseShellExecute = false;
        if (isFfmpeg && !keepInput && !psi.ArgumentList.Contains("-nostdin"))
            psi.ArgumentList.Insert(0, "-nostdin");
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {Path.GetFileName(psi.FileName)}.");
        if (!keepInput)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // It has already exited.
            }
        }
        return process;
    }
}
