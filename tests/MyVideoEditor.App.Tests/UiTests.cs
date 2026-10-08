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
