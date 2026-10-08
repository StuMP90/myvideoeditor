using Avalonia.Controls;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

public partial class ExportWindow : Window
{
    public ExportWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ExportViewModel vm)
            vm.Close = Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing mid-export cancels it (the partial file is removed).
        if (DataContext is ExportViewModel { IsExporting: true } vm)
            vm.CancelCommand.Execute(null);
        base.OnClosing(e);
    }
}
