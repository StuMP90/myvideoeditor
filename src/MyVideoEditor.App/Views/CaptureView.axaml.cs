using Avalonia.Controls;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.Core.Capture;

namespace MyVideoEditor.App.Views;

public partial class CaptureView : UserControl
{
    private RecordingIndicatorWindow? _indicator;
    private CaptureViewModel? _subscribed;

    public CaptureView()
    {
        InitializeComponent();
    }

    private CaptureViewModel? ViewModel => DataContext as CaptureViewModel;
    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Once per view model: a TabControl resets its pages' DataContext on every tab switch.
        if (ViewModel is not { } vm || vm == _subscribed)
            return;
        if (_subscribed != null)
            _subscribed.RecordingChanged -= OnRecordingChanged;
        _subscribed = vm;
        vm.SelectRegion = async () => Owner == null ? null : await RegionSelectWindow.SelectAsync(Owner);
        vm.RecordingChanged += OnRecordingChanged;
    }

    protected override async void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (ViewModel is not { } vm)
            return;
        if (Owner?.Screens is { } screens)
        {
            var index = 1;
            vm.SetScreens(screens.All.Select(s => new CaptureTarget(CaptureTargetKind.Screen,
                screens.All.Count == 1 ? "Whole screen" : $"Screen {index++}{(s.IsPrimary ? " (main)" : "")}",
                s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height)));
        }
        await vm.LoadAudioDevicesAsync();
    }

    /// <summary>While recording: optionally get out of the way, and show a small always-on-top Stop button.</summary>
    private void OnRecordingChanged(bool recording)
    {
        if (Owner is not { } owner || ViewModel is not { } vm)
            return;
        if (recording)
        {
            if (vm.Minimise)
                WindowHider.Hide(owner);
            if (_indicator == null && (vm.IsRecording || vm.IsCountingDown))
            {
                _indicator = new RecordingIndicatorWindow { DataContext = vm };
                _indicator.Show();
            }
        }
        else
        {
            _indicator?.Close();
            _indicator = null;
            WindowHider.Restore(owner);
        }
    }
}
