using SkiaSharp;

namespace MyVideoEditor.Core.Graphics;

/// <summary>Finds installed fonts. Projects name a family; if it isn't installed, a common sans-serif is used.</summary>
public static class FontResolver
{
    private static readonly string[] Fallbacks =
        ["Inter", "Segoe UI", "Helvetica Neue", "Arial", "Noto Sans", "DejaVu Sans", "Liberation Sans", "Ubuntu", "Cantarell"];

    private static readonly Lazy<IReadOnlyList<string>> Families = new(() =>
        SKFontManager.Default.FontFamilies.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList());

    private static readonly Lazy<string> DefaultFamilyName = new(() =>
        Fallbacks.FirstOrDefault(f => Families.Value.Contains(f, StringComparer.OrdinalIgnoreCase))
        ?? SKTypeface.Default.FamilyName);

    /// <summary>Installed font families, sorted.</summary>
    public static IReadOnlyList<string> InstalledFamilies => Families.Value;

    public static string DefaultFamily => DefaultFamilyName.Value;

    public static SKTypeface Resolve(string? family, bool bold, bool italic)
    {
        var style = new SKFontStyle(
            bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var name = string.IsNullOrWhiteSpace(family) ? DefaultFamily : family;
        return SKFontManager.Default.MatchFamily(name, style)
               ?? SKFontManager.Default.MatchFamily(DefaultFamily, style)
               ?? SKTypeface.Default;
    }

    /// <summary>A font that has the character (emoji, CJK), or null.</summary>
    public static SKTypeface? ForCharacter(int codepoint, SKTypeface preferred) =>
        SKFontManager.Default.MatchCharacter(preferred.FamilyName, preferred.FontStyle, null, codepoint);
}
