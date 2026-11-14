using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;
using Xunit.Abstractions;

namespace Lux.App.Tests;

/// <summary>The font set-up: every character the app's texts use is in a bundled face of the text font chain (Archivo, then Inter), so no
/// text depends on the fonts a machine happens to have; and a report (env-gated: LUX_FONT_DIAG=1) of the font manager in use, whether system
/// fonts can be enumerated, and which faces cover which characters.</summary>
public sealed class FontDiagnostics(ITestOutputHelper output)
{
    /// <summary>The non-ASCII characters in the app's and the library's user-facing strings (a scan of the sources).</summary>
    internal const string UsedChars = "·—…×–→“”’−★☆°←›‹≈↓↑≥±∞↔÷";
    internal const string AppChars = UsedChars + "✓⋯✕";

    [AvaloniaFact]
    public void Every_character_the_app_uses_is_in_a_bundled_face()
    {
        var token = Assert.IsType<FontFamily>(Application.Current!.FindResource("LuxTextFont"));
        Assert.Equal(new[] { "Archivo", "Inter" }, token.FamilyNames.ToArray());
        var chain = new[] { new FontFamily("avares://lux-app/Assets/Fonts#Archivo"), new FontFamily("fonts:Inter#Inter") };
        var missing = UsedChars.Where(c => !chain.Any(f => FontManager.Current.TryGetGlyphTypeface(new Typeface(f), out var gt) && gt.TryGetGlyph(c, out _)))
            .Select(c => $"U+{(int)c:X4} {c}").ToArray();
        Assert.Empty(missing);
    }

    [AvaloniaFact]
    public void Font_coverage_report()
    {
        if (Environment.GetEnvironmentVariable("LUX_FONT_DIAG") != "1") return;
        var fm = FontManager.Current;
        var impl = typeof(FontManager).GetMembers(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(m => m switch { System.Reflection.FieldInfo f => f.GetValue(fm), System.Reflection.PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(fm), _ => null })
            .FirstOrDefault(v => v is Avalonia.Platform.IFontManagerImpl);
        output.WriteLine($"font manager impl: {impl?.GetType().FullName}; default family {fm.DefaultFontFamily}");
        try { output.WriteLine($"system fonts: {fm.SystemFonts.Count()} families"); }
        catch (Exception e) { output.WriteLine($"system fonts: {e.GetType().Name}: {e.Message}"); }
        var faces = new (string Name, FontFamily Family)[]
        {
            ("Archivo", new FontFamily("avares://lux-app/Assets/Fonts#Archivo")),
            ("JetBrains Mono", new FontFamily("avares://lux-app/Assets/Fonts#JetBrains Mono")),
            ("Inter", new FontFamily("fonts:Inter#Inter")),
        };
        foreach (var c in AppChars)
        {
            var line = $"U+{(int)c:X4} {c}:";
            foreach (var (name, fam) in faces)
            {
                bool ok = fm.TryGetGlyphTypeface(new Typeface(fam), out var gt) && gt.TryGetGlyph(c, out _);
                line += $" {name}={(ok ? "yes" : "NO")}";
            }
            output.WriteLine(line);
        }
    }
}
