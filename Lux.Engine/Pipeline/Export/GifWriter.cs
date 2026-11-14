using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// Lux's GIF89a writer (no external tool): palette reduction (one global palette by default, or one per frame), optional dithering
/// (ordered Bayer 8×8 by default, or Floyd–Steinberg error diffusion), frame differencing (only the changed rectangle of each frame is
/// stored, with its unchanged pixels transparent and disposal "leave in place"), LZW, delays and the loop count.
///
/// <para>The palette: median cut over a 6-bit-per-channel histogram of every frame (or of the one frame), the box chosen to split being the
/// one with the largest squared error, then a few rounds of k-means on the histogram. The ordered dither is the default because its pattern
/// is fixed to the pixel grid: a still part of the scene quantises to the same indices in every frame, so it does not crawl and the frame
/// differencing finds it unchanged.</para>
/// </summary>
public static class GifWriter
{
    const int Bits = 6, Cells = 1 << (3 * Bits);

    internal static AnimationEncodeResult Write(Stream s, IReadOnlyList<Rgba> frames, IReadOnlyList<int> delaysMs, AnimationEncodeOptions o, bool keep)
    {
        int w = frames[0].W, h = frames[0].H;
        if (w > 65535 || h > 65535) throw new ArgumentException("GIF frames are at most 65535 pixels on a side");
        int colours = Math.Clamp(o.GifColours, 2, 256);
        bool diff = o.FrameDifferencing && frames.Count > 1;
        int real = diff ? Math.Min(colours, 255) : colours;    // one index kept for "unchanged" (transparent)
        var cs = AnimationEncoder.Centiseconds(delaysMs);

        Palette? global = o.GifPalette == GifPalette.Global ? Palette.Build(frames, real) : null;
        var bw = new BufferedStream(s, 1 << 16);
        // ---- header, logical screen, global colour table, loop
        bw.Write("GIF89a"u8);
        WriteU16(bw, w); WriteU16(bw, h);
        int gctBits = global is null ? 0 : TableBits(global.Count + (diff ? 1 : 0));
        bw.WriteByte((byte)(global is null ? 0x70 : 0x80 | 0x70 | (gctBits - 1)));   // GCT flag, colour resolution 8 bits, GCT size
        bw.WriteByte(0);   // background colour index
        bw.WriteByte(0);   // pixel aspect ratio: none
        if (global is not null) WriteTable(bw, global, gctBits);
        if (o.Loops != 1)
        {   // NETSCAPE2.0: 0 = forever; n > 1 plays = n − 1 repeats after the first (no block = play once)
            bw.Write([0x21, 0xFF, 0x0B]); bw.Write("NETSCAPE2.0"u8); bw.Write([0x03, 0x01]);
            WriteU16(bw, o.Loops == 0 ? 0 : Math.Min(65535, o.Loops - 1)); bw.WriteByte(0);
        }

        var shown = new uint[(long)w * h];          // what a decoder displays after the frames so far (0xRRGGBB; bit 24 = never drawn)
        Array.Fill(shown, 0x1000000u);
        var displayed = keep ? new List<Rgba>(frames.Count) : null;
        var q = new uint[(long)w * h];               // this frame's colours after reduction
        var qi = new byte[(long)w * h];              // … and palette indices
        var lzw = new Lzw();
        for (int f = 0; f < frames.Count; f++)
        {
            o.Cancel.ThrowIfCancellationRequested();
            var pal = global ?? Palette.Build(new[] { frames[f] }, real);
            Reduce(frames[f], pal, o, q, qi);
            // the rectangle to store: the whole frame first, then what changed
            RectI rect = new(0, 0, w, h);
            bool transparent = false;
            if (diff && f > 0)
            {
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++)
                {
                    long r = (long)y * w;
                    for (int x = 0; x < w; x++)
                        if (q[r + x] != shown[r + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; y1 = y; }
                }
                rect = x1 < 0 ? new RectI(0, 0, 1, 1) : new RectI(x0, y0, x1 + 1, y1 + 1);
                transparent = true;
            }
            int tIndex = pal.Count;   // the "unchanged" index sits right after the real colours
            int tableBits = global is null ? TableBits(pal.Count + (diff ? 1 : 0)) : gctBits;
            // ---- the rectangle's indices, twice for a later frame: unchanged pixels transparent, or every pixel as it is — the smaller wins
            //      (transparency pays when little changes; when most of the frame changes it only breaks the runs LZW codes)
            byte[] Indices(bool withTransparency)
            {
                var idx = new byte[(long)rect.Width * rect.Height];
                long k = 0;
                for (int y = rect.Y0; y < rect.Y1; y++)
                {
                    long r = (long)y * w;
                    for (int x = rect.X0; x < rect.X1; x++, k++)
                    {
                        long p = r + x;
                        idx[k] = withTransparency && q[p] == shown[p] ? (byte)tIndex : qi[p];
                    }
                }
                return idx;
            }
            byte[] Coded(byte[] idx) { var ms = new MemoryStream(); lzw.Encode(ms, idx, Math.Max(2, tableBits)); return ms.ToArray(); }
            byte[] data = Coded(Indices(false));
            if (transparent)
            {
                var alt = Coded(Indices(true));
                if (alt.Length < data.Length) data = alt; else transparent = false;
            }
            // ---- graphic control extension: disposal 1 (leave in place), transparency, delay
            bw.Write([0x21, 0xF9, 0x04]);
            bw.WriteByte((byte)((1 << 2) | (transparent ? 1 : 0)));
            WriteU16(bw, cs[f]);
            bw.WriteByte((byte)(transparent ? tIndex : 0));
            bw.WriteByte(0);
            // ---- image descriptor (+ local table), the coded indices
            bw.WriteByte(0x2C);
            WriteU16(bw, rect.X0); WriteU16(bw, rect.Y0); WriteU16(bw, rect.Width); WriteU16(bw, rect.Height);
            if (global is null) { bw.WriteByte((byte)(0x80 | (tableBits - 1))); WriteTable(bw, pal, tableBits); }
            else bw.WriteByte(0);
            bw.Write(data);
            // what a viewer now shows: the rectangle's pixels replaced (a transparent index leaves the same colour in place)
            for (int y = rect.Y0; y < rect.Y1; y++) Array.Copy(q, (long)y * w + rect.X0, shown, (long)y * w + rect.X0, rect.Width);
            if (displayed is not null)
            {
                var img = new Rgba(w, h);
                for (long p = 0; p < shown.LongLength; p++)
                {
                    uint c = shown[p];
                    img.P[p * 4] = (byte)(c >> 16); img.P[p * 4 + 1] = (byte)(c >> 8); img.P[p * 4 + 2] = (byte)c; img.P[p * 4 + 3] = 255;
                }
                displayed.Add(img);
            }
            o.Progress?.Invoke(f + 1, frames.Count);
        }
        bw.WriteByte(0x3B);
        bw.Flush();
        return new AnimationEncodeResult(0, frames.Count, 0, displayed) { WrittenDelaysMs = cs.Select(c => c * 10).ToArray() };
    }

    static int TableBits(int n) { int b = 1; while ((1 << b) < n) b++; return b; }

    static void WriteTable(Stream s, Palette p, int bits)
    {
        var t = new byte[3 << bits];
        for (int i = 0; i < p.Count; i++) { t[i * 3] = p.R[i]; t[i * 3 + 1] = p.G[i]; t[i * 3 + 2] = p.B[i]; }
        s.Write(t);
    }

    static void WriteU16(Stream s, int v) { s.WriteByte((byte)v); s.WriteByte((byte)(v >> 8)); }

    // ---------------------------------------------------------------------------------------------------- reduction

    /// <summary>Bayer 8×8 thresholds, 0–63.</summary>
    static readonly byte[] Bayer8 =
    {
         0, 32,  8, 40,  2, 34, 10, 42, 48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38, 60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41, 51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37, 63, 31, 55, 23, 61, 29, 53, 21,
    };

    /// <summary>Map a frame to the palette: <paramref name="q"/> = the colour shown (0xRRGGBB), <paramref name="qi"/> = its index.</summary>
    static void Reduce(Rgba img, Palette pal, AnimationEncodeOptions o, uint[] q, byte[] qi)
    {
        int w = img.W, h = img.H; var P = img.P;
        switch (o.GifDither)
        {
            case GifDither.None:
            case GifDither.Ordered:
            {
                var off = new int[64];
                double strength = o.GifDither == GifDither.Ordered ? o.GifDitherStrength : 0;
                for (int i = 0; i < 64; i++) off[i] = (int)Math.Round(((Bayer8[i] + 0.5) / 64.0 - 0.5) * strength);
                Parallel.For(0, h, y =>
                {
                    long r = (long)y * w;
                    for (int x = 0; x < w; x++)
                    {
                        long p = (r + x) * 4; int d = off[((y & 7) << 3) | (x & 7)];
                        int idx = pal.Nearest(Clamp(P[p] + d), Clamp(P[p + 1] + d), Clamp(P[p + 2] + d));
                        qi[r + x] = (byte)idx; q[r + x] = pal.Rgb(idx);
                    }
                });
                break;
            }
            case GifDither.Diffusion:
            {
                // Floyd–Steinberg, serpentine, errors carried in floats on two rows
                var cur = new float[(w + 2) * 3]; var nxt = new float[(w + 2) * 3];
                for (int y = 0; y < h; y++)
                {
                    Array.Clear(nxt);
                    bool ltr = (y & 1) == 0;
                    for (int i = 0; i < w; i++)
                    {
                        int x = ltr ? i : w - 1 - i, dir = ltr ? 1 : -1;
                        long p = ((long)y * w + x) * 4; int e = (x + 1) * 3;
                        float r = P[p] + cur[e], g = P[p + 1] + cur[e + 1], b = P[p + 2] + cur[e + 2];
                        int idx = pal.Nearest(Clamp((int)MathF.Round(r)), Clamp((int)MathF.Round(g)), Clamp((int)MathF.Round(b)));
                        long o2 = (long)y * w + x; qi[o2] = (byte)idx; q[o2] = pal.Rgb(idx);
                        float er = r - pal.R[idx], eg = g - pal.G[idx], eb = b - pal.B[idx];
                        void Add(float[] a, int xx, float k) { int j = (xx + 1) * 3; a[j] += er * k; a[j + 1] += eg * k; a[j + 2] += eb * k; }
                        if (x + dir >= 0 && x + dir < w) Add(cur, x + dir, 7f / 16);
                        if (x - dir >= 0 && x - dir < w) Add(nxt, x - dir, 3f / 16);
                        Add(nxt, x, 5f / 16);
                        if (x + dir >= 0 && x + dir < w) Add(nxt, x + dir, 1f / 16);
                    }
                    (cur, nxt) = (nxt, cur);
                }
                break;
            }
        }
    }

    static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>A palette and its nearest-colour lookup (cached per 6-bit cell).</summary>
    sealed class Palette
    {
        public readonly byte[] R, G, B;
        public int Count => R.Length;
        readonly int[] _cache = new int[Cells];

        Palette(byte[] r, byte[] g, byte[] b) { R = r; G = g; B = b; Array.Fill(_cache, -1); }

        public uint Rgb(int i) => ((uint)R[i] << 16) | ((uint)G[i] << 8) | B[i];

        public int Nearest(int r, int g, int b)
        {
            int key = ((r >> (8 - Bits)) << (2 * Bits)) | ((g >> (8 - Bits)) << Bits) | (b >> (8 - Bits));
            int c = _cache[key];
            if (c >= 0) return c;
            // the cell's centre
            int half = 1 << (7 - Bits);
            c = Search((r & ~(2 * half - 1)) + half, (g & ~(2 * half - 1)) + half, (b & ~(2 * half - 1)) + half);
            _cache[key] = c;
            return c;
        }

        int Search(int r, int g, int b)
        {
            int best = 0; long bd = long.MaxValue;
            for (int i = 0; i < R.Length; i++)
            {
                long d = Dist(r - R[i], g - G[i], b - B[i]);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        static long Dist(int dr, int dg, int db) => 3L * dr * dr + 4L * dg * dg + 2L * db * db;

        /// <summary>Median cut over the 6-bit histogram of the frames, then k-means on the histogram.</summary>
        public static Palette Build(IReadOnlyList<Rgba> frames, int n)
        {
            var count = new long[Cells]; var sr = new long[Cells]; var sg = new long[Cells]; var sb = new long[Cells];
            // every pixel up to ~16 M samples in all, then a regular grid of them (the palette does not need more)
            long samples = frames.Sum(f => (long)f.W * f.H);
            int step = samples <= 16_000_000 ? 1 : (int)Math.Ceiling(Math.Sqrt(samples / 16_000_000.0));
            foreach (var f in frames)
                for (int y = 0; y < f.H; y += step)
                {
                    long r0 = (long)y * f.W * 4;
                    for (int x = 0; x < f.W; x += step)
                    {
                        long p = r0 + x * 4; byte r = f.P[p], g = f.P[p + 1], b = f.P[p + 2];
                        int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
                        count[key]++; sr[key] += r; sg[key] += g; sb[key] += b;
                    }
                }
            var pts = new List<(double R, double G, double B, long C)>();
            for (int k = 0; k < Cells; k++) if (count[k] > 0) pts.Add(((double)sr[k] / count[k], (double)sg[k] / count[k], (double)sb[k] / count[k], count[k]));
            if (pts.Count <= n)
                return new Palette(pts.Select(p => (byte)Math.Round(p.R)).ToArray(), pts.Select(p => (byte)Math.Round(p.G)).ToArray(), pts.Select(p => (byte)Math.Round(p.B)).ToArray());

            // ---- median cut: split the box with the largest squared error along its widest-variance axis at the weighted median
            var arr = pts.ToArray();
            var boxes = new List<(int Lo, int Hi, double Err, int Axis)>();
            (double Err, int Axis) Stats(int lo, int hi)
            {
                double c = 0, m0 = 0, m1 = 0, m2 = 0, q0 = 0, q1 = 0, q2 = 0;
                for (int i = lo; i < hi; i++) { var p = arr[i]; c += p.C; m0 += p.R * p.C; m1 += p.G * p.C; m2 += p.B * p.C; q0 += p.R * p.R * p.C; q1 += p.G * p.G * p.C; q2 += p.B * p.B * p.C; }
                double v0 = q0 - m0 * m0 / c, v1 = q1 - m1 * m1 / c, v2 = q2 - m2 * m2 / c;
                v0 *= 3; v1 *= 4; v2 *= 2;
                int axis = v0 >= v1 && v0 >= v2 ? 0 : v1 >= v2 ? 1 : 2;
                return (hi - lo > 1 ? v0 + v1 + v2 : 0, axis);
            }
            var s0 = Stats(0, arr.Length); boxes.Add((0, arr.Length, s0.Err, s0.Axis));
            while (boxes.Count < n)
            {
                int bi = -1; double be = 0;
                for (int i = 0; i < boxes.Count; i++) if (boxes[i].Err > be) { be = boxes[i].Err; bi = i; }
                if (bi < 0) break;
                var (lo, hi, _, axis) = boxes[bi];
                Array.Sort(arr, lo, hi - lo, Comparer<(double R, double G, double B, long C)>.Create((a, b) => axis switch
                {
                    0 => a.R.CompareTo(b.R), 1 => a.G.CompareTo(b.G), _ => a.B.CompareTo(b.B),
                }));
                long total = 0; for (int i = lo; i < hi; i++) total += arr[i].C;
                long acc = 0; int mid = lo + 1;
                for (int i = lo; i < hi - 1; i++) { acc += arr[i].C; if (acc * 2 >= total) { mid = i + 1; break; } mid = i + 2; }
                mid = Math.Clamp(mid, lo + 1, hi - 1);
                var a1 = Stats(lo, mid); var a2 = Stats(mid, hi);
                boxes[bi] = (lo, mid, a1.Err, a1.Axis); boxes.Add((mid, hi, a2.Err, a2.Axis));
            }
            var cr = new double[boxes.Count]; var cg = new double[boxes.Count]; var cb = new double[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
            {
                double c = 0, r = 0, g = 0, b = 0;
                for (int j = boxes[i].Lo; j < boxes[i].Hi; j++) { var p = arr[j]; c += p.C; r += p.R * p.C; g += p.G * p.C; b += p.B * p.C; }
                cr[i] = r / c; cg[i] = g / c; cb[i] = b / c;
            }
            // ---- k-means refinement on the histogram (weighted by count)
            int K = boxes.Count; var assign = new int[arr.Length];
            for (int it = 0; it < 4; it++)
            {
                var nr = new double[K]; var ng = new double[K]; var nb = new double[K]; var nc = new double[K];
                object lk = new();
                Parallel.For(0, arr.Length, () => (new double[K], new double[K], new double[K], new double[K]), (i, _, l) =>
                {
                    var p = arr[i]; int best = 0; double bd = double.MaxValue;
                    for (int k = 0; k < K; k++)
                    {
                        double dr = p.R - cr[k], dg = p.G - cg[k], db = p.B - cb[k];
                        double d = 3 * dr * dr + 4 * dg * dg + 2 * db * db;
                        if (d < bd) { bd = d; best = k; }
                    }
                    assign[i] = best;
                    l.Item1[best] += p.R * p.C; l.Item2[best] += p.G * p.C; l.Item3[best] += p.B * p.C; l.Item4[best] += p.C;
                    return l;
                }, l => { lock (lk) for (int k = 0; k < K; k++) { nr[k] += l.Item1[k]; ng[k] += l.Item2[k]; nb[k] += l.Item3[k]; nc[k] += l.Item4[k]; } });
                for (int k = 0; k < K; k++) if (nc[k] > 0) { cr[k] = nr[k] / nc[k]; cg[k] = ng[k] / nc[k]; cb[k] = nb[k] / nc[k]; }
            }
            var R = new byte[K]; var G = new byte[K]; var B = new byte[K];
            for (int k = 0; k < K; k++) { R[k] = (byte)Math.Clamp(Math.Round(cr[k]), 0, 255); G[k] = (byte)Math.Clamp(Math.Round(cg[k]), 0, 255); B[k] = (byte)Math.Clamp(Math.Round(cb[k]), 0, 255); }
            return new Palette(R, G, B);
        }
    }

    // ---------------------------------------------------------------------------------------------------- LZW

    /// <summary>GIF's variable-width LZW (the scheme of Go's compress/lzw writer): clear code first, a new code per miss, the width grows
    /// when the next code reaches the current limit, a clear when 4095 is reached; codes packed LSB first into 255-byte sub-blocks.</summary>
    sealed class Lzw
    {
        readonly int[] _table = new int[4096 << 8];      // (prefix code << 8 | byte) → code + 1; 0 = none
        readonly List<int> _used = new();
        Stream _s = Stream.Null;
        readonly byte[] _block = new byte[255]; int _blockLen;
        uint _acc; int _accBits;

        public void Encode(Stream s, byte[] data, int litWidth)
        {
            _s = s; _blockLen = 0; _acc = 0; _accBits = 0;
            s.WriteByte((byte)litWidth);
            int clear = 1 << litWidth, eof = clear + 1;
            int width = litWidth + 1, hi = eof, overflow = 1 << (litWidth + 1);
            void Reset() { foreach (var k in _used) _table[k] = 0; _used.Clear(); }
            Reset();
            Put(clear, width);
            if (data.Length > 0)
            {
                int code = data[0];
                for (long i = 1; i < data.LongLength; i++)
                {
                    int lit = data[i];
                    int key = (code << 8) | lit;
                    int t = _table[key];
                    if (t != 0) { code = t - 1; continue; }
                    Put(code, width);
                    code = lit;
                    // the next implied code
                    hi++;
                    if (hi == overflow) { width++; overflow <<= 1; }
                    if (hi == 4095)
                    {
                        Put(clear, width);
                        width = litWidth + 1; hi = eof; overflow = 1 << (litWidth + 1);
                        Reset();
                        continue;
                    }
                    _table[key] = hi + 1; _used.Add(key);
                }
                Put(code, width);
                hi++;
                if (hi == overflow) { width++; overflow <<= 1; }
                if (hi == 4095) { Put(clear, width); width = litWidth + 1; }
            }
            Put(eof, width);
            if (_accBits > 0) { Byte((byte)_acc); _acc = 0; _accBits = 0; }
            if (_blockLen > 0) FlushBlock();
            s.WriteByte(0);   // block terminator
        }

        void Put(int code, int width)
        {
            _acc |= (uint)code << _accBits; _accBits += width;
            while (_accBits >= 8) { Byte((byte)_acc); _acc >>= 8; _accBits -= 8; }
        }

        void Byte(byte b) { _block[_blockLen++] = b; if (_blockLen == 255) FlushBlock(); }
        void FlushBlock() { _s.WriteByte((byte)_blockLen); _s.Write(_block, 0, _blockLen); _blockLen = 0; }
    }
}
