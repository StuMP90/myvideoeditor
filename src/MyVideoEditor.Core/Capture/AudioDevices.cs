using System.Diagnostics;
using System.Text.RegularExpressions;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Capture;

/// <summary>
/// Audio inputs that can be recorded with a capture. Linux: PulseAudio/PipeWire sources (via
/// pactl), including ".monitor" sources, which carry what the computer is playing. Windows:
/// DirectShow audio devices.
/// </summary>
public static partial class AudioDevices
{
    public const string LinuxDefaultMicrophone = "default";
    public const string LinuxDefaultMonitor = "@DEFAULT_MONITOR@";

    public static async Task<IReadOnlyList<AudioDevice>> ListAsync(FfmpegLocator? locator = null)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                return await ListPulseAsync().ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
                return await ListDirectShowAsync(locator ?? FfmpegLocator.Default).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("capture", "Couldn't list audio devices.", ex);
        }
        return [];
    }

    private static async Task<IReadOnlyList<AudioDevice>> ListPulseAsync()
    {
        var devices = new List<AudioDevice>
        {
            new(LinuxDefaultMicrophone, "Default microphone", false),
            new(LinuxDefaultMonitor, "Computer sound (default output)", true),
        };
        var output = await RunAsync("pactl", ["list", "short", "sources"]).ConfigureAwait(false);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            var name = parts[1];
            var monitor = name.EndsWith(".monitor", StringComparison.Ordinal);
            devices.Add(new AudioDevice(name, (monitor ? "Sound from " : "Input: ") + Friendly(name), monitor));
        }
        return devices;
    }

    private static string Friendly(string pulseName)
    {
        var name = pulseName.Replace(".monitor", "");
        var dot = name.LastIndexOf('.');
        name = dot > 0 ? name[(name.IndexOf('.') + 1)..] : name;
        return name.Replace('_', ' ').Replace('-', ' ');
    }

    private static async Task<IReadOnlyList<AudioDevice>> ListDirectShowAsync(FfmpegLocator locator)
    {
        var output = await RunAsync(locator.FfmpegPath, ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"]).ConfigureAwait(false);
        var devices = new List<AudioDevice>();
        foreach (var line in output.Split('\n'))
        {
            var m = DshowAudioLine().Match(line);
            if (!m.Success)
                continue;
            var name = m.Groups[1].Value;
            var loopback = name.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("What U Hear", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("virtual-audio-capturer", StringComparison.OrdinalIgnoreCase);
            devices.Add(new AudioDevice(name, name, loopback));
        }
        return devices;
    }

    [GeneratedRegex("\"(.+)\" \\(audio\\)")]
    private static partial Regex DshowAudioLine();

    private static async Task<string> RunAsync(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            return await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ""; // pactl not installed
        }
    }
}
