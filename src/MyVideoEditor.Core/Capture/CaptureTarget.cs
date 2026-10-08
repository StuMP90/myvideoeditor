namespace MyVideoEditor.Core.Capture;

public enum CaptureTargetKind
{
    /// <summary>A whole monitor.</summary>
    Screen,
    /// <summary>One application window (follows it on Linux/X11; on Windows, the window's area on screen).</summary>
    Window,
    /// <summary>A rectangle of the desktop.</summary>
    Region,
}

/// <summary>What to record. Coordinates are physical desktop pixels.</summary>
public sealed record CaptureTarget(CaptureTargetKind Kind, string Title, int X, int Y, int Width, int Height, long WindowHandle = 0)
{
    public string SizeText => $"{Width}×{Height}";

    public string KindLabel => Kind switch
    {
        CaptureTargetKind.Screen => "Screen",
        CaptureTargetKind.Window => "Window",
        _ => "Region",
    };

    public override string ToString() => Kind switch
    {
        CaptureTargetKind.Window => $"{Title} ({Width}×{Height})",
        CaptureTargetKind.Screen => $"{Title} ({Width}×{Height})",
        _ => $"Region {Width}×{Height} at {X},{Y}",
    };
}

public enum AudioCapture
{
    None,
    Microphone,
    /// <summary>What the computer is playing (Linux: the default output's monitor; Windows: a loopback device such as Stereo Mix).</summary>
    SystemAudio,
    Both,
}

public sealed record AudioDevice(string Id, string Name, bool IsMonitor);
