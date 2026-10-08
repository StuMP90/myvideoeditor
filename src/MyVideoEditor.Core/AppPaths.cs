namespace MyVideoEditor.Core;

/// <summary>
/// Per-user data locations. Windows: %LOCALAPPDATA%\myvideoeditor (redirected into the package's
/// private folder automatically when running as MSIX). Linux: $XDG_CONFIG_HOME/myvideoeditor or
/// ~/.config/myvideoeditor. Caches (thumbnails, waveforms, rendered graphics) go under "cache".
/// </summary>
public static class AppPaths
{
    public const string DataDirEnvironmentVariable = "MYVIDEOEDITOR_DATA_DIR";

    public static string DataDirectory { get; } = Resolve();

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string RecoveryDirectory => Path.Combine(DataDirectory, "autosave");

    /// <summary>Default folder for new captures and exports: the user's Videos folder.</summary>
    public static string VideosDirectory
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos))
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            return videos;
        }
    }

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        string root;
        if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            root = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
        }
        return Path.Combine(root, "myvideoeditor");
    }
}
