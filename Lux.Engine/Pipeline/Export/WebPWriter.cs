using System.Buffers.Binary;
using Lux.Engine.Pipeline.Parallax;

namespace Lux.Engine.Pipeline.Export;

/// <summary>
/// Lux's animated WebP writer, lossless (VP8L, RFC 9649), no external tool: every pixel exact.
///
/// <para>Container: <c>RIFF/WEBP</c> with <c>VP8X</c> (animation), <c>ANIM</c> (loop count) and one <c>ANMF</c> per frame. The first frame
/// is whole; each later frame stores the rectangle that changed (offsets even, as the format requires) with its unchanged pixels fully
/// transparent and alpha blending on, so they show the previous frame through.</para>
/// <para>Each frame's VP8L bitstream: the subtract-green transform, the predictor transform (one of the 14 predictors per 16×16 block,
/// chosen by the smallest sum of absolute residuals), then one group of five canonical prefix codes (length-limited to 15 bits) over
/// literals and LZ77 back-references (hash chains plus the "same as the left pixel" and "same as the pixel above" distances). Not used
/// (all optional in the format): the color-cache, the cross-color and color-indexing transforms and per-region prefix codes — they would
/// make files smaller, never different pixels. Lossy VP8 is not written.</para>
/// </summary>
public static class WebPWriter
{
    internal static AnimationEncodeResult Write(Stream s, IReadOnlyList<Rgba> frames, IReadOnlyList<int> delaysMs, AnimationEncodeOptions o, bool keep)
    {
        int w = frames[0].W, h = frames[0].H;
        if (w > 16384 || h > 16384) throw new ArgumentException("WebP frames are at most 16384 pixels on a side");
        int effort = Math.Clamp(o.WebPEffort, 1, 9);
        // ---- each frame's rectangle and pixels (ARGB), then the bitstreams (independent: encoded in parallel)
        var rects = new RectI[frames.Count];
        var blend = new bool[frames.Count];
        for (int f = 0; f < frames.Count; f++)
        {
            if (!o.FrameDifferencing || f == 0) { rects[f] = new RectI(0, 0, w, h); continue; }
            var r = AnimationEncoder.Changed(frames[f - 1].P, frames[f].P, w, h, alpha: true) ?? new RectI(0, 0, 1, 1);
            int x0 = r.X0 & ~1, y0 = r.Y0 & ~1;      // ANMF offsets are stored halved
            rects[f] = new RectI(x0, y0, r.X1, r.Y1);
            blend[f] = true;
        }
        var streams = new byte[frames.Count][];
        bool anyAlpha = false;
        int done = 0;
        var po = new ParallelOptions { CancellationToken = o.Cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
        Parallel.For(0, frames.Count, po, f =>
        {
            var r = rects[f]; int rw = r.Width, rh = r.Height;
            var cur = frames[f].P; var prev = f > 0 ? frames[f - 1].P : null;
            // the rectangle's pixels as they are (no blending), and — for a later frame — with the unchanged ones transparent (blended over
            // the previous frame); both are encoded and the smaller is kept (transparency pays when little changes)
            uint[] Pixels(bool transparentUnchanged, out bool alpha)
            {
                var argb = new uint[(long)rw * rh]; alpha = false;
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        long p = ((long)(r.Y0 + y) * w + r.X0 + x) * 4;
                        uint a = cur[p + 3];
                        if (transparentUnchanged && prev is not null && prev[p] == cur[p] && prev[p + 1] == cur[p + 1] && prev[p + 2] == cur[p + 2] && prev[p + 3] == cur[p + 3])
                        { argb[(long)y * rw + x] = 0; alpha = true; continue; }
                        if (a != 255) alpha = true;
                        argb[(long)y * rw + x] = (a << 24) | ((uint)cur[p] << 16) | ((uint)cur[p + 1] << 8) | cur[p + 2];
                    }
                return argb;
            }
            var opaque = Pixels(false, out bool alphaO);
            var bs = Vp8l.Encode(opaque, rw, rh, alphaO, effort);
            bool usedAlpha = alphaO;
            if (blend[f])
            {
                var tr = Pixels(true, out bool alphaT);
                var bt = Vp8l.Encode(tr, rw, rh, alphaT, effort);
                if (bt.Length < bs.Length) { bs = bt; usedAlpha = alphaT; } else blend[f] = false;
            }
            if (usedAlpha) Volatile.Write(ref anyAlpha, true);
            streams[f] = bs;
            o.Progress?.Invoke(Interlocked.Increment(ref done), frames.Count);
        });

        // ---- RIFF
        var body = new MemoryStream();
        var vp8x = new byte[10];
        vp8x[0] = (byte)(0x02 | (anyAlpha ? 0x10 : 0));   // animation (+ alpha)
        U24(vp8x, 4, w - 1); U24(vp8x, 7, h - 1);
        Chunk(body, "VP8X", vp8x);
        var anim = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(anim, 0x00000000);   // background: transparent black
        BinaryPrimitives.WriteUInt16LittleEndian(anim.AsSpan(4), (ushort)Math.Clamp(o.Loops, 0, 65535));
        Chunk(body, "ANIM", anim);
        for (int f = 0; f < frames.Count; f++)
        {
            var r = rects[f]; var bs = streams[f];
            var vp8l = new MemoryStream();
            Chunk(vp8l, "VP8L", bs);
            var hdr = new byte[16];
            U24(hdr, 0, r.X0 / 2); U24(hdr, 3, r.Y0 / 2); U24(hdr, 6, r.Width - 1); U24(hdr, 9, r.Height - 1);
            U24(hdr, 12, Math.Clamp(delaysMs[f], 0, (1 << 24) - 1));
            hdr[15] = (byte)(blend[f] ? 0 : 0x02);   // bit 1: do not blend; bit 0: dispose to background (never)
            var anmf = new byte[16 + vp8l.Length];
            Buffer.BlockCopy(hdr, 0, anmf, 0, 16);
            vp8l.Position = 0; vp8l.ReadExactly(anmf, 16, (int)vp8l.Length);
            Chunk(body, "ANMF", anmf);
        }
        var head = new byte[12];
        "RIFF"u8.CopyTo(head); BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(4), (uint)(4 + body.Length)); "WEBP"u8.CopyTo(head.AsSpan(8));
        s.Write(head);
        body.Position = 0; body.CopyTo(s);
        return new AnimationEncodeResult(0, frames.Count, 0, keep ? frames : null) { WrittenDelaysMs = delaysMs.ToArray() };
    }

    static void U24(byte[] b, int at, int v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); }

    static void Chunk(Stream s, string fourcc, byte[] data)
    {
        Span<byte> h = stackalloc byte[8];
        for (int i = 0; i < 4; i++) h[i] = (byte)fourcc[i];
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)data.Length);
        s.Write(h); s.Write(data);
        if ((data.Length & 1) != 0) s.WriteByte(0);
    }

    /// <summary>One VP8L image (the lossless bitstream).</summary>
    internal static class Vp8l
    {
        const int PredBits = 4;
        const int NumLengthCodes = 24, NumDistanceCodes = 40, MaxLength = 4096, MinMatch = 3;
        const int MaxDistance = (1 << 20) - 121;
        static readonly int[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        public static byte[] Encode(uint[] argb, int w, int h, bool alpha, int effort)
        {
            var bw = new BitWriter(Math.Max(1024, argb.Length * 2));
            bw.Put(0x2F, 8);
            bw.Put((uint)(w - 1), 14); bw.Put((uint)(h - 1), 14); bw.Put(alpha ? 1u : 0u, 1); bw.Put(0, 3);
            var px = (uint[])argb.Clone();
            // ---- subtract green
            bw.Put(1, 1); bw.Put(2, 2);
            for (long i = 0; i < px.LongLength; i++)
            {
                uint c = px[i], g = (c >> 8) & 0xFF;
                uint r = (((c >> 16) & 0xFF) - g) & 0xFF, b = ((c & 0xFF) - g) & 0xFF;
                px[i] = (c & 0xFF00FF00u) | (r << 16) | b;
            }
            // ---- predictor (choose per block, then residuals from the untouched pixels)
            int bwk = (w + (1 << PredBits) - 1) >> PredBits, bhk = (h + (1 << PredBits) - 1) >> PredBits;
            var modes = ChooseModes(px, w, h, bwk, bhk);
            var res = new uint[px.LongLength];
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    long i = (long)y * w + x;
                    uint pred = y == 0 ? (x == 0 ? 0xFF000000u : px[i - 1]) : x == 0 ? px[i - w] : Predict(modes[(y >> PredBits) * bwk + (x >> PredBits)], px, i, w);
                    res[i] = Sub(px[i], pred);
                }
            });
            bw.Put(1, 1); bw.Put(0, 2); bw.Put(PredBits - 2, 3);
            var modeImg = new uint[modes.Length];
            for (int i = 0; i < modes.Length; i++) modeImg[i] = 0xFF000000u | ((uint)modes[i] << 8);
            WriteImage(bw, modeImg, bwk, bhk, level0: false, effort);
            bw.Put(0, 1);   // no more transforms
            // ---- the main image
            WriteImage(bw, res, w, h, level0: true, effort);
            return bw.ToArray();
        }

        // ------------------------------------------------------------------------------------------------ predictor

        static uint Sub(uint a, uint b)
        {
            uint ag = ((a | 0x00FF00FFu) - (b & 0xFF00FF00u)) & 0xFF00FF00u;
            uint rb = ((a | 0xFF00FF00u) - (b & 0x00FF00FFu)) & 0x00FF00FFu;
            return ag | rb;
        }

        static uint Avg2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFEu) >> 1) + (a & b);

        static int Ch(uint c, int s) => (int)((c >> s) & 0xFF);

        static uint Select(uint L, uint T, uint TL)
        {
            int pL = 0, pT = 0;
            for (int s = 0; s < 32; s += 8)
            {
                int p = Ch(L, s) + Ch(T, s) - Ch(TL, s);
                pL += Math.Abs(p - Ch(L, s)); pT += Math.Abs(p - Ch(T, s));
            }
            return pL < pT ? L : T;
        }

        static uint ClampFull(uint a, uint b, uint c)
        {
            uint o = 0;
            for (int s = 0; s < 32; s += 8) o |= (uint)Math.Clamp(Ch(a, s) + Ch(b, s) - Ch(c, s), 0, 255) << s;
            return o;
        }

        /// <summary>Predictor <paramref name="mode"/> for pixel <paramref name="i"/> (not on the first row or column).</summary>
        static uint Predict(int mode, uint[] p, long i, int w)
        {
            uint L = p[i - 1], T = p[i - w], TL = p[i - w - 1], TR = p[i - w + 1];
            return mode switch
            {
                0 => 0xFF000000u,
                1 => L,
                2 => T,
                3 => TR,
                4 => TL,
                5 => Avg2(Avg2(L, TR), T),
                6 => Avg2(L, TL),
                7 => Avg2(L, T),
                8 => Avg2(TL, T),
                9 => Avg2(T, TR),
                10 => Avg2(Avg2(L, TL), Avg2(T, TR)),
                11 => Select(L, T, TL),
                12 => ClampFull(L, T, TL),
                _ => ClampHalfOf(L, T, TL),
            };
        }

        static uint ClampHalfOf(uint L, uint T, uint TL)
        {
            uint a = Avg2(L, T), r = 0;
            for (int s = 0; s < 32; s += 8) { int x = Ch(a, s), y = Ch(TL, s); r |= (uint)Math.Clamp(x + (x - y) / 2, 0, 255) << s; }
            return r;
        }

        static int[] ChooseModes(uint[] px, int w, int h, int bwk, int bhk)
        {
            var modes = new int[bwk * bhk];
            Parallel.For(0, bhk, by =>
            {
                Span<long> cost = stackalloc long[14];
                for (int bx = 0; bx < bwk; bx++)
                {
                    cost.Clear();
                    int y0 = by << PredBits, x0 = bx << PredBits, y1 = Math.Min(h, y0 + (1 << PredBits)), x1 = Math.Min(w, x0 + (1 << PredBits));
                    for (int y = Math.Max(1, y0); y < y1; y++)
                        for (int x = Math.Max(1, x0); x < x1; x++)
                        {
                            long i = (long)y * w + x;
                            for (int m = 0; m < 14; m++)
                            {
                                uint r = Sub(px[i], Predict(m, px, i, w));
                                cost[m] += AbsByte(r) + AbsByte(r >> 8) + AbsByte(r >> 16) + AbsByte(r >> 24);
                            }
                        }
                    int best = 1; long bc = long.MaxValue;
                    for (int m = 0; m < 14; m++) if (cost[m] < bc) { bc = cost[m]; best = m; }
                    modes[by * bwk + bx] = best;
                }
            });
            return modes;
        }

        static int AbsByte(uint v) { int b = (sbyte)(byte)v; return b < 0 ? -b : b; }

        // ------------------------------------------------------------------------------------------------ entropy-coded image

        readonly record struct Token(uint Value, int Length, int Distance);   // Length 0 = literal ARGB in Value

        static void WriteImage(BitWriter bw, uint[] px, int w, int h, bool level0, int effort)
        {
            bw.Put(0, 1);                  // no color cache
            if (level0) bw.Put(0, 1);      // no per-region prefix codes
            var tokens = Lz77(px, w, effort);
            // histograms of the five alphabets
            var hg = new long[256 + NumLengthCodes]; var hr = new long[256]; var hb = new long[256]; var ha = new long[256]; var hd = new long[NumDistanceCodes];
            foreach (var t in tokens)
            {
                if (t.Length == 0) { uint c = t.Value; hg[(c >> 8) & 0xFF]++; hr[(c >> 16) & 0xFF]++; hb[c & 0xFF]++; ha[c >> 24]++; }
                else
                {
                    hg[256 + Prefix(t.Length, out _, out _)]++;
                    hd[Prefix(DistanceCode(t.Distance, w), out _, out _)]++;
                }
            }
            var cg = Code.Build(hg, 15); var cr = Code.Build(hr, 15); var cb = Code.Build(hb, 15); var ca = Code.Build(ha, 15); var cd = Code.Build(hd, 15);
            cg.WriteTo(bw); cr.WriteTo(bw); cb.WriteTo(bw); ca.WriteTo(bw); cd.WriteTo(bw);
            foreach (var t in tokens)
            {
                if (t.Length == 0)
                {
                    uint c = t.Value;
                    cg.Emit(bw, (int)((c >> 8) & 0xFF)); cr.Emit(bw, (int)((c >> 16) & 0xFF)); cb.Emit(bw, (int)(c & 0xFF)); ca.Emit(bw, (int)(c >> 24));
                }
                else
                {
                    int lc = Prefix(t.Length, out int lb, out int lx);
                    cg.Emit(bw, 256 + lc); bw.Put((uint)lx, lb);
                    int dc = Prefix(DistanceCode(t.Distance, w), out int db, out int dx);
                    cd.Emit(bw, dc); bw.Put((uint)dx, db);
                }
            }
        }

        /// <summary>The distance symbol value: the two neighborhood codes this writer uses (code 1 = the pixel above, code 2 = the pixel to the
        /// left — the first two entries of the format's distance map), else the plain distance + 120.</summary>
        static int DistanceCode(int dist, int w) => dist == w ? 1 : dist == 1 ? 2 : dist + 120;

        /// <summary>The prefix coding of a length or distance value v ≥ 1: symbol, extra bit count and extra bits.</summary>
        static int Prefix(int v, out int extraBits, out int extra)
        {
            int d = v - 1;
            if (d < 4) { extraBits = 0; extra = 0; return d; }
            int hb = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)d);
            int second = (d >> (hb - 1)) & 1;
            extraBits = hb - 1;
            extra = d & ((1 << extraBits) - 1);
            return 2 * hb + second;
        }

        /// <summary>Greedy LZ77 over whole pixels: hash chains on pixel pairs plus the run (distance 1) and the row above (distance w).</summary>
        static List<Token> Lz77(uint[] px, int w, int effort)
        {
            int n = px.Length;
            var tokens = new List<Token>(Math.Min(n, 1 << 20));
            const int HashBits = 18;
            var head = new int[1 << HashBits]; Array.Fill(head, -1);
            var prev = new int[n];
            int chain = 4 << effort;   // candidates per position
            static int Hash(uint a, uint b) => (int)((a * 0x9E3779B1u ^ (b * 0x85EBCA77u + (b >> 15))) >> (32 - 18));
            void Insert(int i) { if (i + 1 >= n) return; int k = Hash(px[i], px[i + 1]); prev[i] = head[k]; head[k] = i; }
            int MatchLen(int i, int j, int max) { int l = 0; while (l < max && px[i + l] == px[j + l]) l++; return l; }
            int pos = 0;
            while (pos < n)
            {
                int max = Math.Min(MaxLength, n - pos), bestLen = 0, bestDist = 0;
                if (max >= MinMatch)
                {
                    // the cheap distances first
                    if (pos >= 1) { int l = MatchLen(pos, pos - 1, max); if (l > bestLen) { bestLen = l; bestDist = 1; } }
                    if (pos >= w && bestLen < max) { int l = MatchLen(pos, pos - w, max); if (l > bestLen) { bestLen = l; bestDist = w; } }
                    if (bestLen < max && pos + 1 < n)
                    {
                        int c = head[Hash(px[pos], px[pos + 1])], k = 0;
                        while (c >= 0 && k++ < chain && pos - c <= MaxDistance)
                        {
                            if (px[c + bestLen < n ? c + bestLen : c] == px[pos + bestLen < n ? pos + bestLen : pos])
                            {
                                int l = MatchLen(pos, c, max);
                                if (l > bestLen || (l == bestLen && l > 0 && pos - c < bestDist)) { bestLen = l; bestDist = pos - c; if (l == max) break; }
                            }
                            c = prev[c];
                        }
                    }
                }
                if (bestLen >= MinMatch)
                {
                    tokens.Add(new Token(0, bestLen, bestDist));
                    for (int k = 0; k < bestLen; k++) Insert(pos + k);
                    pos += bestLen;
                }
                else
                {
                    tokens.Add(new Token(px[pos], 0, 0));
                    Insert(pos);
                    pos++;
                }
            }
            return tokens;
        }

        // ------------------------------------------------------------------------------------------------ prefix codes

        sealed class Code
        {
            readonly int[] _len; readonly uint[] _code; readonly int _single = -1; readonly int _used;
            Code(int[] len, int used)
            {
                _len = len; _used = used; _code = new uint[len.Length];
                if (used == 1) { for (int i = 0; i < len.Length; i++) if (len[i] > 0) _single = i; return; }
                if (used == 0) return;
                int max = len.Max();
                var count = new int[max + 1]; foreach (var l in len) if (l > 0) count[l]++;
                // deflate's canonical assignment: shorter codes first, then by symbol
                var next = new uint[max + 2]; uint code = 0; count[0] = 0;
                for (int b = 1; b <= max; b++) { code = (code + (uint)count[b - 1]) << 1; next[b] = code; }
                for (int i = 0; i < len.Length; i++)
                    if (len[i] > 0) _code[i] = Reverse(next[len[i]]++, len[i]);
            }

            static uint Reverse(uint c, int n) { uint r = 0; for (int i = 0; i < n; i++) { r = (r << 1) | (c & 1); c >>= 1; } return r; }

            public static Code Build(long[] hist, int limit)
            {
                int used = hist.Count(c => c > 0);
                return new Code(Lengths(hist, limit), used);
            }

            public void Emit(BitWriter bw, int symbol)
            {
                if (_used <= 1) return;   // a code with one symbol takes no bits
                bw.Put(_code[symbol], _len[symbol]);
            }

            public void WriteTo(BitWriter bw)
            {
                var syms = Enumerable.Range(0, _len.Length).Where(i => _len[i] > 0).ToList();
                if (syms.Count == 0) { bw.Put(1, 1); bw.Put(0, 1); bw.Put(0, 1); bw.Put(0, 1); return; }   // simple, one symbol: 0
                if (syms.Count <= 2 && syms.All(s => s < 256))
                {
                    bw.Put(1, 1); bw.Put((uint)(syms.Count - 1), 1);
                    if (syms[0] < 2) { bw.Put(0, 1); bw.Put((uint)syms[0], 1); } else { bw.Put(1, 1); bw.Put((uint)syms[0], 8); }
                    if (syms.Count == 2) bw.Put((uint)syms[1], 8);
                    return;
                }
                // normal: the code lengths, run-length coded with 16 / 17 / 18, through a code-length code (lengths ≤ 7)
                var toks = new List<(int Sym, int Extra, int Bits)>();
                int prevNz = 8, i = 0, n = _len.Length;
                while (i < n)
                {
                    int v = _len[i], run = 1;
                    while (i + run < n && _len[i + run] == v) run++;
                    i += run;
                    if (v == 0)
                    {
                        while (run >= 11) { int k = Math.Min(run, 138); toks.Add((18, k - 11, 7)); run -= k; }
                        if (run >= 3) { toks.Add((17, run - 3, 3)); run = 0; }
                        while (run-- > 0) toks.Add((0, 0, 0));
                    }
                    else
                    {
                        if (v != prevNz) { toks.Add((v, 0, 0)); prevNz = v; run--; }
                        while (run >= 3) { int k = Math.Min(run, 6); toks.Add((16, k - 3, 2)); run -= k; }
                        while (run-- > 0) toks.Add((v, 0, 0));
                    }
                }
                var hist = new long[19]; foreach (var t in toks) hist[t.Sym]++;
                var clLen = Lengths(hist, 7);
                var cl = new Code(clLen, hist.Count(c => c > 0));
                int num = 19; while (num > 4 && clLen[CodeLengthOrder[num - 1]] == 0) num--;
                bw.Put(0, 1);
                bw.Put((uint)(num - 4), 4);
                for (int k = 0; k < num; k++) bw.Put((uint)clLen[CodeLengthOrder[k]], 3);
                bw.Put(0, 1);   // every symbol's length follows (no max_symbol)
                foreach (var t in toks) { cl.Emit(bw, t.Sym); if (t.Bits > 0) bw.Put((uint)t.Extra, t.Bits); }
            }

            /// <summary>Huffman code lengths limited to <paramref name="limit"/> bits (small counts raised until the tree fits — always a
            /// complete code). One used symbol gets length 1 (written, but coded with no bits).</summary>
            static int[] Lengths(long[] hist, int limit)
            {
                int n = hist.Length;
                var len = new int[n];
                var syms = Enumerable.Range(0, n).Where(i => hist[i] > 0).ToArray();
                if (syms.Length == 0) return len;
                if (syms.Length == 1) { len[syms[0]] = 1; return len; }
                for (long floor = 1; ; floor *= 2)
                {
                    // Huffman by a priority queue; ties broken by node order so the result is deterministic
                    var pq = new PriorityQueue<int, (long, int)>();
                    var parent = new List<int>(); var weight = new List<long>();
                    foreach (var s in syms) { parent.Add(-1); weight.Add(Math.Max(hist[s], floor)); pq.Enqueue(parent.Count - 1, (weight[^1], parent.Count - 1)); }
                    while (pq.Count > 1)
                    {
                        pq.TryDequeue(out int a, out var pa); pq.TryDequeue(out int b, out var pb);
                        parent.Add(-1); weight.Add(pa.Item1 + pb.Item1); int node = parent.Count - 1;
                        parent[a] = node; parent[b] = node;
                        pq.Enqueue(node, (weight[node], node));
                    }
                    int maxDepth = 0;
                    var depth = new int[syms.Length];
                    for (int k = 0; k < syms.Length; k++) { int d = 0, c = k; while (parent[c] >= 0) { c = parent[c]; d++; } depth[k] = d; maxDepth = Math.Max(maxDepth, d); }
                    if (maxDepth <= limit) { for (int k = 0; k < syms.Length; k++) len[syms[k]] = depth[k]; return len; }
                }
            }
        }

        sealed class BitWriter(int capacity)
        {
            byte[] _buf = new byte[capacity]; int _pos; ulong _acc; int _bits;
            public void Put(uint v, int n)
            {
                if (n == 0) return;
                _acc |= (ulong)(v & (n == 32 ? 0xFFFFFFFFu : (1u << n) - 1)) << _bits; _bits += n;
                while (_bits >= 8)
                {
                    if (_pos == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
                    _buf[_pos++] = (byte)_acc; _acc >>= 8; _bits -= 8;
                }
            }
            public byte[] ToArray()
            {
                if (_bits > 0) { if (_pos == _buf.Length) Array.Resize(ref _buf, _buf.Length + 1); _buf[_pos++] = (byte)_acc; _acc = 0; _bits = 0; }
                return _buf.AsSpan(0, _pos).ToArray();
            }
        }
    }
}
