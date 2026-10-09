using System.Diagnostics;
using System.Runtime.InteropServices;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Conversion;

/// <summary>Where AI models run.</summary>
public enum AiDevice
{
    /// <summary>Whichever is faster here (from the speed test; otherwise the graphics card only if it's a dedicated one).</summary>
    Auto,
    /// <summary>The CPU: works everywhere.</summary>
    Cpu,
    /// <summary>The graphics card (DirectML on Windows; the Vulkan programs on Linux, where available).</summary>
    Gpu,
}

/// <summary>
/// How much of the computer a conversion may use: a cap on CPU threads (for heat and noise on long
/// jobs), low priority (so the computer stays responsive), and where AI models run.
/// </summary>
public sealed record ResourceLimits(int? MaxThreads = null, bool LowPriority = false, AiDevice Device = AiDevice.Auto)
{
    public static readonly ResourceLimits Unlimited = new();

    /// <summary>Threads to use: the cap, or every logical processor.</summary>
    public int Threads => Math.Clamp(MaxThreads ?? Environment.ProcessorCount, 1, Environment.ProcessorCount);

    public bool IsCapped => Threads < Environment.ProcessorCount;

    /// <summary>
    /// Adds ffmpeg's thread options: decoders (before each input), filters, and the encoder
    /// (before the output, the last argument). Nothing when uncapped (ffmpeg's own defaults).
    /// </summary>
    public List<string> ApplyToFfmpeg(IReadOnlyList<string> args)
    {
        if (!IsCapped)
            return [.. args];
        var n = Threads.ToString();
        var result = new List<string> { "-filter_threads", n, "-filter_complex_threads", n };
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "-i" || (i == args.Count - 1 && args.Count > 1))
                result.AddRange(["-threads", n]);
            result.Add(args[i]);
        }
        return result;
    }

    /// <summary>Lowers a child process's priority (ffmpeg, the AI programs) if asked to.</summary>
    public void Apply(Process process)
    {
        if (!LowPriority)
            return;
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("limits", "Couldn't lower a process's priority.", ex);
        }
    }

    /// <summary>
    /// Linux: lowers the calling thread's priority ("niceness" is per thread there, and threads it
    /// creates inherit it, so ONNX Runtime's worker threads created afterwards on this thread are low
    /// priority too). Only call it on a thread made for the job, never the UI thread.
    /// </summary>
    public void LowerDedicatedThread()
    {
        if (!LowPriority || !OperatingSystem.IsLinux())
            return;
        try
        {
            var tid = (int)syscall(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 178 : 186); // gettid
            if (setpriority(0 /* PRIO_PROCESS: one thread on Linux */, tid, 10) != 0)
                Log.Warn("limits", $"setpriority failed ({Marshal.GetLastPInvokeError()}).");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn("limits", "Couldn't lower the thread's priority.", ex);
        }
    }

    /// <summary>
    /// Windows: the whole app runs at below-normal priority while this lasts (thread pools there
    /// don't inherit a thread's priority). Dispose to restore it. Does nothing elsewhere.
    /// </summary>
    public IDisposable LowerAppOnWindows() =>
        LowPriority && OperatingSystem.IsWindows() ? WindowsPriority.Lower() : Nothing.Instance;

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, int who, int prio);

    [DllImport("libc")]
    private static extern long syscall(long number);

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    /// <summary>Below-normal priority for the app while any low-priority job runs (counted, so overlapping jobs work).</summary>
    private sealed class WindowsPriority : IDisposable
    {
        private static readonly object Lock = new();
        private static int _users;
        private static ProcessPriorityClass _previous;
        private bool _disposed;

        public static WindowsPriority Lower()
        {
            lock (Lock)
            {
                if (_users++ == 0)
                {
                    using var me = Process.GetCurrentProcess();
                    _previous = me.PriorityClass;
                    me.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
            }
            return new WindowsPriority();
        }

        public void Dispose()
        {
            lock (Lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (--_users == 0)
                {
                    using var me = Process.GetCurrentProcess();
                    me.PriorityClass = _previous;
                }
            }
        }
    }
}
