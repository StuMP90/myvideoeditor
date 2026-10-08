using CommunityToolkit.Mvvm.ComponentModel;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Playback;

namespace MyVideoEditor.App.ViewModels;

/// <summary>The main window: Edit, Convert and Capture tabs.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    public MainWindowViewModel(DialogService dialogs, AppSettings settings, IAudioSink? audio)
    {
        Settings = settings;
        Editor = new EditorViewModel(dialogs, settings, audio);
        Convert = new ConvertViewModel(settings, dialogs);
        Capture = new CaptureViewModel(settings, dialogs);
        Capture.EditRequested += async path =>
        {
            SelectedTab = 0;
            await Editor.NewProjectFromFileAsync(path);
        };
        Capture.ConvertRequested += async path =>
        {
            SelectedTab = 1;
            await Convert.AddFilesAsync([path]);
        };
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorViewModel.Title))
                OnPropertyChanged(nameof(WindowTitle));
        };
    }

    public AppSettings Settings { get; }
    public EditorViewModel Editor { get; }
    public ConvertViewModel Convert { get; }
    public CaptureViewModel Capture { get; }

    [ObservableProperty]
    private int _selectedTab;

    public string WindowTitle => $"{Editor.Title} — {AppInfo.Name}";
    public string AppVersion => AppInfo.Version;

    public bool FfmpegMissing => !FfmpegLocator.Default.IsAvailable;
    public string FfmpegMissingText =>
        "ffmpeg wasn't found, so nothing can be imported, played or exported. Release builds include it; for a development " +
        "build install ffmpeg (Linux: sudo apt install ffmpeg) or set MYVIDEOEDITOR_FFMPEG_DIR to the folder containing ffmpeg and ffprobe.";

    public void Dispose() => Editor.Dispose();
}
