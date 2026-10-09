using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        // Tunnel, so editing shortcuts work wherever focus is (except in text boxes).
        AddHandler(KeyDownEvent, OnShortcutKeyDown, RoutingStrategies.Tunnel);
        // A key used as a shortcut does nothing else: no typed letter (drop-downs and lists jump to items
        // starting with it) and no Space release (a focused button would click, undoing play/pause).
        AddHandler(TextInputEvent, (_, e) =>
        {
            if (_shortcutKeyDown)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (_shortcutKeyDown)
            {
                e.Handled = true;
                _shortcutKeyDown = false;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>The last key pressed was taken as a shortcut (until it's released).</summary>
    private bool _shortcutKeyDown;

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    /// <summary>Closes without asking to save (tests).</summary>
    internal void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e) => new HelpWindow().ShowDialog(this);

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || ViewModel == null)
            return;
        if (ViewModel.Capture.IsRecording)
            ViewModel.Capture.StopRecordingCommand.Execute(null);
        if (!ViewModel.Editor.Session.IsDirty)
            return;
        e.Cancel = true;
        if (await ViewModel.Editor.ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>
    /// Shortcuts (Edit tab). Plain keys only act when the focus isn't in a text box:
    ///   Space play/pause · ←/→ frame · Shift+←/→ 1 s · Home/End · ↑/↓ previous/next cut
    ///   S split · Del delete · Shift+Del ripple delete · T title · Esc stop drawing / deselect
    ///   Ctrl+Z / Ctrl+Shift+Z / Ctrl+Y undo/redo · Ctrl+S save · Ctrl+Shift+S save as · Ctrl+O open
    ///   Ctrl+N new · Ctrl+I import · Ctrl+E export · Ctrl+D duplicate · Ctrl+= / Ctrl+- zoom · F1 help
    /// </summary>
    private void OnShortcutKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
            return;
        if (e.Key == Key.F1)
        {
            new HelpWindow().ShowDialog(this);
            e.Handled = true;
            return;
        }
        if (vm.SelectedTab != 0)
            return;
        var editor = vm.Editor;
        _shortcutKeyDown = false;
        var ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var focused = FocusManager?.GetFocusedElement();
        var inText = focused is TextBox;
        // Lists, sliders and drop-downs keep their arrow keys.
        var usesArrows = focused is Slider or ComboBox or ListBox or ListBoxItem or ComboBoxItem;

        if (ctrl)
        {
            Action? action = e.Key switch
            {
                Key.Z when shift => () => editor.RedoCommand.Execute(null),
                Key.Z => () => editor.UndoCommand.Execute(null),
                Key.Y => () => editor.RedoCommand.Execute(null),
                Key.S when shift => () => editor.SaveAsCommand.Execute(null),
                Key.S => () => editor.SaveCommand.Execute(null),
                Key.O => () => editor.OpenProjectCommand.Execute(null),
                Key.N => () => editor.NewProjectCommand.Execute(null),
                Key.I => () => editor.ImportMediaCommand.Execute(null),
                Key.E => () => editor.ExportCommand.Execute(null),
                Key.D when !inText => () => editor.DuplicateCommand.Execute(null),
                Key.OemPlus or Key.Add => () => EditorView?.Zoom(1.5),
                Key.OemMinus or Key.Subtract => () => EditorView?.Zoom(1 / 1.5),
                Key.D0 => () => EditorView?.ZoomToFit(),
                _ => null,
            };
            if (action != null && !(inText && e.Key is Key.Z or Key.Y))
            {
                action();
                e.Handled = true;
            }
            return;
        }
        if (inText || (usesArrows && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
            return;
        Action? plain = e.Key switch
        {
            Key.Space => () => editor.PlayPauseCommand.Execute(null),
            Key.J => () => editor.ShuttleCommand.Execute(-1),
            Key.K => () => editor.ShuttleCommand.Execute(0),
            Key.L => () => editor.ShuttleCommand.Execute(1),
            Key.Left when shift => () => editor.StepSecondsCommand.Execute(-1.0),
            Key.Right when shift => () => editor.StepSecondsCommand.Execute(1.0),
            Key.Left => () => editor.StepFramesCommand.Execute(-1),
            Key.Right => () => editor.StepFramesCommand.Execute(1),
            Key.Up => () => editor.JumpToEdgeCommand.Execute(-1),
            Key.Down => () => editor.JumpToEdgeCommand.Execute(1),
            Key.Home => () => editor.GoToStartCommand.Execute(null),
            Key.End => () => editor.GoToEndCommand.Execute(null),
            Key.S => () => editor.SplitCommand.Execute(null),
            Key.T => () => editor.AddTitleCommand.Execute(null),
            Key.Delete or Key.Back when shift => () => editor.RippleDeleteCommand.Execute(null),
            Key.Delete or Key.Back => () => editor.DeleteCommand.Execute(null),
            Key.Escape => () =>
            {
                if (editor.ActiveTool != null)
                    editor.ActiveTool = null;
                else
                    editor.Select(null);
            },
            _ => null,
        };
        if (plain != null)
        {
            plain();
            e.Handled = true;
            _shortcutKeyDown = true;
        }
    }

    private EditorView? EditorView => this.FindDescendantOfType<EditorView>();
}
