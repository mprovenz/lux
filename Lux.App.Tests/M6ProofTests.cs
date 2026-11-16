using System.Text.Json;
using Lux.Engine.Pipeline.Export;
using SkiaSharp;
using Xunit;

namespace Lux.App.Tests;

/// <summary>
/// Animation check (env-gated: LUX_M6_PROOF = a folder of pairs written by a workspace script): each depth-effect animation frame
/// (an exact PNG kept by `lux-light animate --anim-keep-frames`, decoded here by Skia) is the still `lux-light convert --f-number … --focus-depth
/// … --formats jpg` wrote in a separate process: the frame's pixels encoded by the export's JPEG coder (quality 98, 4:2:0) give the convert
/// JPG's quantization tables and entropy-coded scan byte for byte. Writes the table to &lt;folder&gt;/result.tsv.
/// </summary>
public class M6ProofTests
{
    [Fact]
    public void Depth_effect_frames_are_the_convert_stills()
    {
        var dir = Environment.GetEnvironmentVariable("LUX_M6_PROOF");
        if (string.IsNullOrEmpty(dir)) return;
        var lines = new List<string> { "frame\tconvert jpg\tscan bytes\tsame tables\tsame scan" };
        int bad = 0;
        foreach (var pair in File.ReadAllLines(Path.Combine(dir, "pairs.tsv")).Where(l => l.Length > 0))
        {
            var parts = pair.Split('\t');
            string png = parts[0], jpg = parts[1];
            using var bmp = SKBitmap.Decode(png) ?? throw new InvalidOperationException("cannot read " + png);
            using var rgba = bmp.Copy(SKColorType.Rgba8888);
            var px = rgba.Bytes;
            using var ms = new MemoryStream();
            JpegEncoder.Encode(ms, px, rgba.Width, rgba.Height, rgba.Width * 4, grayscale: false, new JpegEncoder { Quality = 98, SubsamplingId = 2 });
            var mine = ms.ToArray(); var theirs = File.ReadAllBytes(jpg);
            var (mt, msn) = Split(mine); var (tt, tsn) = Split(theirs);
            bool tables = mt.SequenceEqual(tt), scan = msn.SequenceEqual(tsn);
            if (!tables || !scan) bad++;
            lines.Add($"{Path.GetFileName(png)}\t{Path.GetFileName(jpg)}\t{tsn.Length}\t{tables}\t{scan}");
        }
        File.WriteAllLines(Path.Combine(dir, "result.tsv"), lines);
        Assert.Equal(0, bad);
    }

    /// <summary>The DQT + DHT + SOF segments (concatenated) and the scan (from the SOS marker to the end of the file).</summary>
    static (byte[] Tables, byte[] Scan) Split(byte[] j)
    {
        var tables = new List<byte>();
        int i = 2;
        while (i + 4 <= j.Length)
        {
            if (j[i] != 0xFF) throw new InvalidDataException("not a marker at " + i);
            byte m = j[i + 1];
            int len = (j[i + 2] << 8) | j[i + 3];
            if (m == 0xDA) return (tables.ToArray(), j[i..]);
            if (m is 0xDB or 0xC4 or 0xC0 or 0xC2) tables.AddRange(j.AsSpan(i, len + 2).ToArray());
            i += 2 + len;
        }
        throw new InvalidDataException("no scan");
    }
}
