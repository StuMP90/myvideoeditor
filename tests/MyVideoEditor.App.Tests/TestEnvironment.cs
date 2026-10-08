using System.Runtime.CompilerServices;

namespace MyVideoEditor.App.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Runs before any test: points the app's data folder (logs, settings, caches) at a temp
    /// folder, so test runs never touch the real one.
    /// </summary>
    [ModuleInitializer]
    internal static void UseTemporaryDataFolder() =>
        Environment.SetEnvironmentVariable("MYVIDEOEDITOR_DATA_DIR", Path.Combine(Path.GetTempPath(), "myvideoeditor-test-data"));
}
