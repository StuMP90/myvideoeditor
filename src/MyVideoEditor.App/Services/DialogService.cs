using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MyVideoEditor.App.Services;

/// <summary>Small modal dialogs, built in code so they need no extra views.</summary>
public sealed class DialogService
{
    private readonly Window _owner;

    public DialogService(Window owner) => _owner = owner;

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", string cancelText = "Cancel", bool danger = false) =>
        OnUiThread(() => ShowAsync(title, message, confirmText, cancelText, danger));

    public Task AlertAsync(string title, string message) =>
        OnUiThread(() => ShowAsync(title, message, "OK", null, false));

    /// <summary>Asks for a secret (masked). Returns null if cancelled. Nothing is stored.</summary>
    public Task<string?> PromptSecretAsync(string title, string message, string? error, string watermark) =>
        OnUiThread(async () =>
        {
            var dialog = new Window
            {
                Title = title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            var input = new TextBox { PasswordChar = '●', Watermark = watermark };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
            ok.Classes.Add("accent");
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            string? result = null;
            ok.Click += (_, _) => { result = input.Text ?? ""; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();

            var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (error != null)
                panel.Children.Add(new TextBlock { Text = error, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { cancel, ok },
            });
            dialog.Content = panel;
            dialog.Opened += (_, _) => input.Focus();
            await dialog.ShowDialog(_owner);
            return result;
        });

    /// <summary>Asks for a line of text. Returns null if cancelled.</summary>
    public Task<string?> PromptTextAsync(string title, string message, string initial) =>
        OnUiThread(async () =>
        {
            var dialog = NewDialog(title, 460);
            var input = new TextBox { Text = initial };
            string? result = null;
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => { result = input.Text ?? ""; dialog.Close(); };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
            cancel.Click += (_, _) => dialog.Close();
            dialog.Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                    Buttons(cancel, ok),
                },
            };
            dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
            await dialog.ShowDialog(_owner);
            return result;
        });

    /// <summary>
    /// Shows a message with several buttons and an optional checkbox. Returns the chosen button's
    /// index (-1 if the window was closed) and whether the box was ticked.
    /// </summary>
    public Task<(int Choice, bool Checked)> ChooseAsync(string title, string message, IReadOnlyList<string> buttons, string? checkbox = null) =>
        OnUiThread(async () =>
        {
            var dialog = NewDialog(title, Math.Max(520, 140 * buttons.Count));
            var choice = -1;
            var box = checkbox == null ? null : new CheckBox { Content = checkbox };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            for (var i = 0; i < buttons.Count; i++)
            {
                var index = i;
                var button = new Button { Content = buttons[i], MinWidth = 90, IsDefault = i == 0 };
                if (i == 0)
                    button.Classes.Add("accent");
                button.Click += (_, _) => { choice = index; dialog.Close(); };
                row.Children.Add(button);
            }
            var panel = new StackPanel { Margin = new Avalonia.Thickness(20), Spacing = 12 };
            panel.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (box != null)
                panel.Children.Add(box);
            panel.Children.Add(row);
            dialog.Content = panel;
            await dialog.ShowDialog(_owner);
            return (choice, box?.IsChecked == true);
        });

    private static Window NewDialog(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static StackPanel Buttons(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    /// <summary>File types for open/save pickers.</summary>
    public static readonly FilePickerFileType MediaFiles = new("Video, audio and images")
    {
        Patterns =
        [
            "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.m4v", "*.mts", "*.m2ts", "*.ts", "*.wmv", "*.flv", "*.mpg", "*.mpeg", "*.3gp", "*.gif",
            "*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.opus", "*.wma", "*.aif", "*.aiff",
            "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff",
        ],
        MimeTypes = ["video/*", "audio/*", "image/*"],
    };

    public static readonly FilePickerFileType ProjectFiles = new("MyVideoEditor projects") { Patterns = ["*.mveproj"] };

    public async Task<IReadOnlyList<string>> OpenFilesAsync(string title, bool multiple, params FilePickerFileType[] types)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = multiple,
            FileTypeFilter = types.Length > 0 ? [.. types, FilePickerFileTypes.All] : null,
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension, string typeName, string? folder = null)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = ["*" + extension] }],
        };
        if (folder != null && Directory.Exists(folder))
            options.SuggestedStartLocation = await _owner.StorageProvider.TryGetFolderFromPathAsync(folder);
        var file = await _owner.StorageProvider.SaveFilePickerAsync(options);
        var path = file?.TryGetLocalPath();
        if (path != null && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path += extension;
        return path;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static Task<T> OnUiThread<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    private async Task<bool> ShowAsync(string title, string message, string confirmText, string? cancelText, bool danger)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var confirm = new Button { Content = confirmText, IsDefault = true, MinWidth = 90 };
        if (danger)
            confirm.Classes.Add("accent");
        confirm.Click += (_, _) => dialog.Close(true);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 16, 0, 0),
        };
        if (cancelText != null)
        {
            var cancel = new Button { Content = cancelText, IsCancel = true, MinWidth = 90 };
            cancel.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(confirm);

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new SelectableTextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Cascadia Mono, JetBrains Mono, DejaVu Sans Mono, Consolas, monospace, Inter"),
                },
                buttons,
            },
        };

        var result = await dialog.ShowDialog<bool?>(_owner);
        return result == true;
    }
}
