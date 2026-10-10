using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MyVideoEditor.Core.Diagnostics;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace MyVideoEditor.Core.Media;

/// <summary>
/// Lottie animations (Bodymovin JSON, or a .lottie archive holding one) that the user imports.
/// ffmpeg can't read them, so Skottie draws their frames into transparent PNGs (cached), listed in
/// an ffconcat file for ffmpeg. Text layers can be given new text per clip. No animations are
/// bundled with the app: they come from the user.
/// </summary>
public static class LottieMedia
{
    private static readonly string CacheDirectory = Path.Combine(AppPaths.CacheDirectory, "lottie");

    /// <summary>A .lottie file, or a .json file that is a Lottie animation.</summary>
    public static bool IsLottie(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lottie")
            return true;
        if (ext != ".json")
            return false;
        try
        {
            // A Lottie document has a version, frame rate, in/out points and layers.
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject o
                   && o.ContainsKey("v") && o.ContainsKey("fr") && o.ContainsKey("op") && o["layers"] is JsonArray;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The animation's JSON: the file itself, or the first animation inside a .lottie archive.</summary>
    public static string ReadJson(string path)
    {
        if (!Path.GetExtension(path).Equals(".lottie", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path);
        using var zip = ZipFile.OpenRead(path);
        string? id = null;
        if (zip.GetEntry("manifest.json") is { } manifest)
        {
            using var reader = new StreamReader(manifest.Open());
            id = (JsonNode.Parse(reader.ReadToEnd())?["animations"] as JsonArray)?.FirstOrDefault()?["id"]?.GetValue<string>();
        }
        var entry = (id != null ? zip.GetEntry($"animations/{id}.json") : null)
                    ?? zip.Entries.Where(e => e.FullName.StartsWith("animations/", StringComparison.Ordinal) && e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.FullName, StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidDataException("This .lottie file has no animation in it.");
        using var stream = new StreamReader(entry.Open());
        return stream.ReadToEnd();
    }

    /// <summary>What the media bin and timeline need to know about an animation.</summary>
    public static MediaInfo Probe(string path)
    {
        var json = ReadJson(path);
        using var animation = Animation.Parse(json) ?? throw new InvalidDataException($"{Path.GetFileName(path)} isn't a Lottie animation Skottie can play.");
        return new MediaInfo
        {
            Path = path,
            Kind = MediaKind.Animation,
            FormatName = "lottie",
            Duration = animation.Duration.TotalSeconds,
            SizeBytes = new FileInfo(path).Length,
            HasVideo = true,
            Width = Math.Max(2, (int)Math.Round(animation.Size.Width) & ~1),
            Height = Math.Max(2, (int)Math.Round(animation.Size.Height) & ~1),
            FrameRate = animation.Fps,
            FrameRateText = animation.Fps.ToString("0.###", CultureInfo.InvariantCulture),
            VideoCodec = "lottie",
        };
    }

    /// <summary>The text layers (by name) and their text, in the order they appear.</summary>
    public static IReadOnlyList<(string Name, string Text)> TextLayers(string json)
    {
        var found = new List<(string, string)>();
        foreach (var layer in AllLayers(JsonNode.Parse(json)))
        {
            if (layer["ty"]?.GetValue<int>() != 5 || layer["nm"]?.GetValue<string>() is not { } name)
                continue;
            var text = (layer["t"]?["d"]?["k"] as JsonArray)?.FirstOrDefault()?["s"]?["t"]?.GetValue<string>() ?? "";
            if (!found.Any(f => f.Item1 == name))
                found.Add((name, text.Replace('\r', '\n')));
        }
        return found;
    }

    /// <summary>
    /// Whether the animation uses pictures (image layers or image assets, embedded or in the archive).
    /// They aren't shown: SkiaSharp 2.88's Skottie has no way to load them.
    /// </summary>
    public static bool HasPictures(string json)
    {
        var root = JsonNode.Parse(json);
        var imageAssets = (root?["assets"] as JsonArray)?.OfType<JsonObject>().Any(a => a.ContainsKey("p") && !a.ContainsKey("layers")) == true;
        return imageAssets || AllLayers(root).Any(l => l["ty"]?.GetValue<int>() == 2);
    }

    /// <summary>The JSON with new text for the named text layers (every keyframe of their text).</summary>
    public static string WithText(string json, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.Count == 0)
            return json;
        var root = JsonNode.Parse(json);
        foreach (var layer in AllLayers(root))
        {
            if (layer["ty"]?.GetValue<int>() != 5 || layer["nm"]?.GetValue<string>() is not { } name || !overrides.TryGetValue(name, out var text))
                continue;
            if (layer["t"]?["d"]?["k"] is JsonArray keys)
                foreach (var key in keys)
                    if (key?["s"] is JsonObject style)
                        style["t"] = text.Replace("\r\n", "\r").Replace('\n', '\r'); // Lottie breaks lines with \r
        }
        return root!.ToJsonString();
    }

    private static IEnumerable<JsonObject> AllLayers(JsonNode? root)
    {
        var lists = new List<JsonArray?> { root?["layers"] as JsonArray };
        if (root?["assets"] is JsonArray assets)
            lists.AddRange(assets.Select(a => a?["layers"] as JsonArray));
        foreach (var list in lists.OfType<JsonArray>())
            foreach (var layer in list.OfType<JsonObject>())
                yield return layer;
    }

    /// <summary>
    /// Draws frames at animation times <paramref name="from"/>, + <paramref name="step"/>, … (<paramref name="count"/> of them;
    /// past the end, the last frame is held) at the animation's own size, and returns an ffconcat list
    /// giving each <paramref name="step"/> seconds.
    /// </summary>
    public static string RenderSequence(string path, IReadOnlyDictionary<string, string> textOverrides, double from, double step, int count)
    {
        var json = WithText(ReadJson(path), textOverrides);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..24].ToLowerInvariant();
        Directory.CreateDirectory(CacheDirectory);
        Animation? animation = null;
        try
        {
            var files = new List<string>();
            for (var i = 0; i < Math.Max(1, count); i++)
            {
                var time = from + i * step;
                var frameIndex = (int)Math.Round(Math.Clamp(time, 0, double.MaxValue) * 1000); // millisecond frames are plenty
                var file = Path.Combine(CacheDirectory, $"{key}-{frameIndex:0000000}.png");
                if (!File.Exists(file))
                {
                    animation ??= Animation.Parse(json) ?? throw new InvalidDataException($"{Path.GetFileName(path)} couldn't be played.");
                    var last = Math.Max(0, animation.Duration.TotalSeconds - 1 / Math.Max(1, animation.Fps));
                    SavePng(animation, Math.Min(frameIndex / 1000.0, last), file);
                }
                files.Add(file);
            }
            var list = new StringBuilder("ffconcat version 1.0\n");
            foreach (var file in files)
                list.Append(CultureInfo.InvariantCulture, $"file '{file.Replace("'", "'\\''")}'\nduration {step:0.######}\n");
            list.Append(CultureInfo.InvariantCulture, $"file '{files[^1].Replace("'", "'\\''")}'\n");
            var listPath = Path.Combine(CacheDirectory, $"{key}-{Guid.NewGuid():N}.ffconcat");
            File.WriteAllText(listPath, list.ToString());
            return listPath;
        }
        finally
        {
            animation?.Dispose();
        }
    }

    /// <summary>Small BGRA thumbnails every <paramref name="interval"/> seconds (for the timeline).</summary>
    public static byte[] Thumbnails(string path, int width, int height, double interval, double duration)
    {
        using var animation = Animation.Parse(ReadJson(path)) ?? throw new InvalidDataException("Not a playable Lottie animation.");
        var count = Math.Max(1, (int)Math.Ceiling(duration / interval));
        var data = new byte[count * width * height * 4];
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        for (var i = 0; i < count; i++)
        {
            canvas.Clear(new SKColor(40, 44, 52));
            animation.SeekFrameTime(Math.Min(i * interval, Math.Max(0, duration - 0.01)));
            animation.Render(canvas, new SKRect(0, 0, width, height));
            canvas.Flush();
            bitmap.GetPixelSpan().CopyTo(data.AsSpan(i * width * height * 4));
        }
        return data;
    }

    private static void SavePng(Animation animation, double time, string path)
    {
        var width = Math.Max(2, (int)Math.Round(animation.Size.Width) & ~1);
        var height = Math.Max(2, (int)Math.Round(animation.Size.Height) & ~1);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        animation.SeekFrameTime(time);
        animation.Render(surface.Canvas, new SKRect(0, 0, width, height));
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        using (var file = File.Create(temp))
            data.SaveTo(file);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn("lottie", $"Couldn't cache a frame ({path}).", ex);
        }
    }
}
