using CommunityToolkit.Mvvm.ComponentModel;
using MyVideoEditor.App.Services;
using MyVideoEditor.Core.Upscaling;

namespace MyVideoEditor.App.ViewModels;

/// <summary>An upscaling method in the Convert tab, with whether it can run here and how long it would take.</summary>
public sealed partial class UpscaleOptionViewModel : ObservableObject
{
    public UpscaleOptionViewModel(UpscaleMethod method) => Method = method;

    public UpscaleMethod Method { get; }
    public string Label => Method.Label;
    public string Description => Method.Description;
    public bool IsAvailable => Method.Unavailable == null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private UpscaleEstimate? _estimate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _isRecommended;

    /// <summary>"Ready · about 4 min", "Download 46 MB", "Needs a graphics card with Vulkan support."</summary>
    public string Status
    {
        get
        {
            if (Method.Unavailable is { } why)
                return why;
            var parts = new List<string>();
            if (IsRecommended)
                parts.Add("Recommended");
            parts.Add(Method.Kind switch
            {
                UpscaleKind.Cpu => "CPU",
                UpscaleKind.GpuShader => "GPU",
                _ => "GPU, AI",
            });
            if (Method.NeedsDownload)
                parts.Add($"download {Shell.FormatSize(Method.DownloadSize)}");
            if (Estimate != null)
                parts.Add(Estimate.Error != null ? "couldn't run here" : Estimate.TotalText);
            return string.Join(" · ", parts);
        }
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}
