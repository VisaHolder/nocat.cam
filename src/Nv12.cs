namespace NocatCam;

/// <summary>
/// An NV12 picture: a full-size brightness plane (Y) followed by a half-size plane of
/// interleaved colour pairs (UV). Pictures stay in this format from camera to virtual camera,
/// so nothing is ever converted to RGB except small copies for the AI models.
/// </summary>
public sealed class Nv12
{
    public readonly int W, H;
    public readonly byte[] Data;
    public Nv12(int w, int h) { W = w; H = h; Data = new byte[w * h * 3 / 2]; }
    public Span<byte> Y => Data.AsSpan(0, W * H);
    public Span<byte> UV => Data.AsSpan(W * H, W * H / 2);

    public static int Even(double v) => Math.Max(2, (int)v / 2 * 2);

    /// <summary>Crop a centred zoom window out of src (frame w x h) and scale it into dst.</summary>
    public static void ZoomInto(byte[] src, int w, int h, double zoom, Nv12 dst)
    {
        int cw = zoom <= 1.001 ? w : Even(w / zoom), ch = zoom <= 1.001 ? h : Even(h / zoom);
        int x0 = (w - cw) / 4 * 2, y0 = (h - ch) / 4 * 2;     // centred, kept even for the colour plane
        Scale(src, w, x0, y0, cw, ch, 1, dst.Data, 0, dst.W, dst.H);
        Scale(src, w, x0 / 2, y0 / 2, cw / 2, ch / 2, 2, dst.Data, dst.W * dst.H, dst.W / 2, dst.H / 2,
              srcOffset: w * h);
    }

    /// <summary>
    /// Scale a (cw x ch) window at (x0, y0) of a plane into a (dw x dh) plane.
    /// channels = 1 for Y, 2 for UV (stride is then w*2 bytes per row, same as w for Y in bytes).
    /// Whole-number shrinks (4K to 1080p) average every source pixel; other sizes use bilinear.
    /// </summary>
    static void Scale(byte[] src, int w, int x0, int y0, int cw, int ch, int channels,
                      byte[] dst, int dstOffset, int dw, int dh, int srcOffset = 0)
    {
        int sStride = channels == 1 ? w : w;            // UV rows are w/2 pairs = w bytes
        if (cw == dw && ch == dh)
        {
            for (int y = 0; y < dh; y++)
                Buffer.BlockCopy(src, srcOffset + (y0 + y) * sStride + x0 * channels, dst, dstOffset + y * dw * channels, dw * channels);
            return;
        }
        if (cw % dw == 0 && ch % dh == 0 && cw / dw == ch / dh)
        {
            int k = cw / dw, area = k * k;
            Parallel.For(0, dh, y =>
            {
                int drow = dstOffset + y * dw * channels;
                for (int x = 0; x < dw; x++)
                    for (int c = 0; c < channels; c++)
                    {
                        int sum = 0;
                        for (int j = 0; j < k; j++)
                        {
                            int srow = srcOffset + (y0 + y * k + j) * sStride + (x0 + x * k) * channels + c;
                            for (int i = 0; i < k; i++) sum += src[srow + i * channels];
                        }
                        dst[drow + x * channels + c] = (byte)((sum + area / 2) / area);
                    }
            });
            return;
        }
        double fx = (double)cw / dw, fy = (double)ch / dh;
        Parallel.For(0, dh, y =>
        {
            double sy = Math.Max(0, (y + 0.5) * fy - 0.5);
            int ya = Math.Min((int)sy, ch - 1), yb = Math.Min(ya + 1, ch - 1);
            int wy = (int)((sy - ya) * 256);
            int ra = srcOffset + (y0 + ya) * sStride, rb = srcOffset + (y0 + yb) * sStride;
            int drow = dstOffset + y * dw * channels;
            for (int x = 0; x < dw; x++)
            {
                double sx = Math.Max(0, (x + 0.5) * fx - 0.5);
                int xa = Math.Min((int)sx, cw - 1), xb = Math.Min(xa + 1, cw - 1);
                int wx = (int)((sx - xa) * 256);
                int ia = (x0 + xa) * channels, ib = (x0 + xb) * channels;
                for (int c = 0; c < channels; c++)
                {
                    int top = src[ra + ia + c] * (256 - wx) + src[ra + ib + c] * wx;
                    int bot = src[rb + ia + c] * (256 - wx) + src[rb + ib + c] * wx;
                    dst[drow + x * channels + c] = (byte)((top * (256 - wy) + bot * wy + 32768) >> 16);
                }
            }
        });
    }

    /// <summary>Small RGB copy (about 'width' wide) of a zoomed frame, for the AI models.</summary>
    public static (byte[] rgb, int w, int h) SmallRgb(byte[] src, int w, int h, double zoom, int width = 640)
    {
        int sw = Even(Math.Min(width, w)), sh = Even((double)h * sw / w);
        var small = new Nv12(sw, sh);
        ZoomInto(src, w, h, zoom, small);
        return (ToRgb(small), sw, sh);
    }

    /// <summary>NV12 (video range, BT.601) to packed RGB.</summary>
    public static byte[] ToRgb(Nv12 f)
    {
        var rgb = new byte[f.W * f.H * 3];
        var d = f.Data;
        int w = f.W, h = f.H, uvBase = w * h;
        Parallel.For(0, h, y =>
        {
            int yr = y * w, uvr = uvBase + (y / 2) * w, o = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int Y = (d[yr + x] - 16) * 298;
                int u = d[uvr + (x & ~1)] - 128, v = d[uvr + (x & ~1) + 1] - 128;
                rgb[o++] = Clamp((Y + 409 * v + 128) >> 8);
                rgb[o++] = Clamp((Y - 100 * u - 208 * v + 128) >> 8);
                rgb[o++] = Clamp((Y + 516 * u + 128) >> 8);
            }
        });
        return rgb;
    }

    /// <summary>Packed BGRA (from a loaded picture) to NV12 of the same size.</summary>
    public static Nv12 FromBgra(byte[] bgra, int w, int h)
    {
        var f = new Nv12(w, h);
        var d = f.Data;
        int uvBase = w * h;
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                d[y * w + x] = Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16);
                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    int o = uvBase + (y / 2) * w + x;
                    d[o] = Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128);
                    d[o + 1] = Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128);
                }
            }
        });
        return f;
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>Blurred copy of a frame: shrink by k, blur, grow back.</summary>
    public static Nv12 Blurred(Nv12 f, int strength)
    {
        int k = 2 + strength * 2;
        var small = new Nv12(Even(f.W / (double)k), Even(f.H / (double)k));
        Scale(f.Data, f.W, 0, 0, f.W, f.H, 1, small.Data, 0, small.W, small.H);
        Scale(f.Data, f.W, 0, 0, f.W / 2, f.H / 2, 2, small.Data, small.W * small.H, small.W / 2, small.H / 2, f.W * f.H);
        BoxBlur(small.Data, 0, small.W, small.H, 1, 2);
        BoxBlur(small.Data, small.W * small.H, small.W / 2, small.H / 2, 2, 1);
        var big = new Nv12(f.W, f.H);
        Scale(small.Data, small.W, 0, 0, small.W, small.H, 1, big.Data, 0, f.W, f.H);
        Scale(small.Data, small.W, 0, 0, small.W / 2, small.H / 2, 2, big.Data, f.W * f.H, f.W / 2, f.H / 2, small.W * small.H);
        return big;
    }

    static void BoxBlur(byte[] d, int off, int w, int h, int ch, int r)
    {
        var tmp = new byte[w * h * ch];
        for (int pass = 0; pass < 2; pass++)
        {
            bool horiz = pass == 0;
            Parallel.For(0, horiz ? h : w, a =>
            {
                int len = horiz ? w : h;
                for (int c = 0; c < ch; c++)
                    for (int b = 0; b < len; b++)
                    {
                        int sum = 0, n = 0;
                        for (int t = Math.Max(0, b - r); t <= Math.Min(len - 1, b + r); t++, n++)
                            sum += horiz ? d[off + (a * w + t) * ch + c] : tmp[(t * w + a) * ch + c];
                        if (horiz) tmp[(a * w + b) * ch + c] = (byte)(sum / n);
                        else d[off + (b * w + a) * ch + c] = (byte)(sum / n);
                    }
            });
        }
    }

    /// <summary>Mix bg into f where the person mask (mw x mh, 0..1) says "not a person".</summary>
    public static void Blend(Nv12 f, Nv12 bg, float[] mask, int mw, int mh)
    {
        var m = SoftEdge(mask, mw, mh);
        int w = f.W, h = f.H;
        var d = f.Data; var b = bg.Data;
        Parallel.For(0, h, y =>
        {
            float my = (y + 0.5f) * mh / h - 0.5f;
            int ya = Math.Clamp((int)my, 0, mh - 1), yb = Math.Min(ya + 1, mh - 1);
            float wy = Math.Clamp(my - ya, 0, 1);
            for (int x = 0; x < w; x++)
            {
                float mx = (x + 0.5f) * mw / w - 0.5f;
                int xa = Math.Clamp((int)mx, 0, mw - 1), xb = Math.Min(xa + 1, mw - 1);
                float wx = Math.Clamp(mx - xa, 0, 1);
                float a = (m[ya * mw + xa] * (1 - wx) + m[ya * mw + xb] * wx) * (1 - wy)
                        + (m[yb * mw + xa] * (1 - wx) + m[yb * mw + xb] * wx) * wy;
                int i = y * w + x;
                d[i] = (byte)(d[i] * a + b[i] * (1 - a) + 0.5f);
                if ((y & 1) == 0 && (x & 1) == 0)
                {
                    int o = w * h + (y / 2) * w + x;
                    d[o] = (byte)(d[o] * a + b[o] * (1 - a) + 0.5f);
                    d[o + 1] = (byte)(d[o + 1] * a + b[o + 1] * (1 - a) + 0.5f);
                }
            }
        });
    }

    /// <summary>Light 3x3 smoothing so the cut-out edge isn't jagged.</summary>
    static float[] SoftEdge(float[] m, int w, int h)
    {
        var o = new float[m.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int n = 0;
                for (int j = -1; j <= 1; j++)
                    for (int i = -1; i <= 1; i++)
                    {
                        int yy = y + j, xx = x + i;
                        if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                        s += m[yy * w + xx]; n++;
                    }
                o[y * w + x] = s / n;
            }
        return o;
    }
}
