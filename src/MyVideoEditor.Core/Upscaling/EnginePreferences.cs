using System.Text.Json;
using MyVideoEditor.Core.Conversion;

namespace MyVideoEditor.Core.Upscaling;

/// <summary>
/// Which engine was faster on this computer, for methods that can run on either (the animation
/// model: ONNX on the CPU, or the Vulkan program on the graphics card). Set by the speed test;
/// used when the AI device is Automatic.
/// </summary>
public static class EnginePreferences
{
    private static readonly object Lock = new();
    private static string File => Path.Combine(AppPaths.DataDirectory, "engine-preferences.json");

    /// <summary>True if the graphics card was faster, false if the CPU was, null if not measured.</summary>
    public static bool? Get(ScaleAlgorithm algorithm)
    {
        lock (Lock)
            return Load().TryGetValue(algorithm.ToString(), out var gpu) ? gpu : null;
    }

    public static void Set(ScaleAlgorithm algorithm, bool gpuFaster)
    {
        lock (Lock)
        {
            var all = Load();
            all[algorithm.ToString()] = gpuFaster;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                System.IO.File.WriteAllText(File, JsonSerializer.Serialize(all));
            }
            catch (IOException) { }
        }
    }

    private static Dictionary<string, bool> Load()
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Dictionary<string, bool>>(System.IO.File.ReadAllText(File)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }
}
