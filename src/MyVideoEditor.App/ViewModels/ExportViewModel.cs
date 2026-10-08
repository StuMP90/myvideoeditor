using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.App.ViewModels;

public sealed record SizeChoice(string Label, int? Height);

/// <summary>The export window: format, quality, size, part of the timeline, and progress.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly Project _project;
    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private CancellationTokenSource? _cts;

    public ExportViewModel(Project project, AppSettings settings, DialogService dialogs, string? projectPath)
    {
        _project = project;
        _settings = settings;
        _dialogs = dialogs;
        Formats = OutputFormats.Available(FfmpegLocator.Default.Capabilities).ToList();
        _format = Formats.FirstOrDefault(f => f.Id == settings.ExportFormat) ?? Formats.FirstOrDefault() ?? OutputFormats.Mp4H264;
        _quality = Enum.TryParse<Quality>(settings.ExportQuality, out var q) ? q : Quality.Standard;
        var h = project.Settings.Height;
        Sizes =
        [
            new($"Project size ({project.Settings.Width}×{h})", null),
            .. new[] { 2160, 1440, 1080, 720, 480, 360 }.Where(x => x != h).Select(x => new SizeChoice($"{x}p{(x > h ? " (upscale)" : "")}", x)),
        ];
        _size = Sizes[0];
        var folder = settings.LastExportFolder ?? (projectPath != null ? Path.GetDirectoryName(projectPath) : null) ?? AppPaths.VideosDirectory;
        _outputPath = Path.Combine(folder, SafeName(project.Name) + _format.Extension);
        _rangeEnd = Timecode.Format(project.Duration);
        _rangeStart = Timecode.Format(0);
    }

    public IReadOnlyList<OutputFormat> Formats { get; }
    public IReadOnlyList<Quality> Qualities { get; } = Enum.GetValues<Quality>();
    public IReadOnlyList<SizeChoice> Sizes { get; }

    [ObservableProperty]
    private OutputFormat _format;

    [ObservableProperty]
    private Quality _quality;

    [ObservableProperty]
    private SizeChoice _size;

    [ObservableProperty]
    private string _outputPath;

    [ObservableProperty]
    private string _rangeStart;

    [ObservableProperty]
    private string _rangeEnd;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isExporting;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _warnings = "";

    [ObservableProperty]
    private string? _finishedPath;

    public bool CanEdit => !IsExporting;
    public bool IsFinished => FinishedPath != null;
    public string FormatDescription => Format.Description;
    public bool FormatHasVideo => Format.HasVideo;

    /// <summary>Closes the window (set by the view).</summary>
    public Action? Close { get; set; }

    partial void OnFormatChanged(OutputFormat value)
    {
        OutputPath = Path.ChangeExtension(OutputPath, value.Extension);
        if (value.IsGif && Size.Height == null && _project.Settings.Height > 480)
            Size = Sizes.FirstOrDefault(s => s.Height == 480) ?? Size;
        OnPropertyChanged(nameof(FormatDescription));
        OnPropertyChanged(nameof(FormatHasVideo));
    }

    partial void OnFinishedPathChanged(string? value) => OnPropertyChanged(nameof(IsFinished));

    [RelayCommand]
    private async Task Browse()
    {
        var path = await _dialogs.SaveFileAsync("Export to", Path.GetFileName(OutputPath), Format.Extension, Format.Label, Path.GetDirectoryName(OutputPath));
        if (path != null)
            OutputPath = path;
    }

    [RelayCommand]
    private async Task Export()
    {
        Error = "";
        Warnings = "";
        FinishedPath = null;
        if (!Timecode.TryParse(RangeStart, out var start, _project.Settings.FrameRate) || !Timecode.TryParse(RangeEnd, out var end, _project.Settings.FrameRate) || end <= start)
        {
            Error = "The From and To times aren't valid (use e.g. 00:12.50 or 1:02:03).";
            return;
        }
        end = Math.Min(end, _project.Duration);
        if (File.Exists(OutputPath) && !await _dialogs.ConfirmAsync("Replace file?", $"{Path.GetFileName(OutputPath)} already exists. Replace it?", "Replace"))
            return;

        int? width = null, height = null;
        if (Size.Height is { } h)
        {
            height = h & ~1;
            width = (int)Math.Round((double)_project.Settings.Width * h / _project.Settings.Height) & ~1;
        }
        var settings = new ExportSettings
        {
            Format = Format,
            Quality = Quality,
            Width = width,
            Height = height,
            RangeStart = start > 0.0001 ? start : null,
            RangeEnd = end < _project.Duration - 0.0001 ? end : null,
        };
        _settings.ExportFormat = Format.Id;
        _settings.ExportQuality = Quality.ToString();
        _settings.LastExportFolder = Path.GetDirectoryName(OutputPath);
        _settings.Save();

        IsExporting = true;
        Progress = 0;
        ProgressText = "Starting…";
        _cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var progress = new Progress<FfmpegProgress>(p =>
        {
            Progress = (p.Fraction ?? 0) * 100;
            var remaining = p.Remaining is { } r ? $" · about {FormatDuration(r)} left" : "";
            var speed = p.Speed > 0 ? $" · {p.Speed:0.0}× real time" : "";
            ProgressText = $"{Progress:0}%{remaining}{speed}";
        });
        try
        {
            var warnings = await new ProjectExporter().ExportAsync(_project, OutputPath, settings, progress, _cts.Token);
            Warnings = string.Join("\n", warnings);
            FinishedPath = OutputPath;
            Progress = 100;
            var size = new FileInfo(OutputPath).Length;
            ProgressText = $"Done in {FormatDuration(DateTime.UtcNow - started)} · {Shell.FormatSize(size)}";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled.";
            Progress = 0;
        }
        catch (Exception ex)
        {
            Log.Error("export", "Export failed.", ex);
            Error = ex is FfmpegException fe ? "ffmpeg: " + fe.Message : ex.Message;
            ProgressText = "";
        }
        finally
        {
            IsExporting = false;
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cts != null)
            _cts.Cancel();
        else
            Close?.Invoke();
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (FinishedPath != null)
            Shell.Open(FinishedPath);
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (FinishedPath != null)
            Shell.ShowInFolder(FinishedPath);
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{Math.Max(0, t.Seconds)}s";

    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length == 0 ? "video" : safe;
    }
}
