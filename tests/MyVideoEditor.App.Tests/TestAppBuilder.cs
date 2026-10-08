using Avalonia;
using Avalonia.Headless;
using MyVideoEditor.App;
using MyVideoEditor.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace MyVideoEditor.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not headless drawing) so screenshots show actual output.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>The real app's styles, without opening its main window (tests open their own).</summary>
public class TestApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
    }
}
