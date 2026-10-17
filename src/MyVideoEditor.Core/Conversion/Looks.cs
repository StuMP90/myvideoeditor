using System.Globalization;

namespace MyVideoEditor.Core.Conversion;

/// <summary>A finishing look for the whole picture, applied after any repair and resizing.</summary>
public enum Look
{
    None,
    BlackAndWhite,
    Sepia,
    /// <summary>Black and white, slightly soft, with flicker, grain and darker corners.</summary>
    OldFilm,
    /// <summary>As <see cref="OldFilm"/>, on a toned (sepia) print.</summary>
    OldFilmSepia,
}

public static class Looks
{
    public static IReadOnlyList<Look> All { get; } = Enum.GetValues<Look>();

    public static string Label(Look look) => look switch
    {
        Look.BlackAndWhite => "Black and white",
        Look.Sepia => "Sepia",
        Look.OldFilm => "Old film (black and white)",
        Look.OldFilmSepia => "Old film (sepia)",
        _ => "None",
    };

    /// <summary>For output file names: "-bw", "-sepia", "-oldfilm-bw", "-oldfilm-sepia"; empty for none.</summary>
    public static string Suffix(Look look) => look switch
    {
        Look.BlackAndWhite => "-bw",
        Look.Sepia => "-sepia",
        Look.OldFilm => "-oldfilm-bw",
        Look.OldFilmSepia => "-oldfilm-sepia",
        _ => "",
    };

    private const string SepiaMixer = "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131";

    /// <summary>
    /// The ffmpeg filters for <paramref name="look"/> on a picture <paramref name="height"/> pixels high
    /// (the softening scales with it). The flicker and grain are pseudo-random but repeat exactly, so
    /// the preview matches the conversion.
    /// </summary>
    public static IEnumerable<string> Filters(Look look, int height)
    {
        switch (look)
        {
            case Look.BlackAndWhite:
                yield return "hue=s=0";
                break;
            case Look.Sepia:
                yield return SepiaMixer;
                break;
            case Look.OldFilm or Look.OldFilmSepia:
                yield return look == Look.OldFilm ? "hue=s=0" : SepiaMixer;
                var sigma = 0.7 * height / 1080;
                if (sigma >= 0.25)
                    yield return $"gblur=sigma={sigma.ToString("0.##", CultureInfo.InvariantCulture)}";
                yield return "eq=contrast=1.15:brightness='0.04*(random(0)-0.5)':eval=frame";
                yield return "noise=c0s=12:c0f=t+u"; // brightness only, so the tint stays even
                yield return "vignette=a=PI/4.5";
                break;
        }
    }
}
