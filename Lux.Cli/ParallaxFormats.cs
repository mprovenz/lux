using System.Globalization;
using Lux.Engine.Pipeline.Isp;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Cli;

/// <summary>The `parallax-*` formats of `convert` (<see cref="ParallaxFormat"/>, <see cref="ParallaxRequest"/> and <see cref="LensRequest"/> are
/// engine data types, <c>Lux.Engine.Session</c>). All EXPERIMENTAL: none of these is held to the 1:1 bar.</summary>
public static class ParallaxFormats
{
    public static readonly ParallaxFormat[] All =
    {
        ParallaxFormat.Wiggle, ParallaxFormat.WiggleInterp, ParallaxFormat.Orbit, ParallaxFormat.Single, ParallaxFormat.Rack,
        ParallaxFormat.Dolly, ParallaxFormat.Dof, ParallaxFormat.Anaglyph, ParallaxFormat.CrossEye, ParallaxFormat.Sbs, ParallaxFormat.Still,
    };

    public static string Name(ParallaxFormat f) => ExportFormats.NameOf(f);

    public static bool TryParse(string name, out ParallaxFormat f)
    {
        foreach (var x in All) if (Name(x) == name) { f = x; return true; }
        f = default; return false;
    }

    public static bool IsAnimated(ParallaxFormat f) =>
        f is ParallaxFormat.Wiggle or ParallaxFormat.WiggleInterp or ParallaxFormat.Orbit or ParallaxFormat.Single or ParallaxFormat.Rack or ParallaxFormat.Dolly;

    /// <summary>Every format but the physical wigglegram synthesises from the metric depth, and so needs the level-0 build.</summary>
    public static bool NeedsDepth(ParallaxFormat f) => ParallaxRequest.NeedsDepthFormat(f);

    /// <summary>The formats whose disocclusions can be filled from the other real modules.</summary>
    public static bool CanUseDonors(ParallaxFormat f) =>
        f is ParallaxFormat.WiggleInterp or ParallaxFormat.Orbit or ParallaxFormat.Anaglyph or ParallaxFormat.CrossEye or ParallaxFormat.Sbs or ParallaxFormat.Still;

    public static string Extension(ParallaxFormat f, string container) => IsAnimated(f) ? "." + container : ".png";

    /// <summary>Per-frame duration defaults: the physical wigglegram has four unique frames and needs 100 ms; the
    /// 24-frame interpolations play at 60 ms, the focus/zoom pulls at 70.</summary>
    public static int DefaultMs(ParallaxFormat f) => f == ParallaxFormat.Wiggle ? 100 : f is ParallaxFormat.Rack or ParallaxFormat.Dolly ? 70 : 60;

    /// <summary>Interocular defaults: 63 mm (human) for the side-by-side pairs; 25 mm for the anaglyph, because at 63 mm
    /// with a near foreground the on-screen disparity reaches 7.5 % of the width — geometrically right and hard to view.</summary>
    public static double DefaultIpd(ParallaxFormat f) => f == ParallaxFormat.Anaglyph ? 25 : 63;
}
