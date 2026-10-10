using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Capture;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.App.ViewModels;

public sealed partial class AudioDeviceViewModel(AudioDevice device, bool selected) : ObservableObject
{
    public AudioDevice Device { get; } = device;
    public string Name => Device.Name;

    [ObservableProperty]
    private bool _isSelected = selected;
}

/// <summary>A recording or screenshot made in this session.</summary>
public sealed class CaptureResultViewModel(string path, MediaInfo info)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsImage => Info.Kind == MediaKind.Image;
    public MediaInfo Info { get; } = info;
    public string Details => (IsImage ? $"Screenshot {Info.Width}×{Info.Height}" : $"{Info.Width}×{Info.Height} · {Timecode.Format(Info.Duration)}")
                             + " · " + Shell.FormatSize(Info.SizeBytes);
}

/// <summary>
/// The Capture tab: record a screen, a window or a region (for bug reports and demos), or take
/// a screenshot, then annotate it in the editor.
/// </summary>
public sealed partial class CaptureViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _stop;
    private DispatcherTimer? _countdownTimer;

    public CaptureViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _frameRate = settings.CaptureFrameRate;
        _showCursor = settings.CaptureCursor;
        _countdown = settings.CaptureCountdown;
        _minimise = settings.CaptureMinimise;
        _outputFolder = settings.CaptureFolder ?? Path.Combine(AppPaths.VideosDirectory, "MyVideoEditor");
    }

    public ObservableCollection<CaptureTarget> Sources { get; } = [];
    public ObservableCollection<AudioDeviceViewModel> AudioDevices { get; } = [];
    public ObservableCollection<CaptureResultViewModel> Results { get; } = [];
    public IReadOnlyList<double> FrameRates { get; } = [10, 15, 24, 30, 60];
    public IReadOnlyList<int> Countdowns { get; } = [0, 3, 5, 10];

    public bool IsWayland => WindowEnumerator.IsWayland;
    public string WaylandNote =>
        "This is a Wayland session. Recording uses X11, so only apps running through XWayland can be captured; " +
        "for everything else, log in with an \"Xorg\" / \"X11\" session (choose it on the login screen).";
    public bool CanCapture => FfmpegLocator.Default.Capabilities.HasInputDevice(OperatingSystem.IsWindows() ? "gdigrab" : "x11grab")
                              || FfmpegLocator.Default.Capabilities.InputDevices.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private CaptureTarget? _selectedSource;

    [ObservableProperty]
    private double _frameRate;

    [ObservableProperty]
    private bool _showCursor;

    [ObservableProperty]
    private int _countdown;

    [ObservableProperty]
    private bool _minimise;

    [ObservableProperty]
    private string _outputFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isCountingDown;

    [ObservableProperty]
    private string _elapsed = "00:00";

    [ObservableProperty]
    private string _status = "";

    public bool IsIdle => !IsRecording && !IsCountingDown;
    public bool HasSource => SelectedSource != null;

    /// <summary>Monitors, set by the view (it knows the screen layout).</summary>
    private List<CaptureTarget> _screens = [];

    /// <summary>The view asks the user to drag out a region; returns it in desktop pixels, or null.</summary>
    public Func<Task<CaptureTarget?>>? SelectRegion { get; set; }
    /// <summary>Recording started/stopped: the view minimises the window and shows a small Stop button.</summary>
    public event Action<bool>? RecordingChanged;
    public event Action<string>? EditRequested;
    public event Action<string>? ConvertRequested;

    public void SetScreens(IEnumerable<CaptureTarget> screens)
    {
        _screens = screens.ToList();
        RefreshSources();
    }

    [RelayCommand]
    private async Task Refresh()
    {
        RefreshSources();
        await LoadAudioDevicesAsync();
    }

    private void RefreshSources()
    {
        var selected = SelectedSource;
        Sources.Clear();
        foreach (var s in _screens)
            Sources.Add(s);
        foreach (var w in WindowEnumerator.ListWindows(Environment.ProcessId))
            Sources.Add(w);
        if (selected?.Kind == CaptureTargetKind.Region)
            Sources.Add(selected);
        SelectedSource = Sources.FirstOrDefault(s => s == selected)
                         ?? Sources.FirstOrDefault(s => selected != null && s.Kind == selected.Kind && s.WindowHandle == selected.WindowHandle && s.Title == selected.Title)
                         ?? Sources.FirstOrDefault();
    }

    public async Task LoadAudioDevicesAsync()
    {
        var devices = await Core.Capture.AudioDevices.ListAsync();
        AudioDevices.Clear();
        foreach (var d in devices)
            AudioDevices.Add(new AudioDeviceViewModel(d, _settings.CaptureAudioDevices.Contains(d.Id)));
    }

    [RelayCommand]
    private async Task PickRegion()
    {
        if (SelectRegion == null)
            return;
        var region = await SelectRegion();
        if (region == null)
            return;
        foreach (var old in Sources.Where(s => s.Kind == CaptureTargetKind.Region).ToList())
            Sources.Remove(old);
        Sources.Add(region);
        SelectedSource = region;
    }

    [RelayCommand]
    private async Task ChooseFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Save recordings to", [OutputFolder]);
        if (folder != null)
            OutputFolder = folder;
    }

    private void SaveSettings()
    {
        _settings.CaptureFrameRate = FrameRate;
        _settings.CaptureCursor = ShowCursor;
        _settings.CaptureCountdown = Countdown;
        _settings.CaptureMinimise = Minimise;
        _settings.CaptureFolder = OutputFolder;
        _settings.CaptureAudioDevices = AudioDevices.Where(a => a.IsSelected).Select(a => a.Device.Id).ToList();
        _settings.Save();
    }

    [RelayCommand]
    private void StartRecording()
    {
        if (SelectedSource == null || !IsIdle)
            return;
        SaveSettings();
        if (Countdown <= 0)
        {
            _ = RecordAsync();
            return;
        }
        var remaining = Countdown;
        IsCountingDown = true;
        Elapsed = remaining.ToString();
        RecordingChanged?.Invoke(true);
        _countdownTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            remaining--;
            Elapsed = remaining.ToString();
            if (remaining > 0)
                return;
            _countdownTimer?.Stop();
            IsCountingDown = false;
            _ = RecordAsync();
        });
        _countdownTimer.Start();
    }

    private async Task RecordAsync()
    {
        var source = SelectedSource!;
        var name = $"Recording {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4";
        var output = Path.Combine(OutputFolder, name);
        var settings = new CaptureSettings
        {
            Target = source,
            FrameRate = FrameRate,
            ShowCursor = ShowCursor,
            AudioDevices = AudioDevices.Where(a => a.IsSelected).Select(a => a.Device.Id).ToList(),
        };
        _stop = new CancellationTokenSource();
        IsRecording = true;
        Elapsed = "00:00";
        Status = "";
        RecordingChanged?.Invoke(true);
        try
        {
            var progress = new Progress<TimeSpan>(t => Elapsed = Timecode.Format(t.TotalSeconds)[..^3]);
            var path = await new ScreenRecorder().RecordAsync(settings, output, progress, _stop.Token);
            var info = await MediaProbe.Default.ProbeAsync(path);
            Results.Insert(0, new CaptureResultViewModel(path, info));
            Status = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Log.Warn("capture", "Recording failed.", ex);
            Status = "Recording failed: " + (ex is FfmpegException fe ? fe.Message : ex.Message);
        }
        finally
        {
            IsRecording = false;
            _stop = null;
            RecordingChanged?.Invoke(false);
        }
    }

    [RelayCommand]
    private void StopRecording()
    {
        if (IsCountingDown)
        {
            _countdownTimer?.Stop();
            IsCountingDown = false;
            RecordingChanged?.Invoke(false);
            return;
        }
        _stop?.Cancel();
    }

    [RelayCommand]
    private async Task Screenshot()
    {
        if (SelectedSource == null)
            return;
        SaveSettings();
        var output = Path.Combine(OutputFolder, $"Screenshot {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        try
        {
            Directory.CreateDirectory(OutputFolder);
            // Give a minimised window time to get out of the way.
            RecordingChanged?.Invoke(true);
            await Task.Delay(Minimise ? 600 : 50);
            await new ScreenRecorder().ScreenshotAsync(SelectedSource, output, ShowCursor);
            var info = await MediaProbe.Default.ProbeAsync(output);
            Results.Insert(0, new CaptureResultViewModel(output, info));
            Status = $"Saved {Path.GetFileName(output)}.";
        }
        catch (Exception ex)
        {
            Status = "Screenshot failed: " + (ex is FfmpegException fe ? fe.Message : ex.Message);
        }
        finally
        {
            RecordingChanged?.Invoke(false);
        }
    }

    [RelayCommand]
    private void Edit(CaptureResultViewModel? result)
    {
        if (result != null)
            EditRequested?.Invoke(result.Path);
    }

    [RelayCommand]
    private void Convert(CaptureResultViewModel? result)
    {
        if (result != null)
            ConvertRequested?.Invoke(result.Path);
    }

    [RelayCommand]
    private void ShowInFolder(CaptureResultViewModel? result)
    {
        if (result != null)
            Shell.ShowInFolder(result.Path);
    }

    [RelayCommand]
    private void Open(CaptureResultViewModel? result)
    {
        if (result != null)
            Shell.Open(result.Path);
    }
}
