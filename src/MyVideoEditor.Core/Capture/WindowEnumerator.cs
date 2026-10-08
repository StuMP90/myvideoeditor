using System.Runtime.InteropServices;
using System.Text;
using MyVideoEditor.Core.Diagnostics;

namespace MyVideoEditor.Core.Capture;

/// <summary>Lists visible top-level application windows (Windows and Linux/X11).</summary>
public static partial class WindowEnumerator
{
    /// <summary>True when the desktop is a Wayland session, where screen capture through X11 only sees X11 apps.</summary>
    public static bool IsWayland =>
        OperatingSystem.IsLinux() && (string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                                      || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")));

    public static IReadOnlyList<CaptureTarget> ListWindows(long excludeProcessId = 0)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return Win32.List(excludeProcessId);
            if (OperatingSystem.IsLinux())
                return X11.List(excludeProcessId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            Log.Warn("capture", "Couldn't list windows.", ex);
        }
        return [];
    }

    // ---- Windows -----------------------------------------------------------------------------

    private static partial class Win32
    {
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        private static partial int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static partial IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);

        [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static partial int DwmGetWindowAttributeInt(IntPtr hwnd, int attribute, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_TOOLWINDOW = 0x80;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int DWMWA_CLOAKED = 14;

        public static List<CaptureTarget> List(long excludePid)
        {
            var result = new List<CaptureTarget>();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0)
                    return true;
                if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
                    return true;
                if (DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0)
                    return true; // on another virtual desktop, or a suspended UWP app
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == excludePid)
                    return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<Rect>()) != 0)
                    return true;
                var width = r.Right - r.Left;
                var height = r.Bottom - r.Top;
                if (width < 40 || height < 40)
                    return true;
                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                result.Add(new CaptureTarget(CaptureTargetKind.Window, title.ToString(), r.Left, r.Top, width, height, hwnd.ToInt64()));
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }

    // ---- Linux / X11 -------------------------------------------------------------------------

    private static partial class X11
    {
        private const string Lib = "libX11.so.6";

        [LibraryImport(Lib)]
        private static partial IntPtr XOpenDisplay(IntPtr name);

        [LibraryImport(Lib)]
        private static partial int XCloseDisplay(IntPtr display);

        [LibraryImport(Lib)]
        private static partial IntPtr XDefaultRootWindow(IntPtr display);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr XInternAtom(IntPtr display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);

        [LibraryImport(Lib)]
        private static partial int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, long offset, long length,
            [MarshalAs(UnmanagedType.Bool)] bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
            out ulong itemCount, out ulong bytesAfter, out IntPtr data);

        [LibraryImport(Lib)]
        private static partial int XFree(IntPtr data);

        [LibraryImport(Lib)]
        private static partial int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y,
            out uint width, out uint height, out uint border, out uint depth);

        [LibraryImport(Lib)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool XTranslateCoordinates(IntPtr display, IntPtr src, IntPtr dest, int srcX, int srcY,
            out int destX, out int destY, out IntPtr child);

        public static List<CaptureTarget> List(long excludePid)
        {
            var result = new List<CaptureTarget>();
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
                return result;
            try
            {
                var root = XDefaultRootWindow(display);
                var clientList = XInternAtom(display, "_NET_CLIENT_LIST_STACKING", false);
                var windows = ReadWindows(display, root, clientList);
                if (windows.Count == 0)
                    windows = ReadWindows(display, root, XInternAtom(display, "_NET_CLIENT_LIST", false));
                windows.Reverse(); // topmost first
                var netWmName = XInternAtom(display, "_NET_WM_NAME", false);
                var utf8 = XInternAtom(display, "UTF8_STRING", false);
                var wmName = XInternAtom(display, "WM_NAME", false);
                var pidAtom = XInternAtom(display, "_NET_WM_PID", false);
                var stateAtom = XInternAtom(display, "_NET_WM_STATE", false);
                var hiddenAtom = XInternAtom(display, "_NET_WM_STATE_HIDDEN", false);
                foreach (var w in windows)
                {
                    if (excludePid != 0 && ReadCardinal(display, w, pidAtom) == excludePid)
                        continue;
                    if (ReadAtoms(display, w, stateAtom).Contains(hiddenAtom))
                        continue; // minimised
                    var title = ReadString(display, w, netWmName, utf8) ?? ReadString(display, w, wmName, IntPtr.Zero) ?? "";
                    if (title.Length == 0)
                        continue;
                    if (XGetGeometry(display, w, out _, out _, out _, out var width, out var height, out _, out _) == 0)
                        continue;
                    XTranslateCoordinates(display, w, root, 0, 0, out var x, out var y, out _);
                    if (width < 40 || height < 40)
                        continue;
                    result.Add(new CaptureTarget(CaptureTargetKind.Window, title, x, y, (int)width, (int)height, w.ToInt64()));
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
            return result;
        }

        private static List<IntPtr> ReadWindows(IntPtr display, IntPtr root, IntPtr property)
        {
            var list = new List<IntPtr>();
            if (XGetWindowProperty(display, root, property, 0, 4096, false, IntPtr.Zero, out _, out var format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
                return list;
            try
            {
                if (format == 32)
                    for (var i = 0; i < (int)count; i++)
                        list.Add(Marshal.ReadIntPtr(data, i * IntPtr.Size)); // format 32 items are longs
            }
            finally
            {
                XFree(data);
            }
            return list;
        }

        private static List<IntPtr> ReadAtoms(IntPtr display, IntPtr window, IntPtr property) => ReadWindows(display, window, property);

        private static long ReadCardinal(IntPtr display, IntPtr window, IntPtr property)
        {
            var values = ReadWindows(display, window, property);
            return values.Count > 0 ? values[0].ToInt64() : 0;
        }

        private static string? ReadString(IntPtr display, IntPtr window, IntPtr property, IntPtr type)
        {
            if (XGetWindowProperty(display, window, property, 0, 1024, false, type, out _, out var format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
                return null;
            try
            {
                return format == 8 && count > 0 ? Marshal.PtrToStringUTF8(data, (int)count) : null;
            }
            finally
            {
                XFree(data);
            }
        }
    }
}
