using Lux.Engine.Imaging;
namespace Lux.Engine.Pipeline.Isp.Stages;

/// <summary>
/// `lt::A::RemoveCrossTalkGeneric&lt;float,1&gt;` (dispatcher `18012f500`, cell prep `FUN_1801386a0`, tile lambda
/// `18013b3e0`, per-cell apply `FUN_180137410`) = `CrossTalkCorrection:ir_correction` on the Bayer-float domain.
/// This file holds the per-cell apply: for every 2×2 Bayer quad of the cell the four sites (R, G1 = R's row,
/// G2 = R's column, B) are remixed with the 4×4 node matrices bilinearly interpolated inside the cell:
/// R' = (ΣG₄)·c_RG·½ + c_RR·R; G1' = ((B_below+B_above)·c_G1B + (R_right+R_left)·c_G1R)·½ + c_G1G1·G1;
/// G2' = ((R_below+R_above)·c_G2R + (B_right+B_left)·c_G2B)·½ + c_G2G2·G2; B' = (ΣG₄)·(c_BG2·½) + c_BB·B;
/// then `out = (in − v')·t + v'` with the highlight guard `t = clamp(max(g_G·(max(G1,G2)−1), 0, max(g_B·(B−1),
/// g_R·(R−1))), 0, 1)`, `g_c = 1/(1/n_c − 0.99999)`. Rows are clamped by pairs and columns replicated by pairs
/// two pixels outside the source rect.
/// </summary>
public static class CrossTalkKernel
{
    public const float Half = 0.5f;   // DAT_180682404

    /// <summary>`FUN_180137410`. <paramref name="src"/>/<paramref name="dst"/> share the coordinate frame of
    /// <paramref name="srcRect"/> (valid source area) and the cell rect; <paramref name="par"/> = (dx,dy) of
    /// R, G1, G2, B in the quad; <paramref name="t"/> = 4×16 node matrices (r,c), (r+1,c), (r,c+1), (r+1,c+1);
    /// <paramref name="guard"/> = the three guard gains.</summary>
    public static void ApplyCell(float[] src, int srcStride, RectI srcRect, float[] dst, int dstStride, int x0, int y0, int x1, int y1,
        float fx, float fy, float invH, float invV, int[] par, float[] t, float[] guard, int srcOffset = 0, int dstOffset = 0)
    {
        int w = x1 - x0;
        float[] Row(int rel)   // cache row for source row y0 + rel, indices −2 … w+1 (offset 2)
        {
            var r = new float[w + 4];
            int ry = rel + y0;
            int half = Math.Clamp(ry >> 1, srcRect.Y0 >> 1, (srcRect.Y1 >> 1) - 1);
            int row = (ry & 1) + half * 2;
            int a = Math.Clamp(x0 - 2, srcRect.X0, srcRect.X1) - x0;      // first valid relative index
            int b = Math.Clamp(x0 + w + 2, srcRect.X0, srcRect.X1) - x0;  // end of valid relative indices
            int baseIdx = srcOffset + row * srcStride + x0;
            float p0 = src[baseIdx + a], p1 = src[baseIdx + a + 1];
            for (int i = -2; i < a; i++) r[i + 2] = ((i - a) & 1) == 0 ? p0 : p1;
            for (int i = a; i < b; i++) r[i + 2] = src[baseIdx + i];
            if (b < w + 2)
            {
                float q0 = src[baseIdx + b - 2], q1 = src[baseIdx + b - 1];
                for (int i = b; i < w + 2; i++) r[i + 2] = ((i - b) & 1) == 0 ? q0 : q1;
            }
            return r;
        }
        for (int py = 0; py < y1 - y0; py += 2)
        {
            float fyy = ((float)py + fy) * invV;
            float L(int i) => (t[16 + i] - t[i]) * fyy + t[i];                       // left column lerp
            float lRR = L(0), lRG = L(1), lG1R = L(4), lG1G1 = L(5), lG1B = L(7), lG2R = L(8), lG2G2 = L(10), lG2B = L(11), lBG2 = L(13), lBB = L(15);
            float S(int i, float left) => ((t[48 + i] - t[32 + i]) * fyy - left) + t[32 + i];   // slope to the right column
            float sRR = S(0, lRR), sRG = S(1, lRG), sG1R = S(4, lG1R), sG1G1 = S(5, lG1G1), sG1B = S(7, lG1B), sG2R = S(8, lG2R), sG2G2 = S(10, lG2G2), sG2B = S(11, lG2B), sBG2 = S(13, lBG2), sBB = S(15, lBB);
            var rows = new float[4][]; for (int k = 0; k < 4; k++) rows[k] = Row(py - 1 + k);
            for (int px = 0; px < w; px += 2)
            {
                float fxx = ((float)px + fx) * invH;
                int dxR = par[0], dyR = par[1], dxG1 = par[2], dyG1 = par[3], dxG2 = par[4], dyG2 = par[5], dxB = par[6], dyB = par[7];
                float[] rR = rows[dyR + 1], rG1 = rows[dyG1 + 1], rG2 = rows[dyG2 + 1], rB = rows[dyB + 1];
                int iR = px + dxR + 2, iG1 = px + dxG1 + 2, iG2 = px + dxG2 + 2, iB = px + dxB + 2;
                float R = rR[iR], G1 = rG1[iG1], G2 = rG2[iG2], B = rB[iB];
                float maxG = G1 <= G2 ? G2 : G1;
                float gR = guard[0] * (R + -1f), gG = guard[1] * (maxG + -1f);
                float gB = (B + -1f) * guard[2];
                if (gB <= gR) gB = gR;
                float tt = gG; if (tt <= 0f) tt = 0f; if (tt <= gB) tt = gB; if (1f <= tt) tt = 1f;
                // R
                float sumGR = ((rR[iR + 1] + rR[iR - 1]) + rows[dyR][iR]) + rows[dyR + 2][iR];
                float cR = sumGR * (fxx * sRG + lRG) * Half + (fxx * sRR + lRR) * R;
                dst[dstOffset + (y0 + py + dyR) * dstStride + x0 + px + dxR] = (R - cR) * tt + cR;
                // G1 (R's row): vertical neighbours B, horizontal R
                float cG1 = ((rows[dyG1 + 2][iG1] + rows[dyG1][iG1]) * (fxx * sG1B + lG1B) + (rG1[iG1 + 1] + rG1[iG1 - 1]) * (fxx * sG1R + lG1R)) * Half + (fxx * sG1G1 + lG1G1) * G1;
                dst[dstOffset + (y0 + py + dyG1) * dstStride + x0 + px + dxG1] = (G1 - cG1) * tt + cG1;
                // G2 (R's column): vertical neighbours R, horizontal B
                float cG2 = ((rows[dyG2 + 2][iG2] + rows[dyG2][iG2]) * (fxx * sG2R + lG2R) + (rG2[iG2 + 1] + rG2[iG2 - 1]) * (fxx * sG2B + lG2B)) * Half + (fxx * sG2G2 + lG2G2) * G2;
                dst[dstOffset + (y0 + py + dyG2) * dstStride + x0 + px + dxG2] = (G2 - cG2) * tt + cG2;
                // B
                float cB = ((rB[iB + 1] + rB[iB - 1]) + rows[dyB][iB]) + rows[dyB + 2][iB];
                cB = cB * ((fxx * sBG2 + lBG2) * Half) + (fxx * sBB + lBB) * B;
                dst[dstOffset + (y0 + py + dyB) * dstStride + x0 + px + dxB] = (B - cB) * tt + cB;
            }
        }
    }

    /// <summary>The 4×4 crosstalk matrix of a model node (lambda `18013b3e0` L20–60 / L420–440): the compact form
    /// (entry[14] == 0) spreads the R/B→G terms over both greens, `(M0, M1·½, M1·½, 0), (M4, M5, 0, M6), (M4, 0, M5,
    /// M6), (0, M9·½, M9·½, M10)`; otherwise the entry is the row-major 4×4 itself.</summary>
    public static float[] NodeMatrix(ReadOnlySpan<float> m)
    {
        if (m[14] == 0f)
        {
            float h1 = m[1] * Half, h9 = m[9] * Half;
            return new[] { m[0], h1, h1, 0f, m[4], m[5], 0f, m[6], m[4], 0f, m[5], m[6], 0f, h9, h9, m[10] };
        }
        return m.ToArray();
    }

    /// <summary>T = (Dinv·E)·D with D = diag(n_R, n_G, n_G, n_B), Dinv = rcp+Newton of the same (exact per element
    /// as the off-diagonal products are zero), then T' = T·C with the node's diagonal IR gain matrix C.</summary>
    public static float[] NodeTransform(float[] e, float[] d, float[] dinv, float[]? c)
    {
        var t = new float[16];
        // the lambda's first `Matrix4x4(vector)` (local_138) holds the neutral, the second (local_178) its reciprocals;
        // the products use local_178 on the left: T(i,j) = (dinv_i · E(i,j)) · d_j  [verified bit-exact vs cp.dll]
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) t[i * 4 + j] = (dinv[i] * e[i * 4 + j]) * d[j];
        if (c is not null)
        {
            var r = new float[16];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                r[i * 4 + j] = ((t[i * 4 + 3] * c[12 + j] + t[i * 4 + 2] * c[8 + j]) + t[i * 4 + 1] * c[4 + j]) + t[i * 4] * c[j];
            t = r;
        }
        return t;
    }

    /// <summary>`(1 − n·r)·r + r` after `rcpps` — the neutral reciprocals (`_DAT_1806824a0` = 1).</summary>
    public static float[] Reciprocals(float[] n)
    {
        var v = System.Runtime.Intrinsics.Vector128.Create(n[0], n[1], n[2], n[3]);
        var r = System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.Reciprocal(v) : System.Runtime.Intrinsics.Vector128.Create(1f / n[0], 1f / n[1], 1f / n[2], 1f / n[3]);
        var one = System.Runtime.Intrinsics.Vector128.Create(1f);
        var rr = System.Runtime.Intrinsics.X86.Sse.Add(System.Runtime.Intrinsics.X86.Sse.Multiply(System.Runtime.Intrinsics.X86.Sse.Subtract(one, System.Runtime.Intrinsics.X86.Sse.Multiply(v, r)), r), r);
        return new[] { rr[0], rr[1], rr[2], rr[3] };
    }

    /// <summary>The tile lambda for one cell (col, row): node matrices, the even-aligned cell rect, the fractional
    /// offsets (L977–1049) and the apply. <paramref name="model"/> = cols × rows × 16 floats, <paramref name="cellGains"/>
    /// = cols × rows × 16 floats (the prepared 4×4 per node, `FUN_1801386a0`) or null (identity).</summary>
    public static void ProcessCell(float[] src, int srcStride, RectI srcRect, int srcOffset, float[] dst, int dstStride, int dstOffset,
        int col, int row, float hspace, float vspace, int modelCols, float[] model, float[]? cellGains, float[] neutral3, RectI cell, RectI intRect, int redX, int redY, float[] guard)
    {
        var d = new[] { neutral3[0], neutral3[1], neutral3[1], neutral3[2] };
        var dinv = Reciprocals(d);
        float[] Node(int r, int c)
        {
            int idx = r * modelCols + c;
            return NodeTransform(NodeMatrix(model.AsSpan(idx * 16, 16)), d, dinv, cellGains is null ? null : cellGains.AsSpan(idx * 16, 16).ToArray());
        }
        var t = new float[64];
        Node(row, col).CopyTo(t, 0); Node(row + 1, col).CopyTo(t, 16); Node(row, col + 1).CopyTo(t, 32); Node(row + 1, col + 1).CopyTo(t, 48);
        float cx = (float)col * hspace, cy = (float)row * vspace;
        float ax = (float)intRect.X0, ay = (float)intRect.Y0;
        int pxAx = (int)ax & 1, pyAy = (int)ay & 1, pxCx = (int)cx & 1, pyCy = (int)cy & 1;
        float dx = ax - cx, dy = ay - cy;
        int ix = 0f < dx ? pxAx : pxCx, iy = 0f < dy ? pyAy : pyCy;
        if (dx <= 0f) dx = 0f; if (dy <= 0f) dy = 0f;
        float fx = dx - (float)ix, fy = dy - (float)iy;
        int rx = redX & 1, ry = redY & 1;
        var par = new[] { rx, ry, rx ^ 1, ry, rx, ry ^ 1, rx ^ 1, ry ^ 1 };
        ApplyCell(src, srcStride, srcRect, dst, dstStride, cell.X0 & ~1, cell.Y0 & ~1, cell.X1 & ~1, cell.Y1 & ~1, fx, fy, 1f / hspace, 1f / vspace, par, t, guard, srcOffset, dstOffset);
    }
}

/// <summary>Bayer-float stage `CrossTalkCorrection:ir_correction` (`setCrossTalkCorrection` `180401420`, slot pad 3 /
/// align 2; lambda_12 `180414cf0`: `RemoveCrossTalkGeneric&lt;float,1&gt;(out, view, (int)floatRect, CapturedImage,
/// Stats neutral, neutral' = min(n)×3 when highlight_restore ≠ none, Stats.IrBlend)`). Dispatcher `18012f500`:
/// the module's `VignettingCharacterization.crosstalk` grid (must be 17×13 for the IR database), the vignetting
/// cell geometry, guard gains 1/(1/n' + (−0.99999)) and the IR cell matrices (`FUN_1801386a0`).</summary>
public sealed class CrossTalkStage : IStage
{
    public StageName Stage => StageName.CrossTalkCorrection;
    public string TypeString => "ir_correction";
    public StageMeta Meta => new(3, 2, 1f);   // setCrossTalkCorrection L156/L300: 0x200000003 = pad 3, align 2 (both types)
    public const float GuardOffset = -0.9999899864196777f;   // DAT_18068c1c8

    public sealed record CrosstalkGrid(int Cols, int Rows, float[] Nodes);

    /// <summary>The crosstalk model of the module's calibration (`FUN_180110250`, "crosstalk model not found!").</summary>
    public static CrosstalkGrid Model(Ltpb.LightHeader h, Ltpb.CameraModule m)
    {
        foreach (var cal in Calibration.ForModule(h, m.Id))
        {
            var ct = cal.Vignetting?.Crosstalk;
            if (ct is null) continue;
            int cols = (int)ct.Width, rows = (int)ct.Height;
            var d = new float[cols * rows * 16];
            if (ct.Data.Count > 0)
            {
                for (int i = 0; i < ct.Data.Count && i < cols * rows; i++)
                {
                    var q = ct.Data[i];
                    float[] v = { q.X00, q.X01, q.X02, q.X03, q.X10, q.X11, q.X12, q.X13, q.X20, q.X21, q.X22, q.X23, q.X30, q.X31, q.X32, q.X33 };
                    v.CopyTo(d, i * 16);
                }
            }
            else for (int i = 0; i < ct.DataPacked.Count && i < d.Length; i++) d[i] = ct.DataPacked[i];
            return new CrosstalkGrid(cols, rows, d);
        }
        throw new InvalidOperationException("crosstalk model not found!");
    }

    /// <summary>Owner +0x290 (`FUN_18010df70` L150): set when any ColorCalibration of the module carries
    /// `spectral_data` (has-bit 2 of the generated class).</summary>
    public static bool SpectralFlag(Ltpb.LightHeader h, Ltpb.CameraModule m)
    {
        foreach (var cal in Calibration.ForModule(h, m.Id)) foreach (var c in cal.Color) if (c.SpectralData is not null) return true;
        return false;
    }

    public void Apply(IspPayload p)
    {
        var img = p.BayerFloat ?? throw new InvalidOperationException("CrossTalkCorrection needs the float Bayer image (BayerToFloat stage)");
        var red = p.Context.Module.SensorBayerRedOverride ?? throw new InvalidOperationException("CrossTalkCorrection needs the sensor red position");
        float blend = p.Stats.IrBlend;
        if (Environment.GetEnvironmentVariable("LUX_ISP_DEBUG") == "1") Console.Error.WriteLine($"[crosstalk] blend {blend:R} neutral ({string.Join(",", p.Stats.Neutral)}) hl {p.Context.Tuning.Type("highlight_restore")}");
        if (float.IsNaN(blend)) throw new InvalidOperationException("CrossTalkCorrection needs Stats.IrBlend (ComputeStats with the frame)");
        var model = Model(p.Context.Header, p.Context.Module);
        if (model.Cols != IrCorrection.Cols) throw new InvalidOperationException("Width of vignetting profile does not match IR correction database!");
        if (model.Rows != IrCorrection.Rows) throw new InvalidOperationException("Height of vignetting profile does not match IR correction database!");
        var cellGains = IrCorrection.CellMatrices(blend, (int)p.Frame.Sensor, (int)p.Context.Module.Id, SpectralFlag(p.Context.Header, p.Context.Module));
        var n = p.Stats.Neutral;
        float[] nGuard = n;
        if (p.Context.Tuning.Type("highlight_restore") != "none") { float mn = n[0]; if (n[1] <= mn) mn = n[1]; if (n[2] <= mn) mn = n[2]; nGuard = new[] { mn, mn, mn }; }
        var guard = new[] { 1f / (1f / nGuard[0] + GuardOffset), 1f / (1f / nGuard[1] + GuardOffset), 1f / (1f / nGuard[2] + GuardOffset) };
        // geometry (dispatcher L30–60): the int rect = (int)floatRect, sx = view.w / rect.w, node spacing over the frame
        var fr = p.FloatRect; var ir = new RectI((int)fr.X0, (int)fr.Y0, (int)fr.X1, (int)fr.Y1);
        var abs = p.ToAbsolute(p.IntRect).Intersect(img.Rect);
        int w = abs.Width, h = abs.Height;
        float sx = (float)w / (float)(ir.X1 - ir.X0), sy = (float)h / (float)(ir.Y1 - ir.Y0);
        float hspace = ((float)p.Context.FrameWidth * sx) / (float)(model.Cols - 1), vspace = ((float)p.Context.FrameHeight * sy) / (float)(model.Rows - 1);
        float xoff = (float)ir.X0 * sx, yoff = (float)ir.Y0 * sy; int ox = (int)xoff, oy = (int)yoff;
        int col0 = (int)(xoff * (1f / hspace)), row0 = (int)(yoff * (1f / vspace));
        int col1 = (int)MathF.Ceiling((float)ir.X1 * sx * (1f / hspace)), row1 = (int)MathF.Ceiling((float)ir.Y1 * sy * (1f / vspace));
        // source view = the int rect; valid source area in view coordinates = the whole padded image
        int srcOffset = img.Offset + (abs.Y0 - img.Rect.Y0) * img.Stride + (abs.X0 - img.Rect.X0);
        var srcRect = new RectI(img.Rect.X0 - abs.X0, img.Rect.Y0 - abs.Y0, img.Rect.X1 - abs.X0, img.Rect.Y1 - abs.Y0);
        var dst = new Image<float>(abs);
        for (int r = row0; r < row1; r++)
            for (int c = col0; c < col1; c++)
            {
                int cx0 = Math.Max((int)((float)c * hspace) - ox, 0), cy0 = Math.Max((int)((float)r * vspace) - oy, 0);
                int cx1 = Math.Min((int)(hspace * (float)(c + 1)) - ox, w), cy1 = Math.Min((int)(vspace * (float)(r + 1)) - oy, h);
                if (cx0 >= cx1 || cy0 >= cy1) continue;
                CrossTalkKernel.ProcessCell(img.Data, img.Stride, srcRect, srcOffset, dst.Data, dst.Stride, 0, c, r, hspace, vspace, model.Cols, model.Nodes, cellGains, n, new RectI(cx0, cy0, cx1, cy1), ir, red.X, red.Y, guard);
            }
        p.BayerFloat = dst;
    }
}
