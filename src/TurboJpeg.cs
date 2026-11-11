using System.Runtime.InteropServices;

namespace NocatCam;

/// <summary>
/// Decodes the camera's MJPEG pictures with libjpeg-turbo (turbojpeg.dll, the decoder Chrome uses).
/// Much faster than Windows' one-at-a-time decoder, and it can shrink while decoding
/// (4K straight to 1080p), which costs almost nothing. Output is NV12.
/// </summary>
public sealed unsafe class TurboJpeg : IDisposable
{
    const string Dll = "turbojpeg.dll";
    const int InitDecompress = 1;
    const int ParamSubsamp = 4, ParamJpegWidth = 5, ParamJpegHeight = 6, ParamFastDct = 10;
    // subsampling ids
    const int Samp444 = 0, Samp422 = 1, Samp420 = 2, SampGray = 3, Samp440 = 4, Samp411 = 5, Samp441 = 6;

    [StructLayout(LayoutKind.Sequential)] struct ScalingFactor { public int num, denom; }

    [DllImport(Dll)] static extern nint tj3Init(int initType);
    [DllImport(Dll)] static extern void tj3Destroy(nint h);
    [DllImport(Dll)] static extern int tj3Set(nint h, int param, int value);
    [DllImport(Dll)] static extern int tj3Get(nint h, int param);
    [DllImport(Dll)] static extern int tj3DecompressHeader(nint h, byte* jpeg, nuint size);
    [DllImport(Dll)] static extern int tj3SetScalingFactor(nint h, ScalingFactor sf);
    [DllImport(Dll)] static extern int tj3DecompressToYUVPlanes8(nint h, byte* jpeg, nuint size, byte** planes, int* strides);
    [DllImport(Dll)] static extern nint tj3GetErrorStr(nint h);

    readonly nint _h;
    byte[] _y = [], _cb = [], _cr = [];

    public TurboJpeg()
    {
        _h = tj3Init(InitDecompress);
        if (_h == 0) throw new InvalidOperationException("couldn't start the JPEG decoder");
        tj3Set(_h, ParamFastDct, 1);
    }

    string Error() => Marshal.PtrToStringAnsi(tj3GetErrorStr(_h)) ?? "unknown";

    /// <summary>Size of the picture in this JPEG (no decoding).</summary>
    public (int w, int h) Header(ReadOnlySpan<byte> jpeg)
    {
        fixed (byte* p = jpeg)
            if (tj3DecompressHeader(_h, p, (nuint)jpeg.Length) != 0) throw new InvalidOperationException("bad picture: " + Error());
        return (tj3Get(_h, ParamJpegWidth), tj3Get(_h, ParamJpegHeight));
    }

    /// <summary>Scale (eighths, 1..8) that shrinks the picture as far as possible while keeping it at least minW wide.</summary>
    public static int Eighths(int jpegW, int minW)
    {
        for (int n = 1; n <= 8; n++) if ((jpegW * n + 7) / 8 >= minW) return n;
        return 8;
    }

    /// <summary>Decode (shrunk to n/8) into an NV12 frame; reallocates 'into' if the size changed.</summary>
    public void DecodeNv12(ReadOnlySpan<byte> jpeg, int eighths, ref Nv12? into)
    {
        fixed (byte* p = jpeg)
        {
            if (tj3DecompressHeader(_h, p, (nuint)jpeg.Length) != 0) throw new InvalidOperationException("bad picture: " + Error());
            int jw = tj3Get(_h, ParamJpegWidth), jh = tj3Get(_h, ParamJpegHeight), samp = tj3Get(_h, ParamSubsamp);
            int g = Gcd(eighths, 8);                       // libjpeg-turbo wants 1/2, 1/1... not 4/8, 8/8
            if (tj3SetScalingFactor(_h, new ScalingFactor { num = eighths / g, denom = 8 / g }) != 0) throw new InvalidOperationException("scale: " + Error());
            int w = (jw * eighths + 7) / 8, h = (jh * eighths + 7) / 8;
            // chroma plane size for this subsampling
            int cw = samp switch { Samp444 or Samp440 => w, Samp411 => (w + 3) / 4, SampGray => 0, _ => (w + 1) / 2 };
            int ch = samp switch { Samp420 or Samp440 => (h + 1) / 2, Samp441 => (h + 3) / 4, SampGray => 0, _ => h };
            if (_y.Length < w * h) _y = new byte[w * h];
            if (_cb.Length < cw * ch) { _cb = new byte[Math.Max(1, cw * ch)]; _cr = new byte[Math.Max(1, cw * ch)]; }
            int ew = w & ~1, eh = h & ~1;                 // NV12 needs even sizes
            if (into == null || into.W != ew || into.H != eh) into = new Nv12(ew, eh);

            fixed (byte* py = _y, pcb = _cb, pcr = _cr)
            {
                byte** planes = stackalloc byte*[3] { py, pcb, pcr };
                int* strides = stackalloc int[3] { w, cw, cw };
                if (tj3DecompressToYUVPlanes8(_h, p, (nuint)jpeg.Length, planes, strides) != 0)
                    throw new InvalidOperationException("decode: " + Error());
            }

            // Y plane
            var d = into.Data;
            if (ew == w) Buffer.BlockCopy(_y, 0, d, 0, ew * eh);
            else for (int y = 0; y < eh; y++) Buffer.BlockCopy(_y, y * w, d, y * ew, ew);

            // UV: half width, half height, interleaved
            int uvBase = ew * eh, uw = ew / 2, uh = eh / 2;
            if (samp == SampGray) { d.AsSpan(uvBase, uw * uh * 2).Fill(128); return; }
            var cb = _cb; var cr = _cr;
            int xs = Math.Max(1, (w + cw - 1) / Math.Max(1, cw)), ys = Math.Max(1, (h + ch - 1) / Math.Max(1, ch));
            Parallel.For(0, uh, y =>
            {
                int sy = Math.Min(ch - 1, y * 2 / ys);
                int srow = sy * cw, drow = uvBase + y * uw * 2;
                if (xs == 2)
                    for (int x = 0; x < uw; x++) { d[drow + x * 2] = cb[srow + x]; d[drow + x * 2 + 1] = cr[srow + x]; }
                else
                    for (int x = 0; x < uw; x++)
                    {
                        int sx = Math.Min(cw - 1, x * 2 / xs);
                        d[drow + x * 2] = cb[srow + sx]; d[drow + x * 2 + 1] = cr[srow + sx];
                    }
            });
        }
    }

    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return a; }

    public void Dispose() => tj3Destroy(_h);
}
