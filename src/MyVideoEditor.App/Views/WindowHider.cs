using Avalonia.Controls;

namespace MyVideoEditor.App.Views;

/// <summary>
/// Gets the app's window out of the way of a capture and brings it back. It's hidden rather than
/// minimised: on Linux (X11) a minimised window's state isn't always reported back, so it couldn't
/// be relied on to restore; hiding and showing behaves the same everywhere, and keeps a maximised
/// window maximised.
/// </summary>
internal static class WindowHider
{
    public static void Hide(Window window)
    {
        if (window.IsVisible)
            window.Hide();
    }

    public static void Restore(Window window)
    {
        if (!window.IsVisible)
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
