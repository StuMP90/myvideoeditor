using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.App.Views;

/// <summary>Project size, frame rate and background colour.</summary>
public static class ProjectSettingsWindow
{
    private static readonly double[] FrameRates = [23.976, 24, 25, 29.97, 30, 50, 59.94, 60];

    public static async Task ShowAsync(Window owner, EditorViewModel editor)
    {
        var current = editor.Project.Settings;
        var sizes = ProjectSettings.CommonSizes.Select(s => s.Name).Append("Custom").ToList();
        var sizeBox = new ComboBox { ItemsSource = sizes, HorizontalAlignment = HorizontalAlignment.Stretch };
        var match = ProjectSettings.CommonSizes.FirstOrDefault(s => s.Width == current.Width && s.Height == current.Height);
        sizeBox.SelectedItem = match.Name ?? "Custom";
        var width = new NumericUpDown { Value = current.Width, Minimum = 16, Maximum = 8192, Increment = 2, FormatString = "0" };
        var height = new NumericUpDown { Value = current.Height, Minimum = 16, Maximum = 8192, Increment = 2, FormatString = "0" };
        sizeBox.SelectionChanged += (_, _) =>
        {
            var preset = ProjectSettings.CommonSizes.FirstOrDefault(s => s.Name == sizeBox.SelectedItem as string);
            if (preset.Name != null)
            {
                width.Value = preset.Width;
                height.Value = preset.Height;
            }
        };
        var fps = new ComboBox
        {
            ItemsSource = FrameRates.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture)).ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedItem = current.FrameRate.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (fps.SelectedItem == null)
            fps.SelectedIndex = 4;
        var background = new ColorPicker { Color = Color.TryParse(current.BackgroundColor, out var c) ? c : Colors.Black, IsAlphaEnabled = false, HorizontalAlignment = HorizontalAlignment.Left };

        var dialog = new Window
        {
            Title = "Project settings",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var apply = false;
        ok.Click += (_, _) => { apply = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        Grid Row(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), Margin = new Thickness(0, 4) };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { width, new TextBlock { Text = "×", VerticalAlignment = VerticalAlignment.Center }, height } };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 4,
            Children =
            {
                Row("Size", sizeBox),
                Row("", sizeRow),
                Row("Frame rate", fps),
                Row("Background", background),
                new TextBlock
                {
                    Text = "Exports use these settings (you can still export at another size). Clips are fitted to the frame, so changing the shape adds bars or crops to match.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.65,
                    FontSize = 12,
                    Margin = new Thickness(0, 8),
                },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
            },
        };
        await dialog.ShowDialog(owner);
        if (!apply)
            return;
        var settings = current.Clone();
        settings.Width = (int)(width.Value ?? current.Width) & ~1;
        settings.Height = (int)(height.Value ?? current.Height) & ~1;
        if (double.TryParse(fps.SelectedItem as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
            settings.FrameRate = rate;
        var col = background.Color;
        settings.BackgroundColor = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
        editor.ApplySettings(settings);
    }
}
