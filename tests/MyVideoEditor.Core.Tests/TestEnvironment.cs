using System.Runtime.CompilerServices;

namespace MyVideoEditor.Core.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Runs before any test: points the app's data folder (logs, caches) at a temp folder, so
    /// test runs never write into the real one.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryDataFolder()
    {
        Environment.SetEnvironmentVariable("MYVIDEOEDITOR_DATA_DIR", Path.Combine(Path.GetTempPath(), "myvideoeditor-test-data"));
        // Downloaded upscalers are shared between runs (they're large), but never with the real app.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MYVIDEOEDITOR_TOOLS_DIR")))
            Environment.SetEnvironmentVariable("MYVIDEOEDITOR_TOOLS_DIR", Path.Combine(Path.GetTempPath(), "myvideoeditor-test-tools"));
    }
}
