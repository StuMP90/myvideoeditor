using System.Globalization;

namespace MyVideoEditor.Core;

/// <summary>Formats and parses times shown to people. All times in the model are seconds (double).</summary>
public static class Timecode
{
    /// <summary>"1:02:03.45", "02:03.45": hours only when needed, hundredths of a second.</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            seconds = 0;
        var negative = seconds < 0;
        var hundredths = (long)Math.Round(Math.Abs(seconds) * 100);
        var h = hundredths / 360_000;
        var m = hundredths / 6000 % 60;
        var s = hundredths / 100 % 60;
        var cs = hundredths % 100;
        var text = h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}.{cs:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m:00}:{s:00}.{cs:00}");
        return negative ? "-" + text : text;
    }

    /// <summary>"00:01:23:12" (hours:minutes:seconds:frames) at the given frame rate.</summary>
    public static string FormatFrames(double seconds, double frameRate)
    {
        var fps = Math.Max(1, (int)Math.Round(frameRate));
        var frames = (long)Math.Round(Math.Max(0, seconds) * frameRate);
        var totalSeconds = (long)Math.Floor(frames / frameRate);
        var f = frames - (long)Math.Round(totalSeconds * frameRate);
        f = Math.Clamp(f, 0, fps - 1);
        return string.Create(CultureInfo.InvariantCulture,
            $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}:{f:00}");
    }

    /// <summary>Parses "83.5", "1:23.5", "1:01:23.5" or "00:01:23:12" (with frames, if a frame rate is given).</summary>
    public static bool TryParse(string? text, out double seconds, double frameRate = 0)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().Split(':');
        if (parts.Length > 4 || (parts.Length == 4 && frameRate <= 0))
            return false;
        double total = 0;
        var count = parts.Length == 4 ? 3 : parts.Length;
        for (var i = 0; i < count; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return false;
            total = total * 60 + v;
        }
        if (parts.Length == 4)
        {
            if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) || frames < 0)
                return false;
            total += frames / frameRate;
        }
        seconds = total;
        return true;
    }

    /// <summary>Rounds a time to the nearest frame boundary.</summary>
    public static double SnapToFrame(double seconds, double frameRate) =>
        frameRate > 0 ? Math.Round(seconds * frameRate) / frameRate : seconds;

    public static string FormatInvariant(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
