using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MyVideoEditor.Core.Media;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.App.ViewModels;

/// <summary>A file in the project's media bin.</summary>
public sealed partial class MediaItemViewModel : ObservableObject
{
    public MediaItemViewModel(MediaReference media)
    {
        Id = media.Id;
        Name = media.Name;
        Path = media.Path;
        Kind = media.Kind;
        IsMissing = media.IsMissing;
        Summary = IsMissing ? "Missing: " + media.Path : Describe(media);
        KindLabel = media.Kind switch { MediaKind.Audio => "♪", MediaKind.Image => "▣", MediaKind.Animation => "✦", _ => "▶" };
    }

    public string Id { get; }
    public string Name { get; }
    public string Path { get; }
    public MediaKind Kind { get; }
    public bool IsMissing { get; }
    public string Summary { get; }
    public string KindLabel { get; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    private static string Describe(MediaReference m)
    {
        var parts = new List<string>();
        if (m.HasVideo)
            parts.Add($"{m.Width}×{m.Height}");
        if (m.Kind is MediaKind.Video or MediaKind.Animation && m.FrameRate > 0)
            parts.Add($"{m.FrameRate:0.##} fps");
        if (m.Kind != MediaKind.Image)
            parts.Add(Core.Timecode.Format(m.Duration));
        if (m.Kind == MediaKind.Audio)
            parts.Insert(0, "Audio");
        return string.Join(" · ", parts);
    }
}
