using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using MyVideoEditor.App.Services;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.App.Views;
using MyVideoEditor.Core.Capture;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;
using MyVideoEditor.Core.Tests;
using SkiaSharp;

namespace MyVideoEditor.App.Tests;

/// <summary>
/// Microsoft Store screenshots: the real app, rendered at 1920×1080 with staged content: footage
/// drawn by <see cref="DemoFootage"/>, made-up names and paths, nothing from this computer (its
/// window titles and sound devices are replaced). A one-off: runs only when
/// MYVIDEOEDITOR_STORE_SCREENSHOTS names an output folder, e.g.
///   MYVIDEOEDITOR_STORE_SCREENSHOTS=packaging/store/screenshots dotnet test tests/MyVideoEditor.App.Tests --filter StoreScreenshots
/// </summary>
public partial class UiTests
{
    private const int StoreWidth = 1920;
    private const int StoreHeight = 1080;

    [AvaloniaFact]
    public void StoreScreenshots()
    {
        var output = Environment.GetEnvironmentVariable("MYVIDEOEDITOR_STORE_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(output))
            return; // only on request
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var footage = Path.Combine(Path.GetTempPath(), "Holiday videos");
        var (hills, sea, city, forest, bug) = (DemoFootage.Hills(footage), DemoFootage.Sailing(footage), DemoFootage.City(footage),
            DemoFootage.Forest(footage), DemoFootage.BugReport(footage));

        // Each scene in a fresh window (a new project would otherwise stop to ask about unsaved changes),
        // with a stand-in sound output so it doesn't say there's no sound device.
        (MainWindow Window, MainWindowViewModel Vm) Scene()
        {
            var opened = Open(StoreWidth, StoreHeight, new SilentAudio());
            return opened;
        }
        void Shot(Window window, string name)
        {
            Pump(400);
            using var frame = window.CaptureRenderedFrame()!;
            frame.Save(Path.Combine(output, name + ".png"));
        }
        void Ready(MainWindow window, EditorViewModel editor)
        {
            // Thumbnails, filmstrips and waveforms arrive in the background.
            WaitFor(() => editor.Media.All(m => m.Thumbnail != null), 60_000);
            Pump(2500);
            window.FindDescendantOfType<EditorView>()!.ZoomToFit();
        }
        void Show(EditorViewModel editor, double time, string? select)
        {
            if (select != null)
                editor.Select(select);
            editor.Seek(time);
            Await(editor.FlushPreviewAsync());
        }

        // ---- 1–3. Editing: four clips with transitions, titles, a lower third, a chapter and a crawl ----
        {
            var (window, vm) = Scene();
            var editor = vm.Editor;
            Await(editor.ImportFilesAsync([hills, sea, city, forest], addToTimeline: true));
            var v1 = editor.Project.Tracks.First(t => t.Name == "V1");
            var v2 = editor.Project.Tracks.First(t => t.Name == "V2").Id;
            var clips = v1.Clips.OrderBy(c => c.Start).ToList();
            (TransitionStyle Style, TransitionDirection Direction)[] transitions =
                [(TransitionStyle.CircleOpen, TransitionDirection.Left), (TransitionStyle.Wipe, TransitionDirection.Left), (TransitionStyle.Dissolve, TransitionDirection.Left)];
            for (var i = 1; i < clips.Count; i++)
            {
                var (style, direction) = transitions[i - 1];
                editor.Session.Editor.UpdateClip(clips[i].Id, "transition", c => { c.TransitionIn = 1; c.TransitionStyle = style; c.TransitionDirection = direction; });
            }
            var title = editor.Session.Editor.AddTitle(0.3, new TitleClip
            {
                Text = "Summer in the hills", FontSize = 110, Y = 0.42, Duration = 4.5, AnimationIn = TitleMotion.Rise, AnimationOut = TitleMotion.Grow,
            }, v2);
            var lowerThird = editor.Session.Editor.AddTitle(7, EditorViewModel.TitleTemplates.First(t => t.Label == "Lower third").Clip, v2);
            editor.Session.Editor.UpdateClip(lowerThird, "text", c =>
            {
                ((TitleClip)c).Text = "Jane Doe\nTravel diaries";
                c.Name = "Jane Doe";
                c.Duration = 4;
                ((TitleClip)c).AnimationIn = TitleMotion.SlideLeft;
            });
            editor.Session.Editor.AddTitle(18.5, new TitleClip { Text = "Chapter 2: the forest", FontSize = 64, Y = 0.12, OutlineWidth = 3, Duration = 3, StartsChapter = true }, v2);
            Ready(window, editor);
            Show(editor, 2.6, title);
            Shot(window, "01-editing");

            Show(editor, 12.45, clips[2].Id); // the wipe into the city at night (a crisp edge shows best)
            Shot(window, "02-transitions");

            var crawl = editor.Session.Editor.AddTitle(12, new TitleClip
            {
                Text = "Part One\nTHE LONG WEEKEND\n\nAfter a golden evening in the hills, our travellers set sail across the bay, " +
                       "chasing the last of the summer light.\n\nBy nightfall the city glittered ahead of them, and every moment of the " +
                       "trip was safely on the timeline....",
                FontSize = 58, Color = "#FFD27A", Shadow = false, Scroll = TitleScroll.Crawl, Duration = 6,
            }, editor.Session.Editor.AddTrack(TrackKind.Video));
            Show(editor, 14.2, crawl);
            Shot(window, "03-titles-crawl");
            window.CloseWithoutPrompt();
        }

        // ---- 4. A bug report: a recording of a made-up app, annotated ----
        {
            var (window, vm) = Scene();
            var editor = vm.Editor;
            Await(editor.ImportFilesAsync([bug], addToTimeline: true));
            string Note(AnnotationShape shape, double x1, double y1, double x2, double y2, string text = "")
            {
                var id = editor.AddAnnotation(shape, x1, y1, x2, y2);
                editor.Session.Editor.UpdateClip(id, "annotation", c => { c.Start = 0; c.Duration = 6; if (text.Length > 0) ((AnnotationClip)c).Text = text; });
                return id;
            }
            Note(AnnotationShape.Step, 0.05, 0.22, 0.09, 0.29, "1");
            Note(AnnotationShape.Blur, 0.255, 0.26, 0.59, 0.33);
            Note(AnnotationShape.Step, 0.87, 0.66, 0.91, 0.73, "2");
            Note(AnnotationShape.Highlight, 0.33, 0.4, 0.74, 0.65);
            Note(AnnotationShape.Arrow, 0.6, 0.86, 0.73, 0.66);
            var callout = Note(AnnotationShape.Callout, 0.36, 0.83, 0.62, 0.95, "Saving fails every time (since v2.3)");
            Ready(window, editor);
            Show(editor, 3.5, callout);
            Shot(window, "04-bug-report-annotations");
            window.CloseWithoutPrompt();
        }

        // ---- 5–6. Converting and upscaling: a before/after comparison, then every method side by side ----
        {
            var (window, vm) = Scene();
            vm.SelectedTab = 1;
            Pump(300);
            var convert = vm.Convert;
            var small = DemoFootage.Smaller(city, 360, "City lights (360p).mp4");
            Await(convert.AddFilesAsync([small, DemoFootage.Smaller(hills, 480, "Hills at dusk (480p).mp4"), DemoFootage.Smaller(sea, 360, "Sailing (360p).mp4")]));
            convert.SelectedJob = convert.Jobs[0];
            convert.SizePreset = "1920×1080";
            convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == ScaleAlgorithm.AiGeneral);
            convert.AiStrength = 70;
            convert.PreviewTime = 3;
            Await(convert.PreviewCommand.ExecuteAsync(null));
            Shot(window, "05-convert-upscale");

            convert.SelectedScaling = convert.Scalings.First(o => o.Method.Algorithm == ScaleAlgorithm.ArtCnn);
            convert.CompareAllCommand.Execute(null);
            Pump(500);
            var compare = window.OwnedWindows.OfType<CompareAllWindow>().Single();
            compare.Width = StoreWidth;
            compare.Height = StoreHeight;
            var compareVm = (CompareAllViewModel)compare.DataContext!;
            WaitFor(() => !compareVm.IsRendering, 20 * 60_000);
            Shot(compare, "06-compare-all-methods");
            compare.Close();
            window.CloseWithoutPrompt();
        }

        // ---- 7. Capture: staged sources and sound devices (not this computer's) ----
        {
            var (window, vm) = Scene();
            vm.SelectedTab = 2;
            Pump(1500); // let it list this computer's windows and devices first, then replace them
            var capture = vm.Capture;
            capture.Sources.Clear();
            foreach (var source in new[]
                     {
                         new CaptureTarget(CaptureTargetKind.Screen, "Whole screen", 0, 0, 1920, 1080),
                         new CaptureTarget(CaptureTargetKind.Window, "Example Notes", 120, 80, 1280, 720, 1),
                         new CaptureTarget(CaptureTargetKind.Window, "Web browser - example.com", 0, 0, 1600, 1000, 2),
                         new CaptureTarget(CaptureTargetKind.Window, "Spreadsheet - Budget 2026", 200, 120, 1400, 900, 3),
                     })
                capture.Sources.Add(source);
            capture.SelectedSource = capture.Sources[1];
            capture.AudioDevices.Clear();
            capture.AudioDevices.Add(new AudioDeviceViewModel(new AudioDevice("mic", "Microphone (USB headset)", false), true));
            capture.AudioDevices.Add(new AudioDeviceViewModel(new AudioDevice("speakers", "Computer sound (speakers)", true), true));
            capture.OutputFolder = @"C:\Users\You\Videos\MyVideoEditor";
            capture.Results.Clear();
            var still = Path.Combine(footage, "Screenshot - error message.png");
            if (!File.Exists(still))
                Await(new Core.Export.ProjectExporter().ExportFrameAsync(SingleClipProject(bug), 3, still));
            capture.Results.Add(new CaptureResultViewModel(still, Await2(MediaProbe.Default.ProbeAsync(still))));
            capture.Results.Add(new CaptureResultViewModel(bug, Await2(MediaProbe.Default.ProbeAsync(bug))));
            Shot(window, "07-capture");
            window.CloseWithoutPrompt();
        }

        // ---- 8. Export: DVD-Video with chapters, over the editor ----
        {
            var (window, vm) = Scene();
            var editor = vm.Editor;
            Await(editor.ImportFilesAsync([hills, sea, city, forest], addToTimeline: true));
            var v2 = editor.Project.Tracks.First(t => t.Name == "V2").Id;
            editor.Session.Editor.AddTitle(0, new TitleClip { Text = "Chapter 1: the hills", FontSize = 64, Y = 0.12, OutlineWidth = 3, Duration = 3, StartsChapter = true }, v2);
            editor.Session.Editor.AddTitle(12, new TitleClip { Text = "Chapter 2: the city", FontSize = 64, Y = 0.12, OutlineWidth = 3, Duration = 3, StartsChapter = true }, v2);
            Ready(window, editor);
            Show(editor, 1.5, null);
            var export = new ExportViewModel(editor.Project, AppSettings.Load(), new DialogService(window), null);
            export.Format = export.Formats.First(f => f.IsDvd);
            export.OutputPath = @"C:\Users\You\Videos\Summer trip.mpg";
            var dialog = new ExportWindow { DataContext = export };
            dialog.Show(window);
            Pump(600);
            SaveWithDialog(window, dialog, Path.Combine(output, "08-export-dvd.png"));
            dialog.Close();
            window.CloseWithoutPrompt();
        }
    }

    /// <summary>A project with just this clip (for saving a frame of it).</summary>
    private static Project SingleClipProject(string file)
    {
        var session = new Core.Editing.EditSession(Project.CreateDefault("still", new ProjectSettings { Width = 1280, Height = 720, FrameRate = 30 }));
        session.Editor.AddMediaClip(session.Editor.ImportMedia(Await2(MediaProbe.Default.ProbeAsync(file))), 0);
        return session.Project;
    }

    /// <summary>Sound output that plays nothing (so the app shows its volume control rather than "no sound device").</summary>
    private sealed class SilentAudio : Core.Playback.IAudioSink
    {
        public int SampleRate => 48000;
        public double PlayedSeconds => 0;
        public bool Started => false;
        public float Volume { get; set; } = 1;
        public void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken) { }
        public void Reset() { }
        public void Dispose() { }
    }

    private static T Await2<T>(Task<T> task)
    {
        Await(task);
        return task.Result;
    }

    /// <summary>The main window, dimmed, with a dialog drawn over its middle (headless windows render separately).</summary>
    private static void SaveWithDialog(Window main, Window dialog, string path)
    {
        using var back = ToSkia(main.CaptureRenderedFrame()!);
        using var front = ToSkia(dialog.CaptureRenderedFrame()!);
        using var surface = SKSurface.Create(new SKImageInfo(back.Width, back.Height));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(back, 0, 0);
        using (var dim = new SKPaint { Color = new SKColor(0, 0, 0, 90) })
            canvas.DrawRect(0, 0, back.Width, back.Height, dim);
        var (x, y) = ((back.Width - front.Width) / 2f, (back.Height - front.Height) / 2f);
        using (var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 140), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) })
            canvas.DrawRect(x + 4, y + 10, front.Width, front.Height, shadow);
        canvas.DrawBitmap(front, x, y);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static SKBitmap ToSkia(Avalonia.Media.Imaging.WriteableBitmap frame)
    {
        using (frame)
        {
            var bitmap = new SKBitmap(new SKImageInfo(frame.PixelSize.Width, frame.PixelSize.Height, frame.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Bgra8888, SKAlphaType.Premul));
            using var locked = frame.Lock();
            var bytes = new byte[locked.RowBytes * frame.PixelSize.Height];
            System.Runtime.InteropServices.Marshal.Copy(locked.Address, bytes, 0, bytes.Length);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), Math.Min(bytes.Length, bitmap.ByteCount));
            return bitmap;
        }
    }
}
