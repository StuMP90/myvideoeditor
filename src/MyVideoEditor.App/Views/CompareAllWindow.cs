using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using MyVideoEditor.App.Controls;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

/// <summary>A full window comparing one frame through every upscaling method this computer can run.</summary>
public sealed class CompareAllWindow : Window
{
    public CompareAllWindow(CompareAllViewModel viewModel)
    {
        DataContext = viewModel;
        Title = "Compare upscaling methods — " + viewModel.Title;
        Width = 1400;
        Height = 900;
        WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var zoom = new ComboBox { ItemsSource = viewModel.Zooms, MinWidth = 90, VerticalAlignment = VerticalAlignment.Center };
        zoom.Bind(ComboBox.SelectedIndexProperty, new Binding(nameof(CompareAllViewModel.Zoom)) { Mode = BindingMode.TwoWay });
        ToolTip.SetTip(zoom, "Zoom (or the mouse wheel); drag any picture to move them all");
        var cancel = new Button { Content = "Stop", VerticalAlignment = VerticalAlignment.Center };
        cancel.Bind(Button.CommandProperty, new Binding(nameof(CompareAllViewModel.CancelCommand)));
        cancel.Bind(IsVisibleProperty, new Binding(nameof(CompareAllViewModel.IsRendering)));
        var close = new Button { Content = "Close", VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => Close();
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(CompareAllViewModel.Status)));
        var title = new TextBlock { Text = viewModel.Title, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };

        var toolbar = new DockPanel { Margin = new Thickness(10, 8) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { zoom, cancel, close } };
        DockPanel.SetDock(right, Dock.Right);
        DockPanel.SetDock(title, Dock.Left);
        toolbar.Children.Add(right);
        toolbar.Children.Add(title);
        toolbar.Children.Add(status);

        var notes = new TextBlock { Text = viewModel.Notes, Opacity = 0.65, FontSize = 12, Margin = new Thickness(10, 0, 10, 8), TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = viewModel.Notes.Length > 0 };

        var grid = new MultiCompareControl { Tiles = viewModel.Tiles };
        grid.Bind(MultiCompareControl.ZoomProperty, new Binding(nameof(CompareAllViewModel.Zoom)) { Mode = BindingMode.TwoWay });

        var layout = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(notes, Dock.Bottom);
        layout.Children.Add(toolbar);
        layout.Children.Add(notes);
        layout.Children.Add(grid);
        Content = layout;

        Opened += async (_, _) => await viewModel.RenderAllAsync();
        Closing += (_, _) => viewModel.Stop();
    }
}
