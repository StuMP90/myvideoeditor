using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace MyVideoEditor.App.Controls;

/// <summary>Drag-and-drop helpers: files from the file manager, and media dragged from the bin.</summary>
internal static class DragData
{
    /// <summary>A media bin item (its id) dragged onto the timeline.</summary>
    public static readonly DataFormat<string> MediaId = DataFormat.CreateStringApplicationFormat("myvideoeditor-media-id");

    public static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    public static List<string> Files(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    public static DragDropEffects FilesOnly(DragEventArgs e) => HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
}
