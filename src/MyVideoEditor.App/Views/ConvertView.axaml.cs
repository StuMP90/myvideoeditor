using Avalonia.Controls;
using Avalonia.Input;
using MyVideoEditor.App.Controls;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

public partial class ConvertView : UserControl
{
    private ConvertViewModel? _subscribed;

    private void OnCompareAllRequested(CompareAllViewModel compare)
    {
        var window = new CompareAllWindow(compare);
        if (TopLevel.GetTopLevel(this) is Window owner)
            window.Show(owner);
        else
            window.Show();
    }

    public ConvertView()
    {
        InitializeComponent();
        // Once per view model: a TabControl resets its pages' DataContext on every tab switch.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ConvertViewModel vm || vm == _subscribed)
                return;
            if (_subscribed != null)
                _subscribed.CompareAllRequested -= OnCompareAllRequested;
            _subscribed = vm;
            vm.CompareAllRequested += OnCompareAllRequested;
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
