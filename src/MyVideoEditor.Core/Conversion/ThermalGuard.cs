using System.Diagnostics;
using System.Runtime.InteropServices;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Conversion;

/// <summary>
/// Keeps the CPU package below a temperature while conversions run (<see cref="ResourceLimits.MaxTemperature"/>):
/// reads it four times a second, averaged over about 3 s so short single-core boosts don't count, and
/// pauses the conversions' child processes (ffmpeg, the AI programs) while it's too hot: above the
/// limit on average, or at once when two readings in a row reach <see cref="PeakAbove"/> degrees over it. They continue
/// once the average is <see cref="ResumeBelow"/> degrees under the limit. The ONNX models run in the
/// app, which is never paused: they wait between frames instead (<see cref="WaitWhileHot"/>).
///
/// Linux only (the x86_pkg_temp sensor, SIGSTOP and SIGCONT). Elsewhere, or without the sensor,
/// <see cref="IsAvailable"/> is false and nothing is watched.
/// </summary>
public sealed class ThermalGuard : IDisposable
{
    public const double ResumeBelow = 5;
    public const double PeakAbove = 8;
    private const int SigCont = 18, SigStop = 19;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private static readonly object Lock = new();
    private static readonly List<ThermalGuard> Active = [];
    private static readonly List<Process> Watched = [];
    private static readonly ManualResetEventSlim Cool = new(true);
    private static readonly Lazy<string?> SensorPath = new(FindSensor);
    private static CancellationTokenSource? _loop;
    private static bool _paused;

    private readonly double _limit;
    private bool _disposed;

    private ThermalGuard(double limit) => _limit = limit;

    /// <summary>Whether the temperature can be read and processes paused here.</summary>
    public static bool IsAvailable => OperatingSystem.IsLinux() && SensorPath.Value != null;

    /// <summary>Watches the temperature until disposed (counted: overlapping jobs use the lowest limit).</summary>
    internal static ThermalGuard Enter(double limit)
    {
        var guard = new ThermalGuard(limit);
        lock (Lock)
        {
            Active.Add(guard);
            if (_loop == null)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                _ = Task.Run(() => RunAsync(token));
            }
        }
        return guard;
    }

    public void Dispose()
    {
        lock (Lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Active.Remove(this);
            if (Active.Count > 0)
                return;
            _loop?.Cancel();
            _loop = null;
            SetPaused(false);
            Watched.Clear();
        }
    }

    /// <summary>Pauses <paramref name="process"/> with the others while it's too hot (only while a guard is active).</summary>
    internal static void Watch(Process process)
    {
        lock (Lock)
        {
            if (Active.Count == 0)
                return;
            Watched.Add(process);
            if (_paused)
                Signal(process, SigStop);
        }
    }

    /// <summary>Continues <paramref name="process"/> and stops pausing it (before asking it to finish, or killing it).</summary>
    internal static void Release(Process process)
    {
        lock (Lock)
        {
            if (Watched.Remove(process) && _paused)
                Signal(process, SigCont);
        }
    }

    /// <summary>Blocks the calling (job) thread while it's too hot.</summary>
    public static void WaitWhileHot(CancellationToken cancellationToken) => Cool.Wait(cancellationToken);

    private static async Task RunAsync(CancellationToken token)
    {
        var state = new ThermalState();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var reading = Read();
                if (reading is { } t)
                {
                    double limit;
                    lock (Lock)
                        limit = Active.Count > 0 ? Active.Min(g => g._limit) : double.MaxValue;
                    var hot = state.Update(t, limit);
                    lock (Lock)
                    {
                        if (token.IsCancellationRequested)
                            return;
                        if (hot != _paused)
                            Log.Info("limits", hot
                                ? $"CPU at {t:0}°C (average {state.Average:0}°C, limit {limit:0}°C): pausing the conversion to cool down."
                                : $"CPU down to {state.Average:0}°C: continuing the conversion.");
                        SetPaused(hot); // while paused, again each time: catches processes started meanwhile
                    }
                }
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("limits", "The temperature guard stopped.", ex);
            lock (Lock)
                SetPaused(false);
        }
    }

    /// <summary>Call with the lock held.</summary>
    private static void SetPaused(bool paused)
    {
        Watched.RemoveAll(HasExited);
        if (paused)
        {
            Cool.Reset();
            foreach (var p in Watched)
                Signal(p, SigStop);
        }
        else
        {
            if (_paused)
                foreach (var p in Watched)
                    Signal(p, SigCont);
            Cool.Set();
        }
        _paused = paused;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true; // disposed, or never started
        }
    }

    private static void Signal(Process process, int signal)
    {
        try
        {
            if (process.Id != Environment.ProcessId) // never the app itself
                kill(process.Id, signal);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private static double? Read()
    {
        try
        {
            return SensorPath.Value is { } path ? int.Parse(File.ReadAllText(path).Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1000.0 : null;
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The sysfs file with the CPU package temperature (millidegrees), or null.</summary>
    private static string? FindSensor()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        try
        {
            foreach (var zone in Directory.EnumerateDirectories("/sys/class/thermal", "thermal_zone*").Order(StringComparer.Ordinal))
            {
                try
                {
                    if (File.ReadAllText(Path.Combine(zone, "type")).Trim() == "x86_pkg_temp"
                        && int.TryParse(File.ReadAllText(Path.Combine(zone, "temp")).Trim(), out _))
                        return Path.Combine(zone, "temp");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}

/// <summary>When to pause: the averaged reading and the two-in-a-row peak (see <see cref="ThermalGuard"/>).</summary>
internal sealed class ThermalState
{
    private double _previous;

    public double? Average { get; private set; }
    public bool Hot { get; private set; }

    /// <summary>Adds a reading (°C); returns whether to be paused now.</summary>
    public bool Update(double reading, double limit)
    {
        Average = Average is { } a ? a + (reading - a) * 0.08 : reading; // ~3 s at 4 readings a second
        var held = Math.Min(reading, _previous); // reached by two readings in a row: not a single-core blip
        _previous = reading;
        if (!Hot && (Average >= limit || held >= limit + ThermalGuard.PeakAbove))
            Hot = true;
        else if (Hot && Average <= limit - ThermalGuard.ResumeBelow)
            Hot = false;
        return Hot;
    }
}
