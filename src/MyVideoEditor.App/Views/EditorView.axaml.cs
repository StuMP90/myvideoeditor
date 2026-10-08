using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MyVideoEditor.App.Controls;
using MyVideoEditor.App.ViewModels;

namespace MyVideoEditor.App.Views;

public partial class EditorView : UserControl
{
    private readonly List<(ToggleButton Button, AnnotationTool Tool)> _toolButtons = [];
    private bool _syncingScroll;
    private Point? _mediaDragStart;

    public EditorView()
    {
        InitializeComponent();
        Timeline.ScrollChanged += SyncScrollBar;
        TimelineScroll.Scroll += (_, e) =>
        {
            if (!_syncingScroll)
                Timeline.ScrollSeconds = e.NewValue;
        };
        TrackScroll.Scroll += (_, e) =>
        {
            if (!_syncingScroll)
                Timeline.ScrollY = e.NewValue;
        };
        Timeline.SizeChanged += (_, _) => SyncScrollBar();
        MediaList.DoubleTapped += (_, _) => ViewModel?.AddMediaToTimelineCommand.Execute(null);
        MediaList.AddHandler(PointerPressedEvent, OnMediaPointerPressed, RoutingStrategies.Tunnel);
        MediaList.AddHandler(PointerMovedEvent, OnMediaPointerMoved, RoutingStrategies.Tunnel);
        MediaList.ContextRequested += OnMediaContextRequested;
        DragDrop.SetAllowDrop(MediaList, true);
        MediaList.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragData.FilesOnly(e));
        MediaList.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var files = DragData.Files(e);
            if (ViewModel != null && files.Count > 0)
                await ViewModel.ImportFilesAsync(files, addToTimeline: false);
        });

        foreach (var tool in EditorViewModel.Tools)
        {
            var button = new ToggleButton { Content = tool.Glyph, Padding = new Thickness(7, 4), MinWidth = 30 };
            ToolTip.SetTip(button, $"{tool.Label}: {tool.Tip}. Then drag on the preview (hold Shift to draw several).");
            button.Click += (_, _) => ViewModel?.ToggleToolCommand.Execute(tool);
            ToolButtons.Children.Add(button);
            _toolButtons.Add((button, tool));
        }

        var titles = new MenuFlyout();
        foreach (var template in EditorViewModel.TitleTemplates)
        {
            var item = new MenuItem { Header = template.Label };
            item.Click += (_, _) => ViewModel?.AddTitleCommand.Execute(template);
            titles.Items.Add(item);
        }
        TitleButton.Flyout = titles;
    }

    private EditorViewModel? ViewModel => DataContext as EditorViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Timeline.Editor = ViewModel;
        Preview.Editor = ViewModel;
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += (_, args) =>
            {
                switch (args.PropertyName)
                {
                    case nameof(EditorViewModel.ActiveTool):
                        foreach (var (button, tool) in _toolButtons)
                            button.IsChecked = ViewModel.ActiveTool == tool;
                        break;
                    case nameof(EditorViewModel.IsPlaying):
                        PlayButton.Content = ViewModel.IsPlaying ? "❚❚" : "▶";
                        break;
                    case nameof(EditorViewModel.DurationText):
                        SyncScrollBar();
                        break;
                }
            };
            ViewModel.ExportRequested += export => new ExportWindow { DataContext = export }.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException());
            ViewModel.ProjectSettingsRequested += () =>
            {
                if (TopLevel.GetTopLevel(this) is Window owner)
                    _ = ProjectSettingsWindow.ShowAsync(owner, ViewModel);
            };
            Dispatcher.UIThread.Post(() => Timeline.ZoomToFit(), DispatcherPriority.Background);
        }
    }

    public void Zoom(double factor) => Timeline.ZoomBy(factor);
    public void ZoomToFit() => Timeline.ZoomToFit();

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Timeline.ZoomBy(1.5);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Timeline.ZoomBy(1 / 1.5);
    private void OnZoomFit(object? sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    private void SyncScrollBar()
    {
        _syncingScroll = true;
        TimelineScroll.Minimum = 0;
        TimelineScroll.ViewportSize = Timeline.ViewportSeconds;
        TimelineScroll.Maximum = Math.Max(0, Timeline.ExtentSeconds - Timeline.ViewportSeconds * 0.5);
        TimelineScroll.LargeChange = Timeline.ViewportSeconds * 0.8;
        TimelineScroll.SmallChange = Timeline.ViewportSeconds * 0.1;
        TimelineScroll.Value = Timeline.ScrollSeconds;
        var overflow = Math.Max(0, Timeline.ContentHeight - Timeline.TracksViewportHeight);
        TrackScroll.IsVisible = overflow > 0;
        TrackScroll.Maximum = overflow;
        TrackScroll.ViewportSize = Timeline.TracksViewportHeight;
        TrackScroll.LargeChange = Timeline.TracksViewportHeight * 0.8;
        TrackScroll.SmallChange = 30;
        TrackScroll.Value = Timeline.ScrollY;
        _syncingScroll = false;
    }

    // ---- media bin: drag to timeline, context menu --------------------------------------------

    private void OnMediaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(MediaList).Properties.IsLeftButtonPressed)
            _mediaDragStart = e.GetPosition(MediaList);
    }

    private async void OnMediaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mediaDragStart is not { } start || !e.GetCurrentPoint(MediaList).Properties.IsLeftButtonPressed)
        {
            _mediaDragStart = null;
            return;
        }
        var p = e.GetPosition(MediaList);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6)
            return;
        _mediaDragStart = null;
        var item = (e.Source as Control)?.DataContext as MediaItemViewModel ?? ViewModel?.SelectedMedia;
        if (item == null)
            return;
        var entry = new DataTransferItem();
        entry.Set(DragData.MediaId, item.Id);
        var data = new DataTransfer();
        data.Add(entry);
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy);
    }

    private void OnMediaContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var item = (e.Source as Control)?.DataContext as MediaItemViewModel;
        if (item == null || ViewModel == null)
            return;
        ViewModel.SelectedMedia = item;
        var add = new MenuItem { Header = "Add to timeline at playhead" };
        add.Click += (_, _) => ViewModel.AddMediaToTimelineCommand.Execute(item);
        var show = new MenuItem { Header = "Show in folder" };
        show.Click += (_, _) => Services.Shell.ShowInFolder(item.Path);
        var remove = new MenuItem { Header = "Remove from project" };
        remove.Click += (_, _) => ViewModel.RemoveMediaCommand.Execute(item);
        new ContextMenu { ItemsSource = new object[] { add, show, new Separator(), remove } }.Open(MediaList);
        e.Handled = true;
    }
}
