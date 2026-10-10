using System.Diagnostics;
using System.Text.RegularExpressions;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Media;

/// <summary>
/// Finds the ffmpeg and ffprobe executables. Order: MYVIDEOEDITOR_FFMPEG_DIR, the copy bundled
/// with the app (an "ffmpeg" folder next to it), then PATH. Release builds bundle a GPL build of
/// FFmpeg; development builds normally use whatever is on PATH.
/// </summary>
public sealed partial class FfmpegLocator
{
    public const string DirEnvironmentVariable = "MYVIDEOEDITOR_FFMPEG_DIR";

    private static readonly Lazy<FfmpegLocator> DefaultInstance = new(() => Find(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static FfmpegLocator Default => DefaultInstance.Value;

    public string FfmpegPath { get; }
    public string FfprobePath { get; }
    public bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);

    private readonly Lazy<FfmpegCapabilities> _capabilities;

    /// <summary>Encoders, filters and capture devices this ffmpeg build supports (queried once).</summary>
    public FfmpegCapabilities Capabilities => _capabilities.Value;

    public FfmpegLocator(string ffmpegPath, string ffprobePath)
    {
        FfmpegPath = ffmpegPath;
        FfprobePath = ffprobePath;
        _capabilities = new Lazy<FfmpegCapabilities>(() => FfmpegCapabilities.Query(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static FfmpegLocator Find()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(DirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"));
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        foreach (var dir in candidates)
        {
            var ffmpeg = Path.Combine(dir, "ffmpeg" + exe);
            var ffprobe = Path.Combine(dir, "ffprobe" + exe);
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                Log.Info("ffmpeg", $"Using ffmpeg in {dir}.");
                return new FfmpegLocator(ffmpeg, ffprobe);
            }
        }
        Log.Warn("ffmpeg", "ffmpeg/ffprobe not found.");
        return new FfmpegLocator("ffmpeg" + exe, "ffprobe" + exe);
    }

    /// <summary>Runs a short query (e.g. "-encoders") and returns stdout. Empty on failure.</summary>
    internal string Query(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(FfmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-hide_banner");
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = ChildProcess.Start(psi, isFfmpeg: true);
            var stderr = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return output + stderr.Result;
        }
        catch (Exception ex)
        {
            Log.Warn("ffmpeg", $"ffmpeg {string.Join(' ', args)} failed.", ex);
            return "";
        }
    }
}

/// <summary>What the located ffmpeg build can do.</summary>
public sealed partial class FfmpegCapabilities
{
    public string Version { get; init; } = "";
    public int MajorVersion { get; init; }
    public IReadOnlySet<string> Encoders { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> Filters { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> InputDevices { get; init; } = new HashSet<string>();

    public bool HasEncoder(string name) => Encoders.Contains(name);
    public bool HasFilter(string name) => Filters.Contains(name);
    public bool HasInputDevice(string name) => InputDevices.Contains(name);

    internal static FfmpegCapabilities Query(FfmpegLocator locator)
    {
        var version = locator.Query("-version");
        var firstLine = version.Split('\n').FirstOrDefault() ?? "";
        var m = VersionRegex().Match(firstLine);
        var major = 0;
        if (m.Success)
            int.TryParse(m.Groups[1].Value, out major);
        var caps = new FfmpegCapabilities
        {
            Version = firstLine.Trim(),
            MajorVersion = major,
            Encoders = ParseList(locator.Query("-encoders"), EncoderRegex()),
            Filters = ParseList(locator.Query("-filters"), FilterRegex()),
            InputDevices = ParseList(locator.Query("-devices"), DeviceRegex()),
        };
        Log.Info("ffmpeg", $"{caps.Version}; {caps.Encoders.Count} encoders, {caps.Filters.Count} filters, input devices: {string.Join(", ", caps.InputDevices)}.");
        return caps;
    }

    internal static HashSet<string> ParseList(string text, Regex line)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var m = line.Match(raw);
            if (m.Success)
                foreach (var name in m.Groups[1].Value.Split(','))
                    set.Add(name.Trim());
        }
        return set;
    }

    // "ffmpeg version 6.1.1-3ubuntu5", "ffmpeg version n8.1-12-g…", "ffmpeg version N-12345-g…" (git: 0).
    [GeneratedRegex(@"version n?(\d+)\.")]
    private static partial Regex VersionRegex();

    // " V....D libx264              libx264 H.264 ..."
    [GeneratedRegex(@"^\s[VAS][F.][S.][X.][B.][D.]\s+(\S+)\s")]
    private static partial Regex EncoderRegex();

    // " TSC drawtext          V->V       Draw text ..." (flags column is 3 chars, older builds have 2)
    [GeneratedRegex(@"^\s[T.][S.][C.]?\s+(\S+)\s+\S+->\S+")]
    private static partial Regex FilterRegex();

    // " D  x11grab         X11 screen capture" / " DE pulse ..." (demuxing-capable devices only)
    [GeneratedRegex(@"^\s+D[E ]?\s+(\S+)\s")]
    private static partial Regex DeviceRegex();
}
