using Avalonia.Controls;
using Avalonia.Input;
using MyVideoEditor.App.Controls;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

public partial class ConvertView : UserControl
{
    public ConvertView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ConvertViewModel vm)
                vm.CompareAllRequested += compare =>
                {
                    var window = new CompareAllWindow(compare);
                    if (TopLevel.GetTopLevel(this) is Window owner)
                        window.Show(owner);
                    else
                        window.Show();
                };
        };
        DragDrop.SetAllowDrop(DropZone, true);
        DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragData.FilesOnly(e));
        DropZone.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = DragData.Files(e);
            if (DataContext is ConvertViewModel vm && files.Count > 0)
                await vm.AddFilesAsync(files);
        });
    }
}
