using System.Text.Json;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.App.Services;

/// <summary>Preferences and recent files, saved as JSON in the data folder.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<string> RecentProjects { get; set; } = [];
    public string? LastMediaFolder { get; set; }
    public string? LastExportFolder { get; set; }
    public string ExportFormat { get; set; } = "mp4-h264";
    public string ExportQuality { get; set; } = "Standard";
    public string ConvertFormat { get; set; } = "mp4-h264";
    public string ConvertScaling { get; set; } = "Lanczos";
    public string ConvertContent { get; set; } = "Camera";
    public string? CaptureFolder { get; set; }
    public double CaptureFrameRate { get; set; } = 30;
    public bool CaptureCursor { get; set; } = true;
    public int CaptureCountdown { get; set; } = 3;
    public bool CaptureMinimise { get; set; } = true;
    public List<string> CaptureAudioDevices { get; set; } = [];
    public bool SnapEnabled { get; set; } = true;
    public double PreviewVolume { get; set; } = 1.0;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("settings", "Settings couldn't be read; using defaults.", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("settings", "Settings couldn't be saved.", ex);
        }
    }

    public void AddRecentProject(string path)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
        RecentProjects.Insert(0, path);
        if (RecentProjects.Count > 10)
            RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
        Save();
    }
}
