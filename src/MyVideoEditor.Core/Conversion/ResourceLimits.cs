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

    /// <summary>
    /// Keeps the whole app, and every process it starts, on <see cref="Threads"/> of the processors it may
    /// use while this lasts. The thread caps above apply to each part of a job separately (decoder, model,
    /// encoder), so together they can still fill the computer when every part is busy, as with a fast
    /// model; this caps the total. Child processes inherit it from their first instruction (Linux and
    /// Windows). Dispose to restore it. An uncapped job lifts other jobs' caps while it runs, as it would
    /// otherwise be held to them.
    /// </summary>
    public IDisposable PinApp() =>
        OperatingSystem.IsLinux() || OperatingSystem.IsWindows() ? CpuPin.Enter(IsCapped ? Threads : CpuPin.All) : Nothing.Instance;

    [DllImport("libc", SetLastError = true)]
    private static extern int setpriority(int which, int who, int prio);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_getaffinity(int pid, nint size, ulong[] mask);

    [DllImport("libc", SetLastError = true)]
    private static extern int sched_setaffinity(int pid, nint size, ulong[] mask);

    [DllImport("libc")]
    private static extern long syscall(long number);

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// The app's processor affinity while any capped job runs (counted, so overlapping jobs work: the
    /// largest cap applies). Uses the first processors of those the app was allowed when the first job
    /// started, so it never escapes a restriction it was started with (taskset, a container).
    /// </summary>
    private sealed class CpuPin : IDisposable
    {
        private const int Words = 16; // 1024 processors, glibc's cpu_set_t
        private static readonly object Lock = new();
        private static readonly List<CpuPin> Active = [];
        private static ulong[]? _original;
        private readonly int _threads;
        private bool _disposed;

        private CpuPin(int threads) => _threads = threads;

        /// <summary>No cap: all the processors the app was allowed.</summary>
        public const int All = int.MaxValue;
        private static int _applied = All;

        public static CpuPin Enter(int threads)
        {
            var pin = new CpuPin(threads);
            lock (Lock)
            {
                Active.Add(pin);
                Apply();
            }
            return pin;
        }

        public void Dispose()
        {
            lock (Lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                Active.Remove(this);
                Apply();
            }
        }

        /// <summary>Applies the largest cap among running jobs (all processors if any is uncapped or none run).</summary>
        private static void Apply()
        {
            var wanted = Active.Count == 0 ? All : Active.Max(p => p._threads);
            if (wanted == _applied)
                return;
            try
            {
                _original ??= Current();
                Set(wanted == All ? _original : FirstOf(_original, wanted));
                _applied = wanted;
                if (wanted == All)
                    _original = null; // read afresh next time, in case it was changed from outside meanwhile
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                Log.Warn("limits", "Couldn't set the processors the app may use.", ex);
            }
        }

        /// <summary>The first <paramref name="count"/> processors set in <paramref name="allowed"/>.</summary>
        internal static ulong[] FirstOf(ulong[] allowed, int count)
        {
            var mask = new ulong[allowed.Length];
            for (var bit = 0; bit < allowed.Length * 64 && count > 0; bit++)
            {
                if ((allowed[bit / 64] & (1UL << (bit % 64))) != 0)
                {
                    mask[bit / 64] |= 1UL << (bit % 64);
                    count--;
                }
            }
            return mask;
        }

        private static ulong[] Current()
        {
            if (OperatingSystem.IsWindows())
            {
                using var me = Process.GetCurrentProcess();
                return [(ulong)me.ProcessorAffinity];
            }
            var mask = new ulong[Words];
            if (sched_getaffinity(Environment.ProcessId, Words * 8, mask) != 0)
                throw new InvalidOperationException($"sched_getaffinity failed ({Marshal.GetLastPInvokeError()}).");
            return mask;
        }

        private static void Set(ulong[] mask)
        {
            if (OperatingSystem.IsWindows())
            {
                // The whole process, its existing and future threads; child processes inherit it.
                using var me = Process.GetCurrentProcess();
                me.ProcessorAffinity = (nint)mask[0];
                return;
            }
            // Linux: affinity is per thread, and new threads and child processes inherit their creator's.
            // A second pass catches threads started during the first.
            for (var pass = 0; pass < 2; pass++)
                foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
                    if (int.TryParse(Path.GetFileName(dir), out var tid))
                        sched_setaffinity(tid, Words * 8, mask); // fails only for threads that have just ended
        }
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
