using System.Collections.Concurrent;
using MyVideoEditor.Core.Model;
using SkiaSharp;

namespace MyVideoEditor.Core.Graphics;

/// <summary>A moment in a title's animation: which motion, and how much of the title is shown (0 none, 1 all).</summary>
public readonly record struct TitleFrame(TitleMotion Motion, double Shown)
{
    public static TitleFrame Still => new(TitleMotion.None, 1);

    public bool IsStill => Motion == TitleMotion.None || Shown >= 1;

    /// <summary>
    /// The state <paramref name="time"/> seconds into the clip: coming in at the start, going out at
    /// the end, still in between. In a clip too short for both, they share its length.
    /// </summary>
    public static TitleFrame At(TitleClip title, double time)
    {
        var length = Math.Max(0, title.Duration);
        var inLength = title.AnimationIn != TitleMotion.None ? Math.Max(0, title.AnimationInDuration) : 0;
        var outLength = title.AnimationOut != TitleMotion.None ? Math.Max(0, title.AnimationOutDuration) : 0;
        if (inLength + outLength > length && inLength + outLength > 0)
        {
            var share = length / (inLength + outLength);
            inLength *= share;
            outLength *= share;
        }
        if (inLength > 0.01 && time < inLength)
            return new TitleFrame(title.AnimationIn, Math.Clamp(time / inLength, 0, 1));
        if (outLength > 0.01 && time > length - outLength)
            return new TitleFrame(title.AnimationOut, Math.Clamp((length - time) / outLength, 0, 1));
        return Still;
    }
}

/// <summary>Pictures for titles, decoded once and kept while their file is unchanged.</summary>
internal static class TitleImages
{
    private static readonly ConcurrentDictionary<string, (string Stamp, SKImage? Image)> Cache = new();

    /// <summary>A file's identity for caching: its path, size and modification time ("" if missing).</summary>
    public static string Stamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }

    /// <summary>The decoded picture, or null if there's none or it can't be read.</summary>
    public static SKImage? Get(string? path)
    {
        var stamp = Stamp(path);
        if (stamp.Length == 0)
            return null;
        if (Cache.TryGetValue(path!, out var cached) && cached.Stamp == stamp)
            return cached.Image;
        SKImage? image = null;
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            image = bitmap == null ? null : SKImage.FromBitmap(bitmap);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn("graphics", $"Couldn't read the title picture {path}.", ex);
        }
        Cache[path!] = (stamp, image);
        return image;
    }
}
