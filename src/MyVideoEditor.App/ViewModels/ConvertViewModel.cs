using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;

namespace MyVideoEditor.App.ViewModels;

public enum ConvertState
{
    Waiting,
    Converting,
    Done,
    Failed,
    Cancelled,
}

/// <summary>A file in the conversion queue.</summary>
public sealed partial class ConvertJobViewModel : ObservableObject
{
    public ConvertJobViewModel(MediaInfo info) => Info = info;

    public MediaInfo Info { get; }
    public string Name => Path.GetFileName(Info.Path);
    public string Details => Info.Summary + " · " + Shell.FormatSize(Info.SizeBytes);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsActive), nameof(StateText))]
    private ConvertState _state;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string _message = "";

    [ObservableProperty]
    private string? _outputPath;

    public bool IsDone => State == ConvertState.Done;
    public bool IsActive => State == ConvertState.Converting;

    public string StateText => State switch
    {
        ConvertState.Waiting => "Waiting",
        ConvertState.Converting => Message,
        ConvertState.Done => Message,
        ConvertState.Failed => "Failed: " + Message,
        _ => "Cancelled",
    };
}

/// <summary>
/// The Convert tab: a queue of files converted one after another with the same settings
/// (format, size and upscaling, frame rate, trimming, rotation).
/// </summary>
public sealed partial class ConvertViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _cts;

    public ConvertViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        Formats = OutputFormats.Available(FfmpegLocator.Default.Capabilities).ToList();
        _format = Formats.FirstOrDefault(f => f.Id == settings.ConvertFormat) ?? Formats.FirstOrDefault() ?? OutputFormats.Mp4H264;
        AiAvailable = AiUpscaler.IsAvailable;
        Algorithms = Enum.GetValues<ScaleAlgorithm>().Where(a => a != ScaleAlgorithm.AiSuperResolution || AiAvailable).ToList();
    }

    public ObservableCollection<ConvertJobViewModel> Jobs { get; } = [];
    public IReadOnlyList<OutputFormat> Formats { get; }
    public IReadOnlyList<Quality> Qualities { get; } = Enum.GetValues<Quality>();
    public IReadOnlyList<ResizeMode> ResizeModes { get; } = Enum.GetValues<ResizeMode>();
    public IReadOnlyList<ScaleAlgorithm> Algorithms { get; }
    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];
    public bool AiAvailable { get; }

    public IReadOnlyList<string> SizePresets { get; } =
        ["Same as source", "3840×2160 (4K)", "2560×1440", "1920×1080", "1280×720", "854×480", "1080×1920 (vertical)", "1080×1080 (square)", "Custom"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatHasVideo))]
    private OutputFormat _format;

    [ObservableProperty]
    private Quality _quality = Quality.Standard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomSize), nameof(ResizeEnabled))]
    private string _sizePreset = "Same as source";

    [ObservableProperty]
    private string _customWidth = "";

    [ObservableProperty]
    private string _customHeight = "";

    [ObservableProperty]
    private ResizeMode _resizeMode = ResizeMode.Fit;

    [ObservableProperty]
    private ScaleAlgorithm _algorithm = ScaleAlgorithm.Lanczos;

    [ObservableProperty]
    private string _frameRate = "";

    [ObservableProperty]
    private string _trimStart = "";

    [ObservableProperty]
    private string _trimEnd = "";

    [ObservableProperty]
    private int _rotate;

    [ObservableProperty]
    private bool _removeAudio;

    [ObservableProperty]
    private bool _deinterlace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReencodeOptionsEnabled))]
    private bool _remux;

    [ObservableProperty]
    private string? _outputFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "";

    public bool IsCustomSize => SizePreset == "Custom";
    public bool ResizeEnabled => SizePreset != "Same as source";
    public bool FormatHasVideo => Format.HasVideo;
    public bool ReencodeOptionsEnabled => !Remux;
    public bool CanStart => !IsRunning && Jobs.Any(j => j.State != ConvertState.Done);
    public string OutputFolderText => OutputFolder ?? "Same folder as each file";

    partial void OnOutputFolderChanged(string? value) => OnPropertyChanged(nameof(OutputFolderText));

    partial void OnFormatChanged(OutputFormat value)
    {
        _settings.ConvertFormat = value.Id;
        _settings.Save();
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var files = await _dialogs.OpenFilesAsync("Add files to convert", true, DialogService.MediaFiles);
        await AddFilesAsync(files);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                Jobs.Add(new ConvertJobViewModel(await MediaProbe.Default.ProbeAsync(path)));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        OnPropertyChanged(nameof(CanStart));
        if (failed.Count > 0)
            await _dialogs.AlertAsync("Some files couldn't be added", string.Join("\n", failed));
    }

    [RelayCommand]
    private void RemoveJob(ConvertJobViewModel? job)
    {
        if (job is { IsActive: false })
            Jobs.Remove(job);
        OnPropertyChanged(nameof(CanStart));
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.State is ConvertState.Done or ConvertState.Failed or ConvertState.Cancelled).ToList())
            Jobs.Remove(job);
        OnPropertyChanged(nameof(CanStart));
    }

    [RelayCommand]
    private async Task ChooseFolder()
    {
        var folder = await _dialogs.PickFolderAsync("Save converted files to");
        if (folder != null)
            OutputFolder = folder;
    }

    [RelayCommand]
    private void SameFolder() => OutputFolder = null;

    [RelayCommand]
    private void OpenOutput(ConvertJobViewModel? job)
    {
        if (job?.OutputPath != null)
            Shell.ShowInFolder(job.OutputPath);
    }

    /// <summary>The settings from the form, or an error message.</summary>
    internal (ConversionSettings? Settings, string? Error) BuildSettings()
    {
        int? width = null, height = null;
        if (SizePreset == "Custom")
        {
            width = int.TryParse(CustomWidth, out var w) && w > 0 ? w : null;
            height = int.TryParse(CustomHeight, out var h) && h > 0 ? h : null;
            if (width == null && height == null)
                return (null, "Enter a width, a height, or both.");
        }
        else if (SizePreset != "Same as source")
        {
            var dims = SizePreset.Split(' ')[0].Split('×');
            width = int.Parse(dims[0]);
            height = int.Parse(dims[1]);
        }
        double? fps = null;
        if (!string.IsNullOrWhiteSpace(FrameRate))
        {
            if (!double.TryParse(FrameRate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) || f <= 0 || f > 240)
                return (null, "The frame rate should be a number such as 30 or 29.97.");
            fps = f;
        }
        double? start = null, end = null;
        if (!string.IsNullOrWhiteSpace(TrimStart))
        {
            if (!Timecode.TryParse(TrimStart, out var s))
                return (null, "The trim start isn't a valid time (e.g. 0:12.5).");
            start = s;
        }
        if (!string.IsNullOrWhiteSpace(TrimEnd))
        {
            if (!Timecode.TryParse(TrimEnd, out var e) || e <= (start ?? 0))
                return (null, "The trim end must be a time after the start.");
            end = e;
        }
        return (new ConversionSettings
        {
            Format = Format,
            Quality = Quality,
            Remux = Remux,
            Width = width,
            Height = height,
            ResizeMode = ResizeMode,
            Algorithm = Algorithm,
            FrameRate = fps,
            TrimStart = start,
            TrimEnd = end,
            Rotate = Rotate,
            RemoveAudio = RemoveAudio,
            Deinterlace = Deinterlace,
        }, null);
    }

    [RelayCommand]
    private async Task Start()
    {
        var (settings, error) = BuildSettings();
        if (settings == null)
        {
            Status = error ?? "";
            return;
        }
        Status = "";
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var converter = new Converter();
        var done = 0;
        try
        {
            foreach (var job in Jobs.Where(j => j.State != ConvertState.Done).ToList())
            {
                if (_cts.IsCancellationRequested)
                    break;
                var folder = OutputFolder ?? Path.GetDirectoryName(job.Info.Path)!;
                var output = UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(job.Info.Path) + SuffixFor(settings) + Format.Extension));
                job.State = ConvertState.Converting;
                job.Progress = 0;
                job.Message = "Starting…";
                var progress = new Progress<FfmpegProgress>(p =>
                {
                    job.Progress = (p.Fraction ?? 0) * 100;
                    var remaining = p.Remaining is { } r ? $" · {ExportViewModel.FormatDuration(r)} left" : "";
                    job.Message = $"{job.Progress:0}%{remaining}";
                });
                try
                {
                    await converter.ConvertAsync(job.Info.Path, output, settings, progress, _cts.Token);
                    job.OutputPath = output;
                    job.Progress = 100;
                    job.Message = $"Done: {Path.GetFileName(output)} ({Shell.FormatSize(new FileInfo(output).Length)})";
                    job.State = ConvertState.Done;
                    done++;
                }
                catch (OperationCanceledException)
                {
                    job.State = ConvertState.Cancelled;
                }
                catch (Exception ex)
                {
                    Log.Warn("convert", $"Converting {job.Info.Path} failed.", ex);
                    job.Message = ex.Message;
                    job.State = ConvertState.Failed;
                }
            }
        }
        finally
        {
            IsRunning = false;
            _cts = null;
            Status = done > 0 ? $"Converted {done} file(s)." : "";
            OnPropertyChanged(nameof(CanStart));
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private static string SuffixFor(ConversionSettings s) =>
        s.Height is { } h ? $"-{h}p" : s.Width is { } w ? $"-{w}w" : s.Remux ? "-remux" : "-converted";

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }
}
