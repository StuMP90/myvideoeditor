using System.Diagnostics;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.App.Services;

/// <summary>Opens files and folders with the system's default apps.</summary>
public static class Shell
{
    public static void Open(string path)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("shell", $"Couldn't open {path}.", ex);
        }
    }

    /// <summary>Shows the file in the file manager (selected, on Windows).</summary>
    public static void ShowInFolder(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Explorer wants /select,"path": quoting the whole argument (as ArgumentList does for
                // paths with spaces) makes it ignore it and open Documents.
                var full = Path.GetFullPath(file);
                if (File.Exists(full))
                    Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{full}\"", UseShellExecute = false });
                else
                    Open(Path.GetDirectoryName(full) ?? full);
            }
            else
                Open(Path.GetDirectoryName(file) ?? file);
        }
        catch (Exception ex)
        {
            Log.Warn("shell", $"Couldn't show {file}.", ex);
        }
    }

    /// <summary>"2.4 MB".</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
