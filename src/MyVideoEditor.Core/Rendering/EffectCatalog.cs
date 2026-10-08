using System.Globalization;
using MyVideoEditor.Core.Model;

namespace MyVideoEditor.Core.Rendering;

public sealed record EffectParameter(
    string Name,
    string Label,
    double Min,
    double Max,
    double Default,
    double Step = 0.01,
    bool IsColor = false,
    string Unit = "");

/// <summary>Values an effect's filter may need besides its own settings.</summary>
public sealed record EffectContext(double PixelScale, int SampleRate);

/// <summary>An effect type: its settings, and how it turns into an ffmpeg filter.</summary>
public sealed record EffectDefinition(
    string Type,
    string Label,
    bool IsAudio,
    string Description,
    IReadOnlyList<EffectParameter> Parameters,
    Func<Effect, EffectContext, string> Filter,
    string? RequiredFilter = null)
{
    public Effect CreateDefault() => new()
    {
        Type = Type,
        Parameters = Parameters.ToDictionary(p => p.Name, p => p.Default),
    };

    public double Value(Effect effect, string name)
    {
        var p = Parameters.First(x => x.Name == name);
        return Math.Clamp(effect.Get(name, p.Default), p.Min, p.Max);
    }
}

/// <summary>
/// The video and audio effects clips can have. Each becomes one ffmpeg filter (chain), so the
/// preview and the export apply exactly the same processing.
/// </summary>
public static class EffectCatalog
{
    public static IReadOnlyList<EffectDefinition> All { get; } = Build();

    public static IEnumerable<EffectDefinition> Video => All.Where(e => !e.IsAudio);
    public static IEnumerable<EffectDefinition> Audio => All.Where(e => e.IsAudio);

    public static EffectDefinition? Find(string type) => All.FirstOrDefault(e => e.Type == type);

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    internal static string Hex(double packedRgb) => "0x" + ((int)packedRgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    private static List<EffectDefinition> Build()
    {
        var list = new List<EffectDefinition>();

        EffectDefinition V(string type, string label, string description, EffectParameter[] ps, Func<Effect, EffectContext, Func<string, double>, string> f, string? requires = null)
        {
            EffectDefinition? def = null;
            def = new EffectDefinition(type, label, false, description, ps, (e, c) => f(e, c, n => def!.Value(e, n)), requires);
            return def;
        }

        EffectDefinition A(string type, string label, string description, EffectParameter[] ps, Func<Effect, EffectContext, Func<string, double>, string> f, string? requires = null)
        {
            EffectDefinition? def = null;
            def = new EffectDefinition(type, label, true, description, ps, (e, c) => f(e, c, n => def!.Value(e, n)), requires);
            return def;
        }

        // ---- video ----
        list.Add(V("color", "Colour correction", "Brightness, contrast, saturation and gamma.",
            [
                new("brightness", "Brightness", -1, 1, 0),
                new("contrast", "Contrast", 0, 3, 1),
                new("saturation", "Saturation", 0, 3, 1),
                new("gamma", "Gamma", 0.1, 3, 1),
            ],
            (_, _, v) => $"eq=brightness={F(v("brightness"))}:contrast={F(v("contrast"))}:saturation={F(v("saturation"))}:gamma={F(v("gamma"))}"));
        list.Add(V("temperature", "Colour temperature", "Warmer (lower) or cooler (higher) colours.",
            [new("kelvin", "Temperature", 2000, 12000, 6500, 100, Unit: "K")],
            (_, _, v) => $"colortemperature=temperature={F(v("kelvin"))}", "colortemperature"));
        list.Add(V("grayscale", "Black and white", "Removes colour.", [],
            (_, _, _) => "hue=s=0"));
        list.Add(V("sepia", "Sepia", "Old-photo brown tint.", [],
            (_, _, _) => "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131"));
        list.Add(V("invert", "Invert colours", "Photographic negative.", [],
            (_, _, _) => "negate"));
        list.Add(V("blur", "Blur", "Softens the whole picture.",
            [new("radius", "Radius", 0.5, 50, 6, 0.5, Unit: "px")],
            (_, c, v) => $"gblur=sigma={F(Math.Max(0.1, v("radius") * c.PixelScale))}"));
        list.Add(V("sharpen", "Sharpen", "Crisper edges (useful after upscaling).",
            [new("amount", "Amount", 0, 3, 1)],
            (_, _, v) => $"unsharp=5:5:{F(v("amount"))}:5:5:0"));
        list.Add(V("vignette", "Vignette", "Darkens the corners.",
            [new("strength", "Strength", 0.1, 1.5, 0.6)],
            (_, _, v) => $"vignette=a={F(v("strength"))}"));
        list.Add(V("denoise", "Reduce noise", "Smooths grain in low-light footage.",
            [new("strength", "Strength", 1, 20, 4, 0.5)],
            (_, _, v) => $"hqdn3d={F(v("strength"))}"));
        list.Add(V("stabilize", "Stabilise", "Reduces camera shake (single pass).", [],
            (_, _, _) => "deshake", "deshake"));
        list.Add(V("hflip", "Mirror", "Flips left to right.", [],
            (_, _, _) => "hflip"));
        list.Add(V("vflip", "Flip vertically", "Flips upside down.", [],
            (_, _, _) => "vflip"));
        list.Add(V("chromakey", "Green screen", "Makes a colour transparent so the tracks below show through.",
            [
                new("color", "Key colour", 0, 0xFFFFFF, 0x00FF00, 1, IsColor: true),
                new("similarity", "Similarity", 0.01, 1, 0.3),
                new("blend", "Edge softness", 0, 1, 0.1),
            ],
            (_, _, v) => $"format=yuva420p,chromakey={Hex(v("color"))}:{F(v("similarity"))}:{F(v("blend"))}"));

        // ---- audio ----
        list.Add(A("normalize", "Normalise loudness", "Evens out loudness to a standard level (−16 LUFS).", [],
            (_, _, _) => "loudnorm=I=-16:TP=-1.5:LRA=11"));
        list.Add(A("denoise_audio", "Reduce background noise", "Removes hiss and hum.",
            [new("amount", "Amount", 1, 40, 12, 1, Unit: "dB")],
            (_, _, v) => $"afftdn=nr={F(v("amount"))}"));
        list.Add(A("highpass", "Cut low rumble", "Removes sound below a frequency (wind, desk bumps).",
            [new("frequency", "Below", 20, 1000, 80, 5, Unit: "Hz")],
            (_, _, v) => $"highpass=f={F(v("frequency"))}"));
        list.Add(A("lowpass", "Cut high hiss", "Removes sound above a frequency.",
            [new("frequency", "Above", 1000, 20000, 12000, 100, Unit: "Hz")],
            (_, _, v) => $"lowpass=f={F(v("frequency"))}"));
        list.Add(A("bass", "Bass", "Boost or cut low frequencies.",
            [new("gain", "Gain", -20, 20, 6, 0.5, Unit: "dB")],
            (_, _, v) => $"bass=g={F(v("gain"))}"));
        list.Add(A("treble", "Treble", "Boost or cut high frequencies.",
            [new("gain", "Gain", -20, 20, 4, 0.5, Unit: "dB")],
            (_, _, v) => $"treble=g={F(v("gain"))}"));
        list.Add(A("compressor", "Compressor", "Evens out loud and quiet parts (voice-overs).",
            [
                new("threshold", "Threshold", -60, 0, -20, 1, Unit: "dB"),
                new("ratio", "Ratio", 1, 20, 4, 0.5),
            ],
            (_, _, v) => $"acompressor=threshold={F(Math.Pow(10, v("threshold") / 20))}:ratio={F(v("ratio"))}"));
        list.Add(A("echo", "Echo", "Adds an echo.",
            [
                new("delay", "Delay", 20, 2000, 300, 10, Unit: "ms"),
                new("decay", "Decay", 0.05, 0.9, 0.4),
            ],
            (_, _, v) => $"aecho=0.8:0.88:{F(v("delay"))}:{F(v("decay"))}"));

        return list;
    }
}
