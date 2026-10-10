using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Export;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Upscaling;

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

    /// <summary>What the last conversion used ("1440p · Sharper detail (ArtCNN)").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsText))]
    private string _settingsText = "";

    public bool HasSettingsText => SettingsText.Length > 0;
    public bool CanConvertAgain => State is ConvertState.Done or ConvertState.Failed or ConvertState.Cancelled;

    partial void OnStateChanged(ConvertState value) => OnPropertyChanged(nameof(CanConvertAgain));

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
        _content = Enum.TryParse<ContentType>(settings.ConvertContent, out var content) ? content : ContentType.Camera;
        Scalings = UpscaleMethods.All.Select(m => new UpscaleOptionViewModel(m)).ToList();
        _selectedScaling = Scalings.FirstOrDefault(o => o.Method.Algorithm.ToString() == settings.ConvertScaling && o.IsAvailable) ?? Scalings[0];
    }

    public ObservableCollection<ConvertJobViewModel> Jobs { get; } = [];
    public IReadOnlyList<OutputFormat> Formats { get; }
    public IReadOnlyList<Quality> Qualities { get; } = Enum.GetValues<Quality>();
    public IReadOnlyList<ResizeMode> ResizeModes { get; } = Enum.GetValues<ResizeMode>();
    public IReadOnlyList<UpscaleOptionViewModel> Scalings { get; }

    /// <summary>Methods that can't run here are shown dimmed.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter AvailableOpacity =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(available => available ? 1.0 : 0.45);
    public IReadOnlyList<int> Rotations { get; } = [0, 90, 180, 270];
    public IReadOnlyList<ContentType> ContentTypes { get; } = Enum.GetValues<ContentType>();

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
    [NotifyPropertyChangedFor(nameof(ScalingDescription), nameof(ScalingNeedsDownload), nameof(DownloadLabel))]
    private UpscaleOptionViewModel _selectedScaling;

    [ObservableProperty]
    private ContentType _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestSpeed))]
    private bool _isBusy;

    [ObservableProperty]
    private string _speedText = "";

    public string ScalingDescription => SelectedScaling.Description + (SelectedScaling.Method.Unavailable is { } why ? " " + why : "");

    /// <summary>How much of an AI method's picture to use, 0–100%; the rest is a faithful Lanczos enlargement.</summary>
    public double AiStrength
    {
        get => _settings.ConvertAiStrength * 100;
        set
        {
            _settings.ConvertAiStrength = Math.Clamp(value / 100, 0, 1);
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiStrengthText));
        }
    }

    public string AiStrengthText => $"{AiStrength:0}%";
    public bool ShowsAiStrength => SelectedScaling.Method.IsAi;
    public bool ScalingNeedsDownload => SelectedScaling.IsAvailable && SelectedScaling.Method.NeedsDownload;
    public string DownloadLabel => $"Download {SelectedScaling.Method.Component?.Label} ({Shell.FormatSize(SelectedScaling.Method.DownloadSize)})";
    public bool CanTestSpeed => !IsBusy && !IsRunning && Jobs.Count > 0 && ResizeEnabled;

    // ---- CPU and heat ---------------------------------------------------------------------------

    public static int Cores => Environment.ProcessorCount;
    public decimal MaxThreads => Cores;

    public IReadOnlyList<string> CpuUseOptions { get; } =
        [$"Full ({Cores} threads)", $"Balanced ({Math.Max(1, Cores / 2)} threads)", "Light (2 threads)", "Custom"];

    private static readonly string[] CpuUseKeys = ["Full", "Balanced", "Light", "Custom"];

    public int CpuUseIndex
    {
        get => Math.Max(0, Array.IndexOf(CpuUseKeys, _settings.ConvertCpuUse));
        set
        {
            _settings.ConvertCpuUse = CpuUseKeys[Math.Clamp(value, 0, CpuUseKeys.Length - 1)];
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomCpu));
        }
    }

    public bool IsCustomCpu => _settings.ConvertCpuUse == "Custom";

    public decimal CustomThreads
    {
        get => Math.Clamp(_settings.ConvertCpuThreads, 1, Cores);
        set
        {
            _settings.ConvertCpuThreads = (int)Math.Clamp(value, 1, Cores);
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool LowPriority
    {
        get => _settings.ConvertLowPriority;
        set
        {
            _settings.ConvertLowPriority = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> AiDeviceOptions { get; } = ["Automatic", "CPU", "Graphics card"];

    private static readonly string[] AiDeviceKeys = ["Auto", "Cpu", "Gpu"];

    public int AiDeviceIndex
    {
        get => Math.Max(0, Array.IndexOf(AiDeviceKeys, _settings.ConvertAiDevice));
        set
        {
            _settings.ConvertAiDevice = AiDeviceKeys[Math.Clamp(value, 0, AiDeviceKeys.Length - 1)];
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(OffersVulkanDownload));
            ClearEstimates();
        }
    }

    public string AiDeviceNote => OperatingSystem.IsWindows()
        ? "Graphics card: ArtCNN and the Real-ESRGAN and Real-CUGAN models run on it through DirectML (moving to smaller pieces, then the CPU, if it runs out of memory)."
        : "On Linux the graphics card applies to the Real-ESRGAN and Real-CUGAN models, through their Vulkan programs (download below, with the method selected). " +
          "Automatic uses whichever the speed test found faster. Other methods use the CPU.";

    /// <summary>The selected method's optional Vulkan program (Linux), for running its models on the graphics card.</summary>
    private UpscalerComponent? VulkanProgram => OperatingSystem.IsWindows() ? null : SelectedScaling.Method.VulkanAlternative;

    public bool OffersVulkanDownload =>
        VulkanProgram is { IsSupportedHere: true } c && AiDeviceIndex != 1 && VulkanDevices.Best != null && !UpscalerComponents.IsInstalled(c);

    public string VulkanDownloadLabel => VulkanProgram is { } c ? $"Download {c.Label} ({Shell.FormatSize(c.ForThisComputer?.Size ?? 0)})" : "";

    [RelayCommand]
    private async Task DownloadVulkanProgram()
    {
        if (VulkanProgram is not { } c)
            return;
        var models = c == UpscalerComponents.RealCugan ? "Real-CUGAN" : "Real-ESRGAN";
        if (!await _dialogs.ConfirmAsync("Download " + c.Label,
                $"This runs the {models} models on the graphics card through Vulkan ({Shell.FormatSize(c.ForThisComputer?.Size ?? 0)}; free, runs on this computer).\n\n" +
                $"Licence: {c.Licence}\n{c.Homepage}\n\nDownload it now?", "Download"))
            return;
        IsBusy = true;
        try
        {
            await UpscalerComponents.InstallAsync(c, new Progress<double>(p => Status = $"Downloading {c.Label}… {p * 100:0}%"));
            Status = $"{c.Label} is installed.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't download {c.Label}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(OffersVulkanDownload));
            foreach (var o in Scalings)
                o.Refresh();
        }
    }

    /// <summary>The limits from the settings above.</summary>
    internal ResourceLimits Limits => new(
        _settings.ConvertCpuUse switch
        {
            "Balanced" => Math.Max(1, Cores / 2),
            "Light" => Math.Min(2, Cores),
            "Custom" => Math.Clamp(_settings.ConvertCpuThreads, 1, Cores),
            _ => null,
        },
        _settings.ConvertLowPriority,
        Enum.TryParse<AiDevice>(_settings.ConvertAiDevice, out var device) ? device : AiDevice.Auto);

    partial void OnSelectedScalingChanged(UpscaleOptionViewModel value)
    {
        OnPropertyChanged(nameof(ShowsAiStrength));
        OnPropertyChanged(nameof(OffersVulkanDownload));
        OnPropertyChanged(nameof(VulkanDownloadLabel));
        _settings.ConvertScaling = value.Method.Algorithm.ToString();
        _settings.Save();
    }

    partial void OnContentChanged(ContentType value)
    {
        _settings.ConvertContent = value.ToString();
        _settings.Save();
        Recommend();
    }

    partial void OnSizePresetChanged(string value) => ClearEstimates();
    partial void OnCustomWidthChanged(string value) => ClearEstimates();
    partial void OnCustomHeightChanged(string value) => ClearEstimates();

    private void ClearEstimates()
    {
        foreach (var o in Scalings)
        {
            o.Estimate = null;
            o.IsRecommended = false;
        }
        SpeedText = "";
        OnPropertyChanged(nameof(CanTestSpeed));
    }

    /// <summary>Times each method on a short sample of the first file, on this computer, and recommends one.</summary>
    [RelayCommand]
    private async Task TestSpeed()
    {
        var (settings, error) = BuildSettings();
        var job = Jobs.FirstOrDefault(j => j.Info.HasVideo);
        if (settings == null || job == null)
        {
            SpeedText = error ?? "Add a video first.";
            return;
        }
        // The GPU shaders are small: offer to fetch them so they can be timed too (AI programs are
        // only timed once downloaded, as they're large).
        var shaders = Scalings.Where(o => o.IsAvailable && o.Method.NeedsDownload && o.Method.Kind == UpscaleKind.GpuShader).Select(o => o.Method).ToList();
        var fetchShaders = shaders.Count > 0 && await _dialogs.ConfirmAsync("Download GPU shaders?",
            $"Your graphics card can run the GPU upscalers, which need {string.Join(" and ", shaders.Select(m => m.Component!.Label))} " +
            $"({Shell.FormatSize(shaders.Sum(m => m.DownloadSize))} from GitHub; free, and they run on this computer). Download them to include them in the test?",
            "Download", "Skip");
        IsBusy = true;
        try
        {
            var bench = new UpscaleBenchmark();
            foreach (var option in Scalings.Where(o => o.IsAvailable))
            {
                if (fetchShaders && option.Method.NeedsDownload && option.Method.Kind == UpscaleKind.GpuShader)
                {
                    SpeedText = $"Downloading {option.Method.Component!.Label}…";
                    await UpscalerComponents.InstallAsync(option.Method.Component!);
                    option.Refresh();
                }
                if (option.Method.NeedsDownload)
                    continue;
                SpeedText = $"Timing {option.Label}…";
                option.Estimate = await bench.EstimateAsync(job.Info, settings with { Algorithm = option.Method.Algorithm });
                option.Refresh();
            }
            Recommend(select: true);
        }
        catch (Exception ex)
        {
            Log.Warn("convert", "Speed test failed.", ex);
            SpeedText = "Speed test failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Recommend(bool select = false)
    {
        var estimates = Scalings.Where(o => o.Estimate != null).ToDictionary(o => o.Method.Algorithm, o => o.Estimate!);
        if (estimates.Count == 0)
            return;
        var best = UpscaleMethods.Recommend(Content, estimates, TimeSpan.FromHours(1));
        foreach (var o in Scalings)
            o.IsRecommended = o.Method == best;
        var option = Scalings.First(o => o.Method == best);
        if (select)
            SelectedScaling = option;
        var untested = Scalings.Where(o => o.IsAvailable && o.Method.NeedsDownload && o.Method.Kind == UpscaleKind.AiFrames).Select(o => o.Label).ToList();
        SpeedText = $"Recommended on this computer for {(Content == ContentType.Camera ? "camera footage" : "animation and screen recordings")}: {best.Label}" +
                    (option.Estimate is { } e ? $" ({e.TotalText} for {Jobs.FirstOrDefault()?.Name})." : ".") +
                    (untested.Count > 0 ? $" Not timed until downloaded: {string.Join(", ", untested)}." : "");
    }

    [RelayCommand]
    private async Task DownloadScaling() => await EnsureDownloadedAsync(SelectedScaling.Method, ask: false);

    /// <summary>Downloads the selected method's component (asking first if <paramref name="ask"/>). False if declined or failed.</summary>
    private async Task<bool> EnsureDownloadedAsync(UpscaleMethod method, bool ask)
    {
        if (!method.NeedsDownload)
            return true;
        var component = method.Component!;
        if (ask && !await _dialogs.ConfirmAsync("Download " + component.Label,
                $"{method.Label} needs {component.Label} ({Shell.FormatSize(method.DownloadSize)}), which is free and runs entirely on this computer.\n\n" +
                $"Licence: {component.Licence}\n{component.Homepage}\n\nDownload it now?", "Download"))
            return false;
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => Status = $"Downloading {component.Label}… {p * 100:0}%");
            await UpscalerComponents.InstallAsync(component, progress);
            Status = $"{component.Label} is installed.";
            foreach (var o in Scalings)
                o.Refresh();
            OnPropertyChanged(nameof(ScalingNeedsDownload));
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("convert", $"Downloading {component.Label} failed.", ex);
            Status = $"Couldn't download {component.Label}: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

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
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanTestSpeed));
    public bool ResizeEnabled => SizePreset != "Same as source";
    public bool FormatHasVideo => Format.HasVideo;
    public bool ReencodeOptionsEnabled => !Remux;
    public bool CanStart => !IsRunning && Jobs.Any(j => j.State is ConvertState.Waiting or ConvertState.Failed or ConvertState.Cancelled);
    public string OutputFolderText => OutputFolder ?? "Same folder as each file";

    partial void OnOutputFolderChanged(string? value) => OnPropertyChanged(nameof(OutputFolderText));

    partial void OnFormatChanged(OutputFormat value)
    {
        _settings.ConvertFormat = value.Id;
        _settings.Save();
    }

    // ---- before/after preview ------------------------------------------------------------------

    /// <summary>The file the preview shows (the selected row of the queue).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewDuration), nameof(CanPreview))]
    private ConvertJobViewModel? _selectedJob;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTimeText))]
    private double _previewTime;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _previewBefore;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _previewAfter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreview))]
    private bool _isPreviewing;

    [ObservableProperty]
    private string _previewText = "";

    [ObservableProperty]
    private bool _previewSideBySide;

    /// <summary>0 = fit, 1 = 100%, 2 = 200%.</summary>
    [ObservableProperty]
    private int _previewZoom;

    [ObservableProperty]
    private bool _previewStale;

    /// <summary>Shown over the picture while a preview renders ("Rendering… 12 s").</summary>
    [ObservableProperty]
    private string? _previewOverlay;

    private CancellationTokenSource? _previewCts;

    public IReadOnlyList<string> PreviewZooms { get; } = ["Fit", "100%", "200%"];
    public double PreviewDuration => SelectedJob?.Info.Duration ?? 0;
    public string PreviewTimeText => Timecode.Format(PreviewTime);
    public bool CanPreview => SelectedJob is { Info.HasVideo: true } && !IsPreviewing;

    partial void OnSelectedJobChanged(ConvertJobViewModel? value)
    {
        PreviewTime = (value?.Info.Duration ?? 0) / 2;
        PreviewStale = PreviewAfter != null;
        _ = ScrubAsync();
    }

    partial void OnPreviewTimeChanged(double value)
    {
        PreviewStale = PreviewAfter != null;
        _ = ScrubAsync();
    }

    /// <summary>The original frame at the slider, shown while choosing which frame to preview.</summary>
    [ObservableProperty]
    private Avalonia.Media.Imaging.WriteableBitmap? _scrubFrame;

    [ObservableProperty]
    private string _scrubLabel = "";

    private bool _scrubRunning;
    private bool _scrubAgain;

    /// <summary>One grab at a time; moves during a grab collapse into one more grab of the latest position.</summary>
    private async Task ScrubAsync()
    {
        if (_scrubRunning)
        {
            _scrubAgain = true;
            return;
        }
        _scrubRunning = true;
        try
        {
            do
            {
                _scrubAgain = false;
                if (SelectedJob is not { Info.HasVideo: true } job)
                {
                    ScrubFrame = null;
                    return;
                }
                var time = PreviewTime;
                var frame = await new FrameGrabber().GrabAsync(job.Info, time, Rotate, 1280, 720);
                if (frame != null && !_scrubAgain)
                {
                    ScrubFrame = EditorViewModel.ToBitmap(frame.Bgra, frame.Width, frame.Height);
                    ScrubLabel = $"Original at {Timecode.Format(time)}: press Preview frame to compare";
                }
            } while (_scrubAgain);
        }
        catch (Exception ex)
        {
            Log.Warn("convert", "Couldn't show the frame.", ex);
        }
        finally
        {
            _scrubRunning = false;
        }
    }

    /// <summary>Raised to open the full-window comparison of every method (handled by the view).</summary>
    public event Action<CompareAllViewModel>? CompareAllRequested;

    [RelayCommand]
    private void CompareAll()
    {
        var (settings, error) = BuildSettings();
        if (SelectedJob is not { Info.HasVideo: true } job || settings == null)
        {
            PreviewText = error ?? "Choose a video first.";
            return;
        }
        CompareAllRequested?.Invoke(new CompareAllViewModel(job, settings, PreviewTime, Scalings.Select(o => o.Method).ToList()));
    }

    /// <summary>Any setting that changes the result makes the shown preview out of date.</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (PreviewAfter != null && e.PropertyName is nameof(SelectedScaling) or nameof(SizePreset) or nameof(CustomWidth) or nameof(CustomHeight)
                or nameof(ResizeMode) or nameof(Rotate) or nameof(Deinterlace) or nameof(Format) or nameof(Quality) or nameof(AiStrength) or nameof(AiDeviceIndex))
            PreviewStale = true;
    }

    /// <summary>Renders the frame at <see cref="PreviewTime"/> through the conversion as set up now, next to the original.</summary>
    [RelayCommand]
    private async Task Preview()
    {
        if (SelectedJob is not { } job)
            return;
        var (settings, error) = BuildSettings();
        if (settings == null)
        {
            PreviewText = error ?? "";
            return;
        }
        var method = UpscaleMethods.Get(Converter.Effective(job.Info, settings).Algorithm);
        if (method.Unavailable is { } why)
        {
            PreviewText = $"{method.Label} can't run on this computer: {why}";
            return;
        }
        if (!await EnsureDownloadedAsync(method, ask: true))
            return;
        IsPreviewing = true;
        var time = PreviewTime;
        PreviewText = method.Kind == UpscaleKind.AiFrames
            ? $"Rendering with {method.Label}… AI upscaling takes seconds to minutes per frame, depending on the frame size and the graphics card."
            : $"Rendering with {method.Label}…";
        var dir = Path.Combine(AppPaths.CacheDirectory, "preview-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _previewCts = new CancellationTokenSource();
        // The old picture stays, dimmed, under a ticking "Rendering…", so it's never mistaken for the new one.
        PreviewOverlay = $"Rendering with {method.Label}…";
        var ticker = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), Avalonia.Threading.DispatcherPriority.Background,
            (_, _) => PreviewOverlay = $"Rendering with {method.Label}… {watch.Elapsed.TotalSeconds:0} s");
        ticker.Start();
        try
        {
            var before = Path.Combine(dir, "before.png");
            var after = Path.Combine(dir, "after.png");
            await new Converter().RenderPreviewAsync(job.Info.Path, settings, time, before, after, _previewCts.Token);
            PreviewBefore = LoadBitmap(before);
            PreviewAfter = LoadBitmap(after);
            ScrubFrame = null;
            // Settings or the frame changed while it rendered: it shows what was asked for then, not now.
            PreviewStale = BuildSettings().Settings != settings || Math.Abs(PreviewTime - time) > 1e-6 || SelectedJob != job;
            var (srcW, srcH) = settings.Rotate % 180 == 0 ? (job.Info.Width, job.Info.Height) : (job.Info.Height, job.Info.Width);
            PreviewText = $"Original {srcW}×{srcH} (shown enlarged, unsmoothed) · converted {PreviewAfter.PixelSize.Width}×{PreviewAfter.PixelSize.Height} " +
                          $"with {method.Label} · frame at {Timecode.Format(PreviewTime)} · took {watch.Elapsed.TotalSeconds:0.#} s";
        }
        catch (OperationCanceledException)
        {
            PreviewText = "Preview cancelled.";
            PreviewStale = PreviewAfter != null;
        }
        catch (Exception ex)
        {
            Log.Warn("convert", "Preview failed.", ex);
            PreviewText = "Preview failed: " + (ex is FfmpegException fe ? fe.Message : ex.Message);
            PreviewStale = PreviewAfter != null;
        }
        finally
        {
            ticker.Stop();
            PreviewOverlay = null;
            IsPreviewing = false;
            _previewCts = null;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Stops a preview that's rendering (the AI program and ffmpeg are stopped too).</summary>
    [RelayCommand]
    private void CancelPreview() => _previewCts?.Cancel();

    private static Avalonia.Media.Imaging.Bitmap LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        return new Avalonia.Media.Imaging.Bitmap(stream);
    }

    [RelayCommand]
    private async Task AddFiles()
    {
        var files = await _dialogs.OpenFilesAsync("Add files to convert", true, [_settings.LastMediaFolder], DialogService.MediaFiles);
        if (files.Count > 0)
        {
            _settings.LastMediaFolder = Path.GetDirectoryName(files[0]);
            _settings.Save();
        }
        await AddFilesAsync(files);
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var info = await MediaProbe.Default.ProbeAsync(path);
                if (info.Kind == MediaKind.Animation)
                    throw new NotSupportedException("Lottie animations can't be converted directly: add it to a project in the Edit tab and export.");
                Jobs.Add(new ConvertJobViewModel(info));
                SelectedJob ??= Jobs[^1];
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanTestSpeed));
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
        var folder = await _dialogs.PickFolderAsync("Save converted files to", [OutputFolder, _settings.LastMediaFolder]);
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
            Algorithm = SelectedScaling.Method.Algorithm,
            FrameRate = fps,
            TrimStart = start,
            TrimEnd = end,
            Rotate = Rotate,
            RemoveAudio = RemoveAudio,
            Deinterlace = Deinterlace,
            AiStrength = _settings.ConvertAiStrength,
            Limits = Limits,
        }, null);
    }

    /// <summary>Converts every file that's waiting (and retries failed or cancelled ones), with the current settings.</summary>
    [RelayCommand]
    private async Task Start()
    {
        foreach (var job in Jobs.Where(j => j.State is ConvertState.Failed or ConvertState.Cancelled))
            job.State = ConvertState.Waiting;
        await RunQueueAsync();
    }

    /// <summary>Converts one file again with the current settings (into a new file; earlier results are kept).</summary>
    [RelayCommand]
    private async Task ConvertAgain(ConvertJobViewModel? job)
    {
        if (job is not { CanConvertAgain: true })
            return;
        job.State = ConvertState.Waiting;
        job.Progress = 0;
        OnPropertyChanged(nameof(CanStart));
        // Already running: the queue picks it up.
        if (!IsRunning)
            await RunQueueAsync();
    }

    /// <summary>Converts every file in the list again with the current settings.</summary>
    [RelayCommand]
    private async Task ConvertAllAgain()
    {
        foreach (var job in Jobs.Where(j => j.CanConvertAgain))
        {
            job.State = ConvertState.Waiting;
            job.Progress = 0;
        }
        OnPropertyChanged(nameof(CanStart));
        if (!IsRunning)
            await RunQueueAsync();
    }

    private async Task RunQueueAsync()
    {
        if (IsRunning)
            return;
        var (settings, error) = BuildSettings();
        if (settings == null)
        {
            Status = error ?? "";
            return;
        }
        Status = "";
        var method = SelectedScaling.Method;
        if (method.Kind != UpscaleKind.Cpu && ResizeEnabled)
        {
            if (method.Unavailable is { } why)
            {
                Status = $"{method.Label} can't run on this computer: {why}";
                return;
            }
            if (!await EnsureDownloadedAsync(method, ask: true))
                return;
            if (method.IsAi && !await ConfirmAiTimeAsync(settings))
                return;
        }
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var converter = new Converter();
        var done = 0;
        var description = DescribeSettings(settings);
        try
        {
            // A live queue: files marked "Convert again" while this runs are picked up too.
            while (!_cts.IsCancellationRequested && Jobs.FirstOrDefault(j => j.State == ConvertState.Waiting) is { } job)
            {
                var folder = OutputFolder ?? Path.GetDirectoryName(job.Info.Path)!;
                var output = UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(job.Info.Path) + SuffixFor(settings, ResizeEnabled) + Format.Extension));
                job.State = ConvertState.Converting;
                job.Progress = 0;
                job.Message = "Starting…";
                job.SettingsText = description;
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
            // Anything still waiting after a cancel stays waiting for the next run.
        }
        finally
        {
            IsRunning = false;
            _cts = null;
            Status = done > 0 ? $"Converted {done} file(s)." : "";
            OnPropertyChanged(nameof(CanStart));
        }
    }

    /// <summary>"1440p · Sharper detail (ArtCNN)", "same size · MP4 (H.264)".</summary>
    internal string DescribeSettings(ConversionSettings s)
    {
        var parts = new List<string> { s.Format.Label };
        if (s.Remux)
            parts.Add("copied without re-encoding");
        else if (ResizeEnabled)
        {
            parts.Add(s.Width != null && s.Height != null ? $"{s.Width}×{s.Height} ({s.ResizeMode})" : s.Height != null ? $"{s.Height}p" : $"{s.Width} wide");
            var method = UpscaleMethods.Get(s.Algorithm);
            parts.Add(method.IsAi && s.AiStrength < 0.995 ? $"{method.Label}, {s.AiStrength * 100:0}%" : method.Label);
        }
        if (s.FrameRate is { } fps)
            parts.Add($"{fps:0.##} fps");
        if (s.TrimStart != null || s.TrimEnd != null)
            parts.Add("trimmed");
        return string.Join(" · ", parts);
    }

    /// <summary>AI upscaling can take hours: estimate the whole queue first and ask if it's long.</summary>
    private async Task<bool> ConfirmAiTimeAsync(ConversionSettings settings)
    {
        IsBusy = true;
        Status = "Estimating how long this will take on this computer…";
        try
        {
            var bench = new UpscaleBenchmark();
            var total = TimeSpan.Zero;
            foreach (var job in Jobs.Where(j => j.State != ConvertState.Done && j.Info.HasVideo))
            {
                var e = await bench.EstimateAsync(job.Info, settings);
                if (e.Error != null)
                {
                    Status = $"{SelectedScaling.Label} couldn't run: {e.Error}";
                    return false;
                }
                total += e.Total;
            }
            SelectedScaling.Refresh();
            Status = "";
            if (total < TimeSpan.FromMinutes(30))
                return true;
            return await _dialogs.ConfirmAsync("This will take a while",
                $"{SelectedScaling.Label} will take {UpscaleEstimate.Format(total)} on this computer for {Jobs.Count(j => j.State != ConvertState.Done)} file(s). " +
                "The computer must stay on; you can cancel at any time.\n\n" +
                "Sharper detail (GPU) or Cleaner lines (GPU) take minutes instead. Continue with AI upscaling?", "Start");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>
    /// A file-name suffix describing the settings, so conversions with different settings sit side by
    /// side: "-1440p-ArtCNN", "-1080p-AI-general-40%", "-remux", "-converted".
    /// </summary>
    internal static string SuffixFor(ConversionSettings s, bool resized)
    {
        if (s.Remux)
            return "-remux";
        if (!resized)
            return "-converted";
        var size = s.Width != null && s.Height != null ? $"-{s.Width}x{s.Height}" : s.Height is { } h ? $"-{h}p" : $"-{s.Width}w";
        var method = s.Algorithm switch
        {
            ScaleAlgorithm.ArtCnn => "ArtCNN",
            ScaleAlgorithm.GpuLines => "Anime4K",
            ScaleAlgorithm.AiAnimation => "AI-animation",
            ScaleAlgorithm.AiGeneral => "AI-general",
            ScaleAlgorithm.AiAnime => "AI-CUGAN",
            ScaleAlgorithm.AiPhoto => "AI-photo",
            var other => other.ToString(),
        };
        var strength = UpscaleMethods.Get(s.Algorithm).IsAi && s.AiStrength < 0.995 ? $"-{s.AiStrength * 100:0}%" : "";
        return $"{size}-{method}{strength}";
    }

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
