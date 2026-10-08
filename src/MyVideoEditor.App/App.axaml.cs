using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyVideoEditor.App.Services;
using MyVideoEditor.App.ViewModels;
using MyVideoEditor.App.Views;

namespace MyVideoEditor.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var dialogs = new DialogService(window);
            var settings = AppSettings.Load();
            var audio = OpenAlAudioSink.TryCreate();
            var viewModel = new MainWindowViewModel(dialogs, settings, audio);
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) =>
            {
                viewModel.Dispose();
                audio?.Dispose();
            };
            window.Opened += async (_, _) =>
            {
                await viewModel.Editor.OfferRecoveryAsync();
                // A project or media file passed on the command line (or "Open with").
                var files = desktop.Args?.Where(File.Exists).ToList() ?? [];
                if (files.Count > 0)
                    await viewModel.Editor.ImportFilesAsync(files, addToTimeline: true);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
