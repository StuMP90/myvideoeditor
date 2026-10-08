using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

/// <summary>A small always-on-top box with the recording time and a Stop button.</summary>
public sealed class RecordingIndicatorWindow : Window
{
    public RecordingIndicatorWindow()
    {
        SystemDecorations = SystemDecorations.BorderOnly;
        Topmost = true;
        ShowInTaskbar = true;
        CanResize = false;
        Title = "Recording — MyVideoEditor";
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.Parse("#1B1F27"));

        var dot = new TextBlock { Text = "●", Foreground = new SolidColorBrush(Color.Parse("#FF3B30")), FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var time = new TextBlock { FontSize = 18, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, DejaVu Sans Mono, Consolas, monospace") };
        time.Bind(TextBlock.TextProperty, new Binding(nameof(CaptureViewModel.Elapsed)));
        var stop = new Button { Content = "■ Stop", MinWidth = 80 };
        stop.Classes.Add("record");
        stop.Click += (_, _) => (DataContext as CaptureViewModel)?.StopRecordingCommand.Execute(null);
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(12, 8),
            Children = { dot, time, stop },
        };
        Opened += (_, _) =>
        {
            // Top-right corner of the main screen, out of the way of most content.
            if (Screens.Primary is { } screen)
                Position = new PixelPoint(screen.WorkingArea.Right - (int)(Bounds.Width * screen.Scaling) - 24, screen.WorkingArea.Y + 24);
        };
    }
}
