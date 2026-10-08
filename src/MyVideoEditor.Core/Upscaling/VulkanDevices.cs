using System.Text.RegularExpressions;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.Core.Upscaling;

public enum GpuKind
{
    Discrete,
    Integrated,
    Virtual,
    /// <summary>A CPU pretending to be a GPU (Mesa llvmpipe, SwiftShader): far too slow for real work.</summary>
    Software,
    Other,
}

/// <summary>A Vulkan device. <see cref="Index"/> is the order Vulkan lists it in, used by ffmpeg and the ncnn tools alike.</summary>
public sealed record GpuDevice(int Index, string Name, GpuKind Kind)
{
    public override string ToString() => $"{Name} ({Kind.ToString().ToLowerInvariant()})";
}

/// <summary>
/// The Vulkan GPUs on this computer, as the bundled ffmpeg sees them. GPU upscaling uses the
/// best real one: dedicated, then integrated. Software renderers are never chosen, unless
/// MYVIDEOEDITOR_ALLOW_SOFTWARE_GPU=1 (tests and CI, where they're the only "GPU").
/// </summary>
public static partial class VulkanDevices
{
    public const string AllowSoftwareEnvironmentVariable = "MYVIDEOEDITOR_ALLOW_SOFTWARE_GPU";

    private static readonly Lazy<IReadOnlyList<GpuDevice>> Cached = new(() => Detect(FfmpegLocator.Default), LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<GpuDevice> All => Cached.Value;

    /// <summary>The device to use, or null if there's no usable Vulkan GPU.</summary>
    public static GpuDevice? Best => Choose(All, AllowSoftware);

    public static bool AllowSoftware => Environment.GetEnvironmentVariable(AllowSoftwareEnvironmentVariable) == "1";

    internal static GpuDevice? Choose(IEnumerable<GpuDevice> devices, bool allowSoftware) =>
        devices.Where(d => allowSoftware || d.Kind != GpuKind.Software)
            .OrderBy(d => d.Kind switch { GpuKind.Discrete => 0, GpuKind.Integrated => 1, GpuKind.Virtual => 2, GpuKind.Other => 3, _ => 4 })
            .ThenBy(d => d.Index)
            .FirstOrDefault();

    internal static IReadOnlyList<GpuDevice> Detect(FfmpegLocator locator)
    {
        // Asking for a device that can't exist makes ffmpeg print the list it chose from.
        var output = locator.Query("-v", "verbose", "-init_hw_device", "vulkan=probe:999", "-f", "lavfi", "-i", "nullsrc", "-frames:v", "1", "-f", "null", "-");
        var devices = Parse(output);
        Log.Info("gpu", devices.Count == 0 ? "No Vulkan devices found." : "Vulkan devices: " + string.Join("; ", devices));
        return devices;
    }

    /// <summary>Parses ffmpeg's "GPU listing:" lines, e.g. "    0: Intel(R) Iris(R) Xe Graphics (ADL GT2) (integrated) (0x46a6)".</summary>
    internal static List<GpuDevice> Parse(string ffmpegOutput)
    {
        var list = new List<GpuDevice>();
        foreach (var line in ffmpegOutput.Split('\n'))
        {
            var m = DeviceLine().Match(line);
            if (!m.Success)
                continue;
            var kind = m.Groups["kind"].Value switch
            {
                "discrete" => GpuKind.Discrete,
                "integrated" => GpuKind.Integrated,
                "virtual" => GpuKind.Virtual,
                "software" or "cpu" => GpuKind.Software,
                _ => GpuKind.Other,
            };
            list.Add(new GpuDevice(int.Parse(m.Groups["index"].Value), m.Groups["name"].Value.Trim(), kind));
        }
        return list;
    }

    [GeneratedRegex(@"\]\s+(?<index>\d+): (?<name>.+) \((?<kind>integrated|discrete|virtual|software|cpu|unknown|other)\) \(0x[0-9a-fA-F]+\)\s*$")]
    private static partial Regex DeviceLine();
}
