using System.Globalization;
using Lux.Engine.Pipeline.Export;

namespace Lux.Library.Edit;

/// <summary>
/// Lumen's `lt::TransformImpl` — the object behind `CIAPI::Transform`: the domain aspect (reduced), the 3×3 matrix (Lumen's order:
/// x' = m0·x + m3·y + m6, y' = m1·x + m4·y + m7, mapping the output to the capture) and the crop corners. Each operation keeps
/// Lumen's float operations in the compiled order, so a sequence of operations gives the same bits as Lumen.
/// <para>Each operation is also appended to <see cref="Ops"/> ("setcrop x y w h" with hex bit patterns, "reset 0", "rot90 k", "fliph",
/// "flipv"), so the same sequence can be replayed on Lumen's own object.</para>
/// </summary>
internal sealed class LumenTransform
{
    public int AspectW = 4, AspectH = 3;
    public readonly float[] M = { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
    public float X0, Y0, X1 = 1f, Y1 = 1f;
    /// <summary>The Lumen calls made so far (for replay on Lumen's object); null = not recorded.</summary>
    public List<string>? Ops;

    public LumenTransform Clone()
    {
        var t = new LumenTransform { AspectW = AspectW, AspectH = AspectH, X0 = X0, Y0 = Y0, X1 = X1, Y1 = Y1, Ops = Ops is null ? null : new List<string>(Ops) };
        Array.Copy(M, t.M, 9);
        return t;
    }

    public static LumenTransform Identity(int aspectW, int aspectH) => new() { AspectW = aspectW, AspectH = aspectH };

    public static LumenTransform FromExport(ExportTransform e)
    {
        var t = new LumenTransform { AspectW = e.AspectW, AspectH = e.AspectH, X0 = e.CropX0, Y0 = e.CropY0, X1 = e.CropX1, Y1 = e.CropY1 };
        Array.Copy(e.Matrix, t.M, 9);
        return t;
    }

    public ExportTransform ToExport() => new() { Matrix = (float[])M.Clone(), AspectW = AspectW, AspectH = AspectH, CropX0 = X0, CropY0 = Y0, CropX1 = X1, CropY1 = Y1 };

    public bool IsIdentity => X0 == 0f && Y0 == 0f && X1 == 1f && Y1 == 1f && M[0] == 1f && M[1] == 0f && M[2] == 0f && M[3] == 0f && M[4] == 1f && M[5] == 0f && M[6] == 0f && M[7] == 0f && M[8] == 1f;

    /// <summary>Same bits.</summary>
    public bool SameAs(LumenTransform o)
    {
        if (AspectW != o.AspectW || AspectH != o.AspectH) return false;
        for (int i = 0; i < 9; i++) if (BitConverter.SingleToUInt32Bits(M[i]) != BitConverter.SingleToUInt32Bits(o.M[i])) return false;
        return Bits(X0) == Bits(o.X0) && Bits(Y0) == Bits(o.Y0) && Bits(X1) == Bits(o.X1) && Bits(Y1) == Bits(o.Y1);
    }
    static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);
    static string H(float f) => Bits(f).ToString("x8", CultureInfo.InvariantCulture);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"aspect {AspectW}:{AspectH} matrix [{string.Join(' ', M.Select(H))}] crop [{H(X0)} {H(Y0)} {H(X1)} {H(Y1)}]");

    // ------------------------------------------------------------------------------------------------ the operations

    /// <summary>`CIAPI::Transform::setCrop(RectF)`: crop = (x, y, w + x, h + y) (two single-precision additions).</summary>
    public void SetCrop(float x, float y, float w, float h)
    {
        Ops?.Add($"setcrop {H(x)} {H(y)} {H(w)} {H(h)}");
        X0 = x; Y0 = y; X1 = w + x; Y1 = h + y;
    }

    /// <summary>`CIAPI::Transform::crop()`: (x0, y0, x1 − x0, y1 − y0).</summary>
    public (float X, float Y, float W, float H) Crop() => (X0, Y0, X1 - X0, Y1 - Y0);

    /// <summary>`CIAPI::Transform::reset(int orientation)`, orientation 0 (the only one Lumen passes besides ParamInt 2, which
    /// is 0 on every L16 capture): identity matrix, crop (0, 0, 1, 1).</summary>
    public void Reset(int orientation = 0)
    {
        if (orientation != 0) throw new NotSupportedException("only orientation 0 is used by Lumen on the L16");
        Ops?.Add("reset 0");
        X0 = 0f; Y0 = 0f; X1 = 1f; Y1 = 1f;
        M[0] = 1f; M[1] = 0f; M[2] = 0f; M[3] = 0f; M[4] = 1f; M[5] = 0f; M[6] = 0f; M[7] = 0f; M[8] = 1f;
    }

    /// <summary>`CIAPI::Transform::rotate90(int k)`: a rotation by k quarter turns about the crop center (in the aspect domain),
    /// composed onto the matrix (new = M·A), and for odd k the crop rect turned about its center with the aspect swapped.</summary>
    public void Rotate90(int k)
    {
        Ops?.Add(string.Create(CultureInfo.InvariantCulture, $"rot90 {k}"));
        float cx = (X1 - X0) * 0.5f + X0, cy = (Y1 - Y0) * 0.5f + Y0;
        float px = (float)AspectW * cx, nx = -px;
        float py = (float)AspectH * cy, ny = -py;
        // c, s: v = (|k| & 2) ? −1 : 1; even k → c = v, s = 0; odd k → c = 0, s = v · sign(k)
        int sgn = k >> 31, abs = (k ^ sgn) - sgn, e = ((~abs) & 2) - 1;
        float c, s;
        if ((k & 1) == 0) { c = (float)e; s = 0f; }
        else { int sk = ((k != 0 ? 1 : 0) ^ sgn) - sgn; c = 0f; s = (float)(e * sk); }
        // the initializer list {c, s, 0, −s, c, 0, 0, 0, 1}
        float R0 = c, R1 = s, R2 = 0f, R3 = -s, R4 = c, R5 = 0f, R6 = 0f, R7 = 0f, R8 = 1f;
        float A0 = R2 * px + R0;
        float t3 = R0 * nx;
        float A1 = R2 * py + R1;
        float t7 = R1 * nx;
        float t11 = nx * R2;
        float A3 = R5 * px + R3;
        float t6 = R3 * ny + t3;
        float A4 = R5 * py + R4;
        float t10 = R4 * ny + t7;
        float t8 = ny * R5 + t11;
        t6 = t6 + R6; t10 = t10 + R7; t8 = t8 + R8;
        float A6 = t8 * px + t6;
        float A7 = t8 * py + t10;
        float A2 = R2, A5 = R5, A8 = t8;
        Compose(A0, A1, A2, A3, A4, A5, A6, A7, A8);
        if ((k & 1) != 0)
        {
            float W = (float)AspectW, Hh = (float)AspectH;
            float invH = 1f / Hh;
            float x0 = X0, y0 = Y0, dx = X1 - x0;
            float x0W = x0 * W;
            float Hy0 = Hh * y0;
            float nx0 = ((px - py) + Hy0) / W;
            float WinvH = W * invH;
            float inv = 1f / WinvH;
            float ny0 = ((py - px) + x0W) * invH;
            float nx1 = (Y1 - y0) * inv + nx0;
            float ny1 = dx * WinvH + ny0;
            X0 = nx0; Y0 = ny0; X1 = nx1; Y1 = ny1;
        }
    }

    /// <summary>The matrix product of the transform operations (lane by lane, in Lumen's multiply/add order): new = M·A.</summary>
    void Compose(float A0, float A1, float A2, float A3, float A4, float A5, float A6, float A7, float A8)
    {
        float M0 = M[0], M1 = M[1], M2 = M[2], M3 = M[3], M4 = M[4], M5 = M[5], M6 = M[6], M7 = M[7], M8 = M[8];
        M[0] = A2 * M6 + (A1 * M3 + A0 * M0);
        M[1] = A2 * M7 + (A1 * M4 + A0 * M1);
        M[2] = A2 * M8 + (A1 * M5 + A0 * M2);
        M[3] = A5 * M6 + (A4 * M3 + A3 * M0);
        M[4] = A5 * M7 + (A4 * M4 + A3 * M1);
        M[5] = A5 * M8 + (A4 * M5 + A3 * M2);
        M[6] = A8 * M6 + (A7 * M3 + A6 * M0);
        M[7] = A8 * M7 + (A7 * M4 + A6 * M1);
        M[8] = A8 * M8 + (A7 * M5 + A6 * M2);
    }

    /// <summary>`CIAPI::Transform::flipHorizontal()`: mirror about the crop's x center — t = (c + c)·W; row 0 negated, t × the old
    /// row 0 added to row 2; crop unchanged.</summary>
    public void FlipHorizontal()
    {
        Ops?.Add("fliph");
        float c = (X1 - X0) * 0.5f + X0;
        float t = (c + c) * (float)AspectW;
        float M0 = M[0], M1 = M[1], M2 = M[2];
        M[0] = -M0; M[1] = -M1; M[2] = -M2;
        M[6] = t * M0 + M[6]; M[7] = t * M1 + M[7]; M[8] = t * M2 + M[8];
    }

    /// <summary>`CIAPI::Transform::flipVertical()`: mirror about the crop's y center (row 1 negated, t × the old row 1 into row 2).</summary>
    public void FlipVertical()
    {
        Ops?.Add("flipv");
        float c = (Y1 - Y0) * 0.5f + Y0;
        float t = (c + c) * (float)AspectH;
        float M3 = M[3], M4 = M[4], M5 = M[5];
        M[3] = -M3; M[4] = -M4; M[5] = -M5;
        M[6] = M3 * t + M[6]; M[7] = M4 * t + M[7]; M[8] = t * M5 + M[8];
    }

    // ------------------------------------------------------------------------------------------------ the level rotation (Lumen has none)

    /// <summary>Rotate by a free angle about the crop center. <paramref name="c"/> and <paramref name="s"/> are the cosine and the sine of
    /// the angle. The matrix is made as <see cref="Rotate90"/> makes it, and composed in the same way (new = M·A). A positive sine turns the
    /// picture content counter-clockwise. The crop does not change.</summary>
    public void RotateFine(float c, float s)
    {
        Ops?.Add($"rotate {H(c)} {H(s)}");
        float cx = (X1 - X0) * 0.5f + X0, cy = (Y1 - Y0) * 0.5f + Y0;
        float px = (float)AspectW * cx, py = (float)AspectH * cy;
        float A6 = px + ((-s) * (-py) + c * (-px));
        float A7 = py + (c * (-py) + s * (-px));
        Compose(c, s, 0f, -s, c, 0f, A6, A7, 1f);
    }

    /// <summary>The free rotation in the matrix: the cosine and the sine that <see cref="RotateFine"/> composed onto a matrix of quarter
    /// turns and flips. The values are the same bits that went in. (1, 0) for a matrix without a free rotation.</summary>
    public (float C, float S) FineRotation()
    {
        static float Sign(float v) => v < 0f ? -1f : 1f;
        // The matrix is Q·R. Q has only 0 and ±1. R is the free rotation. The first column of R is Qᵀ·(m0, m1).
        bool transposed = Math.Abs(M[1]) > Math.Abs(M[0]);
        return transposed ? (Sign(M[1]) * M[1], Sign(M[3]) * M[0]) : (Sign(M[0]) * M[0], Sign(M[4]) * M[1]);
    }

    /// <summary>The matrix has a free rotation.</summary>
    public bool HasFineRotation => FineRotation().S != 0f;

    /// <summary>Make the crop smaller about its center until its four corners map into the capture frame. A crop that is inside does not
    /// change. Returns true when the crop changed.</summary>
    public bool FitCropInside()
    {
        double w = AspectW, h = AspectH;
        double cw = (double)X1 - X0, ch = (double)Y1 - Y0;
        double cx = X0 + cw / 2, cy = Y0 + ch / 2;
        double px = w * cx, py = h * cy, hw = w * cw / 2, hh = h * ch / 2;
        // the crop center in the capture, and the largest distance of a corner from it on each axis
        double u = M[0] * px + M[3] * py + M[6], v = M[1] * px + M[4] * py + M[7];
        double eu = Math.Abs(M[0]) * hw + Math.Abs(M[3]) * hh, ev = Math.Abs(M[1]) * hw + Math.Abs(M[4]) * hh;
        double k = 1.0;
        if (eu > 0) k = Math.Min(k, Math.Min(u, w - u) / eu);
        if (ev > 0) k = Math.Min(k, Math.Min(v, h - v) / ev);
        if (!(k < 1.0 - 1e-6)) return false;
        if (k < 0.05) k = 0.05;   // the center is at the border or outside: keep a small picture
        double nw = cw * k, nh = ch * k;
        SetCrop((float)(cx - nw / 2), (float)(cy - nh / 2), (float)nw, (float)nh);
        return true;
    }
}

/// <summary>Qt 5's `QRectF` (doubles x, y, w, h) with the member functions Lumen imports, as Qt defines them.</summary>
internal struct QRect
{
    public double X, Y, W, H;
    public QRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    public readonly (double X, double Y) Center => (X + W / 2, Y + H / 2);
    public void MoveCenter((double X, double Y) p) { X = p.X - W / 2; Y = p.Y - H / 2; }
    public void SetX(double pos) { double diff = pos - X; X += diff; W -= diff; }
    public void SetY(double pos) { double diff = pos - Y; Y += diff; H -= diff; }
    public readonly double Top => Y;
    public readonly double Left => X;
    public readonly double Bottom => Y + H;
    public readonly double Right => X + W;
    public override readonly string ToString() => string.Create(CultureInfo.InvariantCulture, $"({X:R}, {Y:R}, {W:R}, {H:R})");
}

/// <summary>
/// Lumen's `GeometryEdits` (the editor's GEOMETRY section; a Qt object with the methods `rotateRight`, `rotateLeft`, `mirrorHorizontal`,
/// `mirrorVertical`, `applyCrop`, `setCropMode`, `initTransform` and a crop state setter) and its helpers on `CIAPI::Transform`.
/// Its crop frame (and the frame when the crop tool opened) is a `QRectF` in the capture's own (unturned, unmirrored) normalized
/// coordinates, so it turns with the photo. `QRectF` arithmetic in doubles, conversions where Lumen makes them.
/// </summary>
internal sealed class LumenGeometryEdits
{
    public LumenTransform T;
    /// <summary>The frame when the crop tool was opened.</summary>
    public QRect Original;
    /// <summary>The crop frame (applied crop when the tool is hidden).</summary>
    public QRect Frame;
    /// <summary>The crop mode (`CropMode`: 0 free, 1 4:3, 2 3:4, 3 16:9, 4 9:16, 5 3:2, 6 2:3, 7 2:1, 8 1:2, 9 1:1).</summary>
    public int CropMode;
    /// <summary>The crop state (0 hidden, 1 shown, 2 active).</summary>
    public int CropState;

    public LumenGeometryEdits(LumenTransform t) { T = t; InitTransform(t); }

    public LumenGeometryEdits Clone() => new(T.Clone()) { Original = Original, Frame = Frame, CropMode = CropMode, CropState = CropState };

    // ---- helpers on CIAPI::Transform

    /// <summary>|m4·m0| &lt; |m3·m1| — the matrix turns by an odd number of quarter turns.</summary>
    public static bool Transposed(LumenTransform t)
    {
        float a = t.M[4] * t.M[0], b = t.M[3] * t.M[1];
        if (!(a >= 0f)) a = -a;   // negate unless a ≥ 0 (a NaN is negated too)
        if (!(b >= 0f)) b = -b;
        return b > a;            // false when either is NaN
    }

    /// <summary>`1400409b0`: the orientation of the matrix — angle 0 / 90 / 180 / 270 (clockwise) and mirrored. A matrix that matches no
    /// case leaves the outputs as they were (Lumen passes uninitialized locals; for the matrices its operations make every case matches).</summary>
    public static (int Angle, bool Mirrored) Orientation(LumenTransform t)
    {
        int angle = 0; bool mir = false;
        var m = t.M;
        if (!Transposed(t))
        {
            float f = m[0], g = m[4];
            if (0f <= f || g <= 0f)
            {
                if (f <= 0f || (0f < g || g == 0f))
                {
                    if (0f <= f || (0f < g || g == 0f)) { if (0f < f && 0f < g) { angle = 0; mir = false; } }
                    else { mir = false; angle = 180; }
                }
                else { angle = 180; mir = true; }
            }
            else { angle = 0; mir = true; }
        }
        else
        {
            float f = m[1], g = m[3];
            if (f <= 0f || g <= 0f)
            {
                if (0f <= f || g <= 0f)
                {
                    if (f <= 0f || (0f < g || g == 0f))
                    {
                        if (0f <= f || (0f < g || g == 0f)) return (angle, mir);
                        mir = true;
                    }
                    else mir = false;
                    angle = 270;
                }
                else { angle = 90; mir = false; }
            }
            else { angle = 90; mir = true; }
        }
        return (angle, mir);
    }

    /// <summary>`140040940`: rebuild the matrix for its current orientation: `reset(0)`, `rotate90(−angle/90)`, `flipHorizontal()` when mirrored.</summary>
    public static void Canonical(LumenTransform t)
    {
        var (angle, mir) = Orientation(t);
        t.Reset(0);
        if (angle != 0)
        {
            // −angle / 90 in integer arithmetic: the high word of angle·0x49f49f49, minus angle,
            // shifted right by 6, plus its sign bit
            long p = (long)angle * 0x49f49f49;
            int e = (int)(p >> 32) - angle;
            int q = e >> 6; q += (int)((uint)q >> 31);
            t.Rotate90(q);
        }
        if (mir) t.FlipHorizontal();
    }

    /// <summary>Set the transform's crop from a frame in capture coordinates (the conversion through the orientation, in doubles,
    /// rounded to float where Lumen rounds).</summary>
    public static void SetCropFromRect(LumenTransform t, QRect r)
    {
        var (angle, mir) = Orientation(t);
        if (!Transposed(t))
        {
            const float one = 1f;
            bool sil = angle == 180;
            float x = sil == mir ? (float)r.X : one - (float)(r.W + r.X);
            float y = sil ? one - (float)(r.H + r.Y) : (float)r.Y;
            t.SetCrop(x, y, (float)r.W, (float)r.H);
        }
        else
        {
            var b = TransposedBase(t);
            bool sil = angle == 90;
            double ty = sil != mir ? (1.0 - r.Y) - r.H : r.Y;
            float x = (float)(b.X + b.W * ty);
            double tx = sil ? r.X : 1.0 - (r.W + r.X);
            float y = (float)(b.Y + b.H * tx);
            float w = (float)(r.H * b.W);
            float h = (float)(r.W * b.H);
            t.SetCrop(x, y, w, h);
        }
    }

    /// <summary>The uncropped picture of a turned transform in its own domain: ((1 − 1/a)·0.5, (1 − a)·0.5, 1/a, a) with a = (double)((float)W / (float)H).</summary>
    static QRect TransposedBase(LumenTransform t)
    {
        float af = (float)t.AspectW / (float)t.AspectH;
        double a = af;
        return new QRect((1.0 - 1.0 / a) * 0.5, (1.0 - a) * 0.5, 1.0 / a, a);
    }

    /// <summary>`140040b40`: the frame (capture coordinates) of the transform's crop, clamped to x, y ≥ 0 and w, h ≤ 1.</summary>
    public static QRect RectFromCrop(LumenTransform t)
    {
        var (angle, mir) = Orientation(t);
        var cr = t.Crop();
        var r = new QRect(0, 0, 0, 0);
        const double one = 1.0;
        if (!Transposed(t))
        {
            if (angle == 180)
            {
                float yy = 1f - (cr.H + cr.Y);
                float xx = 1f - (cr.W + cr.X);
                r = new QRect(xx, yy, cr.W, cr.H);
            }
            else r = new QRect(cr.X, cr.Y, cr.W, cr.H);
            if (mir)
            {
                var c = r.Center;
                r.MoveCenter((one - c.X, c.Y));
            }
        }
        else
        {
            var b = TransposedBase(t);
            if (angle == 90)
            {
                r.SetX(((double)cr.Y - b.Y) / b.H);
                r.SetY(one - ((double)cr.W + ((double)cr.X - b.X)) / b.W);
            }
            else
            {
                r.SetX(one - ((double)cr.H + ((double)cr.Y - b.Y)) / b.H);
                r.SetY(((double)cr.X - b.X) / b.W);
            }
            r.W = (double)cr.H / b.H;
            r.H = (double)cr.W / b.W;
            if (mir)
            {
                var c = r.Center;
                r.MoveCenter((c.X, one - c.Y));
            }
        }
        r.SetX(0.0 <= r.X ? r.X : 0.0);
        r.SetY(0.0 <= r.Y ? r.Y : 0.0);
        r.W = one <= r.W ? one : r.W;
        r.H = one <= r.H ? one : r.H;
        return r;
    }

    // ---- GeometryEdits methods

    /// <summary>`1400baeb0` initTransform: take the renderer's transform; the frame = its crop; crop mode free.</summary>
    public void InitTransform(LumenTransform t)
    {
        T = t;
        Frame = RectFromCrop(T);
        if (CropState != 0) SetCropFromRect(T, Original);
        CropMode = 0;
    }

    /// <summary>`1400bbd10`: change the crop state; both frames re-derived from the transform's crop.</summary>
    public void SetCropState(int state)
    {
        if (state == CropState) return;
        CropState = state;
        Original = RectFromCrop(T);
        Frame = RectFromCrop(T);
    }

    QRect FrameForRotation => CropState == 0 ? Frame : Original;

    /// <summary>`1400bb9b0` rotateRight (clockwise): crop full, canonical matrix, `rotate90(−1)`, the frame applied again.</summary>
    public void RotateRight() => Turn(t => t.Rotate90(-1), false);
    /// <summary>`1400bb8e0` rotateLeft (counter-clockwise): the same with `rotate90(1)`.</summary>
    public void RotateLeft() => Turn(t => t.Rotate90(1), false);
    /// <summary>`1400bb0d0` mirrorHorizontal.</summary>
    public void MirrorHorizontal() => Turn(t => t.FlipHorizontal(), true);
    /// <summary>`1400bb190` mirrorVertical.</summary>
    public void MirrorVertical() => Turn(t => t.FlipVertical(), true);

    /// <summary>A turn or flip as Lumen makes it. Lumen rebuilds the matrix from quarter turns, so a level rotation is removed first and
    /// applied again afterwards. A flip mirrors the picture, so the level rotation then has the opposite direction.</summary>
    void Turn(Action<LumenTransform> op, bool mirrors)
    {
        var (c, s) = T.FineRotation();
        SetCropFromRect(T, new QRect(0.0, 0.0, 1.0, 1.0));
        Canonical(T);
        op(T);
        SetCropFromRect(T, FrameForRotation);
        if (s != 0f) T.RotateFine(c, mirrors ? -s : s);
    }

    /// <summary>Set the level rotation (not a Lumen operation): the picture as shown turns by the angle with the cosine <paramref name="c"/>
    /// and the sine <paramref name="s"/> about the center of its crop. A positive sine turns the content of the capture counter-clockwise;
    /// in a mirrored picture the direction is the opposite one. A level rotation that is already there is replaced. The crop then
    /// becomes smaller about its center until it has no empty corners.</summary>
    public void Level(float c, float s)
    {
        var frame = FrameForRotation;
        SetCropFromRect(T, new QRect(0.0, 0.0, 1.0, 1.0));
        Canonical(T);
        SetCropFromRect(T, frame);
        if (Orientation(T).Mirrored) s = -s;
        if (s != 0f) T.RotateFine(c, s);
        FitFrame();
    }

    /// <summary>The level rotation of the capture's content in degrees (positive = counter-clockwise), 0 when there is none. For display.</summary>
    public static double LevelDegrees(LumenTransform t)
    {
        var (c, s) = t.FineRotation();
        if (s == 0f) return 0;
        double d = Math.Atan2(s, c) * (180.0 / Math.PI);
        return Orientation(t).Mirrored ? -d : d;
    }

    /// <summary>Remove a level rotation from a transform. The quarter turns, the flip and the crop stay.</summary>
    public static void RemoveLevel(LumenTransform t)
    {
        if (!t.HasFineRotation) return;
        var frame = RectFromCrop(t);
        SetCropFromRect(t, new QRect(0.0, 0.0, 1.0, 1.0));
        Canonical(t);
        SetCropFromRect(t, frame);
    }

    /// <summary>Keep the crop of a leveled picture inside the capture frame, and derive the frames from it again.</summary>
    public void FitFrame()
    {
        if (!T.HasFineRotation || !T.FitCropInside()) return;
        Original = RectFromCrop(T);
        Frame = RectFromCrop(T);
    }

    /// <summary>`1400ba990`: the aspect of a crop mode in the transform's domain (inverted for a turned picture); free = the frame's own.</summary>
    float AspectFactor(int mode)
    {
        float v = mode switch
        {
            1 => 1.3333334f, 3 => 1.7777778f, 5 => 1.5f, 2 => 0.75f, 4 => 0.5625f, 6 => 0.6666667f, 7 => 2f, 8 => 0.5f, 9 => 1f,
            _ => (float)(((Frame.W * (double)(float)T.AspectW) / (double)(float)T.AspectH) / Frame.H),
        };
        if (Transposed(T)) v = 1f / v;
        return v;
    }

    /// <summary>`1400bba80` setCropMode: the frame reshaped to the mode's aspect — the same area, at most the whole picture, centered where it was,
    /// moved back inside.</summary>
    public void SetCropMode(int mode)
    {
        CropMode = mode;
        var r = Frame;
        if (mode != 0)
        {
            float wh = (float)T.AspectW / (float)T.AspectH;
            float f = AspectFactor(mode);
            double d10 = f / wh;
            double s = Math.Sqrt(r.H * r.W * d10);
            float f7 = (float)s; f7 = f7 < 1f ? f7 : 1f;   // MINSS order: NaN gives 1
            r.W = f7;
            r.H = r.W / d10;
            if (r.H > 1.0) { r.W = d10; r.H = 1.0; }
            r.MoveCenter(Frame.Center);
            float top = (float)r.Top, left = (float)r.Left;
            left = left < 0f ? left : 0f; top = top < 0f ? top : 0f;   // MINSS order: NaN gives 0
            var c = r.Center;
            r.MoveCenter((c.X - (double)left, c.Y - (double)top));
            float bb = (float)r.Bottom - 1f;
            float rr = (float)r.Right - 1f;
            bb = 0f > bb ? 0f : bb; rr = 0f > rr ? 0f : rr;            // MAXSS order: a NaN passes through
            c = r.Center;
            r.MoveCenter((c.X - (double)rr, c.Y - (double)bb));
        }
        Frame = r;
    }

    /// <summary>`1400ba340` applyCrop(shouldHide): the frame into the transform's crop; hiding the tool re-derives both frames.</summary>
    public void ApplyCrop(bool hide = true)
    {
        SetCropFromRect(T, Frame);
        if (hide && CropState != 0)
        {
            CropState = 0;
            Original = RectFromCrop(T);
            Frame = RectFromCrop(T);
        }
    }

    /// <summary>The GEOMETRY reset: `reset(0)` (ParamInt 2 = 0 on the L16) and the frame re-derived (crop mode free).</summary>
    public void ResetGeometry()
    {
        T.Reset(0);
        CropState = 0;
        Original = RectFromCrop(T);
        Frame = RectFromCrop(T);
        CropMode = 0;
    }

    // ---- the frame in the coordinates of the picture as shown (turned, mirrored, uncropped) — for the UI

    /// <summary>A frame in capture coordinates → the shown, uncropped picture (normalized): the inverse of the orientation <see cref="SetCropFromRect"/>
    /// applies, without the float rounding (display only).</summary>
    public static QRect ToShown(LumenTransform t, QRect r)
    {
        var (angle, mir) = Orientation(t);
        if (!Transposed(t))
        {
            bool xf = (angle == 180) != mir, yf = angle == 180;
            return new QRect(xf ? 1.0 - (r.W + r.X) : r.X, yf ? 1.0 - (r.H + r.Y) : r.Y, r.W, r.H);
        }
        bool sil = angle == 90;
        double sx = sil != mir ? (1.0 - r.Y) - r.H : r.Y;
        double sy = sil ? r.X : 1.0 - (r.W + r.X);
        return new QRect(sx, sy, r.H, r.W);
    }

    /// <summary>The inverse of <see cref="ToShown"/>.</summary>
    public static QRect FromShown(LumenTransform t, QRect s)
    {
        var (angle, mir) = Orientation(t);
        if (!Transposed(t))
        {
            bool xf = (angle == 180) != mir, yf = angle == 180;
            return new QRect(xf ? 1.0 - (s.W + s.X) : s.X, yf ? 1.0 - (s.H + s.Y) : s.Y, s.W, s.H);
        }
        bool sil = angle == 90;
        // shown x ← capture y (flipped when sil != mir); shown y ← capture x (flipped unless sil)
        double ry = sil != mir ? (1.0 - s.X) - s.W : s.X;
        double rx = sil ? s.Y : 1.0 - (s.H + s.Y);
        return new QRect(rx, ry, s.H, s.W);
    }

    /// <summary>The crop of the transform in the shown, uncropped picture (normalized), from its crop corners and the uncropped base.</summary>
    public static QRect ShownCrop(LumenTransform t)
    {
        var cr = t.Crop();
        if (!Transposed(t)) return new QRect(cr.X, cr.Y, cr.W, cr.H);
        var b = TransposedBase(t);
        return new QRect((cr.X - b.X) / b.W, (cr.Y - b.Y) / b.H, cr.W / b.W, cr.H / b.H);
    }

    /// <summary>The transform of the crop tool's layout: the same orientation, the whole picture (Lumen's `setCrop` of the full frame).</summary>
    public static LumenTransform Uncropped(LumenTransform t)
    {
        var u = t.Clone(); u.Ops = null;
        SetCropFromRect(u, new QRect(0, 0, 1, 1));
        return u;
    }
}
