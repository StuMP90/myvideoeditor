using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyVideoEditor.Core;
using MyVideoEditor.Core.Conversion;
using MyVideoEditor.Core.Diagnostics;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Upscaling;

namespace MyVideoEditor.App.ViewModels;

public enum TileState
{
    Waiting,
    Rendering,
    Done,
    Failed,
    Cancelled,
}

/// <summary>One picture in the comparison: the original, or the frame through one method.</summary>
public sealed partial class CompareTileViewModel(string label, UpscaleMethod? method) : ObservableObject
{
    public string Label { get; } = label;
    public UpscaleMethod? Method { get; } = method;

    [ObservableProperty]
    private Bitmap? _picture;

    [ObservableProperty]
    private TileState _state;

    /// <summary>"0.9 s on the CPU", "Rendering… 12 s", "Failed: …".</summary>
    [ObservableProperty]
    private string _detail = "Waiting";
}

/// <summary>
/// The full-window comparison: one frame through every upscaling method this computer can run
/// (installed and supported here), next to the original, all laid out identically so they can
/// be compared with a shared zoom and pan. Methods render one after another, fastest first.
/// </summary>
public sealed partial class CompareAllViewModel : ObservableObject
{
    private readonly ConvertJobViewModel _job;
    private readonly ConversionSettings _settings;
    private CancellationTokenSource? _cts;

    public CompareAllViewModel(ConvertJobViewModel job, ConversionSettings settings, double time, IReadOnlyList<UpscaleMethod> methods)
    {
        _job = job;
        _settings = settings;
        Time = time;
        var runnable = methods.Where(m => m.Unavailable == null && !m.NeedsDownload)
            .OrderBy(m => m.Kind switch { UpscaleKind.Cpu => 0, UpscaleKind.Onnx when !m.IsAi => 1, UpscaleKind.GpuShader => 2, UpscaleKind.Onnx => 3, _ => 4 })
            .ThenBy(m => m.Quality)
            .ToList();
        Tiles.Add(new CompareTileViewModel("Original (enlarged, unsmoothed)", null));
        foreach (var m in runnable)
            Tiles.Add(new CompareTileViewModel(m.HasStrength && settings.AiStrength < 0.995 ? $"{m.Label}, {settings.AiStrength * 100:0}% strength" : m.Label, m));
        var missing = methods.Where(m => m.Unavailable == null && m.NeedsDownload).Select(m => m.Label).ToList();
        var unsupported = methods.Where(m => m.Unavailable != null).Select(m => m.Label).ToList();
        Notes = string.Join("  ", new[]
        {
            missing.Count > 0 ? $"Not included until downloaded (Convert tab): {string.Join(", ", missing)}." : "",
            unsupported.Count > 0 ? $"Can't run on this computer: {string.Join(", ", unsupported)}." : "",
        }.Where(t => t.Length > 0));
        var (srcW, srcH) = settings.Rotate % 180 == 0 ? (job.Info.Width, job.Info.Height) : (job.Info.Height, job.Info.Width);
        var size = ScaledSize.For(srcW, srcH, settings.Width, settings.Height, settings.ResizeMode);
        Title = $"{job.Name} at {Timecode.Format(time)}: {srcW}×{srcH} → {(settings.Width != null || settings.Height != null ? $"{settings.Width ?? size.Width}×{settings.Height ?? size.Height}" : "same size")}";
    }

    public ObservableCollection<CompareTileViewModel> Tiles { get; } = [];
    public double Time { get; }
    public string Title { get; }
    public string Notes { get; }
    public IReadOnlyList<string> Zooms { get; } = ["Fit", "100%", "200%", "400%"];

    /// <summary>0 = fit, 1–3 = 100/200/400%.</summary>
    [ObservableProperty]
    private int _zoom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isRendering;

    [ObservableProperty]
    private string _status = "";

    public bool CanCancel => IsRendering;

    /// <summary>Renders every tile in turn (called when the window opens).</summary>
    public async Task RenderAllAsync()
    {
        _cts = new CancellationTokenSource();
        IsRendering = true;
        var dir = Path.Combine(AppPaths.CacheDirectory, "compare-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var converter = new Converter();
        try
        {
            var original = Tiles[0];
            for (var i = 1; i < Tiles.Count; i++)
            {
                var tile = Tiles[i];
                if (_cts.IsCancellationRequested)
                {
                    tile.State = TileState.Cancelled;
                    tile.Detail = "Cancelled";
                    continue;
                }
                tile.State = TileState.Rendering;
                Status = $"Rendering {tile.Label} ({i} of {Tiles.Count - 1})…";
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var ticker = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
                    (_, _) => tile.Detail = $"Rendering… {watch.Elapsed.TotalSeconds:0} s");
                tile.Detail = "Rendering…";
                ticker.Start();
                try
                {
                    var after = Path.Combine(dir, $"after{i}.png");
                    var before = original.Picture == null ? Path.Combine(dir, "before.png") : null;
                    await converter.RenderPreviewAsync(_job.Info.Path, _settings with { Algorithm = tile.Method!.Algorithm }, Time, before, after, _cts.Token);
                    if (before != null)
                    {
                        original.Picture = Load(before);
                        original.State = TileState.Done;
                        original.Detail = $"{_job.Info.Width}×{_job.Info.Height}";
                    }
                    tile.Picture = Load(after);
                    tile.State = TileState.Done;
                    tile.Detail = $"{watch.Elapsed.TotalSeconds:0.#} s" + (tile.Method.Kind switch
                    {
                        UpscaleKind.Onnx => " on the " + OnnxEngine.DeviceFor(tile.Method.OnnxModel!),
                        UpscaleKind.GpuShader or UpscaleKind.AiFrames => " on the graphics card",
                        _ => " on the CPU",
                    });
                }
                catch (OperationCanceledException)
                {
                    tile.State = TileState.Cancelled;
                    tile.Detail = "Cancelled";
                }
                catch (Exception ex)
                {
                    Log.Warn("compare", $"{tile.Label} failed.", ex);
                    tile.State = TileState.Failed;
                    tile.Detail = "Failed: " + ex.Message;
                }
                finally
                {
                    ticker.Stop();
                }
            }
            Status = _cts.IsCancellationRequested ? "Stopped." : "Done. Double-click a picture to look at it on its own.";
        }
        finally
        {
            IsRendering = false;
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    public void Stop() => _cts?.Cancel();

    private static Bitmap Load(string path)
    {
        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }
}
