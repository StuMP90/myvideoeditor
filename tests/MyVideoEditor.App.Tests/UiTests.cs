using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyVideoEditor.App.Controls;
using MyVideoEditor.App.Services;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.App.Views;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Tests;

namespace MyVideoEditor.App.Tests;

/// <summary>
/// Drives the real UI headlessly (rendered with Skia, off-screen). Set
/// MYVIDEOEDITOR_SCREENSHOT_DIR to keep screenshots of each step.
/// </summary>
public class UiTests
{
    private static (MainWindow Window, MainWindowViewModel ViewModel) Open(int width = 1440, int height = 900)
    {
        var window = new MainWindow { Width = width, Height = height };
        var vm = new MainWindowViewModel(new DialogService(window), new AppSettings(), audio: null);
        window.DataContext = vm;
        window.Show();
        Pump();
        return (window, vm);
    }

    /// <summary>Lets queued UI work and background renders finish.</summary>
    private static void Pump(int milliseconds = 50)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
    }

    /// <summary>Waits (pumping the UI) until a condition holds.</summary>
    private static void WaitFor(Func<bool> condition, int timeoutMs = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Condition not met in time.");
            Pump(20);
        }
    }

    private static void Await(Task task)
    {
        WaitFor(() => task.IsCompleted, 30000);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Makes test media off the UI thread (blocking the UI thread on its awaits would deadlock).</summary>
    private static string Media(Func<Task<string>> make) => Task.Run(make).GetAwaiter().GetResult();

    private static void Screenshot(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("MYVIDEOEDITOR_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir))
            return;
        Directory.CreateDirectory(dir);
        Pump();
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"));
    }

    [AvaloniaFact]
    public void Starts_with_an_empty_project_on_the_edit_tab()
    {
        var (window, vm) = Open();
        Assert.Equal(0, vm.SelectedTab);
        Assert.Empty(vm.Editor.Media);
        Assert.Contains("Untitled", window.Title);
        Assert.NotNull(window.FindDescendantOfType<TimelineControl>());
        Assert.NotNull(window.FindDescendantOfType<PreviewControl>());
        Screenshot(window, "01-empty");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Importing_media_fills_the_bin_timeline_and_preview()
    {
        var (window, vm) = Open();
        var editor = vm.Editor;
        var video = Media(() => TestMedia.PatternAsync(4, 640, 360));
        var tone = Media(() => TestMedia.ToneAsync(3));
        Await(editor.ImportFilesAsync([video], addToTimeline: true));
        Await(editor.ImportFilesAsync([tone], addToTimeline: true, at: 0.5));

        Assert.Equal(2, editor.Media.Count);
        // The first video sets the project's size.
        Assert.Equal((640, 360), (editor.Project.Settings.Width, editor.Project.Settings.Height));
        Assert.Equal(2, editor.Project.AllClips().Count());
        Assert.Contains("•", window.Title);

        editor.Seek(1.5);
        Await(editor.FlushPreviewAsync());
        Assert.True(editor.PreviewIsCurrent);
        // Thumbnails and waveforms arrive in the background.
        WaitFor(() => editor.Media.Any(m => m.Thumbnail != null));
        editor.Select(editor.Project.AllClips().First().Clip.Id);
        Pump(300);
        Assert.NotNull(editor.Inspector);
        Assert.True(editor.Inspector!.IsMedia);
        Screenshot(window, "02-editing");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Titles_annotations_split_and_undo()
    {
        var (window, vm) = Open();
        var editor = vm.Editor;
        Await(editor.ImportFilesAsync([Media(() => TestMedia.PatternAsync(4, 640, 360))], addToTimeline: true));
        editor.Seek(1);
        editor.AddTitleCommand.Execute(EditorViewModel.TitleTemplates[2]);
        Assert.True(editor.Inspector?.IsTitle);
        editor.Inspector!.Text = "Jane Doe\nSupport";
        editor.AddAnnotation(AnnotationShape.Arrow, 0.2, 0.7, 0.45, 0.4);
        editor.AddAnnotation(AnnotationShape.Blur, 0.6, 0.1, 0.9, 0.3);
        Assert.Equal(4, editor.Project.AllClips().Count());

        editor.Seek(2);
        editor.Select(null);
        editor.SplitCommand.Execute(null); // video, title, arrow and blur are all under the playhead
        Assert.Equal(8, editor.Project.AllClips().Count());
        editor.UndoCommand.Execute(null);
        Assert.Equal(4, editor.Project.AllClips().Count());

        editor.Seek(1.5);
        Await(editor.FlushPreviewAsync());
        Assert.True(editor.PreviewIsCurrent);
        Screenshot(window, "03-annotations");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Drawing_on_the_preview_adds_an_annotation()
    {
        var (window, vm) = Open();
        var editor = vm.Editor;
        Await(editor.ImportFilesAsync([Media(() => TestMedia.PatternAsync(2, 640, 360))], addToTimeline: true));
        editor.ToggleToolCommand.Execute(EditorViewModel.Tools.First(t => t.Shape == AnnotationShape.Rectangle));
        var preview = window.FindDescendantOfType<PreviewControl>()!;
        var origin = preview.TranslatePoint(new Avalonia.Point(0, 0), window)!.Value;
        var b = preview.Bounds;
        var from = origin + new Avalonia.Point(b.Width * 0.4, b.Height * 0.4);
        var to = origin + new Avalonia.Point(b.Width * 0.6, b.Height * 0.6);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Pump();
        var annotation = Assert.Single(editor.Project.AllClips().Select(x => x.Clip).OfType<AnnotationClip>());
        Assert.Equal(AnnotationShape.Rectangle, annotation.Shape);
        Assert.Null(editor.ActiveTool); // the tool is put down after one shape
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Keyboard_shortcuts_play_step_and_delete()
    {
        var (window, vm) = Open();
        var editor = vm.Editor;
        Await(editor.ImportFilesAsync([Media(() => TestMedia.PatternAsync(2, 320, 240))], addToTimeline: true));
        window.FindDescendantOfType<TimelineControl>()!.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(1 / editor.Project.Settings.FrameRate, editor.Playhead, 3);
        editor.Select(editor.Project.AllClips().First().Clip.Id);
        window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Empty(editor.Project.AllClips());
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Assert.Single(editor.Project.AllClips());
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Convert_and_capture_tabs_render()
    {
        var (window, vm) = Open();
        vm.SelectedTab = 1;
        Await(vm.Convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(2, 320, 240))]));
        Assert.Single(vm.Convert.Jobs);
        Assert.True(vm.Convert.CanStart);
        Pump();
        Screenshot(window, "04-convert");
        vm.SelectedTab = 2;
        Pump(300);
        Assert.NotNull(window.FindDescendantOfType<CaptureView>());
        // The headless platform has no monitors; give it one.
        vm.Capture.SetScreens([new Core.Capture.CaptureTarget(Core.Capture.CaptureTargetKind.Screen, "Whole screen", 0, 0, 1920, 1080)]);
        Assert.Equal("Whole screen", vm.Capture.SelectedSource?.Title);
        Screenshot(window, "05-capture");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Converting_a_file_from_the_queue_works()
    {
        var (window, vm) = Open();
        var convert = vm.Convert;
        Await(convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(1, 320, 240))]));
        convert.OutputFolder = TestMedia.NewOutputDirectory();
        convert.SizePreset = "854×480";
        convert.Format = convert.Formats.First(f => f.Id == "webm-vp9");
        Await(convert.StartCommand.ExecuteAsync(null));
        var job = Assert.Single(convert.Jobs);
        Assert.Equal(ConvertState.Done, job.State);
        Assert.True(File.Exists(job.OutputPath));
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Speed_test_times_methods_and_recommends_one()
    {
        var (window, vm) = Open();
        vm.SelectedTab = 1;
        var convert = vm.Convert;
        // Install the shaders first, so the test doesn't stop at the "download?" question.
        foreach (var c in new[] { Core.Upscaling.UpscalerComponents.Anime4K })
            if (!Core.Upscaling.UpscalerComponents.IsInstalled(c))
                Task.Run(() => Core.Upscaling.UpscalerComponents.InstallAsync(c)).GetAwaiter().GetResult();
        Await(convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(2, 320, 240))]));
        Assert.False(convert.CanTestSpeed); // nothing to upscale yet
        convert.SizePreset = "1280×720";
        Assert.True(convert.CanTestSpeed);
        Await(convert.TestSpeedCommand.ExecuteAsync(null));
        Assert.Contains("Recommended", convert.SpeedText);
        var lanczos = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.Lanczos);
        Assert.NotNull(lanczos.Estimate);
        Assert.Contains("under a minute", lanczos.Status);
        Assert.Single(convert.Scalings, o => o.IsRecommended);
        Assert.Same(convert.Scalings.Single(o => o.IsRecommended), convert.SelectedScaling);
        // Bundled ONNX AI models are timed; the downloadable AI programs are offered for download instead.
        Assert.NotNull(convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.AiAnimation).Estimate);
        var cugan = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.AiAnime);
        if (cugan.IsAvailable)
        {
            Assert.Null(cugan.Estimate);
            Assert.Contains("download", cugan.Status);
        }
        Pump();
        Screenshot(window, "06-upscaling");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Preview_compares_a_frame_before_and_after()
    {
        var (window, vm) = Open();
        vm.SelectedTab = 1;
        var convert = vm.Convert;
        Await(convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(4, 320, 240))]));
        Assert.Same(convert.Jobs[0], convert.SelectedJob);
        Assert.Equal(2, convert.PreviewTime, 1);
        convert.SizePreset = "1280×720";
        convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.ArtCnn);
        Await(convert.PreviewCommand.ExecuteAsync(null));
        Assert.DoesNotContain("failed", convert.PreviewText);
        Assert.Equal(convert.PreviewAfter!.PixelSize, convert.PreviewBefore!.PixelSize);
        Assert.Contains("Original 320×240", convert.PreviewText);
        Assert.Equal((1280, 720), (convert.PreviewAfter!.PixelSize.Width, convert.PreviewAfter.PixelSize.Height));
        Assert.False(convert.PreviewStale);
        Pump();
        Screenshot(window, "07-preview-wipe");

        convert.PreviewSideBySide = true;
        convert.PreviewZoom = 2;
        Pump();
        Screenshot(window, "08-preview-side-by-side-200");

        // Changing a setting marks the preview out of date.
        convert.ResizeMode = Core.Conversion.ResizeMode.Fill;
        Assert.True(convert.PreviewStale);
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Slider_shows_the_original_frame_and_strength_is_for_ai_only()
    {
        var (window, vm) = Open();
        vm.SelectedTab = 1;
        var convert = vm.Convert;
        Await(convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(4, 320, 240))]));
        WaitFor(() => convert.ScrubFrame != null);
        convert.PreviewTime = 3.2;
        WaitFor(() => convert.ScrubLabel.Contains("00:03.20"));
        Assert.NotNull(convert.ScrubFrame);
        Pump();
        Screenshot(window, "09-scrub");

        convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.ArtCnn);
        Assert.False(convert.ShowsAiStrength);
        convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.AiGeneral);
        Assert.True(convert.ShowsAiStrength);
        convert.AiStrength = 40;
        Assert.Equal(0.4, convert.BuildSettings().Settings!.AiStrength, 3);
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Compare_all_shows_every_method_this_computer_can_run()
    {
        var (window, vm) = Open();
        vm.SelectedTab = 1;
        var convert = vm.Convert;
        Await(convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(2, 160, 120))]));
        convert.SizePreset = "Custom";
        convert.CustomWidth = "640";
        convert.CustomHeight = "480";
        convert.AiStrength = 70;
        CompareAllViewModel? compare = null;
        convert.CompareAllRequested += c => compare = c;
        convert.CompareAllCommand.Execute(null);
        Assert.NotNull(compare);
        var runnable = convert.Scalings.Count(o => o.IsAvailable && !o.Method.NeedsDownload);
        Assert.Equal(runnable + 1, compare!.Tiles.Count);
        Assert.Contains(compare.Tiles, t => t.Label.Contains("70% strength"));

        var compareWindow = new CompareAllWindow(compare) { WindowState = WindowState.Normal, Width = 1440, Height = 900 };
        compareWindow.Show();
        WaitFor(() => !compare.IsRendering && compare.Tiles.All(t => t.State != TileState.Waiting && t.State != TileState.Rendering), 180_000);
        Assert.All(compare.Tiles.Skip(1), t => Assert.Equal(TileState.Done, t.State));
        Assert.All(compare.Tiles, t => Assert.Equal(new Avalonia.PixelSize(640, 480), t.Picture!.PixelSize));
        Assert.Contains(compare.Tiles, t => t.Detail.Contains("on the CPU"));
        Pump();
        Screenshot(compareWindow, "10-compare-all");
        compare.Zoom = 2;
        Pump();
        Screenshot(compareWindow, "11-compare-all-200");
        compareWindow.Close();
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Toolbar_buttons_are_all_the_same_size()
    {
        var (window, vm) = Open();
        foreach (var tab in new[] { 0, 1, 2 })
        {
            vm.SelectedTab = tab;
            Pump(100);
            var buttons = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Cast<Control>()
                .Concat(window.GetVisualDescendants().OfType<Button>())
                .Where(b => b.Classes.Contains("tool") && b.IsEffectivelyVisible)
                .ToList();
            Assert.NotEmpty(buttons);
            Assert.All(buttons, b => Assert.Equal(30, b.Bounds.Height));
            Assert.All(buttons.Where(b => b.Classes.Contains("icon")), b => Assert.Equal(30, b.Bounds.Width));
            Screenshot(window, $"12-buttons-tab{tab}");
        }
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Files_can_be_converted_again_with_new_settings()
    {
        var (window, vm) = Open();
        var convert = vm.Convert;
        Await(convert.AddFilesAsync([Media(() => TestMedia.PatternAsync(1, 160, 120))]));
        convert.OutputFolder = TestMedia.NewOutputDirectory();
        convert.SizePreset = "1280×720";
        convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.Lanczos);
        Await(convert.StartCommand.ExecuteAsync(null));
        var job = Assert.Single(convert.Jobs);
        Assert.Equal(ConvertState.Done, job.State);
        var first = job.OutputPath!;
        Assert.EndsWith("-1280x720-Lanczos.mp4", first);
        Assert.Contains("Lanczos", job.SettingsText);
        Assert.False(convert.CanStart); // nothing waiting
        Assert.True(job.CanConvertAgain);

        convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == Core.Conversion.ScaleAlgorithm.ArtCnn);
        Await(convert.ConvertAgainCommand.ExecuteAsync(job));
        Assert.Equal(ConvertState.Done, job.State);
        Assert.EndsWith("-1280x720-ArtCNN.mp4", job.OutputPath);
        Assert.True(File.Exists(first), "the earlier result is kept");
        Assert.Contains("ArtCNN", job.SettingsText);

        // Same settings again: a numbered name rather than overwriting.
        Await(convert.ConvertAllAgainCommand.ExecuteAsync(null));
        Assert.EndsWith("-1280x720-ArtCNN (2).mp4", job.OutputPath);
        Assert.Equal(3, Directory.GetFiles(convert.OutputFolder!).Length);
        Screenshot(window, "13-convert-again");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Cpu_and_heat_settings_reach_the_conversion()
    {
        var (window, vm) = Open();
        var convert = vm.Convert;
        Assert.True(convert.LowPriority, "low priority is the default");
        Assert.Null(convert.BuildSettings().Settings!.Limits.MaxThreads);
        convert.CpuUseIndex = 2; // Light
        Assert.Equal(Math.Min(2, Environment.ProcessorCount), convert.BuildSettings().Settings!.Limits.Threads);
        convert.CpuUseIndex = 3; // Custom
        Assert.True(convert.IsCustomCpu);
        convert.CustomThreads = 3;
        Assert.Equal(Math.Min(3, Environment.ProcessorCount), convert.BuildSettings().Settings!.Limits.Threads);
        convert.AiDeviceIndex = 1;
        Assert.Equal(Core.Conversion.AiDevice.Cpu, convert.BuildSettings().Settings!.Limits.Device);
        Assert.True(convert.BuildSettings().Settings!.Limits.LowPriority);
        vm.SelectedTab = 1;
        Pump(100);
        Screenshot(window, "14-cpu-heat");
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Exporting_from_the_dialog_writes_the_file()
    {
        var (window, vm) = Open();
        var editor = vm.Editor;
        Await(editor.ImportFilesAsync([Media(() => TestMedia.ColorAsync("red", 1))], addToTimeline: true));
        ExportViewModel? export = null;
        editor.ExportRequested += e => export = e;
        editor.ExportCommand.Execute(null);
        Assert.NotNull(export);
        export!.OutputPath = Path.Combine(TestMedia.NewOutputDirectory(), "export.mp4");
        Await(export.ExportCommand.ExecuteAsync(null));
        Assert.Equal("", export.Error);
        Assert.True(export.IsFinished);
        Assert.True(File.Exists(export.OutputPath));
        window.CloseWithoutPrompt();
    }

    [AvaloniaFact]
    public void Saving_and_reopening_a_project()
    {
        var (window, vm) = Open();
        var editor = vm.Editor;
        Await(editor.ImportFilesAsync([Media(() => TestMedia.ColorAsync("blue", 1))], addToTimeline: true));
        editor.AddTitleCommand.Execute(null);
        var path = Path.Combine(TestMedia.NewOutputDirectory(), "p" + Project.FileExtension);
        ProjectSerializer.Save(editor.Project, path);
        Await(editor.OpenProjectFileAsync(path));
        Assert.Equal(path, editor.Session.FilePath);
        Assert.Equal(2, editor.Project.AllClips().Count());
        Assert.DoesNotContain("•", window.Title);
        window.CloseWithoutPrompt();
    }
}
